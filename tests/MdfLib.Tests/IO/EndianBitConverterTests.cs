// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using MdfLib.IO;
using Xunit;

namespace MdfLib.Tests.IO
{
    public sealed class EndianBitConverterTests
    {
        [Fact]
        public void LiteralIntegerVectorsUseTheSpecifiedByteOrder()
        {
            byte[] bytes = { 0x7f, 0x01, 0x23, 0x45, 0x67, 0x89, 0xab, 0xcd, 0xef, 0x7f };
            Assert.Equal((ushort)0x0123, EndianBitConverter.ReadUInt16(bytes, 1, ByteOrder.BigEndian));
            Assert.Equal((ushort)0x2301, EndianBitConverter.ReadUInt16(bytes, 1, ByteOrder.LittleEndian));
            Assert.Equal(0x01234567U, EndianBitConverter.ReadUInt32(bytes, 1, ByteOrder.BigEndian));
            Assert.Equal(0x67452301U, EndianBitConverter.ReadUInt32(bytes, 1, ByteOrder.LittleEndian));
            Assert.Equal(0x0123456789abcdefUL, EndianBitConverter.ReadUInt64(bytes, 1, ByteOrder.BigEndian));
            Assert.Equal(0xefcdab8967452301UL, EndianBitConverter.ReadUInt64(bytes, 1, ByteOrder.LittleEndian));
        }

