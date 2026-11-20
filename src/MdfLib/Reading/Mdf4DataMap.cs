// Copyright 2021 Ingemar Hedvall
// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// DL/HL/DZ wire layouts adapted from pinned MIT mdflib; indexed range/budget policy is original.
using System;
using System.Collections.Generic;
using System.Threading;
using MdfLib.Formats;
using MdfLib.Internal;
using MdfLib.IO;

namespace MdfLib.Reading
{
    internal sealed class Mdf4DataMap
    {
        internal Segment[] Segments { get; private set; }
        internal int MaximumDepth { get; private set; }
        /// <summary>Total logical stream length (sum of nonempty segment lengths).</summary>
        internal ulong Total { get; private set; }

        internal static Mdf4DataMap Build(BlockResolver resolver, ReaderLimits limits, ReadBudget budget,
            ulong rootAddress, ulong samples, ulong recordBytes, CancellationToken token, bool summaries = false, string role = "DT")
        {
            if (samples > ulong.MaxValue / recordBytes) { throw Error(rootAddress, null, "Declared fixed record range overflows."); }
            return Build(resolver, limits, budget, rootAddress, samples * recordBytes, true, role, token, summaries);
        }

        // a logical SD-role stream (VLSD signal data) has no declared total; same bounded list/DZ policies.
        // in exact 4.20 (`listData`) an SD root that is an LD, or an HL over an LD, is a
        // signal-data list; every other root, and every pre-4.20 root, keeps the SD/DZ/DL/HL rules unchanged.
        internal static Mdf4DataMap BuildStream(BlockResolver resolver, ReaderLimits limits, ReadBudget budget,
            ulong rootAddress, string role, CancellationToken token, bool summaries = false, bool listData = false)
        {
            if (listData && role == "SD" && Mdf4ListDataMap.IsListRoot(resolver, rootAddress, token))
            {
                return Mdf4ListDataMap.BuildSignalStream(resolver, limits, budget, rootAddress, token);
            }
            return Build(resolver, limits, budget, rootAddress, ulong.MaxValue, false, role, token, summaries);
        }

