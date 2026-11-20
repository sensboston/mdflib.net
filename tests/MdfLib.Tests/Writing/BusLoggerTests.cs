// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Writing
{
    public sealed class BusLoggerTests
    {
        private static byte[] Log(MdfBusType bus, IEnumerable<MdfBusMessage> messages, MdfBusLoggerOptions options = null)
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfBusLogger logger = MdfBusLogger.Create(output, bus, true, options))
            {
                logger.StartMeasurement();
                foreach (MdfBusMessage message in messages) { logger.Write(message); }
                logger.StopMeasurement();
                logger.FinalizeMeasurement();
                Assert.Equal(MdfWriterState.Finalized, logger.State);
            }
            return output.Snapshot();
        }

        private static List<MdfBusMessage> Read(byte[] file)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file)))
            {
                List<MdfBusMessage> messages = MdfBusReader.ReadMessages(reader).ToList();
                Assert.True(MdfValidator.Validate(reader).IsValid);
                return messages;
            }
        }

        private static void Same(MdfBusMessage expected, MdfBusMessage actual)
        {
            Assert.Equal(expected.Type, actual.Type);
            Assert.Equal(expected.Timestamp, actual.Timestamp);
            Assert.Equal(expected.BusChannel, actual.BusChannel);
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.IsExtendedId, actual.IsExtendedId);
            Assert.Equal(expected.Direction, actual.Direction);
            Assert.Equal(expected.Data ?? new byte[0], actual.Data ?? new byte[0]);
            Assert.Equal(expected.IsFd, actual.IsFd);
            Assert.Equal(expected.BitRateSwitch, actual.BitRateSwitch);
            Assert.Equal(expected.ErrorStateIndicator, actual.ErrorStateIndicator);
            Assert.Equal(expected.ErrorType, actual.ErrorType);
            Assert.Equal(expected.BitPosition, actual.BitPosition);
            Assert.Equal(expected.Crc, actual.Crc);
            Assert.Equal(expected.FrameDuration, actual.FrameDuration);
            Assert.Equal(expected.Checksum, actual.Checksum);
            Assert.Equal(expected.Source, actual.Source);
            Assert.Equal(expected.Destination, actual.Destination);
            Assert.Equal(expected.EtherType, actual.EtherType);
            Assert.Equal(expected.Cycle, actual.Cycle);
            Assert.Equal(expected.FlexRayChannel, actual.FlexRayChannel);
        }

        private static byte[] Bytes(int count, int seed) { return Enumerable.Range(0, count).Select(i => (byte)(seed + 7 * i)).ToArray(); }

        private static List<MdfBusMessage> CanMessages()
        {
            return new List<MdfBusMessage>
            {
                new MdfBusMessage { Type = MdfBusMessageType.CanDataFrame, Timestamp = 0.001, BusChannel = 1, Id = 0x123, Data = Bytes(8, 1), Crc = 0x1234, FrameDuration = 222000 },
                new MdfBusMessage { Type = MdfBusMessageType.CanDataFrame, Timestamp = 0.002, BusChannel = 2, Id = 0x18FF1234, IsExtendedId = true, Direction = MdfBusDirection.Tx, Data = Bytes(3, 9) },
                new MdfBusMessage { Type = MdfBusMessageType.CanDataFrame, Timestamp = 0.003, Id = 0x7FF, Data = Bytes(64, 3), IsFd = true, BitRateSwitch = true, ErrorStateIndicator = true },
                new MdfBusMessage { Type = MdfBusMessageType.CanDataFrame, Timestamp = 0.004, Id = 0, Data = null },
                new MdfBusMessage { Type = MdfBusMessageType.CanDataFrame, Timestamp = 0.005, Id = 0x1FFFFFFF, IsExtendedId = true, Data = Bytes(12, 5), IsFd = true },
                new MdfBusMessage { Type = MdfBusMessageType.CanRemoteFrame, Timestamp = 0.0015, BusChannel = 3, Id = 0x456, Dlc = 4, Direction = MdfBusDirection.Tx, Crc = 77 },
                new MdfBusMessage { Type = MdfBusMessageType.CanErrorFrame, Timestamp = 0.0025, BusChannel = 4, Id = 0x10, Data = Bytes(2, 200), ErrorType = 5, BitPosition = 513, Crc = 0xFFFFFFFF, FrameDuration = 1 },
            };
        }

        [Fact]
        public void CanFramesRoundTripThroughAsamBusLoggingGroups()
        {
            List<MdfBusMessage> expected = CanMessages();
            byte[] file = Log(MdfBusType.Can, expected, new MdfBusLoggerOptions { NetworkName = "Powertrain", StartTimeNanoseconds = 1700000000000000000UL });
            List<MdfBusMessage> actual = Read(file);
            // Group order: data frames, remote frames, error frames; each in record order.
            MdfBusMessage[] ordered = expected.Where(m => m.Type == MdfBusMessageType.CanDataFrame)
                .Concat(expected.Where(m => m.Type == MdfBusMessageType.CanRemoteFrame))
                .Concat(expected.Where(m => m.Type == MdfBusMessageType.CanErrorFrame)).ToArray();
            Assert.Equal(ordered.Length, actual.Count);
            for (int i = 0; i < ordered.Length; i++) { Same(ordered[i], actual[i]); }
            Assert.Equal(new byte[] { 8, 3, 15, 0, 9, 4, 2 }, actual.Select(m => m.Dlc).ToArray());
        }

        [Fact]
        public void CanMetadataFollowsTheAsamNamingAndFlags()
        {
            byte[] file = Log(MdfBusType.Can, CanMessages(), new MdfBusLoggerOptions { MaxDataLength = 64 });
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file)))
            {
                IReadOnlyList<MdfDataGroup> groups = reader.ReadMetadata();
                Assert.Equal(new[] { "CAN_DataFrame", "CAN_RemoteFrame", "CAN_ErrorFrame" }, groups.Select(g => g.ChannelGroups.Single().Name).ToArray());
                MdfChannelGroup data = groups[0].ChannelGroups[0];
                Assert.Equal((ushort)6, data.Flags);
                Assert.Equal('.', data.PathSeparator);
                Assert.Equal((byte)2, data.Source.BusType);
                Assert.Equal((byte)2, data.Source.SourceType);
                Assert.Equal("CAN", data.Source.Name);
                Assert.Equal("CAN_DataFrame", data.Source.Path);
                Assert.Equal(24UL + 64, data.RecordByteCount);
                Assert.Equal(5UL, data.SampleCount);
                MdfChannel time = data.Channels[0];
                Assert.True(time.IsMaster);
                Assert.Equal("s", time.Unit);
                MdfChannel frame = data.Channels.Single(c => c.Name == "CAN_DataFrame");
                Assert.Equal(MdfValueKind.ByteArray, frame.ValueKind);
                Assert.Equal(new[] { "BusChannel", "ID", "IDE", "DLC", "DataLength", "Dir", "EDL", "BRS", "ESI", "FrameDuration", "CRC", "SRR", "WakeUp", "SingleWire", "R0", "R1", "DataBytes" },
                    frame.Components.Select(c => c.Name.Substring("CAN_DataFrame.".Length)).ToArray());
                Assert.All(data.Channels, c => Assert.True(c.CanReadRaw, c.Name + ": " + c.UnsupportedReason));
                Assert.Equal("B", frame.Components.Single(c => c.Name == "CAN_DataFrame.DataLength").Unit);
                Assert.Equal("ns", frame.Components.Single(c => c.Name == "CAN_DataFrame.FrameDuration").Unit);
                // The MLSD payload reads through the ordinary sample API with exactly DataLength bytes.
                MdfChannel bytes = frame.Components.Last();
                List<int> lengths = new List<int>();
                foreach (MdfSampleBatch batch in reader.ReadSamples(data, new[] { bytes.Index }))
                { for (int r = 0; r < batch.SampleCount; r++) { lengths.Add(batch.GetValue(r, 0).GetByteArrayValue().Length); } }
                Assert.Equal(new[] { 8, 3, 64, 0, 12 }, lengths);
                Assert.DoesNotContain(groups[1].ChannelGroups[0].Channels, c => c.Name.EndsWith(".DataBytes", StringComparison.Ordinal));
            }
        }

        [Fact]
        public void LinRecordBytesMatchTheAsamBitLayout()
        {
            MdfBusMessage frame = new MdfBusMessage
            {
                Type = MdfBusMessageType.LinFrame, Timestamp = 1.5, BusChannel = 7, Id = 0x3C, Direction = MdfBusDirection.Tx,
                Data = new byte[] { 0xA1, 0xB2, 0xC3 }, Checksum = 0x5E
            };
            byte[] file = Log(MdfBusType.Lin, new[] { frame });
            // One group, one uncompressed DT: the last 45-byte record (all optional members) precedes 3 alignment bytes.
            byte[] record = new byte[45];
            Array.Copy(file, file.Length - 48, record, 0, 45);
            Assert.Equal(1.5, BitConverter.ToDouble(record, 0));
            Assert.Equal(7, record[8]);
            Assert.Equal(0x3C | 0x40, record[9]);         // ID bits 0..5, Dir bit 6.
            Assert.Equal(0x03 | 0x30, record[10]);        // ReceivedDataByteCount bits 0..3, DataLength bits 4..7.
            Assert.Equal(new byte[] { 0xA1, 0xB2, 0xC3, 0, 0, 0, 0, 0 }, record.Skip(11).Take(8).ToArray());
            Assert.Equal(0x5E, record[19]);
            Assert.Equal(new byte[25], record.Skip(20).ToArray()); // ChecksumModel, SOF, baud rates and break lengths default to zero.
            Same(frame, Read(file).Single());
        }

        [Fact]
        public void ExtendedCanIdentifierSharesItsWordWithTheIdeBit()
        {
            MdfBusMessage frame = new MdfBusMessage { Type = MdfBusMessageType.CanRemoteFrame, Id = 0x18FF1234, IsExtendedId = true, Dlc = 8 };
            byte[] file = Log(MdfBusType.Can, new[] { frame }, new MdfBusLoggerOptions { MaxDataLength = 8 });
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file)))
            {
                MdfChannelGroup remote = reader.ReadMetadata()[1].ChannelGroups[0];
                MdfChannel parent = remote.Channels.Single(c => c.Name == "CAN_RemoteFrame");
                foreach (MdfSampleBatch batch in reader.ReadSamples(remote, new[] { parent.Index }))
                {
                    byte[] window = batch.GetValue(0, 0).GetByteArrayValue();
                    Assert.Equal(16, window.Length);
                    Assert.Equal(0x98FF1234U, BitConverter.ToUInt32(window, 1)); // ID bits 0..28, IDE bit 31.
                    Assert.Equal(8, window[5] & 0x0F);                           // DLC.
                    Assert.Equal(8, window[6]);                                  // DataLength.
                }
            }
        }

        [Fact]
        public void EthernetAndFlexRayFramesRoundTrip()
        {
            MdfBusMessage[] eth =
            {
                new MdfBusMessage { Type = MdfBusMessageType.EthernetFrame, Timestamp = 0.25, BusChannel = 1, Source = 0x0011223344FFUL, Destination = 0xFFFFFFFFFFFFUL,
                    EtherType = 0x86DD, Data = Bytes(1500, 1), Crc = 0xDEADBEEF, Direction = MdfBusDirection.Tx },
                new MdfBusMessage { Type = MdfBusMessageType.EthernetFrame, Timestamp = 0.5, EtherType = 0x0800, Data = Bytes(46, 2) },
            };
            List<MdfBusMessage> ethRead = Read(Log(MdfBusType.Ethernet, eth, new MdfBusLoggerOptions { CompressData = true }));
            Assert.Equal(2, ethRead.Count);
            Same(eth[0], ethRead[0]);
            Same(eth[1], ethRead[1]);

            MdfBusMessage[] flx =
            {
                new MdfBusMessage { Type = MdfBusMessageType.FlexRayFrame, Timestamp = 2, BusChannel = 1, Id = 2047, Cycle = 63, FlexRayChannel = 1,
                    Direction = MdfBusDirection.Tx, Data = Bytes(254, 4), Crc = 0xFFFFFF, FrameDuration = 5000 },
                new MdfBusMessage { Type = MdfBusMessageType.FlexRayFrame, Timestamp = 3, Id = 1, Data = Bytes(5, 6) },
            };
            byte[] flexRay = Log(MdfBusType.FlexRay, flx, new MdfBusLoggerOptions { Version = "4.20" });
            List<MdfBusMessage> flxRead = Read(flexRay);
            Same(flx[0], flxRead[0]);
            Same(flx[1], flxRead[1]);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(flexRay)))
            {
                MdfChannelGroup group = reader.ReadMetadata().Single().ChannelGroups.Single();
                Assert.Equal((byte)5, group.Source.BusType);
                MdfChannel payload = group.Channels.Single(c => c.Name == "FLX_Frame.PayloadLength");
                Assert.Equal("word", payload.Unit);
                List<ulong> words = new List<ulong>();
                foreach (MdfSampleBatch batch in reader.ReadSamples(group, new[] { payload.Index }))
                { for (int r = 0; r < batch.SampleCount; r++) { words.Add(batch.GetValue(r, 0).UnsignedValue); } }
                Assert.Equal(new ulong[] { 127, 3 }, words);
            }
        }

        [Fact]
        public void ManyCompressedFramesRoundTripInEveryGroup()
        {
            List<MdfBusMessage> expected = new List<MdfBusMessage>();
            for (int i = 0; i < 3000; i++)
            {
                MdfBusMessageType type = i % 10 == 9 ? MdfBusMessageType.CanErrorFrame : i % 10 == 8 ? MdfBusMessageType.CanRemoteFrame : MdfBusMessageType.CanDataFrame;
                expected.Add(new MdfBusMessage
                {
                    Type = type, Timestamp = i * 0.001, Id = (uint)(i % 0x800), BusChannel = (byte)(i % 4),
                    Data = type == MdfBusMessageType.CanRemoteFrame ? null : Bytes(i % 9, i), Dlc = type == MdfBusMessageType.CanRemoteFrame ? (byte)(i % 9) : (byte)0
                });
            }
            byte[] file = Log(MdfBusType.Can, expected, new MdfBusLoggerOptions { CompressData = true, MaxDataLength = 8, CompressionBlockBytes = 4096 });
            List<MdfBusMessage> actual = Read(file);
            MdfBusMessage[] ordered = expected.OrderBy(m => m.Type).ToArray(); // Stable: record order within each group.
            Assert.Equal(ordered.Length, actual.Count);
            for (int i = 0; i < ordered.Length; i++) { Same(ordered[i], actual[i]); }
        }

        [Fact]
        public void EmptyLogIsAValidFileWithEmptyGroups()
        {
            byte[] file = Log(MdfBusType.Can, new MdfBusMessage[0]);
            Assert.Empty(Read(file));
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file)))
            { Assert.All(reader.ReadMetadata(), g => Assert.Equal(0UL, g.ChannelGroups[0].SampleCount)); }
        }

        private static IEnumerable<object[]> InvalidFrames()
        {
            yield return new object[] { MdfBusType.Can, new MdfBusMessage { Id = 0x800 } };
            yield return new object[] { MdfBusType.Can, new MdfBusMessage { Id = 0x20000000, IsExtendedId = true } };
            yield return new object[] { MdfBusType.Can, new MdfBusMessage { Data = new byte[9] } };
            yield return new object[] { MdfBusType.Can, new MdfBusMessage { Data = new byte[13], IsFd = true } };
            yield return new object[] { MdfBusType.Can, new MdfBusMessage { Data = new byte[1], BitRateSwitch = true } };
            yield return new object[] { MdfBusType.Can, new MdfBusMessage { Type = MdfBusMessageType.CanRemoteFrame, Data = new byte[1] } };
            yield return new object[] { MdfBusType.Can, new MdfBusMessage { Type = MdfBusMessageType.CanRemoteFrame, Dlc = 16 } };
            yield return new object[] { MdfBusType.Can, new MdfBusMessage { Type = MdfBusMessageType.CanErrorFrame, ErrorType = 8 } };
            yield return new object[] { MdfBusType.Can, new MdfBusMessage { Timestamp = double.NaN } };
            yield return new object[] { MdfBusType.Can, new MdfBusMessage { Type = MdfBusMessageType.LinFrame } };
            yield return new object[] { MdfBusType.Can, new MdfBusMessage { Type = (MdfBusMessageType)99 } };
            yield return new object[] { MdfBusType.Lin, new MdfBusMessage { Type = MdfBusMessageType.LinFrame, Id = 64 } };
            yield return new object[] { MdfBusType.Lin, new MdfBusMessage { Type = MdfBusMessageType.LinFrame, Data = new byte[9] } };
            yield return new object[] { MdfBusType.FlexRay, new MdfBusMessage { Type = MdfBusMessageType.FlexRayFrame, Cycle = 64 } };
            yield return new object[] { MdfBusType.FlexRay, new MdfBusMessage { Type = MdfBusMessageType.FlexRayFrame, Crc = 0x1000000 } };
            yield return new object[] { MdfBusType.FlexRay, new MdfBusMessage { Type = MdfBusMessageType.FlexRayFrame, FlexRayChannel = 2 } };
            yield return new object[] { MdfBusType.Ethernet, new MdfBusMessage { Type = MdfBusMessageType.EthernetFrame, Source = 1UL << 48 } };
            yield return new object[] { MdfBusType.Ethernet, new MdfBusMessage { Type = MdfBusMessageType.EthernetFrame, Data = new byte[1501] } };
        }

        [Fact]
        public void InvalidFramesAreRejectedWithoutWriting()
        {
            int cases = 0;
            foreach (object[] item in InvalidFrames())
            {
                Rejected((MdfBusType)item[0], (MdfBusMessage)item[1]);
                cases++;
            }
            Assert.Equal(18, cases);
        }

        private static void Rejected(MdfBusType bus, MdfBusMessage message)
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfBusLogger logger = MdfBusLogger.Create(output, bus, true))
            {
                logger.StartMeasurement();
                long before = output.Snapshot().Length;
                Assert.ThrowsAny<ArgumentException>(() => logger.Write(message));
                Assert.Equal(MdfWriterState.Recording, logger.State);
                Assert.Equal(0UL, logger.MessageCount);
                Assert.Equal(before, output.Snapshot().Length);
                logger.StopMeasurement();
                logger.FinalizeMeasurement();
            }
        }

        [Fact]
        public void OptionsAndArgumentsAreChecked()
        {
            Assert.Throws<ArgumentNullException>(() => MdfBusLogger.Create((Stream)null, MdfBusType.Can));
            Assert.Throws<ArgumentOutOfRangeException>(() => MdfBusLogger.Create(new MemoryStream(), (MdfBusType)1));
            Assert.Throws<ArgumentOutOfRangeException>(() => MdfBusLogger.Create(new MemoryStream(), MdfBusType.Can, false, new MdfBusLoggerOptions { MaxDataLength = 65 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => MdfBusLogger.Create(new MemoryStream(), MdfBusType.Can, false, new MdfBusLoggerOptions { MaxDataLength = -1 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => MdfBusLogger.Create(new MemoryStream(), MdfBusType.Lin, false, new MdfBusLoggerOptions { MaxDataLength = 4 }));
            Assert.Throws<NotSupportedException>(() => MdfBusLogger.Create(new MemoryStream(), MdfBusType.Can, false, new MdfBusLoggerOptions { Version = "3.30" }));
            Assert.Throws<ArgumentNullException>(() => MdfBusReader.ReadMessages(null));
            using (MdfBusLogger logger = MdfBusLogger.Create(new MemoryStream(), MdfBusType.Ethernet, false, new MdfBusLoggerOptions { MaxDataLength = 9000 }))
            {
                Assert.Equal(9000, logger.MaxDataLength);
                Assert.Equal(MdfBusType.Ethernet, logger.BusType);
                Assert.Throws<InvalidOperationException>(() => logger.Write(new MdfBusMessage { Type = MdfBusMessageType.EthernetFrame }));
                Assert.Throws<ArgumentNullException>(() => logger.Write(null));
            }
            Assert.Equal(64, MdfBusMessage.CanFdLength(15));
            Assert.Equal(8, MdfBusMessage.CanFdLength(8));
            Assert.Equal(12, MdfBusMessage.CanFdLength(9));
            Assert.Throws<ArgumentOutOfRangeException>(() => MdfBusMessage.CanFdLength(16));
        }

        [Fact]
        public void NonBusFilesYieldNoMessages()
        {
            byte[] file = MdfLib.TestSupport.FixtureCorpus.Read("m7a-numeric410");
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file))) { Assert.Empty(MdfBusReader.ReadMessages(reader)); }
        }
    }
}
