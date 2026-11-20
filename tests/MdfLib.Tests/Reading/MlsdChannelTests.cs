// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MdfLib.Internal;
using MdfLib.Reading;
using MdfLib.Formats.Mdf4;
using MdfLib.Tests.TestDoubles;
using MdfLib.TestSupport;
using Xunit;

namespace MdfLib.Tests.Reading
{
    // literal mutations of the frozen stdlib m5p-mlsd410 file, never the library's writer.
    public sealed class MlsdChannelTests
    {
        private const string Sorted = "m5p-mlsd410"; // 0 t (f64 master), 1 len (u8, inval bit 0), 2 frame (MLSD bytes max 8),
                                                    // 3 tlen (u16 BE), 4 text (MLSD UTF-8 max 6, inval bit 1)
        private const int Len = 1, Frame = 2, Tlen = 3, Text = 4;
        private const int RecordBytes = 26; // 25 data + 1 invalidation byte, 6 rows.

        private static void Field(byte[] bytes, int index, int offset, ulong value, int width)
        {
            M3Wire.Put(bytes, M3Wire.Payload(bytes, M5DWire.Cn(bytes, index)) + offset, value, width);
        }

        private static void SizeLink(byte[] bytes, int index, ulong target)
        {
            M3Wire.Put(bytes, M3Wire.Link(M5DWire.Cn(bytes, index), 5), target);
        }

        private static int Row(byte[] bytes, int row) { return M5DWire.Root(bytes) + 24 + row * RecordBytes; }

        private static MdfChannelGroup Group(MdfReader reader) { return reader.ReadMetadata()[0].ChannelGroups[0]; }

        private static List<MdfSampleValue> Column(MdfReader reader, int index, ulong start = 0, ulong count = ulong.MaxValue)
        {
            MdfChannelGroup group = Group(reader);
            return reader.ReadSamples(group, new[] { index }, start, count).SelectMany(b => Enumerable.Range(0, b.SampleCount)
                .Select(r => b.GetValue(r, 0))).ToList();
        }

        private static string Hex(MdfSampleValue value) { return BitConverter.ToString(value.GetByteArrayValue()).Replace("-", "").ToLowerInvariant(); }

