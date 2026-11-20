// Copyright 2021 Ingemar Hedvall
// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// MDF 3 wire fields adapted from the pinned MIT upstream. New traversal/model/security policy
using System;
using System.Collections.Generic;
using System.Threading;
using MdfLib.Internal;
using MdfLib.IO;
using MdfLib.Reading;

namespace MdfLib.Formats.Mdf3
{
    internal sealed class MetadataReader
    {
        private readonly BlockResolver resolver;
        private readonly ReaderLimits limits;
        private readonly ReadBudget budget;
        private readonly MdfFileHeader header;
        private readonly object document;
        private readonly CancellationToken token;
        private readonly HashSet<ulong> structural = new HashSet<ulong>();
        private readonly Dictionary<ulong, string> texts = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, MdfSourceInformation> sources = new Dictionary<ulong, MdfSourceInformation>();
        private readonly Dictionary<ulong, MdfChannelConversion> conversions = new Dictionary<ulong, MdfChannelConversion>();

        internal MetadataReader(BlockResolver resolver, ReaderLimits limits, ReadBudget budget, MdfFileHeader header, object document, CancellationToken token)
        {
            this.resolver = resolver;
            this.limits = limits;
            this.budget = budget;
            this.header = header;
            this.document = document;
            this.token = token;
        }

        internal IReadOnlyList<MdfDataGroup> Read()
        {
            string version = header.Version;
            BlockHeader hd = resolver.Resolve(64, "HD", HeaderCodec.MinimumSize((version[0] - '0') * 100 + (version[2] - '0') * 10 + version[3] - '0'), 3, token);
            List<MdfDataGroup> groups = new List<MdfDataGroup>();
            HashSet<ulong> chain = NewChain(64);
            ulong offset = hd.Links[0];
            while (offset != 0)
            {
                Own(offset, 1, chain);
                BlockHeader block = resolver.Resolve(offset, "DG", 24, 4, token);
                RequireSize(block, 24, 28);
                byte[] data = resolver.ReadPayload(block, (int)block.DataLength, token);
                IReadOnlyList<MdfChannelGroup> children = ReadChannelGroups(block.Links[1], block.Links[3], U16(data, 2), offset);
                RequireCount(block, U16(data, 0), children.Count);
                groups.Add(new MdfDataGroup { Index = groups.Count, Comment = string.Empty, ChannelGroups = children });
                offset = block.Links[0];
            }
            RequireCount(hd, header.DataGroupCount.Value, groups.Count);
            // Headerless data has no DT header. Reserve its complete known range only after all metadata extents exist.
            HashSet<ulong> dataOwners = NewChain(64);
            foreach (MdfDataGroup group in groups)
            {
                for (int index = 0; index < group.ChannelGroups.Count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    MdfChannelGroup child = group.ChannelGroups[index];
                    RecordLayout layout = child.Layout;
                    InterleavedLayout interleaved = layout.Interleaved;
                    if (!child.CanReadSamples || (interleaved != null && index != 0)) { continue; }
                    // One combined extent for an interleaved DG, not overlapping pseudo-extents for each CG.
                    ulong length = interleaved == null ? child.SampleCount * child.RecordByteCount : interleaved.TotalBytes;
                    if (length == 0) { continue; }
                    if (layout.DataAddress == 0) { throw ScalarError(layout.BlockAddress, "CG", "A nonempty group has no raw data address."); }
                    if (!dataOwners.Add(layout.DataAddress))
                    {
                        throw new MdfFormatException("Two data groups share a headerless data range.", "MDF014", layout.DataAddress, "DT");
                    }
                    resolver.RegisterRawDataRange(layout.DataAddress, length, token);
                }
            }
            return Array.AsReadOnly(groups.ToArray());
        }

