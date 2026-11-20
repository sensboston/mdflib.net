// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;

namespace MdfLib.Writing
{
    internal static class ScalarEncoder
    {
        internal static void Write(byte[] record, WriteChannel channel, MdfSampleValue sample)
        {
            Write(record, channel, sample, channel.DataOffset);
        }

        // `offset` is the byte offset of the field (an array element) in the record.
        internal static void Write(byte[] record, WriteChannel channel, MdfSampleValue sample, int offset)
        {
            if (sample.Kind != channel.Kind) { throw new ArgumentException("A raw sample has the wrong exact kind.", nameof(sample)); }
            if (channel.Kind == MdfValueKind.Text || channel.Kind == MdfValueKind.ByteArray) { Field(record, channel, sample, offset); return; }
            if (channel.Kind == MdfValueKind.Complex)
            {
                int half = channel.Bits / 2;
                Put(record, offset, channel.Big, half, Ieee(sample.RealPart, half));
                Put(record, offset + half / 8, channel.Big, half, Ieee(sample.ImaginaryPart, half));
                return;
            }
            ulong bits;
            if (channel.Kind == MdfValueKind.UnsignedInteger)
            {
                bits = sample.UnsignedValue;
                if (channel.Bits < 64 && bits >= (1UL << channel.Bits))
                { throw new ArgumentOutOfRangeException(nameof(sample), "Unsigned value does not fit the configured width."); }
            }
            else if (channel.Kind == MdfValueKind.SignedInteger)
            {
                long value = sample.SignedValue;
                if (channel.Bits < 64)
                {
                    long limit = 1L << (channel.Bits - 1);
                    if (value < -limit || value >= limit)
                    { throw new ArgumentOutOfRangeException(nameof(sample), "Signed value does not fit the configured width."); }
                }
                bits = unchecked((ulong)value);
                if (channel.Bits < 64) { bits &= (1UL << channel.Bits) - 1; }
            }
            else
            {
                double value = sample.FloatingPointValue;
                if (channel.Bits == 32)
                {
                    if (!double.IsInfinity(value) && (value > float.MaxValue || value < -float.MaxValue))
                    { throw new ArgumentOutOfRangeException(nameof(sample), "Finite value overflows IEEE single precision."); }
                    bits = double.IsNaN(value) ? 0x7fc00000UL : BitConverter.ToUInt32(BitConverter.GetBytes((float)value), 0);
                }
                else { bits = double.IsNaN(value) ? 0x7ff8000000000000UL : unchecked((ulong)BitConverter.DoubleToInt64Bits(value)); }
            }
            Put(record, offset, channel.Big, channel.Bits, bits);
        }

        private static void Put(byte[] record, int offset, bool big, int width, ulong bits)
        {
            int count = (width + 7) / 8;
            for (int i = 0; i < count; i++)
            { record[offset + (big ? count - 1 - i : i)] = (byte)(bits >> (8 * i)); }
        }

        // complex parts follow the same IEEE rules as FloatingPoint (canonical quiet NaN, binary32 range check).
        private static ulong Ieee(double value, int width)
        {
            if (width == 32)
            {
                if (!double.IsInfinity(value) && !double.IsNaN(value) && (value > float.MaxValue || value < -float.MaxValue))
                { throw new ArgumentOutOfRangeException("sample", "Finite value overflows IEEE single precision."); }
                return double.IsNaN(value) ? 0x7fc00000UL : BitConverter.ToUInt32(BitConverter.GetBytes((float)value), 0);
            }
            return double.IsNaN(value) ? 0x7ff8000000000000UL : unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
        }

        // the bytes of one VLSD record (without its UInt32 length): strict text without NUL or a byte array of any length.
        internal static byte[] Variable(WriteChannel channel, MdfSampleValue sample, int maximumBytes)
        {
            if (sample.Kind != channel.Kind) { throw new ArgumentException("A raw sample has the wrong exact kind.", nameof(sample)); }
            byte[] encoded;
            if (channel.Kind == MdfValueKind.ByteArray) { encoded = sample.StoredBytes; }
            else
            {
                string text = sample.TextValue;
                if (text.IndexOf('\0') >= 0) { throw new ArgumentException("A text sample cannot contain NUL.", nameof(sample)); }
                try { encoded = channel.TextEncoding.GetBytes(text); }
                catch (System.Text.EncoderFallbackException error)
                { throw new ArgumentException("The text is not representable in the output encoding.", nameof(sample), error); }
            }
            WriterSettings.Limit(nameof(MdfWriterOptions.MaximumTextBytes), (ulong)maximumBytes, (ulong)encoded.Length);
            return encoded;
        }

        // text is re-encoded strictly in the output encoding and NUL-padded; it must fit and contain no NUL.
        // A byte array must have exactly the field width (padding would change the value).
        private static void Field(byte[] record, WriteChannel channel, MdfSampleValue sample, int offset)
        {
            int width = channel.Bits / 8;
            if (channel.Kind == MdfValueKind.ByteArray)
            {
                byte[] stored = sample.StoredBytes;
                if (stored.Length != width) { throw new ArgumentException("A byte-array sample must have exactly the field width.", nameof(sample)); }
                Buffer.BlockCopy(stored, 0, record, offset, width);
                return;
            }
            string text = sample.TextValue;
            if (text.IndexOf('\0') >= 0) { throw new ArgumentException("A text sample cannot contain NUL.", nameof(sample)); }
            byte[] encoded;
            try { encoded = channel.TextEncoding.GetBytes(text); }
            catch (System.Text.EncoderFallbackException error)
            { throw new ArgumentException("The text is not representable in the output encoding.", nameof(sample), error); }
            if (encoded.Length > width) { throw new ArgumentOutOfRangeException(nameof(sample), "The encoded text does not fit the field."); }
            Buffer.BlockCopy(encoded, 0, record, offset, encoded.Length); // The record was cleared: NUL padding.
        }
    }
}
