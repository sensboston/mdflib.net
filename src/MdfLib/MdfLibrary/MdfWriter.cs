// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// The MdfWriter collects the channel tree its handle objects mutate and hands it to the managed engine
// writer when the measurement starts. A capability the engine does not have is refused
// with NotSupportedException and never silently dropped. The tree itself lives in FacadeTree.cs.
using System;
using System.IO;

namespace MdfLibrary
{
    /// <summary>The writer the wrapper factory creates.</summary>
    public enum MdfWriterType : int
    {
        /// <summary>MDF 3.30 writer.</summary>
        Mdf3Basic = 0,
        /// <summary>MDF 4 writer.</summary>
        Mdf4Basic = 1,
        /// <summary>Bus logger writer for CAN/LIN/FlexRay/MOST/Ethernet frames.</summary>
        MdfBusLogger = 2,
        /// <summary>MDF 4 converter writer keeping unknown blocks.</summary>
        MdfConverter = 3,
    }

    /// <summary>The bus mask a bus log selects. The engine's
    /// MdfLib.MdfBusType numbers the MDF si_bus_type codes instead, so only the facade's own BusType matches it.</summary>
    public enum MdfBusType : ushort
    {
        /// <summary>No bus selected.</summary>
        UNKNOWN = 0,
        /// <summary>CAN bus.</summary>
        CAN = 1,
        /// <summary>LIN bus.</summary>
        LIN = 2,
        /// <summary>FlexRay bus.</summary>
        FlexRay = 4,
        /// <summary>MOST bus.</summary>
        MOST = 8,
        /// <summary>Ethernet bus.</summary>
        Ethernet = 16,
    }

    /// <summary>How a bus log stores the raw frame payload.</summary>
    public enum MdfStorageType : int
    {
        /// <summary>One fixed length field per frame.</summary>
        FixedLengthStorage = 0,
        /// <summary>Variable length signal data per frame.</summary>
        VlsdStorage = 1,
        /// <summary>Master specific long storage.</summary>
        MlsdStorage = 2,
    }

    /// <summary>The file writer of the library.</summary>
    public class MdfWriter
    {
        private readonly FacadeContext context_;
        private bool disposed_;

        /// <summary>Selects the writer, touches no file. The converter writer is
        /// refused: the managed engine has none.</summary>
        public MdfWriter(MdfWriterType writerType)
        {
            if (writerType == MdfWriterType.MdfConverter)
            { throw new NotSupportedException("The MDF 4 converter writer keeps unknown blocks, which the managed engine does not implement."); }
            context_ = new FacadeContext(writerType);
            if (writerType == MdfWriterType.MdfBusLogger) { context_.StorageType = MdfStorageType.MlsdStorage; }
            context_.File = new MdfFile(context_);
        }

        /// <summary>The bus log configuration belongs to MdfLib.MdfBusLogger, whose
        /// layout is fixed, so it is refused here.</summary>
        public bool CreateBusLogConfiguration()
        {
            if (context_.Type != MdfWriterType.MdfBusLogger)
            { throw new NotSupportedException("CreateBusLogConfiguration configures a bus log, which only an MdfBusLogger writer has."); }
            context_.RejectFlushed();
            if (context_.BusType != MdfBusType.CAN)
            { throw new NotSupportedException("Select the bus with BusType first; the bus logger bridge covers CAN only."); }
            if (!context_.BusConfigured)
            {
                PendingDataGroup dataGroup = context_.AddDataGroup();
                PendingDataGroup.FacadeOf(dataGroup);
                foreach (string name in FacadeContext.CanFrameGroupNames)
                {
                    PendingGroup group = context_.AddChannelGroup(dataGroup);
                    group.Name = name;
                }
                context_.BusConfigured = true;
            }
            return true;
        }

        /// <summary>Adds a data group with its first channel group to the tree.</summary>
        public MdfDataGroup CreateDataGroup()
        {
            context_.RejectFlushed();
            PendingDataGroup dataGroup = context_.AddDataGroup();
            dataGroup.Facade = new MdfDataGroup(dataGroup);
            context_.AddChannelGroup(dataGroup);
            return dataGroup.Facade;
        }

