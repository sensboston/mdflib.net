// Copyright 2021 Ingemar Hedvall
// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// DG/CG/CN/CA/CC/SI layouts adapted from pinned MIT mdflib (CA: Copyright 2021 Ingemar Hedvall).
// Traversal/security/model policy is new; CA shape never expands into per-element models.
using System;
using System.Collections.Generic;
using System.Threading;
using MdfLib.Internal;
using MdfLib.IO;
using MdfLib.Metadata;
using MdfLib.Reading;

namespace MdfLib.Formats.Mdf4
{
    internal sealed class MetadataReader
    {
        private readonly BlockResolver resolver;
        private readonly ReadBudget budget;
        private readonly object document;
        private readonly CancellationToken token;
        private readonly MetadataTextReader text;
        private readonly bool mdf420;
        private readonly Dictionary<ulong, MdfChannelGroup> channelGroups;
        private readonly HashSet<ulong> structural = new HashSet<ulong>();
        private readonly Dictionary<ulong, Cached<MdfSourceInformation>> sources = new Dictionary<ulong, Cached<MdfSourceInformation>>();
        private readonly Dictionary<ulong, Cached<MdfChannelConversion>> conversions = new Dictionary<ulong, Cached<MdfChannelConversion>>();
        private readonly HashSet<ulong> resolvingConversions = new HashSet<ulong>(); // bitfield part reference cycles.

        internal MetadataReader(BlockResolver resolver, ReaderLimits limits, ReadBudget budget, object document, bool mdf420, CancellationToken token)
        {
            this.resolver = resolver;
            this.budget = budget;
            this.document = document;
            this.mdf420 = mdf420;
            this.token = token;
            // Fixed catalog structures are covered by the facade's pre-allocation metadata allowance.
            if (mdf420) { channelGroups = new Dictionary<ulong, MdfChannelGroup>(); }
            text = new MetadataTextReader(resolver, limits, budget, token);
        }

        internal IReadOnlyList<MdfDataGroup> Read()
        {
            BlockHeader hd = resolver.Resolve(64, "##HD", 104, cancellationToken: token);
            List<MdfDataGroup> groups = new List<MdfDataGroup>();
            HashSet<ulong> chain = NewChain(64);
            ulong offset = hd.Links[0];
            while (offset != 0)
            {
                Own(offset, 1, chain);
                BlockHeader block = resolver.Resolve(offset, "##DG", 64, cancellationToken: token);
                RequireLinks(block, 4);
                byte[] data = resolver.ReadPayload(block, 8, token);
                MdfDataGroup group = new MdfDataGroup
                {
                    Index = groups.Count, BlockAddress = offset,
                    Comment = text.Comment(block.Links[3], 2),
                    ChannelGroups = ReadChannelGroups(block.Links[1], block.Links[2], data[0], offset)
                };
                groups.Add(group);
                offset = block.Links[0];
            }
            if (mdf420) { ValidateRemoteMasters(); }
            token.ThrowIfCancellationRequested();
            return Array.AsReadOnly(groups.ToArray());
        }

        private void ValidateRemoteMasters()
        {
            // Only catalog-owned CG identities are considered. Never follow arbitrary links or fetch samples.
            foreach (KeyValuePair<ulong, MdfChannelGroup> entry in channelGroups)
            {
                token.ThrowIfCancellationRequested();
                MdfChannelGroup group = entry.Value;
                RecordLayout layout = group.Layout;
                if (!layout.HasRemoteMaster) { continue; }
                MdfChannelGroup master;
                if (layout.RemoteMasterAddress == 0 || layout.RemoteMasterAddress == entry.Key
                    || !channelGroups.TryGetValue(layout.RemoteMasterAddress, out master))
                { throw new MdfFormatException("The remote master must be another channel group in this document's catalog.", "MDF013", entry.Key, "##CG"); }
                if (group.SampleCount != master.SampleCount)
                { throw new MdfFormatException("Remote and stored master groups must have equal sample counts.", "MDF013", entry.Key, "##CG"); }
                if (master.Layout.HasRemoteMaster || !master.CanReadSamples || master.Layout.Interleaved != null || !master.Layout.HasStoredMasterChannel)
                { group.UnsupportedReason = "Remote master chains, cycles or targets without a supported sorted stored master are not supported."; }
            }
        }

        private IReadOnlyList<MdfChannelGroup> ReadChannelGroups(ulong offset, ulong dataAddress, byte recordIdLength, ulong dataGroupAddress)
        {
            List<MdfChannelGroup> groups = new List<MdfChannelGroup>();
            HashSet<ulong> chain = NewChain(offset);
            while (offset != 0)
            {
                Own(offset, 2, chain);
                BlockHeader block = resolver.Resolve(offset, "##CG", 104, cancellationToken: token);
                if (block.Links.Count < 6) { throw Shape(block, "CG requires at least six base links."); }
                byte[] data = resolver.ReadPayload(block, 32, token);
                ushort flags = U16(data, 16);
                uint dataBytes = U32(data, 24);
                uint invalidationBytes = U32(data, 28);
                // bus-event (bit 1) and plain bus-event (bit 2) flags only classify the group; they never change the layout.
                ushort layoutFlags = (ushort)(flags & ~6);
                bool remote = mdf420 && layoutFlags == 8 && block.Links.Count == 7;
                bool vlsd = flags == 1 && block.Links.Count == 6 && block.Links[1] == 0; // no channels, no fixed record.
                if (mdf420) { budget.ReserveMetadata(128, offset); } // Catalog lookup capacity before allocation/addition.
                MdfChannelGroup group = new MdfChannelGroup
                {
                    Document = document,
                    Index = groups.Count, Flags = flags, PathSeparator = (char)U16(data, 18),
                    Name = text.Name(block.Links[2], 3),
                    Comment = text.Comment(block.Links[5], 3),
                    Source = Source(block.Links[3], 3),
                    SampleCount = U64(data, 8),
                    // A VLSD CG's two size fields are an informational UInt64 total whose writer conventions differ.
                    RecordByteCount = vlsd ? 0 : (ulong)dataBytes + invalidationBytes,
                    Layout = new RecordLayout
                    {
                        DataAddress = dataAddress, BlockAddress = offset,
                        DataBytes = vlsd ? 0 : dataBytes, InvalidationBytes = vlsd ? 0 : invalidationBytes, RecordId = U64(data, 0),
                        IsMdf420 = mdf420, HasRemoteMaster = remote, RemoteMasterAddress = remote ? block.Links[6] : 0,
                        IsVlsdGroup = vlsd
                    },
                    Channels = ReadChannels(block.Links[1], dataBytes, invalidationBytes)
                };
                if (vlsd) { group.UnsupportedReason = VlsdGroupReason; }
                else if (!remote && (layoutFlags != 0 || block.Links.Count != 6)) { group.UnsupportedReason = "Extended or variable-length channel groups and this remote-master link/flag shape are not supported."; }
                else if (group.RecordByteCount == 0 || (mdf420 && dataBytes == 0)) { group.UnsupportedReason = "A sample record must have a nonzero fixed data size."; }
                if (mdf420)
                {
                    // Compute once per CG, not once per incoming remote edge (which could be quadratic).
                    foreach (MdfChannel channel in group.Channels)
                    {
                        token.ThrowIfCancellationRequested();
                        // a virtual master is not a stored master for remote-master targets.
                        if (channel.IsMaster && channel.CanReadRaw && !channel.Layout.Virtual) { group.Layout.HasStoredMasterChannel = true; break; }
                    }
                    channelGroups.Add(offset, group);
                }
                groups.Add(group);
                offset = block.Links[0];
            }
            IReadOnlyList<MdfChannelGroup> result = Array.AsReadOnly(groups.ToArray());
            ResolveVlsdGroups(groups);
            if (recordIdLength == 1 || recordIdLength == 2 || recordIdLength == 4 || recordIdLength == 8)
            {
                if (mdf420)
                {
                    // MDF 4.20 remote masters are defined here only for sorted LD storage; refuse the whole unsorted DG.
                    foreach (MdfChannelGroup group in groups)
                    {
                        if (group.Layout.HasRemoteMaster)
                        { group.UnsupportedReason = "Remote-master channel groups are not supported in unsorted MDF 4.20 data groups."; }
                    }
                }
                InterleavedLayout.Attach(result, recordIdLength, 0, budget, dataGroupAddress, "##DG", token);
            }
            else if (recordIdLength != 0 || groups.Count != 1)
            {
                foreach (MdfChannelGroup group in groups)
                {
                    group.UnsupportedReason = recordIdLength == 0 ? "Multiple channel groups require explicit record IDs."
                        : "Only record-ID prefix widths 1, 2, 4 and 8 are supported.";
                }
            }
            return result;
        }

