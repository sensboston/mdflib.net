// Copyright 2021 Ingemar Hedvall
// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// FH/AT/EV/CH wire layouts adapted from the pinned MIT mdflib (fh4block, at4block, ev4block, ch4block).
// Traversal, reference resolution, bounds and model policy are new. No payload is read and no external file is opened.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using MdfLib.Internal;
using MdfLib.IO;
using MdfLib.Metadata;

namespace MdfLib.Formats.Mdf4
{
    internal sealed class FileModelReader
    {
        internal const int AttachmentFixedBytes = 40;
        private static readonly string[] HistoryFields = { "TX", "tool_id", "tool_vendor", "tool_version", "user_name" };
        private static readonly string[] HeaderProperties = { "author", "department", "project", "subject", "Measurement.UUID", "Recorder.UUID", "Recorder.FileIndex" };
        private static readonly IReadOnlyList<MdfChannel> NoChannels = new ReadOnlyCollection<MdfChannel>(new MdfChannel[0]);
        private static readonly IReadOnlyList<MdfChannelGroup> NoGroups = new ReadOnlyCollection<MdfChannelGroup>(new MdfChannelGroup[0]);
        private static readonly IReadOnlyList<MdfChannelHierarchy> NoNodes = new ReadOnlyCollection<MdfChannelHierarchy>(new MdfChannelHierarchy[0]);

        private readonly BlockResolver resolver;
        private readonly ReadBudget budget;
        private readonly object document;
        private readonly IReadOnlyList<MdfDataGroup> catalog;
        private readonly CancellationToken token;
        private readonly MetadataTextReader text;
        private readonly bool mdf420;
        private Dictionary<ulong, ChannelEntry> channels;
        private Dictionary<ulong, MdfChannelGroup> groups;
        private Dictionary<ulong, MdfAttachment> attachments;

        internal FileModelReader(BlockResolver resolver, ReaderLimits limits, ReadBudget budget, object document,
            IReadOnlyList<MdfDataGroup> catalog, bool mdf420, CancellationToken token)
        {
            this.resolver = resolver;
            this.budget = budget;
            this.document = document;
            this.catalog = catalog;
            this.mdf420 = mdf420;
            this.token = token;
            text = new MetadataTextReader(resolver, limits, budget, token);
        }

        internal MdfFileModel Read()
        {
            BlockHeader hd = resolver.Resolve(64, "##HD", 104, cancellationToken: token);
            IndexCatalog();
            MdfFileModel model = new MdfFileModel { Comment = text.Comment(hd.Links[5], 1) };
            string[] properties = text.HeaderProperties(hd.Links[5], 1, HeaderProperties);
            model.Description = properties[0];
            model.Author = properties[1];
            model.Organization = properties[2];
            model.Project = properties[3];
            model.Subject = properties[4];
            model.MeasurementId = properties[5];
            model.RecorderId = properties[6];
            long recorderIndex;
            model.RecorderIndex = System.Int64.TryParse(properties[7], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out recorderIndex) ? recorderIndex : 0L;
            model.FileHistory = ReadHistory(hd.Links[1]);
            model.Attachments = ReadAttachments(hd.Links[3]);
            model.SetChannelAttachments(ChannelAttachments());
            model.Events = ReadEvents(hd.Links[4]);
            model.ChannelHierarchy = ReadHierarchy(hd.Links[2]);
            token.ThrowIfCancellationRequested();
            return model;
        }

        // Only catalog-owned identities are indexed; hierarchy and event links are never followed into DG/CG/CN blocks.
        private void IndexCatalog()
        {
            channels = new Dictionary<ulong, ChannelEntry>();
            groups = new Dictionary<ulong, MdfChannelGroup>();
            foreach (MdfDataGroup dataGroup in catalog)
            {
                foreach (MdfChannelGroup group in dataGroup.ChannelGroups)
                {
                    token.ThrowIfCancellationRequested();
                    budget.ReserveMetadata(64, group.Layout.BlockAddress);
                    groups[group.Layout.BlockAddress] = group;
                    foreach (MdfChannel channel in group.Channels)
                    {
                        budget.ReserveMetadata(96, channel.BlockAddress);
                        channels[channel.BlockAddress] = new ChannelEntry
                        { Channel = channel, Group = group.Layout.BlockAddress, DataGroup = dataGroup.BlockAddress };
                    }
                }
            }
        }

