// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;

namespace MdfLib.IO
{
    internal static class EndianBitConverter
    {
        internal static ushort ReadUInt16(byte[] buffer, int offset, ByteOrder byteOrder)
        {
            return (ushort)ReadInteger(buffer, offset, 2, byteOrder);
        }

        internal static uint ReadUInt32(byte[] buffer, int offset, ByteOrder byteOrder)
        {
            return (uint)ReadInteger(buffer, offset, 4, byteOrder);
        }

        internal static ulong ReadUInt64(byte[] buffer, int offset, ByteOrder byteOrder)
        {
            return ReadInteger(buffer, offset, 8, byteOrder);
        }

        internal static short ReadInt16(byte[] buffer, int offset, ByteOrder byteOrder)
        {
            return unchecked((short)ReadUInt16(buffer, offset, byteOrder));
        }

        internal static int ReadInt32(byte[] buffer, int offset, ByteOrder byteOrder)
        {
            return unchecked((int)ReadUInt32(buffer, offset, byteOrder));
        }

        internal static long ReadInt64(byte[] buffer, int offset, ByteOrder byteOrder)
        {
            return unchecked((long)ReadUInt64(buffer, offset, byteOrder));
        }

        internal static float ReadSingle(byte[] buffer, int offset, ByteOrder byteOrder)
        {
            // GetBytes and ToSingle use the same host byte order: only the bits are reinterpreted.
            return BitConverter.ToSingle(BitConverter.GetBytes(ReadUInt32(buffer, offset, byteOrder)), 0);
        }

        internal static double ReadDouble(byte[] buffer, int offset, ByteOrder byteOrder)
        {
            return BitConverter.Int64BitsToDouble(unchecked((long)ReadUInt64(buffer, offset, byteOrder)));
        }

        // IEEE 754 binary16 widened exactly to binary64.
        internal static double ReadHalf(byte[] buffer, int offset, ByteOrder byteOrder)
        {
            return HalfToDouble(ReadUInt16(buffer, offset, byteOrder));
        }

        // Every binary16 value is exact in binary64: the sign is kept (incl. -0 and NaN), the 10-bit fraction becomes the
        // top of the 52-bit fraction, subnormals are normalized, exponent 31 maps to infinity/NaN with its payload.
        internal static double HalfToDouble(ushort bits)
        {
            long sign = (long)(bits >> 15) << 63;
            int exponent = bits >> 10 & 0x1F;
            long fraction = bits & 0x3FF;
            if (exponent == 0x1F)
            {
                return BitConverter.Int64BitsToDouble(sign | 0x7FF0000000000000L | fraction << 42);
            }
            if (exponent == 0)
            {
                if (fraction == 0) { return BitConverter.Int64BitsToDouble(sign); }
                exponent = 1;
                while ((fraction & 0x400) == 0)
                {
                    fraction <<= 1;
                    exponent--;
                }
                fraction &= 0x3FF;
            }
            return BitConverter.Int64BitsToDouble(sign | (long)(exponent + 1008) << 52 | fraction << 42);
        }

        internal static ulong ReadUnsignedBits(byte[] buffer, long bitOffset, int bitCount, ByteOrder byteOrder)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }
            ValidateByteOrder(byteOrder);
            if (bitCount < 1 || bitCount > 64)
            {
                throw new ArgumentOutOfRangeException(nameof(bitCount));
            }

            long totalBits = (long)buffer.Length * 8;
            if (bitOffset < 0 || bitOffset > totalBits)
            {
                throw new ArgumentOutOfRangeException(nameof(bitOffset));
            }
            if (bitCount > totalBits - bitOffset)
            {
                throw new ArgumentOutOfRangeException(nameof(bitCount));
            }

            ulong result = 0;
            for (int index = 0; index < bitCount; index++)
            {
                long bit = bitOffset + index;
                int withinByte = (int)(bit & 7);
                int shift = byteOrder == ByteOrder.LittleEndian ? withinByte : 7 - withinByte;
                ulong value = (ulong)((buffer[(int)(bit >> 3)] >> shift) & 1);
                if (byteOrder == ByteOrder.LittleEndian)
                {
                    result |= value << index;
                }
                else
                {
                    result = (result << 1) | value;
                }
            }
            return result;
        }

        internal static long ReadSignedBits(byte[] buffer, long bitOffset, int bitCount, ByteOrder byteOrder)
        {
            ulong result = ReadUnsignedBits(buffer, bitOffset, bitCount, byteOrder);
            if (bitCount < 64 && (result & (1UL << (bitCount - 1))) != 0)
            {
                result |= ulong.MaxValue << bitCount;
            }
            return unchecked((long)result);
        }

        internal static void ValidateByteOrder(ByteOrder byteOrder)
        {
            if (byteOrder != ByteOrder.LittleEndian && byteOrder != ByteOrder.BigEndian)
            {
                throw new ArgumentOutOfRangeException(nameof(byteOrder));
            }
        }

        private static ulong ReadInteger(byte[] buffer, int offset, int width, ByteOrder byteOrder)
        {
            BinaryBounds.ValidateBufferRange(buffer, offset, width);
            ValidateByteOrder(byteOrder);
            ulong result = 0;
            for (int index = 0; index < width; index++)
            {
                int source = byteOrder == ByteOrder.LittleEndian ? width - index - 1 : index;
                result = (result << 8) | buffer[offset + source];
            }
            return result;
        }
    }
}