        /// <summary>Closes the file. Like the engine writer it never stops, finalizes
        /// or repairs an unfinished measurement, and a second call does nothing.</summary>
        public void Dispose()
        {
            if (disposed_) { return; }
            disposed_ = true;
            context_.Closed = true;
            context_.DisposeEngine();
        }

        /// <summary>Writes the counters and the finalized identification. Without a
        /// measurement, and on a handle whose Dispose has run, there is nothing to finalize, which the wrapper
        /// reports as false.</summary>
        public bool FinalizeMeasurement()
        {
            if (!context_.Prepared || context_.Closed) { return false; }
            if (!context_.TreeFlushed) { context_.Flush(); }
            if (!context_.Created) { return false; }
            if (context_.MeasurementStarted)
            {
                context_.Drain();
                context_.StopEngine();
                context_.MeasurementStarted = false;
            }
            context_.FinalizeEngine();
            context_.Finalized = true;
            return true;
        }

        /// <summary>Validates the tree and the path and writes the metadata blocks,
        /// which is the point where mdflib brings the file up. False for a tree without a channel group or a
        /// channel group without channels, for a path that cannot be used and for a file that already exists;
        /// a definition the engine cannot store is an exception, because a bool alone would not say which one.</summary>
        public bool InitMeasurement()
        {
            return context_.TreeFlushed || context_.Flush();
        }

        /// <summary>Records the file name and reports whether the path can be used. No
        /// byte is written before InitMeasurement, as in the wrapper, where Init only opens and reads a name.
        /// A file that already exists is recognised by <see cref="IsFileNew"/> and left alone; writing into one
        /// is of the plan, which InitMeasurement reports as false until then.</summary>
        public bool Init(string pathName)
        {
            if (disposed_ || string.IsNullOrEmpty(pathName)) { return false; }
            context_.Path = pathName;
            context_.File.SetFileName(pathName);
            return context_.Probe();
        }

        /// <summary>A CAN frame is written into the bus logger, whose frame group must be the CAN group of the frame.
        /// The frame is stored with the v2.3.0 CAN layout: the payload in full (also CAN FD above 8 bytes), the Dlc as given (its low four bits)
        /// without a check against the payload, and a payload on a remote or overload frame is ignored. FrameDuration, Crc and the R0 and R1 bits
        /// are ignored silently, as the release ignores them.</summary>
        public void SaveCanMessage(MdfChannelGroup group, ulong time, CanMessage message)
        {
            context_.SaveCanFrame(context_.GroupOf(group), time, message);
        }

        /// <summary>See the other overload.</summary>
        public void SaveCanMessage(MdfDataGroup dg, MdfChannelGroup cg, ulong time, CanMessage message)
        {
            PendingGroup group = context_.GroupOf(cg);
            if (group.DataGroupIndex != dg.Index)
            { throw new ArgumentException("The channel group is not part of that data group.", "cg"); }
            context_.SaveCanFrame(group, time, message);
        }

        /// <summary>Writes the values the group's channels hold, with
        /// the time argument as the master value.</summary>
        public void SaveSample(MdfChannelGroup group, ulong time)
        {
            context_.SaveSample(context_.GroupOf(group), time);
        }

        /// <summary>The data group argument must be the group the channel group
        /// belongs to, which the facade checks before writing.</summary>
        public void SaveSample(MdfDataGroup dg, MdfChannelGroup cg, ulong time)
        {
            PendingGroup group = context_.GroupOf(cg);
            if (group.DataGroupIndex != dg.Index)
            { throw new ArgumentException("The channel group is not part of that data group.", "cg"); }
            context_.SaveSample(group, time);
        }

