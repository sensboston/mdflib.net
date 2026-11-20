// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Unfinalized-file rules from ASAM MDF 4 id_unfi_flags (cycle counters, last DT length) and MDF 3.30 "UnFinMF";
// the bounded traversal and conservative in-place policy are original.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading;

namespace MdfLib
{
    /// <summary>The outcome of <see cref="MdfRecovery.FinalizeFile"/>.</summary>
    internal sealed class MdfRecoveryResult
    {
        internal MdfRecoveryResult(bool changed, IList<string> actions)
        {
            Changed = changed;
            Actions = new ReadOnlyCollection<string>(actions);
        }

        /// <summary>Gets whether the destination was modified.</summary>
        public bool Changed { get; }
        /// <summary>Gets one line per applied patch, in write order.</summary>
        public IReadOnlyList<string> Actions { get; }
    }

    /// <summary>Explicit, in-place finalization of an unfinalized ("UnFinMF ") MDF 3.30/4.x file, such as one whose writer
    /// stopped before <see cref="MdfWriter.FinalizeMeasurement"/>.</summary>
    /// <remarks>MDF 4: data groups with one or several channel groups (record IDs, VLSD groups) whose data is a DT, a DZ, or a
    /// DL chain (optionally under an HL) of DT/DZ blocks. The last DL of a chain may be preallocated with unused links (as
    /// written by <see cref="MdfWriter"/> for compressed or multi-group output); it is compacted in place. With id_unfi_flags
    /// bit 2 the DT block that ends the file is extended (or cut) to the last complete record. Record counts (and with bit 5,
    /// VLSD byte totals) are recomputed by scanning interleaved and VLSD data. MDF 3: sorted groups with one channel group, and
    /// interleaved groups with one-byte record IDs (records counted up to the first incomplete record or unknown ID).
    /// The identification becomes "MDF " with cleared flags last. Trailing partial records stay in the file but outside
    /// every block. SR blocks and VLSD offset updates (bit 6) are refused before any byte is written. Not a general repair tool.</remarks>
    internal static class MdfRecovery
    {
        private const int MaximumGroups = 65536;

        /// <summary>Finalizes in place and reports the patches; a finalized file is left unchanged.</summary>
        /// <exception cref="ArgumentException">The stream is not readable, writable and seekable.</exception>
        /// <exception cref="MdfFormatException">The file is not MDF or its structure is inconsistent.</exception>
        /// <exception cref="NotSupportedException">The file needs a recovery rule outside the supported subset.</exception>
        public static MdfRecoveryResult FinalizeFile(Stream stream, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (stream == null) { throw new ArgumentNullException(nameof(stream)); }
            if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek) { throw new ArgumentException("The stream must be readable, writable and seekable.", nameof(stream)); }
            cancellationToken.ThrowIfCancellationRequested();
            long length = stream.Length;
            byte[] id = Read(stream, 0, 64, length);
            string file = Encoding.ASCII.GetString(id, 0, 8);
            if (file == "MDF     ") { return new MdfRecoveryResult(false, new string[0]); }
            if (file != "UnFinMF ") { throw new MdfFormatException("The file identification is neither MDF nor UnFinMF.", "MDF001", 0); }
            ushort version = (ushort)(id[28] | (id[29] << 8));
            ushort flags = version >= 330 ? (ushort)(id[60] | (id[61] << 8)) : (ushort)0;
            ushort custom = version >= 330 ? (ushort)(id[62] | (id[63] << 8)) : (ushort)0;
            if (custom != 0) { throw new NotSupportedException("Custom unfinalized flags 0x" + custom.ToString("X4") + " (such as unlinked MdfLib data blocks) cannot be recovered."); }
            if ((flags & ~(version >= 400 ? 0x3F : 1)) != 0)
            { throw new NotSupportedException("Unfinalized flags 0x" + flags.ToString("X4") + " request VLSD offset updates, which are not recovered."); }
            List<Patch> patches = new List<Patch>();
            List<string> actions = new List<string>();
            if (version >= 400 && version < 500) { PlanMdf4(stream, length, flags, patches, actions, cancellationToken); }
            else if (version >= 300 && version < 400) { PlanMdf3(stream, length, patches, actions, cancellationToken); }
            else { throw new NotSupportedException("Unknown MDF version " + version + "."); }
            byte[] finalId = new byte[] { (byte)'M', (byte)'D', (byte)'F', 32, 32, 32, 32, 32 };
            patches.Add(new Patch(0, finalId));
            actions.Add("ID: identification set to \"MDF     \"");
            if (version >= 330) { patches.Add(new Patch(60, new byte[4])); actions.Add("ID: unfinalized flags cleared"); }
            // Planning finished without writing: apply block patches, flush, then publish the identification last.
            for (int i = 0; i < patches.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (i == patches.Count - (version >= 330 ? 2 : 1)) { stream.Flush(); }
                stream.Seek(patches[i].Offset, SeekOrigin.Begin);
                stream.Write(patches[i].Bytes, 0, patches[i].Bytes.Length);
            }
            stream.Flush();
            return new MdfRecoveryResult(true, actions);
        }

