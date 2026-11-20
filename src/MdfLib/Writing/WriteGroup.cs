// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System.Collections.Generic;

namespace MdfLib.Writing
{
    // one written channel group. MDF 4 multi-group output places each group in its own DG (sorted); records are
    // buffered per group into whole-record DT/DZ blocks, each linked into a preallocated per-group DL once written.
    internal sealed class WriteGroup
    {
        internal readonly List<WriteChannel> Channels = new List<WriteChannel>();
        internal byte[] Name; // Encoded acquisition name with NUL, or null.
        internal int DataBytes, RecordBytes;
        // invalidation bits used so far (one per scalar, one per array element) and values per row.
        internal int InvalidationBits, ValueCount;
        internal bool HasMaster;
        internal ulong SampleCount;
        internal long DataGroupAddress, ChannelGroupAddress, NameAddress, CountOffset;
        internal byte[] Record, Chunk;
        internal byte[] Identified; // MDF 3 multi-group wire record: the record-ID byte, then Record.
        // VLSD channels of this group (each with its own VLSD CG, record IDs 2..); records then carry ID 1.
        internal readonly List<WriteChannel> VlsdChannels = new List<WriteChannel>();
        // the group's SD channel (at most one) and the SD block body: [UInt32 length][bytes] per record.
        internal WriteChannel SdChannel;
        internal System.IO.MemoryStream SdData;
        internal byte[] Wire; // Reused row buffer: VLSD records, then the identified fixed record.
        internal int ChunkLength, BlockBytes;
        internal readonly List<long> Blocks = new List<long>();
        // the group's current, preallocated DL (data links are patched as blocks complete) and its used links.
        internal long ListAddress;
        internal int ListCount;
        // VLSD groups write whole rows per block (variable block lengths), so their DLs carry dl_offset values.
        internal long LogicalBytes;
        internal readonly List<long> ListOffsets = new List<long>();
        // explicit-layout (bus logging) groups: cg_flags, path separator, SI and records supplied as raw bytes.
        internal bool Raw;
        internal ushort Flags, PathSeparator;
        internal byte[] SourceName, SourcePath, SourceComment; // Encoded with NUL, or null when the group has no SI block.
        internal byte SourceType, BusType, SourceFlags; // SI payload; flags: 1 name, 2 path, 4 comment, 8 sync bus.
        internal byte[] CommentText; // the MDF 4 CGcomment MD payload; MDF 3 channel groups have no comment.
        internal long SourceAddress, SourceNameAddress, SourcePathAddress, SourceCommentAddress;
        internal long CommentAddress;
        internal byte[] DataGroupComment; internal long DataGroupCommentAddress; // the DG MD payload of the owner
        // channel groups that share one unsorted data group. DataOwner is the group whose DG block the group uses
        // (null for a group with its own DG); Members counts the groups of an owner's DG and RecordId is the group's
        // record ID inside it. Shared groups write into the owner's record stream.
        internal WriteGroup DataOwner;
        internal int Members = 1;
        internal byte RecordId = 1;

        internal WriteGroup Owner { get { return DataOwner ?? this; } }

        internal static int CountDataGroups(IReadOnlyList<WriteGroup> groups)
        {
            int count = 0;
            foreach (WriteGroup group in groups) { if (group.DataOwner == null) { count++; } }
            return count;
        }
    }
}
