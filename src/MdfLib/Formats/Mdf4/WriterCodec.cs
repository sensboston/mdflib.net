// Copyright 2021 Ingemar Hedvall
// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Wire field tables from pinned MIT mdflib; planning/serialization and lifecycle policy are new.
using System;
using System.Collections.Generic;
using MdfLib.Writing;

namespace MdfLib.Formats.Mdf4
{
    internal static class WriterCodec
    {
        internal static void Write(WriterPlan plan, WriterSettings settings, IReadOnlyList<WriteGroup> groups)
        {
            byte[] bytes = plan.Prefix;
            long o = plan.Origin; // append: the prefix starts at the aligned end of the existing file.
            if (o == 0)
            {
                WriterWire.Identification(bytes, settings);
                Block(bytes, 64, "HD", 104, 6);
                Link(bytes, 64, 0, groups[0].DataGroupAddress);
                WriterWire.UInt(bytes, 136, settings.StartTime, 8);
                bytes[148] = 2; // UTC timestamp, no timezone guessing.
                WriterMetadata header = settings.Header;
                // HD start angle and distance: hd_flags bits 0x01 and 0x02 mark them valid (upstream Hd4Block).
                if (header.StartAngle.HasValue) { bytes[150] |= 0x01; WriterWire.Double(bytes, 152, header.StartAngle.Value); }
                if (header.StartDistance.HasValue) { bytes[150] |= 0x02; WriterWire.Double(bytes, 160, header.StartDistance.Value); }
                if (plan.HeaderCommentAddress != 0)
                {
                    Link(bytes, 64, 5, plan.HeaderCommentAddress);
                    Metadata(bytes, (int)plan.HeaderCommentAddress, header.HeaderComment);
                }
                if (plan.HistoryAddress != 0)
                {
                    int fh = (int)plan.HistoryAddress;
                    Link(bytes, 64, 1, fh);
                    Block(bytes, fh, "FH", 56, 2);
                    Link(bytes, fh, 1, plan.HistoryCommentAddress);
                    WriterWire.UInt(bytes, fh + 40, header.HistoryTime, 8);
                    bytes[fh + 52] = 2; // Same UTC policy as the header.
                    Metadata(bytes, (int)plan.HistoryCommentAddress, header.HistoryComment);
                }
            }
            for (int g = 0; g < groups.Count; g++)
            {
                WriteGroup group = groups[g];
                IReadOnlyList<WriteChannel> channels = group.Channels;
                int cg = R(group.ChannelGroupAddress, o);
                if (group.DataOwner == null)
                {
                    // one sorted DG per CG, unless the following groups share it (unsorted, record IDs).
                    int dg = R(group.DataGroupAddress, o);
                    Block(bytes, dg, "DG", 64, 4);
                    Link(bytes, dg, 0, NextDataGroup(groups, g));
                    Link(bytes, dg, 1, group.ChannelGroupAddress);
                    Link(bytes, dg, 2, plan.DataBlockAddress);
                    if (group.Members > 1 || group.VlsdChannels.Count != 0) { bytes[dg + 56] = 1; } // one record ID byte
                    Link(bytes, dg, 3, group.DataGroupCommentAddress);
                    if (group.DataGroupCommentAddress != 0) { Metadata(bytes, R(group.DataGroupCommentAddress, o), group.DataGroupComment); }
                }
                else
                {
                    // a group sharing a DG chains behind the group before it in the same DG.
                    for (int k = g - 1; k >= 0; k--)
                    {
                        if (groups[k].Owner == group.DataOwner) { Link(bytes, R(groups[k].ChannelGroupAddress, o), 0, group.ChannelGroupAddress); break; }
                    }
                }
                Block(bytes, cg, "CG", 104, 6);
                if (group.Members > 1 || (group.DataOwner != null)) { WriterWire.UInt(bytes, cg + 72, group.RecordId, 8); }
                IReadOnlyList<WriteChannel> vlsd = group.VlsdChannels;
                if (vlsd.Count != 0)
                {
                    // unsorted DG: the fixed CG (record ID 1) chains to one VLSD CG per VLSD channel (IDs 2..).
                    WriterWire.UInt(bytes, cg + 72, 1, 8);
                    Link(bytes, cg, 0, vlsd[0].VlsdGroupAddress);
                    for (int k = 0; k < vlsd.Count; k++)
                    {
                        int vcg = R(vlsd[k].VlsdGroupAddress, o);
                        Block(bytes, vcg, "CG", 104, 6);
                        Link(bytes, vcg, 0, k + 1 < vlsd.Count ? vlsd[k + 1].VlsdGroupAddress : 0);
                        WriterWire.UInt(bytes, vcg + 72, (ulong)(k + 2), 8);
                        WriterWire.UInt(bytes, vcg + 88, 1, 2); // cg_flags bit 0: VLSD.
                        vlsd[k].VlsdCountOffset = o + vcg + 80;
                    }
                }
                // top-level channels form the CG chain; components chain under their parent's composition link.
                int[] next = new int[channels.Count], first = new int[channels.Count];
                int[] last = new int[channels.Count + 1];
                for (int i = 0; i <= channels.Count; i++) { last[i] = -1; }
                for (int i = 0; i < channels.Count; i++)
                {
                    next[i] = -1;
                    first[i] = -1;
                }
                for (int i = 0; i < channels.Count; i++)
                {
                    int parent = channels[i].Parent;
                    int slot = parent + 1;
                    if (last[slot] >= 0) { next[last[slot]] = i; }
                    else if (parent >= 0) { first[parent] = i; }
                    last[slot] = i;
                }
                Link(bytes, cg, 1, channels[0].ChannelAddress);
                Link(bytes, cg, 2, group.NameAddress);
                Link(bytes, cg, 3, group.SourceAddress);
                if (group.NameAddress != 0) { Text(bytes, R(group.NameAddress, o), group.Name); }
                // the group comment MD block and the full SI payload (name/path/description/flags).
                Link(bytes, cg, 5, group.CommentAddress);
                if (group.CommentAddress != 0) { Metadata(bytes, R(group.CommentAddress, o), group.CommentText); }
                if (group.SourceAddress != 0)
                {
                    int source = R(group.SourceAddress, o);
                    Block(bytes, source, "SI", 56, 3);
                    Link(bytes, source, 0, group.SourceNameAddress);
                    Link(bytes, source, 1, group.SourcePathAddress);
                    Link(bytes, source, 2, group.SourceCommentAddress);
                    bytes[source + 48] = group.SourceType;
                    bytes[source + 49] = group.BusType;
                    bytes[source + 50] = group.SourceFlags;
                    if (group.SourceName != null) { Text(bytes, R(group.SourceNameAddress, o), group.SourceName); }
                    if (group.SourcePathAddress != 0) { Text(bytes, R(group.SourcePathAddress, o), group.SourcePath); }
                    if (group.SourceCommentAddress != 0) { Metadata(bytes, R(group.SourceCommentAddress, o), group.SourceComment); }
                }
                WriterWire.UInt(bytes, cg + 88, group.Flags, 2);
                WriterWire.UInt(bytes, cg + 90, group.PathSeparator, 2);
                WriterWire.UInt(bytes, cg + 96, (ulong)group.DataBytes, 4);
                WriterWire.UInt(bytes, cg + 100, (ulong)(group.RecordBytes - group.DataBytes), 4);
                group.CountOffset = o + cg + 80;
                for (int i = 0; i < channels.Count; i++)
                {
                    WriteChannel channel = channels[i];
                    int cn = R(channel.ChannelAddress, o);
                    Block(bytes, cn, "CN", 160, 8);
                    Link(bytes, cn, 0, next[i] >= 0 ? channels[next[i]].ChannelAddress : 0);
                    Link(bytes, cn, 1, first[i] >= 0 ? channels[first[i]].ChannelAddress : channel.ArrayAddress);
                    Link(bytes, cn, 2, channel.NameAddress);
                    Link(bytes, cn, 3, channel.SourceAddress);
                    Link(bytes, cn, 7, channel.CommentAddress);
                    Link(bytes, cn, 4, channel.ConversionAddress);
                    Link(bytes, cn, 5, channel.Vlsd ? channel.VlsdGroupAddress : channel.Size >= 0 ? channels[channel.Size].ChannelAddress : 0);
                    Link(bytes, cn, 6, channel.UnitAddress);
                    bytes[cn + 88] = channel.Explicit ? (byte)channel.Type : channel.Master ? (byte)2 : channel.Vlsd ? (byte)1 : (byte)0;
                    bytes[cn + 89] = channel.SyncType != 0 ? channel.SyncType : channel.Master ? (byte)1 : (byte)0;
                    bytes[cn + 90] = DataType(channel);
                    bytes[cn + 91] = (byte)channel.BitOffset;
                    WriterWire.UInt(bytes, cn + 92, (ulong)channel.DataOffset, 4);
                    WriterWire.UInt(bytes, cn + 96, (ulong)channel.Bits, 4);
                    WriterWire.UInt(bytes, cn + 100, ChannelFlagsOf(channel), 4);
                    WriterWire.UInt(bytes, cn + 104, channel.Explicit ? 0UL : (ulong)channel.InvalidationBit, 4);
                    ChannelFields(bytes, cn, channel);
                    if (channel.ArrayAddress != 0)
                    {
                        // CN-template array: ca_type 0, storage 0, no flags; element stride and invalidation-bit step 1.
                        int ca = R(channel.ArrayAddress, o);
                        int rank = channel.Dimensions.Length;
                        Block(bytes, ca, "CA", 48 + 8 * rank, 1);
                        WriterWire.UInt(bytes, ca + 34, (ulong)rank, 2);
                        WriterWire.UInt(bytes, ca + 40, (ulong)channel.Stride, 4);
                        WriterWire.UInt(bytes, ca + 44, 1, 4);
                        for (int axis = 0; axis < rank; axis++) { WriterWire.UInt(bytes, ca + 48 + 8 * axis, channel.Dimensions[axis], 8); }
                    }
                    // the CN comment MD block, the long identifier field and the per-channel SI block.
                    if (channel.CommentAddress != 0) { Metadata(bytes, R(channel.CommentAddress, o), channel.CommentText); }
                    // cn_long_identifier is the 26-byte tail field of this block (134..160): EncodeChannel refused a
                    // text whose stored size does not fit it, and no link points at it, so the block stays 160 bytes.
                    if (channel.Description != null) { Array.Copy(channel.Description, 0, bytes, cn + 134, channel.Description.Length); }
                    if (channel.SourceAddress != 0)
                    {
                        int si = R(channel.SourceAddress, o);
                        Block(bytes, si, "SI", 56, 3);
                        Link(bytes, si, 0, channel.SourceNameAddress);
                        Link(bytes, si, 1, channel.SourcePathAddress);
                        Link(bytes, si, 2, channel.SourceCommentAddress);
                        bytes[si + 48] = channel.SourceType;
                        bytes[si + 49] = channel.BusType;
                        bytes[si + 50] = channel.SourceFlags;
                        if (channel.SourceId != null) { Text(bytes, R(channel.SourceNameAddress, o), channel.SourceId); }
                        if (channel.SourcePathAddress != 0) { Text(bytes, R(channel.SourcePathAddress, o), channel.SourcePath); }
                        if (channel.SourceCommentAddress != 0) { Metadata(bytes, R(channel.SourceCommentAddress, o), channel.SourceComment); }
                    }
                    Text(bytes, R(channel.NameAddress, o), channel.Name);
                    if (channel.UnitAddress != 0) { Text(bytes, R(channel.UnitAddress, o), channel.Unit); }
                    if (channel.Conversion != null) { Conversion(bytes, o, channel); }
                    else if (channel.ConversionAddress != 0)
                    {
                        int cc = R(channel.ConversionAddress, o);
                        Block(bytes, cc, "CC", 96, 4);
                        Link(bytes, cc, 1, channel.UnitAddress);
                        bytes[cc + 56] = 1;
                        WriterWire.UInt(bytes, cc + 62, 2, 2);
                        WriterWire.Double(bytes, cc + 80, channel.Offset);
                        WriterWire.Double(bytes, cc + 88, channel.Factor);
                    }
                }
            }
            if (plan.DataBlockAddress != 0) { Block(bytes, R(plan.DataBlockAddress, o), "DT", 24, 0); } // compressed data is linked at finalization.
        }