        private IReadOnlyList<MdfFileHistory> ReadHistory(ulong offset)
        {
            List<MdfFileHistory> result = new List<MdfFileHistory>();
            HashSet<ulong> chain = new HashSet<ulong>();
            while (offset != 0)
            {
                Visit(offset, 2, chain);
                BlockHeader block = resolver.Resolve(offset, "##FH", 56, cancellationToken: token);
                RequireLinks(block, 2);
                byte[] data = resolver.ReadPayload(block, 16, token);
                string[] fields = text.RootFields(block.Links[1], 3, "FHcomment", HistoryFields);
                result.Add(new MdfFileHistory
                {
                    Index = result.Count,
                    RawTimeNanoseconds = EndianBitConverter.ReadUInt64(data, 0, ByteOrder.LittleEndian),
                    UtcOffsetMinutes = EndianBitConverter.ReadInt16(data, 8, ByteOrder.LittleEndian),
                    DaylightSavingOffsetMinutes = EndianBitConverter.ReadInt16(data, 10, ByteOrder.LittleEndian),
                    TimeFlags = data[12],
                    Comment = text.Comment(block.Links[1], 3),
                    Text = fields[0], ToolIdentifier = fields[1], ToolVendor = fields[2], ToolVersion = fields[3], UserName = fields[4]
                });
                offset = block.Links[0];
            }
            return Array.AsReadOnly(result.ToArray());
        }

        private IReadOnlyList<MdfAttachment> ReadAttachments(ulong offset)
        {
            attachments = new Dictionary<ulong, MdfAttachment>();
            List<MdfAttachment> result = new List<MdfAttachment>();
            HashSet<ulong> chain = new HashSet<ulong>();
            while (offset != 0)
            {
                Visit(offset, 2, chain);
                BlockHeader block = resolver.Resolve(offset, "##AT", 24 + 4 * 8 + AttachmentFixedBytes, cancellationToken: token);
                RequireLinks(block, 4);
                byte[] data = resolver.ReadPayload(block, AttachmentFixedBytes, token);
                ushort flags = EndianBitConverter.ReadUInt16(data, 0, ByteOrder.LittleEndian);
                ulong original = EndianBitConverter.ReadUInt64(data, 24, ByteOrder.LittleEndian);
                ulong embedded = EndianBitConverter.ReadUInt64(data, 32, ByteOrder.LittleEndian);
                bool isEmbedded = (flags & 1) != 0;
                if (!isEmbedded && embedded != 0)
                { throw new MdfFormatException("An external attachment must not declare embedded data.", "MDF013", offset, block.Type); }
                if (embedded > block.DataLength - AttachmentFixedBytes)
                { throw new MdfFormatException("The embedded attachment data exceeds its AT block.", "MDF006", offset, block.Type); }
                if (isEmbedded && (flags & 2) == 0 && original != embedded)
                { throw new MdfFormatException("An uncompressed embedded attachment must have equal original and embedded sizes.", "MDF013", offset, block.Type); }
                byte[] checksum = new byte[Md5.HashBytes];
                Array.Copy(data, 8, checksum, 0, checksum.Length);
                MdfAttachment attachment = new MdfAttachment
                {
                    Document = document, BlockAddress = offset, DataOffset = block.DataOffset + AttachmentFixedBytes,
                    Index = result.Count, Flags = flags, CreatorIndex = EndianBitConverter.ReadUInt16(data, 2, ByteOrder.LittleEndian),
                    OriginalSize = original, EmbeddedSize = embedded,
                    FileName = text.Name(block.Links[1], 3), MimeType = text.Name(block.Links[2], 3), Comment = text.Comment(block.Links[3], 3)
                };
                attachment.SetChecksum(checksum);
                attachments.Add(offset, attachment);
                result.Add(attachment);
                offset = block.Links[0];
            }
            return Array.AsReadOnly(result.ToArray());
        }

        private Dictionary<MdfChannel, IReadOnlyList<MdfAttachment>> ChannelAttachments()
        {
            Dictionary<MdfChannel, IReadOnlyList<MdfAttachment>> result = null;
            foreach (ChannelEntry entry in channels.Values)
            {
                ulong[] addresses = entry.Channel.AttachmentAddresses;
                if (addresses == null) { continue; }
                if (result == null) { result = new Dictionary<MdfChannel, IReadOnlyList<MdfAttachment>>(); }
                budget.ReserveMetadata(64 + 8UL * (ulong)addresses.Length, entry.Channel.BlockAddress);
                result.Add(entry.Channel, Attachments(addresses, 0, addresses.Length, entry.Channel.BlockAddress, "##CN"));
            }
            return result;
        }

