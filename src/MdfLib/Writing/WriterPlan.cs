// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Threading;

namespace MdfLib.Writing
{
    internal sealed class WriterPlan
    {
        internal byte[] Prefix;
        internal byte[] FinalIdentification;
        internal long DataStart, DataBlockAddress, DataGroupAddress, ChannelGroupAddress, CountOffset;
        internal long Origin; // append: absolute offset of Prefix[0] (0 for a new file).
        internal long HeaderCommentAddress, HistoryAddress, HistoryCommentAddress; // 0 when absent.
        private readonly WriterSettings settings;
        private readonly ulong allowance;
        private long position = 64;

        private WriterPlan(WriterSettings settings, ulong allowance)
        {
            this.settings = settings;
            this.allowance = allowance;
        }

        internal static WriterPlan Create(WriterSettings settings, IReadOnlyList<WriteGroup> groups, ulong allowance, CancellationToken token, long origin = 0)
        {
            WriterPlan plan = new WriterPlan(settings, allowance);
            if (origin == 0)
            {
                plan.Reserve(settings.Mdf3 ? 208 : 104);
                WriterMetadata header = settings.Header;
                if (header.HeaderComment != null) { plan.HeaderCommentAddress = plan.Reserve((settings.Mdf3 ? 4 : 24) + header.HeaderComment.Length); }
                if (header.HistoryComment != null)
                {
                    plan.HistoryAddress = plan.Reserve(56);
                    plan.HistoryCommentAddress = plan.Reserve(24 + header.HistoryComment.Length);
                }
            }
            else { plan.Origin = plan.position = origin; } // Aligned end of an existing MDF 4 file; no ID/HD.
            foreach (WriteGroup group in groups)
            {
                // MDF 3 writes one DG whose channel groups are told apart by a one-byte record ID; MDF 4 groups
                // that share a DG (DataOwner) take the owner's DG block, which precedes them in the list.
                group.DataGroupAddress = group.DataOwner != null ? group.DataOwner.DataGroupAddress : plan.Reserve(settings.Mdf3 ? 28 : 64);
                group.DataGroupCommentAddress = group.DataOwner == null && group.DataGroupComment != null ? plan.Reserve(24 + group.DataGroupComment.Length) : 0;
                group.ChannelGroupAddress = plan.Reserve(settings.Mdf3 ? 26 : 104);
                group.NameAddress = group.Name != null ? plan.Reserve(24 + group.Name.Length) : 0;
                // An SI block and its texts; the description MD block is only reserved when the group actually has one.
                group.SourceAddress = (group.SourceName != null || group.SourceComment != null) ? plan.Reserve(56) : 0;
                group.SourceNameAddress = group.SourceName != null ? plan.Reserve(24 + group.SourceName.Length) : 0;
                group.SourcePathAddress = group.SourcePath != null ? plan.Reserve(24 + group.SourcePath.Length) : 0;
                group.SourceCommentAddress = group.SourceComment != null ? plan.Reserve(24 + group.SourceComment.Length) : 0;
                group.CommentAddress = group.CommentText != null ? plan.Reserve(24 + group.CommentText.Length) : 0;
                foreach (WriteChannel channel in group.Channels)
                {
                    token.ThrowIfCancellationRequested();
                    channel.ChannelAddress = plan.Reserve(settings.Mdf3 ? 228 : 160);
                    channel.NameAddress = plan.Reserve((settings.Mdf3 ? 4 : 24) + channel.Name.Length);
                    // an MDF 4 comment and a long identifier longer than the fixed field are own blocks; the
                    // MDF 3 comment and identifier are fields of the 228-byte CN itself, so nothing more is reserved.
                    channel.CommentAddress = channel.CommentText != null ? plan.Reserve(24 + channel.CommentText.Length) : 0;
                    channel.SourceAddress = (channel.SourceId != null || channel.SourceComment != null) ? plan.Reserve(56) : 0;
                    channel.SourceNameAddress = channel.SourceId != null ? plan.Reserve(24 + channel.SourceId.Length) : 0;
                    channel.SourcePathAddress = channel.SourcePath != null ? plan.Reserve(24 + channel.SourcePath.Length) : 0;
                    channel.SourceCommentAddress = channel.SourceComment != null ? plan.Reserve(24 + channel.SourceComment.Length) : 0;
                    channel.UnitAddress = !settings.Mdf3 && channel.Unit.Length > 1 ? plan.Reserve(24 + channel.Unit.Length) : 0;
                    channel.VlsdGroupAddress = channel.Vlsd ? plan.Reserve(104) : 0; // the channel's own VLSD CG.
                    channel.ArrayAddress = channel.Dimensions != null ? plan.Reserve(48 + 8 * channel.Dimensions.Length) : 0; // CA.
                    WriterConversion conversion = channel.Conversion;
                    if (conversion != null)
                    {
                        channel.ConversionAddress = plan.Reserve(settings.Mdf3 ? conversion.Mdf3Size : conversion.Mdf4Size);
                        conversion.NameAddress = conversion.Name == null ? 0 : plan.Reserve(24 + conversion.Name.Length);
                        conversion.UnitAddress = conversion.Unit == null ? 0 : plan.Reserve(24 + conversion.Unit.Length);
                        conversion.CommentAddress = conversion.Comment == null ? 0 : plan.Reserve(24 + conversion.Comment.Length);
                        if (conversion.Inverse != null) { conversion.InverseAddress = plan.Reserve(conversion.Inverse.Mdf4Size); }
                        if (conversion.SeparateTexts(settings.Mdf3))
                        {
                            for (int i = 0; i < conversion.Texts.Length; i++)
                            {
                                byte[] text = conversion.Texts[i];
                                conversion.TextAddresses[i] = text == null ? 0 : plan.Reserve((settings.Mdf3 ? 4 : 24) + text.Length);
                            }
                        }
                    }
                    else
                    {
                        channel.ConversionAddress = channel.Linear || (settings.Mdf3 && channel.Unit.Length > 1)
                            ? plan.Reserve(settings.Mdf3 ? (channel.Linear ? 62 : 46) : 96) : 0;
                    }
                }
            }
            plan.DataGroupAddress = groups[0].DataGroupAddress;
            plan.ChannelGroupAddress = groups[0].ChannelGroupAddress;
            // DZ blocks and per-group DT/DZ blocks follow the records; only one plain group uses one growing DT.
            int dataGroups = WriteGroup.CountDataGroups(groups);
            plan.DataBlockAddress = settings.Mdf3 || settings.Compress || dataGroups > 1 ? 0 : plan.Reserve(24);
            plan.DataStart = plan.position;
            settings.Metadata(checked(allowance + (ulong)(plan.position - plan.Origin)));
            token.ThrowIfCancellationRequested();
            plan.Prefix = new byte[checked((int)(plan.position - plan.Origin))];
            if (settings.Mdf3) { Formats.Mdf3.WriterCodec.Write(plan, settings, groups); }
            else { Formats.Mdf4.WriterCodec.Write(plan, settings, groups); }
            plan.CountOffset = groups[0].CountOffset;
            if (origin != 0) { return plan; } // Appended groups are published by one link patch instead of the ID.
            plan.FinalIdentification = new byte[64];
            Array.Copy(plan.Prefix, plan.FinalIdentification, 64);
            WriterWire.Ascii(plan.Prefix, 0, "UnFinMF ");
            // id_unfi_flags for explicit recovery: cycle counters (bit 0) and, for one growing DT, its length (bit 2).
            // per-group/compressed blocks are linked into preallocated DLs as they complete; the last DL of each
            // chain then needs its link count shrunk (bit 4). No custom flags.
            bool blocked = settings.Compress || (dataGroups > 1 && !settings.Mdf3);
            bool vlsd = false; // VLSD CG byte totals are recomputed too (bit 5).
            foreach (WriteGroup group in groups) { vlsd |= group.VlsdChannels.Count != 0; }
            WriterWire.UInt(plan.Prefix, 60, (settings.Mdf3 ? 1UL : blocked ? 0x11UL : 5UL) | (vlsd ? 0x20UL : 0), 2);
            return plan;
        }