        internal const string VlsdGroupReason = "A VLSD channel group holds variable-length signal data of another channel and has no sample records of its own.";
        internal const string VlsdTargetReason = "A VLSD channel's channel-group link must address a VLSD channel group of the same unsorted data group.";
        internal const string VlsdSharedReason = "A VLSD channel group must be referenced by exactly one VLSD channel.";

        // a VLSD channel's CG link must address a VLSD CG of this DG's chain, owned by exactly one channel.
        // O(CG + channels) per DG; only catalog-owned identities are compared, no link is followed.
        private void ResolveVlsdGroups(List<MdfChannelGroup> groups)
        {
            Dictionary<ulong, MdfChannelGroup> targets = null;
            Dictionary<ulong, MdfChannel> owners = null;
            foreach (MdfChannelGroup group in groups)
            {
                foreach (MdfChannel channel in group.Channels)
                {
                    token.ThrowIfCancellationRequested();
                    ChannelLayout layout = channel.Layout;
                    if (layout == null || !layout.SignalDataIsGroup || channel.UnsupportedReason != null) { continue; }
                    ulong address = layout.SignalDataAddress;
                    if (targets == null)
                    {
                        budget.ReserveMetadata(256 + 64UL * (ulong)groups.Count, address); // Both dictionaries' fixed/capacity allowance.
                        targets = new Dictionary<ulong, MdfChannelGroup>(groups.Count);
                        owners = new Dictionary<ulong, MdfChannel>();
                        foreach (MdfChannelGroup candidate in groups) { targets[candidate.Layout.BlockAddress] = candidate; }
                    }
                    MdfChannelGroup target;
                    MdfChannel owner;
                    if (!targets.TryGetValue(address, out target) || !target.Layout.IsVlsdGroup) { RefuseVlsd(channel, VlsdTargetReason); }
                    else if (owners.TryGetValue(address, out owner))
                    {
                        RefuseVlsd(channel, VlsdSharedReason);
                        if (owner != null) { RefuseVlsd(owner, VlsdSharedReason); owners[address] = null; }
                    }
                    else
                    {
                        budget.ReserveMetadata(64, address);
                        owners.Add(address, channel);
                        layout.SignalGroup = target.Layout;
                    }
                }
            }
        }

        private static void RefuseVlsd(MdfChannel channel, string reason)
        {
            channel.UnsupportedReason = reason;
            channel.Layout.TextStorage = 0;
            channel.Layout.SignalGroup = null;
        }

        internal const string MaxLengthTypeReason = "MLSD channels require a text or byte-array data type.";
        internal const string MaxLengthNoSizeReason = "An MLSD channel requires a size channel.";
        internal const string MaxLengthGroupReason = "An MLSD size channel must be a top-level channel of the same channel group."; // Fixed text; also accepts same-CG components.
        internal const string MaxLengthKindReason = "An MLSD size channel must be a supported stored unsigned integer channel.";

        private IReadOnlyList<MdfChannel> ReadChannels(ulong offset, uint dataBytes, uint invalidationBytes)
        {
            List<MdfChannel> channels = new List<MdfChannel>();
            List<StructureFrame> roots = null;
            List<ulong> addresses = new List<ulong>(); // CN addresses (size resolution), one per channel.
            bool maxLength = false;
            HashSet<ulong> chain = NewChain(offset);
            while (offset != 0)
            {
                Own(offset, 3, chain);
                ulong components, next;
                MdfChannel channel = ReadChannel(offset, 3, channels.Count, dataBytes, invalidationBytes, null, out components, out next);
                channels.Add(channel);
                addresses.Add(offset);
                maxLength |= channel.Layout.TextStorage == 3;
                if (components != 0)
                {
                    // Traversal context once, then a fixed frame/chain/list allowance per recognized parent.
                    if (roots == null) { budget.ReserveMetadata(256, offset); roots = new List<StructureFrame>(); }
                    roots.Add(Frame(channel, offset, components, 3, null));
                }
                offset = next;
            }
            if (roots != null)
            {
                ReadComponents(roots, channels, dataBytes, invalidationBytes);
                for (int index = addresses.Count; index < channels.Count; index++)
                {
                    addresses.Add(channels[index].BlockAddress);
                    maxLength |= channels[index].Layout.TextStorage == 3;
                }
            }
            if (maxLength) { ResolveSizeChannels(channels, addresses); }
            return Array.AsReadOnly(channels.ToArray());
        }

