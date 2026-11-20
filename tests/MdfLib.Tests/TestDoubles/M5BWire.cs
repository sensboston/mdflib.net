// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using MdfLib.TestSupport;

namespace MdfLib.Tests.TestDoubles
{
    // Literal field operations on the independently committed corpus, not a MdfLib writer/codec.
    internal static class M5BWire
    {
        internal static byte[] Read(string id = "m5b-id1-dt410") { return FixtureCorpus.Read(id); }
        internal static bool Is3(byte[] bytes) { return bytes[8] == (byte)'3'; }
        internal static ulong UInt(byte[] bytes, int field, int width = 8)
        {
            ulong result = 0;
            for (int index = 0; index < width; index++) { result |= (ulong)bytes[field + index] << (index * 8); }
            return result;
        }
        internal static void Put(byte[] bytes, int field, ulong value, int width = 8) { M3Wire.Put(bytes, field, value, width); }
        internal static int Pointer(byte[] bytes, int field) { return (int)UInt(bytes, field, Is3(bytes) ? 4 : 8); }
        internal static int Dg(byte[] bytes) { return Pointer(bytes, Is3(bytes) ? 68 : 88); }
        internal static int[] Groups(byte[] bytes)
        {
            List<int> groups = new List<int>();
            int cg = Pointer(bytes, Dg(bytes) + (Is3(bytes) ? 8 : 32));
            while (cg != 0) { groups.Add(cg); cg = Pointer(bytes, cg + (Is3(bytes) ? 4 : 24)); }
            return groups.ToArray();
        }
        internal static int Cg(byte[] bytes, int group = 0) { return Groups(bytes)[group]; }
        internal static int Cn(byte[] bytes, int group = 0) { return Pointer(bytes, Cg(bytes, group) + (Is3(bytes) ? 8 : 32)); }
        internal static int Root(byte[] bytes) { return Pointer(bytes, Dg(bytes) + (Is3(bytes) ? 16 : 40)); }
        internal static int Raw(byte[] bytes) { return Root(bytes) + (Is3(bytes) ? 0 : 24); }
        internal static void Id(byte[] bytes, int group, ulong value) { Put(bytes, Cg(bytes, group) + (Is3(bytes) ? 16 : 72), value, Is3(bytes) ? 2 : 8); }
        internal static void Count(byte[] bytes, int group, ulong value) { Put(bytes, Cg(bytes, group) + (Is3(bytes) ? 22 : 80), value, Is3(bytes) ? 4 : 8); }
        internal static void Root(byte[] bytes, ulong value) { Put(bytes, Dg(bytes) + (Is3(bytes) ? 16 : 40), value, Is3(bytes) ? 4 : 8); }
        internal static void Width(byte[] bytes, int value) { Put(bytes, Dg(bytes) + (Is3(bytes) ? 22 : 56), (ulong)value, Is3(bytes) ? 2 : 1); }
        internal static void Size(byte[] bytes, int group, uint dataBytes)
        {
            Put(bytes, Cg(bytes, group) + (Is3(bytes) ? 20 : 96), dataBytes, Is3(bytes) ? 2 : 4);
        }
        internal static byte[] WithSegments(byte[] original, byte[][] payloads, bool[] zipped)
        {
            byte[] bytes = (byte[])original.Clone();
            ulong[] links = new ulong[payloads.Length + 1];
            ulong[] starts = new ulong[payloads.Length];
            ulong next = 0;
            for (int index = 0; index < payloads.Length; index++)
            {
                byte[] raw = payloads[index];
                links[index + 1] = (ulong)M5Wire.Add(ref bytes, zipped[index] ? "DZ" : "DT", new ulong[0], zipped[index] ? M5Wire.Zipped(raw) : raw);
                starts[index] = next;
                next += (ulong)raw.Length;
            }
            int list = M5Wire.Add(ref bytes, "DL", links, M5Wire.Offsets(starts));
            Root(bytes, (ulong)list);
            return bytes;
        }
    }
}
