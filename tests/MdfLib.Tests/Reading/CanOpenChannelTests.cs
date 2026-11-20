// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MdfLib.Formats.Mdf4;
using MdfLib.Tests.TestDoubles;
using MdfLib.TestSupport;
using Xunit;

namespace MdfLib.Tests.Reading
{
    // CANopen date (13) / time (14) are fixed byte arrays of exactly 7/6 bytes,
    // published as their wire bytes. Literal mutations of the frozen stdlib m5z files, never the library's writer.
    public sealed class CanOpenChannelTests
    {
        private const string Sorted = "m5z-canopen410"; // 0 t, 1 date @8, 2 time @15, 3 date_inv @21 (bit 0), 4 n @28; 30 + 1 bytes
        private const int Date = 1, Time = 2, DateInv = 3, Counter = 4, RecordBytes = 31;
        private static readonly string[] DateBytes = { "00000000c10100", "5fea3b97ff0c63", "2a76058e300b1a", "d2041e09af0654", "f40101129d0218" };
        private static readonly string[] TimeBytes = { "000000000000", "ff5b2605ffff", "952cb302bf3b", "010000000100", "002e93021027" };
        private const string VirtualReason = "Virtual channels require an unsigned integer data type.";
        private const string SyncReason = "Synchronization channels require a numeric data type.";

        private static int Field(byte[] bytes, int index) { return M3Wire.Payload(bytes, M5DWire.Cn(bytes, index)); }
        private static int Record(byte[] bytes, int row) { return M3Wire.Payload(bytes, M5DWire.Root(bytes)) + row * RecordBytes; }
        private static MdfChannelGroup Group(MdfReader reader) { return reader.ReadMetadata()[0].ChannelGroups[0]; }
        private static string Hex(MdfSampleValue value) { return BitConverter.ToString(value.GetByteArrayValue()).Replace("-", "").ToLowerInvariant(); }
        private static MdfReader Open(byte[] bytes) { return MdfReader.Open(new MemoryStream(bytes, false)); }

        private static List<MdfSampleValue> Column(MdfReader reader, int index, MdfValueRepresentation representation = MdfValueRepresentation.Raw)
        {
            return reader.ReadSamples(Group(reader), new[] { index }, representation: representation)
                .SelectMany(b => Enumerable.Range(0, b.SampleCount).Select(r => b.GetValue(r, 0))).ToList();
        }

        [Fact]
        public void DateAndTimeAreTheirExactWireBytesInBothRepresentations()
        {
            using (MdfReader reader = Open(FixtureCorpus.Read(Sorted)))
            {
                foreach (int index in new[] { Date, Time, DateInv })
                {
                    MdfChannel channel = Group(reader).Channels[index];
                    Assert.Equal(MdfValueKind.ByteArray, channel.ValueKind);
                    Assert.Null(channel.UnsupportedReason);
                    Assert.Equal(MdfConversionKind.Identity, channel.Conversion.Kind);
                    Assert.Null(channel.Array);
                }
                Assert.Equal(56U, Group(reader).Channels[Date].BitCount);
                Assert.Equal(48U, Group(reader).Channels[Time].BitCount);
                foreach (MdfValueRepresentation representation in new[] { MdfValueRepresentation.Raw, MdfValueRepresentation.Physical })
                {
                    Assert.Equal(DateBytes, Column(reader, Date, representation).Select(Hex));
                    Assert.Equal(TimeBytes, Column(reader, Time, representation).Select(Hex));
                    List<MdfSampleValue> inv = Column(reader, DateInv, representation);
                    Assert.Equal(new[] { 2, 3, 4, 0, 1 }.Select(i => DateBytes[i]), inv.Select(Hex)); // Invalid rows keep their bytes.
                    Assert.Equal(new[] { true, false, true, false, true }, inv.Select(v => v.IsValid));
                }
                MdfSampleValue first = Column(reader, Date)[0];
                Assert.Throws<InvalidOperationException>(() => first.TextValue);
                Assert.Throws<InvalidOperationException>(() => first.UnsignedValue);
            }
        }

        [Fact]
        public void ReservedBitsAreNeitherMaskedNorValidated()
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            int record = Record(bytes, 0);
            bytes[record + 8 + 2] |= 0xC0; // minute bits 6-7
            bytes[record + 8 + 3] |= 0x7F; // hour 31 with reserved bits 5-6
            bytes[record + 8 + 5] |= 0xC0; // month bits 6-7
            bytes[record + 8 + 6] |= 0x80; // year bit 7
            bytes[record + 15 + 3] |= 0xF0; // time ms bits 28-31
            using (MdfReader reader = Open(bytes))
            {
                Assert.Equal("0000c07fc1c180", Hex(Column(reader, Date)[0]));
                Assert.Equal("000000f00000", Hex(Column(reader, Time)[0]));
                Assert.Equal(DateBytes.Skip(1), Column(reader, Date).Skip(1).Select(Hex));
            }
        }