        // an MLSD cn_data must equal the address of a supported stored unsigned channel (top-level or structure
        // component, as in ASAM bus logging) of this CG. O(channels) once per CG; no link is followed.
        private void ResolveSizeChannels(List<MdfChannel> channels, List<ulong> addresses)
        {
            budget.ReserveMetadata(256 + 32UL * (ulong)addresses.Count, addresses[0]);
            Dictionary<ulong, int> topLevel = new Dictionary<ulong, int>(addresses.Count);
            for (int index = 0; index < addresses.Count; index++) { topLevel[addresses[index]] = index; } // Chain addresses are unique.
            for (int index = 0; index < addresses.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                ChannelLayout layout = channels[index].Layout;
                if (layout.TextStorage != 3) { continue; }
                int target;
                string reason = null;
                if (!topLevel.TryGetValue(layout.SizeAddress, out target)) { reason = MaxLengthGroupReason; }
                else
                {
                    MdfChannel size = channels[target];
                    ChannelLayout sizeLayout = size.Layout;
                    // Supported, non-master, non-virtual, scalar, numeric unsigned => a stored value (cn_type 0) integer.
                    if (size.UnsupportedReason != null || size.IsMaster || sizeLayout.Virtual || sizeLayout.Synchronization || sizeLayout.TextStorage != 0
                        || size.Array != null || sizeLayout.DataType > 1)
                    { reason = MaxLengthKindReason; }
                    else { layout.SizeChannel = size; }
                }
                if (reason != null)
                {
                    channels[index].UnsupportedReason = reason;
                    layout.TextStorage = 0;
                }
            }
        }

        // Explicit-stack preorder DFS: component indices are appended after every top-level channel.
        private void ReadComponents(List<StructureFrame> roots, List<MdfChannel> channels, uint dataBytes, uint invalidationBytes)
        {
            HashSet<ulong> active = new HashSet<ulong>();
            Stack<StructureFrame> stack = new Stack<StructureFrame>();
            foreach (StructureFrame root in roots)
            {
                stack.Push(root);
                active.Add(root.Address);
                while (stack.Count != 0)
                {
                    token.ThrowIfCancellationRequested();
                    StructureFrame frame = stack.Peek();
                    if (frame.Next == 0)
                    {
                        frame.Parent.Components = frame.Components.Count == 0 ? MdfChannel.NoComponents : Array.AsReadOnly(frame.Components.ToArray());
                        active.Remove(frame.Address);
                        stack.Pop();
                        continue;
                    }
                    ulong address = frame.Next;
                    int depth = frame.Depth + 1;
                    if (active.Contains(address))
                    { throw new MdfFormatException("A channel composition refers to one of its ancestors.", "MDF010", address, "##CN"); }
                    Own(address, depth, frame.Chain);
                    budget.ReserveMetadata(64, address); // Parent/component relationship and catalog index entry.
                    ulong components, next;
                    MdfChannel child = ReadChannel(address, depth, channels.Count, dataBytes, invalidationBytes, frame, out components, out next);
                    child.Parent = frame.Parent;
                    channels.Add(child);
                    frame.Components.Add(child);
                    frame.Next = next;
                    if (components != 0)
                    {
                        stack.Push(Frame(child, address, components, depth, frame));
                        active.Add(address);
                    }
                }
            }
        }

        private StructureFrame Frame(MdfChannel parent, ulong address, ulong components, int depth, StructureFrame owner)
        {
            budget.ReserveMetadata(384, address);
            ChannelLayout layout = parent.Layout;
            return new StructureFrame
            {
                Parent = parent, Address = address, Next = components, Depth = depth,
                Chain = new HashSet<ulong>(), Components = new List<MdfChannel>(),
                Start = layout.ByteOffset, End = (ulong)layout.ByteOffset + parent.BitCount / 8,
                Blocked = (owner != null && owner.Blocked) || parent.BlocksComponents
            };
        }

