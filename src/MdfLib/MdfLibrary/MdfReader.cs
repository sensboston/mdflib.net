// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// The reader of the facade. The engine reader is opened for one step at a time, so
// no step keeps the file locked after it returns (the wrapper's Open/Close pair only sets that state). The steps fill
// the same pending tree the writer uses, and the samples of a data group are kept per channel group as engine batches.
using System;
using System.Collections.Generic;
using System.IO;

namespace MdfLibrary
{
    /// <summary>Reads one MDF file through the managed engine. The steps follow the
    /// wrapper's ladder (Open, ReadHeader, ReadMeasurementInfo, ReadEverythingButData, ReadData); each step reads what it
    /// needs on its own, so a step that is called first runs the steps it depends on.</summary>
    public class MdfReader
    {
        private readonly string path_;
        private long index_;
        private bool disposed_;
        private FacadeContext context_;
        private MdfHeader header_;
        private MdfFile file_;
        private bool headerRead_, measurementRead_, modelRead_;

        /// <summary>The reader is created for a file name and opens nothing yet.</summary>
        public MdfReader(string filename)
        {
            path_ = filename ?? throw new ArgumentNullException("filename");
        }

        /// <summary>The wrapper's Close calls Open again (v2.3.0), so the file is only opened.</summary>
        public void Close()
        {
            if (!disposed_) { Open(); }
        }

        /// <summary>Releases the reader. The model is gone and every handle answers empty.</summary>
        public void Dispose()
        {
            disposed_ = true;
            index_ = 0;
            context_ = null;
            header_ = null;
            file_ = null;
        }

        /// <summary>The file of the reader, null before the header is read.</summary>
        public MdfFile File
        {
            get
            {
                return file_;
            }
        }

        /// <summary>The header block, null before the header is read.</summary>
        public MdfHeader Header
        {
            get
            {
                return header_;
            }
        }

        /// <summary>The caller's own id of the file, not used by the reader.</summary>
        public long Index
        {
            get
            {
                return disposed_ ? 0L : index_;
            }
            set
            {
                if (!disposed_) { index_ = value; }
            }
        }

        /// <summary>False after Dispose.</summary>
        public bool IsOk
        {
            get
            {
                return !disposed_;
            }
        }

        /// <summary>The data group at an index, null outside the list or before the measurement info is read.</summary>
        public MdfDataGroup this[ulong i]
        {
            get
            {
                return get_DataGroup(i);
            }
        }

        /// <summary>The file name without its path and extension.</summary>
        public string Name
        {
            get
            {
                if (disposed_) { return string.Empty; }
                try { return System.IO.Path.GetFileNameWithoutExtension(path_) ?? string.Empty; }
                catch (ArgumentException) { return string.Empty; }
            }
        }

        /// <summary>Checks that the file opens. Returns false instead of throwing when it does not.</summary>
        public bool Open()
        {
            if (disposed_) { return false; }
            try
            {
                using (OpenEngine()) { }
                return true;
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                Report(ex);
                return false;
            }
        }

        /// <summary>The samples of one data group, all of them. Returns false when the file cannot be read.</summary>
        public bool ReadData(MdfDataGroup group)
        {
            if (group == null) { throw new NullReferenceException("MdfReader.ReadData: the data group is null; the wrapper dereferences it."); }
            return ReadDataGroup(group, 0UL, ulong.MaxValue);
        }

        /// <summary>Everything except the sample data: the metadata, the file history, the attachments and the events.</summary>
        public bool ReadEverythingButData()
        {
            if (disposed_) { return false; }
            if (!ReadMeasurementInfo()) { return false; }
            if (modelRead_) { return true; }
            try
            {
                using (MdfLib.MdfReader engine = OpenEngine())
                {
                    FacadeLoader.FillDescription(context_, engine.ReadFileModel());
                }
                modelRead_ = true;
                return true;
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                Report(ex);
                return false;
            }
        }

        /// <summary>The identification and the header block. Returns false when the file is not an MDF file.</summary>
        public bool ReadHeader()
        {
            if (disposed_) { return false; }
            try
            {
                using (MdfLib.MdfReader engine = OpenEngine())
                {
                    MdfLib.MdfFileHeader snapshot = engine.Header;
                    Attach(snapshot.Version);
                    FacadeLoader.FillHeader(context_, snapshot);
                    // The header comment is read with the model; a file whose model is damaged still has its header.
                    try { FacadeLoader.FillDescription(context_, engine.ReadFileModel()); }
                    catch (Exception ex) when (IsReadFailure(ex)) { Report(ex); }
                }
                headerRead_ = true;
                return true;
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                Report(ex);
                return false;
            }
        }

        /// <summary>The data groups, channel groups and channels. The sample data stays unread.</summary>
        public bool ReadMeasurementInfo()
        {
            if (disposed_) { return false; }
            if (!headerRead_ && !ReadHeader()) { return false; }
            if (measurementRead_) { return true; }
            try
            {
                using (MdfLib.MdfReader engine = OpenEngine())
                {
                    FacadeLoader.FillGroups(context_, engine.Header, engine.ReadMetadata());
                }
                measurementRead_ = true;
                return true;
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                Report(ex);
                return false;
            }
        }

