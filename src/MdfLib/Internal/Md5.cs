// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Original managed MD5 (RFC 1321) for MDF 4 attachment checksums only. The BCL implementation is deliberately not used:
// on modern.NET it delegates to the operating system's native cryptography provider.
using System;

namespace MdfLib.Internal
{
    internal static class Md5
    {
        internal const int HashBytes = 16;

        private static readonly uint[] Constants =
        {
            0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee, 0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501,
            0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be, 0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821,
            0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa, 0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8,
            0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed, 0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a,
            0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c, 0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70,
            0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05, 0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665,
            0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039, 0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1,
            0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1, 0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391
        };

        private static readonly int[] Shifts =
        {
            7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
            5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
            4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
            6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21
        };

        /// <summary>Hashes a bounded in-memory buffer; the checkpoint runs between 64 KiB spans of blocks.</summary>
        internal static byte[] Hash(byte[] data, int count, Action checkpoint)
        {
            if (data == null) { throw new ArgumentNullException(nameof(data)); }
            if (count < 0 || count > data.Length) { throw new ArgumentOutOfRangeException(nameof(count)); }
            uint a0 = 0x67452301, b0 = 0xefcdab89, c0 = 0x98badcfe, d0 = 0x10325476;
            uint[] words = new uint[16];
            byte[] tail = new byte[128];
            int full = count / 64;
            for (int block = 0; block < full; block++)
            {
                if (checkpoint != null && (block & 1023) == 0) { checkpoint(); }
                Load(data, block * 64, words);
                Round(words, ref a0, ref b0, ref c0, ref d0);
            }
            int remaining = count - full * 64;
            Array.Copy(data, full * 64, tail, 0, remaining);
            tail[remaining] = 0x80;
            int tailBytes = remaining < 56 ? 64 : 128;
            ulong bits = (ulong)count * 8;
            for (int i = 0; i < 8; i++) { tail[tailBytes - 8 + i] = (byte)(bits >> (8 * i)); }
            for (int offset = 0; offset < tailBytes; offset += 64)
            {
                Load(tail, offset, words);
                Round(words, ref a0, ref b0, ref c0, ref d0);
            }
            byte[] result = new byte[HashBytes];
            Store(result, 0, a0);
            Store(result, 4, b0);
            Store(result, 8, c0);
            Store(result, 12, d0);
            return result;
        }

        private static void Round(uint[] m, ref uint a0, ref uint b0, ref uint c0, ref uint d0)
        {
            uint a = a0, b = b0, c = c0, d = d0;
            for (int i = 0; i < 64; i++)
            {
                uint f;
                int g;
                if (i < 16) { f = (b & c) | (~b & d); g = i; }
                else if (i < 32) { f = (d & b) | (~d & c); g = (5 * i + 1) & 15; }
                else if (i < 48) { f = b ^ c ^ d; g = (3 * i + 5) & 15; }
                else { f = c ^ (b | ~d); g = (7 * i) & 15; }
                uint sum = unchecked(a + f + Constants[i] + m[g]);
                a = d;
                d = c;
                c = b;
                b = unchecked(b + ((sum << Shifts[i]) | (sum >> (32 - Shifts[i]))));
            }
            unchecked
            {
                a0 += a;
                b0 += b;
                c0 += c;
                d0 += d;
            }
        }

        private static void Load(byte[] source, int offset, uint[] words)
        {
            for (int i = 0; i < 16; i++)
            {
                int at = offset + 4 * i;
                words[i] = source[at] | (uint)source[at + 1] << 8 | (uint)source[at + 2] << 16 | (uint)source[at + 3] << 24;
            }
        }

        private static void Store(byte[] target, int offset, uint value)
        {
            for (int i = 0; i < 4; i++) { target[offset + i] = (byte)(value >> (8 * i)); }
        }
    }
}