        private long Reserve(int length)
        {
            long start = settings.Mdf3 ? position : (position + 7) & ~7L;
            long next = checked(start + length);
            // The metadata limit (at most Int32.MaxValue) bounds the prefix; append origins may exceed 2 GiB.
            settings.Metadata(checked(allowance + (ulong)(next - Origin)));
            position = next;
            return start;
        }

        internal static long CheckAppend(bool mdf3, ulong count, long end, int recordBytes)
        {
            if (end < 0 || recordBytes <= 0) { throw new ArgumentOutOfRangeException(nameof(end)); }
            if (count == ulong.MaxValue || (mdf3 && count >= uint.MaxValue))
            { throw new NotSupportedException("The selected MDF revision's sample counter is exhausted."); }
            ulong next = (ulong)end + (ulong)recordBytes;
            ulong padding = mdf3 ? 0 : (8 - (next & 7)) & 7;
            if (next > (ulong)long.MaxValue - padding || (mdf3 && next > uint.MaxValue))
            { throw new NotSupportedException("The record and final alignment exceed the selected MDF/Stream address range."); }
            return (long)next;
        }
    }

    // Small managed serialization primitives shared by the internal format codecs.
    internal static class WriterWire
    {
        internal static void UInt(byte[] bytes, int offset, ulong value, int width)
        {
            for (int i = 0; i < width; i++) { bytes[offset + i] = (byte)(value >> (8 * i)); }
        }

        internal static void Double(byte[] bytes, int offset, double value)
        {
            UInt(bytes, offset, unchecked((ulong)BitConverter.DoubleToInt64Bits(value)), 8);
        }

        internal static void Ascii(byte[] bytes, int offset, string text)
        {
            for (int i = 0; i < text.Length; i++) { bytes[offset + i] = checked((byte)text[i]); }
        }

        internal static void Identification(byte[] bytes, WriterSettings settings)
        {
            Ascii(bytes, 0, "MDF     ");
            Ascii(bytes, 8, settings.Version);
            for (int i = 16; i < 24; i++) { bytes[i] = 0; }
            if (settings.ProgramIdentifier != null) { Ascii(bytes, 16, settings.ProgramIdentifier); }
            else if (!settings.Upstream) { Ascii(bytes, 16, "MdfLib  "); }
            else if (settings.Mdf3) { Ascii(bytes, 16, "MdfWrite"); }
            UInt(bytes, 28, settings.Mdf3 ? 330UL : settings.Version == "4.20" ? 420UL : 410UL, 2);
            if (settings.Mdf3) { UInt(bytes, 30, 28591, 2); }
        }
    }
}