        private IReadOnlyList<MdfAttachment> Attachments(IList<ulong> links, int start, int count, ulong owner, string type)
        {
            if (count == 0) { return MdfFileModel.NoAttachments; }
            List<MdfAttachment> result = new List<MdfAttachment>(count);
            for (int i = 0; i < count; i++)
            {
                // A zero link is an unused reference slot (a writer may reserve the count before the attachment
                // exists); it refers to nothing and is skipped.
                if (links[start + i] == 0) { continue; }
                MdfAttachment attachment;
                if (!attachments.TryGetValue(links[start + i], out attachment))
                { throw new MdfFormatException("An attachment reference must address an AT block of the header's attachment chain.", "MDF013", owner, type); }
                result.Add(attachment);
            }
            return result.Count == 0 ? MdfFileModel.NoAttachments : Array.AsReadOnly(result.ToArray());
        }

        private IReadOnlyList<MdfEvent> ReadEvents(ulong offset)
        {
            List<MdfEvent> result = new List<MdfEvent>();
            List<ulong[]> pending = new List<ulong[]>();
            Dictionary<ulong, MdfEvent> byAddress = new Dictionary<ulong, MdfEvent>();
            HashSet<ulong> chain = new HashSet<ulong>();
            while (offset != 0)
            {
                Visit(offset, 2, chain);
                BlockHeader block = resolver.Resolve(offset, "##EV", 24 + 5 * 8 + 32, cancellationToken: token);
                if (block.Links.Count < 5) { throw Shape(block, "EV requires five base links."); }
                byte[] data = resolver.ReadPayload(block, 32, token);
                byte flags = data[4];
                uint scopes = EndianBitConverter.ReadUInt32(data, 8, ByteOrder.LittleEndian);
                ushort references = EndianBitConverter.ReadUInt16(data, 12, ByteOrder.LittleEndian);
                bool grouped = mdf420 && (flags & 2) != 0;
                if ((ulong)block.Links.Count != 5UL + scopes + references + (grouped ? 1UL : 0UL))
                { throw Shape(block, "EV link count does not match its scope, attachment and group-name counts."); }
                List<MdfChannelGroup> scopeGroups = null;
                List<MdfChannel> scopeChannels = null;
                for (int i = 0; i < (int)scopes; i++)
                {
                    token.ThrowIfCancellationRequested();
                    ulong target = block.Links[5 + i];
                    MdfChannelGroup group;
                    ChannelEntry channel;
                    if (groups.TryGetValue(target, out group)) { (scopeGroups ?? (scopeGroups = new List<MdfChannelGroup>())).Add(group); }
                    else if (channels.TryGetValue(target, out channel)) { (scopeChannels ?? (scopeChannels = new List<MdfChannel>())).Add(channel.Channel); }
                    else { throw new MdfFormatException("An event scope must address a channel group or channel of this document's catalog.", "MDF013", offset, block.Type); }
                }
                MdfEvent item = new MdfEvent
                {
                    Index = result.Count,
                    Name = text.Name(block.Links[3], 3), Comment = text.Comment(block.Links[4], 3),
                    GroupName = grouped ? text.Name(block.Links[block.Links.Count - 1], 3) : string.Empty,
                    Type = (MdfEventType)data[0], SyncType = (MdfEventSyncType)data[1], RangeType = (MdfEventRangeType)data[2],
                    Cause = (MdfEventCause)data[3], Flags = flags,
                    CreatorIndex = EndianBitConverter.ReadUInt16(data, 14, ByteOrder.LittleEndian),
                    SyncBaseValue = EndianBitConverter.ReadInt64(data, 16, ByteOrder.LittleEndian),
                    SyncFactor = EndianBitConverter.ReadDouble(data, 24, ByteOrder.LittleEndian),
                    ScopeChannelGroups = scopeGroups == null ? NoGroups : Array.AsReadOnly(scopeGroups.ToArray()),
                    ScopeChannels = scopeChannels == null ? NoChannels : Array.AsReadOnly(scopeChannels.ToArray()),
                    Attachments = Attachments(block.Links, 5 + (int)scopes, references, offset, block.Type)
                };
                budget.ReserveMetadata(64, offset);
                pending.Add(new[] { offset, block.Links[1], block.Links[2] });
                byAddress.Add(offset, item);
                result.Add(item);
                offset = block.Links[0];
            }
            for (int i = 0; i < result.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                result[i].Parent = Event(byAddress, pending[i][1], pending[i][0], "parent");
                result[i].Range = Event(byAddress, pending[i][2], pending[i][0], "range");
            }
            return Array.AsReadOnly(result.ToArray());
        }