        private const int MaximumInflatedBlock = 64 << 20;
        private const int ScanChunk = 1 << 20;

        private sealed class Group4
        {
            internal long Address;
            internal int Data; // Offset of the CG data section (after the links).
            internal ulong RecordId, OldCount, OldBytes, Count, Bytes;
            internal long Record;
            internal bool Vlsd;
        }

        private sealed class Leaf
        {
            internal long Address, Length; // Logical (inflated) length.
            internal bool Zipped;
            internal byte Zip;
            internal uint Parameter;
            internal long Compressed;
        }

        private static void PlanMdf4(Stream stream, long length, ushort flags, List<Patch> patches, List<string> actions, CancellationToken token)
        {
            byte[] hd = Block(stream, 64, "##HD", length);
            long dgAddress = (long)U64(hd, 24);
            HashSet<long> seen = new HashSet<long>();
            List<KeyValuePair<List<Group4>, List<Leaf>>> work = new List<KeyValuePair<List<Group4>, List<Leaf>>>();
            List<int> idSizes = new List<int>();
            long lastLeaf = -1;
            while (dgAddress != 0)
            {
                token.ThrowIfCancellationRequested();
                if (!seen.Add(dgAddress) || seen.Count > MaximumGroups) { throw new MdfFormatException("The DG chain is cyclic or too long.", "MDF010", (ulong)dgAddress, "##DG"); }
                byte[] dg = Block(stream, dgAddress, "##DG", length);
                long cgAddress = (long)U64(dg, 32), data = (long)U64(dg, 40);
                int idSize = dg[56];
                if (idSize != 0 && idSize != 1 && idSize != 2 && idSize != 4 && idSize != 8)
                { throw new MdfFormatException("The record ID size must be 0, 1, 2, 4 or 8.", "MDF013", (ulong)dgAddress, "##DG"); }
                List<Group4> groups = new List<Group4>();
                while (cgAddress != 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (!seen.Add(cgAddress) || seen.Count > 4 * MaximumGroups) { throw new MdfFormatException("The CG chain is cyclic or too long.", "MDF010", (ulong)cgAddress, "##CG"); }
                    byte[] cg = Block(stream, cgAddress, "##CG", length);
                    long links = (long)U64(cg, 16);
                    if (links < 6 || 24 + 8 * links + 32 > cg.Length) { throw new MdfFormatException("The CG block is too short.", "MDF006", (ulong)cgAddress, "##CG"); }
                    if (U64(cg, 56) != 0) { throw new NotSupportedException("Unfinalized sample reductions are not recovered."); }
                    int d = (int)(24 + 8 * links);
                    Group4 group = new Group4
                    {
                        Address = cgAddress, Data = d, RecordId = U64(cg, d), OldCount = U64(cg, d + 8), Vlsd = (U16(cg, d + 16) & 1) != 0,
                        OldBytes = U64(cg, d + 24), Record = (long)U32(cg, d + 24) + U32(cg, d + 28)
                    };
                    if (!group.Vlsd && group.Record == 0 && idSize == 0) { throw new MdfFormatException("A channel group declares zero record bytes.", "MDF013", (ulong)cgAddress, "##CG"); }
                    foreach (Group4 other in groups)
                    {
                        if (other.RecordId == group.RecordId) { throw new MdfFormatException("Two channel groups share a record ID.", "MDF013", (ulong)cgAddress, "##CG"); }
                    }
                    groups.Add(group);
                    cgAddress = (long)U64(cg, 24);
                }
                if (groups.Count > 1 && idSize == 0) { throw new MdfFormatException("Several channel groups need record IDs.", "MDF013", (ulong)dgAddress, "##DG"); }
                if (groups.Count != 0)
                {
                    List<Leaf> leaves = new List<Leaf>();
                    if (data != 0) { Leaves(stream, length, data, leaves, patches, actions, token); }
                    foreach (Leaf leaf in leaves) { lastLeaf = Math.Max(lastLeaf, leaf.Address); }
                    work.Add(new KeyValuePair<List<Group4>, List<Leaf>>(groups, leaves));
                    idSizes.Add(idSize);
                }
                dgAddress = (long)U64(dg, 24);
            }
            for (int w = 0; w < work.Count; w++)
            {
                List<Group4> groups = work[w].Key;
                List<Leaf> leaves = work[w].Value;
                int idSize = idSizes[w];
                // Only the DT block that ends the file may grow (or shrink to whole records) when bit 2 asks for it.
                Leaf open = leaves.Count != 0 && !leaves[leaves.Count - 1].Zipped && leaves[leaves.Count - 1].Address == lastLeaf && (flags & 4) != 0
                    ? leaves[leaves.Count - 1] : null;
                long declared = open == null ? 0 : open.Length;
                if (open != null) { open.Length = length - open.Address - 24; }
                long consumed, openStart = 0;
                foreach (Leaf leaf in leaves) { if (leaf != open) { openStart += leaf.Length; } }
                if (idSize == 0 && !groups[0].Vlsd)
                {
                    long total = openStart + (open == null ? 0 : open.Length);
                    long records = total / groups[0].Record;
                    consumed = records * groups[0].Record;
                    groups[0].Count = (ulong)(consumed / groups[0].Record);
                }
                else { consumed = Scan(stream, length, leaves, idSize, groups, open == null ? long.MaxValue : openStart, token); }
                if (open != null)
                {
                    long size = Math.Max(0, consumed - openStart);
                    if (size != declared)
                    {
                        patches.Add(new Patch(open.Address + 8, BitConverter.GetBytes((ulong)(24 + size))));
                        actions.Add("DT @" + open.Address + ": length set to " + (24 + size));
                    }
                }
                foreach (Group4 group in groups)
                {
                    if (group.Count != group.OldCount || (flags & 1) != 0)
                    {
                        patches.Add(new Patch(group.Address + group.Data + 8, BitConverter.GetBytes(group.Count)));
                        actions.Add("CG @" + group.Address + ": cycle count set to " + group.Count);
                    }
                    if (group.Vlsd && (flags & 0x20) != 0 && group.Bytes != group.OldBytes)
                    {
                        patches.Add(new Patch(group.Address + group.Data + 24, BitConverter.GetBytes(group.Bytes)));
                        actions.Add("CG @" + group.Address + ": VLSD data bytes set to " + group.Bytes);
                    }
                }
            }
        }