        private IReadOnlyList<MdfChannelGroup> ReadChannelGroups(ulong offset, ulong dataAddress, ushort recordIdCount, ulong dataGroupAddress)
        {
            List<MdfChannelGroup> groups = new List<MdfChannelGroup>();
            HashSet<ulong> chain = NewChain(offset);
            while (offset != 0)
            {
                Own(offset, 2, chain);
                BlockHeader block = resolver.Resolve(offset, "CG", 26, 3, token);
                RequireSize(block, 26, 30);
                byte[] data = resolver.ReadPayload(block, (int)block.DataLength, token);
                if (block.Length == 30)
                {
                    budget.ReserveAdditionalLinks(3, 1, offset);
                    TailLink(block, data, 10); // SR is ancillary, not a second data source.
                }
                ushort dataBytes = U16(data, 4);
                IReadOnlyList<MdfChannel> channels = ReadChannels(block.Links[1], dataBytes, dataGroupAddress, offset);
                RequireCount(block, U16(data, 2), channels.Count);
                MdfChannelGroup group = new MdfChannelGroup
                {
                    Document = document, Index = groups.Count, Name = string.Empty,
                    Comment = Text(block.Links[2], 3), Source = null,
                    SampleCount = U32(data, 6), RecordByteCount = dataBytes, Channels = channels,
                    Layout = new RecordLayout { DataAddress = dataAddress, BlockAddress = offset, DataBytes = dataBytes, HeaderlessData = true, RecordId = U16(data, 0) }
                };
                if (dataBytes == 0) { group.UnsupportedReason = "A sample record must have a nonzero fixed size."; }
                groups.Add(group);
                offset = block.Links[0];
            }
            IReadOnlyList<MdfChannelGroup> result = Array.AsReadOnly(groups.ToArray());
            if (recordIdCount == 1 || recordIdCount == 2)
            {
                InterleavedLayout.Attach(result, 1, recordIdCount == 2 ? 1 : 0, budget, dataGroupAddress, "DG", token);
            }
            else if (recordIdCount != 0 || groups.Count != 1)
            {
                foreach (MdfChannelGroup group in groups)
                {
                    group.UnsupportedReason = recordIdCount == 0 ? "Multiple channel groups require explicit record IDs."
                        : "Only one prefix record-ID byte and an optional matching suffix are supported.";
                }
            }
            return result;
        }