        /// <summary>The samples of one data group from minSample to maxSample inclusive.</summary>
        public bool ReadPartialData(MdfDataGroup group, ulong minSample, ulong maxSample)
        {
            if (group == null) { throw new NullReferenceException("MdfReader.ReadPartialData: the data group is null; the wrapper dereferences it."); }
            if (minSample > maxSample) { return false; }
            ulong count = maxSample == ulong.MaxValue ? ulong.MaxValue : maxSample - minSample + 1UL;
            return ReadDataGroup(group, minSample, count);
        }

        /// <summary>The data group at an index, null outside the list or before the measurement info is read.</summary>
        public MdfDataGroup get_DataGroup(ulong i)
        {
            if (context_ == null || i >= (ulong)context_.DataGroups.Count) { return null; }
            return PendingDataGroup.FacadeOf(context_.DataGroups[(int)i]);
        }

        // ---- internals -----------------------------------------------------------------------------------------------

        // The file-level handles are created once, with the revision the file reports; the writer's context type is
        // only a label here, because nothing is written through a reader.
        private void Attach(string version)
        {
            if (context_ != null) { return; }
            bool mdf3 = version != null && version.StartsWith("3", StringComparison.Ordinal);
            context_ = new FacadeContext(mdf3 ? MdfWriterType.Mdf3Basic : MdfWriterType.Mdf4Basic);
            context_.Reading = true;
            context_.FileName = path_;
            file_ = new MdfFile(context_);
            context_.File = file_;
            header_ = new MdfHeader(context_);
        }

        private bool ReadDataGroup(MdfDataGroup group, ulong start, ulong count)
        {
            if (disposed_ || context_ == null) { return false; }
            PendingDataGroup dataGroup = group.Pending;
            if (dataGroup == null || !context_.DataGroups.Contains(dataGroup)) { return false; }
            if (!ReadMeasurementInfo()) { return false; }
            try
            {
                using (MdfLib.MdfReader engine = OpenEngine())
                {
                    // The engine catalog and the pending tree are built from the same file in the same order.
                    MdfLib.MdfDataGroup engineGroup = engine.ReadMetadata()[dataGroup.Sequence];
                    foreach (PendingGroup pending in dataGroup.Groups)
                    {
                        LoadGroup(engine, engineGroup.ChannelGroups[pending.Index], pending, start, count);
                    }
                }
                dataGroup.Loaded = true;
                return true;
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                Report(ex);
                return false;
            }
        }

        // Reads the readable channels of one channel group in the engine's batches. Raw and physical values are read
        // separately, because a channel with an unreadable conversion has raw values only.
        private static void LoadGroup(MdfLib.MdfReader engine, MdfLib.MdfChannelGroup engineGroup, PendingGroup pending, ulong start, ulong count)
        {
            pending.RawBatches.Clear();
            pending.PhysBatches.Clear();
            pending.RawIndices.Clear();
            pending.PhysIndices.Clear();
            // A channel's facade position is its place in the wrapper's order; the engine reads it by its engine index.
            List<int> rawEngine = new List<int>();
            List<int> physEngine = new List<int>();
            int[] order = FacadeFormat.ChannelOrder(engineGroup.Channels);
            for (int position = 0; position < order.Length; position++)
            {
                MdfLib.MdfChannel channel = engineGroup.Channels[order[position]];
                if (channel.CanReadRaw) { pending.RawIndices.Add(position); rawEngine.Add(order[position]); }
                if (channel.CanReadPhysical) { pending.PhysIndices.Add(position); physEngine.Add(order[position]); }
            }
            ulong total = engineGroup.SampleCount;
            ulong available = total > start ? total - start : 0UL;
            ulong rows = count < available ? count : available;
            if (rows == 0UL) { return; }
            if (pending.RawIndices.Count > 0)
            {
                foreach (MdfLib.MdfSampleBatch batch in engine.ReadSamples(engineGroup, rawEngine, start, rows, 1024, MdfLib.MdfValueRepresentation.Raw))
                {
                    pending.RawBatches.Add(batch);
                }
            }
            if (pending.PhysIndices.Count > 0)
            {
                foreach (MdfLib.MdfSampleBatch batch in engine.ReadSamples(engineGroup, physEngine, start, rows, 1024, MdfLib.MdfValueRepresentation.Physical))
                {
                    pending.PhysBatches.Add(batch);
                }
            }
        }

        private MdfLib.MdfReader OpenEngine()
        {
            FileStream stream = new FileStream(path_, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                return MdfLib.MdfReader.Open(stream, false);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        // What the wrapper reports as false: a file that cannot be opened or read, or does not parse as MDF. Any other
        // exception is a defect and keeps propagating.
        // A read failure the wrapper answers with false is also reported to System.Diagnostics.Trace, with its inner
        // exceptions, so a listener (the reading oracle's facade dump) can name the cause. The answer does not change.
        private void Report(Exception ex)
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder();
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                if (text.Length != 0) { text.Append(" <- "); }
                text.Append(e.GetType().FullName).Append(": ").Append(e.Message);
            }
            System.Diagnostics.Trace.WriteLine("MdfLibrary.MdfReader read failure on " + path_ + ": " + text.ToString());
        }

        private static bool IsReadFailure(Exception ex)
        {
            return ex is MdfLib.MdfFormatException || ex is MdfLib.MdfResourceLimitException || ex is IOException
                || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is ArgumentException
                || ex is InvalidOperationException;
        }
    }
}