        // Collects the DT/DZ leaves of a DG data root (DT, DZ, DL chain or HL over a DL chain) in stream order. The last DL
        // of a chain may be preallocated (unused zero links, as written by MdfLib): it is compacted in place to its used links.
        private static void Leaves(Stream stream, long length, long root, List<Leaf> leaves, List<Patch> patches, List<string> actions, CancellationToken token)
        {
            string type = Encoding.ASCII.GetString(Read(stream, root, 4, length));
            if (type == "##HL")
            {
                byte[] hl = Block(stream, root, "##HL", length);
                if (U64(hl, 16) < 1 || U64(hl, 24) == 0) { throw new MdfFormatException("HL has no first DL link.", "MDF006", (ulong)root, "##HL"); }
                root = (long)U64(hl, 24);
                type = Encoding.ASCII.GetString(Read(stream, root, 4, length));
                if (type != "##DL") { throw new MdfFormatException("HL must reference a DL.", "MDF006", (ulong)root, type); }
            }
            if (type != "##DL") { leaves.Add(LeafAt(stream, length, root)); return; }
            HashSet<long> chain = new HashSet<long>();
            long address = root;
            while (address != 0)
            {
                token.ThrowIfCancellationRequested();
                if (!chain.Add(address) || chain.Count > MaximumGroups) { throw new MdfFormatException("The data-list chain contains a cycle.", "MDF010", (ulong)address, "##DL"); }
                byte[] header = Read(stream, address, 24, length);
                if (Encoding.ASCII.GetString(header, 0, 4) != "##DL") { throw new MdfFormatException("Expected a ##DL block.", "MDF006", (ulong)address, "##DL"); }
                ulong size = U64(header, 8), linkCount = U64(header, 16);
                if (linkCount < 1 || size > 64 << 20 || size < 24 + 8 * linkCount + 8) { throw new MdfFormatException("The DL block length is invalid.", "MDF006", (ulong)address, "##DL"); }
                byte[] dl = Read(stream, address, (int)size, length);
                int links = (int)linkCount - 1, fields = 24 + 8 * (int)linkCount;
                byte dlFlags = dl[fields];
                uint count = U32(dl, fields + 4);
                long next = (long)U64(dl, 24);
                int used = 0; // Leading data links; dl_count may lag one link behind an interrupted writer.
                while (used < links && U64(dl, 32 + 8 * used) != 0) { used++; }
                if (count > used) { throw new MdfFormatException("dl_count exceeds the used DL data links.", "MDF006", (ulong)address, "##DL"); }
                if (used != links || used != count)
                {
                    if (next != 0) { throw new MdfFormatException("Only the last DL of a chain may have unused links.", "MDF006", (ulong)address, "##DL"); }
                    if ((dlFlags & ~1) != 0) { throw new NotSupportedException("A partial DL with time/angle/distance values is not recovered."); }
                    for (int i = used; i < links; i++)
                    {
                        if (U64(dl, 32 + 8 * i) != 0) { throw new MdfFormatException("A DL has data links after an unused link.", "MDF006", (ulong)address, "##DL"); }
                    }
                    int extra = (dlFlags & 1) != 0 ? 8 : 8 * used;
                    byte[] section = new byte[8 + extra];
                    section[0] = dlFlags;
                    Array.Copy(BitConverter.GetBytes((uint)used), 0, section, 4, 4);
                    if ((dlFlags & 1) != 0) { Array.Copy(dl, fields + 8, section, 8, 8); }
                    else { Array.Copy(dl, fields + 8, section, 8, 8 * used); }
                    if (!BitConverter.IsLittleEndian) { throw new NotSupportedException("Big-endian hosts are not supported."); }
                    patches.Add(new Patch(address + 24 + 8 * (1 + used), section, false));
                    byte[] shape = new byte[16];
                    Array.Copy(BitConverter.GetBytes((ulong)(24 + 8 * (1 + used) + section.Length)), 0, shape, 0, 8);
                    Array.Copy(BitConverter.GetBytes((ulong)(1 + used)), 0, shape, 8, 8);
                    patches.Add(new Patch(address + 8, shape, false));
                    actions.Add("DL @" + address + ": compacted to " + used + " data links");
                }
                for (int i = 0; i < used; i++) { leaves.Add(LeafAt(stream, length, (long)U64(dl, 32 + 8 * i))); }
                address = next;
            }
        }

