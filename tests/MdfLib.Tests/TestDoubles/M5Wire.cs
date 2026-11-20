// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Linq;
using MdfLib.TestSupport;

namespace MdfLib.Tests.TestDoubles
{
    // Literal mutation helpers over the independently committed pre-parser corpus, not a library writer.
    internal static class M5Wire
    {
        internal const int Dg = 168;
        internal const int Cg = 232;
        internal static byte[] Read(string id = "m5-dz410") { return FixtureCorpus.Read(id); }
        internal static int Root(byte[] bytes) { return (int)M3Wire.U64(bytes, M3Wire.Link(Dg, 2)); }
        internal static int List(byte[] bytes)
        {
            int root = Root(bytes);
            return bytes[root + 2] == (byte)'H' ? (int)M3Wire.U64(bytes, root + 24) : root;
        }
        internal static int Add(ref byte[] bytes, string type, ulong[] links, byte[] payload)
        {
            byte[] data = new byte[links.Length * 8 + payload.Length];
            for (int index = 0; index < links.Length; index++) { M3Wire.Put(data, index * 8, links[index]); }
            Array.Copy(payload, 0, data, links.Length * 8, payload.Length);
            int at = M3Wire.AppendBlock(ref bytes, type, data);
            M3Wire.Put(bytes, at + 16, (ulong)links.Length);
            return at;
        }
        internal static byte[] Header(string type, ulong dataLength)
        {
            byte[] bytes = new byte[24];
            bytes[0] = bytes[1] = (byte)'#'; bytes[2] = (byte)type[0]; bytes[3] = (byte)type[1];
            M3Wire.Put(bytes, 8, dataLength + 24);
            return bytes;
        }
        internal static byte[] FirstRecords(int count)
        {
            byte[] bytes = Read();
            return bytes.Skip(Root(bytes) + 48 + 7).Take(count * 23).ToArray(); // Literal 7-byte stored-zlib prefix.
        }
        internal static byte[] Zipped(byte[] raw, ulong? declaredLength = null)
        {
            byte[] result = new byte[24 + 7 + raw.Length + 4];
            result[0] = (byte)'D'; result[1] = (byte)'T';
            M3Wire.Put(result, 8, declaredLength ?? (ulong)raw.Length);
            M3Wire.Put(result, 16, (ulong)raw.Length + 11);
            result[24] = 0x78; result[25] = 1; result[26] = 1;
            M3Wire.Put(result, 27, (ulong)raw.Length, 2); M3Wire.Put(result, 29, (ulong)(raw.Length ^ 65535), 2);
            Array.Copy(raw, 0, result, 31, raw.Length);
            uint a = 1, b = 0;
            foreach (byte item in raw) { a = (a + item) % 65521; b = (b + a) % 65521; }
            uint sum = (b << 16) | a;
            for (int index = 0; index < 4; index++) { result[result.Length - 4 + index] = (byte)(sum >> (24 - index * 8)); }
            return result;
        }
        internal static byte[] Offsets(params ulong[] offsets)
        {
            byte[] bytes = new byte[8 + 8 * offsets.Length];
            M3Wire.Put(bytes, 4, (ulong)offsets.Length, 4);
            for (int index = 0; index < offsets.Length; index++) { M3Wire.Put(bytes, 8 + index * 8, offsets[index]); }
            return bytes;
        }
    }
}
