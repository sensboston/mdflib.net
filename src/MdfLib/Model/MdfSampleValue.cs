// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;

namespace MdfLib
{
    /// <summary>An exact-kind numeric, complex, text or byte-array value with independent validity. Wrong-kind getters throw.</summary>
    /// <remarks>The default value is unsigned zero with IsValid=false.</remarks>
    internal struct MdfSampleValue
    {
        private readonly ulong integer;
        private readonly double floatingPoint;
        private readonly object payload; // byte[] for ByteArray, TextPayload for Text; never exposed directly.

        internal MdfSampleValue(MdfValueKind kind, bool isValid, ulong integer, double floatingPoint)
        {
            Kind = kind;
            IsValid = isValid;
            this.integer = integer;
            this.floatingPoint = floatingPoint;
            payload = null;
        }

        internal MdfSampleValue(bool isValid, string text, byte[] stored)
        {
            Kind = text == null ? MdfValueKind.ByteArray : MdfValueKind.Text;
            IsValid = isValid;
            integer = 0;
            floatingPoint = 0;
            payload = text == null ? (object)stored : new TextPayload(text, stored);
        }

        /// <summary>Gets the exact value kind (numeric, Text or ByteArray).</summary>
        public MdfValueKind Kind { get; }
        /// <summary>Gets validity without discarding or replacing the stored value.</summary>
        public bool IsValid { get; }
        /// <summary>Gets an unsigned value; throws InvalidOperationException for another kind.</summary>
        public ulong UnsignedValue
        {
            get
            {
                Require(MdfValueKind.UnsignedInteger);
                return integer;
            }
        }
        /// <summary>Gets a signed value; throws InvalidOperationException for another kind.</summary>
        public long SignedValue
        {
            get
            {
                Require(MdfValueKind.SignedInteger);
                return unchecked((long)integer);
            }
        }
        /// <summary>Gets a floating-point value; throws InvalidOperationException for another kind.</summary>
        public double FloatingPointValue
        {
            get
            {
                Require(MdfValueKind.FloatingPoint);
                return floatingPoint;
            }
        }

        /// <summary>Gets the real part of a complex value; throws InvalidOperationException for another kind.</summary>
        public double RealPart
        {
            get
            {
                Require(MdfValueKind.Complex);
                return floatingPoint;
            }
        }

        /// <summary>Gets the imaginary part of a complex value; throws InvalidOperationException for another kind.</summary>
        public double ImaginaryPart
        {
            get
            {
                Require(MdfValueKind.Complex);
                return BitConverter.Int64BitsToDouble(unchecked((long)integer));
            }
        }

        // the stored bytes of a Text/ByteArray value without a copy (internal decoders only); null otherwise.
        internal byte[] StoredBytes => Kind == MdfValueKind.Text ? ((TextPayload)payload).Bytes
            : Kind == MdfValueKind.ByteArray ? (byte[])payload : null;

        /// <summary>Gets decoded text up to the first terminator; throws InvalidOperationException for another kind.</summary>
        public string TextValue
        {
            get
            {
                Require(MdfValueKind.Text);
                return ((TextPayload)payload).Text;
            }
        }

        /// <summary>Returns a new copy of the exact stored bytes of a Text or ByteArray value.</summary>
        /// <remarks>Fixed fields include terminator/padding bytes; a VLSD value is exactly its stored payload.</remarks>
        /// <exception cref="InvalidOperationException">The value is numeric.</exception>
        public byte[] GetByteArrayValue()
        {
            byte[] stored = StoredBytes;
            if (stored == null) { throw new InvalidOperationException("Only text and byte-array values have stored bytes."); }
            return (byte[])stored.Clone();
        }

        /// <summary>Creates an exact unsigned raw value without conversion through double.</summary>
        public static MdfSampleValue FromUnsigned(ulong value, bool isValid = true)
        {
            return new MdfSampleValue(MdfValueKind.UnsignedInteger, isValid, value, 0);
        }

        /// <summary>Creates an exact signed raw value without conversion through double.</summary>
        public static MdfSampleValue FromSigned(long value, bool isValid = true)
        {
            return new MdfSampleValue(MdfValueKind.SignedInteger, isValid, unchecked((ulong)value), 0);
        }

        /// <summary>Creates an IEEE raw value with separate validity; NaN is not an invalidity flag.</summary>
        public static MdfSampleValue FromFloatingPoint(double value, bool isValid = true)
        {
            return new MdfSampleValue(MdfValueKind.FloatingPoint, isValid, 0, value);
        }

        /// <summary>Creates a complex value from its IEEE parts with separate validity; NaN parts are not invalidity flags.</summary>
        public static MdfSampleValue FromComplex(double realPart, double imaginaryPart, bool isValid = true)
        {
            return Complex(realPart, imaginaryPart, isValid);
        }

        /// <summary>Creates a text value for writing; its stored bytes are the strict UTF-8 encoding without terminator.</summary>
        /// <remarks>The writer re-encodes the text in the output's channel encoding (MDF 4 UTF-8, MDF 3.30 Latin-1).</remarks>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
        /// <exception cref="ArgumentException">The text contains an unpaired surrogate.</exception>
        public static MdfSampleValue FromText(string value, bool isValid = true)
        {
            if (value == null) { throw new ArgumentNullException(nameof(value)); }
            byte[] stored;
            try { stored = StrictUtf8.GetBytes(value); }
            catch (System.Text.EncoderFallbackException error) { throw new ArgumentException("The text is not well-formed UTF-16.", nameof(value), error); }
            return new MdfSampleValue(isValid, value, stored);
        }

        /// <summary>Creates a byte-array value from a copy of <paramref name="value"/>.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
        public static MdfSampleValue FromByteArray(byte[] value, bool isValid = true)
        {
            if (value == null) { throw new ArgumentNullException(nameof(value)); }
            return new MdfSampleValue(isValid, null, (byte[])value.Clone());
        }

        private static readonly System.Text.UTF8Encoding StrictUtf8 = new System.Text.UTF8Encoding(false, true);

        internal static MdfSampleValue Complex(double realPart, double imaginaryPart, bool isValid)
        {
            return new MdfSampleValue(MdfValueKind.Complex, isValid, unchecked((ulong)BitConverter.DoubleToInt64Bits(imaginaryPart)), realPart);
        }

        private void Require(MdfValueKind kind)
        {
            if (Kind != kind)
            {
                throw new InvalidOperationException("The value has another kind; no implicit conversion is performed.");
            }
        }

        private sealed class TextPayload
        {
            internal TextPayload(string text, byte[] bytes) { Text = text; Bytes = bytes; }
            internal string Text { get; }
            internal byte[] Bytes { get; }
        }
    }
}