        // `summaries` is true only for an exact MDF 4.20 record data (DT-role) stream.
        // Then DL time/angle/distance bits 1-3 are accepted with their exact arrays, which are never read.
        private static Mdf4DataMap Build(BlockResolver resolver, ReaderLimits limits, ReadBudget budget,
            ulong rootAddress, ulong expected, bool exact, string role, CancellationToken token, bool summaries)
        {
            byte knownFlags = summaries && role == "DT" ? (byte)0x0F : (byte)0x01;
            budget.ReserveMetadata(640, rootAddress); // Map, builder/list/set and immutable-array fixed allowance.
            List<Segment> segments = new List<Segment>();
            HashSet<ulong> chain = new HashSet<ulong>();
            ulong total = 0;
            int depth = 1;
            budget.CheckDepth(depth, rootAddress);
            BlockHeader root = resolver.Resolve(rootAddress, cancellationToken: token);
            bool headerList = root.Type == "##HL";
            byte expectedZip = 0;
            ushort expectedFlags = 0;
            if (headerList)
            {
                if (root.Links.Count != 1 || root.DataLength != 8) { throw Shape(root, "HL requires exactly one link and eight data bytes."); }
                byte[] data = resolver.ReadPayload(root, 8, token);
                expectedFlags = EndianBitConverter.ReadUInt16(data, 0, ByteOrder.LittleEndian);
                expectedZip = data[2];
                if (expectedFlags > 1 || expectedZip > 1) { throw new NotSupportedException("Extended HL flags or compression modes are not supported."); }
                if (!FixedRecords(role) && expectedZip != 0) { throw new NotSupportedException("Transposed DZ signal data is not supported."); }
                if (root.Links[0] == 0) { throw Error(root.Offset, root.Type, "HL has no first DL link."); }
                depth = 2;
                budget.CheckDepth(depth, root.Links[0]);
                root = resolver.Resolve(root.Links[0], "##DL", cancellationToken: token);
            }
            if (root.Type == "##DL")
            {
                while (root != null)
                {
                    token.ThrowIfCancellationRequested();
                    budget.ReserveMetadata(128, root.Offset); // Charge chain growth before HashSet allocation, including failed attempts.
                    if (!chain.Add(root.Offset)) { throw new MdfFormatException("The data-list chain contains a cycle.", "MDF010", root.Offset, root.Type); }
                    if (root.Links.Count < 2 || root.DataLength < 8) { throw Shape(root, "DL requires a next link, data links and a count."); }
                    byte[] prefix = resolver.ReadPayload(root, 8, token);
                    byte flags = prefix[0];
                    if ((flags & ~knownFlags) != 0) { throw new NotSupportedException("DL time/angle/distance or unknown extension flags are not supported."); }
                    bool equalMode = (flags & 1) != 0;
                    if (headerList && (flags & 1) != expectedFlags) { throw Error(root.Offset, root.Type, "HL and DL equal-length flags disagree."); }
                    uint count = EndianBitConverter.ReadUInt32(prefix, 4, ByteOrder.LittleEndian);
                    if (count == 0 || count != (uint)(root.Links.Count - 1)) { throw Error(root.Offset, root.Type, "DL count does not equal its data-link count."); }
                    ulong payloadSize = equalMode ? 16UL : 8 + (ulong)count * 8;
                    ulong summaryBytes = (ulong)count * 8 * (ulong)(((flags >> 1) & 1) + ((flags >> 2) & 1) + ((flags >> 3) & 1));
                    if (root.DataLength != payloadSize + summaryBytes) { throw Error(root.Offset, root.Type, "DL fields do not exactly occupy the declared payload."); }
                    ReadBudget.Check(nameof(MdfReaderOptions.MaximumReadBufferBytes), (ulong)limits.MaximumReadBufferBytes, 0, payloadSize, root.Offset);
                    byte[] data = resolver.ReadPayload(root, (int)payloadSize, token); // Summary arrays after this prefix are never read.
                    ulong equal = equalMode ? U64(data, 8) : 0;
                    if (equalMode && equal == 0) { throw Error(root.Offset, root.Type, "An equal-length DL cannot declare zero length."); }
                    for (int index = 0; index < (int)count; index++)
                    {
                        token.ThrowIfCancellationRequested();
                        budget.CheckDepth(depth + 1, root.Offset);
                        if (root.Links[index + 1] == 0) { throw Error(root.Offset, root.Type, "DL data link is null."); }
                        Segment segment = Leaf(resolver, root.Links[index + 1], token, budget, role);
                        if (headerList && (segment.Block.Type != "##DZ" || segment.ZipType != expectedZip))
                        { throw Error(segment.Block.Offset, segment.Block.Type, "HL children must be DZ blocks of its declared compression mode."); }
                        if (segment.Length == 0 || (equalMode ? (segment.Length > equal || (index + 1 < count && segment.Length != equal))
                            : U64(data, 8 + index * 8) != total))
                        { throw Error(root.Offset, root.Type, "DL lengths or contiguous logical offsets are inconsistent."); }
                        Add(segments, segment, ref total, expected);
                    }
                    ulong next = root.Links[0];
                    root = next == 0 ? null : resolver.Resolve(next, "##DL", cancellationToken: token);
                }
                depth++;
            }
            else
            {
                Add(segments, Leaf(resolver, root.Offset, token, budget, role), ref total, expected);
            }
            if (exact && total != expected) { throw Error(rootAddress, null, "Data stream length does not equal the declared fixed record count/size."); }
            token.ThrowIfCancellationRequested();
            return new Mdf4DataMap { Segments = segments.ToArray(), MaximumDepth = depth, Total = total };
        }

