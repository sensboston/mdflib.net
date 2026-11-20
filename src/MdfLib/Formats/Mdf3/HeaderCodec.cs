// Copyright 2021 Ingemar Hedvall
// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// HD wire fields adapted from the pinned MIT upstream, with the LE metadata policy documented as D-001.
using System.Threading;
using MdfLib.IO;

namespace MdfLib.Formats.Mdf3
{
    internal static class HeaderCodec
    {
        internal static void Read(BlockResolver resolver, IdentificationBlock identification, MdfFileHeader snapshot, CancellationToken token)
        {
            BlockHeader block = resolver.Resolve(64, "HD", MinimumSize(identification.Version), 3, token);
            bool extended = block.Length >= ExtendedSize; // start time fields are size-driven (pre-3.20 HD may be 164 bytes).
            byte[] data = resolver.ReadPayload(block, extended ? ExtendedSize - 16 : ShortSize - 16, token); // Offsets below are relative to HD byte 16.
            snapshot.CodePage = identification.CodePage;
            snapshot.DataGroupCount = EndianBitConverter.ReadUInt16(data, 0, ByteOrder.LittleEndian);
            snapshot.DateText = FixedText.Read(data, 2, 10, 20127, 82, "HD");
            snapshot.TimeText = FixedText.Read(data, 12, 8, 20127, 92, "HD");
            snapshot.Author = Text(data, 20, identification.CodePage);
            snapshot.Organization = Text(data, 52, identification.CodePage);
            snapshot.Project = Text(data, 84, identification.CodePage);
            snapshot.Subject = Text(data, 116, identification.CodePage);
            if (!extended)
            {
                snapshot.TimerIdentification = string.Empty;
                return;
            }
            snapshot.RawStartTimeNanoseconds = EndianBitConverter.ReadUInt64(data, 148, ByteOrder.LittleEndian);
            snapshot.UtcOffsetMinutes = EndianBitConverter.ReadInt16(data, 156, ByteOrder.LittleEndian) * 60;
            snapshot.TimeQuality = EndianBitConverter.ReadUInt16(data, 158, ByteOrder.LittleEndian);
            snapshot.TimerIdentification = FixedText.Read(data, 160, 32, identification.CodePage, 240, "HD");
        }

        internal const int ShortSize = 164;
        internal const int ExtendedSize = 208;

        /// <summary>The HD minimum of a revision: 164 bytes before 3.20, 208 bytes (with start time fields) from 3.20.</summary>
        internal static int MinimumSize(int version)
        {
            return version >= 320 ? ExtendedSize : ShortSize;
        }

        private static string Text(byte[] bytes, int start, int codePage)
        {
            // The engine keeps the leading white space; the facade applies the wrapper's trim (FacadeFormat.Trim).
            return FixedText.Read(bytes, start, 32, codePage, (ulong)(80 + start), "HD");
        }
    }
}