        private static Leaf LeafAt(Stream stream, long length, long address)
        {
            byte[] header = Read(stream, address, 24, length);
            string type = Encoding.ASCII.GetString(header, 0, 4);
            long size = (long)U64(header, 8);
            if (size < 24 || size > length - address) { throw new MdfFormatException("A data block lies outside the file.", "MDF007", (ulong)address, type); }
            if (type == "##DT") { return new Leaf { Address = address, Length = size - 24 }; }
            if (type != "##DZ") { throw new NotSupportedException("Data blocks of type " + type + " are not recovered."); }
            byte[] dz = Read(stream, address, 48, length);
            if (dz[24] != 'D' || dz[25] != 'T' || dz[26] > 1) { throw new NotSupportedException("Only zlib DZ blocks of DT data are recovered."); }
            long original = (long)U64(dz, 32), compressed = (long)U64(dz, 40);
            if (original < 0 || compressed != size - 48) { throw new MdfFormatException("The DZ lengths are inconsistent.", "MDF006", (ulong)address, type); }
            return new Leaf { Address = address, Length = original, Zipped = true, Zip = dz[26], Parameter = U32(dz, 28), Compressed = compressed };
        }

        // Counts records per CG (and VLSD bytes) through the logical data stream; returns the end of the last whole record.
        // Inside the growing DT (from `tolerant` on) an unknown record ID ends the data like a torn record.
        private static long Scan(Stream stream, long length, List<Leaf> leaves, int idSize, List<Group4> groups, long tolerant, CancellationToken token)
        {
            LogicalStream input = new LogicalStream(stream, length, leaves, token);
            byte[] id = new byte[8], size = new byte[4];
            long consumed = 0;
            while (true)
            {
                Group4 group = groups[0];
                if (idSize != 0)
                {
                    if (!input.Read(id, idSize)) { break; }
                    ulong value = idSize == 1 ? id[0] : idSize == 2 ? U16(id, 0) : idSize == 4 ? U32(id, 0) : U64(id, 0);
                    group = null;
                    foreach (Group4 candidate in groups) { if (candidate.RecordId == value) { group = candidate; break; } }
                    if (group == null && consumed >= tolerant) { break; }
                    if (group == null) { throw new MdfFormatException("A record has an unknown record ID " + value + ".", "MDF013", (ulong)input.Address); }
                }
                long body;
                if (group.Vlsd)
                {
                    if (!input.Read(size, 4)) { break; }
                    body = U32(size, 0);
                    if (!input.Skip(body)) { break; }
                    body += 4;
                }
                else
                {
                    body = group.Record;
                    if (!input.Skip(body)) { break; }
                }
                group.Count++;
                group.Bytes += (ulong)body;
                consumed = input.Position;
            }
            return consumed;
        }

