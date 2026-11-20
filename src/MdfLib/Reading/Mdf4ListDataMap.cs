// Copyright 2022 Ingemar Hedvall
// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// LD field/flag layout adapted from pinned MIT mdflib. Atomic paired maps and resource/range policy are original.
using System;
using System.Collections.Generic;
using System.Threading;
using MdfLib.Formats;
using MdfLib.Internal;
using MdfLib.IO;

namespace MdfLib.Reading
{
    // Both immutable stream maps are published as one holder, never independently on a partial traversal.
    internal sealed class Mdf4ListDataMap
    {
        internal Mdf4DataMap Values { get; private set; }
        internal Mdf4DataMap Invalidations { get; private set; }

        internal static Mdf4ListDataMap Build(BlockResolver resolver, ReaderLimits limits, ReadBudget budget,
            ulong rootAddress, ulong samples, uint dataBytes, uint invalidationBytes, CancellationToken token,
            string valueType = "DV", string invalidationType = "DI")
        {
            if (dataBytes == 0 || samples > ulong.MaxValue / dataBytes
                || (invalidationBytes != 0 && samples > ulong.MaxValue / invalidationBytes))
            { throw Error(rootAddress, "Declared separate record stream size overflows or has no fixed data size."); }
            budget.ReserveMetadata(1024, rootAddress); // Holder, two maps, builders/lists/set and final arrays' fixed allowance.
            List<Mdf4DataMap.Segment> values = new List<Mdf4DataMap.Segment>();
            List<Mdf4DataMap.Segment> invalidations = new List<Mdf4DataMap.Segment>();
            HashSet<ulong> chain = new HashSet<ulong>();
            ulong totalRows = 0;
            ulong address = rootAddress;
            // an optional root HL over the LD chain adds one depth level and
            // requires agreeing equal flags and DZ leaves of its declared compression mode.
            int depth = 1;
            bool headerList = false;
            uint headerEqual = 0;
            byte headerZip = 0;
            budget.CheckDepth(depth, rootAddress);
            BlockHeader root = resolver.Resolve(rootAddress, cancellationToken: token);
            if (root.Type == "##HL")
            {
                if (root.Links.Count != 1 || root.DataLength != 8) { throw Shape(root, "HL requires exactly one link and eight data bytes."); }
                byte[] header = resolver.ReadPayload(root, 8, token);
                ushort headerFlags = EndianBitConverter.ReadUInt16(header, 0, ByteOrder.LittleEndian);
                headerZip = header[2];
                if (headerFlags > 1 || headerZip > 1) { throw new NotSupportedException("Extended HL flags or compression modes are not supported."); }
                if (root.Links[0] == 0) { throw new MdfFormatException("HL has no first LD link.", "MDF013", root.Offset, root.Type); }
                headerList = true;
                headerEqual = headerFlags;
                depth = 2;
                address = root.Links[0];
            }
            while (address != 0)
            {
                token.ThrowIfCancellationRequested();
                budget.CheckDepth(depth, address); // Siblings do not increase depth. No recursive list nesting.
                budget.ReserveMetadata(128, address);
                if (!chain.Add(address)) { throw new MdfFormatException("The list-data chain contains a cycle.", "MDF010", address, "##LD"); }
                BlockHeader list = resolver.Resolve(address, "##LD", cancellationToken: token);
                if (list.Links.Count < 2 || list.DataLength < 8) { throw Shape(list, "LD requires a next link, value links and flags/count fields."); }
                byte[] prefix = resolver.ReadPayload(list, 8, token);
                uint flags = U32(prefix, 0);
                uint count = U32(prefix, 4);
                // time/angle/distance summary arrays (bits 1-3) are accepted but inert and never read.
                if ((flags & ~0x8000000FU) != 0) { throw new NotSupportedException("LD zip-info extensions and unknown flags are not supported."); }
                bool equalMode = (flags & 1) != 0;
                bool hasInvalidations = (flags & 0x80000000U) != 0;
                if (count == 0 || (ulong)list.Links.Count != 1 + (ulong)count * (hasInvalidations ? 2UL : 1UL))
                { throw Error(address, "LD count does not equal its value/invalidation link section."); }
                if (headerList && (flags & 1) != headerEqual) { throw Error(address, "HL and LD equal-sample flags disagree."); }
                ulong payloadBytes = equalMode ? 16UL : 8 + (ulong)count * 8; // Count is bounded by the resolved link count.
                ulong summaryBytes = (ulong)count * 8 * (((flags >> 1) & 1) + ((flags >> 2) & 1) + ((flags >> 3) & 1));
                if (list.DataLength != payloadBytes + summaryBytes) { throw Shape(list, "LD fields must exactly occupy its declared data section."); }
                if (hasInvalidations && invalidationBytes == 0) { throw Error(address, "LD has invalidation links for a group with no invalidation bytes."); }
                if (!hasInvalidations && invalidationBytes != 0)
                { throw new NotSupportedException("A group with invalidation bytes requires explicit LD DI links (null DI means all-valid)."); }
                ReadBudget.Check(nameof(MdfReaderOptions.MaximumReadBufferBytes), (ulong)limits.MaximumReadBufferBytes, 0, payloadBytes, address);
                byte[] fields = resolver.ReadPayload(list, (int)payloadBytes, token);
                ulong equal = equalMode ? U64(fields, 8) : 0;
                if (equalMode && equal == 0) { throw Error(address, "LD equal sample count must be positive."); }
                for (int index = 0; index < (int)count; index++) // Count proved <= bounded Int32 links before this cast.
                {
                    token.ThrowIfCancellationRequested();
                    budget.CheckDepth(depth + 1, address);
                    budget.ReserveMetadata(128, address); // Logical pair allowance before creating either segment.
                    ulong valueAddress = list.Links[1 + index];
                    if (valueAddress == 0) { throw Error(address, "LD value link must not be null."); }
                    Mdf4DataMap.Segment value = Mdf4DataMap.ReadSegment(resolver, valueAddress, token, budget, valueType);
                    if (headerList) { RequireHeaderListLeaf(value, headerZip); }
                    ulong rows = value.Length / dataBytes;
                    if (value.Length % dataBytes != 0 || rows == 0 || rows > samples - totalRows
                        || (equalMode ? rows > equal || (index + 1 < count && rows != equal) : U64(fields, 8 + 8 * index) != totalRows))
                    { throw Error(address, "LD value lengths, equal sample counts or global sample ordinal offsets are inconsistent."); }
                    value.Start = totalRows * dataBytes;
                    values.Add(value);
                    if (hasInvalidations)
                    {
                        ulong invalidationAddress = list.Links[1 + (int)count + index];
                        Mdf4DataMap.Segment invalidation;
                        ulong expected = rows * invalidationBytes; // Both complete streams and row counts proved bounded above.
                        if (invalidationAddress == 0)
                        {
                            budget.ReserveMetadata(128, address);
                            invalidation = new Mdf4DataMap.Segment { Length = expected }; // Sparse logical zero extent, no sample-sized array.
                        }
                        else
                        {
                            invalidation = Mdf4DataMap.ReadSegment(resolver, invalidationAddress, token, budget, invalidationType);
                            if (headerList) { RequireHeaderListLeaf(invalidation, headerZip); }
                            if (invalidation.Length != expected) { throw Error(address, "DI length must equal its paired DV row count times invalidation bytes."); }
                        }
                        invalidation.Start = totalRows * invalidationBytes;
                        invalidations.Add(invalidation);
                    }
                    totalRows += rows;
                }
                address = list.Links[0];
            }
            if (totalRows != samples) { throw Error(rootAddress, "LD row sum does not equal the declared CG sample count."); }
            token.ThrowIfCancellationRequested();
            return new Mdf4ListDataMap
            {
                Values = Mdf4DataMap.FromSegments(values, depth + 1), Invalidations = Mdf4DataMap.FromSegments(invalidations, depth + 1)
            };
        }

