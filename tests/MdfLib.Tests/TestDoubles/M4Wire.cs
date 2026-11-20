// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using MdfLib.TestSupport;

namespace MdfLib.Tests.TestDoubles
{
    // Literal mutations of pre-parser Python corpus bytes; never uses a MdfLib writer/format codec.
    internal static class M4Wire
    {
        internal const int Dg = 272;
        internal const int Cg = 300;
        internal const int Cn = 330;
        internal const int SecondCn = 558;
        internal const int LastCn = 3775;
        internal const int IdentityCc = 4003;
        internal const int LinearCc = 4049;
        internal const int Ecu = 4111;
        internal const int Raw = 4311;
        internal const int Dg2 = 4751;
        internal const int Cg2 = 4775;
        internal const int HighCn = 4801;
        internal const int Can = 5029;
        internal const int Raw2 = 5157;
        internal const int ScalarRaw = Cn + 228;
        internal static byte[] Read(bool bigEndian = false) { return FixtureCorpus.Read(bigEndian ? "m4-packed-be330" : "m4-packed-le330"); }
        internal static void Put(byte[] bytes, int field, ulong value, int width = 4) { M3Wire.Put(bytes, field, value, width); }
        internal static void FirstOnly(byte[] bytes)
        {
            Put(bytes, 80, 1, 2);
            Put(bytes, Dg + 4, 0);
        }

        internal static byte[] ScalarTemplate()
        {
            byte[] original = Read();
            byte[] bytes = new byte[ScalarRaw + 9];
            Array.Copy(original, bytes, ScalarRaw);
            FirstOnly(bytes);
            Array.Clear(bytes, 100, 32); // Author contained Latin-1; other HD fixed fields are ASCII.
            Array.Clear(bytes, Cn + 4, 224);
            bytes[Cn + 26] = (byte)'S';
            Put(bytes, Dg + 16, ScalarRaw);
            Put(bytes, Cg + 12, 0);
            Put(bytes, Cg + 18, 1, 2);
            Put(bytes, Cg + 20, 9, 2);
            Put(bytes, Cg + 22, 1);
            Put(bytes, Cn + 188, 8, 2);
            Put(bytes, Cn + 190, 13, 2);
            return bytes;
        }

        internal static int AppendTx(ref byte[] bytes, byte[] payload)
        {
            int address = bytes.Length; // MDF 3 has no eight-byte alignment rule.
            Array.Resize(ref bytes, checked(address + 4 + payload.Length));
            bytes[address] = (byte)'T'; bytes[address + 1] = (byte)'X';
            Put(bytes, address + 2, checked((ushort)(4 + payload.Length)), 2);
            Array.Copy(payload, 0, bytes, address + 4, payload.Length);
            return address;
        }
    }
}