        private MdfChannel ReadChannel(ulong offset, int depth, int index, uint dataBytes, uint invalidationBytes, StructureFrame owner,
            out ulong components, out ulong next)
        {
            components = 0;
            BlockHeader block = resolver.Resolve(offset, "##CN", 160, cancellationToken: token);
            if (block.Links.Count < 8) { throw Shape(block, "CN requires eight base links."); }
            byte[] data = resolver.ReadPayload(block, 72, token);
            uint flags = U32(data, 12);
            int expectedLinks = 8 + U16(data, 22) + ((flags & 0x1000) != 0 ? 3 : 0);
            RequireLinks(block, expectedLinks);
            uint bits = U32(data, 8);
            ChannelLayout layout = new ChannelLayout
            {
                DataType = data[2], BitOffset = data[3], ByteOffset = U32(data, 4),
                BitCount = bits <= 64 ? (int)bits : 0, Flags = flags, InvalidationBit = U32(data, 16),
                CnType = data[0], CnDataType = data[2]
            };
            // MIME sample (11) and MIME stream (12) are byte arrays under every type-10 rule.
            // Internally they decode as 10 (TextDecoder code 11 is the MDF 3 ASCII page); the literal type stays in data[2].
            if (layout.DataType == 11 || layout.DataType == 12) { layout.DataType = 10; }
            // CANopen date (13) / time (14) are fixed byte arrays of exactly 7/6 bytes,
            // published as their wire bytes (no calendar decoding); every other role or width is an inspectable refusal.
            bool canOpen = data[2] == 13 || data[2] == 14;
            if (canOpen) { layout.DataType = 10; }
            // complex LE (15) / BE (16) decode as two IEEE parts of half the width.
            if (data[2] == 15 || data[2] == 16)
            {
                layout.DataType = (byte)(data[2] - 11);
                layout.Complex = true;
                layout.BitCount = bits == 32 || bits == 64 || bits == 128 ? (int)(bits / 2) : 0;
            }
            bool textual = layout.DataType >= 6 && layout.DataType <= 10;
            // virtual master (3) and virtual data (6) channels have no record field; raw = record index.
            bool virtualRole = data[0] == 3 || data[0] == 6;
            string unsupported = virtualRole ? VirtualSupport(block, bits, layout)
                : data[0] == 4 ? SyncSupport(block, data[1], bits, layout, dataBytes, invalidationBytes, depth)
                : data[0] == 5 && !textual ? MaxLengthTypeReason
                // CANopen VLSD (1) / MLSD (5) values follow the byte-array rules; MdfCalendar decodes exact lengths only.
                : canOpen && (data[0] == 0 ? layout.BitOffset != 0 || bits != (data[2] == 13 ? 56U : 48U) : data[0] != 1 && data[0] != 5)
                    ? CanOpenRefusal(block, layout, invalidationBytes)
                : textual ? TextSupport(block, data[0], bits, layout, dataBytes, invalidationBytes, depth)
                : ScalarSupport(block, data[0], bits, layout, dataBytes, invalidationBytes);
            MdfChannelArray array = null;
            bool structure = false;
            if (block.Links[1] != 0)
            {
                token.ThrowIfCancellationRequested();
                budget.CheckDepth(depth + 1, block.Links[1]);
                BlockHeader target = resolver.Resolve(block.Links[1], cancellationToken: token);
                if (target.Type == "##CA")
                {
                    string arrayReason;
                    int arrayStride;
                    bool inverse;
                    uint invalidationStride;
                    array = ReadArray(target, out arrayReason, out arrayStride, out inverse, out invalidationStride);
                    if (unsupported == null && virtualRole) { unsupported = "Virtual array channels are not supported."; }
                    // fixed-length (cn_type 0) text/byte-array elements use the element layout; VLSD/MLSD stay refused.
                    if (unsupported == null && textual && layout.TextStorage != 1) { unsupported = TextArrayReason; }
                    if (unsupported == null && data[0] == 4) { unsupported = "Synchronization array channels are not supported."; }
                    if (unsupported == null)
                    {
                        unsupported = arrayReason;
                        if (unsupported == null)
                        {
                            // an element is the scalar field (any 1..64-bit integer,
                            // bit offset 0..7; floats byte-aligned 16/32/64 by ScalarSupport) inside its own container.
                            uint container = (bits + (uint)layout.BitOffset + 7) / 8; // BitOffset is 0..7 (ScalarSupport).
                            if (data[0] != 0) { unsupported = "Array master channels are not supported; a stored master must be scalar."; }
                            else if ((uint)arrayStride < container)
                            { unsupported = "Array byte stride must be at least the element's byte width."; }
                            else if (invalidationStride != 0 && (layout.Flags & 3) == 0)
                            { unsupported = "A per-element invalidation stride requires the channel's invalidation-bit flag."; }
                            else
                            {
                                // element s occupies ByteOffset + stride*s.. +container; ScalarSupport proved the first fits.
                                ulong available = (ulong)dataBytes - layout.ByteOffset - container;
                                if (array.ElementCount - 1 > available / (uint)arrayStride)
                                { throw ScalarError(block, "The complete array extends beyond the record's data area."); }
                                if ((layout.Flags & 3) == 2 && invalidationStride != 0
                                    && array.ElementCount - 1 > ((ulong)invalidationBytes * 8 - 1 - layout.InvalidationBit) / invalidationStride)
                                { throw ScalarError(block, "Per-element invalidation bits extend beyond the record's invalidation area."); }
                                layout.ElementByteStride = arrayStride;
                                layout.ElementInvalidationStride = (layout.Flags & 3) == 2 ? invalidationStride : 0;
                                if (inverse && array.Dimensions.Count > 1)
                                {
                                    int rank = array.Dimensions.Count;
                                    ulong[] dimensions = new ulong[rank];
                                    ulong[] weights = new ulong[rank];
                                    ulong weight = 1; // Every prefix product divides ElementCount, which fits UInt64.
                                    for (int axis = 0; axis < rank; axis++)
                                    {
                                        dimensions[axis] = array.Dimensions[axis];
                                        weights[axis] = weight;
                                        weight *= dimensions[axis];
                                    }
                                    layout.ElementDimensions = dimensions;
                                    layout.ElementColumnWeights = weights;
                                }
                            }
                        }
                    }
                }
                else if (target.Type == "##CN" && data[0] == 0 && data[2] == 10 && layout.BitOffset == 0 && bits != 0 && bits % 8 == 0)
                {
                    // Recognized fixed byte-array structure. Children use absolute record byte offsets.
                    if (layout.ByteOffset > dataBytes || bits / 8 > (ulong)dataBytes - layout.ByteOffset)
                    { throw ScalarError(block, "A structure parent extends beyond the data area of its record."); }
                    structure = true;
                    components = block.Links[1];
                    // the aggregate value is the parent's record window through the fixed byte-array path
                    // (TextSupport already validated it); components neither alter it nor inherit from it.
                }
                else if (unsupported == null)
                {
                    unsupported = virtualRole ? "Virtual structure channels are not supported." : "Structure/unknown channel composition is not supported.";
                }
            }
            if (owner != null)
            {
                ulong extent = 0;
                if (structure) { extent = bits / 8; }
                else if (!virtualRole && ((layout.DataType <= 5 && bits != 0 && (bits <= 64 || layout.Complex)) || layout.TextStorage == 1 || layout.TextStorage == 3)) // A virtual channel has no field.
                {
                    ulong container = ((ulong)bits + (uint)layout.BitOffset + 7) / 8;
                    extent = layout.ElementByteStride > 0 ? (array.ElementCount - 1) * (uint)layout.ElementByteStride + container : container;
                }
                if (extent != 0 && (layout.ByteOffset < owner.Start || layout.ByteOffset > owner.End || extent > owner.End - layout.ByteOffset))
                { throw ScalarError(block, "A structure component extends beyond its parent structure."); }
                if (unsupported == null && virtualRole) { unsupported = "Virtual structure components are not supported."; }
                // fixed-length text/byte-array leaves read their absolute record window like top-level fields.
                // so do maximum-length (MLSD) leaves, such as the DataBytes of ASAM bus logging frames.
                // a VLSD (SD) byte-array component is read like a top-level VLSD channel: its absolute record offset holds the SD index.
                if (unsupported == null && textual && !structure && layout.TextStorage != 1 && layout.TextStorage != 2 && layout.TextStorage != 3) { unsupported = TextComponentReason; }
                if (unsupported == null && data[0] == 4) { unsupported = "Synchronization channels inside structures are not supported."; }
                if (unsupported == null && data[0] != 0 && data[0] != 5 && !(data[0] == 1 && layout.TextStorage == 2)) { unsupported = "Stored master channels inside structures are not supported."; }
                if (unsupported == null && owner.Blocked)
                { unsupported = "An ancestor structure declares invalidity, layout flags or channel-specific data whose inheritance is not supported."; }
            }
            MdfChannelConversion conversion = Conversion(block.Links[4], depth + 1);
            bool textInput = PhysicalConversion.TakesText(conversion.Kind); // only decoded Text (data types 6-9).
            if (unsupported == null && textual && conversion.Kind != MdfConversionKind.Identity && !(textInput && layout.DataType <= 9))
            { unsupported = "Text and byte-array channels support only identity conversion."; }
            if (unsupported != null) { layout.TextStorage = 0; } // Never decode an unsupported field as text.
            layout.Virtual = virtualRole && unsupported == null;
            layout.Synchronization = data[0] == 4 && unsupported == null;
            MdfChannel channel = new MdfChannel
            {
                Index = index, Layout = layout, Array = array,
                Name = text.Name(block.Links[2], depth + 1),
                Source = Source(block.Links[3], depth + 1), Conversion = conversion,
                HasConversionBlock = block.Links[4] != 0,
                Unit = block.Links[6] == 0 ? DefaultUnit(data[1], conversion.Unit) : text.Unit(block.Links[6], depth + 1),
                Comment = text.Comment(block.Links[7], depth + 1),
                BitCount = bits, ValueKind = data[2] <= 1 ? MdfValueKind.UnsignedInteger
                    : data[2] <= 3 ? MdfValueKind.SignedInteger
                    : data[2] <= 5 ? MdfValueKind.FloatingPoint
                    : data[2] <= 9 ? MdfValueKind.Text
                    : data[2] <= 14 ? MdfValueKind.ByteArray : data[2] <= 16 ? MdfValueKind.Complex : MdfValueKind.Unsupported, // 11/12 MIME, 13/14 CANopen, 15/16.
                CalendarKind = data[2] == 13 ? MdfCalendarKind.CanOpenDate : data[2] == 14 ? MdfCalendarKind.CanOpenTime : MdfCalendarKind.None,
                IsMaster = data[0] == 2 || data[0] == 3, SynchronizationType = data[1], UnsupportedReason = unsupported,
                BlocksComponents = structure && ((flags & 3) != 0 || (flags & ~0x1dffU) != 0 || block.Links[5] != 0),
                ConversionMismatch = (textInput && !textual)
                    || (conversion.Kind == MdfConversionKind.BitfieldToText && !virtualRole && data[2] > 3) // integer raw only.
                    || (layout.Complex && conversion.Kind != MdfConversionKind.Identity) // identity physical only.
            };
            channel.BlockAddress = offset;
            int attachmentCount = U16(data, 22);
            if (attachmentCount != 0)
            {
                // cn_at_reference links follow the eight base links; resolved only by ReadFileModel.
                budget.ReserveMetadata(32 + 8UL * (ulong)attachmentCount, offset);
                ulong[] attachments = new ulong[attachmentCount];
                for (int i = 0; i < attachments.Length; i++) { attachments[i] = block.Links[8 + i]; }
                channel.AttachmentAddresses = attachments;
            }
            next = block.Links[0];
            return channel;
        }