        private IReadOnlyList<MdfChannel> ReadChannels(ulong offset, ushort dataBytes, ulong dataGroupAddress, ulong channelGroupAddress)
        {
            List<MdfChannel> channels = new List<MdfChannel>();
            List<ulong> addresses = new List<ulong>();
            List<ulong> dependencies = new List<ulong>();
            List<int> parents = new List<int>();
            HashSet<ulong> chain = NewChain(offset);
            while (offset != 0)
            {
                Own(offset, 3, chain);
                BlockHeader block = resolver.Resolve(offset, "CN", 218, 5, token);
                if (block.Length != 218 && block.Length != 222 && block.Length != 228) { throw Shape(block, "Unknown or partial CN tail layout."); }
                byte[] data = resolver.ReadPayload(block, (int)block.DataLength, token);
                int extraLinks = block.Length == 218 ? 0 : block.Length == 222 ? 1 : 2;
                budget.ReserveAdditionalLinks(5, extraLinks, offset);
                ulong longNameAddress = extraLinks == 0 ? 0 : TailLink(block, data, 194);
                if (extraLinks == 2) { Text(TailLink(block, data, 198), 4); } // Validate display alias, not the primary name.
                ushort role = U16(data, 0);
                ushort bits = U16(data, 164);
                int floatBits;
                byte encoding = NormalizeEncoding(U16(data, 166), out floatBits);
                ushort start = U16(data, 162);
                ChannelLayout layout = new ChannelLayout
                {
                    ByteOffset = (uint)(start / 8) + (block.Length == 228 ? U16(data, 202) : 0U),
                    BitOffset = start % 8, BitCount = bits <= 64 ? bits : 0, DataType = encoding
                };
                ushort wireType = U16(data, 166);
                layout.CnType = (byte)role;
                layout.CnDataType = (byte)wireType;
                bool textual = wireType == 7 || wireType == 8;
                bool dependency = block.Links[3] != 0;
                string unsupported = textual ? TextSupport(block, role, bits, layout, dataBytes, wireType)
                    : ScalarSupport(block, role, bits, floatBits, layout, dataBytes, dependency);
                if (dependency && !textual && unsupported == null) { parents.Add(channels.Count); }
                MdfChannelConversion conversion = Conversion(block.Links[1], 4);
                if (unsupported == null && textual && conversion.Kind != MdfConversionKind.Identity)
                { unsupported = "String and byte-array channels support only identity conversion."; }
                if (unsupported != null) { layout.TextStorage = 0; } // Never decode an unsupported field as text.
                string shortName = Fixed(block, data, 2, 32);
                string description = Fixed(block, data, 34, 128);
                string longName = Text(longNameAddress, 4);
                channels.Add(new MdfChannel
                {
                    Index = channels.Count, Layout = layout, Name = longName.Length == 0 ? shortName : longName,
                    Comment = block.Links[4] == 0 ? description : Text(block.Links[4], 4), Unit = conversion.Unit,
                    Source = Source(block.Links[2], 4), Conversion = conversion, HasConversionBlock = block.Links[1] != 0, FixedDescription = description, BitCount = bits,
                    IsMaster = role == 1, SynchronizationType = role == 1 ? (byte)1 : (byte)0,
                    ValueKind = textual ? (wireType == 7 ? MdfValueKind.Text : MdfValueKind.ByteArray)
                        : encoding <= 1 ? MdfValueKind.UnsignedInteger : encoding <= 3 ? MdfValueKind.SignedInteger
                        : encoding <= 5 ? MdfValueKind.FloatingPoint : MdfValueKind.Unsupported,
                    UnsupportedReason = unsupported
                });
                addresses.Add(offset);
                dependencies.Add(block.Links[3]);
                offset = block.Links[0];
            }
            if (parents.Count != 0)
            {
                // Every CN of the group is known before any CD reference is interpreted (no dereference of references).
                budget.ReserveMetadata(64UL + 48UL * (ulong)channels.Count, channelGroupAddress);
                Dictionary<ulong, int> byAddress = new Dictionary<ulong, int>();
                for (int index = 0; index < addresses.Count; index++) { byAddress.Add(addresses[index], index); }
                foreach (int index in parents)
                {
                    ReadDependency(channels[index], index, channels[index].Layout, dependencies, channels, byAddress, dataGroupAddress, channelGroupAddress);
                }
            }
            return Array.AsReadOnly(channels.ToArray());
        }

