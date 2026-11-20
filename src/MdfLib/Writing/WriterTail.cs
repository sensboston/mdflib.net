// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Threading;
using MdfLib.Internal;

namespace MdfLib.Writing
{
    // attachments and events, snapshotted when added and serialized as one tail behind the data at finalization.
    internal sealed class WriteAttachment
    {
        internal byte[] FileName, MimeType, Comment; // Encoded with NUL, or null when empty.
        internal bool CommentMd; // the comment is an MD block (XML with the common properties), not a TX block.
        internal byte[] Stored, Checksum; // Stored = embedded bytes (compressed when Compressed); null for external files.
        internal ulong OriginalSize;
        internal bool Compressed;
        internal ushort CreatorIndex; // the creator index, at offset 58 of the AT block.
        internal long Address;

        internal static WriteAttachment Create(MdfAttachmentDefinition value, WriterSettings settings, ulong allowance, Action checkpoint, out ulong charge)
        {
            if (value == null) { throw new ArgumentNullException(nameof(value)); }
            if (value.Data == null && string.IsNullOrEmpty(value.FileName))
            { throw new ArgumentException("An external attachment needs a file name.", nameof(value)); }
            if (value.Data == null && value.Compress) { throw new ArgumentException("Only embedded data can be compressed.", nameof(value)); }
            WriteAttachment result = new WriteAttachment
            {
                FileName = WriterTail.Text(settings, value.FileName), MimeType = WriterTail.Text(settings, value.MimeType),
                CreatorIndex = value.CreatorIndex
            };
            result.CommentMd = WriterMetadata.HasTags(value.CommonProperties);
            if (result.CommentMd)
            {
                int commentBytes;
                result.Comment = WriterMetadata.Comment("ATcomment", value.Comment, value.CommonProperties, settings, out commentBytes);
            }
            else
            {
                result.Comment = WriterTail.Text(settings, value.Comment);
            }
            charge = 256UL + WriterTail.Size(result.FileName) + WriterTail.Size(result.MimeType) + WriterTail.Size(result.Comment)
                + (value.Data == null ? 0UL : 2UL * (ulong)value.Data.LongLength);
            settings.Metadata(checked(allowance + charge)); // Reserve before copying, hashing and compressing.
            if (value.Data != null)
            {
                byte[] data = (byte[])value.Data.Clone();
                result.OriginalSize = (ulong)data.LongLength;
                result.Checksum = Md5.Hash(data, data.Length, checkpoint);
                result.Compressed = value.Compress;
                result.Stored = value.Compress ? Compression.ZlibDeflater.Compress(data, 0, data.Length, checkpoint) : data;
            }
            return result;
        }
    }

    internal sealed class WriteEvent
    {
        internal byte[] Name, Comment, GroupName;
        internal bool CommentMd; // the comment is an MD block (XML with the common properties), not a TX block.
        internal byte Type, Sync, Range, Cause;
        internal long SyncBase;
        internal double SyncFactor;
        internal ushort CreatorIndex; // the creator index, at offset 14 of the EV block's fixed fields.
        internal int Parent, RangeBegin;
        internal int[] Groups, Attachments;
        internal long Address;

