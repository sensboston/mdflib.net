// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Reading
{
    // literal mutations of the frozen m5k-layouts410 file, never the library's writer.
    public sealed class ArrayLayoutTests
    {
        private const string Id = "m5k-layouts410";
        private const int DataBytes = 165;
        private const int Record = 169;
        private const string Overlap = "Array byte stride must be at least the element's byte width.";
        private const string NoFlag = "A per-element invalidation stride requires the channel's invalidation-bit flag.";
        private const string Dynamic = "Dynamic-size arrays are not supported.";
        private const string Fixed = "A fixed-axis array requires the axis flag.";
        private const string Flags = "Unknown array flags, or the standard-axis flag before MDF 4.20, are not supported.";

        [Theory]
        [InlineData(1, 1U)]   // pad_u16: width 2
        [InlineData(6, 7U)]   // inv_f64: width 8, inverse
        [InlineData(9, 1U)]   // elem_pad_u16be: width 2, per-element bits
        public void OverlappingStridesAreUnsupportedWithoutGuessingAndOtherChannelsStillRead(int channel, uint stride)
        {
            byte[] bytes = M5DWire.Read(Id);
            M3Wire.Put(bytes, Ca(bytes, channel) + 8, stride, 4);
            Unsupported(bytes, channel, Overlap);
        }

        [Theory]
        [InlineData(7)]
        [InlineData(8)]
        [InlineData(9)]
        public void PerElementStrideWithoutAnInvalidationFlagIsUnsupported(int channel)
        {
            byte[] bytes = M5DWire.Read(Id);
            M3Wire.Put(bytes, Cn(bytes, channel) + 12, 0, 4);
            Unsupported(bytes, channel, NoFlag);
        }

        // each refused flag now has its own exact reason.
        [Theory]
        [InlineData(4, 0x41U, Dynamic)]
        [InlineData(4, 0x20U, Fixed)]
        [InlineData(4, 0x10040U, Flags)]
        [InlineData(4, 0x140U, Flags)]   // standard axis before 4.20
        [InlineData(1, 0x01U, Dynamic)]
        public void RefusedFlagsCombinedWithInverseLayoutRemainUnsupported(int channel, uint flags, string reason)
        {
            byte[] bytes = M5DWire.Read(Id);
            M3Wire.Put(bytes, Ca(bytes, channel) + 4, flags, 4);
            Unsupported(bytes, channel, reason);
        }

        [Theory]
        [InlineData(4, 0xC0U)]
        [InlineData(6, 0xC0U)]
        [InlineData(1, 0x80U)]
        public void LeftOpenFlagIsInertMetadataAndNeverChangesInverseOrPaddedValues(int channel, uint flags)
        {
            byte[] bytes = M5DWire.Read(Id);
            MdfSampleValue[][] before = Values(bytes, channel);
            M3Wire.Put(bytes, Ca(bytes, channel) + 4, flags, 4);
            MdfSampleValue[][] after = Values(bytes, channel);
            Assert.Equal(before.Select(row => row.Select(Bits).ToArray()), after.Select(row => row.Select(Bits).ToArray()));
            Assert.Equal(before.Select(row => row.Select(v => v.IsValid).ToArray()), after.Select(row => row.Select(v => v.IsValid).ToArray()));
        }

        [Theory]
        [InlineData(7, 0x48U)]   // comparison quantity without its DG/CG/CN triple
        [InlineData(7, 0x50U)]   // axis without its conversion and axis-channel links
        [InlineData(4, 0x42U)]   // input quantity without its per-dimension triples
        public void AcceptedFlagsWithoutTheirDeclaredLinksAreMalformedNotGuessed(int channel, uint flags)
        {
            byte[] bytes = M5DWire.Read(Id);
            M3Wire.Put(bytes, Ca(bytes, channel) + 4, flags, 4);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfFormatException error = Assert.Throws<MdfFormatException>(() => reader.ReadMetadata());
                Assert.Equal("MDF006", error.DiagnosticCode);
                Assert.Equal((ulong)M5DWire.Ca(bytes, channel), error.Offset);
            }
        }

        [Theory]
        [InlineData(9, 7U, true)]          // bits 17, 24, 31 of 32
        [InlineData(9, 8U, false)]         // bit 33
        [InlineData(7, 9U, true)]          // bits 3.. 30
        [InlineData(7, 10U, false)]        // bit 33
        [InlineData(8, 7U, true)]          // bits 8.. 29
        [InlineData(8, 0x80000000U, false)]
        [InlineData(7, uint.MaxValue, false)]
        public void PerElementInvalidationBitsAreBoundedByTheRecordsInvalidationArea(int channel, uint invalidationBase, bool fits)
        {
            byte[] bytes = M5DWire.Read(Id);
            M3Wire.Put(bytes, Ca(bytes, channel) + 12, invalidationBase, 4);
            if (fits) { Assert.Equal(5, Values(bytes, channel).Length); return; }
            Malformed(bytes, channel, "Per-element invalidation bits extend beyond the record's invalidation area.");
        }

        [Fact]
        public void AnAllInvalidChannelIgnoresItsPerElementStrideBounds()
        {
            byte[] bytes = M5DWire.Read(Id);
            M3Wire.Put(bytes, Ca(bytes, 11) + 12, uint.MaxValue, 4);
            for (int row = 0; row < 5; row++) { M3Wire.Put(bytes, RecordStart(bytes, row) + DataBytes, 0, 4); }
            Assert.All(Values(bytes, 11).SelectMany(r => r), v => Assert.False(v.IsValid));
        }

        [Theory]
        [InlineData(11, 1U, true)]          // offset 163, two u8: last byte 164
        [InlineData(11, 2U, false)]
        [InlineData(1, 77U, true)]          // offset 8, three u16: 8 + 154 + 2 = 164
        [InlineData(1, 78U, false)]
        [InlineData(6, (uint)int.MaxValue, false)]
        public void PaddedArrayExtentIsStrideTimesCountMinusOnePlusWidth(int channel, uint stride, bool fits)
        {
            byte[] bytes = M5DWire.Read(Id);
            M3Wire.Put(bytes, Ca(bytes, channel) + 8, stride, 4);
            if (fits) { Assert.Equal(5, Values(bytes, channel).Length); return; }
            Malformed(bytes, channel, "The complete array extends beyond the record's data area.");
        }

        [Theory]
        [InlineData(4, new ulong[] { 2, 3 })]
        [InlineData(5, new ulong[] { 2, 3 })]
        [InlineData(6, new ulong[] { 2, 2, 2 })]
        [InlineData(8, new ulong[] { 2, 2 })]
        public void InverseLayoutReadsTheColumnMajorStoredElementOfTheNaiveRowMajorReading(int channel, ulong[] shape)
        {
            byte[] bytes = M5DWire.Read(Id);
            MdfSampleValue[][] inverse = Values(bytes, channel);
            M3Wire.Put(bytes, Ca(bytes, channel) + 4, 0, 4);
            M3Wire.Put(bytes, Ca(bytes, channel) + 12, 0, 4); // naive reading also drops per-element validity
            MdfSampleValue[][] naive = Values(bytes, channel);
            bool differs = false;
            for (int row = 0; row < 5; row++)
            for (int element = 0; element < inverse[row].Length; element++)
            {
                MdfSampleValue expected = naive[row][(int)Stored(shape, (ulong)element)];
                Assert.Equal(Bits(expected), Bits(inverse[row][element]));
                differs |= Bits(naive[row][element]) != Bits(inverse[row][element]);
            }
            Assert.True(differs);
        }

        [Fact]
        public void RankOneInverseLayoutIsTheIdentityMapping()
        {
            byte[] bytes = M5DWire.Read(Id);
            MdfSampleValue[][] plain = Values(bytes, 1);
            M3Wire.Put(bytes, Ca(bytes, 1) + 4, 0x40, 4);
            MdfSampleValue[][] inverse = Values(bytes, 1);
            for (int row = 0; row < 5; row++) { Assert.Equal(plain[row].Select(Bits), inverse[row].Select(Bits)); }
        }

        [Fact]
        public void PaddingBytesAndTheChannelInsideThemAreNotPartOfTheArray()
        {
            byte[] bytes = M5DWire.Read(Id);
            MdfSampleValue[][] array = Values(bytes, 1);
            for (int row = 0; row < 5; row++)
            {
                int start = RecordStart(bytes, row);
                M3Wire.Put(bytes, start + 10, 0xBEEF, 2);   // gap_u16 inside the first padding
                M3Wire.Put(bytes, start + 14, 0xA5A5, 2);   // second padding
            }
            MdfSampleValue[][] after = Values(bytes, 1);
            MdfSampleValue[][] gap = Values(bytes, 2);
            for (int row = 0; row < 5; row++)
            {
                Assert.Equal(array[row].Select(Bits), after[row].Select(Bits));
                Assert.Equal(0xBEEFUL, gap[row][0].UnsignedValue);
            }
        }

        [Theory]
        [InlineData(7, 3, 1, new ulong[] { 4 }, false)]
        [InlineData(8, 8, 2, new ulong[] { 2, 2 }, true)]
        [InlineData(9, 17, 3, new ulong[] { 3 }, false)]
        public void EachElementOwnsExactlyItsStoredInvalidationBit(int channel, int bit, int invalidationBase, ulong[] shape, bool inverse)
        {
            byte[] original = M5DWire.Read(Id);
            MdfSampleValue[][] before = Values(original, channel);
            for (int element = 0; element < before[0].Length; element++)
            {
                byte[] bytes = (byte[])original.Clone();
                ulong stored = inverse ? Stored(shape, (ulong)element) : (ulong)element;
                int position = bit + invalidationBase * (int)stored;
                bytes[RecordStart(bytes, 1) + DataBytes + position / 8] ^= (byte)(1 << (position % 8));
                MdfSampleValue[][] after = Values(bytes, channel);
                for (int row = 0; row < 5; row++)
                for (int other = 0; other < before[row].Length; other++)
                {
                    bool flipped = row == 1 && other == element;
                    Assert.Equal(before[row][other].IsValid != flipped, after[row][other].IsValid);
                    Assert.Equal(Bits(before[row][other]), Bits(after[row][other]));
                }
            }
        }

        [Fact]
        public void ZeroInvalidationStrideKeepsOneSharedBitForEveryElement()
        {
            byte[] bytes = M5DWire.Read(Id);
            MdfSampleValue[][] before = Values(bytes, 10);
            bytes[RecordStart(bytes, 1) + DataBytes + 3] ^= 1; // bit 24
            MdfSampleValue[][] after = Values(bytes, 10);
            Assert.All(before[1], v => Assert.False(v.IsValid));
            Assert.All(after[1], v => Assert.True(v.IsValid));
            for (int row = 0; row < 5; row++)
            { if (row != 1) { Assert.Equal(before[row].Select(v => v.IsValid), after[row].Select(v => v.IsValid)); } }
        }

        [Fact]
        public void StrayInvalidationBitsDoNotAffectAnyChannel()
        {
            byte[] bytes = M5DWire.Read(Id);
            List<MdfSampleValue[][]> before = Enumerable.Range(0, 12).Select(c => Values(bytes, c)).ToList();
            for (int row = 0; row < 5; row++)
            {
                int area = RecordStart(bytes, row) + DataBytes;
                bytes[area] ^= 0x80;       // bit 7
                bytes[area + 1] ^= 0x0A;   // bits 9 and 11
            }
            for (int channel = 0; channel < 12; channel++)
            {
                MdfSampleValue[][] after = Values(bytes, channel);
                for (int row = 0; row < 5; row++)
                { Assert.Equal(before[channel][row].Select(v => v.IsValid), after[row].Select(v => v.IsValid)); }
            }
        }

        [Theory]
        [InlineData(40U, 2U, 0U, true)]      // frozen matrix u16[2,3] at 40: ends at 52 inside packet [16,56)
        [InlineData(40U, 3U, 0U, false)]     // padded: 40 + 3*5 + 2 = 57 > 56
        [InlineData(38U, 3U, 0U, true)]      // padded: 38 + 15 + 2 = 55
        [InlineData(38U, 3U, 0x40U, true)]   // padded inverse leaf
        [InlineData(44U, 2U, 0x40U, true)]   // inverse does not change the extent: 44 + 10 + 2 = 56, exact end
        [InlineData(45U, 2U, 0x40U, false)]  // 57 > 56
        public void PaddedArrayLeafExtentInsideAStructureUsesTheSameFormula(uint byteOffset, uint stride, uint flags, bool fits)
        {
            byte[] bytes = M5EWire.Read();
            int matrix = M5EWire.Child(bytes, M5EWire.Packet(bytes), 2);
            int ca = (int)M3Wire.U64(bytes, M3Wire.Link(matrix, 1));
            M5EWire.ByteOffset(bytes, matrix, byteOffset);
            M3Wire.Put(bytes, M3Wire.Payload(bytes, ca) + 8, stride, 4);
            M3Wire.Put(bytes, M3Wire.Payload(bytes, ca) + 4, flags, 4);
            if (fits)
            {
                using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
                {
                    MdfChannel leaf = reader.ReadMetadata()[0].ChannelGroups[0].Channels[2].Components[2];
                    Assert.True(leaf.CanReadRaw, leaf.UnsupportedReason);
                    Assert.Equal(new ulong[] { 2, 3 }, leaf.Array.Dimensions);
                }
                return;
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfFormatException error = Assert.Throws<MdfFormatException>(() => reader.ReadMetadata());
                Assert.Equal("MDF013", error.DiagnosticCode);
                Assert.Equal((ulong)matrix, error.Offset);
            }
        }

        private static ulong Stored(ulong[] shape, ulong element)
        {
            ulong stored = 0;
            ulong weight = 1;
            ulong[] indices = new ulong[shape.Length];
            for (int axis = shape.Length - 1; axis >= 0; axis--) { indices[axis] = element % shape[axis]; element /= shape[axis]; }
            for (int axis = 0; axis < shape.Length; axis++) { stored += indices[axis] * weight; weight *= shape[axis]; }
            return stored;
        }

        private static long Bits(MdfSampleValue value)
        {
            if (value.Kind == MdfValueKind.UnsignedInteger) { return unchecked((long)value.UnsignedValue); }
            if (value.Kind == MdfValueKind.SignedInteger) { return value.SignedValue; }
            return BitConverter.DoubleToInt64Bits(value.FloatingPointValue);
        }

        private static int Ca(byte[] bytes, int channel) { return M3Wire.Payload(bytes, M5DWire.Ca(bytes, channel)); }
        private static int Cn(byte[] bytes, int channel) { return M3Wire.Payload(bytes, M5DWire.Cn(bytes, channel)); }
        private static int RecordStart(byte[] bytes, int row) { return M5DWire.Root(bytes) + 24 + row * Record; }

        private static MdfSampleValue[][] Values(byte[] bytes, int channel)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.True(group.Channels[channel].CanReadRaw, group.Channels[channel].UnsupportedReason);
                ulong count = group.Channels[channel].Array == null ? 1 : group.Channels[channel].Array.ElementCount;
                List<MdfSampleValue[]> rows = new List<MdfSampleValue[]>();
                foreach (MdfSampleBatch batch in reader.ReadSamples(group, new[] { channel }, batchSize: 2))
                for (int row = 0; row < batch.SampleCount; row++)
                {
                    MdfSampleValue[] values = new MdfSampleValue[count];
                    for (ulong element = 0; element < count; element++) { values[element] = batch.GetValue(row, 0, element); }
                    rows.Add(values);
                }
                return rows.ToArray();
            }
        }

        private static void Unsupported(byte[] bytes, int channel, string reason)
        {
            long body = M5DWire.Root(bytes) + 24;
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.NotNull(group.Channels[channel].Array);
                Assert.False(group.Channels[channel].CanReadRaw);
                Assert.False(group.Channels[channel].CanReadPhysical);
                Assert.Equal(reason, group.Channels[channel].UnsupportedReason);
                // Header-only catalog; refusal happens before any record I/O.
                Assert.DoesNotContain(source.ReadRanges, r => r.Item1 + r.Item2 > body && r.Item1 < body + 5 * Record);
                int before = source.ReadCalls;
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { channel }).ToList());
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 0, channel }).ToList());
                Assert.Equal(before, source.ReadCalls);
                Assert.Equal(5, reader.ReadSamples(group, new[] { 0, 2 }).Sum(b => b.SampleCount));
            }
        }

        private static void Malformed(byte[] bytes, int channel, string message)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfFormatException error = Assert.Throws<MdfFormatException>(() => reader.ReadMetadata());
                Assert.Equal("MDF013", error.DiagnosticCode);
                Assert.Contains(message, error.Message);
                Assert.Equal((ulong)M5DWire.Cn(bytes, channel), error.Offset);
            }
        }
    }
}
