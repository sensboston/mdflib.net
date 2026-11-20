// Copyright 2021 Ingemar Hedvall
// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// SR wire layouts (sr3block/sr4block) adapted from the pinned MIT mdflib; traversal, validation and bounds are new.
using System;
using System.Collections.Generic;
using System.Threading;
using MdfLib.Internal;
using MdfLib.IO;

namespace MdfLib.Formats
{
    // reads the SR chain of one channel group. Reduction data is mapped lazily by the value reader; MDF 3 raw ranges are
    // registered here (bounds/overlap) so that a failed listing never publishes a partial chain.
    internal static class SampleReductionReader
    {
        internal const int Mdf4FixedBytes = 24;

        internal static IReadOnlyList<MdfSampleReduction> Read(BlockResolver resolver, ReadBudget budget, object document,
            MdfChannelGroup group, bool mdf4, CancellationToken token)
        {
            List<MdfSampleReduction> result = new List<MdfSampleReduction>();
            HashSet<ulong> chain = new HashSet<ulong>();
            ulong offset = mdf4 ? Mdf4First(resolver, group, token) : Mdf3First(resolver, group, token);
            while (offset != 0)
            {
                token.ThrowIfCancellationRequested();
                budget.CheckDepth(4, offset);
                budget.ReserveMetadata(256, offset);
                if (!chain.Add(offset)) { throw new MdfFormatException("The sample-reduction chain contains a cycle.", "MDF010", offset, mdf4 ? "##SR" : "SR"); }
                MdfSampleReduction reduction = mdf4 ? Mdf4(resolver, group, offset, token) : Mdf3(resolver, group, offset, token);
                reduction.Document = document;
                reduction.Group = group;
                reduction.Index = result.Count;
                result.Add(reduction);
                offset = resolver.Resolve(offset, cancellationToken: token, mdf3LinkCount: mdf4 ? 0 : 2).Links[0];
            }
            token.ThrowIfCancellationRequested();
            return Array.AsReadOnly(result.ToArray());
        }

        private static ulong Mdf4First(BlockResolver resolver, MdfChannelGroup group, CancellationToken token)
        {
            BlockHeader cg = resolver.Resolve(group.Layout.BlockAddress, "##CG", cancellationToken: token);
            return cg.Links.Count > 4 ? cg.Links[4] : 0;
        }

        private static ulong Mdf3First(BlockResolver resolver, MdfChannelGroup group, CancellationToken token)
        {
            BlockHeader cg = resolver.Resolve(group.Layout.BlockAddress, "CG", 26, 3, token);
            if (cg.Length < 30) { return 0; } // Pre-3.30 producers may omit the trailing SR link.
            byte[] data = resolver.ReadPayload(cg, 14, token);
            ulong first = EndianBitConverter.ReadUInt32(data, 10, ByteOrder.LittleEndian);
            resolver.ValidateAdditionalLink(first, cg.Offset + 26, cg.Type);
            return first;
        }

        private static MdfSampleReduction Mdf4(BlockResolver resolver, MdfChannelGroup group, ulong offset, CancellationToken token)
        {
            BlockHeader block = resolver.Resolve(offset, "##SR", 24 + 16 + Mdf4FixedBytes, cancellationToken: token);
            if (block.Links.Count != 2) { throw new MdfFormatException("SR requires exactly the next and data links.", "MDF006", offset, block.Type); }
            byte[] data = resolver.ReadPayload(block, Mdf4FixedBytes, token);
            MdfSampleReduction reduction = new MdfSampleReduction
            {
                BlockAddress = offset, DataAddress = block.Links[1],
                CycleCount = EndianBitConverter.ReadUInt64(data, 0, ByteOrder.LittleEndian),
                Interval = EndianBitConverter.ReadDouble(data, 8, ByteOrder.LittleEndian),
                SyncType = (MdfReductionSyncType)data[16], Flags = data[17]
            };
            uint dataBytes = group.Layout.DataBytes, invalidationBytes = group.Layout.InvalidationBytes;
            if (reduction.HasInvalidationBytes && invalidationBytes == 0)
            { throw new MdfFormatException("SR declares invalidation bytes for a channel group without them.", "MDF013", offset, block.Type); }
            if (reduction.CycleCount != 0 && reduction.DataAddress == 0)
            { throw new MdfFormatException("A nonempty sample reduction has no data link.", "MDF013", offset, block.Type); }
            reduction.RecordBytes = 3UL * dataBytes + (reduction.HasInvalidationBytes ? invalidationBytes : 0UL);
            if ((reduction.Flags & ~3) != 0) { reduction.UnsupportedReason = "Unknown SR flags are not supported."; }
            else if (group.Layout.IsVlsdGroup || dataBytes == 0) { reduction.UnsupportedReason = "The channel group has no fixed record layout."; }
            return reduction;
        }

        private static MdfSampleReduction Mdf3(BlockResolver resolver, MdfChannelGroup group, ulong offset, CancellationToken token)
        {
            BlockHeader block = resolver.Resolve(offset, "SR", 24, 2, token);
            byte[] data = resolver.ReadPayload(block, 12, token);
            MdfSampleReduction reduction = new MdfSampleReduction
            {
                BlockAddress = offset, DataAddress = block.Links[1], RawData = true,
                CycleCount = EndianBitConverter.ReadUInt32(data, 0, ByteOrder.LittleEndian),
                Interval = EndianBitConverter.ReadDouble(data, 4, ByteOrder.LittleEndian),
                SyncType = MdfReductionSyncType.Time, Flags = 0,
                RecordBytes = 3UL * group.Layout.DataBytes
            };
            if (reduction.CycleCount != 0)
            {
                if (reduction.DataAddress == 0 || reduction.RecordBytes == 0)
                { throw new MdfFormatException("A nonempty sample reduction has no data range.", "MDF013", offset, block.Type); }
                // Both factors are at most 32 bits wide, so the product cannot overflow.
                resolver.RegisterRawDataRange(reduction.DataAddress, reduction.CycleCount * reduction.RecordBytes, token);
            }
            return reduction;
        }
    }
}