        [Fact]
        public void UnsignedValuesAboveTheDoublePrecisionLimitRemainExact()
        {
            byte[] bytes = { 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20, 0x00 };
            Assert.Equal(9_007_199_254_740_993UL, EndianBitConverter.ReadUInt64(bytes, 0, ByteOrder.LittleEndian));
            Array.Reverse(bytes);
            Assert.Equal(9_007_199_254_740_993UL, EndianBitConverter.ReadUInt64(bytes, 0, ByteOrder.BigEndian));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SignedExtremaUseTwosComplement(bool bigEndian)
        {
            ByteOrder order = bigEndian ? ByteOrder.BigEndian : ByteOrder.LittleEndian;
            byte[] minimum16 = Ordered(new byte[] { 0x80, 0x00 }, bigEndian);
            byte[] minimum32 = Ordered(new byte[] { 0x80, 0x00, 0x00, 0x00 }, bigEndian);
            byte[] minimum64 = Ordered(new byte[] { 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, bigEndian);
            Assert.Equal(short.MinValue, EndianBitConverter.ReadInt16(minimum16, 0, order));
            Assert.Equal(int.MinValue, EndianBitConverter.ReadInt32(minimum32, 0, order));
            Assert.Equal(long.MinValue, EndianBitConverter.ReadInt64(minimum64, 0, order));
            Assert.Equal(short.MaxValue, EndianBitConverter.ReadInt16(Ordered(new byte[] { 0x7f, 0xff }, bigEndian), 0, order));
            Assert.Equal(int.MaxValue, EndianBitConverter.ReadInt32(Ordered(new byte[] { 0x7f, 0xff, 0xff, 0xff }, bigEndian), 0, order));
            Assert.Equal(long.MaxValue, EndianBitConverter.ReadInt64(Ordered(new byte[] { 0x7f, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff }, bigEndian), 0, order));
            byte[] ones = { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff };
            Assert.Equal((short)-1, EndianBitConverter.ReadInt16(ones, 0, order));
            Assert.Equal(-1, EndianBitConverter.ReadInt32(ones, 0, order));
            Assert.Equal(-1L, EndianBitConverter.ReadInt64(ones, 0, order));
            Assert.Equal(ushort.MaxValue, EndianBitConverter.ReadUInt16(ones, 0, order));
            Assert.Equal(uint.MaxValue, EndianBitConverter.ReadUInt32(ones, 0, order));
            Assert.Equal(ulong.MaxValue, EndianBitConverter.ReadUInt64(ones, 0, order));
            minimum16[bigEndian ? 0 : 1] = 0x7f;
            minimum32[bigEndian ? 0 : 3] = 0x7f;
            minimum64[bigEndian ? 0 : 7] = 0x7f;
            // The low bits are still zero: these are positive values, not sign-extended ones.
            Assert.Equal((short)0x7f00, EndianBitConverter.ReadInt16(minimum16, 0, order));
            Assert.Equal(0x7f000000, EndianBitConverter.ReadInt32(minimum32, 0, order));
            Assert.Equal(0x7f00000000000000L, EndianBitConverter.ReadInt64(minimum64, 0, order));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void FloatingPointVectorsPreserveSpecialValuesAndBits(bool bigEndian)
        {
            ByteOrder order = bigEndian ? ByteOrder.BigEndian : ByteOrder.LittleEndian;
            Assert.Equal(1.0f, EndianBitConverter.ReadSingle(Ordered(new byte[] { 0x3f, 0x80, 0x00, 0x00 }, bigEndian), 0, order));
            Assert.Equal(1.0, EndianBitConverter.ReadDouble(Ordered(new byte[] { 0x3f, 0xf0, 0, 0, 0, 0, 0, 0 }, bigEndian), 0, order));
            Assert.Equal(float.PositiveInfinity, EndianBitConverter.ReadSingle(Ordered(new byte[] { 0x7f, 0x80, 0, 0 }, bigEndian), 0, order));
            Assert.Equal(double.NegativeInfinity, EndianBitConverter.ReadDouble(Ordered(new byte[] { 0xff, 0xf0, 0, 0, 0, 0, 0, 0 }, bigEndian), 0, order));
            float singleZero = EndianBitConverter.ReadSingle(Ordered(new byte[] { 0x80, 0, 0, 0 }, bigEndian), 0, order);
            Assert.Equal(0x80000000U, BitConverter.ToUInt32(BitConverter.GetBytes(singleZero), 0));
            double doubleZero = EndianBitConverter.ReadDouble(Ordered(new byte[] { 0x80, 0, 0, 0, 0, 0, 0, 0 }, bigEndian), 0, order);
            Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(doubleZero));
            float singleNan = EndianBitConverter.ReadSingle(Ordered(new byte[] { 0x7f, 0xc0, 0, 1 }, bigEndian), 0, order);
            double doubleNan = EndianBitConverter.ReadDouble(Ordered(new byte[] { 0x7f, 0xf8, 0, 0, 0, 0, 0, 1 }, bigEndian), 0, order);
            Assert.True(float.IsNaN(singleNan));
            Assert.True(double.IsNaN(doubleNan));
            Assert.Equal(0x7fc00001U, BitConverter.ToUInt32(BitConverter.GetBytes(singleNan), 0));
            Assert.Equal(0x7ff8000000000001L, BitConverter.DoubleToInt64Bits(doubleNan));
            Assert.Equal(float.Epsilon, EndianBitConverter.ReadSingle(Ordered(new byte[] { 0, 0, 0, 1 }, bigEndian), 0, order));
            Assert.Equal(double.Epsilon, EndianBitConverter.ReadDouble(Ordered(new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 }, bigEndian), 0, order));
        }

        [Fact]
        public void PrimitiveReadersRejectInvalidArguments()
        {
            byte[] bytes = new byte[8];
            Assert.Throws<ArgumentNullException>(() => EndianBitConverter.ReadUInt64(null, 0, ByteOrder.LittleEndian));
            Assert.Throws<ArgumentOutOfRangeException>(() => EndianBitConverter.ReadUInt16(bytes, -1, ByteOrder.LittleEndian));
            Assert.Throws<ArgumentOutOfRangeException>(() => EndianBitConverter.ReadUInt32(bytes, 5, ByteOrder.LittleEndian));
            Assert.Throws<ArgumentOutOfRangeException>(() => EndianBitConverter.ReadUInt64(bytes, 1, ByteOrder.LittleEndian));
            Assert.Throws<ArgumentOutOfRangeException>(() => EndianBitConverter.ReadDouble(bytes, int.MaxValue, ByteOrder.BigEndian));
            Assert.Throws<ArgumentOutOfRangeException>(() => EndianBitConverter.ReadUInt16(bytes, 0, (ByteOrder)17));
            Assert.Throws<ArgumentOutOfRangeException>(() => EndianBitConverter.ReadSingle(bytes, 0, (ByteOrder)(-1)));
        }

        [Fact]
        public void PackedBitVectorsHaveExplicitBitNumbering()
        {
            byte[] bytes = { 0xd6, 0x39, 0xa5 };
            Assert.Equal(0xe75UL, EndianBitConverter.ReadUnsignedBits(bytes, 2, 12, ByteOrder.LittleEndian));
            Assert.Equal(0x58eUL, EndianBitConverter.ReadUnsignedBits(bytes, 2, 12, ByteOrder.BigEndian));
            Assert.Equal(-0x18bL, EndianBitConverter.ReadSignedBits(bytes, 2, 12, ByteOrder.LittleEndian));
            Assert.Equal(0x58eL, EndianBitConverter.ReadSignedBits(bytes, 2, 12, ByteOrder.BigEndian));
            Assert.Equal(1UL, EndianBitConverter.ReadUnsignedBits(bytes, 23, 1, ByteOrder.BigEndian));
            Assert.Equal(-1L, EndianBitConverter.ReadSignedBits(bytes, 23, 1, ByteOrder.BigEndian));
            Assert.Equal(0UL, EndianBitConverter.ReadUnsignedBits(bytes, 0, 1, ByteOrder.LittleEndian));
        }

        [Fact]
        public void BitSignExtensionHandlesEveryWidthIncludingSixtyFour()
        {
            byte[] ones = new byte[9];
            for (int index = 0; index < ones.Length; index++)
            {
                ones[index] = 0xff;
            }
            byte[] zeroes = new byte[9];
            foreach (ByteOrder order in new[] { ByteOrder.LittleEndian, ByteOrder.BigEndian })
            {
                for (int width = 1; width <= 64; width++)
                {
                    Assert.Equal(-1L, EndianBitConverter.ReadSignedBits(ones, 3, width, order));
                    Assert.Equal(0L, EndianBitConverter.ReadSignedBits(zeroes, 3, width, order));
                    ulong expected = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
                    Assert.Equal(expected, EndianBitConverter.ReadUnsignedBits(ones, 3, width, order));
                }
            }
            byte[] minimum = { 0x80, 0, 0, 0, 0, 0, 0, 0 };
            Assert.Equal(long.MinValue, EndianBitConverter.ReadSignedBits(minimum, 0, 64, ByteOrder.BigEndian));
            Array.Reverse(minimum);
            Assert.Equal(long.MinValue, EndianBitConverter.ReadSignedBits(minimum, 0, 64, ByteOrder.LittleEndian));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ByteAlignedBitValuesMatchIndependentlyGeneratedIntegerValues(bool bigEndian)
        {
            Random random = new Random(731648);
            ByteOrder order = bigEndian ? ByteOrder.BigEndian : ByteOrder.LittleEndian;
            for (int sample = 0; sample < 256; sample++)
            {
                byte[] source = new byte[8];
                random.NextBytes(source);
                // Build the expected value arithmetically, without calling the library writer/reader.
                ulong expected = 0;
                ulong factor = 1;
                for (int index = 0; index < source.Length; index++)
                {
                    int selected = bigEndian ? source.Length - index - 1 : index;
                    expected += source[selected] * factor;
                    if (index != source.Length - 1)
                    {
                        factor *= 256;
                    }
                }
                Assert.Equal(expected, EndianBitConverter.ReadUInt64(source, 0, order));
                Assert.Equal(expected, EndianBitConverter.ReadUnsignedBits(source, 0, 64, order));
            }
        }

        [Fact]
        public void ConvertersNeverMutateTheSourceArray()
        {
            byte[] source = { 0xd6, 0x39, 0xa5, 0x67, 0x89, 0xab, 0xcd, 0xef };
            byte[] original = (byte[])source.Clone();
            foreach (ByteOrder order in new[] { ByteOrder.LittleEndian, ByteOrder.BigEndian })
            {
                EndianBitConverter.ReadUInt16(source, 1, order);
                EndianBitConverter.ReadUInt32(source, 1, order);
                EndianBitConverter.ReadUInt64(source, 0, order);
                EndianBitConverter.ReadSingle(source, 1, order);
                EndianBitConverter.ReadDouble(source, 0, order);
                EndianBitConverter.ReadUnsignedBits(source, 1, 63, order);
                EndianBitConverter.ReadSignedBits(source, 1, 63, order);
            }
            Assert.Equal(original, source);
        }

        [Fact]
        public void BitReadersRejectInvalidWidthsAndOverflowingOffsets()
        {
            byte[] bytes = new byte[9];
            Assert.Throws<ArgumentNullException>(() => EndianBitConverter.ReadUnsignedBits(null, 0, 1, ByteOrder.LittleEndian));
            Assert.Throws<ArgumentOutOfRangeException>(() => EndianBitConverter.ReadUnsignedBits(bytes, 0, 0, ByteOrder.LittleEndian));
            Assert.Throws<ArgumentOutOfRangeException>(() => EndianBitConverter.ReadUnsignedBits(bytes, 0, 65, ByteOrder.LittleEndian));
            Assert.Throws<ArgumentOutOfRangeException>(() => EndianBitConverter.ReadSignedBits(bytes, 0, -1, ByteOrder.BigEndian));
            Assert.Throws<ArgumentOutOfRangeException>(() => EndianBitConverter.ReadUnsignedBits(bytes, -1, 1, ByteOrder.LittleEndian));
            Assert.Throws<ArgumentOutOfRangeException>(() => EndianBitConverter.ReadUnsignedBits(bytes, long.MaxValue, 1, ByteOrder.BigEndian));
            Assert.Throws<ArgumentOutOfRangeException>(() => EndianBitConverter.ReadUnsignedBits(bytes, 72, 1, ByteOrder.LittleEndian));
            Assert.Throws<ArgumentOutOfRangeException>(() => EndianBitConverter.ReadUnsignedBits(bytes, 9, 64, ByteOrder.BigEndian));
            Assert.Throws<ArgumentOutOfRangeException>(() => EndianBitConverter.ReadUnsignedBits(bytes, 0, 1, (ByteOrder)17));
        }

        private static byte[] Ordered(byte[] bigEndianBytes, bool bigEndian)
        {
            if (!bigEndian)
            {
                Array.Reverse(bigEndianBytes);
            }
            return bigEndianBytes;
        }
    }
}