        // top-level unsigned virtual channel without invalidation or channel data.
        // Byte/bit offset and bit count are not a record field and are neither range-checked nor read.
        private static string VirtualSupport(BlockHeader block, uint bits, ChannelLayout layout)
        {
            if (layout.DataType > 1) { return "Virtual channels require an unsigned integer data type."; }
            if ((layout.Flags & 3) != 0) { return "Invalidation of virtual channels is not supported."; }
            if ((layout.Flags & ~0x1dffU) != 0) { return "Layout-affecting virtual, event or unknown channel flags are not supported."; }
            if (block.Links[5] != 0) { return "Channel-specific data links are not supported."; }
            if (bits > 64) { return "Virtual channel bit counts greater than 64 are not supported."; }
            return null;
        }

        private static string ScalarSupport(BlockHeader block, byte role, uint bits, ChannelLayout layout, uint dataBytes, uint invalidationBytes)
        {
            if ((layout.Flags & 1) == 0 && (layout.Flags & 2) != 0
                && (ulong)layout.InvalidationBit >= (ulong)invalidationBytes * 8)
            {
                throw ScalarError(block, "Invalidation bit lies outside the record's trailing invalidation area.");
            }
            // Unknown/virtual encodings are inspectable; their storage is not guessed as an ordinary numeric field.
            if (role != 0 && role != 2 && role != 4) { return "Only stored value and stored master channel roles are supported."; }
            if (layout.DataType > 5) { return "Only unsigned, signed and IEEE numeric encodings are supported."; }
            if (bits == 0 || layout.BitOffset > 7)
            {
                throw ScalarError(block, "Invalid numeric bit count or bit offset.");
            }
            ulong bytes = ((ulong)bits + (ulong)layout.BitOffset + 7) / 8;
            if (layout.ByteOffset > dataBytes || bytes > (ulong)dataBytes - layout.ByteOffset)
            {
                throw ScalarError(block, "Numeric field extends beyond the data area of its record.");
            }
            if (block.Links[5] != 0 && role != 4) { return "Channel-specific data links are not supported."; } // sync cn_data = AT.
            if ((layout.Flags & ~0x1dffU) != 0) { return "Layout-affecting virtual, event or unknown channel flags are not supported."; }
            if (layout.Complex)
            {
                if (role != 0) { return ComplexRoleReason; }
                return layout.BitOffset != 0 || layout.BitCount == 0 ? ComplexReason : null;
            }
            if (bits > 64) { return "Integer widths greater than 64 bits are not supported."; }
            if (layout.DataType >= 4 && (layout.BitOffset != 0 || (bits != 16 && bits != 32 && bits != 64)))
            {
                return FloatReason; // binary16 added.
            }
            return null;
        }

        internal const string ComplexReason = "Only byte-aligned complex storage of two IEEE 16/32/64-bit parts is supported.";
        internal const string ComplexRoleReason = "Complex values are supported only for stored value channels.";
        internal const string FloatReason = "Only byte-aligned IEEE 16/32/64-bit floating-point storage is supported.";
        internal const string VlsdRootReason = "Only SD, DZ, DL or HL signal data, or MDF 4.20 LD signal data, is supported for VLSD channels.";
        internal const string SyncNumericReason = "Synchronization channels require a numeric data type.";
        internal const string SyncTypeReason = "A synchronization channel requires a time, angle, distance or index synchronization type.";
        internal const string SyncNoAttachmentReason = "A synchronization channel requires an attachment (AT) data link.";
        internal const string SyncAttachmentReason = "A synchronization channel's data link must reference an attachment (AT) block.";

        // a cn_type 4 channel is a stored numeric scalar whose cn_data is an AT block.
        // Only the AT header is resolved (cached, budgeted); its payload and HD chain membership are not read.
        // Cn4Block::Unit of the wrapper: a channel without a unit link takes the unit of its synchronization type
        // (time s, angle rad, distance m), and only otherwise the unit of its conversion.
        internal static string DefaultUnit(byte syncType, string conversionUnit)
        {
            switch (syncType)
            {
                case 1: return "s";
                case 2: return "rad";
                case 3: return "m";
                default: return conversionUnit ?? string.Empty;
            }
        }

        private string SyncSupport(BlockHeader block, byte syncType, uint bits, ChannelLayout layout, uint dataBytes, uint invalidationBytes, int depth)
        {
            string reason = ScalarSupport(block, 4, bits, layout, dataBytes, invalidationBytes); // MDF013 invalidation check first.
            if (layout.DataType > 5) { return SyncNumericReason; }
            if (reason != null) { return reason; }
            if (syncType < 1 || syncType > 4) { return SyncTypeReason; }
            if (block.Links[5] == 0) { return SyncNoAttachmentReason; }
            token.ThrowIfCancellationRequested();
            budget.CheckDepth(depth + 1, block.Links[5]);
            BlockHeader target = resolver.Resolve(block.Links[5], cancellationToken: token);
            return target.Type == "##AT" ? null : SyncAttachmentReason;
        }

        internal const string TextArrayReason = "Text and byte-array arrays are not supported.";
        internal const string TextComponentReason = "Text and byte-array structure components are not supported.";
        internal const string CanOpenReason = "CANopen date and time channels must be fixed-length stored values of exactly 7 (date) or 6 (time) bytes, or VLSD/MLSD byte arrays.";