        private sealed class LogicalStream
        {
            private readonly Stream stream;
            private readonly long fileLength;
            private readonly List<Leaf> leaves;
            private readonly CancellationToken token;
            private int leaf = -1;
            private byte[] buffer = new byte[0];
            private int bufferLength, bufferIndex;
            private long leafOffset; // Next unread offset inside the current leaf (for DT reads).

            internal LogicalStream(Stream stream, long fileLength, List<Leaf> leaves, CancellationToken token)
            {
                this.stream = stream; this.fileLength = fileLength; this.leaves = leaves; this.token = token;
            }

            internal long Position { get; private set; }
            internal long Address { get { return leaf < 0 || leaf >= leaves.Count ? 0 : leaves[leaf].Address; } }

            private bool Fill()
            {
                while (bufferIndex == bufferLength)
                {
                    token.ThrowIfCancellationRequested();
                    if (leaf >= 0 && leaf < leaves.Count && !leaves[leaf].Zipped && leafOffset < leaves[leaf].Length)
                    {
                        int count = (int)Math.Min(ScanChunk, leaves[leaf].Length - leafOffset);
                        buffer = MdfRecovery.Read(stream, leaves[leaf].Address + 24 + leafOffset, count, fileLength);
                        bufferLength = count; bufferIndex = 0; leafOffset += count;
                        return true;
                    }
                    if (++leaf >= leaves.Count) { return false; }
                    Leaf current = leaves[leaf];
                    leafOffset = 0; bufferLength = 0; bufferIndex = 0;
                    if (current.Zipped)
                    {
                        if (current.Length > MaximumInflatedBlock || current.Compressed > MaximumInflatedBlock)
                        { throw new NotSupportedException("DZ blocks over " + MaximumInflatedBlock + " bytes are not recovered."); }
                        byte[] packed = MdfRecovery.Read(stream, current.Address + 48, (int)current.Compressed, fileLength);
                        byte[] plain = Compression.ZlibInflater.Inflate(packed, (int)current.Length, MaximumInflatedBlock, () => token.ThrowIfCancellationRequested());
                        if (plain.Length != current.Length) { throw new MdfFormatException("A DZ block inflates to the wrong length.", "MDF015", (ulong)current.Address, "##DZ"); }
                        buffer = current.Zip == 1 ? Untranspose(plain, current.Parameter) : plain;
                        bufferLength = buffer.Length; leafOffset = buffer.Length;
                    }
                }
                return true;
            }

