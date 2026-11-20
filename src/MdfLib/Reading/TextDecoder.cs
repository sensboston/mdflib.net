// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Text;
using MdfLib.Formats;
using MdfLib.IO;
using MdfLib.Internal;

namespace MdfLib.Reading
{
    // text/byte values. Decoding is original and identical on every target framework:
    // no Encoding.UTF8 (its replacement behavior differs between.NET Framework and.NET).
    // Internal DataType codes are MDF 4 numbers (6 Latin-1, 7 UTF-8, 8/9 UTF-16, 10 bytes) plus Ascii (MDF 3 code page 20127).
    internal static class TextDecoder
    {
        internal const byte Ascii = 11;

        internal static bool IsValid(byte[] buffer, int recordOffset, RecordLayout record, ChannelLayout layout)
        {
            bool valid = (layout.Flags & 1) == 0;
            if (valid && (layout.Flags & 2) != 0)
            {
                int invalidationByte = checked(recordOffset + (int)record.DataBytes + (int)(layout.InvalidationBit / 8));
                valid = (buffer[invalidationByte] & (1 << (int)(layout.InvalidationBit & 7))) == 0;
            }
            return valid;
        }

        internal static MdfSampleValue Fixed(byte[] buffer, int recordOffset, RecordLayout record, ChannelLayout layout, int element = 0)
        {
            // array element s (stored order) at ByteOffset + stride*s with bit InvalidationBit + s*ElementInvalidationStride.
            ulong index = ScalarDecoder.StoredIndex(layout, element);
            ulong byteOffset = (ulong)layout.ByteOffset + index * (uint)layout.ElementByteStride;
            bool valid = (layout.Flags & 1) == 0;
            if (valid && (layout.Flags & 2) != 0)
            {
                ulong bit = (ulong)layout.InvalidationBit + index * layout.ElementInvalidationStride;
                int invalidationByte = checked(recordOffset + (int)record.DataBytes + (int)(bit / 8));
                valid = (buffer[invalidationByte] & (1 << (int)(bit & 7))) == 0;
            }
            byte[] stored = new byte[(int)layout.FieldBytes]; // Metadata proved every element inside one bounded record.
            Buffer.BlockCopy(buffer, checked(recordOffset + (int)byteOffset), stored, 0, stored.Length);
            return Create(valid, layout.DataType, stored);
        }

        // the first N bytes of the max-length field, N = raw value of the same-record
        // size channel. An invalid size is never used or bound-checked; a valid N above the maximum is malformed.
        internal static MdfSampleValue MaxLength(byte[] buffer, int recordOffset, RecordLayout record, ChannelLayout layout)
        {
            MdfSampleValue size = ScalarDecoder.Read(buffer, recordOffset, record, layout.SizeChannel, MdfValueRepresentation.Raw, 0);
            if (!size.IsValid) { return Empty(layout.DataType); }
            if (size.UnsignedValue > layout.FieldBytes)
            {
                throw new MdfFormatException("An MLSD size value exceeds the channel's maximum length.", "MDF013", layout.SizeAddress, "##CN");
            }
            byte[] stored = new byte[(int)size.UnsignedValue]; // At most FieldBytes, proven inside one bounded record.
            Buffer.BlockCopy(buffer, checked(recordOffset + (int)layout.ByteOffset), stored, 0, stored.Length);
            return Create(IsValid(buffer, recordOffset, record, layout), layout.DataType, stored);
        }

        internal static ulong Offset(byte[] buffer, int recordOffset, ChannelLayout layout)
        {
            return EndianBitConverter.ReadUInt64(buffer, checked(recordOffset + (int)layout.ByteOffset), ByteOrder.LittleEndian);
        }

        internal static MdfSampleValue Create(bool valid, byte dataType, byte[] stored)
        {
            return new MdfSampleValue(valid, dataType == 10 ? null : Decode(dataType, stored), stored);
        }