        private static MdfEvent Event(Dictionary<ulong, MdfEvent> events, ulong target, ulong owner, string role)
        {
            if (target == 0) { return null; }
            MdfEvent result;
            if (target == owner || !events.TryGetValue(target, out result))
            { throw new MdfFormatException("An event " + role + " must address another EV block of the header's event chain.", "MDF013", owner, "##EV"); }
            return result;
        }

        // Explicit-stack traversal; every CH node has exactly one owner (no shared subtrees or cycles).
        private IReadOnlyList<MdfChannelHierarchy> ReadHierarchy(ulong first)
        {
            if (first == 0) { return NoNodes; }
            HashSet<ulong> owned = new HashSet<ulong>();
            List<MdfChannelHierarchy> roots = new List<MdfChannelHierarchy>();
            Stack<Frame> stack = new Stack<Frame>();
            stack.Push(new Frame { Next = first, Depth = 2, Siblings = roots });
            while (stack.Count != 0)
            {
                token.ThrowIfCancellationRequested();
                Frame frame = stack.Peek();
                if (frame.Next == 0)
                {
                    stack.Pop();
                    if (frame.Node != null) { frame.Node.Children = frame.Siblings.Count == 0 ? NoNodes : Array.AsReadOnly(frame.Siblings.ToArray()); }
                    continue;
                }
                ulong offset = frame.Next;
                budget.CheckDepth(frame.Depth, offset);
                budget.ReserveMetadata(256, offset);
                if (!owned.Add(offset)) { throw new MdfFormatException("A channel-hierarchy block has multiple owners or forms a cycle.", "MDF010", offset, "##CH"); }
                BlockHeader block = resolver.Resolve(offset, "##CH", 64, cancellationToken: token);
                if (block.Links.Count < 4) { throw Shape(block, "CH requires four base links."); }
                byte[] data = resolver.ReadPayload(block, 8, token);
                uint elements = EndianBitConverter.ReadUInt32(data, 0, ByteOrder.LittleEndian);
                if ((ulong)block.Links.Count != 4UL + 3UL * elements) { throw Shape(block, "CH link count does not match its element count."); }
                MdfChannel[] referenced = new MdfChannel[elements];
                for (int i = 0; i < referenced.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    ChannelEntry entry;
                    if (!channels.TryGetValue(block.Links[4 + 3 * i + 2], out entry)
                        || entry.DataGroup != block.Links[4 + 3 * i] || entry.Group != block.Links[4 + 3 * i + 1])
                    { throw new MdfFormatException("A channel-hierarchy element must address a catalog channel with its own DG and CG.", "MDF013", offset, block.Type); }
                    referenced[i] = entry.Channel;
                }
                MdfChannelHierarchy node = new MdfChannelHierarchy
                {
                    Name = text.Name(block.Links[2], frame.Depth + 1), Comment = text.Comment(block.Links[3], frame.Depth + 1),
                    Type = (MdfHierarchyType)data[4],
                    Channels = referenced.Length == 0 ? NoChannels : Array.AsReadOnly(referenced),
                    Children = NoNodes
                };
                frame.Siblings.Add(node);
                frame.Next = block.Links[0];
                if (block.Links[1] != 0)
                {
                    stack.Push(new Frame { Next = block.Links[1], Depth = frame.Depth + 1, Siblings = new List<MdfChannelHierarchy>(), Node = node });
                }
            }
            return Array.AsReadOnly(roots.ToArray());
        }

        private void Visit(ulong offset, int depth, HashSet<ulong> chain)
        {
            token.ThrowIfCancellationRequested();
            budget.CheckDepth(depth, offset);
            budget.ReserveMetadata(256, offset);
            if (!chain.Add(offset)) { throw new MdfFormatException("A sibling block chain contains a cycle.", "MDF010", offset); }
        }

        private static void RequireLinks(BlockHeader block, int count)
        {
            if (block.Links.Count != count) { throw Shape(block, "Block has an incompatible link count."); }
        }

        private static MdfFormatException Shape(BlockHeader block, string message) { return new MdfFormatException(message, "MDF006", block.Offset, block.Type); }

        private sealed class ChannelEntry
        {
            internal MdfChannel Channel;
            internal ulong Group;
            internal ulong DataGroup;
        }

        private sealed class Frame
        {
            internal ulong Next;
            internal int Depth;
            internal List<MdfChannelHierarchy> Siblings;
            internal MdfChannelHierarchy Node;
        }
    }
}