        // The next data group of the file: the first group after g that owns a DG block, or NIL.
        private static long NextDataGroup(IReadOnlyList<WriteGroup> groups, int g)
        {
            for (int k = g + 1; k < groups.Count; k++) { if (groups[k].DataOwner == null) { return groups[k].DataGroupAddress; } }
            return 0;
        }

        // 0/2/4 numeric (+1 BE), 7 UTF-8 text, 10 byte array, 15/16 complex.
        private static byte DataType(WriteChannel channel)
        {
            switch (channel.Kind)
            {
                case MdfValueKind.Text: return 7;
                case MdfValueKind.ByteArray: return 10;
                case MdfValueKind.Complex: return channel.Big ? (byte)16 : (byte)15;
                default:
                    return (byte)((channel.Kind == MdfValueKind.UnsignedInteger ? 0 : channel.Kind == MdfValueKind.SignedInteger ? 2 : 4) + (channel.Big ? 1 : 0));
            }
        }

        private static void Block(byte[] bytes, int at, string type, int length, int links)
        {
            WriterWire.Ascii(bytes, at, "##" + type);
            WriterWire.UInt(bytes, at + 8, (ulong)length, 8);
            WriterWire.UInt(bytes, at + 16, (ulong)links, 8);
        }
        private static void Link(byte[] bytes, int block, int index, long target)
        { WriterWire.UInt(bytes, block + 24 + 8 * index, (ulong)target, 8); }
        private static int R(long address, long origin) { return checked((int)(address - origin)); }
        // cc_type, cc_ref_count/cc_val_count, references (TX or NIL) then values; unit shared with the channel.
        private static void Conversion(byte[] bytes, long o, WriteChannel channel)
        {
            WriterConversion conversion = channel.Conversion;
            int cc = R(channel.ConversionAddress, o);
            int references = conversion.Texts.Length;
            Block(bytes, cc, "CC", conversion.Mdf4Size, 4 + references);
            // cc_tx_name, cc_md_unit (the conversion's own unit, else the channel's as before), cc_md_comment.
            Link(bytes, cc, 0, conversion.NameAddress);
            Link(bytes, cc, 1, conversion.UnitAddress != 0 ? conversion.UnitAddress : channel.UnitAddress);
            Link(bytes, cc, 2, conversion.CommentAddress);
            Link(bytes, cc, 3, conversion.InverseAddress);
            if (conversion.NameAddress != 0) { Text(bytes, R(conversion.NameAddress, o), conversion.Name); }
            if (conversion.UnitAddress != 0) { Text(bytes, R(conversion.UnitAddress, o), conversion.Unit); }
            if (conversion.CommentAddress != 0) { Text(bytes, R(conversion.CommentAddress, o), conversion.Comment); }
            for (int i = 0; i < references; i++)
            {
                Link(bytes, cc, 4 + i, conversion.TextAddresses[i]);
                if (conversion.Texts[i] != null) { Text(bytes, R(conversion.TextAddresses[i], o), conversion.Texts[i]); }
            }
            int data = cc + 24 + 8 * (4 + references);
            bytes[data] = (byte)conversion.WireType;
            WriterWire.UInt(bytes, data + 4, (ulong)references, 2);
            WriterWire.UInt(bytes, data + 6, (ulong)conversion.Values.Length, 2);
            ConversionFields(bytes, data, conversion);
            for (int i = 0; i < conversion.Values.Length; i++) { WriterWire.Double(bytes, data + 24 + 8 * i, conversion.Values[i]); }
            if (conversion.Inverse != null) { InverseBlock(bytes, o, conversion.Inverse, conversion.InverseAddress); }
        }

