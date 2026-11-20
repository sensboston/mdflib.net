// Copyright 2021 Ingemar Hedvall
// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Wire field tables from pinned MIT mdflib; new explicit LE metadata and bounded writer policy.
using System;
using System.Collections.Generic;
using System.Globalization;
using MdfLib.Writing;

namespace MdfLib.Formats.Mdf3
{
    internal static class WriterCodec
    {
        internal static void Write(WriterPlan plan, WriterSettings settings, IReadOnlyList<WriteGroup> groups)
        {
            byte[] bytes = plan.Prefix;
            WriterWire.Identification(bytes, settings);
            Block(bytes, 64, "HD", 208);
            WriterWire.UInt(bytes, 68, (ulong)plan.DataGroupAddress, 4);
            WriterWire.UInt(bytes, 80, 1, 2);
            DateTime time = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks((long)(settings.StartTime / 100));
            WriterWire.Ascii(bytes, 82, time.ToString("dd:MM:yyyy", CultureInfo.InvariantCulture));
            WriterWire.Ascii(bytes, 92, time.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            WriterWire.UInt(bytes, 228, settings.StartTime, 8);
            if (!settings.Upstream) { WriterWire.Ascii(bytes, 240, "MdfLib"); }
            WriterMetadata header = settings.Header;
            byte[][] fields = { header.Author, header.Organization, header.Project, header.Subject };
            for (int i = 0; i < fields.Length; i++)
            {
                if (fields[i] != null) { Array.Copy(fields[i], 0, bytes, 100 + WriterMetadata.Mdf3FieldBytes * i, fields[i].Length); }
            }
            if (plan.HeaderCommentAddress != 0)
            {
                int tx = (int)plan.HeaderCommentAddress;
                WriterWire.UInt(bytes, 72, (ulong)tx, 4);
                Block(bytes, tx, "TX", 4 + header.HeaderComment.Length);
                Array.Copy(header.HeaderComment, 0, bytes, tx + 4, header.HeaderComment.Length);
            }
            int dg = (int)plan.DataGroupAddress;
            Block(bytes, dg, "DG", 28);
            WriterWire.UInt(bytes, dg + 8, (ulong)plan.ChannelGroupAddress, 4);
            WriterWire.UInt(bytes, dg + 16, (ulong)plan.DataStart, 4);
            WriterWire.UInt(bytes, dg + 20, (ulong)groups.Count, 2);
            bool identified = groups.Count > 1; // one prefix record-ID byte (the group index + 1) per record.
            WriterWire.UInt(bytes, dg + 22, identified ? 1UL : 0, 2);
            for (int g = 0; g < groups.Count; g++)
            {
                WriteGroup group = groups[g];
                int cg = (int)group.ChannelGroupAddress;
                Block(bytes, cg, "CG", 26);
                WriterWire.UInt(bytes, cg + 4, g + 1 < groups.Count ? (ulong)groups[g + 1].ChannelGroupAddress : 0, 4);
                WriterWire.UInt(bytes, cg + 8, (ulong)group.Channels[0].ChannelAddress, 4);
                WriterWire.UInt(bytes, cg + 16, identified ? (ulong)(g + 1) : 0, 2);
                WriterWire.UInt(bytes, cg + 18, (ulong)group.Channels.Count, 2);
                WriterWire.UInt(bytes, cg + 20, (ulong)group.DataBytes, 2);
                group.CountOffset = cg + 22;
                Channels(bytes, group.Channels);
            }
        }

        private static void Channels(byte[] bytes, IReadOnlyList<WriteChannel> channels)
        {
            for (int i = 0; i < channels.Count; i++)
            {
                WriteChannel channel = channels[i];
                int cn = (int)channel.ChannelAddress;
                Block(bytes, cn, "CN", 228);
                WriterWire.UInt(bytes, cn + 4, i + 1 < channels.Count ? (ulong)channels[i + 1].ChannelAddress : 0, 4);
                WriterWire.UInt(bytes, cn + 8, (ulong)channel.ConversionAddress, 4);
                WriterWire.UInt(bytes, cn + 24, channel.Master ? 1UL : 0, 2);
                Array.Copy(channel.Name, 0, bytes, cn + 26, Math.Min(31, channel.Name.Length - 1));
                // cn_description is the CHAR[128] at 58 of this layout, right after the 32-byte short name;
                // the comment goes there and no CN comment or description TX link is written, which is how the reader
                // reports the field back as MdfChannel.Comment (it reads the field up to its first NUL). The stored text
                // carries a NUL of its own that the field has no room for at full width, hence the clamp: the policy in
                // TextSize already refuses a longer comment, and EncodeChannel refuses a long identifier for MDF 3.
                if (channel.Comment != null)
                { Array.Copy(channel.Comment, 0, bytes, cn + 58, Math.Min(WriterMetadata.Mdf3DescriptionBytes, channel.Comment.Length)); }
                int extra = channel.DataOffset / 8192 * 8192;
                WriterWire.UInt(bytes, cn + 186, (ulong)((channel.DataOffset - extra) * 8), 2);
                WriterWire.UInt(bytes, cn + 188, (ulong)channel.Bits, 2);
                int kind = channel.Kind == MdfValueKind.UnsignedInteger ? 0 : channel.Kind == MdfValueKind.SignedInteger ? 1 : channel.Bits == 32 ? 2 : 3;
                // String (7, the ID code page 28591) and Byte Array (8) fields have no byte order.
                int wire = channel.Kind == MdfValueKind.Text ? 7 : channel.Kind == MdfValueKind.ByteArray ? 8 : (channel.Big ? 9 : 13) + kind;
                WriterWire.UInt(bytes, cn + 190, (ulong)wire, 2);
                WriterWire.UInt(bytes, cn + 218, (ulong)channel.NameAddress, 4);
                WriterWire.UInt(bytes, cn + 226, (ulong)extra, 2);
                Block(bytes, (int)channel.NameAddress, "TX", 4 + channel.Name.Length);
                Array.Copy(channel.Name, 0, bytes, (int)channel.NameAddress + 4, channel.Name.Length);
                if (channel.Conversion != null) { Conversion(bytes, channel); }
                else if (channel.ConversionAddress != 0)
                {
                    int cc = (int)channel.ConversionAddress;
                    Block(bytes, cc, "CC", channel.Linear ? 62 : 46);
                    Array.Copy(channel.Unit, 0, bytes, cc + 22, channel.Unit.Length);
                    WriterWire.UInt(bytes, cc + 42, channel.Linear ? 0UL : 65535, 2);
                    WriterWire.UInt(bytes, cc + 44, channel.Linear ? 2UL : 0, 2);
                    if (channel.Linear)
                    {
                        WriterWire.Double(bytes, cc + 46, channel.Offset);
                        WriterWire.Double(bytes, cc + 54, channel.Factor);
                    }
                }
            }
        }

        // unit CHAR[20], type, count, then parameters, CHAR[256] formula, TABX or RTABX (entry 0 = default text).
        private static void Conversion(byte[] bytes, WriteChannel channel)
        {
            WriterConversion conversion = channel.Conversion;
            int cc = (int)channel.ConversionAddress;
            Block(bytes, cc, "CC", conversion.Mdf3Size);
            Array.Copy(channel.Unit, 0, bytes, cc + 22, channel.Unit.Length);
            WriterWire.UInt(bytes, cc + 42, (ulong)conversion.WireType, 2);
            WriterWire.UInt(bytes, cc + 44, (ulong)conversion.Mdf3Count, 2);
            double[] values = conversion.Values;
            switch (conversion.Kind)
            {
                case MdfConversionKind.Algebraic:
                    Array.Copy(conversion.Texts[0], 0, bytes, cc + 46, conversion.Texts[0].Length);
                    break;
                case MdfConversionKind.ValueToText:
                    for (int i = 0; i < values.Length; i++)
                    {
                        WriterWire.Double(bytes, cc + 46 + 40 * i, values[i]);
                        byte[] text = conversion.Texts[i];
                        if (text != null) { Array.Copy(text, 0, bytes, cc + 54 + 40 * i, text.Length); }
                    }
                    break;
                case MdfConversionKind.RangeToText:
                    int n = conversion.Texts.Length - 1;
                    for (int k = 0; k <= n; k++)
                    {
                        int entry = cc + 46 + 20 * k, index = k == 0 ? n : k - 1;
                        if (k > 0)
                        {
                            WriterWire.Double(bytes, entry, values[2 * index]);
                            WriterWire.Double(bytes, entry + 8, values[2 * index + 1]);
                        }
                        long address = conversion.TextAddresses[index];
                        WriterWire.UInt(bytes, entry + 16, (ulong)address, 4);
                        if (address != 0)
                        {
                            byte[] text = conversion.Texts[index];
                            Block(bytes, (int)address, "TX", 4 + text.Length);
                            Array.Copy(text, 0, bytes, (int)address + 4, text.Length);
                        }
                    }
                    break;
                default:
                    for (int i = 0; i < values.Length; i++) { WriterWire.Double(bytes, cc + 46 + 8 * i, values[i]); }
                    break;
            }
        }

        private static void Block(byte[] bytes, int at, string type, int length)
        {
            WriterWire.Ascii(bytes, at, type);
            WriterWire.UInt(bytes, at + 2, (ulong)length, 2);
        }
    }
}