        internal static MdfSampleValue Empty(byte dataType)
        {
            return Create(false, dataType, new byte[0]);
        }

        internal static string Decode(byte dataType, byte[] bytes)
        {
            switch (dataType)
            {
                case 6: return Latin1(bytes);
                case 7: return Utf8(bytes);
                case 8: return Utf16(bytes, false);
                case Ascii: return AsciiText(bytes);
                default: return Utf16(bytes, true);
            }
        }

        // MDF 3 code page 20127 sample values: one U+FFFD per byte above 0x7F (never an error, never trimmed).
        private static string AsciiText(byte[] bytes)
        {
            int length = Array.IndexOf(bytes, (byte)0);
            if (length < 0) { length = bytes.Length; }
            char[] chars = new char[length];
            for (int index = 0; index < length; index++) { chars[index] = bytes[index] < 0x80 ? (char)bytes[index] : '\uFFFD'; }
            return new string(chars);
        }

        private static string Latin1(byte[] bytes)
        {
            int length = Array.IndexOf(bytes, (byte)0);
            if (length < 0) { length = bytes.Length; }
            char[] chars = new char[length];
            for (int index = 0; index < length; index++) { chars[index] = (char)bytes[index]; }
            return new string(chars);
        }

        private static string Utf16(byte[] bytes, bool bigEndian)
        {
            int units = bytes.Length / 2; // An odd trailing byte is not a code unit; it remains in the stored bytes.
            StringBuilder builder = new StringBuilder(units);
            for (int index = 0; index < units; index++)
            {
                int first = bytes[2 * index], second = bytes[2 * index + 1];
                char unit = bigEndian ? (char)((first << 8) | second) : (char)((second << 8) | first);
                if (unit == '\0') { break; }
                builder.Append(unit); // Lone surrogates are kept verbatim.
            }
            return builder.ToString();
        }

        // Unicode Table 3-7 well-formed sequences; each maximal subpart of an ill-formed sequence becomes one U+FFFD.
        private static string Utf8(byte[] bytes)
        {
            int end = Array.IndexOf(bytes, (byte)0);
            if (end < 0) { end = bytes.Length; }
            StringBuilder builder = new StringBuilder(end);
            int index = 0;
            while (index < end)
            {
                int lead = bytes[index];
                if (lead < 0x80) { builder.Append((char)lead); index++; continue; }
                int needed, low = 0x80, high = 0xBF, code;
                if (lead >= 0xC2 && lead <= 0xDF) { needed = 1; code = lead & 0x1F; }
                else if (lead >= 0xE0 && lead <= 0xEF)
                {
                    needed = 2; code = lead & 0x0F;
                    if (lead == 0xE0) { low = 0xA0; } else if (lead == 0xED) { high = 0x9F; }
                }
                else if (lead >= 0xF0 && lead <= 0xF4)
                {
                    needed = 3; code = lead & 0x07;
                    if (lead == 0xF0) { low = 0x90; } else if (lead == 0xF4) { high = 0x8F; }
                }
                else { builder.Append('\uFFFD'); index++; continue; }
                int position = index + 1;
                bool complete = true;
                for (int part = 0; part < needed; part++)
                {
                    if (position >= end) { complete = false; break; }
                    int next = bytes[position];
                    if (next < low || next > high) { complete = false; break; }
                    code = (code << 6) | (next & 0x3F);
                    low = 0x80;
                    high = 0xBF;
                    position++;
                }
                if (!complete) { builder.Append('\uFFFD'); index = position; continue; }
                if (code >= 0x10000)
                {
                    code -= 0x10000;
                    builder.Append((char)(0xD800 + (code >> 10)));
                    builder.Append((char)(0xDC00 + (code & 0x3FF)));
                }
                else { builder.Append((char)code); }
                index = position;
            }
            return builder.ToString();
        }
    }

