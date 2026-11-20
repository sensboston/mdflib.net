// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Linq;
using MdfLib.Tests.TestDoubles;
using MdfLib.TestSupport;
using Xunit;

namespace MdfLib.Tests.Reading
{
    // MDF 3.30 CD arrays over padded / bit-packed element CNs, by literal mutations of the frozen m5v-packed-le330.
    public sealed class Mdf3PackedArrayElementTests
    {
        // Block addresses recorded in tools/fixtures/m5v-expectations.json (m5v-packed-le330).
        private const int CnU12 = 558;
        private const int CnU12Element0 = 844;
        private const int CnStride = 228; // Every CN block is 228 bytes and the u12 elements follow each other.
        private const int CnU8oElement1 = 7054;
        private const int CnF32Element1 = 8718;
        private const int Data = 9954;
        private const int RecordBytes = 81;
        private const int Rows = 5;
        private const int U12 = 1;
        private const int U12Hi = 6;
        private const int I5 = 13;
        private const int U8o = 26;
        private const int U16Pad = 30;
        private const int Counter = 39;
        private const string Element = "Channel dependency elements must be supported numeric scalars of the parent's encoding, bit count, bit offset and conversion.";
        private const string Layout = "Channel dependency elements must be evenly spaced in reference order at a positive byte stride.";

        private static byte[] Fixture() { return FixtureCorpus.Read("m5v-packed-le330"); }

        private static MdfChannelGroup Group(MdfReader reader) { return reader.ReadMetadata()[0].ChannelGroups[0]; }

        private static int U12Cn(int element) { return CnU12Element0 + CnStride * element; }

        private static ulong Field(byte[] bytes, int offset, int bitOffset, int bits, bool bigEndian)
        {
            int span = (bitOffset + bits + 7) / 8;
            ulong container = 0;
            for (int i = 0; i < span; i++)
            {
                container |= (ulong)bytes[offset + (bigEndian ? span - 1 - i : i)] << (8 * i);
            }
            return (container >> bitOffset) & ((1UL << bits) - 1);
        }

        [Theory]
        [InlineData(0, false)]
        [InlineData(1, false)]
        [InlineData(2, false)]
        [InlineData(3, false)]
        [InlineData(4, false)]
        [InlineData(0, true)]
        [InlineData(1, true)]
        [InlineData(2, true)]
        [InlineData(3, true)]
        [InlineData(4, true)]
        public void ElementsAtEveryCommonBitOffsetAreTheirReferencedScalars(int bitOffset, bool bigEndian)
        {
            byte[] bytes = Fixture();
            for (int element = 0; element < 4; element++) { M3Wire.Put(bytes, U12Cn(element) + 186, (ulong)((8 + 2 * element) * 8 + bitOffset), 2); }
            if (bigEndian) { M3Wire.Put(bytes, 24, 1, 2); } // Default byte order of every encoding 0..3.
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = Group(reader);
                Assert.True(group.Channels[U12].CanReadRaw, group.Channels[U12].UnsupportedReason);
                MdfSampleBatch batch = reader.ReadSamples(group, new[] { U12, 2, 3, 4, 5 }).Single();
                for (int row = 0; row < Rows; row++)
                for (int element = 0; element < 4; element++)
                {
                    ulong wanted = Field(bytes, Data + row * RecordBytes + 8 + 2 * element, bitOffset, 12, bigEndian);
                    Assert.Equal(wanted, batch.GetValue(row, 0, (ulong)element).UnsignedValue);
                    Assert.Equal(wanted, batch.GetValue(row, 1 + element).UnsignedValue);
                }
            }
        }

        [Fact]
        public void FillerAndPaddingBitsNeverReachElementValues()
        {
            byte[] original = Fixture();
            byte[] noisy = (byte[])original.Clone();
            for (int row = 0; row < Rows; row++)
            {
                int record = Data + row * RecordBytes;
                for (int element = 0; element < 4; element++) { noisy[record + 9 + 2 * element] ^= 0xF0; }  // u12: bits 12..15
                for (int element = 0; element < 6; element++) { noisy[record + 16 + 2 * element] ^= 0x0F; } // u12hi: bits 0..3
                for (int element = 0; element < 3; element++) { noisy[record + 28 + element] ^= 0x07; }     // i5: bits 0..2
                noisy[record + 42] ^= 0xF0; // u8o: last container's upper nibble
                noisy[record + 45] ^= 0xFF; // u16pad: padding byte between elements
            }
            int[] selected = { U12, U12Hi, I5, U8o, U16Pad };
            using (MdfReader clean = MdfReader.Open(new MemoryStream(original, false)))
            using (MdfReader dirty = MdfReader.Open(new MemoryStream(noisy, false)))
            {
                MdfSampleBatch a = clean.ReadSamples(Group(clean), selected).Single();
                MdfSampleBatch b = dirty.ReadSamples(Group(dirty), selected).Single();
                for (int row = 0; row < Rows; row++)
                for (int column = 0; column < selected.Length; column++)
                for (ulong element = 0; element < a.Channels[column].Array.ElementCount; element++)
                {
                    MdfSampleValue x = a.GetValue(row, column, element);
                    MdfSampleValue y = b.GetValue(row, column, element);
                    Assert.Equal(x.Kind, y.Kind);
                    if (x.Kind == MdfValueKind.SignedInteger) { Assert.Equal(x.SignedValue, y.SignedValue); }
                    else { Assert.Equal(x.UnsignedValue, y.UnsignedValue); }
                }
            }
        }

