// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Numerics;
using MdfLib.Reading;
using Xunit;

namespace MdfLib.Tests.Reading
{
    public sealed class ScalarDecoderTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void EveryMdfWidthAndOffsetMatchesIndependentArbitraryPrecisionContainerArithmetic(bool bigEndian, bool signed)
        {
            Random random = new Random(7341064);
            RecordLayout record = new RecordLayout { DataBytes = 24, InvalidationBytes = 0 };
            for (int width = 1; width <= 64; width++)
            {
                for (int shift = 0; shift <= 7; shift++)
                {
                    int span = (width + shift + 7) / 8;
                    for (int sample = 0; sample < 12; sample++)
                    {
                        byte[] buffer = new byte[32];
                        random.NextBytes(buffer);
                        // Independent whole-container BigInteger oracle; never calls the library's bit primitives.
                        byte[] positive = new byte[span + 1];
                        Array.Copy(buffer, 8, positive, 0, span);
                        if (bigEndian) { Array.Reverse(positive, 0, span); }
                        BigInteger container = new BigInteger(positive);
                        BigInteger expected = (container >> shift) & ((BigInteger.One << width) - 1);
                        if (signed && expected >= (BigInteger.One << (width - 1))) { expected -= BigInteger.One << width; }
                        MdfChannel channel = new MdfChannel
                        {
                            Layout = new ChannelLayout { ByteOffset = 3, BitOffset = shift, BitCount = width, DataType = (byte)((signed ? 2 : 0) + (bigEndian ? 1 : 0)) },
                            Conversion = MdfChannelConversion.Identity
                        };
                        MdfSampleValue actual = ScalarDecoder.Read(buffer, 5, record, channel, MdfValueRepresentation.Raw, 0);
                        Assert.True(actual.IsValid);
                        if (signed) { Assert.Equal((long)expected, actual.SignedValue); }
                        else { Assert.Equal((ulong)expected, actual.UnsignedValue); }
                    }
                }
            }
        }

        [Theory]
        [InlineData(0UL)]
        [InlineData(4294967296UL)]
        [InlineData(ulong.MaxValue)]
        public void VirtualChannelsReturnTheFullRecordIndexWithoutReadingTheBuffer(ulong index)
        {
            // no record field, never truncated to the declared bit count, always valid.
            MdfChannel channel = new MdfChannel
            {
                Layout = new ChannelLayout { Virtual = true, DataType = 0, BitCount = 8, ByteOffset = uint.MaxValue },
                Conversion = new MdfChannelConversion { Kind = MdfConversionKind.Linear, Factor = 2, Offset = -1 }
            };
            MdfSampleValue raw = ScalarDecoder.Read(new byte[0], 0, new RecordLayout { DataBytes = 1 }, channel, MdfValueRepresentation.Raw, index);
            Assert.Equal(MdfValueKind.UnsignedInteger, raw.Kind);
            Assert.Equal(index, raw.UnsignedValue);
            Assert.True(raw.IsValid);
            MdfSampleValue physical = ScalarDecoder.Read(new byte[0], 0, new RecordLayout { DataBytes = 1 }, channel, MdfValueRepresentation.Physical, index);
            Assert.Equal(MdfValueKind.FloatingPoint, physical.Kind);
            Assert.Equal(2.0 * index - 1, physical.FloatingPointValue);
        }

        [Fact]
        public void AllInvalidOverridesUnusedInvalidationPositionAndLinearKeepsValiditySeparate()
        {
            MdfChannel channel = new MdfChannel
            {
                Layout = new ChannelLayout { DataType = 0, BitCount = 8, Flags = 3, InvalidationBit = uint.MaxValue },
                Conversion = new MdfChannelConversion { Kind = MdfConversionKind.Linear, Factor = double.MaxValue, Offset = -10 }
            };
            MdfSampleValue raw = ScalarDecoder.Read(new byte[] { 255 }, 0, new RecordLayout { DataBytes = 1 }, channel, MdfValueRepresentation.Raw, 7);
            Assert.Equal(255UL, raw.UnsignedValue);
            Assert.False(raw.IsValid);
            MdfSampleValue physical = ScalarDecoder.Read(new byte[] { 255 }, 0, new RecordLayout { DataBytes = 1 }, channel, MdfValueRepresentation.Physical, 7);
            Assert.Equal(double.PositiveInfinity, physical.FloatingPointValue);
            Assert.False(physical.IsValid);
        }
    }
}