        /// <summary>Starts the measurement and writes the start time into the header,
        /// which the engine can only do for the first measurement of a file.</summary>
        public void StartMeasurement(ulong startTime)
        {
            if (!context_.TreeFlushed)
            {
                context_.Header.StartTime = startTime;
                context_.StartTimeNs = startTime;
                context_.Flush();
            }
            else if (startTime != context_.StartTimeNs)
            {
                // the header is already written; its start time is a fixed-width field the engine patches in place.
                context_.PatchStartTime(startTime);
                context_.Header.StartTime = startTime;
                context_.StartTimeNs = startTime;
            }
            context_.StartCalled = true;
            context_.Start();
            context_.ReleasePreTrigger();
        }

        /// <summary>A DateTime becomes UTC nanoseconds first.</summary>
        public void StartMeasurement(System.DateTime startTime)
        {
            StartMeasurement(FacadeClock.UnixNs(startTime));
        }

        /// <summary>A null timestamp starts with the current wall clock, as in the
        /// wrapper. A local timestamp keeps its raw nanoseconds; a timezone timestamp with offsets cannot be
        /// written, because the engine stores one UTC time and no offset.</summary>
        public void StartMeasurement(IMdfTimeStamp startTime)
        {
            if (startTime == null) { StartMeasurement(FacadeClock.NowNs()); return; }
            MdfTimezoneTimestamp timezone = startTime as MdfTimezoneTimestamp;
            if (timezone != null && (timezone.TimezoneOffsetMin != 0 || timezone.DstOffsetMin != 0))
            { throw new NotSupportedException("The managed writer stores a single UTC start time, so a timezone timestamp with non-zero offsets has nowhere to go."); }
            StartMeasurement(FacadeContext.NanosecondsOf(startTime));
        }

        /// <summary>Records the stop time and flushes the writer. Like the wrapper, the
        /// facade writes no stop time into the header.</summary>
        public void StopMeasurement(ulong stopTime)
        {
            context_.StopTimeNs = stopTime;
            if (context_.Closed || !context_.Created || !context_.MeasurementStarted) { return; }
            context_.Drain();
            context_.StopEngine();
            context_.MeasurementStarted = false;
        }

        /// <summary>A DateTime becomes UTC nanoseconds first.</summary>
        public void StopMeasurement(System.DateTime stopTime)
        {
            StopMeasurement(FacadeClock.UnixNs(stopTime));
        }

        /// <summary>The bus mask of a bus log, which this writer does not create.</summary>
        public MdfBusType BusType
        {
            get
            {
                return context_.BusType;
            }
            set
            {
                if (context_.Type != MdfWriterType.MdfBusLogger)
                { throw new NotSupportedException("BusType configures a bus log, which only an MdfBusLogger writer has."); }
                context_.RejectFlushed();
                if (value != MdfBusType.CAN)
                { throw new NotSupportedException("The bus logger bridge covers CAN only; LIN, FlexRay, MOST and Ethernet logging stay with the engine."); }
                context_.BusType = value;
            }
        }

        /// <summary>Selects zlib compression, which only an MDF 4 file has.</summary>
        public bool CompressData
        {
            get
            {
                return context_.CompressData;
            }
            set
            {
                if (context_.TreeFlushed) { throw new InvalidOperationException("CompressData must be set before the metadata blocks are written."); }
                if (context_.IsMdf3 && value) { throw new NotSupportedException("MDF 3.30 has no compressed data blocks."); }
                context_.CompressData = value;
            }
        }

        /// <summary>The file this writer writes.</summary>
        public MdfFile File
        {
            get
            {
                return context_.File;
            }
        }

        /// <summary>The file's header, the object the wrapper shares with it.</summary>
        public MdfHeader Header
        {
            get
            {
                return context_.File.Header;
            }
        }

        /// <summary>True once the file has really been created, which in the facade is
        /// the point where the tree is written.</summary>
        public bool IsFileNew
        {
            get
            {
                return context_.FileIsNew;
            }
        }