        internal const string SignalFlagsReason = "Signal-data LD blocks support only the equal-sample-count and time/angle/distance summary flags.";

        // an exact 4.20 VLSD signal-data LD chain (optionally under a root HL with zip 0)
        // of nonempty SD / zlib DZ(SD) leaves. The logical stream is their data sections in link/chain order. Equal counts
        // and sample offsets count signal values: validated structurally, never interpreted; summary arrays are never read.
        internal static Mdf4DataMap BuildSignalStream(BlockResolver resolver, ReaderLimits limits, ReadBudget budget,
            ulong rootAddress, CancellationToken token)
        {
            budget.ReserveMetadata(640, rootAddress); // Map, builder/list/set and final array fixed allowance.
            List<Mdf4DataMap.Segment> segments = new List<Mdf4DataMap.Segment>();
            HashSet<ulong> chain = new HashSet<ulong>();
            ulong total = 0;
            ulong previousOffset = 0;
            bool first = true;
            int depth = 1;
            bool headerList = false;
            uint headerEqual = 0;
            ulong address = rootAddress;
            budget.CheckDepth(depth, rootAddress);
            BlockHeader root = resolver.Resolve(rootAddress, cancellationToken: token);
            if (root.Type == "##HL")
            {
                if (root.Links.Count != 1 || root.DataLength != 8) { throw Shape(root, "HL requires exactly one link and eight data bytes."); }
                byte[] header = resolver.ReadPayload(root, 8, token);
                ushort headerFlags = EndianBitConverter.ReadUInt16(header, 0, ByteOrder.LittleEndian);
                byte headerZip = header[2];
                if (headerFlags > 1 || headerZip > 1) { throw new NotSupportedException("Extended HL flags or compression modes are not supported."); }
                if (headerZip != 0) { throw new NotSupportedException("Transposed DZ signal data is not supported."); }
                if (root.Links[0] == 0) { throw new MdfFormatException("HL has no first LD link.", "MDF013", root.Offset, root.Type); }
                headerList = true;
                headerEqual = headerFlags;
                depth = 2;
                address = root.Links[0];
            }
            while (address != 0)
            {
                token.ThrowIfCancellationRequested();
                budget.CheckDepth(depth, address); // Siblings do not increase depth. No recursive list nesting.
                budget.ReserveMetadata(128, address);
                if (!chain.Add(address)) { throw new MdfFormatException("The list-data chain contains a cycle.", "MDF010", address, "##LD"); }
                BlockHeader list = resolver.Resolve(address, "##LD", cancellationToken: token);
                if (list.Links.Count < 2 || list.DataLength < 8) { throw Shape(list, "LD requires a next link, data links and flags/count fields."); }
                byte[] prefix = resolver.ReadPayload(list, 8, token);
                uint flags = U32(prefix, 0);
                uint count = U32(prefix, 4);
                if ((flags & ~0x0000000FU) != 0) { throw new NotSupportedException(SignalFlagsReason); }
                bool equalMode = (flags & 1) != 0;
                if (count == 0 || (ulong)list.Links.Count != 1 + (ulong)count) { throw Error(address, "LD count does not equal its signal-data link section."); }
                if (headerList && (flags & 1) != headerEqual) { throw Error(address, "HL and LD equal-sample flags disagree."); }
                ulong payloadBytes = equalMode ? 16UL : 8 + (ulong)count * 8; // Count is bounded by the resolved link count.
                ulong summaryBytes = (ulong)count * 8 * (((flags >> 1) & 1) + ((flags >> 2) & 1) + ((flags >> 3) & 1));
                if (list.DataLength != payloadBytes + summaryBytes) { throw Shape(list, "LD fields must exactly occupy its declared data section."); }
                ReadBudget.Check(nameof(MdfReaderOptions.MaximumReadBufferBytes), (ulong)limits.MaximumReadBufferBytes, 0, payloadBytes, address);
                byte[] fields = resolver.ReadPayload(list, (int)payloadBytes, token); // Summary arrays after these fields are never read.
                if (equalMode && U64(fields, 8) == 0) { throw Error(address, "LD equal sample count must be positive."); }
                for (int index = 0; index < (int)count; index++) // Count proved <= bounded Int32 links before this cast.
                {
                    token.ThrowIfCancellationRequested();
                    budget.CheckDepth(depth + 1, address);
                    if (!equalMode)
                    {
                        ulong offset = U64(fields, 8 + 8 * index);
                        if (first ? offset != 0 : offset < previousOffset)
                        { throw Error(address, "LD signal-value sample offsets must start at zero and never decrease."); }
                        previousOffset = offset;
                    }
                    first = false;
                    ulong leafAddress = list.Links[1 + index];
                    if (leafAddress == 0) { throw Error(address, "LD data link must not be null."); }
                    Mdf4DataMap.Segment leaf = Mdf4DataMap.ReadSegment(resolver, leafAddress, token, budget, "SD");
                    if (leaf.ZipType != 0) { throw new NotSupportedException("Transposed DZ signal data is not supported."); }
                    if (headerList) { RequireHeaderListLeaf(leaf, 0); }
                    if (leaf.Length == 0) { throw Error(address, "LD signal-data blocks must not be empty."); }
                    if (leaf.Length > ulong.MaxValue - total) { throw Error(address, "LD signal-data stream length overflows."); }
                    leaf.Start = total;
                    total += leaf.Length;
                    segments.Add(leaf);
                }
                address = list.Links[0];
            }
            token.ThrowIfCancellationRequested();
            return Mdf4DataMap.FromSegments(segments, depth + 1);
        }