        internal static WriteEvent Create(MdfEventDefinition value, WriterSettings settings, int events, int groups, int attachments,
            ulong allowance, out ulong charge)
        {
            if (value == null) { throw new ArgumentNullException(nameof(value)); }
            if (!Enum.IsDefined(typeof(MdfEventType), value.Type) || !Enum.IsDefined(typeof(MdfEventSyncType), value.SyncType)
                || !Enum.IsDefined(typeof(MdfEventRangeType), value.RangeType) || !Enum.IsDefined(typeof(MdfEventCause), value.Cause))
            { throw new ArgumentOutOfRangeException(nameof(value), "An event enumeration value is undefined."); }
            if (double.IsNaN(value.SyncFactor) || double.IsInfinity(value.SyncFactor))
            { throw new ArgumentException("The sync factor must be finite.", nameof(value)); }
            if (value.ParentIndex < -1 || value.ParentIndex >= events)
            { throw new ArgumentOutOfRangeException(nameof(value), "ParentIndex must be -1 or the index of an earlier event."); }
            if (value.RangeIndex < -1 || value.RangeIndex >= events)
            { throw new ArgumentOutOfRangeException(nameof(value), "RangeIndex must be -1 or the index of an earlier event."); }
            if (!string.IsNullOrEmpty(value.GroupName) && settings.Version != "4.20")
            { throw new NotSupportedException("Event group names need MDF 4.20."); }
            WriteEvent result = new WriteEvent
            {
                Name = WriterTail.Text(settings, value.Name),
                GroupName = WriterTail.Text(settings, value.GroupName),
                Type = (byte)value.Type, Sync = (byte)value.SyncType, Range = (byte)value.RangeType, Cause = (byte)value.Cause,
                SyncBase = value.SyncBaseValue, SyncFactor = value.SyncFactor, Parent = value.ParentIndex, RangeBegin = value.RangeIndex,
                CreatorIndex = value.CreatorIndex,
                Groups = Indices(value.ScopeGroupIndices, groups, "ScopeGroupIndices"),
                Attachments = Indices(value.AttachmentIndices, attachments, "AttachmentIndices")
            };
            if (result.Attachments.Length > ushort.MaxValue) { throw new NotSupportedException("An event references at most 65535 attachments."); }
            result.CommentMd = WriterMetadata.HasTags(value.CommonProperties);
            if (result.CommentMd)
            {
                int commentBytes;
                result.Comment = WriterMetadata.Comment("EVcomment", value.Comment, value.CommonProperties, settings, out commentBytes);
            }
            else
            {
                result.Comment = WriterTail.Text(settings, value.Comment);
            }
            charge = 256UL + 8UL * (ulong)(result.Groups.Length + result.Attachments.Length)
                + WriterTail.Size(result.Name) + WriterTail.Size(result.Comment) + WriterTail.Size(result.GroupName);
            settings.Metadata(checked(allowance + charge));
            return result;
        }

