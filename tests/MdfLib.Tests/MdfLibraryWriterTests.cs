// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Phase of the MdfLibrary facade): the writer collects a channel
// tree and hands it to the managed engine, and the file it produces is read back by our own reader. The scenario
// follows the upstream wrapper test MdfLibraryTest.TestWriter; the refusals are the facade's own contract.
using System;
using System.Collections.Generic;
using System.IO;
using MdfLib;
using Xunit;

namespace MdfLib.Tests
{
    public sealed class MdfLibraryWriterTests : IDisposable
    {
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private readonly string directory_;

        public MdfLibraryWriterTests()
        {
            directory_ = Path.Combine(Path.GetTempPath(), "mdflib-facade-w1-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory_);
        }

        public void Dispose()
        {
            try { Directory.Delete(directory_, true); }
            catch (IOException) { }
        }

        private string PathFor(string name)
        {
            return Path.Combine(directory_, name);
        }

        // A double time master, a 32-bit unsigned value with a linear conversion and a UTF-8 text, plus the
        // group name, comment and source the engine stores in the CG blocks: all of it comes back through the
        // reader, which is what the wrapper test checked against the file it had just written.
        [Fact]
        public void WriterCollectsTheTreeAndTheReaderReadsTheSameNamesUnitsAndValues()
        {
            string file = PathFor("core.mf4");
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            Assert.True(writer.Init(file));
            MdfLibrary.MdfDataGroup dataGroup = writer.CreateDataGroup();
            MdfLibrary.MdfChannelGroup group = dataGroup.ChannelGroups[0];
            group.Name = "Engine";
            group.Description = "bench run";
            MdfLibrary.MdfSourceInformation source = group.CreateSourceInformation();
            source.Name = "TestBench";
            source.Path = "Rig/2";
            source.Bus = MdfLibrary.BusType.Can;
            MdfLibrary.MdfChannel master = group.CreateChannel();
            master.Name = "Time";
            master.Unit = "s";
            master.Type = MdfLibrary.ChannelType.Master;
            master.Sync = MdfLibrary.ChannelSyncType.Time;
            master.DataType = MdfLibrary.ChannelDataType.FloatLe;
            master.BitCount = 64;
            MdfLibrary.MdfChannel count = group.CreateChannel();
            count.Name = "Counter";
            count.Unit = "-";
            count.DataType = MdfLibrary.ChannelDataType.UnsignedIntegerLe;
            count.BitCount = 32;
            MdfLibrary.MdfChannelConversion conv = count.CreateChannelConversion();
            conv.Type = MdfLibrary.ConversionType.Linear;
            conv.Parameter(0, 10.0);
            conv.Parameter(1, 2.0);
            Assert.Equal(2, (int)conv.NofParameters);
            MdfLibrary.MdfChannel label = group.CreateChannel();
            label.Name = "Label";
            label.DataType = MdfLibrary.ChannelDataType.StringUTF8;
            label.BitCount = 32;
            // A start time given after InitMeasurement is patched into the header in place. The last
            // value wins, and the header must carry it, not the earlier one.
            writer.Header.StartTime = 1000000000UL;
            Assert.True(writer.InitMeasurement());
            writer.StartMeasurement(7UL);
            Assert.Equal(7UL, writer.StartTime);
            Assert.Equal(7UL, writer.Header.StartTime);
            writer.StartMeasurement(1000000000UL);
            for (ulong sample = 0; sample < 3; sample++)
            {
                count.SetChannelValue(7UL * sample);
                label.SetChannelValue("abc");
                writer.SaveSample(group, 1000000000UL + sample * 1000000UL);
            }
            writer.StopMeasurement(1002000000UL);
            Assert.True(writer.FinalizeMeasurement());
            Assert.Equal(1002000000UL, writer.StopTime);
            Assert.Equal(1000000000UL, writer.StartTime);
            Assert.Equal(1000000000UL, writer.Header.StartTime);
            Assert.Equal(3UL, group.NofSamples);
            Assert.True(writer.File.Finalized);
            writer.Dispose();
            writer.Dispose();

            using (MdfReader reader = MdfReader.Open(File.OpenRead(file)))
            {
                IReadOnlyList<MdfDataGroup> groups = reader.ReadMetadata();
                Assert.Single(groups);
                MdfChannelGroup read = groups[0].ChannelGroups[0];
                Assert.Equal("Engine", read.Name);
                // The engine hands the comment block back as stored, which in MDF 4 is the schema document around the
                // text; the wrapper property the reviewed surface maps is checked against the text inside it.
                Assert.Contains("bench run", read.Comment, StringComparison.Ordinal);
                Assert.Equal("TestBench", read.Source.Name);
                Assert.Equal("Rig/2", read.Source.Path);
                Assert.Equal(2, (int)read.Source.BusType);
                Assert.Equal(3UL, read.SampleCount);
                Assert.Equal("Time", read.Channels[0].Name);
                Assert.Equal("s", read.Channels[0].Unit);
                Assert.True(read.Channels[0].IsMaster);
                Assert.Equal("Counter", read.Channels[1].Name);
                Assert.Equal<double?>(10.0, read.Channels[1].Conversion.Offset);
                Assert.Equal<double?>(2.0, read.Channels[1].Conversion.Factor);
                Assert.Equal("abc", Value(reader, read, 2, 0).TextValue);
                // The master holds seconds from the start, so the first stamp (the start) reads as zero.
                Assert.Equal(0.0, Value(reader, read, 0, 0).FloatingPointValue);
                Assert.Equal(0UL, Value(reader, read, 1, 0).UnsignedValue);
                Assert.Equal(7UL, Value(reader, read, 1, 1).UnsignedValue);
                Assert.Equal(14UL, Value(reader, read, 1, 2).UnsignedValue);
            }
        }

        private static MdfSampleValue Value(MdfReader reader, MdfChannelGroup group, int channel, int sample)
        {
            foreach (MdfSampleBatch batch in reader.ReadSamples(group, new int[] { channel }, (ulong)sample, 1UL))
            {
                return batch.GetValue(0, 0);
            }
            throw new InvalidOperationException("The reader returned no batch for channel " + channel + ".");
        }

        // The facade never overwrites: writing into a file that already exists is, which arrives with the
        // read side, so the second run is reported as false and the bytes of the first file stay as they are.
        [Fact]
        public void SecondWriterOverAnExistingFileIsRefusedAndLeavesTheBytesAlone()
        {
            string file = PathFor("twice.mf4");
            MdfLibrary.MdfWriter first = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            Assert.True(first.Init(file));
            MdfLibrary.MdfChannelGroup group = CreateGroup(first);
            Assert.True(first.InitMeasurement());
            first.StartMeasurement(0UL);
            group.Channels[1].SetChannelValue(1UL);
            first.SaveSample(group, 1UL);
            Assert.True(first.FinalizeMeasurement());
            first.Dispose();
            long length = new FileInfo(file).Length;

            MdfLibrary.MdfWriter second = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            Assert.True(second.Init(file));
            Assert.False(second.IsFileNew);
            Assert.False(second.InitMeasurement());
            second.CreateDataGroup();
            Assert.False(second.InitMeasurement());
            Assert.Equal(length, new FileInfo(file).Length);
            second.Dispose();
        }

        // A group without a stored master and a channel with a field the managed writer has no place for are
        // both refused while the tree is collected, so no file is created for a definition that cannot be written.
        [Fact]
        public void DefinitionsThatTheEngineCannotStoreAreRefusedBeforeAnyByteIsWritten()
        {
            MdfLibrary.MdfWriter noMaster = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            string file = PathFor("no-master.mf4");
            Assert.True(noMaster.Init(file));
            MdfLibrary.MdfChannelGroup group = noMaster.CreateDataGroup().ChannelGroups[0];
            group.CreateChannel().Name = "NoMaster";
            Assert.Throws<NotSupportedException>(() => noMaster.InitMeasurement());
            Assert.False(File.Exists(file));
            // A group without a single channel is mdflib's other PrepareForWriting failure, and it has no field
            // that the engine could refuse, so it is reported as false instead of as an exception.
            MdfLibrary.MdfWriter empty = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            string second = PathFor("empty-group.mf4");
            Assert.True(empty.Init(second));
            empty.CreateDataGroup();
            Assert.False(empty.InitMeasurement());
            Assert.False(File.Exists(second));

            MdfLibrary.MdfWriter limits = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            string other = PathFor("limits.mf4");
            Assert.True(limits.Init(other));
            MdfLibrary.MdfChannelGroup limitGroup = limits.CreateDataGroup().ChannelGroups[0];
            MdfLibrary.MdfChannel time = limitGroup.CreateChannel();
            time.Name = "Time";
            time.Type = MdfLibrary.ChannelType.Master;
            time.Sync = MdfLibrary.ChannelSyncType.Time;
            time.DataType = MdfLibrary.ChannelDataType.FloatLe;
            MdfLibrary.MdfChannel withLimit = limitGroup.CreateChannel();
            withLimit.Name = "Limited";
            withLimit.DataType = MdfLibrary.ChannelDataType.UnsignedIntegerLe;
            withLimit.BitCount = 8;
            withLimit.Limit = System.Tuple.Create(0.0, 10.0);
            Assert.True(withLimit.LimitUsed);
            Assert.False(withLimit.RangeUsed);
            Assert.Equal(10.0, withLimit.Limit.Item2);
            Assert.Throws<NotSupportedException>(() => limits.InitMeasurement());
            Assert.False(File.Exists(other));
        }

        // The wrapper's clock rules: MdfNetHelper converts a DateTime to UTC nanoseconds and a local timestamp
        // through the machine's zone, and the file timestamp forms present the stored value without shifting it.
        [Fact]
        public void TimestampsConvertByTheWrappersOwnRules()
        {
            DateTime utc = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            ulong utcNs = (ulong)utc.Subtract(Epoch).Ticks * 100UL;
            MdfLibrary.IMdfTimeStamp utcStamp = new MdfLibrary.MdfUtcTimestamp(utc);

            DateTime local = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Unspecified);
            ulong localNs = (ulong)local.ToLocalTime().Subtract(Epoch).Ticks * 100UL;
            MdfLibrary.MdfLocalTimestamp localStamp = new MdfLibrary.MdfLocalTimestamp(local);

            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            Assert.True(writer.Init(PathFor("stamps.mf4")));
            CreateGroup(writer);
            writer.Header.SetStartTime(localStamp);
            Assert.Equal(localNs, writer.Header.StartTime);
            writer.StartMeasurement(utcStamp);
            Assert.Equal(utcNs, writer.StartTime);
            Assert.Equal(utcNs, writer.Header.StartTime);
            MdfLibrary.IMdfFileTimestamp readBack = writer.Header.GetStartTime();
            Assert.Equal(utcNs, readBack.TimeNs);
            Assert.Equal(utc, readBack.Time);
            Assert.Equal((short)0, readBack.TimezoneMin);
            Assert.Equal((short)0, readBack.DstMin);

            MdfLibrary.MdfFileUtcTimestamp utcFile = new MdfLibrary.MdfFileUtcTimestamp(42UL);
            Assert.Equal(42UL, utcFile.TimeNs);
            Assert.Equal(Epoch, utcFile.Time);
            Assert.Equal((short)0, utcFile.TimezoneMin);
            MdfLibrary.MdfFileLocalTimestampTz zoneFile = new MdfLibrary.MdfFileLocalTimestampTz(80UL, 60);
            Assert.Equal((short)60, zoneFile.TimezoneMin);
            Assert.Equal((short)0, zoneFile.DstMin);
            Assert.Equal(Epoch, zoneFile.Time);
            MdfLibrary.MdfFileTimezoneTimestamp bothFile = new MdfLibrary.MdfFileTimezoneTimestamp(120UL, 120, 60);
            Assert.Equal((short)120, bothFile.TimezoneMin);
            Assert.Equal((short)60, bothFile.DstMin);
            Assert.Equal(120UL, bothFile.TimeNs);

            MdfLibrary.MdfTimezoneTimestamp offsetStamp = new MdfLibrary.MdfTimezoneTimestamp(utc, 60, 30);
            Assert.Throws<NotSupportedException>(() => writer.Header.SetStartTime(offsetStamp));
            writer.Dispose();
        }

