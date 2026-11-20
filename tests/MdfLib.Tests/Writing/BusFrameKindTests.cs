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
    public sealed class BusFrameKindTests
    {
        private static readonly MdfBusType[] Buses = { MdfBusType.Can, MdfBusType.Lin, MdfBusType.Most, MdfBusType.FlexRay, MdfBusType.Ethernet };

        private static ulong Pattern(int bits, ulong seed)
        {
            ulong mask = bits >= 64 ? ulong.MaxValue : (1UL << bits) - 1;
            ulong value = (seed * 0x9E3779B97F4A7C15UL) & mask;
            return value == 0 ? 1 : value;
        }

        private static List<MdfBusMessageType> KindsOf(MdfBusType bus)
        {
            return BusFrameLayout.All.Where(l => l.Bus == bus).Select(l => l.Type).ToList();
        }

        // Fills every member of the frame layout: typed properties within range and all other members through Fields.
        private static MdfBusMessage Fill(BusFrameLayout layout, int seed)
        {
            MdfBusMessage m = new MdfBusMessage { Type = layout.Type, Timestamp = 0.25 * seed };
            bool can = layout.Bus == MdfBusType.Can;
            foreach (BusFieldLayout f in layout.Fields)
            {
                ulong v = Pattern(f.Bits, (ulong)(seed * 31 + f.Byte * 8 + f.Bit + 1));
                switch (f.Field)
                {
                    case BusField.BusChannel: m.BusChannel = (byte)v; break;
                    case BusField.Id: m.Id = (uint)(can ? v & 0x1FFFFFFF : v); break;
                    case BusField.Ide: m.IsExtendedId = true; break;
                    case BusField.Dir: m.Direction = MdfBusDirection.Tx; break;
                    case BusField.Edl: m.IsFd = true; break;
                    case BusField.Brs: m.BitRateSwitch = true; break;
                    case BusField.Esi: m.ErrorStateIndicator = true; break;
                    case BusField.ErrorType: m.ErrorType = (byte)v; break;
                    case BusField.BitPosition: m.BitPosition = (ushort)v; break;
                    case BusField.FrameDuration: m.FrameDuration = (uint)v; break;
                    case BusField.Crc: m.Crc = (uint)v; break;
                    case BusField.Checksum: m.Checksum = (byte)v; break;
                    case BusField.Source: m.Source = v; break;
                    case BusField.Destination: m.Destination = v; break;
                    case BusField.EtherType: m.EtherType = (ushort)v; break;
                    case BusField.Cycle: m.Cycle = (byte)v; break;
                    case BusField.FlxChannel: m.FlexRayChannel = 1; break;
                    case BusField.Dlc: if (layout.DataOffset < 0) { m.Dlc = 5; } break;
                    case BusField.ReceivedCount: if (f != layout.SizeMember) { m.Fields[f.Suffix] = MdfSampleValue.FromUnsigned(v); } break;
                    case BusField.Extra:
                        m.Fields[f.Suffix] = f.Value == BusValue.Signed ? MdfSampleValue.FromSigned(-1)
                            : f.Value == BusValue.Float ? MdfSampleValue.FromFloatingPoint(19200.0 + seed)
                            : MdfSampleValue.FromUnsigned(v);
                        break;
                }
            }
            if (layout.DataOffset >= 0)
            {
                int length = layout.Bus == MdfBusType.Lin ? 5 : m.IsFd ? 12 : can ? 7 : 9;
                m.Data = Enumerable.Range(0, length).Select(i => (byte)(seed + 17 * i)).ToArray();
            }
            return m;
        }

        private static void SameFields(MdfBusMessage expected, MdfBusMessage actual)
        {
            foreach (KeyValuePair<string, MdfSampleValue> pair in expected.Fields)
            {
                Assert.True(actual.Fields.ContainsKey(pair.Key), expected.Type + "." + pair.Key);
                MdfSampleValue got = actual.Fields[pair.Key];
                Assert.True(got.IsValid);
                switch (pair.Value.Kind)
                {
                    case MdfValueKind.SignedInteger: Assert.Equal(pair.Value.SignedValue, got.SignedValue); break;
                    case MdfValueKind.FloatingPoint: Assert.Equal(pair.Value.FloatingPointValue, got.FloatingPointValue); break;
                    default: Assert.Equal(pair.Value.UnsignedValue, got.UnsignedValue); break;
                }
            }
        }

        private static void Same(MdfBusMessage e, MdfBusMessage a)
        {
            Assert.Equal(e.Type, a.Type);
            Assert.Equal(e.Timestamp, a.Timestamp);
            Assert.Equal(e.BusChannel, a.BusChannel);
            Assert.Equal(e.Id, a.Id);
            Assert.Equal(e.IsExtendedId, a.IsExtendedId);
            Assert.Equal(e.Direction, a.Direction);
            Assert.Equal(e.Data ?? new byte[0], a.Data ?? new byte[0]);
            Assert.Equal(e.IsFd, a.IsFd);
            Assert.Equal(e.BitRateSwitch, a.BitRateSwitch);
            Assert.Equal(e.ErrorStateIndicator, a.ErrorStateIndicator);
            Assert.Equal(e.ErrorType, a.ErrorType);
            Assert.Equal(e.BitPosition, a.BitPosition);
            Assert.Equal(e.Crc, a.Crc);
            Assert.Equal(e.FrameDuration, a.FrameDuration);
            Assert.Equal(e.Checksum, a.Checksum);
            Assert.Equal(e.Source, a.Source);
            Assert.Equal(e.Destination, a.Destination);
            Assert.Equal(e.EtherType, a.EtherType);
            Assert.Equal(e.Cycle, a.Cycle);
            Assert.Equal(e.FlexRayChannel, a.FlexRayChannel);
            if (e.Type == MdfBusMessageType.CanRemoteFrame) { Assert.Equal(e.Dlc, a.Dlc); }
            SameFields(e, a);
        }

        [Fact]
        public void EveryFrameKindOfEveryBusRoundTripsWithAllMembers()
        {
            int kinds = 0;
            foreach (MdfBusType bus in Buses)
            {
                List<MdfBusMessageType> types = KindsOf(bus);
                List<MdfBusMessage> written = new List<MdfBusMessage>();
                TestWriteStream output = new TestWriteStream();
                MdfBusLoggerOptions options = new MdfBusLoggerOptions { MessageTypes = types, MaxDataLength = bus == MdfBusType.Lin ? 0 : 64, Version = "4.20" };
                using (MdfBusLogger logger = MdfBusLogger.Create(output, bus, true, options))
                {
                    logger.StartMeasurement();
                    foreach (MdfBusMessageType type in types)
                    {
                        for (int seed = 1; seed <= 2; seed++)
                        {
                            MdfBusMessage message = Fill(BusFrameLayout.All[(int)type], seed);
                            logger.Write(message);
                            written.Add(message);
                        }
                    }
                    logger.StopMeasurement();
                    logger.FinalizeMeasurement();
                }
                using (MdfReader reader = MdfReader.Open(new MemoryStream(output.Snapshot())))
                {
                    IReadOnlyList<MdfDataGroup> groups = reader.ReadMetadata();
                    Assert.Equal(types.Select(t => BusFrameLayout.All[(int)t].Name).ToArray(), groups.Select(g => g.ChannelGroups.Single().Name).ToArray());
                    Assert.All(groups, g => Assert.Equal((byte)bus, g.ChannelGroups[0].Source.BusType));
                    Assert.All(groups.SelectMany(g => g.ChannelGroups[0].Channels), c => Assert.True(c.CanReadRaw, c.Name + ": " + c.UnsupportedReason));
                    List<MdfBusMessage> read = MdfBusReader.ReadMessages(reader).ToList();
                    Assert.Equal(written.Count, read.Count);
                    for (int i = 0; i < written.Count; i++) { Same(written[i], read[i]); }
                    Assert.True(MdfValidator.Validate(reader).IsValid);
                }
                kinds += types.Count;
            }
            Assert.Equal(Enum.GetValues(typeof(MdfBusMessageType)).Length, kinds);
        }

        [Fact]
        public void MostDefaultsToMessagesPacketsAndEthernetPackets()
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfBusLogger logger = MdfBusLogger.Create(output, MdfBusType.Most, true))
            {
                Assert.Equal(1524, logger.MaxDataLength);
                logger.StartMeasurement();
                MdfBusMessage message = new MdfBusMessage { Type = MdfBusMessageType.MostMessage, Timestamp = 1, Source = 0x0101, Destination = 0xFFFF, Data = new byte[] { 1, 2, 3 } };
                message.Fields["Speed"] = MdfSampleValue.FromUnsigned(2);
                message.Fields["SpecifiedDataByteCount"] = MdfSampleValue.FromUnsigned(3);
                logger.Write(message);
                Assert.Throws<ArgumentException>(() => logger.Write(new MdfBusMessage { Type = MdfBusMessageType.MostShutdownFlag }));
                logger.StopMeasurement();
                logger.FinalizeMeasurement();
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(output.Snapshot())))
            {
                Assert.Equal(new[] { "MOST_Message", "MOST_Packet", "MOST_EthernetPacket" }, reader.ReadMetadata().Select(g => g.ChannelGroups.Single().Name).ToArray());
                Assert.Equal("MOST", reader.ReadMetadata()[0].ChannelGroups[0].Source.Name);
                MdfBusMessage read = MdfBusReader.ReadMessages(reader).Single();
                Assert.Equal(0x0101UL, read.Source);
                Assert.Equal(0xFFFFUL, read.Destination);
                Assert.Equal(2UL, read.Fields["Speed"].UnsignedValue);
                Assert.Equal(3UL, read.Fields["ReceivedDataByteCount"].UnsignedValue);
                Assert.Equal(new byte[] { 1, 2, 3 }, read.Data);
            }
        }

        [Fact]
        public void EthernetMacAddressesAreStoredMostSignificantByteFirst()
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfBusLogger logger = MdfBusLogger.Create(output, MdfBusType.Ethernet, true, new MdfBusLoggerOptions { MaxDataLength = 8 }))
            {
                logger.StartMeasurement();
                logger.Write(new MdfBusMessage { Type = MdfBusMessageType.EthernetFrame, Source = 0x0011223344FFUL, Destination = 0xA1B2C3D4E5F6UL });
                logger.StopMeasurement();
                logger.FinalizeMeasurement();
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(output.Snapshot())))
            {
                MdfChannelGroup group = reader.ReadMetadata().Single().ChannelGroups.Single();
                MdfChannel source = group.Channels.Single(c => c.Name == "ETH_Frame.Source");
                MdfChannel destination = group.Channels.Single(c => c.Name == "ETH_Frame.Destination");
                Assert.Equal(MdfValueKind.ByteArray, source.ValueKind);
                foreach (MdfSampleBatch batch in reader.ReadSamples(group, new[] { source.Index, destination.Index }))
                {
                    Assert.Equal(new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0xFF }, batch.GetValue(0, 0).GetByteArrayValue());
                    Assert.Equal(new byte[] { 0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0xF6 }, batch.GetValue(0, 1).GetByteArrayValue());
                }
            }
        }

        [Fact]
        public void LinBaudRateIsFloat32AndChecksumModelIsSigned()
        {
            TestWriteStream output = new TestWriteStream();
            MdfBusMessage frame = new MdfBusMessage { Type = MdfBusMessageType.LinFrame, Id = 1 };
            frame.Fields["Baudrate"] = MdfSampleValue.FromFloatingPoint(19200);
            frame.Fields["ChecksumModel"] = MdfSampleValue.FromSigned(-1);
            frame.Fields["SOF"] = MdfSampleValue.FromUnsigned(ulong.MaxValue);
            using (MdfBusLogger logger = MdfBusLogger.Create(output, MdfBusType.Lin, true))
            {
                logger.StartMeasurement();
                logger.Write(frame);
                logger.StopMeasurement();
                logger.FinalizeMeasurement();
            }
            byte[] file = output.Snapshot();
            byte[] record = file.Skip(file.Length - 48).Take(45).ToArray();
            Assert.Equal(19200f, BitConverter.ToSingle(record, 29));
            Assert.Equal(3, record[20] & 3);
            Assert.Equal(ulong.MaxValue, BitConverter.ToUInt64(record, 21));
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file)))
            {
                MdfBusMessage read = MdfBusReader.ReadMessages(reader).Single();
                Assert.Equal(MdfValueKind.FloatingPoint, read.Fields["Baudrate"].Kind);
                Assert.Equal(-1L, read.Fields["ChecksumModel"].SignedValue);
                Assert.Equal(ulong.MaxValue, read.Fields["SOF"].UnsignedValue);
                Assert.Equal(0.0, read.Fields["ResponseBaudrate"].FloatingPointValue);
            }
        }

        private static void Refused(MdfBusType bus, MdfBusMessage message, MdfBusLoggerOptions options = null)
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfBusLogger logger = MdfBusLogger.Create(output, bus, true, options))
            {
                logger.StartMeasurement();
                long before = output.Snapshot().Length;
                Assert.ThrowsAny<ArgumentException>(() => logger.Write(message));
                Assert.Equal(0UL, logger.MessageCount);
                Assert.Equal(before, output.Snapshot().Length);
            }
        }

        private static MdfBusMessage With(MdfBusMessageType type, string key, MdfSampleValue value)
        {
            MdfBusMessage message = new MdfBusMessage { Type = type };
            message.Fields[key] = value;
            return message;
        }

        [Fact]
        public void InvalidMembersAndKindsAreRefused()
        {
            Refused(MdfBusType.Can, With(MdfBusMessageType.CanDataFrame, "Unknown", MdfSampleValue.FromUnsigned(1)));
            Refused(MdfBusType.Can, With(MdfBusMessageType.CanDataFrame, "DataLength", MdfSampleValue.FromUnsigned(1)));
            Refused(MdfBusType.Can, With(MdfBusMessageType.CanDataFrame, "DataBytes", MdfSampleValue.FromUnsigned(1)));
            Refused(MdfBusType.Can, With(MdfBusMessageType.CanDataFrame, "SRR", MdfSampleValue.FromUnsigned(2)));
            Refused(MdfBusType.Can, With(MdfBusMessageType.CanDataFrame, "SRR", MdfSampleValue.FromSigned(-1)));
            Refused(MdfBusType.Can, With(MdfBusMessageType.CanDataFrame, "SRR", MdfSampleValue.FromFloatingPoint(1)));
            Refused(MdfBusType.Can, With(MdfBusMessageType.CanDataFrame, "SRR", MdfSampleValue.FromUnsigned(1, false)));
            Refused(MdfBusType.Lin, With(MdfBusMessageType.LinFrame, "ChecksumModel", MdfSampleValue.FromSigned(2)));
            Refused(MdfBusType.Lin, With(MdfBusMessageType.LinFrame, "ChecksumModel", MdfSampleValue.FromSigned(-3)));
            Refused(MdfBusType.Lin, With(MdfBusMessageType.LinFrame, "Baudrate", MdfSampleValue.FromFloatingPoint(1e300)));
            Refused(MdfBusType.Lin, With(MdfBusMessageType.LinFrame, "Baudrate", MdfSampleValue.FromByteArray(new byte[] { 1 })));
            Refused(MdfBusType.Ethernet, With(MdfBusMessageType.EthernetFrame, "Source", MdfSampleValue.FromByteArray(new byte[5])));
            Refused(MdfBusType.Ethernet, new MdfBusMessage { Type = MdfBusMessageType.EthernetReceiveError, ErrorType = 2 },
                new MdfBusLoggerOptions { MessageTypes = new[] { MdfBusMessageType.EthernetReceiveError } });
            Refused(MdfBusType.Can, new MdfBusMessage { Type = MdfBusMessageType.CanOverloadFrame, Data = new byte[1] },
                new MdfBusLoggerOptions { MessageTypes = new[] { MdfBusMessageType.CanOverloadFrame } });
            Refused(MdfBusType.Can, new MdfBusMessage { Type = MdfBusMessageType.CanOverloadFrame });
            Refused(MdfBusType.Lin, new MdfBusMessage { Type = MdfBusMessageType.LinReceiveError, Id = 64 },
                new MdfBusLoggerOptions { MessageTypes = new[] { MdfBusMessageType.LinReceiveError } });
            Refused(MdfBusType.Most, new MdfBusMessage { Type = MdfBusMessageType.MostMessage, Source = 0x10000 });
            Refused(MdfBusType.Most, With(MdfBusMessageType.MostAllocationTable, "TableLength", MdfSampleValue.FromUnsigned(1)),
                new MdfBusLoggerOptions { MessageTypes = new[] { MdfBusMessageType.MostAllocationTable } });
            Refused(MdfBusType.FlexRay, new MdfBusMessage { Type = MdfBusMessageType.FlexRaySymbol, Data = new byte[1] },
                new MdfBusLoggerOptions { MessageTypes = new[] { MdfBusMessageType.FlexRaySymbol } });

            Assert.Throws<ArgumentException>(() => MdfBusLogger.Create(new MemoryStream(), MdfBusType.Can, false,
                new MdfBusLoggerOptions { MessageTypes = new[] { MdfBusMessageType.LinFrame } }));
            Assert.Throws<ArgumentException>(() => MdfBusLogger.Create(new MemoryStream(), MdfBusType.Can, false,
                new MdfBusLoggerOptions { MessageTypes = new MdfBusMessageType[0] }));
            Assert.Throws<ArgumentException>(() => MdfBusLogger.Create(new MemoryStream(), MdfBusType.Can, false,
                new MdfBusLoggerOptions { MessageTypes = new[] { (MdfBusMessageType)999 } }));
            Assert.Throws<ArgumentOutOfRangeException>(() => MdfBusLogger.Create(new MemoryStream(), MdfBusType.Most, false,
                new MdfBusLoggerOptions { MaxDataLength = 65536 }));
        }

        [Fact]
        public void FieldsOverrideDerivedMembersAndAcceptSignedNonNegativeIntegers()
        {
            TestWriteStream output = new TestWriteStream();
            MdfBusMessage frame = new MdfBusMessage { Type = MdfBusMessageType.EthernetFrame, Data = new byte[] { 1, 2 } };
            frame.Fields["ReceivedDataByteCount"] = MdfSampleValue.FromSigned(60);
            frame.Fields["PadByteCount"] = MdfSampleValue.FromUnsigned(44);
            frame.Fields["Source"] = MdfSampleValue.FromByteArray(new byte[] { 2, 0, 0, 0, 0, 1 });
            using (MdfBusLogger logger = MdfBusLogger.Create(output, MdfBusType.Ethernet, true))
            {
                logger.StartMeasurement();
                logger.Write(frame);
                logger.StopMeasurement();
                logger.FinalizeMeasurement();
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(output.Snapshot())))
            {
                MdfBusMessage read = MdfBusReader.ReadMessages(reader).Single();
                Assert.Equal(60UL, read.Fields["ReceivedDataByteCount"].UnsignedValue);
                Assert.Equal(44UL, read.Fields["PadByteCount"].UnsignedValue);
                Assert.Equal(0x020000000001UL, read.Source);
                Assert.Equal(new byte[] { 1, 2 }, read.Data);
            }
        }
    }
}
