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
    // an integer array element is the scalar field at the CN bit offset inside
    // its own container. Literal mutations of frozen files (m5d-arrays410 u8/u16 LE/BE [2,3]; m5u corpus), never the writer.
    public sealed class PackedArrayElementTests
    {
        private const string Overlap = "Array byte stride must be at least the element's byte width.";

        [Theory]
        [InlineData(2, 1)]   // u8_le
        [InlineData(3, 1)]   // u8_be
        [InlineData(4, 2)]   // u16_le
        [InlineData(5, 2)]   // u16_be
        public void EveryBitOffsetAndWidthInsideTheContainerIsTheShiftedMaskedField(int channel, int width)
        {
            byte[] original = M5DWire.Read();
            ulong[][] containers = Values(original, channel).Select(row => row.Select(v => v.UnsignedValue).ToArray()).ToArray();
            int cn = M3Wire.Payload(original, M5DWire.Cn(original, channel));
            byte dataType = original[cn + 2];
            bool bigEndian = (dataType & 1) != 0;
            for (int offset = 0; offset < 8; offset++)
            for (int bits = 1; bits <= width * 8 - offset; bits++)
            foreach (bool signed in new[] { false, true })
            {
                byte[] bytes = (byte[])original.Clone();
                bytes[cn + 2] = (byte)(dataType + (signed ? 2 : 0));
                bytes[cn + 3] = (byte)offset;
                M3Wire.Put(bytes, cn + 8, (ulong)bits, 4);
                MdfSampleValue[][] values = Values(bytes, channel);
                for (int row = 0; row < values.Length; row++)
                for (int element = 0; element < values[row].Length; element++)
                {
                    // The element container is the minimal span of whole bytes; a big-endian span shorter than the stored
                    // width is the leading (most significant) bytes of the original value.
                    int span = (offset + bits + 7) / 8;
                    ulong container = bigEndian ? containers[row][element] >> (8 * (width - span)) : containers[row][element];
                    ulong field = container >> offset & (bits == 64 ? ulong.MaxValue : (1UL << bits) - 1);
                    if (!signed)
                    {
                        Assert.Equal(MdfValueKind.UnsignedInteger, values[row][element].Kind);
                        Assert.Equal(field, values[row][element].UnsignedValue);
                        continue;
                    }
                    long expected = (field >> (bits - 1) & 1) != 0 ? (long)field - (1L << bits) : (long)field;
                    Assert.Equal(MdfValueKind.SignedInteger, values[row][element].Kind);
                    Assert.Equal(expected, values[row][element].SignedValue);
                }
            }
        }

        [Theory]
        [InlineData(2, 1, 8)]
        [InlineData(2, 2, 7)]
        [InlineData(4, 1, 16)]
        [InlineData(4, 7, 10)]
        [InlineData(5, 3, 14)]
        public void ContainerWiderThanTheByteStrideIsRefusedNotOverlapped(int channel, int offset, int bits)
        {
            byte[] bytes = M5DWire.Read();
            int cn = M3Wire.Payload(bytes, M5DWire.Cn(bytes, channel));
            bytes[cn + 3] = (byte)offset;
            M3Wire.Put(bytes, cn + 8, (ulong)bits, 4);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(Overlap, group.Channels[channel].UnsupportedReason);
                Assert.NotNull(group.Channels[channel].Array);
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { channel }));
                Assert.Equal(5, reader.ReadSamples(group, new[] { 1 }).Sum(b => b.SampleCount));
            }
        }

        [Theory]
        [InlineData(1, "Only byte-aligned IEEE 16/32/64-bit floating-point storage is supported.")]
        [InlineData(65, "Integer widths greater than 64 bits are not supported.")]
        public void FloatsStayByteAlignedAndIntegersStayAtMost64Bits(int variant, string reason)
        {
            byte[] bytes = M5DWire.Read();
            int cn = M3Wire.Payload(bytes, M5DWire.Cn(bytes, 2));
            if (variant == 1) { bytes[cn + 2] = 4; M3Wire.Put(bytes, cn + 8, 32, 4); bytes[cn + 3] = 1; }
            else { M3Wire.Put(bytes, cn + 8, 65, 4); }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                Assert.Equal(reason, reader.ReadMetadata()[0].ChannelGroups[0].Channels[2].UnsupportedReason);
            }
        }

        [Theory]
        [InlineData(24U, true)]    // 45 + 24 + 9: the last 9-byte container ends exactly at the data area end (78)
        [InlineData(25U, false)]
        public void LastContainerMustFitTheDataArea(uint stride, bool fits)
        {
            byte[] bytes = M5DWire.Read("m5u-packed410");
            int u64 = Index(bytes, "u64o");
            M3Wire.Put(bytes, M3Wire.Payload(bytes, M5DWire.Ca(bytes, u64)) + 8, stride, 4);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                if (fits)
                {
                    MdfChannel channel = reader.ReadMetadata()[0].ChannelGroups[0].Channels[u64];
                    Assert.True(channel.CanReadRaw, channel.UnsupportedReason);
                    return;
                }
                MdfFormatException error = Assert.Throws<MdfFormatException>(() => reader.ReadMetadata());
                Assert.Equal("MDF013", error.DiagnosticCode);
            }
        }

        [Fact]
        public void FillerBitsNeverReachElementValues()
        {
            byte[] bytes = M5DWire.Read("m5u-packed410");
            int u12 = Index(bytes, "u12hi");      // field = upper 12 bits of 2-byte containers at 16, 18, 20
            MdfSampleValue[][] before = Values(bytes, u12);
            int records = M5DWire.Root(bytes) + 24;
            const int size = 79;                  // 78 data bytes + 1 invalidation byte
            for (int row = 0; row < 5; row++)
            for (int element = 0; element < 3; element++) { bytes[records + row * size + 16 + element * 2] ^= 0x0F; }
            MdfSampleValue[][] after = Values(bytes, u12);
            Assert.Equal(before.Select(r => r.Select(v => v.UnsignedValue)), after.Select(r => r.Select(v => v.UnsignedValue)));
            bytes[records + 16] ^= 0x10;          // lowest field bit of row 0, element 0
            Assert.NotEqual(before[0][0].UnsignedValue, Values(bytes, u12)[0][0].UnsignedValue);
        }

        [Fact]
        public void NineByteBigEndianContainersAndPerElementInvalidationReadExactly()
        {
            byte[] bytes = M5DWire.Read("m5u-packed410");
            MdfSampleValue[][] values = Values(bytes, Index(bytes, "u64o"));
            Assert.Equal(new ulong[] { 0, 1 }, values[0].Select(v => v.UnsignedValue));
            Assert.Equal(new ulong[] { 1, ulong.MaxValue }, values[1].Select(v => v.UnsignedValue));
            byte[] other = M5DWire.Read("m5u-packed420");
            MdfSampleValue[][] u3 = Values(other, Index(other, "u3be"));
            Assert.Contains(u3.SelectMany(r => r), v => !v.IsValid);
            Assert.Contains(u3.SelectMany(r => r), v => v.IsValid);
            Assert.All(u3.SelectMany(r => r), v => Assert.InRange(v.UnsignedValue, 0UL, 7UL));
        }

        private static int Index(byte[] bytes, string name)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            { return reader.ReadMetadata()[0].ChannelGroups[0].Channels.Single(c => c.Name == name).Index; }
        }

        private static MdfSampleValue[][] Values(byte[] bytes, int channel)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.True(group.Channels[channel].CanReadRaw, group.Channels[channel].UnsupportedReason);
                ulong count = group.Channels[channel].Array.ElementCount;
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
    }
}