        // cc_inverse: a numeric conversion block of its own, with no links but the type, the values and the range.
        private static void InverseBlock(byte[] bytes, long o, WriterConversion inverse, long address)
        {
            int cc = R(address, o);
            Block(bytes, cc, "CC", inverse.Mdf4Size, 4);
            int data = cc + 24 + 8 * 4;
            bytes[data] = (byte)inverse.WireType;
            WriterWire.UInt(bytes, data + 6, (ulong)inverse.Values.Length, 2);
            ConversionFields(bytes, data, inverse);
            for (int i = 0; i < inverse.Values.Length; i++) { WriterWire.Double(bytes, data + 24 + 8 * i, inverse.Values[i]); }
        }

        // cc_precision (byte 1), cc_flags (uint16 at 2) with the validity bits of the precision (0x1) and the range (0x2),
        // and cc_phy_range_min/max (at 8 and 16) when the range is set.
        private static void ConversionFields(byte[] bytes, int data, WriterConversion conversion)
        {
            uint flags = conversion.Flags;
            if (conversion.HasPrecision) { flags |= 0x1U; bytes[data + 1] = conversion.Precision; }
            if (conversion.Range != null)
            {
                flags |= 0x2U;
                WriterWire.Double(bytes, data + 8, conversion.Range[0]);
                WriterWire.Double(bytes, data + 16, conversion.Range[1]);
            }
            WriterWire.UInt(bytes, data + 2, flags, 2);
        }