        /// <summary>The converter writer's flag to keep only mandatory members, which
        /// the managed engine has no converter for.</summary>
        public bool MandatoryMemberOnly
        {
            get
            {
                return context_.MandatoryMemberOnly;
            }
            set
            {
                if (value)
                { throw new NotSupportedException("MandatoryMemberOnly only applies to the MDF 4 converter writer, which the managed engine does not implement."); }
                context_.MandatoryMemberOnly = value;
            }
        }

        /// <summary>The longest raw bus message a bus log accepts. The getter answers 8
        /// while no writer exists, which is the wrapper's own default.</summary>
        public uint MaxLength
        {
            get
            {
                return context_.MaxLength;
            }
            set
            {
                if (context_.Type == MdfWriterType.MdfBusLogger)
                {
                    context_.RejectFlushed();
                    if (value > int.MaxValue) { throw new ArgumentOutOfRangeException("value", "MaxLength must fit a 32-bit length."); }
                    context_.MaxLength = value;
                    return;
                }
                if (value != 8)
                { throw new NotSupportedException("MaxLength is the payload width of a bus log; an ordinary writer has none."); }
                context_.MaxLength = value;
            }
        }

        /// <summary>The file name without its extension.</summary>
        public string Name
        {
            get
            {
                return context_.Path == null ? string.Empty : Path.GetFileNameWithoutExtension(context_.Path);
            }
        }

        /// <summary>The pre-trigger time in seconds. Samples and frames saved before StartMeasurement
        /// are kept for this window, as the upstream sample queue keeps them.</summary>
        public double PreTrigTime
        {
            get
            {
                return context_.PreTriggerNs / 1000000000.0;
            }
            set
            {
                if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
                { throw new ArgumentOutOfRangeException("PreTrigTime", "The pre-trigger time must be a finite, non-negative number of seconds."); }
                double nanoseconds = value * 1000000000.0;
                if (nanoseconds >= 1.8e19) { throw new ArgumentOutOfRangeException("PreTrigTime", "The pre-trigger time is too long to count in nanoseconds."); }
                context_.PreTriggerNs = (ulong)nanoseconds;
            }
        }

        /// <summary>Saving every N seconds has no engine counterpart, the managed
        /// equivalent is MdfLib.MdfBackgroundWriter. As in the wrapper the getter answers true while
        /// no file is open.</summary>
        public bool SavePeriodic
        {
            get
            {
                return context_.Writer == null ? true : context_.Periodic;
            }
            set
            {
                if (!value) { context_.Periodic = false; return; }
                throw new NotSupportedException("Periodic saving with the pre-trigger queue is not implemented by the managed writer.");
            }
        }

        /// <summary>The start time in nanoseconds since 1970-01-01, zero until a
        /// measurement has been started.</summary>
        public ulong StartTime
        {
            get
            {
                return context_.StartTimeNs;
            }
        }

        /// <summary>The stop time given to StopMeasurement, zero until then.</summary>
        public ulong StopTime
        {
            get
            {
                return context_.StopTimeNs;
            }
        }

        /// <summary>The payload storage of a bus log, which this writer does not create.</summary>
        public MdfStorageType StorageType
        {
            get
            {
                return context_.StorageType;
            }
            set
            {
                if (context_.Type != MdfWriterType.MdfBusLogger)
                { throw new NotSupportedException("MdfStorageType configures a bus log, which this writer is not."); }
                context_.RejectFlushed();
                if (value != MdfStorageType.MlsdStorage)
                { throw new NotSupportedException("The engine stores CAN payloads as maximum-length (MLSD) byte arrays, so only MlsdStorage can be chosen."); }
                context_.StorageType = value;
            }
        }

        /// <summary>The writer type the file reports. As in the wrapper, an
        /// uncreated writer answers with the type it was constructed for.</summary>
        public MdfWriterType TypeOfWriter
        {
            get
            {
                return context_.Type == MdfWriterType.MdfBusLogger ? MdfWriterType.MdfBusLogger : context_.Writer == null ? MdfWriterType.Mdf4Basic
                    : context_.IsMdf3 ? MdfWriterType.Mdf3Basic : MdfWriterType.Mdf4Basic;
            }
        }
    }
}