        private static int[] Indices(IReadOnlyList<int> values, int count, string name)
        {
            if (values == null) { return new int[0]; }
            int[] result = new int[values.Count];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = values[i];
                if (result[i] < 0 || result[i] >= count) { throw new ArgumentOutOfRangeException(name); }
            }
            return result;
        }
    }

    internal static class WriterTail
    {
        internal static byte[] Text(WriterSettings settings, string value)
        {
            if (string.IsNullOrEmpty(value)) { return null; }
            return settings.Encode(value, settings.TextSize(value, TextKind.Unit));
        }

        internal static ulong Size(byte[] text) { return text == null ? 0UL : 32UL + (ulong)text.Length; }

        private static long Align(long value) { return (value + 7) & ~7L; }

        private static long TextBlock(byte[] text, ref long position)
        {
            if (text == null) { return 0; }
            long at = Align(position);
            position = at + 24 + text.Length;
            return at;
        }

        // Plans addresses from `start` (aligned) and returns the serialized tail; AT then EV chains, each followed by its TX.
        internal static byte[] Build(long start, IReadOnlyList<WriteAttachment> attachments, IReadOnlyList<WriteEvent> events,
            IReadOnlyList<WriteGroup> groups, CancellationToken token)
        {
            long position = start;
            long[][] texts = new long[attachments.Count + events.Count][];
            for (int i = 0; i < attachments.Count; i++)
            {
                WriteAttachment at = attachments[i];
                at.Address = Align(position);
                position = checked(at.Address + 96 + (at.Stored == null ? 0 : at.Stored.LongLength));
                texts[i] = new[] { TextBlock(at.FileName, ref position), TextBlock(at.MimeType, ref position), TextBlock(at.Comment, ref position) };
            }
            for (int i = 0; i < events.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                WriteEvent ev = events[i];
                ev.Address = Align(position);
                int links = 5 + ev.Groups.Length + ev.Attachments.Length + (ev.GroupName != null ? 1 : 0);
                position = checked(ev.Address + 24 + 8L * links + 32);
                texts[attachments.Count + i] = new[] { TextBlock(ev.Name, ref position), TextBlock(ev.Comment, ref position), TextBlock(ev.GroupName, ref position) };
            }
            long total = Align(position) - start;
            if (total > int.MaxValue) { throw new NotSupportedException("Attachments and events exceed 2 GiB."); }
            byte[] bytes = new byte[total];
            for (int i = 0; i < attachments.Count; i++)
            {
                WriteAttachment at = attachments[i];
                int a = (int)(at.Address - start);
                Block(bytes, a, "AT", 96 + (at.Stored == null ? 0 : at.Stored.Length), 4);
                Link(bytes, a, 0, i + 1 < attachments.Count ? attachments[i + 1].Address : 0);
                for (int k = 0; k < 3; k++) { Link(bytes, a, 1 + k, texts[i][k]); }
                ushort flags = (ushort)(at.Stored == null ? 0 : 1 | 4 | (at.Compressed ? 2 : 0));
                WriterWire.UInt(bytes, a + 56, flags, 2);
                WriterWire.UInt(bytes, a + 58, at.CreatorIndex, 2);
                if (at.Stored != null)
                {
                    Array.Copy(at.Checksum, 0, bytes, a + 64, Md5.HashBytes);
                    WriterWire.UInt(bytes, a + 80, at.OriginalSize, 8);
                    WriterWire.UInt(bytes, a + 88, (ulong)at.Stored.Length, 8);
                    Array.Copy(at.Stored, 0, bytes, a + 96, at.Stored.Length);
                }
                Texts(bytes, start, texts[i], at.CommentMd ? 2 : -1, at.FileName, at.MimeType, at.Comment);
            }
            for (int i = 0; i < events.Count; i++)
            {
                WriteEvent ev = events[i];
                int e = (int)(ev.Address - start);
                int links = 5 + ev.Groups.Length + ev.Attachments.Length + (ev.GroupName != null ? 1 : 0);
                Block(bytes, e, "EV", 24 + 8 * links + 32, links);
                Link(bytes, e, 0, i + 1 < events.Count ? events[i + 1].Address : 0);
                Link(bytes, e, 1, ev.Parent >= 0 ? events[ev.Parent].Address : 0);
                Link(bytes, e, 2, ev.RangeBegin >= 0 ? events[ev.RangeBegin].Address : 0);
                Link(bytes, e, 3, texts[attachments.Count + i][0]);
                Link(bytes, e, 4, texts[attachments.Count + i][1]);
                for (int k = 0; k < ev.Groups.Length; k++) { Link(bytes, e, 5 + k, groups[ev.Groups[k]].ChannelGroupAddress); }
                for (int k = 0; k < ev.Attachments.Length; k++) { Link(bytes, e, 5 + ev.Groups.Length + k, attachments[ev.Attachments[k]].Address); }
                if (ev.GroupName != null) { Link(bytes, e, links - 1, texts[attachments.Count + i][2]); }
                int d = e + 24 + 8 * links;
                bytes[d] = ev.Type;
                bytes[d + 1] = ev.Sync;
                bytes[d + 2] = ev.Range;
                bytes[d + 3] = ev.Cause;
                bytes[d + 4] = (byte)(ev.GroupName != null ? 2 : 0);
                WriterWire.UInt(bytes, d + 8, (ulong)ev.Groups.Length, 4);
                WriterWire.UInt(bytes, d + 12, (ulong)ev.Attachments.Length, 2);
                WriterWire.UInt(bytes, d + 14, ev.CreatorIndex, 2);
                WriterWire.UInt(bytes, d + 16, unchecked((ulong)ev.SyncBase), 8);
                WriterWire.Double(bytes, d + 24, ev.SyncFactor);
                Texts(bytes, start, texts[attachments.Count + i], ev.CommentMd ? 1 : -1, ev.Name, ev.Comment, ev.GroupName);
            }
            return bytes;
        }

        // markupIndex is the text written as an MD block (the comment with common properties); -1 when there is none.
        private static void Texts(byte[] bytes, long start, long[] addresses, int markupIndex, params byte[][] texts)
        {
            for (int k = 0; k < texts.Length; k++)
            {
                if (texts[k] == null) { continue; }
                int at = (int)(addresses[k] - start);
                Block(bytes, at, k == markupIndex ? "MD" : "TX", 24 + texts[k].Length, 0);
                Array.Copy(texts[k], 0, bytes, at + 24, texts[k].Length);
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
    }
}