            internal bool Read(byte[] target, int count)
            {
                for (int i = 0; i < count; i++)
                {
                    if (!Fill()) { return false; }
                    target[i] = buffer[bufferIndex++];
                    Position++;
                }
                return true;
            }

            internal bool Skip(long count)
            {
                while (count > 0)
                {
                    if (!Fill()) { return false; }
                    int step = (int)Math.Min(count, bufferLength - bufferIndex);
                    bufferIndex += step; Position += step; count -= step;
                }
                return true;
            }
        }

        // MDF 4 transposition: the first rows*columns bytes are stored column by column (columns = zip parameter).
        internal static byte[] Untranspose(byte[] data, uint parameter)
        {
            if (parameter == 0) { return data; }
            long columns = parameter, rows = data.Length / columns;
            byte[] result = (byte[])data.Clone();
            for (long r = 0; r < rows; r++)
            {
                for (long c = 0; c < columns; c++) { result[r * columns + c] = data[c * rows + r]; }
            }
            return result;
        }

        private static void PlanMdf3(Stream stream, long length, List<Patch> patches, List<string> actions, CancellationToken token)
        {
            byte[] hd = Read(stream, 64, 208, length);
            if (hd[0] != 'H' || hd[1] != 'D') { throw new MdfFormatException("The MDF 3 HD block is missing.", "MDF006", 64, "HD"); }
            long dgAddress = U32(hd, 4);
            List<Mdf3Group> groups = new List<Mdf3Group>();
            HashSet<long> seen = new HashSet<long>();
            while (dgAddress != 0)
            {
                token.ThrowIfCancellationRequested();
                if (!seen.Add(dgAddress) || seen.Count > MaximumGroups) { throw new MdfFormatException("The DG chain is cyclic or too long.", "MDF010", (ulong)dgAddress, "DG"); }
                byte[] dg = Read(stream, dgAddress, 24, length);
                if (dg[0] != 'D' || dg[1] != 'G') { throw new MdfFormatException("A DG block is missing.", "MDF006", (ulong)dgAddress, "DG"); }
                Mdf3Group group = new Mdf3Group { Data = U32(dg, 16), RecordIds = U16(dg, 22) };
                if (group.RecordIds > 2) { throw new NotSupportedException("Only one prefix record-ID byte and an optional suffix are recovered."); }
                long cgAddress = U32(dg, 8);
                while (cgAddress != 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (!seen.Add(cgAddress) || seen.Count > MaximumGroups) { throw new MdfFormatException("The CG chain is cyclic or too long.", "MDF010", (ulong)cgAddress, "CG"); }
                    byte[] cg = Read(stream, cgAddress, 26, length);
                    if (cg[0] != 'C' || cg[1] != 'G') { throw new MdfFormatException("A CG block is missing.", "MDF006", (ulong)cgAddress, "CG"); }
                    long record = U16(cg, 20);
                    if (record == 0) { throw new MdfFormatException("A channel group declares zero record bytes.", "MDF013", (ulong)cgAddress, "CG"); }
                    group.ChannelGroups.Add(new[] { cgAddress, U16(cg, 16), record, U32(cg, 22) });
                    cgAddress = U32(cg, 4);
                }
                if (group.RecordIds == 0 && group.ChannelGroups.Count > 1)
                { throw new NotSupportedException("Unfinalized data groups with several channel groups need record IDs."); }
                groups.Add(group);
                dgAddress = U32(dg, 4);
            }
            long last = -1;
            foreach (Mdf3Group group in groups) { if (group.ChannelGroups.Count > 0 && group.Data > last) { last = group.Data; } }
            foreach (Mdf3Group group in groups)
            {
                if (group.Data == 0 || group.Data != last || group.ChannelGroups.Count == 0) { continue; } // Earlier record areas are bounded by later blocks.
                long[] counts = group.RecordIds == 0 ? new[] { Math.Max(0, (length - group.Data) / group.ChannelGroups[0][2]) }
                    : CountIdentified(stream, length, group, token);
                for (int i = 0; i < counts.Length; i++)
                {
                    long[] cg = group.ChannelGroups[i];
                    if (counts[i] > uint.MaxValue) { throw new NotSupportedException("The MDF 3 cycle counter cannot hold the recovered record count."); }
                    if (counts[i] == cg[3]) { continue; }
                    patches.Add(new Patch(cg[0] + 22, BitConverter.GetBytes((uint)counts[i])));
                    actions.Add("CG @" + cg[0] + ": cycle count set to " + counts[i]);
                }
            }
        }

