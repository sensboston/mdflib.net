// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;

namespace MdfLib.Compression
{
    // original managed zlib (RFC 1950) encoder: one final fixed-Huffman deflate block (RFC 1951 BTYPE 01) with greedy
    // LZ77 matching over a 32 KiB window (3-byte hash chains, bounded chain walk). No BCL compressor, native code or tables
    // shared with the decoder. Output is deterministic for equal input.
    internal static class ZlibDeflater
    {
        private const int WindowSize = 32768;
        private const int MinimumMatch = 3;
        private const int MaximumMatch = 258;
        private const int HashBits = 15;
        private const int MaximumChain = 128;
        private const int CheckpointBytes = 65536;

        private static readonly int[] LengthBase = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
        private static readonly int[] LengthExtra = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
        private static readonly int[] DistanceBase = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577 };
        private static readonly int[] DistanceExtra = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };

        internal static byte[] Compress(byte[] data, int offset, int count, Action checkpoint)
        {
            if (data == null) { throw new ArgumentNullException(nameof(data)); }
            if (offset < 0 || count < 0 || offset > data.Length - count) { throw new ArgumentOutOfRangeException(nameof(count)); }
            // Fixed codes cost at most 9 bits per literal; header, block bits, end code and Adler-32 fit the constant.
            BitWriter output = new BitWriter(checked(count + count / 8 + 32));
            output.Bits(0x78, 8);
            output.Bits(0x9C, 8);
            output.Bits(1, 1); // BFINAL
            output.Bits(1, 2); // BTYPE 01: fixed Huffman codes.
            int[] head = new int[1 << HashBits];
            int[] previous = new int[WindowSize];
            for (int i = 0; i < head.Length; i++) { head[i] = -1; }
            int end = offset + count;
            int position = offset;
            int nextCheckpoint = offset + CheckpointBytes;
            while (position < end)
            {
                if (position >= nextCheckpoint)
                {
                    if (checkpoint != null) { checkpoint(); }
                    nextCheckpoint = position + CheckpointBytes;
                }
                int bestLength = 0, bestDistance = 0;
                if (end - position >= MinimumMatch)
                {
                    int hash = Hash(data, position);
                    int candidate = head[hash];
                    int limit = Math.Min(MaximumMatch, end - position);
                    for (int chain = 0; candidate >= 0 && position - candidate <= WindowSize && chain < MaximumChain; chain++)
                    {
                        if (data[candidate + bestLength] == data[position + bestLength])
                        {
                            int length = 0;
                            while (length < limit && data[candidate + length] == data[position + length]) { length++; }
                            if (length > bestLength)
                            {
                                bestLength = length;
                                bestDistance = position - candidate;
                                if (length == limit) { break; }
                            }
                        }
                        int older = previous[(candidate - offset) & (WindowSize - 1)];
                        if (older >= candidate) { break; } // Slot reused by a newer position: the chain left the window.
                        candidate = older;
                    }
                }
                if (bestLength >= MinimumMatch)
                {
                    Length(output, bestLength);
                    Distance(output, bestDistance);
                    int stop = position + bestLength;
                    for (; position < stop; position++) { Insert(data, position, offset, end, head, previous); }
                }
                else
                {
                    Literal(output, data[position]);
                    Insert(data, position, offset, end, head, previous);
                    position++;
                }
            }
            Symbol(output, 256);
            output.Align();
            uint adler = Adler32(data, offset, count);
            output.Bits((int)(adler >> 24), 8);
            output.Bits((int)((adler >> 16) & 0xFF), 8);
            output.Bits((int)((adler >> 8) & 0xFF), 8);
            output.Bits((int)(adler & 0xFF), 8);
            return output.ToArray();
        }

        internal static uint Adler32(byte[] data, int offset, int count)
        {
            uint a = 1, b = 0;
            int index = offset, remaining = count;
            while (remaining > 0)
            {
                int run = Math.Min(remaining, 5552); // Largest run whose sums cannot overflow 32 bits before reduction.
                remaining -= run;
                for (int i = 0; i < run; i++)
                {
                    a += data[index++];
                    b += a;
                }
                a %= 65521;
                b %= 65521;
            }
            return (b << 16) | a;
        }

        private static int Hash(byte[] data, int position)
        {
            int value = (data[position] << 16) | (data[position + 1] << 8) | data[position + 2];
            return (int)(((uint)value * 2654435761U) >> (32 - HashBits));
        }

        private static void Insert(byte[] data, int position, int offset, int end, int[] head, int[] previous)
        {
            if (end - position < MinimumMatch) { return; }
            int hash = Hash(data, position);
            previous[(position - offset) & (WindowSize - 1)] = head[hash];
            head[hash] = position;
        }

        private static void Literal(BitWriter output, byte value) { Symbol(output, value); }

        private static void Length(BitWriter output, int length)
        {
            int code = LengthBase.Length - 1;
            while (LengthBase[code] > length) { code--; }
            Symbol(output, 257 + code);
            if (LengthExtra[code] != 0) { output.Bits(length - LengthBase[code], LengthExtra[code]); }
        }

        private static void Distance(BitWriter output, int distance)
        {
            int code = DistanceBase.Length - 1;
            while (DistanceBase[code] > distance) { code--; }
            output.Huffman(code, 5);
            if (DistanceExtra[code] != 0) { output.Bits(distance - DistanceBase[code], DistanceExtra[code]); }
        }

        // RFC 1951 3.2.6 fixed literal/length code.
        private static void Symbol(BitWriter output, int symbol)
        {
            if (symbol < 144) { output.Huffman(0x30 + symbol, 8); }
            else if (symbol < 256) { output.Huffman(0x190 + symbol - 144, 9); }
            else if (symbol < 280) { output.Huffman(symbol - 256, 7); }
            else { output.Huffman(0xC0 + symbol - 280, 8); }
        }

        private sealed class BitWriter
        {
            private byte[] buffer;
            private int length;
            private uint pending;
            private int pendingBits;

            internal BitWriter(int capacity) { buffer = new byte[capacity]; }

            // Data elements are packed least-significant bit first.
            internal void Bits(int value, int count)
            {
                pending |= (uint)value << pendingBits;
                pendingBits += count;
                while (pendingBits >= 8)
                {
                    Put((byte)pending);
                    pending >>= 8;
                    pendingBits -= 8;
                }
            }

            // Huffman codes are packed most-significant bit first.
            internal void Huffman(int code, int count)
            {
                int reversed = 0;
                for (int i = 0; i < count; i++) { reversed = (reversed << 1) | ((code >> i) & 1); }
                Bits(reversed, count);
            }

            internal void Align()
            {
                if (pendingBits > 0) { Bits(0, 8 - pendingBits); }
            }

            internal byte[] ToArray()
            {
                byte[] result = new byte[length];
                Buffer.BlockCopy(buffer, 0, result, 0, length);
                return result;
            }

            private void Put(byte value)
            {
                if (length == buffer.Length) { Array.Resize(ref buffer, checked(buffer.Length * 2)); }
                buffer[length++] = value;
            }
        }
    }
}