        // cn_flags: the explicit bus layout's bits, or the ordinary channel's invalidation bit (2, as before), plus the
        // caller's bits and the validity bits of the precision (0x4), range (0x8), limit (0x10) and extended limit (0x20).
        private static ulong ChannelFlagsOf(WriteChannel channel)
        {
            uint flags = (channel.Explicit ? channel.Flags : 2U) | channel.ChannelFlags;
            if (channel.HasPrecision) { flags |= 0x4U; }
            if (channel.Range != null) { flags |= 0x8U; }
            if (channel.Limit != null) { flags |= 0x10U; }
            if (channel.ExtLimit != null) { flags |= 0x20U; }
            return flags;
        }

        // cn_precision (108), cn_val_range_min/max (112), cn_limit_min/max (128) and cn_limit_ext_min/max (144).
        private static void ChannelFields(byte[] bytes, int cn, WriteChannel channel)
        {
            if (channel.HasPrecision) { bytes[cn + 108] = channel.Precision; }
            WriteRange(bytes, cn + 112, channel.Range);
            WriteRange(bytes, cn + 128, channel.Limit);
            WriteRange(bytes, cn + 144, channel.ExtLimit);
        }

        private static void WriteRange(byte[] bytes, int at, double[] values)
        {
            if (values == null) { return; }
            WriterWire.Double(bytes, at, values[0]);
            WriterWire.Double(bytes, at + 8, values[1]);
        }

        private static void Metadata(byte[] bytes, int at, byte[] xml)
        {
            Block(bytes, at, "MD", 24 + xml.Length, 0);
            Array.Copy(xml, 0, bytes, at + 24, xml.Length);
        }
        private static void Text(byte[] bytes, int at, byte[] text)
        {
            Block(bytes, at, "TX", 24 + text.Length, 0);
            Array.Copy(text, 0, bytes, at + 24, text.Length);
        }
    }
}
