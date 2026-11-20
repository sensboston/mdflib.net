// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Original managed decoder v1 from RFC 1950/1951 format descriptions.
using System;
using System.IO;

namespace MdfLib.Compression
{
    internal static class ZlibInflater
    {
        private static readonly int[] LengthBase = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
        private static readonly int[] LengthExtra = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
        private static readonly int[] DistanceBase = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577 };
        private static readonly int[] DistanceExtra = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };
        private static readonly int[] CodeOrder = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };
        private static readonly Huffman FixedLiterals = FixedLiteralTree();
        private static readonly Huffman FixedDistances = new Huffman(Fill(32, 5), 0, 32, false, false);

        internal static byte[] Inflate(byte[] source, int expectedBytes, int maximumBytes, Action checkpoint)
        {
            if (source == null) { throw new ArgumentNullException(nameof(source)); }
            if (checkpoint == null) { throw new ArgumentNullException(nameof(checkpoint)); }
            if (expectedBytes < 0) { throw new ArgumentOutOfRangeException(nameof(expectedBytes)); }
            if (maximumBytes <= 0) { throw new ArgumentOutOfRangeException(nameof(maximumBytes)); }
            checkpoint();
            if (source.Length > maximumBytes || expectedBytes > maximumBytes)
            {
                throw new MdfResourceLimitException(nameof(MdfReaderOptions.MaximumReadBufferBytes), (ulong)maximumBytes,
                    (ulong)Math.Max(source.Length, expectedBytes), 0);
            }
            if (source.Length < 6) { throw Bad("Truncated zlib wrapper."); }
            Bits input = new Bits(source);
            int cmf = input.Byte();
            int flags = input.Byte();
            if ((cmf & 15) != 8 || (cmf >> 4) > 7 || ((cmf << 8) + flags) % 31 != 0)
            {
                throw Bad("Invalid zlib method, window or header check.");
            }
            if ((flags & 32) != 0) { throw new NotSupportedException("Preset zlib dictionaries are not supported."); }
            checkpoint();
            byte[] output = new byte[expectedBytes];
            int position = 0;
            int window = 1 << ((cmf >> 4) + 8);
            bool final;
            do
            {
                checkpoint();
                final = input.Read(1) != 0;
                int mode = input.Read(2);
                if (mode == 0)
                {
                    input.Align();
                    int length = input.Read(16);
                    if (input.Read(16) != (length ^ 0xffff)) { throw Bad("Stored DEFLATE LEN/NLEN mismatch."); }
                    RequireRoom(output, position, length);
                    for (int index = 0; index < length; index++)
                    {
                        if ((index & 4095) == 0) { checkpoint(); }
                        output[position++] = (byte)input.Read(8);
                    }
                }
                else if (mode == 1 || mode == 2)
                {
                    Huffman literals = FixedLiterals;
                    Huffman distances = FixedDistances;
                    if (mode == 2) { Dynamic(input, out literals, out distances); }
                    while (true)
                    {
                        checkpoint();
                        int symbol = literals.Decode(input);
                        if (symbol == 256) { break; }
                        if (symbol < 256)
                        {
                            RequireRoom(output, position, 1);
                            output[position++] = (byte)symbol;
                        }
                        else
                        {
                            if (symbol > 285) { throw Bad("Reserved DEFLATE length symbol."); }
                            int lengthIndex = symbol - 257;
                            int length = LengthBase[lengthIndex] + input.Read(LengthExtra[lengthIndex]);
                            int distanceSymbol = distances.Decode(input);
                            if (distanceSymbol >= 30) { throw Bad("Reserved DEFLATE distance symbol."); }
                            int distance = DistanceBase[distanceSymbol] + input.Read(DistanceExtra[distanceSymbol]);
                            if (distance > position || distance > window) { throw Bad("DEFLATE back-reference exceeds the produced window."); }
                            RequireRoom(output, position, length);
                            for (int index = 0; index < length; index++, position++)
                            {
                                output[position] = output[position - distance]; // Overlapping copies are intentional.
                            }
                        }
                    }
                }
                else { throw Bad("Reserved DEFLATE block mode."); }
            }
            while (!final);
            input.Align();
            uint declared = ((uint)input.Byte() << 24) | ((uint)input.Byte() << 16) | ((uint)input.Byte() << 8) | (uint)input.Byte();
            if (position != output.Length) { throw Bad("DEFLATE output is shorter than the declared exact size."); }
            if (!input.AtEnd) { throw Bad("Extra bytes or a second member follow the zlib stream."); }
            uint a = 1;
            uint b = 0;
            for (int index = 0; index < output.Length; index++)
            {
                if ((index & 4095) == 0) { checkpoint(); }
                a = (a + output[index]) % 65521;
                b = (b + a) % 65521;
            }
            if (declared != ((b << 16) | a)) { throw Bad("zlib Adler-32 checksum mismatch."); }
            checkpoint();
            return output;
        }

        private static void RequireRoom(byte[] output, int position, int count)
        {
            if (count > output.Length - position) { throw Bad("DEFLATE output exceeds the declared exact size."); }
        }

        private static void Dynamic(Bits input, out Huffman literals, out Huffman distances)
        {
            int literalCount = 257 + input.Read(5);
            int distanceCount = 1 + input.Read(5);
            int codeCount = 4 + input.Read(4);
            if (literalCount > 286) { throw Bad("Reserved DEFLATE literal alphabet size."); }
            int[] codes = new int[19];
            for (int index = 0; index < codeCount; index++) { codes[CodeOrder[index]] = input.Read(3); }
            Huffman codeTree = new Huffman(codes, 0, codes.Length, false, false);
            int[] lengths = new int[literalCount + distanceCount];
            int written = 0;
            while (written < lengths.Length)
            {
                int value = codeTree.Decode(input);
                if (value <= 15) { lengths[written++] = value; }
                else
                {
                    int repeat;
                    int previous = 0;
                    if (value == 16)
                    {
                        if (written == 0) { throw Bad("DEFLATE repeat has no previous length."); }
                        previous = lengths[written - 1];
                        repeat = 3 + input.Read(2);
                    }
                    else if (value == 17) { repeat = 3 + input.Read(3); }
                    else { repeat = 11 + input.Read(7); }
                    if (repeat > lengths.Length - written) { throw Bad("DEFLATE code-length repeat overruns the alphabets."); }
                    for (int index = 0; index < repeat; index++) { lengths[written++] = previous; }
                }
            }
            if (lengths[256] == 0) { throw Bad("DEFLATE literal tree has no end-of-block symbol."); }
            literals = new Huffman(lengths, 0, literalCount, false, true);
            distances = new Huffman(lengths, literalCount, distanceCount, true, true);
        }

        private static Huffman FixedLiteralTree()
        {
            int[] lengths = new int[288];
            for (int symbol = 0; symbol < lengths.Length; symbol++)
            {
                lengths[symbol] = symbol < 144 ? 8 : symbol < 256 ? 9 : symbol < 280 ? 7 : 8;
            }
            return new Huffman(lengths, 0, lengths.Length, false, false);
        }
        private static int[] Fill(int count, int value)
        {
            int[] result = new int[count];
            for (int index = 0; index < count; index++) { result[index] = value; }
            return result;
        }
        private static InvalidDataException Bad(string message) { return new InvalidDataException(message); }

        private sealed class Bits
        {
            private readonly byte[] source;
            private int position;
            private uint pending;
            private int remaining;
            internal Bits(byte[] source) { this.source = source; }
            internal bool AtEnd => position == source.Length;
            internal int Byte()
            {
                if (position == source.Length) { throw Bad("Truncated zlib/DEFLATE input."); }
                return source[position++];
            }
            internal int Read(int count)
            {
                while (remaining < count)
                {
                    pending |= (uint)Byte() << remaining;
                    remaining += 8;
                }
                int value = (int)(pending & ((1U << count) - 1));
                pending >>= count;
                remaining -= count;
                return value;
            }
            internal void Align() { pending = 0; remaining = 0; }
        }

        private sealed class Huffman
        {
            private readonly int[] counts = new int[16];
            private readonly int[] first = new int[16];
            private readonly int[] starts = new int[16];
            private readonly int[] symbols;
            internal Huffman(int[] lengths, int offset, int count, bool allowEmpty, bool allowSingle)
            {
                int total = 0;
                int maximum = 0;
                for (int index = 0; index < count; index++)
                {
                    int bits = lengths[offset + index];
                    if (bits != 0) { counts[bits]++; total++; maximum = Math.Max(maximum, bits); }
                }
                int left = 1;
                for (int bits = 1; bits <= 15; bits++)
                {
                    left = (left << 1) - counts[bits];
                    if (left < 0) { throw Bad("Oversubscribed DEFLATE Huffman tree."); }
                }
                if (total == 0 ? !allowEmpty : left != 0 && !(allowSingle && total == 1 && maximum == 1))
                {
                    throw Bad("Incomplete DEFLATE Huffman tree.");
                }
                symbols = new int[total];
                int code = 0;
                int next = 0;
                int[] cursors = new int[16];
                for (int bits = 1; bits <= 15; bits++)
                {
                    code = (code + counts[bits - 1]) << 1;
                    first[bits] = code;
                    starts[bits] = cursors[bits] = next;
                    next += counts[bits];
                }
                for (int symbol = 0; symbol < count; symbol++)
                {
                    int bits = lengths[offset + symbol];
                    if (bits != 0) { symbols[cursors[bits]++] = symbol; }
                }
            }
            internal int Decode(Bits input)
            {
                int code = 0;
                for (int bits = 1; bits <= 15; bits++)
                {
                    code = (code << 1) | input.Read(1);
                    int index = code - first[bits];
                    if (index >= 0 && index < counts[bits]) { return symbols[starts[bits] + index]; }
                }
                throw Bad("Invalid or absent DEFLATE Huffman symbol.");
            }
        }
    }
}