        // refused CANopen role/width. The invalidation-bit placement is still malformed data (MDF013); signal data,
        // size channels and other links of the refused channel are never resolved.
        private static string CanOpenRefusal(BlockHeader block, ChannelLayout layout, uint invalidationBytes)
        {
            if ((layout.Flags & 1) == 0 && (layout.Flags & 2) != 0
                && (ulong)layout.InvalidationBit >= (ulong)invalidationBytes * 8)
            {
                throw ScalarError(block, "Invalidation bit lies outside the record's trailing invalidation area.");
            }
            return CanOpenReason;
        }

        // top-level fixed (cn_type 0) or VLSD (cn_type 1) MDF 4 text/byte storage. Only the VLSD data
        // root's header is resolved here; SD payload, DL lists and DZ headers are read lazily by the sample engine.
        private string TextSupport(BlockHeader block, byte type, uint bits, ChannelLayout layout, uint dataBytes, uint invalidationBytes, int depth)
        {
            if ((layout.Flags & 1) == 0 && (layout.Flags & 2) != 0
                && (ulong)layout.InvalidationBit >= (ulong)invalidationBytes * 8)
            {
                throw ScalarError(block, "Invalidation bit lies outside the record's trailing invalidation area.");
            }
            if (type != 0 && type != 1 && type != 5)
            { return "Only fixed-length and VLSD text/byte channels and maximum-length (MLSD) channels are supported."; }
            if ((layout.Flags & ~0x1dffU) != 0) { return "Layout-affecting virtual, event or unknown channel flags are not supported."; }
            if (type == 0 || type == 5)
            {
                if (bits == 0 || layout.BitOffset != 0 || bits % 8 != 0) { return "Text and byte-array fields must be nonempty, byte-aligned whole bytes."; }
                if ((layout.DataType == 8 || layout.DataType == 9) && bits % 16 != 0) { return "UTF-16 fields must contain whole code units."; }
                if (layout.ByteOffset > dataBytes || bits / 8 > (ulong)dataBytes - layout.ByteOffset)
                {
                    throw ScalarError(block, "Text or byte-array field extends beyond the data area of its record.");
                }
                if (type == 0)
                {
                    if (block.Links[5] != 0) { return "Channel-specific data links are not supported."; }
                    layout.TextStorage = 1;
                }
                else
                {
                    // cn_data addresses the size CN; resolved by address within this CG only (never dereferenced).
                    if (block.Links[5] == 0) { return MaxLengthNoSizeReason; }
                    layout.TextStorage = 3;
                    layout.SizeAddress = block.Links[5];
                }
                layout.FieldBytes = bits / 8;
                return null;
            }
            if (layout.BitOffset != 0 || bits != 64) { return "VLSD offsets must be byte-aligned 64-bit values."; }
            if (layout.ByteOffset > dataBytes || 8 > (ulong)dataBytes - layout.ByteOffset)
            {
                throw ScalarError(block, "VLSD offset field extends beyond the data area of its record.");
            }
            ulong root = block.Links[5];
            if (root != 0)
            {
                token.ThrowIfCancellationRequested();
                budget.CheckDepth(depth + 1, root);
                string kind = resolver.Resolve(root, cancellationToken: token).Type;
                if (kind == "##CG") { layout.SignalDataIsGroup = true; } // resolved against this DG's CG chain.
                else if (kind == "##LD" && mdf420) { } // LD signal-data list, walked lazily by the SD map builder.
                else if (kind != "##SD" && kind != "##DZ" && kind != "##DL" && kind != "##HL") { return VlsdRootReason; }
            }
            layout.TextStorage = 2;
            layout.SignalDataAddress = root;
            return null;
        }

        private MdfChannelArray ReadArray(BlockHeader block, out string unsupported, out int stride, out bool inverse, out uint invalidationStride)
        {
            unsupported = null;
            stride = 0;
            inverse = false;
            invalidationStride = 0;
            ulong offset = block.Offset;
            if (block.Links.Count < 1 || block.DataLength < 16)
            { throw Shape(block, "CA requires a composition link and a sixteen-byte prefix."); }
            byte[] prefix = resolver.ReadPayload(block, 16, token);
            int dimensions = U16(prefix, 2);
            if (dimensions == 0) { throw ScalarError(block, "A fixed-array shape requires at least one dimension."); }
            int payloadBytes = 16 + dimensions * 8; // UInt16 dimensions: proven Int32-safe.
            if (block.DataLength < (ulong)payloadBytes)
            { throw Shape(block, "The CA dimensions exceed the declared payload."); }
            uint arrayFlags = U32(prefix, 4);
            // roles and metadata flags that never change element values are accepted;
            // their extra links and fixed axis values are counted, never dereferenced or read.
            string refusal = ArrayRefusal(prefix[0], prefix[1], dimensions, arrayFlags);
            bool fixedAxis = (arrayFlags & 0x20) != 0;
            if (refusal == null)
            {
                bool axis = (arrayFlags & 0x10) != 0;
                int links = 1 + ((arrayFlags & 0x02) != 0 ? 3 * dimensions : 0) + ((arrayFlags & 0x04) != 0 ? 3 : 0)
                    + ((arrayFlags & 0x08) != 0 ? 3 : 0) + (axis ? dimensions : 0) + (axis && !fixedAxis ? 3 * dimensions : 0);
                if (block.Links.Count != links)
                { throw Shape(block, "A CN-template CA requires exactly the links its flags and dimensions declare."); }
                if (!fixedAxis && block.DataLength != (ulong)payloadBytes)
                { throw Shape(block, "A CN-template CA without fixed axes requires exactly its dimension section."); }
            }
            // Descriptor/wrapper/shape allowance precedes allocation; no allocation by product.
            budget.ReserveMetadata(256UL + (ulong)dimensions * 16, offset);
            byte[] data = resolver.ReadPayload(block, payloadBytes, token);
            ulong[] shape = new ulong[dimensions];
            ulong elements = 1;
            ulong axisValues = 0;
            for (int index = 0; index < dimensions; index++)
            {
                token.ThrowIfCancellationRequested();
                ulong size = U64(data, 16 + index * 8);
                if (size == 0 || elements > ulong.MaxValue / size)
                { throw ScalarError(block, "Array dimensions must be positive and their product must fit UInt64."); }
                shape[index] = size;
                elements *= size;
                axisValues = size > ulong.MaxValue - axisValues ? ulong.MaxValue : axisValues + size;
            }
            if (refusal == null && fixedAxis
                && (axisValues > (ulong.MaxValue - (ulong)payloadBytes) / 8 || block.DataLength != (ulong)payloadBytes + axisValues * 8))
            { throw Shape(block, "A fixed-axis CA requires exactly one REAL value per element of every dimension."); }
            stride = unchecked((int)U32(data, 8));
            inverse = (arrayFlags & 0x40) != 0;
            invalidationStride = U32(data, 12);
            if (refusal != null) { unsupported = refusal; }
            else if (block.Links[0] != 0) { unsupported = "Chained/nested array composition is not supported."; }
            else if (stride <= 0) { unsupported = "Array byte stride must be positive."; }
            return new MdfChannelArray(Array.AsReadOnly(shape), elements, prefix[0], prefix[1], arrayFlags);
        }