        private static MdfLibrary.MdfChannelGroup CreateGroup(MdfLibrary.MdfWriter writer)
        {
            MdfLibrary.MdfChannelGroup group = writer.CreateDataGroup().ChannelGroups[0];
            MdfLibrary.MdfChannel master = group.CreateChannel();
            master.Name = "Time";
            master.Unit = "s";
            master.Type = MdfLibrary.ChannelType.Master;
            master.Sync = MdfLibrary.ChannelSyncType.Time;
            master.DataType = MdfLibrary.ChannelDataType.FloatLe;
            master.BitCount = 64; // A width is required, and the engine takes it in bits; see the raw-value test for the byte spelling.
            MdfLibrary.MdfChannel value = group.CreateChannel();
            value.Name = "Value";
            value.DataType = MdfLibrary.ChannelDataType.UnsignedIntegerLe;
            value.BitCount = 8;
            return group;
        }

        // The handles share one tree, and the flags the managed writer has no counterpart for are refused
        // instead of changing the format silently. A path whose directory does not exist is reported by Init
        // itself, which is what its "can this path be used" answer means.
        [Fact]
        public void WriterAndFileHandlesShareOneTreeAndRefuseWhatTheEngineCannotWrite()
        {
            // While the file can still be configured the same edit is refused for a different reason: 3.10 is
            // not one of the revisions the managed writer produces at all.
            MdfLibrary.MdfWriter revision = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf3Basic);
            Assert.True(revision.Init(PathFor("revision.mf3")));
            Assert.Throws<NotSupportedException>(() => revision.File.MinorVersion = 10);
            Assert.Equal(30, revision.File.MinorVersion);
            Assert.Equal(3, revision.File.MainVersion);
            Assert.False(revision.File.IsMdf4);
            Assert.Equal("3.30", revision.File.Version);
            revision.Dispose();