        [Fact]
        public void SizedPrefixesInvalidSizesAndOwnValidityFollowTheContract()
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(Sorted), false)))
            {
                MdfChannel frame = Group(reader).Channels[Frame];
                Assert.True(frame.CanReadRaw && frame.CanReadPhysical, frame.UnsupportedReason);
                Assert.Equal(MdfValueKind.ByteArray, frame.ValueKind);
                Assert.Equal(64U, frame.BitCount);
                Assert.False(frame.IsMaster);
                List<MdfSampleValue> bytes = Column(reader, Frame);
                Assert.Equal(new[] { "", "010203", "1122334455667788", "deadbeef00", "", "00" }, bytes.Select(Hex));
                Assert.Equal(new[] { true, true, true, true, false, true }, bytes.Select(v => v.IsValid)); // row 4: invalid size 200
                Assert.All(bytes, v => Assert.Equal(MdfValueKind.ByteArray, v.Kind));
                List<MdfSampleValue> text = Column(reader, Text);
                Assert.Equal(new[] { "\u20ACabZ", "\uFFFD", "abc", "", "\u00FC1", "ABCD" }, text.Select(v => v.TextValue));
                Assert.Equal(new[] { true, true, false, true, true, true }, text.Select(v => v.IsValid)); // own bit on row 2
                Assert.Equal("6162630064", Hex(text[2])); // invalid own row still exposes its N stored bytes
                Assert.Equal(Column(reader, Text, 0, 6).Select(Hex), reader.ReadSamples(Group(reader), new[] { Text }, 0, 6, 1,
                    MdfValueRepresentation.Physical).Select(b => Hex(b.GetValue(0, 0))));
            }
        }

        [Theory]
        [InlineData(Frame, 2, 0UL, 1, MetadataReader.MaxLengthTypeReason)]      // numeric MLSD
        [InlineData(Frame, 2, 4UL, 1, MetadataReader.MaxLengthTypeReason)]      // IEEE MLSD
        [InlineData(Frame, 3, 3UL, 1, "byte-aligned")]                          // bit offset
        [InlineData(Frame, 8, 60UL, 4, "byte-aligned")]                         // not whole bytes
        [InlineData(Frame, 12, 0x2000UL, 4, "flags")]                           // outside the 0x19ff mask
        [InlineData(Text, 2, 8UL, 1, "UTF-16")]                                 // UTF-16LE over a 5-byte field (below)
        public void IneligibleMlsdShapesAreInspectableAndOtherChannelsStillRead(int index, int offset, ulong value, int width, string reason)
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            Field(bytes, index, offset, value, width);
            if (index == Text) { Field(bytes, Text, 8, 40, 4); } // 5 bytes: not whole UTF-16 code units.
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = Group(reader);
                MdfChannel channel = group.Channels[index];
                Assert.False(channel.CanReadRaw);
                Assert.Contains(reason, channel.UnsupportedReason);
                int before = source.ReadCalls;
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { index }));
                Assert.Equal(before, source.ReadCalls);
                Assert.Equal(new byte[] { 0, 3, 8, 5, 200, 1 }, Column(reader, Len).Select(v => (byte)v.UnsignedValue));
            }
        }

        [Fact]
        public void NumericChannelTypeFiveKeepsItsNumericKindAndTheTypeReason()
        {
            byte[] bytes = FixtureCorpus.Read("m5o-virtual410");
            M3Wire.Put(bytes, M3Wire.Payload(bytes, M5DWire.Cn(bytes, 2)), 5, 1); // unsigned virtual data -> cn_type 5
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannel channel = Group(reader).Channels[2];
                Assert.Equal(MetadataReader.MaxLengthTypeReason, channel.UnsupportedReason);
                Assert.Equal(MdfValueKind.UnsignedInteger, channel.ValueKind);
            }
        }

        [Fact]
        public void MissingSizeLinkIsUnsupported()
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            SizeLink(bytes, Frame, 0);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = Group(reader);
                Assert.Equal(MetadataReader.MaxLengthNoSizeReason, group.Channels[Frame].UnsupportedReason);
                Assert.True(group.Channels[Text].CanReadRaw);
            }
        }

        [Theory]
        [InlineData("text-block")]  // a TX block of the same file
        [InlineData("mid-block")]   // aligned and in range, but inside a CN (not a block start)
        [InlineData("cg")]          // the channel group itself
        public void SizeLinksOutsideTheTopLevelChainAreRefusedWithoutBeingFollowed(string target)
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            ulong address = target == "text-block" ? M3Wire.U64(bytes, M3Wire.Link(M5DWire.Cn(bytes, Len), 2))
                : target == "mid-block" ? (ulong)M5DWire.Cn(bytes, Len) + 8 : (ulong)M5DWire.Cg(bytes);
            SizeLink(bytes, Frame, address);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = Group(reader);
                Assert.Equal(MetadataReader.MaxLengthGroupReason, group.Channels[Frame].UnsupportedReason);
                Assert.Equal(MdfValueKind.ByteArray, group.Channels[Frame].ValueKind);
                Assert.True(group.Channels[Text].CanReadRaw, group.Channels[Text].UnsupportedReason);
            }
        }

        [Theory]
        [InlineData(3UL)]       // unaligned
        [InlineData(1UL << 40)] // beyond the source
        public void SizeLinksBreakingTheCommonLinkRuleStayMalformed(ulong address)
        {
            // every nonzero MDF 4 link is in range and 8-byte aligned; cn_data is no exception (MDF007).
            byte[] bytes = FixtureCorpus.Read(Sorted);
            SizeLink(bytes, Frame, address);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                Assert.Equal("MDF007", Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
            }
        }

        [Theory]
        [InlineData(0, -1, 0UL, 0)]           // f64 master
        [InlineData(Text, -1, 0UL, 0)]        // another MLSD (text)
        [InlineData(Frame, -1, 0UL, 0)]       // itself
        [InlineData(Len, 2, 2UL, 1)]          // signed size
        [InlineData(Len, 12, 0x2002UL, 4)]    // unsupported size (unknown flag)
        [InlineData(Len, 0, 6UL, 1)]          // virtual data role (invalidation flag makes it unsupported too)
        public void UnsuitableSizeChannelsAreRefused(int target, int offset, ulong value, int width)
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            if (offset >= 0) { Field(bytes, target, offset, value, width); }
            SizeLink(bytes, Frame, (ulong)M5DWire.Cn(bytes, target));
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannel frame = Group(reader).Channels[Frame];
                Assert.Equal(MetadataReader.MaxLengthKindReason, frame.UnsupportedReason);
                Assert.False(frame.CanReadRaw);
            }
        }

        [Fact]
        public void AnyStoredUnsignedChannelOfTheGroupCanBeTheSize()
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            SizeLink(bytes, Frame, (ulong)M5DWire.Cn(bytes, Tlen)); // big-endian u16 sizes 6, 2, 5, 0, 3, 4; always valid
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                List<MdfSampleValue> values = Column(reader, Frame);
                Assert.Equal(new[] { "a1a2a3a4a5a6", "0102", "1122334455", "", "ffffff", "00a5a5a5" }, values.Select(Hex));
                Assert.All(values, v => Assert.True(v.IsValid));
            }
        }

        [Fact]
        public void SizeAboveTheMaximumIsMalformedOnlyWhenThatRowIsRead()
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            bytes[Row(bytes, 1) + 8] = 9; // len row 1: 9 > 8 bytes
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannel frame = Group(reader).Channels[Frame];
                Assert.True(frame.CanReadRaw, frame.UnsupportedReason); // metadata is unchanged
                MdfFormatException error = Assert.Throws<MdfFormatException>(() => Column(reader, Frame));
                Assert.Equal("MDF013", error.DiagnosticCode);
                Assert.Equal((ulong)M5DWire.Cn(bytes, Len), error.Offset);
                Assert.Equal(new[] { "1122334455667788", "deadbeef00", "", "00" }, Column(reader, Frame, 2).Select(Hex));
                Assert.Equal(new[] { "" }, Column(reader, Frame, 0, 1).Select(Hex));
                Assert.Equal(9UL, Column(reader, Len)[1].UnsignedValue); // the size channel itself still reads
                Assert.Equal(6, Column(reader, Text).Count);
            }
        }

        [Fact]
        public void InvalidSizesAreNeverUsedOrBoundChecked()
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            Field(bytes, Len, 12, 1, 4); // all-invalid size channel; every stored size is ignored
            for (int row = 0; row < 6; row++) { bytes[Row(bytes, row) + 8] = 255; }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                List<MdfSampleValue> values = Column(reader, Frame);
                Assert.All(values, v => { Assert.False(v.IsValid); Assert.Empty(v.GetByteArrayValue()); });
            }
        }

        [Fact]
        public void MlsdArraysStructuresAndConversionsKeepTheirRefusals()
        {
            byte[] array = FixtureCorpus.Read(Sorted);
            M5DWire.Array(ref array, new ulong[] { 2 }); // attaches to channel index 2 = frame
            byte[] structure = FixtureCorpus.Read(Sorted);
            M3Wire.Put(structure, M3Wire.Link(M5DWire.Cn(structure, Frame), 1), (ulong)M5DWire.Cn(structure, Tlen));
            byte[] converted = FixtureCorpus.Read(Sorted);
            byte[] cc = new byte[40];
            cc[0] = 1; // linear
            M3Wire.Put(cc, 6, 2, 2); // '<2B3H4d': type, precision, flags, ref count, value count, min, max, p1 = 0, p2 = 2
            M3Wire.Put(cc, 32, BitConverter.ToUInt64(BitConverter.GetBytes(2.0), 0));
            int block = M5Wire.Add(ref converted, "CC", new ulong[4], cc);
            M3Wire.Put(converted, M3Wire.Link(M5DWire.Cn(converted, Text), 4), (ulong)block);
            foreach (Tuple<byte[], int, string> c in new[]
            {
                Tuple.Create(array, Frame, "Text and byte-array arrays are not supported."),
                Tuple.Create(structure, Frame, "Structure/unknown channel composition is not supported."),
                Tuple.Create(converted, Text, "Text and byte-array channels support only identity conversion.")
            })
            using (MdfReader reader = MdfReader.Open(new MemoryStream(c.Item1, false)))
            {
                Assert.Equal(c.Item3, Group(reader).Channels[c.Item2].UnsupportedReason);
            }
        }

        [Fact]
        public void FieldOutsideTheRecordIsMalformedMetadata()
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            Field(bytes, Frame, 4, 20, 4); // 8 bytes at 20 > 25 data bytes
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                Assert.Equal("MDF013", Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
            }
        }

        [Fact]
        public void ArraySizeTargetsAreRefusedAndMlsdStructureComponentsAreLeaves()
        {
            byte[] array = FixtureCorpus.Read(Sorted);
            int ca = M5DWire.Array(ref array, new ulong[] { 2 }); // attaches to index 2; move it to the u8 size channel
            M3Wire.Put(array, M3Wire.Link(M5DWire.Cn(array, Frame), 1), 0);
            M3Wire.Put(array, M3Wire.Link(M5DWire.Cn(array, Len), 1), (ulong)ca);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(array, false)))
            {
                MdfChannelGroup group = Group(reader);
                Assert.True(group.Channels[Len].CanReadRaw, group.Channels[Len].UnsupportedReason); // a readable u8[2] array
                Assert.Equal(MetadataReader.MaxLengthKindReason, group.Channels[Frame].UnsupportedReason);
            }
            byte[] component = FixtureCorpus.Read(Sorted);
            int parent = M5DWire.Cn(component, Text); // becomes a fixed byte-array structure parent over its 6 bytes
            Field(component, Text, 0, 0, 1);
            Field(component, Text, 2, 10, 1);
            Field(component, Text, 12, 0, 4); // no invalidation flags on the parent: they would block its components
            M3Wire.Put(component, M3Wire.Link(parent, 5), 0);
            int child = M5EWire.AddCn(ref component, 0, 0, 10, 19, 16, 5);
            M3Wire.Put(component, M3Wire.Link(child, 5), (ulong)M5DWire.Cn(component, Len));
            M3Wire.Put(component, M3Wire.Link(M5DWire.Cn(component, Text), 1), (ulong)child);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(component, false)))
            {
                MdfChannelGroup group = Group(reader);
                Assert.Equal(6, group.Channels.Count);
                // an MLSD component (ASAM bus logging DataBytes) resolves its same-CG size channel like a top-level one.
                Assert.True(group.Channels[5].CanReadRaw, group.Channels[5].UnsupportedReason);
                Assert.Same(group.Channels[Text], group.Channels[5].Parent);
                Assert.True(group.Channels[Frame].CanReadRaw, group.Channels[Frame].UnsupportedReason);
            }
        }

        [Fact]
        public void InvalidationBitOutsideTheRecordIsMalformedMetadata()
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            Field(bytes, Text, 16, 8, 4); // bit 8 of a 1-byte invalidation area
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                Assert.Equal("MDF013", Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
            }
        }

        [Fact]
        public void MetadataReadsNoRecordBytesAndAnMlsdOnlySelectionReadsTheSizeFromTheSameRecord()
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            int root = M5DWire.Root(bytes);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = Group(reader);
                Assert.DoesNotContain(source.ReadRanges, r => r.Item1 >= root + 24); // Size resolution is by address only.
                Assert.Equal(new[] { "", "010203", "1122334455667788", "deadbeef00", "", "00" }, Column(reader, Frame).Select(Hex));
                Assert.Contains(source.ReadRanges, r => r.Item1 >= root + 24 && r.Item1 < root + 24 + 6 * RecordBytes);
            }
        }

        [Fact]
        public void BatchCostUsesTheMaximumLength()
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            // A selection costs 40 per value + 64 + 3 * FieldBytes per MLSD value per row, like a fixed text field.
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = Group(reader);
                SampleRequest request = SampleRequest.Create(new ReaderLimits(null), group, new[] { Frame, Text }, 0, 6, 1024,
                    MdfValueRepresentation.Raw, CancellationToken.None);
                Assert.Equal(2UL * 40 + (64 + 3 * 8) + (64 + 3 * 6), request.RowBytes);
            }
        }
    }
}
