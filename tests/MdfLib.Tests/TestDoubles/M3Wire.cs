// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Text;
using MdfLib.TestSupport;

namespace MdfLib.Tests.TestDoubles
{
    // Mutates original Python-produced bytes, never calls a MdfLib writer/format codec.
    internal static class M3Wire
    {
        internal const int Dg = 168;
        internal const int Cg = 232;
        internal const int Cn = 336;
        internal const int LastCn = 1920;
        internal const int Si = 2576;
        internal const int Cc = 2632;
        internal const int Dt = 2768;
        internal const int Dg2 = 3032;
        internal const int Cg2 = 3096;
        internal static byte[] Read() { return FixtureCorpus.Read("m3-packed410"); }
        internal static int Link(int block, int index) { return block + 24 + index * 8; }
        internal static int Payload(byte[] bytes, int block) { return checked(block + 24 + (int)U64(bytes, block + 16) * 8); }
        internal static ulong U64(byte[] bytes, int offset)
        {
            ulong value = 0;
            for (int index = 7; index >= 0; index--) { value = (value << 8) | bytes[offset + index]; }
            return value;
        }
        internal static void Put(byte[] bytes, int offset, ulong value, int width = 8)
        {
            for (int index = 0; index < width; index++) { bytes[offset + index] = (byte)value; value >>= 8; }
        }
        internal static int AppendText(ref byte[] bytes, string value, bool xml = true)
        {
            return AppendBlock(ref bytes, xml ? "MD" : "TX", Encoding.UTF8.GetBytes(value));
        }
        internal static int AppendBlock(ref byte[] bytes, string type, byte[] payload)
        {
            int address = (bytes.Length + 7) & ~7;
            Array.Resize(ref bytes, address + 24 + payload.Length);
            bytes[address] = bytes[address + 1] = (byte)'#';
            bytes[address + 2] = (byte)type[0]; bytes[address + 3] = (byte)type[1];
            Put(bytes, address + 8, (ulong)payload.Length + 24);
            Array.Copy(payload, 0, bytes, address + 24, payload.Length);
            return address;
        }
    }
}