        private string ArrayRefusal(byte type, byte storage, int dimensions, uint flags)
        {
            if (storage != 0) { return "Only CN-template array storage is supported."; }
            if ((flags & 0x01) != 0) { return "Dynamic-size arrays are not supported."; }
            if ((flags >> 9) != 0 || ((flags & 0x100) != 0 && !mdf420))
            { return "Unknown array flags, or the standard-axis flag before MDF 4.20, are not supported."; }
            if (type > 2) { return "Only array, scaling-axis and look-up CN-template arrays are supported."; }
            if (type == 1 && dimensions != 1) { return "A scaling-axis array must have exactly one dimension."; }
            if ((flags & 0x20) != 0 && (flags & 0x10) == 0) { return "A fixed-axis array requires the axis flag."; }
            return null;
        }

        private MdfSourceInformation Source(ulong offset, int depth)
        {
            if (offset == 0) { return null; }
            token.ThrowIfCancellationRequested();
            budget.CheckDepth(depth, offset);
            Cached<MdfSourceInformation> cached;
            if (sources.TryGetValue(offset, out cached))
            {
                budget.CheckDepth(depth + cached.Height, offset);
                return cached.Value;
            }
            BlockHeader block = resolver.Resolve(offset, "##SI", 56, cancellationToken: token);
            RequireLinks(block, 3);
            budget.ReserveMetadata(384, offset);
            byte[] data = resolver.ReadPayload(block, 8, token);
            MdfSourceInformation source = new MdfSourceInformation
            {
                Name = text.Name(block.Links[0], depth + 1), Path = text.Name(block.Links[1], depth + 1),
                Comment = text.Comment(block.Links[2], depth + 1), SourceType = data[0], BusType = data[1], Flags = data[2]
            };
            sources.Add(offset, new Cached<MdfSourceInformation>(source, ChildHeight(block)));
            return source;
        }

        private MdfChannelConversion Conversion(ulong offset, int depth)
        {
            if (offset == 0) { return MdfChannelConversion.Identity; }
            token.ThrowIfCancellationRequested();
            budget.CheckDepth(depth, offset);
            Cached<MdfChannelConversion> cached;
            if (conversions.TryGetValue(offset, out cached))
            {
                budget.CheckDepth(depth + cached.Height, offset);
                return cached.Value;
            }
            BlockHeader block = resolver.Resolve(offset, "##CC", 80, cancellationToken: token);
            if (block.Links.Count < 4) { throw Shape(block, "CC requires four base links."); }
            byte[] data = resolver.ReadPayload(block, 24, token);
            ushort references = U16(data, 4);
            ushort values = U16(data, 6);
            if (block.Links.Count != 4 + references || (ulong)values > (block.DataLength - 24) / 8)
            {
                throw ScalarError(block, "CC reference/value counts exceed the declared sections.");
            }
            byte kind = data[0];
            if ((kind == 0 && (references != 0 || values != 0)) || (kind == 1 && (references != 0 || values != 2)))
            {
                throw ScalarError(block, "Identity/linear conversion has incompatible parameter counts.");
            }
            MdfConversionKind numeric = NumericKind(kind);
            // rational 6 values; tables n >= 1 (key, value) pairs; ranges n >= 0 triples plus a default; no references.
            if (numeric != MdfConversionKind.Unsupported && (references != 0
                || (numeric == MdfConversionKind.Rational && values != 6)
                || ((numeric == MdfConversionKind.Table || numeric == MdfConversionKind.TableInterpolation) && (values == 0 || values % 2 != 0))
                || (numeric == MdfConversionKind.RangeTable && values % 3 != 1)))
            {
                throw ScalarError(block, "Numeric value-to-value conversion has incompatible parameter counts.");
            }
            MdfConversionKind textual = TextKind(kind);
            // value to text n keys + n + 1 refs; range to text 2n bounds + n + 1 refs; text to value n refs + n + 1
            // values; text to text 2n + 1 refs and no values.
            if ((textual == MdfConversionKind.ValueToText && references != values + 1)
                || (textual == MdfConversionKind.RangeToText && (values % 2 != 0 || references != values / 2 + 1))
                || (textual == MdfConversionKind.TextToValue && values != references + 1)
                || (textual == MdfConversionKind.TextToText && (values != 0 || references % 2 != 1)))
            {
                throw ScalarError(block, "Text conversion has incompatible reference/value counts.");
            }
            // algebraic exactly one reference (the formula TX) and no values.
            if (kind == 3 && (references != 1 || values != 0))
            {
                throw ScalarError(block, "Algebraic conversion has incompatible reference/value counts.");
            }
            // bitfield n >= 1 UINT64 masks and n CC references.
            bool bitfield = kind == 11;
            if (bitfield && (values == 0 || references != values))
            {
                throw ScalarError(block, "Bitfield conversion has incompatible reference/mask counts.");
            }
            bool evaluated = numeric != MdfConversionKind.Unsupported || textual != MdfConversionKind.Unsupported || bitfield;
            budget.ReserveMetadata(384 + (evaluated ? 16UL * values : 0)
                + (textual != MdfConversionKind.Unsupported || bitfield ? 96UL * references : 0), offset);
            MdfChannelConversion result = new MdfChannelConversion
            {
                Kind = kind == 0 ? MdfConversionKind.Identity : kind == 1 ? MdfConversionKind.Linear : MdfConversionKind.Unsupported,
                Factor = kind == 0 ? (double?)1 : null, Offset = kind == 0 ? (double?)0 : null,
                Name = text.Name(block.Links[0], depth + 1), Unit = text.Unit(block.Links[1], depth + 1),
                Comment = text.Comment(block.Links[2], depth + 1)
            };
            if (kind == 1)
            {
                byte[] parameters = resolver.ReadPayload(block, 40, token);
                result.Offset = EndianBitConverter.ReadDouble(parameters, 24, ByteOrder.LittleEndian);
                result.Factor = EndianBitConverter.ReadDouble(parameters, 32, ByteOrder.LittleEndian);
                result.Values = new[] { result.Offset.Value, result.Factor.Value };
            }
            else if (numeric != MdfConversionKind.Unsupported)
            {
                byte[] parameters = resolver.ReadPayload(block, 24 + 8 * values, token);
                double[] wire = new double[values];
                for (int i = 0; i < wire.Length; i++) { wire[i] = EndianBitConverter.ReadDouble(parameters, 24 + 8 * i, ByteOrder.LittleEndian); }
                if (PhysicalConversion.Valid(numeric, wire))
                {
                    result.Kind = numeric;
                    result.Values = wire;
                }
            }
            int height = ChildHeight(block);
            if (textual != MdfConversionKind.Unsupported)
            {
                byte[] parameters = resolver.ReadPayload(block, 24 + 8 * values, token);
                double[] wire = new double[values];
                for (int i = 0; i < wire.Length; i++) { wire[i] = EndianBitConverter.ReadDouble(parameters, 24 + 8 * i, ByteOrder.LittleEndian); }
                string[] texts = new string[references];
                bool nested = false;
                for (int i = 0; i < references; i++)
                {
                    bool nestedConversion;
                    texts[i] = text.Reference(block.Links[4 + i], depth + 1, out nestedConversion);
                    nested |= nestedConversion; // nested CC references are not evaluated (the whole conversion stays Unsupported).
                    if (block.Links[4 + i] != 0) { height = 1; }
                }
                Dictionary<string, int> keys = TextKeys(textual, texts);
                if (!nested && (keys != null || !PhysicalConversion.TakesText(textual)) && PhysicalConversion.Valid(textual, wire))
                {
                    result.Kind = textual;
                    result.Values = wire;
                    result.References = texts;
                    result.KeyIndex = keys;
                }
            }
            if (kind == 3)
            {
                bool nestedConversion;
                string formula = text.Reference(block.Links[4], depth + 1, out nestedConversion); // NIL or CC: no formula text.
                if (block.Links[4] != 0) { height = Math.Max(height, 1); }
                if (formula != null)
                {
                    // Compiled nodes are bounded by the formula length (at most one node per character).
                    budget.ReserveMetadata(64UL + 40UL * (ulong)Math.Min(formula.Length, AlgebraicFormula.MaximumNodes), offset);
                    result.Formula = formula;
                    result.CompiledFormula = AlgebraicFormula.Compile(formula);
                    if (result.CompiledFormula != null) { result.Kind = MdfConversionKind.Algebraic; }
                }
            }
            if (bitfield)
            {
                if (!resolvingConversions.Add(offset)) { throw new MdfFormatException("A bitfield conversion references itself.", "MDF010", offset, block.Type); }
                byte[] parameters = resolver.ReadPayload(block, 24 + 8 * values, token);
                ulong[] masks = new ulong[values];
                MdfChannelConversion[] parts = new MdfChannelConversion[values];
                bool supported = true;
                for (int i = 0; i < values; i++)
                {
                    masks[i] = EndianBitConverter.ReadUInt64(parameters, 24 + 8 * i, ByteOrder.LittleEndian);
                    ulong link = block.Links[4 + i];
                    bool nestedConversion;
                    text.Reference(link, depth + 1, out nestedConversion);
                    if (!nestedConversion) { supported = false; continue; } // TX/NIL parts are not evaluated (Unsupported).
                    parts[i] = Conversion(link, depth + 1);
                    height = Math.Max(height, 1 + conversions[link].Height);
                    supported &= parts[i].Kind == MdfConversionKind.ValueToText || parts[i].Kind == MdfConversionKind.RangeToText;
                }
                resolvingConversions.Remove(offset);
                if (supported)
                {
                    result.Kind = MdfConversionKind.BitfieldToText;
                    result.SetBitfield(masks, parts);
                }
            }
            conversions.Add(offset, new Cached<MdfChannelConversion>(result, height));
            return result;
        }