        private static Segment Leaf(BlockResolver resolver, ulong address, CancellationToken token, ReadBudget budget, string role)
        {
            Segment segment = ReadSegment(resolver, address, token, budget, role);
            if (!FixedRecords(role) && segment.ZipType != 0) { throw new NotSupportedException("Transposed DZ signal data is not supported."); }
            return segment;
        }

        // RD reduction records are fixed-size like DT records, so transposed DZ(RD) is meaningful.
        private static bool FixedRecords(string role) { return role == "DT" || role == "RD"; }

        internal static Mdf4DataMap FromSegments(List<Segment> segments, int maximumDepth)
        {
            ulong total = 0;
            foreach (Segment segment in segments) { total += segment.Length; }
            return new Mdf4DataMap { Segments = segments.ToArray(), MaximumDepth = maximumDepth, Total = total };
        }

        internal static Segment ReadSegment(BlockResolver resolver, ulong address, CancellationToken token, ReadBudget budget, string originalType = "DT")
        {
            budget.ReserveMetadata(128, address); // Segment/list-capacity allowance before constructing a segment.
            BlockHeader block = resolver.Resolve(address, cancellationToken: token);
            if (block.Type != "##" + originalType && block.Type != "##DZ")
            { throw new NotSupportedException("Expected a " + originalType + " or role-matching DZ segment, found " + block.Type + ". Nested/other data layouts are not supported."); }
            if (block.Links.Count != 0) { throw Shape(block, "Raw data and DZ leaves must not have links."); }
            if (block.Type != "##DZ") { return new Segment { Block = block, Length = block.DataLength }; }
            if (block.DataLength < 24) { throw Shape(block, "DZ is shorter than its fixed fields."); }
            byte[] data = resolver.ReadPayload(block, 24, token);
            if (data[0] != (byte)originalType[0] || data[1] != (byte)originalType[1])
            { throw new NotSupportedException("The DZ original block type must match its " + originalType + " stream role."); }
            byte zip = data[2];
            if (zip > 1) { throw new NotSupportedException("Only zlib and transpose+zlib DZ modes are supported."); }
            uint parameter = EndianBitConverter.ReadUInt32(data, 4, ByteOrder.LittleEndian);
            ulong compressed = U64(data, 16);
            if (compressed == 0 || compressed != block.DataLength - 24)
            { throw Error(block.Offset, block.Type, "DZ compressed length does not equal its declared section."); }
            if (zip == 0 ? parameter != 0 : parameter == 0)
            { throw new MdfFormatException("Invalid DZ transpose parameter for its compression mode.", "MDF015", block.Offset, block.Type,
                new System.IO.InvalidDataException("Invalid transpose parameter.")); }
            return new Segment { Block = block, Length = U64(data, 8), CompressedLength = compressed, Parameter = parameter, ZipType = zip };
        }

        private static void Add(List<Segment> segments, Segment segment, ref ulong total, ulong expected)
        {
            if (segment.Length > expected - total) { throw Error(segment.Block.Offset, segment.Block.Type, "Data stream exceeds the declared fixed record count/size."); }
            if (segment.Length == 0) { return; } // Direct empty DT/DZ has no logical segment; zero requests never need this map.
            segment.Start = total;
            total += segment.Length;
            segments.Add(segment);
        }
        private static ulong U64(byte[] bytes, int offset) { return EndianBitConverter.ReadUInt64(bytes, offset, ByteOrder.LittleEndian); }
        private static MdfFormatException Shape(BlockHeader block, string message) { return new MdfFormatException(message, "MDF006", block.Offset, block.Type); }
        private static MdfFormatException Error(ulong offset, string type, string message) { return new MdfFormatException(message, "MDF013", offset, type); }

        internal sealed class Segment
        {
            internal BlockHeader Block { get; set; }
            internal ulong Start { get; set; }
            internal ulong Length { get; set; }
            internal ulong CompressedLength { get; set; }
            internal uint Parameter { get; set; }
            internal byte ZipType { get; set; }
        }
    }
}
