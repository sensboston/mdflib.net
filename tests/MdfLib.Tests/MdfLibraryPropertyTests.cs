// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Facade coverage): every property and method
// of the channel, channel group and channel conversion wrappers is written and read back on the tree the writer collects,
// and the refusals are checked: a flushed tree, a message payload past 64 bytes.
// These are unit tests of the facade's own code; the file format is not compared here.
using System;
using System.IO;
using Xunit;

namespace MdfLib.Tests
{
    public sealed class MdfLibraryPropertyTests : IDisposable
    {
        private readonly string directory_;

        public MdfLibraryPropertyTests()
        {
            directory_ = Path.Combine(Path.GetTempPath(), "mdflib-facade-property-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory_);
        }

        public void Dispose()
        {
            try { Directory.Delete(directory_, true); }
            catch (IOException) { }
        }

        private MdfLibrary.MdfWriter NewWriter(string name)
        {
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            Assert.True(writer.Init(Path.Combine(directory_, name + ".mf4")));
            return writer;
        }

        [Fact]
        public void ChannelPropertiesAndMethodsRoundTripOnTheCollectedTree()
        {
            MdfLibrary.MdfWriter writer = NewWriter("channel");
            using (Release(writer))
            {
                MdfLibrary.MdfChannelGroup group = writer.CreateDataGroup().ChannelGroups[0];
                MdfLibrary.MdfChannel channel = group.CreateChannel();
                channel.Name = "Speed";
                channel.DisplayName = "Speed display";
                channel.Description = "vehicle speed";
                channel.Unit = "km/h";
                Assert.True(channel.UnitUsed);
                channel.DataType = MdfLibrary.ChannelDataType.FloatLe;
                channel.BitCount = 16;
                channel.BitOffset = 4;
                channel.Sync = MdfLibrary.ChannelSyncType.Time;
                channel.Type = MdfLibrary.ChannelType.FixedLength;
                channel.SamplingRate = 10.5;
                channel.VlsdRecordId = 3UL;
                channel.DataBytes = 8UL;
                channel.Flags = (MdfLibrary.CnFlag)0;
                channel.Limit = Tuple.Create(0.0, 200.0);
                Assert.True(channel.LimitUsed);
                channel.ExtLimit = Tuple.Create(-10.0, 210.0);
                Assert.True(channel.ExtLimitUsed);
                channel.Range = Tuple.Create(1.0, 5.0);
                Assert.True(channel.RangeUsed);

                Assert.Equal("Speed", channel.Name);
                Assert.Equal("Speed display", channel.DisplayName);
                Assert.Equal("vehicle speed", channel.Description);
                Assert.Equal("km/h", channel.Unit);
                Assert.Equal(MdfLibrary.ChannelDataType.FloatLe, channel.DataType);
                // DataBytes is written after BitCount, so the width it sets (8 bytes = 64 bits) is the one read back.
                Assert.Equal(64UL, (ulong)channel.BitCount);
                Assert.Equal((ushort)4, channel.BitOffset);
                Assert.Equal(MdfLibrary.ChannelSyncType.Time, channel.Sync);
                Assert.Equal(MdfLibrary.ChannelType.FixedLength, channel.Type);
                Assert.Equal(0.0, channel.SamplingRate); // v2.3.0 keeps no sampling rate: the value reads back as 0
                Assert.Equal(3UL, channel.VlsdRecordId);
                Assert.Equal(8UL, channel.DataBytes);
                Assert.Equal(Tuple.Create(0.0, 200.0), channel.Limit);
                Assert.Equal(Tuple.Create(-10.0, 210.0), channel.ExtLimit);
                Assert.Equal(Tuple.Create(1.0, 5.0), channel.Range);
                Assert.Equal((MdfLibrary.CnFlag)0, channel.Flags);
                Assert.False(channel.PrecisionUsed);
                Assert.Equal((byte)2, channel.Precision); // the channel has no precision setter: the engine stores two digits
                // Before any conversion or source exists, the wrapper hands out null for both.
                Assert.Null(channel.ChannelConversion);
                Assert.Null(channel.SourceInformation);
                MdfLibrary.MdfChannelConversion conversion = channel.CreateChannelConversion();
                Assert.NotNull(conversion);
                Assert.NotNull(channel.ChannelConversion);
                MdfLibrary.MdfSourceInformation source = channel.CreateSourceInformation();
                Assert.NotNull(source);
                Assert.NotNull(channel.SourceInformation);
                Assert.NotNull(channel.CreateMetaData());
                // A structure keeps no conversion of its own, so the composition is made on a channel without one.
                MdfLibrary.MdfChannel structure = group.CreateChannel();
                structure.Name = "Structure";
                structure.Type = MdfLibrary.ChannelType.FixedLength;
                MdfLibrary.MdfChannel composition = structure.CreateChannelComposition();
                Assert.NotNull(composition);
                Assert.Single(structure.ChannelCompositions);
            }
        }

        [Fact]
        public void ChannelRefusesChangesOnceTheMeasurementIsInitialized()
        {
            MdfLibrary.MdfWriter writer = NewWriter("flushed");
            using (Release(writer))
            {
                MdfLibrary.MdfChannelGroup group = writer.CreateDataGroup().ChannelGroups[0];
                MdfLibrary.MdfChannel channel = group.CreateChannel();
                channel.Name = "Counter";
                channel.DataType = MdfLibrary.ChannelDataType.UnsignedIntegerLe;
                channel.BitCount = 32;
                MdfLibrary.MdfChannel time = group.CreateChannel();
                time.Name = "Time";
                time.Type = MdfLibrary.ChannelType.Master;
                time.Sync = MdfLibrary.ChannelSyncType.Time;
                time.DataType = MdfLibrary.ChannelDataType.FloatLe;
                time.BitCount = 64;
                Assert.True(writer.InitMeasurement());
                Assert.Throws<InvalidOperationException>(() => channel.Name = "Renamed");
                Assert.Throws<InvalidOperationException>(() => channel.Unit = "s");
                Assert.Throws<InvalidOperationException>(() => channel.Description = "late");
                Assert.Throws<InvalidOperationException>(() => channel.DisplayName = "late");
                Assert.Throws<InvalidOperationException>(() => channel.Limit = Tuple.Create(0.0, 1.0));
                Assert.Throws<InvalidOperationException>(() => channel.ExtLimit = Tuple.Create(0.0, 1.0));
                Assert.Throws<InvalidOperationException>(() => channel.Range = Tuple.Create(0.0, 1.0));
                Assert.Throws<InvalidOperationException>(() => channel.VlsdRecordId = 1UL);
                Assert.Throws<InvalidOperationException>(() => channel.DataBytes = 1UL);
                Assert.Throws<InvalidOperationException>(() => channel.Flags = (MdfLibrary.CnFlag)0);
                Assert.Equal("Counter", channel.Name);
            }
        }

        [Fact]
        public void ChannelGroupPropertiesAndTheMasterLookupRoundTrip()
        {
            MdfLibrary.MdfWriter writer = NewWriter("group");
            using (Release(writer))
            {
                MdfLibrary.MdfChannelGroup group = writer.CreateDataGroup().ChannelGroups[0];
                group.Name = "Engine";
                group.Description = "bench";
                group.Flags = 1;
                group.RecordId = 2UL;
                group.PathSeparator = '.';
                Assert.Equal("Engine", group.Name);
                Assert.Equal("bench", group.Description);
                Assert.Equal((ushort)1, group.Flags);
                Assert.Equal(2UL, group.RecordId);
                Assert.Equal('.', group.PathSeparator);
                group.NofSamples = 0UL;
                Assert.Throws<NotSupportedException>(() => group.NofSamples = 5UL);
                Assert.NotNull(group.CreateMetaData());
                Assert.NotNull(group.CreateSourceInformation());
                Assert.NotNull(group.SourceInformation);

                MdfLibrary.MdfChannel data = group.CreateChannel();
                data.Name = "Data";
                MdfLibrary.MdfChannel master = group.CreateChannel();
                master.Name = "Time";
                master.Type = MdfLibrary.ChannelType.Master;
                master.Sync = MdfLibrary.ChannelSyncType.Time;
                Assert.Equal(2, group.Channels.Length);
                Assert.Equal(MdfLibrary.ChannelType.Master, master.Type);
            }
        }

        [Fact]
        public void ChannelGroupRefusesStructuralChangesOnceTheMeasurementIsInitialized()
        {
            MdfLibrary.MdfWriter writer = NewWriter("group-flushed");
            using (Release(writer))
            {
                MdfLibrary.MdfChannelGroup group = writer.CreateDataGroup().ChannelGroups[0];
                MdfLibrary.MdfChannel channel = group.CreateChannel();
                channel.Name = "Value";
                channel.DataType = MdfLibrary.ChannelDataType.UnsignedIntegerLe;
                channel.BitCount = 8;
                MdfLibrary.MdfChannel time = group.CreateChannel();
                time.Name = "Time";
                time.Type = MdfLibrary.ChannelType.Master;
                time.Sync = MdfLibrary.ChannelSyncType.Time;
                time.DataType = MdfLibrary.ChannelDataType.FloatLe;
                time.BitCount = 64;
                Assert.True(writer.InitMeasurement());
                Assert.Throws<InvalidOperationException>(() => group.Flags = 2);
                Assert.Throws<InvalidOperationException>(() => group.RecordId = 9UL);
                Assert.Throws<InvalidOperationException>(() => group.PathSeparator = '.');
            }
        }

        [Fact]
        public void ChannelConversionPropertiesParametersAndReferencesRoundTrip()
        {
            MdfLibrary.MdfWriter writer = NewWriter("conversion");
            using (Release(writer))
            {
                MdfLibrary.MdfChannel channel = writer.CreateDataGroup().ChannelGroups[0].CreateChannel();
                channel.Name = "Raw";
                channel.DataType = MdfLibrary.ChannelDataType.UnsignedIntegerLe;
                channel.BitCount = 16;
                MdfLibrary.MdfChannelConversion conversion = channel.CreateChannelConversion();
                conversion.Name = "conv";
                conversion.Description = "linear";
                conversion.Type = MdfLibrary.ConversionType.Linear;
                conversion.Unit = "V";
                Assert.True(conversion.UnitUsed);
                conversion.Formula = null;
                Assert.Equal(string.Empty, conversion.Formula);
                conversion.Formula = "X*2";
                Assert.Equal("X*2", conversion.Formula);
                conversion.Flags = 0;
                conversion.Precision = 3;
                Assert.Equal((byte)3, conversion.Precision);
                conversion.Range = Tuple.Create(0.0, 1.0);
                Assert.True(conversion.RangeUsed);
                Assert.Equal(Tuple.Create(0.0, 1.0), conversion.Range);
                conversion.Range = null;
                conversion.Parameter(0, 10.0);
                conversion.Parameter(1, 2.0);
                conversion.ParameterUInt(2, 7UL);
                Assert.Equal(10.0, conversion.Parameter(0));
                Assert.Equal(2.0, conversion.Parameter(1));
                Assert.Equal(7UL, conversion.ParameterUInt(2));
                conversion.Reference(0, "ref.channel");
                Assert.Equal("ref.channel", conversion.Reference(0));
                Assert.Equal((ushort)1, conversion.NofReferences);
                Assert.Equal(MdfLibrary.ConversionType.Linear, conversion.Type);
                Assert.NotNull(conversion.CreateMetaData());
                Assert.NotNull(conversion.CreateInverse());
            }
        }

        [Fact]
        public void CanMessageCarriesItsFieldsAndRefusesOutOfRangeLengths()
        {
            MdfLibrary.CanMessage message = new MdfLibrary.CanMessage();
            message.MessageId = 0x1ABCDEFu;
            message.ExtendedId = true;
            message.BusChannel = 2;
            message.Timestamp = 1.5;
            message.Crc = 0xABCDu;
            message.FrameDuration = 100u;
            message.BitPosition = 5;
            message.ErrorType = MdfLibrary.CanErrorType.BIT_ERROR;
            message.TypeOfMessage = MdfLibrary.CanMessageType.Can_DataFrame;
            message.Brs = true;
            message.Edl = true;
            message.Esi = true;
            message.Dir = true;
            message.R0 = true;
            message.R1 = true;
            message.Srr = true;
            message.SingleWire = true;
            message.WakeUp = true;
            Assert.Equal(0x1ABCDEFu, message.MessageId);
            Assert.True(message.ExtendedId);
            Assert.Equal((byte)2, message.BusChannel);
            Assert.Equal(1.5, message.Timestamp);
            Assert.Equal(0xABCDu, message.Crc);
            Assert.Equal(100u, message.FrameDuration);
            Assert.Equal((ushort)5, message.BitPosition);
            Assert.Equal(MdfLibrary.CanErrorType.BIT_ERROR, message.ErrorType);
            Assert.True(message.Brs && message.Edl && message.Esi && message.Dir && message.R0 && message.R1 && message.Srr);
            Assert.True(message.SingleWire && message.WakeUp);

            message.DataLength = 8u;
            Assert.Equal(8u, message.DataLength);
            Assert.Equal((byte)8, message.Dlc);
            Assert.Throws<ArgumentOutOfRangeException>(() => message.DataLength = 65u);
            message.DataBytes = new byte[] { 1, 2, 3 };
            Assert.Equal(new byte[] { 1, 2, 3 }, message.DataBytes);
            Assert.Equal(3u, message.DataLength);
            Assert.Equal((byte)3, message.Dlc);
            Assert.Throws<ArgumentOutOfRangeException>(() => message.DataBytes = new byte[65]);
            message.DataBytes = null;
            Assert.Equal(0u, message.DataLength);
            message.Dlc = 12;
            Assert.Equal((byte)12, message.Dlc);
            Assert.Throws<ArgumentOutOfRangeException>(() => message.Dlc = 16);

            message.Rtr = true;
            Assert.True(message.Rtr);
            Assert.Equal(MdfLibrary.CanMessageType.Can_RemoteFrame, message.TypeOfMessage);
            message.Rtr = false;
            Assert.False(message.Rtr);
            Assert.Equal(MdfLibrary.CanMessageType.Can_DataFrame, message.TypeOfMessage);

            Assert.Equal((byte)12, MdfLibrary.CanMessage.DlcToLength(9));
            Assert.Equal((byte)64, MdfLibrary.CanMessage.DlcToLength(15));
            Assert.Equal((byte)0, MdfLibrary.CanMessage.DlcToLength(16));
            message.Dispose();
            Assert.Equal(0u, message.DataLength);
        }

        // MdfWriter has a Dispose method but does not implement IDisposable, so a using statement cannot take it directly.
        private static IDisposable Release(MdfLibrary.MdfWriter writer)
        {
            return new Scope(writer.Dispose);
        }

        private sealed class Scope : IDisposable
        {
            private readonly Action action_;

            public Scope(Action action)
            {
                action_ = action;
            }

            public void Dispose()
            {
                action_();
            }
        }
    }
}