        // MDF 3.30 N-dimensional CD (256+N) over contiguous same-CG numeric element CNs -> MdfChannel.Array.
        private void ReadDependency(MdfChannel parent, int parentIndex, ChannelLayout layout, List<ulong> dependencies, List<MdfChannel> channels,
            Dictionary<ulong, int> byAddress, ulong dataGroupAddress, ulong channelGroupAddress)
        {
            ulong address = dependencies[parentIndex];
            token.ThrowIfCancellationRequested();
            budget.CheckDepth(4, address);
            BlockHeader block = resolver.Resolve(address, "CD", 8, 0, token);
            byte[] prefix = resolver.ReadPayload(block, 4, token);
            int type = U16(prefix, 0);
            int count = U16(prefix, 2);
            ulong referenceBytes = 8UL + 12UL * (ulong)count;
            if (block.Length < referenceBytes) { throw Shape(block, "The CD references exceed the declared block size."); }
            int rank = type > 256 ? type - 256 : 0;
            if (rank != 0 && block.Length != referenceBytes + 2UL * (ulong)rank)
            {
                throw Shape(block, "An N-dimensional CD requires exactly its reference and dimension sections.");
            }
            if (rank == 0)
            {
                parent.UnsupportedReason = "Only N-dimensional (256+N) MDF 3 channel dependencies are supported.";
                return;
            }
            // Shape/reference allowance precedes allocation; n <= 65535 and N <= 65279 by the UInt16 fields.
            budget.ReserveMetadata(256UL + 16UL * (ulong)count + 8UL * (ulong)rank, address);
            byte[] data = resolver.ReadPayload(block, (int)block.DataLength, token);
            ulong[] shape = new ulong[rank];
            ulong product = 1;
            for (int index = 0; index < rank; index++)
            {
                token.ThrowIfCancellationRequested();
                ushort size = U16(data, 4 + 12 * count + 2 * index);
                if (size == 0) { throw ScalarError(address, block.Type, "CD dimension sizes must be positive."); }
                product *= size; // product <= 65535 before each step, so no overflow.
                if (product > (ulong)count) { throw ScalarError(address, block.Type, "The CD dimension product must equal its reference count."); }
                shape[rank - 1 - index] = size; // Stored reversed; exposed row-major with the last index fastest.
            }
            if (product != (ulong)count) { throw ScalarError(address, block.Type, "The CD dimension product must equal its reference count."); }
            parent.Array = new MdfChannelArray(Array.AsReadOnly(shape), product);
            int[] elements = new int[count];
            HashSet<int> seen = new HashSet<int>();
            bool sameGroup = true;
            for (int index = 0; index < count; index++)
            {
                token.ThrowIfCancellationRequested();
                int at = 4 + 12 * index;
                if (U32(data, at) != dataGroupAddress || U32(data, at + 4) != channelGroupAddress) { sameGroup = false; continue; }
                int element;
                if (!byAddress.TryGetValue(U32(data, at + 8), out element))
                {
                    throw ScalarError(address, block.Type, "A same-group CD reference does not name a channel of that group.");
                }
                if (element == parentIndex || !seen.Add(element)) { sameGroup = false; }
                elements[index] = element;
            }
            if (parent.IsMaster) { parent.UnsupportedReason = "Array master channels are not supported; a stored master must be scalar."; return; }
            if (!sameGroup)
            {
                parent.UnsupportedReason = "Channel dependency elements must be distinct other channels of the same channel group.";
                return;
            }
            // elements of one bit count 1..64, one bit offset 0..7 and one encoding/conversion at a constant positive
            // byte stride; element e is then exactly the e-th referenced scalar (padding and overlapping containers allowed).
            uint bits = parent.BitCount;
            bool width = bits >= 1 && bits <= 64 && (parent.ValueKind != MdfValueKind.FloatingPoint || bits == 32 || bits == 64);
            int bitOffset = channels[elements[0]].Layout.BitOffset;
            foreach (int element in elements)
            {
                MdfChannel candidate = channels[element];
                ChannelLayout candidateLayout = candidate.Layout;
                if (!width || candidate.UnsupportedReason != null || candidate.IsMaster || dependencies[element] != 0
                    || candidateLayout.DataType != layout.DataType || candidate.BitCount != bits || candidateLayout.BitOffset != bitOffset
                    || !SameConversion(candidate.Conversion, parent.Conversion))
                {
                    parent.UnsupportedReason = "Channel dependency elements must be supported numeric scalars of the parent's encoding, bit count, bit offset and conversion.";
                    return;
                }
            }
            ulong first = channels[elements[0]].Layout.ByteOffset;
            ulong stride = count > 1 ? channels[elements[1]].Layout.ByteOffset - first : 1UL; // Wraps for a descending pair.
            for (int index = 0; index < count; index++)
            {
                if (stride == 0 || stride > ushort.MaxValue || channels[elements[index]].Layout.ByteOffset != first + (ulong)index * stride)
                {
                    parent.UnsupportedReason = "Channel dependency elements must be evenly spaced in reference order at a positive byte stride.";
                    return;
                }
            }
            // Elements were each range-checked (with their container) against the record, so every element is inside it.
            layout.ByteOffset = (uint)first;
            layout.BitOffset = bitOffset;
            layout.ElementByteStride = (int)stride;
        }