    // One VLSD signal-data reader per selected channel and enumeration; the immutable map is cached on the channel layout.
    // for a VLSD CG source the virtual stream is the CG's record bodies (length + bytes) in DG order, found by a
    // private forward frame scan; a decreasing offset rewinds that scan to the DG start (no per-sample index).
    internal sealed class SignalDataReader
    {
        private readonly ReaderLimits limits;
        private readonly ChannelLayout layout;
        private readonly Mdf4DataCursor cursor;
        private readonly byte[] prefix = new byte[4];
        private readonly InterleavedCursor records;
        private readonly int member;
        private ulong virtualEnd;
        private bool hit;
        private ulong hitOffset;
        private ulong hitBody;
        private uint hitLength;

        internal SignalDataReader(ReaderLimits limits, ChannelLayout layout, Mdf4DataCursor cursor)
        {
            this.limits = limits;
            this.layout = layout;
            this.cursor = cursor;
        }

        internal SignalDataReader(ReaderLimits limits, ChannelLayout layout, Mdf4DataCursor cursor, InterleavedCursor records, int member)
            : this(limits, layout, cursor)
        {
            this.records = records;
            this.member = member;
        }

        // Bounds-checks offset, offset+4 and offset+4+length in UInt64 against the logical total before any allocation.
        internal uint Length(ulong offset)
        {
            if (records != null) { return GroupLength(offset); }
            Mdf4DataMap map = layout.SignalData;
            if (map == null) { throw Error("A valid VLSD value has no signal data."); }
            if (offset > map.Total || map.Total - offset < 4) { throw Error("A VLSD offset lies outside its signal data stream."); }
            cursor.Read(offset, prefix, 4);
            uint length = EndianBitConverter.ReadUInt32(prefix, 0, ByteOrder.LittleEndian);
            if (length > map.Total - offset - 4) { throw Error("A VLSD entry extends beyond its signal data stream."); }
            if (length > (ulong)limits.MaximumReadBufferBytes)
            {
                throw new MdfResourceLimitException(nameof(MdfReaderOptions.MaximumReadBufferBytes), (ulong)limits.MaximumReadBufferBytes,
                    length, layout.SignalDataAddress);
            }
            return length;
        }

        internal byte[] Payload(ulong offset, uint length)
        {
            if (records != null && (!hit || offset != hitOffset || length != hitLength)) { length = GroupLength(offset); }
            byte[] stored = new byte[(int)length]; // Bounded by the positive Int32 read-buffer cap in Length.
            if (length != 0) { cursor.Read((records != null ? hitBody : offset) + 4, stored, stored.Length); }
            return stored;
        }

        private uint GroupLength(ulong offset)
        {
            if (hit && offset == hitOffset) { return hitLength; }
            if (offset < virtualEnd)
            {
                records.Restart();
                virtualEnd = 0;
            }
            hit = false;
            ulong body;
            while (true)
            {
                int found;
                ulong size;
                if (!records.NextFrame(out found, out body, out size)) { throw Error("A VLSD offset lies outside its VLSD channel-group records."); }
                if (found != member) { continue; }
                ulong start = virtualEnd;
                virtualEnd += size; // Bounded by the logical DG stream length.
                if (start == offset) { break; }
                if (offset < virtualEnd) { throw Error("A VLSD offset does not address the start of a VLSD channel-group record."); }
            }
            uint length = records.LastLength;
            if (length > (ulong)limits.MaximumReadBufferBytes)
            {
                throw new MdfResourceLimitException(nameof(MdfReaderOptions.MaximumReadBufferBytes), (ulong)limits.MaximumReadBufferBytes,
                    length, layout.SignalDataAddress);
            }
            hit = true;
            hitOffset = offset;
            hitLength = length;
            hitBody = body; // Logical DG-stream address of the record body (its UInt32 length prefix).
            return length;
        }

        private MdfFormatException Error(string message)
        {
            return new MdfFormatException(message, "MDF013", layout.SignalDataAddress, layout.SignalDataAddress == 0 ? "##CN" : null);
        }
    }
}