        // walks the interleaved records (UINT8 record ID, record, optional suffix ID) up to the first incomplete record
        // or unknown ID; the trailing bytes stay outside every channel group.
        private static long[] CountIdentified(Stream stream, long length, Mdf3Group group, CancellationToken token)
        {
            int[] index = new int[256];
            for (int i = 0; i < index.Length; i++) { index[i] = -1; }
            for (int i = 0; i < group.ChannelGroups.Count; i++)
            {
                long id = group.ChannelGroups[i][1];
                if (id > 255 || index[id] >= 0) { throw new NotSupportedException("MDF 3 record IDs must be distinct one-byte values."); }
                index[id] = i;
            }
            long[] counts = new long[group.ChannelGroups.Count];
            byte[] window = new byte[1 << 16];
            long windowStart = 0;
            int windowLength = 0;
            long position = group.Data;
            for (long n = 0; position < length; n++)
            {
                if ((n & 0xFFFF) == 0) { token.ThrowIfCancellationRequested(); }
                if (position < windowStart || position >= windowStart + windowLength)
                {
                    windowStart = position;
                    windowLength = (int)Math.Min(window.Length, length - position);
                    Array.Copy(Read(stream, position, windowLength, length), window, windowLength);
                }
                int target = index[window[position - windowStart]];
                if (target < 0) { break; }
                long next = position + 1 + group.ChannelGroups[target][2] + (group.RecordIds == 2 ? 1 : 0);
                if (next > length) { break; }
                counts[target]++;
                position = next;
            }
            return counts;
        }

        private sealed class Mdf3Group
        {
            internal long Data;
            internal int RecordIds;
            internal readonly List<long[]> ChannelGroups = new List<long[]>(); // CG, record ID, record bytes, stored count.
        }

        private sealed class Patch
        {
            internal readonly long Offset;
            internal readonly byte[] Bytes;
            internal Patch(long offset, byte[] bytes, bool number = true)
            {
                Offset = offset;
                Bytes = BitConverter.IsLittleEndian || !number ? bytes : Reverse(bytes);
            }
            private static byte[] Reverse(byte[] bytes) { Array.Reverse(bytes); return bytes; }
        }

        private static byte[] Block(Stream stream, long address, string type, long length)
        {
            byte[] header = Read(stream, address, 24, length);
            if (Encoding.ASCII.GetString(header, 0, 4) != type) { throw new MdfFormatException("Expected a " + type + " block.", "MDF006", (ulong)address, type); }
            ulong size = U64(header, 8);
            if (size < 24 || size > 1 << 20) { throw new MdfFormatException("The " + type + " block length is invalid.", "MDF006", (ulong)address, type); }
            return Read(stream, address, (int)size, length);
        }

        private static byte[] Read(Stream stream, long offset, int count, long length)
        {
            if (offset < 0 || offset > length - count) { throw new MdfFormatException("A block lies outside the file.", "MDF007", (ulong)Math.Max(0, offset)); }
            byte[] buffer = new byte[count];
            stream.Seek(offset, SeekOrigin.Begin);
            int done = 0;
            while (done < count)
            {
                int read = stream.Read(buffer, done, count - done);
                if (read <= 0) { throw new EndOfStreamException("The stream ended inside a block."); }
                done += read;
            }
            return buffer;
        }

        private static ushort U16(byte[] b, int o) { return (ushort)(b[o] | (b[o + 1] << 8)); }
        private static uint U32(byte[] b, int o) { return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)); }
        private static ulong U64(byte[] b, int o) { return U32(b, o) | ((ulong)U32(b, o + 4) << 32); }
    }
}