        private static bool SameConversion(MdfChannelConversion left, MdfChannelConversion right)
        {
            if (ReferenceEquals(left, right)) { return true; }
            if (left.Kind != right.Kind || left.Kind == MdfConversionKind.Unsupported || left.Values.Length != right.Values.Length) { return false; }
            for (int i = 0; i < left.Values.Length; i++)
            {
                if (!left.Values[i].Equals(right.Values[i])) { return false; } // double.Equals, as the former Factor/Offset comparison
            }
            if (!string.Equals(left.Formula, right.Formula, StringComparison.Ordinal)) { return false; }
            if (left.References.Length != right.References.Length) { return false; }
            for (int i = 0; i < left.References.Length; i++)
            {
                if (!string.Equals(left.References[i], right.References[i], StringComparison.Ordinal)) { return false; }
            }
            return true;
        }

        private byte NormalizeEncoding(ushort kind, out int floatBits)
        {
            floatBits = 0;
            bool big = header.DefaultDataIsBigEndian;
            if (kind >= 9 && kind <= 16) { big = kind <= 12; kind = (ushort)((kind - 9) % 4); }
            if (kind == 0) { return big ? (byte)1 : (byte)0; }
            if (kind == 1) { return big ? (byte)3 : (byte)2; }
            if (kind == 2 || kind == 3)
            {
                floatBits = kind == 2 ? 32 : 64;
                return big ? (byte)5 : (byte)4;
            }
            return byte.MaxValue;
        }

        // fixed in-record MDF 3.30 String (7, decoded by the ID code page) and Byte Array (8) fields.
        private string TextSupport(BlockHeader block, ushort role, ushort bits, ChannelLayout layout, ushort dataBytes, ushort wireType)
        {
            if (role != 0) { return "Only stored value string and byte-array channels are supported."; }
            if (bits == 0 || layout.BitOffset != 0 || bits % 8 != 0)
            {
                return "MDF 3 string and byte-array fields must be nonempty, byte-aligned whole bytes.";
            }
            if (layout.ByteOffset > dataBytes || (ulong)(bits / 8) > (ulong)dataBytes - layout.ByteOffset)
            {
                throw ScalarError(block.Offset, block.Type, "String or byte-array field extends beyond its record.");
            }
            if (block.Links[3] != 0) { return "Text and byte-array arrays are not supported."; } // CD is not read.
            int codePage = header.CodePage.Value;
            layout.DataType = wireType == 8 ? (byte)10 : codePage == 65001 ? (byte)7 : codePage == 20127 ? TextDecoder.Ascii : (byte)6;
            layout.TextStorage = 1;
            layout.FieldBytes = (uint)(bits / 8);
            return null;
        }

        private static string ScalarSupport(BlockHeader block, ushort role, ushort bits, int floatBits, ChannelLayout layout, ushort dataBytes, bool dependency)
        {
            if (role != 0 && role != 1) { return "Only stored value and time master channel roles are supported."; }
            if (layout.DataType == byte.MaxValue) { return "Only integer and IEEE32/64 numeric encodings are supported."; }
            if (bits == 0) { throw ScalarError(block.Offset, block.Type, "Numeric bit count cannot be zero."); }
            if (dependency) { return null; } // a CD parent's own start is not interpreted; its CD decides (second pass).
            ulong span = ((ulong)bits + (ulong)layout.BitOffset + 7) / 8;
            if (layout.ByteOffset > dataBytes || span > (ulong)dataBytes - layout.ByteOffset)
            {
                throw ScalarError(block.Offset, block.Type, "Numeric field extends beyond its record.");
            }
            if (bits > 64) { return "Integer widths greater than 64 bits are not supported."; }
            if (floatBits != 0 && (layout.BitOffset != 0 || bits != floatBits))
            {
                return "Only byte-aligned IEEE storage matching the declared 32/64-bit type is supported.";
            }
            return null;
        }

