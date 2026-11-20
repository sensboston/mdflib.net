// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using MdfLib.TestSupport;

namespace MdfLib.Tests.TestDoubles
{
    // Literal navigation/mutation of the frozen pre-parser m5e-structure410 file; never the library writer/parser.
    // Top-level: time0, prefix1, packet2{counter, nested{signed, real, linear, packed}, matrix, last}, after3, packet4{...}.
    internal static class M5EWire
    {
        internal const int DataBytes = 74;
        internal static byte[] Read(string id = "m5e-structure410") { return FixtureCorpus.Read(id); }
        internal static int Top(byte[] bytes, int index) { return M5DWire.Cn(bytes, index); }
        internal static int Child(byte[] bytes, int parent, int index)
        {
            int cn = (int)M3Wire.U64(bytes, M3Wire.Link(parent, 1));
            while (index-- > 0) { cn = (int)M3Wire.U64(bytes, M3Wire.Link(cn, 0)); }
            return cn;
        }
        internal static int Packet(byte[] bytes) { return Top(bytes, 2); }
        internal static int Nested(byte[] bytes) { return Child(bytes, Packet(bytes), 1); }
        internal static int Payload(byte[] bytes, int cn) { return M3Wire.Payload(bytes, cn); }
        internal static void Set(byte[] bytes, int cn, int field, ulong value, int width)
        {
            M3Wire.Put(bytes, Payload(bytes, cn) + field, value, width);
        }
        internal static void ByteOffset(byte[] bytes, int cn, uint value) { Set(bytes, cn, 4, value, 4); }
        internal static void BitCount(byte[] bytes, int cn, uint value) { Set(bytes, cn, 8, value, 4); }
        internal static void Flags(byte[] bytes, int cn, uint value) { Set(bytes, cn, 12, value, 4); }
        internal static void Link(byte[] bytes, int cn, int link, ulong target) { M3Wire.Put(bytes, M3Wire.Link(cn, link), target); }

        // Appends one CN without text/source/conversion links: role, data type, byte offset and bit count only.
        internal static int AddCn(ref byte[] bytes, ulong next, ulong composition, byte dataType, uint byteOffset, uint bits, byte role = 0)
        {
            byte[] payload = new byte[72];
            payload[0] = role;
            payload[2] = dataType;
            M3Wire.Put(payload, 4, byteOffset, 4);
            M3Wire.Put(payload, 8, bits, 4);
            return M5Wire.Add(ref bytes, "CN", new[] { next, composition, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL }, payload);
        }
    }
}