        [Theory]
        [InlineData(Date, 0, 0, 48U)]   // date needs 56 bits
        [InlineData(Date, 0, 0, 64U)]
        [InlineData(Time, 0, 0, 56U)]   // time needs 48 bits
        [InlineData(Time, 0, 1, 48U)]   // bit offset
        [InlineData(Date, 2, 0, 56U)]   // stored master
        [InlineData(Time, 7, 0, 48U)]   // unknown role
        [InlineData(DateInv, 0, 0, 8U)]
        public void OtherRolesWidthsAndOffsetsAreRefusedWithTheM5ZReason(int index, int cnType, int bitOffset, uint bits)
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            int field = Field(bytes, index);
            bytes[field] = (byte)cnType;
            bytes[field + 3] = (byte)bitOffset;
            M3Wire.Put(bytes, field + 8, bits, 4);
            using (MdfReader reader = Open(bytes))
            {
                MdfChannel channel = Group(reader).Channels[index];
                Assert.Equal(MdfValueKind.ByteArray, channel.ValueKind);
                Assert.Equal(MetadataReader.CanOpenReason, channel.UnsupportedReason);
                Assert.Throws<NotSupportedException>(() => Column(reader, index));
                Assert.Equal(5, Column(reader, Counter).Count);
                Assert.Equal(TimeBytes.Length, Column(reader, index == Time ? Date : Time).Count);
            }
        }

        [Theory]
        [InlineData(6, VirtualReason)]
        [InlineData(4, SyncReason)]
        public void VirtualAndSynchronizationRolesKeepTheirReasons(int cnType, string reason)
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            bytes[Field(bytes, Date)] = (byte)cnType;
            using (MdfReader reader = Open(bytes))
            {
                Assert.Equal(reason, Group(reader).Channels[Date].UnsupportedReason);
                Assert.Equal(TimeBytes, Column(reader, Time).Select(Hex));
            }
        }

        [Fact]
        public void ChannelDataLinkArrayAndConversionKeepTheByteArrayReasons()
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            int dateCn = M5DWire.Cn(bytes, Date);
            M3Wire.Put(bytes, M3Wire.Link(dateCn, 5), M3Wire.U64(bytes, M3Wire.Link(dateCn, 2))); // cn_data -> its own name TX
            byte[] cc = new byte[40];
            cc[0] = 1;
            M3Wire.Put(cc, 6, 2, 2);
            M3Wire.Put(cc, 32, (ulong)BitConverter.DoubleToInt64Bits(2.0));
            int conversion = M5Wire.Add(ref bytes, "CC", new ulong[4], cc);
            M3Wire.Put(bytes, M3Wire.Link(M5DWire.Cn(bytes, DateInv), 4), (ulong)conversion);
            M5DWire.Array(ref bytes, new ulong[] { 2 }, 6); // on channel 2 (time)
            using (MdfReader reader = Open(bytes))
            {
                MdfChannelGroup group = Group(reader);
                Assert.Equal("Channel-specific data links are not supported.", group.Channels[Date].UnsupportedReason);
                Assert.Null(group.Channels[Time].UnsupportedReason); // a fixed CANopen time[2] array (stride 6).
                Assert.Equal(2UL, group.Channels[Time].Array.ElementCount);
                Assert.Equal(MdfCalendarKind.CanOpenTime, group.Channels[Time].CalendarKind);
                Assert.Equal("Text and byte-array channels support only identity conversion.", group.Channels[DateInv].UnsupportedReason);
                Assert.Equal(MdfConversionKind.Linear, group.Channels[DateInv].Conversion.Kind);
                Assert.Equal(5, Column(reader, Counter).Count);
                MdfSampleBatch batch = reader.ReadSamples(group, new[] { Time }).Single();
                Assert.Equal(TimeBytes, Enumerable.Range(0, 5).Select(r => Hex(batch.GetValue(r, 0, 0))));
                Assert.Equal(new[] { 2, 3, 4, 0, 1 }.Select(i => DateBytes[i].Substring(0, 12)), Enumerable.Range(0, 5).Select(r => Hex(batch.GetValue(r, 0, 1))));
            }
        }

        [Fact]
        public void FieldBeyondTheRecordIsMalformed()
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            M3Wire.Put(bytes, Field(bytes, Date) + 4, 24, 4); // 24 + 7 > 30 data bytes
            using (MdfReader reader = Open(bytes))
            {
                Assert.Equal("MDF013", Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
            }
        }

        [Fact]
        public void InvalidationBitOutsideTheAreaIsMalformedEvenForARefusedWidth()
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            int field = Field(bytes, DateInv);
            M3Wire.Put(bytes, field + 8, 64, 4);
            M3Wire.Put(bytes, field + 16, 8, 4); // one invalidation byte: bits 0..7
            using (MdfReader reader = Open(bytes))
            {
                Assert.Equal("MDF013", Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
            }
        }

        [Fact]
        public void Mdf400ReadsTheSameBytes()
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            bytes[10] = (byte)'0';
            M3Wire.Put(bytes, 28, 400, 2);
            using (MdfReader reader = Open(bytes))
            {
                Assert.Equal("4.00", reader.Header.Version);
                Assert.Equal(DateBytes, Column(reader, Date).Select(Hex));
                Assert.Equal(TimeBytes, Column(reader, Time).Select(Hex));
            }
        }
    }
}