        private MdfChannelConversion Conversion(ulong offset, int depth)
        {
            if (offset == 0) { return MdfChannelConversion.Identity; }
            token.ThrowIfCancellationRequested();
            budget.CheckDepth(depth, offset);
            MdfChannelConversion cached;
            if (conversions.TryGetValue(offset, out cached)) { return cached; }
            BlockHeader block = resolver.Resolve(offset, "CC", 46, cancellationToken: token);
            byte[] data = resolver.ReadPayload(block, 42, token);
            ushort kind = U16(data, 38);
            ushort count = U16(data, 40);
            if ((kind == 65535 && (count != 0 || block.Length != 46)) || (kind == 0 && (count != 2 || block.Length != 62)))
            {
                throw ScalarError(offset, block.Type, "Identity/linear CC has incompatible size or parameter count.");
            }
            MdfConversionKind numeric = kind == 9 ? MdfConversionKind.Rational : kind == 6 ? MdfConversionKind.Polynomial
                : kind == 1 ? MdfConversionKind.TableInterpolation : kind == 2 ? MdfConversionKind.Table
                : kind == 7 ? MdfConversionKind.Exponential : kind == 8 ? MdfConversionKind.Logarithmic : MdfConversionKind.Unsupported;
            // rational/polynomial exactly 6 parameters; tables count >= 1 (key, value) pairs; exact block length.
            int doubles = numeric == MdfConversionKind.Table || numeric == MdfConversionKind.TableInterpolation ? 2 * count : count;
            if (numeric != MdfConversionKind.Unsupported && (doubles == 0 || block.Length != 46UL + 8UL * (ulong)doubles
                || ((numeric == MdfConversionKind.Rational || numeric == MdfConversionKind.Polynomial) && count != 6)
                || ((numeric == MdfConversionKind.Exponential || numeric == MdfConversionKind.Logarithmic) && count != 7)))
            {
                throw ScalarError(offset, block.Type, "Numeric value-to-value CC has incompatible size or parameter count.");
            }
            MdfConversionKind textual = kind == 11 ? MdfConversionKind.ValueToText : kind == 12 ? MdfConversionKind.RangeToText : MdfConversionKind.Unsupported;
            // TABX n x (value, char[32]); RTABX n + 1 x (lower, upper, TX link) with entry 0 = default; exact length.
            int entry = textual == MdfConversionKind.ValueToText ? 40 : 20;
            if (textual != MdfConversionKind.Unsupported
                && ((textual == MdfConversionKind.RangeToText && count == 0) || block.Length != 46UL + (ulong)entry * count))
            {
                throw ScalarError(offset, block.Type, "Text CC has incompatible size or entry count.");
            }
            budget.ReserveMetadata(384 + (numeric != MdfConversionKind.Unsupported ? 16UL * (ulong)doubles : 0)
                + (textual != MdfConversionKind.Unsupported ? 112UL * count : 0), offset);
            MdfChannelConversion result = new MdfChannelConversion
            {
                Kind = kind == 65535 ? MdfConversionKind.Identity : kind == 0 ? MdfConversionKind.Linear : MdfConversionKind.Unsupported,
                Factor = kind == 65535 ? (double?)1 : null, Offset = kind == 65535 ? (double?)0 : null,
                Unit = Fixed(block, data, 18, 20), Name = string.Empty, Comment = string.Empty
            };
            if (kind == 0)
            {
                byte[] parameters = resolver.ReadPayload(block, 58, token);
                result.Offset = EndianBitConverter.ReadDouble(parameters, 42, ByteOrder.LittleEndian);
                result.Factor = EndianBitConverter.ReadDouble(parameters, 50, ByteOrder.LittleEndian);
                result.Values = new[] { result.Offset.Value, result.Factor.Value };
            }
            else if (numeric != MdfConversionKind.Unsupported)
            {
                byte[] parameters = resolver.ReadPayload(block, 42 + 8 * doubles, token);
                double[] wire = new double[doubles];
                for (int i = 0; i < wire.Length; i++) { wire[i] = EndianBitConverter.ReadDouble(parameters, 42 + 8 * i, ByteOrder.LittleEndian); }
                if (PhysicalConversion.Valid(numeric, wire))
                {
                    result.Kind = numeric;
                    result.Values = wire;
                }
            }
            else if (kind == 10)
            {
                // TEXT FORMULA occupies the rest of the block (CHAR[256] in MDF 3.3); the count field is not a length.
                int length = (int)block.Length - 46;
                ReadBudget.Check(nameof(MdfReaderOptions.MaximumTextBytes), (ulong)limits.MaximumTextBytes, 0, (ulong)length, offset);
                budget.ReserveMetadata(96UL + 42UL * (ulong)length, offset);
                byte[] parameters = resolver.ReadPayload(block, 42 + length, token);
                string formula = Fixed(block, parameters, 42, length);
                result.Formula = formula;
                result.CompiledFormula = AlgebraicFormula.Compile(formula);
                if (result.CompiledFormula != null) { result.Kind = MdfConversionKind.Algebraic; }
            }
            else if (textual != MdfConversionKind.Unsupported)
            {
                byte[] parameters = resolver.ReadPayload(block, 42 + entry * count, token);
                double[] wire;
                string[] references;
                if (textual == MdfConversionKind.ValueToText)
                {
                    wire = new double[count];
                    references = new string[count + 1];
                    for (int i = 0; i < count; i++)
                    {
                        wire[i] = EndianBitConverter.ReadDouble(parameters, 42 + 40 * i, ByteOrder.LittleEndian);
                        references[i] = Fixed(block, parameters, 50 + 40 * i, 32);
                    }
                    references[count] = string.Empty; // TABX has no default text.
                }
                else
                {
                    wire = new double[2 * (count - 1)];
                    references = new string[count];
                    for (int k = 0; k < count; k++)
                    {
                        if (k > 0)
                        {
                            wire[2 * k - 2] = EndianBitConverter.ReadDouble(parameters, 42 + 20 * k, ByteOrder.LittleEndian);
                            wire[2 * k - 1] = EndianBitConverter.ReadDouble(parameters, 50 + 20 * k, ByteOrder.LittleEndian);
                        }
                        ulong link = TailLink(block, parameters, 58 + 20 * k);
                        references[k == 0 ? count - 1 : k - 1] = link == 0 ? null : Text(link, depth + 1);
                    }
                }
                if (PhysicalConversion.Valid(textual, wire))
                {
                    result.Kind = textual;
                    result.Values = wire;
                    result.References = references;
                }
            }
            conversions.Add(offset, result);
            return result;
        }