        [Fact]
        public void OverlappingContainersReadDisjointFields()
        {
            byte[] bytes = Fixture();
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = Group(reader);
                MdfSampleBatch batch = reader.ReadSamples(group, new[] { U8o, 27, 28, 29 }).Single();
                for (int row = 0; row < Rows; row++)
                for (int element = 0; element < 3; element++)
                {
                    // 8-bit fields at bit offset 4 with byte stride 1: each 2-byte container overlaps its neighbour.
                    ulong wanted = Field(bytes, Data + row * RecordBytes + 39 + element, 4, 8, false);
                    Assert.Equal(wanted, batch.GetValue(row, 0, (ulong)element).UnsignedValue);
                    Assert.Equal(wanted, batch.GetValue(row, 1 + element).UnsignedValue);
                }
            }
        }

        [Fact]
        public void SignedPackedElementsAreSignExtendedAndLinearlyConverted()
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(Fixture(), false)))
            {
                MdfChannelGroup group = Group(reader);
                MdfSampleBatch raw = reader.ReadSamples(group, new[] { I5 }).Single();
                MdfSampleBatch physical = reader.ReadSamples(group, new[] { I5 }, representation: MdfValueRepresentation.Physical).Single();
                Assert.Equal(MdfValueKind.SignedInteger, raw.GetValue(0, 0, 0).Kind);
                Assert.Equal(-16L, raw.GetValue(0, 0, 0).SignedValue);
                Assert.Equal(-1.0 + 0.5 * -16, physical.GetValue(0, 0, 0).FloatingPointValue);
            }
        }

        [Theory]
        [InlineData("element-bit-offset", 2, 186, 12 * 8 + 1, 2, U12, Element)]
        [InlineData("element-bit-count", 1, 188, 11, 2, U12, Element)]
        [InlineData("element-signed", 3, 190, 1, 2, U12, Element)]
        [InlineData("uneven", 3, 186, 15 * 8, 2, U12, Layout)]
        [InlineData("descending", 1, 186, 6 * 8, 2, U12, Layout)]
        [InlineData("coincident", 1, 186, 8 * 8, 2, U12, Layout)]
        [InlineData("parent-too-wide", -1, 188, 65, 2, U12, Element)]
        [InlineData("parent-float-width", -1, 190, 2, 2, U12, Element)]
        public void IneligiblePackedDependenciesStayInspectableButUnsupported(string name, int element, int field, int value, int width, int parent, string reason)
        {
            byte[] bytes = Fixture();
            M3Wire.Put(bytes, (element < 0 ? CnU12 : U12Cn(element)) + field, (ulong)value, width);
            Refused(bytes, name, parent, reason);
        }

        [Fact]
        public void AShiftedFloatElementRefusesItsParent()
        {
            byte[] bytes = Fixture();
            M3Wire.Put(bytes, CnF32Element1 + 186, 55 * 8 + 1, 2);
            Refused(bytes, "float-shift", 33, Element);
        }

        [Fact]
        public void AnOverlappingDescendingPairIsStillALayoutRefusal()
        {
            byte[] bytes = Fixture();
            M3Wire.Put(bytes, CnU8oElement1 + 186, 38 * 8 + 4, 2); // u8o[1] one byte before u8o[0]
            Refused(bytes, "u8o-descending", U8o, Layout);
        }

        private static void Refused(byte[] bytes, string name, int parent, string reason)
        {
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = Group(reader);
                MdfChannel channel = group.Channels[parent];
                Assert.Equal(reason, channel.UnsupportedReason);
                Assert.False(channel.CanReadRaw, name);
                Assert.NotNull(channel.Array);
                int before = source.ReadCalls;
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { parent }).ToList());
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { Counter, parent }).ToList());
                Assert.Equal(before, source.ReadCalls);
                int other = parent == U16Pad ? U12Hi : U16Pad;
                Assert.True(group.Channels[other].CanReadRaw);
                Assert.Equal(Rows, reader.ReadSamples(group, new[] { Counter, other }).Sum(b => b.SampleCount));
            }
        }
    }
}