        /// <summary>true when a 4.20 data root is an LD or an HL whose first link is an LD (header-only, no body).</summary>
        internal static bool IsListRoot(BlockResolver resolver, ulong rootAddress, CancellationToken token)
        {
            BlockHeader root = resolver.Resolve(rootAddress, cancellationToken: token);
            if (root.Type == "##LD") { return true; }
            if (root.Type != "##HL" || root.Links.Count < 1 || root.Links[0] == 0) { return false; }
            return resolver.Resolve(root.Links[0], cancellationToken: token).Type == "##LD";
        }

        private static void RequireHeaderListLeaf(Mdf4DataMap.Segment segment, byte zip)
        {
            if (segment.Block.Type != "##DZ" || segment.ZipType != zip)
            { throw new MdfFormatException("HL children must be DZ blocks of its declared compression mode.", "MDF013", segment.Block.Offset, segment.Block.Type); }
        }

        private static uint U32(byte[] bytes, int offset) { return EndianBitConverter.ReadUInt32(bytes, offset, ByteOrder.LittleEndian); }
        private static ulong U64(byte[] bytes, int offset) { return EndianBitConverter.ReadUInt64(bytes, offset, ByteOrder.LittleEndian); }
        private static MdfFormatException Shape(BlockHeader block, string message) { return new MdfFormatException(message, "MDF006", block.Offset, block.Type); }
        private static MdfFormatException Error(ulong address, string message) { return new MdfFormatException(message, "MDF013", address, "##LD"); }
    }
}