        private MdfSourceInformation Source(ulong offset, int depth)
        {
            if (offset == 0) { return null; }
            token.ThrowIfCancellationRequested();
            budget.CheckDepth(depth, offset);
            MdfSourceInformation cached;
            if (sources.TryGetValue(offset, out cached)) { return cached; }
            BlockHeader block = resolver.Resolve(offset, "CE", 6, cancellationToken: token);
            byte[] kindData = resolver.ReadPayload(block, 2, token);
            ushort kind = U16(kindData, 0);
            if (kind != 2 && kind != 19) { throw new NotSupportedException("Unsupported MDF 3 CE source type " + kind + "."); }
            if (block.Length != 128) { throw Shape(block, "Known CE source requires exactly 128 bytes."); }
            budget.ReserveMetadata(512, offset);
            byte[] data = resolver.ReadPayload(block, 124, token);
            MdfSourceInformation result = new MdfSourceInformation
            {
                Name = Fixed(block, data, kind == 2 ? 8 : 10, kind == 2 ? 80 : 36),
                Path = Fixed(block, data, kind == 2 ? 88 : 46, kind == 2 ? 32 : 36),
                SourceType = kind == 2 ? (byte)1 : (byte)2, BusType = kind == 2 ? (byte)0 : (byte)2,
                Comment = string.Empty, Flags = 0
            };
            sources.Add(offset, result);
            return result;
        }

