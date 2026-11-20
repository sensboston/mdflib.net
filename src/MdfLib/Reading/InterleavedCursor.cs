// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Globalization;
using System.Threading;
using MdfLib.Formats;
using MdfLib.Internal;
using MdfLib.IO;

namespace MdfLib.Reading
{
    // O(CG) scratch and two bounded passes; no whole-DG sorting, row-offset index or persistent scan cache.
    internal sealed class InterleavedCursor
    {
        private readonly MdfReader reader;
        private readonly BinaryInput input;
        private readonly RecordLayout record;
        private readonly InterleavedLayout layout;
        private readonly Mdf4DataCursor cursor;
        private readonly CancellationToken token;
        private readonly ulong[] counts;
        private readonly byte[] idBytes;
        private readonly ulong total;
        private ulong position;
        private uint lastLength;

        internal InterleavedCursor(MdfReader reader, BinaryInput input, RecordLayout record, Mdf4DataCursor cursor, CancellationToken token)
        {
            this.reader = reader;
            this.input = input;
            this.record = record;
            layout = record.Interleaved;
            this.cursor = cursor;
            this.token = token;
            ReadBudget.Check(nameof(MdfReaderOptions.MaximumReadBufferBytes), (ulong)reader.Limits.MaximumReadBufferBytes,
                0, 8UL * (ulong)layout.Groups.Count, layout.BlockAddress);
            counts = new ulong[layout.Groups.Count];
            idBytes = new byte[8];
            // a DG with VLSD CG members has no metadata-derived extent; its data root's logical length rules.
            total = layout.HasVariableRecords ? cursor.Total : layout.TotalBytes;
        }

        // UInt32 payload length of the frame most recently returned by NextFrame for a VLSD CG member.
        internal uint LastLength => lastLength;

        // lookup scans: frame-by-frame over the already validated stream; Restart rewinds to the DG start.
        internal bool NextFrame(out int member, out ulong body, out ulong size)
        {
            if (!Next(out member, out body)) { size = 0; return false; }
            size = position - body - (ulong)layout.SuffixBytes;
            return true;
        }

        internal void Restart()
        {
            Array.Clear(counts, 0, counts.Length);
            position = 0;
        }

        internal void Validate()
        {
            int member;
            ulong body;
            while (Next(out member, out body)) { }
            for (int index = 0; index < counts.Length; index++)
            {
                Checkpoint();
                if (counts[index] != layout.Groups[index].SampleCount)
                {
                    throw Error(position, "Observed record counts do not match the declared channel-group cycles.");
                }
            }
            Restart();
        }

        internal void ReadRows(int selectedMember, ulong first, int rows, byte[] buffer, int recordSize, int destinationRow = 0)
        {
            int written = 0;
            while (written < rows)
            {
                int member;
                ulong body;
                if (!Next(out member, out body)) { throw Error(position, "The selected channel group ended before the requested rows."); }
                if (member != selectedMember || counts[member] - 1 < first) { continue; }
                Read(body, buffer, recordSize, (destinationRow + written) * recordSize);
                written++;
            }
        }

        private bool Next(out int member, out ulong body)
        {
            Checkpoint();
            member = 0;
            body = 0;
            if (position == total) { return false; }
            ulong start = position;
            if ((ulong)layout.PrefixBytes > total - start) { throw Error(start, "A record-ID prefix is truncated."); }
            Read(start, idBytes, layout.PrefixBytes);
            ulong id = 0;
            for (int index = 0; index < layout.PrefixBytes; index++) { id |= (ulong)idBytes[index] << (index * 8); }
            if (!layout.Find(id, out member)) { throw Error(start, "Unknown record ID in an interleaved data group."); }
            body = start + (ulong)layout.PrefixBytes;
            ulong size = layout.Groups[member].RecordByteCount;
            ulong remaining = total - body;
            if (layout.Groups[member].Layout.IsVlsdGroup)
            {
                if (remaining < 4) { throw Error(start, "A VLSD record length is truncated."); }
                Read(body, idBytes, 4);
                lastLength = EndianBitConverter.ReadUInt32(idBytes, 0, ByteOrder.LittleEndian);
                size = 4UL + lastLength;
                if (size > remaining) { throw Error(start, "A VLSD record exceeds the logical data stream."); }
            }
            else if (size > remaining || (ulong)layout.SuffixBytes > remaining - size)
            {
                throw Error(start, "The fixed record and suffix exceed the declared logical data stream.");
            }
            if (counts[member] >= layout.Groups[member].SampleCount)
            {
                throw Error(start, "Observed records exceed the declared channel-group cycle count.");
            }
            ulong end = body + size;
            if (layout.SuffixBytes != 0)
            {
                Read(end, idBytes, 1);
                if (idBytes[0] != (byte)id) { throw Error(end, "The trailing MDF 3 record ID does not match its prefix."); }
            }
            counts[member]++;
            position = end + (ulong)layout.SuffixBytes;
            return true;
        }

        private void Read(ulong start, byte[] buffer, int count, int destination = 0)
        {
            Checkpoint();
            if (cursor != null) { cursor.Read(start, buffer, count, destination); }
            else { FormatInput.ReadAt(input, record.DataAddress + start, buffer, count, "DT", token, destination); }
            Checkpoint();
        }

        private void Checkpoint() { reader.CheckOpen(); token.ThrowIfCancellationRequested(); }
        private MdfFormatException Error(ulong logicalOffset, string message)
        {
            return new MdfFormatException(message + " Logical DG byte offset 0x" + logicalOffset.ToString("X", CultureInfo.InvariantCulture) + ".",
                "MDF013", layout.BlockAddress, layout.BlockType);
        }
    }
}