            MdfLibrary.MdfWriter missing = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            Assert.False(missing.Init(Path.Combine(directory_, "nested", "handles.mf4")));
            Assert.False(missing.InitMeasurement());
            Assert.False(File.Exists(Path.Combine(directory_, "nested", "handles.mf4")));

            string file = PathFor("handles.mf4");
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf3Basic);
            Assert.Equal(MdfLibrary.MdfWriterType.Mdf4Basic, writer.TypeOfWriter);
            Assert.True(writer.SavePeriodic);
            Assert.Equal(8U, writer.MaxLength);
            Assert.True(writer.Init(file));
            MdfLibrary.MdfChannelGroup group = CreateGroup(writer);
            Assert.True(writer.InitMeasurement());
            Assert.Equal(MdfLibrary.MdfWriterType.Mdf3Basic, writer.TypeOfWriter);
            Assert.Equal("3.30", writer.File.Version);
            Assert.Equal(3, writer.File.MainVersion);
            Assert.Equal(30, writer.File.MinorVersion);
            Assert.False(writer.File.IsMdf4);
            Assert.Equal("handles", writer.File.Name);
            Assert.Equal("handles", writer.Name);
            Assert.False(writer.File.Finalized);
            Assert.Throws<NotSupportedException>(() => writer.CreateBusLogConfiguration());
            Assert.Throws<NotSupportedException>(() => writer.BusType = MdfLibrary.MdfBusType.CAN);
            Assert.Throws<NotSupportedException>(() => writer.StorageType = MdfLibrary.MdfStorageType.VlsdStorage);
            Assert.Throws<NotSupportedException>(() => writer.MandatoryMemberOnly = true);
            Assert.Throws<NotSupportedException>(() => writer.SavePeriodic = true);
            Assert.Throws<NotSupportedException>(() => writer.MaxLength = 16U);
            Assert.Throws<NotSupportedException>(() => writer.Header.CreateFileHistory());
            Assert.Throws<InvalidOperationException>(() => writer.Header.RecorderId = "id");
            Assert.Throws<InvalidOperationException>(() => writer.Header.StartAngle = 1.0);
            Assert.Throws<InvalidOperationException>(() => writer.Header.MeasurementId = "id");
            Assert.Throws<InvalidOperationException>(() => writer.File.ProgramId = "tool");
            Assert.Throws<InvalidOperationException>(() => writer.File.MinorVersion = 10);
            Assert.Throws<NotSupportedException>(() => { MdfLibrary.MdfWriter ignored = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.MdfConverter); });
            Assert.Throws<InvalidOperationException>(() => group.Description = "written already");
            Assert.Throws<InvalidOperationException>(() => group.CreateChannel());
            Assert.Throws<InvalidOperationException>(() => writer.Header.Author = "too late");
            writer.Dispose();
            // Closing the writer is the end of the handle: a sample, a second Dispose and a new Init are all
            // answered without touching the file again, which is what the wrapper's destructor does not do.
            Assert.Throws<InvalidOperationException>(() => writer.SaveSample(group, 5UL));
            Assert.False(writer.Init(file));
            writer.Dispose();
        }

        // MDF 3.30 has no name, comment or source for a channel group and no variable length storage; the
        // facade collects them and then refuses the file, so no 3.30 file quietly loses the metadata it was given.
        [Fact]
        public void Mdf3RefusesTheMetadataAndStorageThatRevisionDoesNotHave()
        {
            string file = PathFor("mdf3.mf3");
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf3Basic);
            Assert.True(writer.Init(file));
            MdfLibrary.MdfChannelGroup group = CreateGroup(writer);
            group.Name = "Ignored";
            Assert.Throws<NotSupportedException>(() => writer.InitMeasurement());
            Assert.False(File.Exists(file));

            string other = PathFor("mdf3-comment.mf3");
            MdfLibrary.MdfWriter commented = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf3Basic);
            Assert.True(commented.Init(other));
            MdfLibrary.MdfChannelGroup commentedGroup = CreateGroup(commented);
            commentedGroup.Description = "no comment block in this revision";
            Assert.Throws<NotSupportedException>(() => commented.InitMeasurement());

            string third = PathFor("mdf3-vlsd.mf3");
            MdfLibrary.MdfWriter vlsd = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf3Basic);
            Assert.True(vlsd.Init(third));
            MdfLibrary.MdfChannelGroup vlsdGroup = vlsd.CreateDataGroup().ChannelGroups[0];
            MdfLibrary.MdfChannel time = vlsdGroup.CreateChannel();
            time.Name = "Time";
            time.Type = MdfLibrary.ChannelType.Master;
            time.Sync = MdfLibrary.ChannelSyncType.Time;
            time.DataType = MdfLibrary.ChannelDataType.FloatLe;
            time.BitCount = 64; // Sized, so what this case reports really is the variable length storage it refuses.
            MdfLibrary.MdfChannel text = vlsdGroup.CreateChannel();
            text.Name = "Text";
            text.DataType = MdfLibrary.ChannelDataType.StringUTF8;
            text.Type = MdfLibrary.ChannelType.VariableLength;
            Assert.Throws<NotSupportedException>(() => vlsd.InitMeasurement());
        }

        // The value rules of the wrapper's raw setters: a float channel writes the IEEE bits it was given, a
        // 8-bit signed channel keeps the low bits of a negative number, and a channel with no value at all is
        // refused before the record is written.
        [Fact]
        public void RawValuesAreWrittenInTheWidthAndKindOfTheChannel()
        {
            string file = PathFor("values.mf4");
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            Assert.True(writer.Init(file));
            MdfLibrary.MdfChannelGroup group = writer.CreateDataGroup().ChannelGroups[0];
            MdfLibrary.MdfChannel master = group.CreateChannel();
            master.Name = "Time";
            master.Type = MdfLibrary.ChannelType.Master;
            master.Sync = MdfLibrary.ChannelSyncType.Time;
            master.DataType = MdfLibrary.ChannelDataType.FloatLe;
            master.DataBytes = 4UL;
            Assert.Equal(32U, master.BitCount); // One field, two units: the byte count is the bit count it stands for.
            MdfLibrary.MdfChannel value = group.CreateChannel();
            value.Name = "Signed";
            value.DataType = MdfLibrary.ChannelDataType.SignedIntegerLe;
            value.BitCount = 8;
            MdfLibrary.MdfChannel bytes = group.CreateChannel();
            bytes.Name = "Bytes";
            bytes.DataType = MdfLibrary.ChannelDataType.ByteArray;
            bytes.BitCount = 24;
            Assert.True(writer.InitMeasurement());
            writer.StartMeasurement(0UL);
            value.SetChannelValue(-2L);
            bytes.SetChannelValue(new byte[] { 1, 2, 3 });
            writer.SaveSample(group, 7UL);
            Assert.True(writer.FinalizeMeasurement());
            writer.Dispose();

            // A channel the caller never gave a value is refused before the row is written: the engine writes
            // exactly one value per channel of the group, and a hole would be a record of the wrong length.
            MdfLibrary.MdfWriter neverSet = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            string other = PathFor("never-set.mf4");
            Assert.True(neverSet.Init(other));
            MdfLibrary.MdfChannelGroup bare = CreateGroup(neverSet);
            Assert.True(neverSet.InitMeasurement());
            neverSet.StartMeasurement(0UL);
            Assert.Throws<InvalidOperationException>(() => neverSet.SaveSample(bare, 1UL));
            Assert.True(neverSet.FinalizeMeasurement());
            neverSet.Dispose();

            using (MdfReader reader = MdfReader.Open(File.OpenRead(file)))
            {
                MdfChannelGroup read = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(1UL, read.SampleCount);
                Assert.Equal(-2L, Value(reader, read, 1, 0).SignedValue);
                Assert.Equal(3, Value(reader, read, 2, 0).GetByteArrayValue().Length);
                Assert.Equal(7e-9, Value(reader, read, 0, 0).FloatingPointValue, 12);
            }
        }

        // The root of the reviewed surface is the one part of the wrapper that needs no engine at all: the
        // singleton a log handler is registered on, the wall clock and the eight-byte probe of the ID block
        //). The probe is the engine's own, so the file the writer
        // just finalized is recognised and a file of anything else is not.
        [Fact]
        public void TheRootSingletonLogsOnItsEventAndProbesAFileByName()
        {
            MdfLibrary.MdfLibrary root = MdfLibrary.MdfLibrary.Instance;
            Assert.Same(root, MdfLibrary.MdfLibrary.Instance);

            List<string> lines = new List<string>();
            MdfLibrary.DoLogEvent handler = delegate (MdfLibrary.MdfLogSeverity severity, string function, string message)
            {
                lines.Add(severity + "|" + function + "|" + message);
            };
            root.LogEvent += handler;
            root.AddLog(MdfLibrary.MdfLogSeverity.Warning, "Init", "no path");
            Assert.Single(lines);
            Assert.Equal("Warning|Init|no path", lines[0]);

            // The destructor of the wrapper unregisters the sink it had installed; here that is the whole of
            // Dispose, so a line after it reaches nobody and the list keeps its single entry.
            root.Dispose();
            root.Dispose();
            root.AddLog(MdfLibrary.MdfLogSeverity.Error, "Flush", "nobody listens");
            Assert.Single(lines); // Dispose unregistered the sink, so the one line is all there still is.

            ulong lower = (ulong)(DateTime.UtcNow.Subtract(Epoch).Ticks - 1) * 100UL;
            ulong now = MdfLibrary.MdfLibrary.NowNs();
            ulong upper = (ulong)(DateTime.UtcNow.Subtract(Epoch).Ticks + 1) * 100UL;
            Assert.InRange(now, lower, upper);

            string file = PathFor("probe.mf4");
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            Assert.True(writer.Init(file));
            MdfLibrary.MdfChannelGroup group = CreateGroup(writer);
            Assert.True(writer.InitMeasurement());
            writer.StartMeasurement(0UL);
            group.Channels[1].SetChannelValue(3UL); // The group's other channel needs a value: a hole is refused.
            writer.SaveSample(group, 1UL);
            Assert.True(writer.FinalizeMeasurement());
            writer.Dispose();

            Assert.True(MdfLibrary.MdfLibrary.IsMdfFile(file));
            string text = PathFor("probe.txt");
            File.WriteAllText(text, "not an MDF file at all");
            Assert.False(MdfLibrary.MdfLibrary.IsMdfFile(text));
            Assert.False(MdfLibrary.MdfLibrary.IsMdfFile(PathFor("missing.mf4")));
            Assert.False(MdfLibrary.MdfLibrary.IsMdfFile(directory_)); // A directory is not readable as a file.
            Assert.False(MdfLibrary.MdfLibrary.IsMdfFile(null));
            Assert.False(MdfLibrary.MdfLibrary.IsMdfFile(string.Empty));
        }

        // keeps the wrapper's exception types observable: it dereferences a handle argument without checking
        // it, so a null data group is a NullReferenceException there and the facade, which reaches for the same
        // object, stays one. Where the wrapper has no argument check either but the facade has to look the handle
        // up in its own tree, a null and a foreign group are answered by that lookup, with a message the wrapper
        // could not give. The value setters of the channel are the facade's own refusal of a lost value.
        [Fact]
        public void HandleAndValueArgumentsKeepTheWrappersOwnExceptions()
        {
            string file = PathFor("handles.mf4");
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            Assert.True(writer.Init(file));
            // The group's master is the float the helper creates, so the channel added here is the second value
            // of the record; none of them is written, because this case only reaches the refusals.
            MdfLibrary.MdfChannelGroup group = CreateGroup(writer);
            MdfLibrary.MdfChannel channel = group.CreateChannel();
            channel.Name = "Text";
            channel.DataType = MdfLibrary.ChannelDataType.FloatLe;
            channel.BitCount = 64;
            Assert.True(writer.InitMeasurement());
            writer.StartMeasurement(0UL);

            Assert.Throws<ArgumentException>(() => writer.SaveSample(null, 1UL));
            Assert.Throws<NullReferenceException>(() => writer.SaveSample(null, group, 1UL));

            // A group of another file is the same lookup failure, and a tree edit after the flush stays refused.
            MdfLibrary.MdfWriter other = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            Assert.True(other.Init(PathFor("other.mf4")));
            MdfLibrary.MdfChannelGroup foreign = CreateGroup(other);
            Assert.Throws<ArgumentException>(() => writer.SaveSample(foreign, 1UL));
            Assert.Throws<InvalidOperationException>(() => group.CreateChannel());

            Assert.Throws<ArgumentNullException>(() => channel.SetChannelValue((string)null));
            Assert.Throws<ArgumentNullException>(() => channel.SetChannelValue((byte[])null, true));

            Assert.True(writer.FinalizeMeasurement());
            writer.Dispose();
            other.Dispose();
        }
    }
}