        private string Text(ulong offset, int depth)
        {
            if (offset == 0) { return string.Empty; }
            token.ThrowIfCancellationRequested();
            budget.CheckDepth(depth, offset);
            string cached;
            if (texts.TryGetValue(offset, out cached)) { return cached; }
            BlockHeader block = resolver.Resolve(offset, "TX", 4, cancellationToken: token);
            ReadBudget.Check(nameof(MdfReaderOptions.MaximumTextBytes), (ulong)limits.MaximumTextBytes, 0, block.DataLength, offset);
            // Declared MDF 3 block length is UInt16; allowance also covers cached decoded string/entry before allocation.
            budget.ReserveMetadata(96 + 2 * block.DataLength, offset);
            byte[] data = resolver.ReadPayload(block, (int)block.DataLength, token);
            string result = FixedText.Read(data, 0, data.Length, header.CodePage.Value, block.DataOffset, block.Type);
            texts.Add(offset, result);
            return result;
        }

        private ulong TailLink(BlockHeader block, byte[] data, int position)
        {
            ulong result = U32(data, position);
            resolver.ValidateAdditionalLink(result, block.DataOffset + (ulong)position, block.Type);
            return result;
        }

        private string Fixed(BlockHeader block, byte[] data, int start, int count)
        {
            // The engine keeps the leading white space; the facade applies the wrapper's trim (FacadeFormat.Trim).
            return FixedText.Read(data, start, count, header.CodePage.Value, block.DataOffset + (ulong)start, block.Type);
        }

        private void Own(ulong offset, int depth, HashSet<ulong> chain)
        {
            token.ThrowIfCancellationRequested();
            budget.CheckDepth(depth, offset);
            budget.ReserveMetadata(1280, offset); // Model/layout/fixed strings, list/array and ownership/chain entries.
            if (!chain.Add(offset)) { throw new MdfFormatException("A sibling block chain contains a cycle.", "MDF010", offset); }
            if (!structural.Add(offset)) { throw new MdfFormatException("A structural block has multiple or incompatible owners.", "MDF014", offset); }
        }

        private HashSet<ulong> NewChain(ulong offset)
        {
            budget.ReserveMetadata(192, offset);
            return new HashSet<ulong>();
        }

        private static void RequireSize(BlockHeader block, int first, int second)
        {
            if (block.Length != (ulong)first && block.Length != (ulong)second) { throw Shape(block, "Unknown or partial structural block layout."); }
        }
        private static void RequireCount(BlockHeader block, int declared, int actual)
        {
            if (declared != actual) { throw ScalarError(block.Offset, block.Type, "Declared child count does not match the actual sibling chain."); }
        }
        private static MdfFormatException Shape(BlockHeader block, string message) { return new MdfFormatException(message, "MDF006", block.Offset, block.Type); }
        private static MdfFormatException ScalarError(ulong offset, string type, string message) { return new MdfFormatException(message, "MDF013", offset, type); }
        private static ushort U16(byte[] data, int offset) { return EndianBitConverter.ReadUInt16(data, offset, ByteOrder.LittleEndian); }
        private static uint U32(byte[] data, int offset) { return EndianBitConverter.ReadUInt32(data, offset, ByteOrder.LittleEndian); }
    }
}
