// Copyright 2021 Ingemar Hedvall
// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// HD wire fields adapted from the pinned MIT upstream; linked metadata is deliberately lazy.
using System.Threading;
using MdfLib.IO;

namespace MdfLib.Formats.Mdf4
{
    internal static class HeaderCodec
    {
        internal static void Read(BlockResolver resolver, MdfFileHeader snapshot, CancellationToken token)
        {
            BlockHeader block = resolver.Resolve(64, "##HD", 104, 0, token);
            if (block.Links.Count != 6)
            {
                throw new MdfFormatException("MDF 4 HD requires exactly six links.", "MDF006", 64, "##HD");
            }
            byte[] data = resolver.ReadPayload(block, 32, token);
            snapshot.RawStartTimeNanoseconds = EndianBitConverter.ReadUInt64(data, 0, ByteOrder.LittleEndian);
            snapshot.UtcOffsetMinutes = EndianBitConverter.ReadInt16(data, 8, ByteOrder.LittleEndian);
            snapshot.DaylightSavingOffsetMinutes = EndianBitConverter.ReadInt16(data, 10, ByteOrder.LittleEndian);
            snapshot.TimeFlags = data[12];
            snapshot.TimeQuality = data[13];
            snapshot.Flags = data[14];
            // The HD flags say which of the start angle and start distance the file stores; the other stays unset.
            snapshot.StartAngle = (data[14] & 0x01) != 0 ? (double?)EndianBitConverter.ReadDouble(data, 16, ByteOrder.LittleEndian) : null;
            snapshot.StartDistance = (data[14] & 0x02) != 0 ? (double?)EndianBitConverter.ReadDouble(data, 24, ByteOrder.LittleEndian) : null;
        }
    }
}
