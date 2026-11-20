// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Linq;
using MdfLib.TestSupport;

namespace MdfLib.Tests.TestDoubles
{
    // Literal wire mutations of the immutable independent corpus, never a MdfLib writer/decoder.
    internal static class M5CWire
    {
        internal static byte[] Read(string id = "m5c-ld-equal420") { return FixtureCorpus.Read(id); }
        internal static int Dg(byte[] bytes, int index = 0)
        {
            int dg = (int)M3Wire.U64(bytes, M3Wire.Link(64, 0));
            while (index-- > 0) { dg = (int)M3Wire.U64(bytes, M3Wire.Link(dg, 0)); }
            return dg;
        }
        internal static int Cg(byte[] bytes, int index = 0) { return (int)M3Wire.U64(bytes, M3Wire.Link(Dg(bytes, index), 1)); }
        internal static int Root(byte[] bytes, int index = 0) { return (int)M3Wire.U64(bytes, M3Wire.Link(Dg(bytes, index), 2)); }
        internal static int Leaf(byte[] bytes, int index = 0, bool invalidation = false, int dg = 0)
        {
            int list = Root(bytes, dg);
            int count = (int)M3Wire.U64(bytes, list + 16);
            int pairs = (count - 1) / ((bytes[M3Wire.Payload(bytes, list) + 3] & 0x80) == 0 ? 1 : 2);
            return (int)M3Wire.U64(bytes, M3Wire.Link(list, 1 + index + (invalidation ? pairs : 0)));
        }
        internal static int CloneCg(ref byte[] bytes, int dgIndex, ushort flags, int linkCount)
        {
            int old = Cg(bytes, dgIndex);
            byte[] payload = bytes.Skip(M3Wire.Payload(bytes, old)).Take(32).ToArray();
            M3Wire.Put(payload, 16, flags, 2);
            ulong[] links = new ulong[linkCount];
            for (int index = 0; index < Math.Min(linkCount, (int)M3Wire.U64(bytes, old + 16)); index++)
            { links[index] = M3Wire.U64(bytes, M3Wire.Link(old, index)); }
            int cg = M5Wire.Add(ref bytes, "CG", links, payload);
            M3Wire.Put(bytes, M3Wire.Link(Dg(bytes, dgIndex), 1), (ulong)cg);
            return cg;
        }
        internal static int List(ref byte[] bytes, ulong[] values, ulong[] invalidations, ulong[] offsets, ulong? equal = null, ulong next = 0)
        {
            byte[] fields = equal.HasValue ? new byte[16] : M5Wire.Offsets(offsets);
            M3Wire.Put(fields, 0, (invalidations == null ? 0UL : 0x80000000UL) | (equal.HasValue ? 1UL : 0UL), 4);
            M3Wire.Put(fields, 4, (ulong)values.Length, 4);
            if (equal.HasValue) { M3Wire.Put(fields, 8, equal.Value); }
            return M5Wire.Add(ref bytes, "LD", new[] { next }.Concat(values).Concat(invalidations ?? new ulong[0]).ToArray(), fields);
        }
        internal static void SetRoot(byte[] bytes, int root, int dg = 0) { M3Wire.Put(bytes, M3Wire.Link(Dg(bytes, dg), 2), (ulong)root); }
        internal static byte[] Values()
        {
            byte[] bytes = Read("m5c-dt420");
            int start = Root(bytes) + 24;
            return Enumerable.Range(0, 7).SelectMany(row => bytes.Skip(start + row * 24).Take(22)).ToArray();
        }
        internal static byte[] Invalidations()
        {
            byte[] bytes = Read("m5c-dt420");
            int start = Root(bytes) + 24;
            return Enumerable.Range(0, 7).SelectMany(row => bytes.Skip(start + row * 24 + 22).Take(2)).ToArray();
        }
        internal static byte[] Zipped(byte[] raw, string role, ulong? declared = null, uint parameter = 0)
        {
            byte[] transposed = (byte[])raw.Clone();
            if (parameter != 0)
            {
                int rows = (int)((ulong)raw.Length / parameter);
                int matrix = (int)((ulong)rows * parameter);
                for (int index = 0; index < matrix; index++)
                { transposed[index] = raw[(index % rows) * (int)parameter + index / rows]; }
            }
            byte[] payload = M5Wire.Zipped(transposed, declared);
            payload[0] = (byte)role[0]; payload[1] = (byte)role[1];
            if (parameter != 0) { payload[2] = 1; M3Wire.Put(payload, 4, parameter, 4); }
            return payload;
        }
    }
}