        private static MdfConversionKind TextKind(byte kind)
        {
            switch (kind)
            {
                case 7: return MdfConversionKind.ValueToText;
                case 8: return MdfConversionKind.RangeToText;
                case 9: return MdfConversionKind.TextToValue;
                case 10: return MdfConversionKind.TextToText;
                default: return MdfConversionKind.Unsupported;
            }
        }

        // First wire index of each key text (ordinal); null when a key reference is NIL or a CC (no key text).
        private static Dictionary<string, int> TextKeys(MdfConversionKind kind, string[] texts)
        {
            if (!PhysicalConversion.TakesText(kind)) { return null; }
            int stride = kind == MdfConversionKind.TextToValue ? 1 : 2;
            int count = kind == MdfConversionKind.TextToValue ? texts.Length : texts.Length / 2;
            Dictionary<string, int> keys = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                string key = texts[stride * i];
                if (key == null) { return null; }
                if (!keys.ContainsKey(key)) { keys.Add(key, stride * i); }
            }
            return keys;
        }

        private static MdfConversionKind NumericKind(byte kind)
        {
            switch (kind)
            {
                case 2: return MdfConversionKind.Rational;
                case 4: return MdfConversionKind.TableInterpolation;
                case 5: return MdfConversionKind.Table;
                case 6: return MdfConversionKind.RangeTable;
                default: return MdfConversionKind.Unsupported;
            }
        }

        private void Own(ulong offset, int depth, HashSet<ulong> chain)
        {
            token.ThrowIfCancellationRequested();
            budget.CheckDepth(depth, offset);
            // Includes models/layouts, ownership/chain entries, list capacity and the final read-only arrays.
            budget.ReserveMetadata(896, offset);
            if (!chain.Add(offset)) { throw new MdfFormatException("A sibling block chain contains a cycle.", "MDF010", offset); }
            if (!structural.Add(offset)) { throw new MdfFormatException("A structural block has multiple or incompatible owners.", "MDF014", offset); }
        }

        private HashSet<ulong> NewChain(ulong offset)
        {
            budget.ReserveMetadata(192, offset);
            return new HashSet<ulong>();
        }

        private static int ChildHeight(BlockHeader block)
        {
            return block.Links[0] != 0 || block.Links[1] != 0 || block.Links[2] != 0 ? 1 : 0;
        }

        private static void RequireLinks(BlockHeader block, int count)
        {
            if (block.Links.Count != count) { throw Shape(block, "Block has an incompatible link count."); }
        }
        private static MdfFormatException Shape(BlockHeader block, string message) { return new MdfFormatException(message, "MDF006", block.Offset, block.Type); }
        private static MdfFormatException ScalarError(BlockHeader block, string message) { return new MdfFormatException(message, "MDF013", block.Offset, block.Type); }
        private static ushort U16(byte[] data, int offset) { return EndianBitConverter.ReadUInt16(data, offset, ByteOrder.LittleEndian); }
        private static uint U32(byte[] data, int offset) { return EndianBitConverter.ReadUInt32(data, offset, ByteOrder.LittleEndian); }
        private static ulong U64(byte[] data, int offset) { return EndianBitConverter.ReadUInt64(data, offset, ByteOrder.LittleEndian); }

        private sealed class StructureFrame
        {
            internal MdfChannel Parent;
            internal ulong Address;
            internal ulong Next;
            internal int Depth;
            internal HashSet<ulong> Chain;
            internal List<MdfChannel> Components;
            internal ulong Start;
            internal ulong End;
            internal bool Blocked;
        }

        private sealed class Cached<T>
        {
            internal Cached(T value, int height) { Value = value; Height = height; }
            internal T Value { get; }
            internal int Height { get; }
        }
    }
}
