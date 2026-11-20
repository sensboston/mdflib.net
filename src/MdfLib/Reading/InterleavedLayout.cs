// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Threading;
using MdfLib.Internal;

namespace MdfLib.Reading
{
    // Reader-owned immutable framing shared by every CG of one DG, never an index proportional to samples.
    internal sealed class InterleavedLayout
    {
        private readonly Dictionary<ulong, int> members;

        private InterleavedLayout(IReadOnlyList<MdfChannelGroup> groups, Dictionary<ulong, int> members,
            int prefixBytes, int suffixBytes, ulong blockAddress, string blockType, ulong totalBytes, bool variable)
        {
            HasVariableRecords = variable;
            Groups = groups;
            this.members = members;
            PrefixBytes = prefixBytes;
            SuffixBytes = suffixBytes;
            BlockAddress = blockAddress;
            BlockType = blockType;
            TotalBytes = totalBytes;
        }

        internal IReadOnlyList<MdfChannelGroup> Groups { get; }
        internal int PrefixBytes { get; }
        internal int SuffixBytes { get; }
        internal ulong BlockAddress { get; }
        internal string BlockType { get; }
        // Exact metadata-derived extent; meaningless when HasVariableRecords (then the data root's logical length rules).
        internal ulong TotalBytes { get; }
        // at least one member is a VLSD CG whose records are UInt32 length + bytes.
        internal bool HasVariableRecords { get; }
        internal Mdf4DataMap DataMap { get; set; }
        internal bool Find(ulong id, out int index) { return members.TryGetValue(id, out index); }

        internal static void Attach(IReadOnlyList<MdfChannelGroup> groups, int prefixBytes, int suffixBytes,
            ReadBudget budget, ulong blockAddress, string blockType, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            // Includes descriptor/dictionary capacity and entries before their first allocation.
            budget.ReserveMetadata(256 + 128UL * (ulong)groups.Count, blockAddress);
            Dictionary<ulong, int> members = new Dictionary<ulong, int>(groups.Count);
            string unsupported = null;
            for (int index = 0; index < groups.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                MdfChannelGroup group = groups[index];
                ulong id = group.Layout.RecordId;
                if (prefixBytes < 8 && id >= (1UL << (prefixBytes * 8)))
                {
                    throw new MdfFormatException("A channel-group record ID exceeds its declared prefix width.", "MDF013", group.Layout.BlockAddress, blockType == "DG" ? "CG" : "##CG");
                }
                if (members.ContainsKey(id))
                {
                    throw new MdfFormatException("Duplicate record IDs within one data group.", "MDF013", group.Layout.BlockAddress, blockType == "DG" ? "CG" : "##CG");
                }
                members.Add(id, index);
                if (!group.CanReadSamples && !group.Layout.IsVlsdGroup) { unsupported = group.UnsupportedReason; }
            }
            if (unsupported != null)
            {
                foreach (MdfChannelGroup group in groups)
                {
                    if (group.Layout.IsVlsdGroup) { continue; } // Keeps its own reason; it never had samples.
                    group.UnsupportedReason = "The interleaved data group contains an unsupported channel-group framing: " + unsupported;
                }
                return;
            }
            ulong total = 0;
            bool variable = false;
            foreach (MdfChannelGroup group in groups)
            {
                token.ThrowIfCancellationRequested();
                if (group.Layout.IsVlsdGroup) { variable = true; continue; }
                // CG sizes come from UInt16 or two UInt32 fields, so the small framing addition fits UInt64.
                ulong frame = group.RecordByteCount + (ulong)prefixBytes + (ulong)suffixBytes;
                if (group.SampleCount > ulong.MaxValue / frame)
                {
                    throw new MdfFormatException("The declared interleaved record extent overflows.", "MDF013", blockAddress, blockType);
                }
                ulong length = group.SampleCount * frame;
                if (length > ulong.MaxValue - total)
                {
                    throw new MdfFormatException("The combined interleaved data-group extent overflows.", "MDF013", blockAddress, blockType);
                }
                total += length;
            }
            InterleavedLayout layout = new InterleavedLayout(groups, members, prefixBytes, suffixBytes, blockAddress, blockType, total, variable);
            foreach (MdfChannelGroup group in groups) { group.Layout.Interleaved = layout; }
        }
    }
}
