// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Linq;
using MdfLib.TestSupport;

namespace MdfLib.Tests.TestDoubles
{
    // Literal mutations of independently frozen files, never the library's writer/parser.
    internal static class M5DWire
    {
        internal static byte[] Read(string id = "m5d-arrays410") { return FixtureCorpus.Read(id); }
        internal static int Dg(byte[] bytes) { return (int)M3Wire.U64(bytes, M3Wire.Link(64, 0)); }
        internal static int Cg(byte[] bytes) { return (int)M3Wire.U64(bytes, M3Wire.Link(Dg(bytes), 1)); }
        internal static int Root(byte[] bytes) { return (int)M3Wire.U64(bytes, M3Wire.Link(Dg(bytes), 2)); }
        internal static int Cn(byte[] bytes, int index = 2)
        {
            int cn = (int)M3Wire.U64(bytes, M3Wire.Link(Cg(bytes), 1));
            while (index-- > 0) { cn = (int)M3Wire.U64(bytes, M3Wire.Link(cn, 0)); }
            return cn;
        }
        internal static int Ca(byte[] bytes, int index = 2) { return (int)M3Wire.U64(bytes, M3Wire.Link(Cn(bytes, index), 1)); }
        internal static int Array(ref byte[] bytes, ulong[] shape, int stride = 1, ulong next = 0, uint flags = 0, int links = 1,
            byte type = 0, int axisValues = 0, ulong fill = 0)
        {
            byte[] data = new byte[16 + shape.Length * 8 + axisValues * 8];
            data[0] = type;
            M3Wire.Put(data, 2, (ulong)shape.Length, 2);
            M3Wire.Put(data, 4, flags, 4);
            M3Wire.Put(data, 8, unchecked((uint)stride), 4);
            for (int i = 0; i < shape.Length; i++) { M3Wire.Put(data, 16 + i * 8, shape[i]); }
            ulong[] targets = new ulong[links];
            for (int i = 1; i < links; i++) { targets[i] = fill; }
            for (int i = 0; i < axisValues; i++) { M3Wire.Put(data, 16 + shape.Length * 8 + i * 8, (ulong)BitConverter.DoubleToInt64Bits(i + 0.5)); }
            if (links != 0) { targets[0] = next; }
            int address = M5Wire.Add(ref bytes, "CA", targets, data);
            M3Wire.Put(bytes, M3Wire.Link(Cn(bytes), 1), (ulong)address);
            return address;
        }
        internal static byte[] Records(byte[] bytes)
        {
            int cg = M3Wire.Payload(bytes, Cg(bytes));
            int size = checked((int)(M3Wire.U64(bytes, cg + 24) & uint.MaxValue) + (int)(M3Wire.U64(bytes, cg + 24) >> 32));
            return bytes.Skip(Root(bytes) + 24).Take(5 * size).ToArray();
        }
    }
}
