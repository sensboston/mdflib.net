// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// CC wire layouts from the ASAM MDF 3.30/4.x tables as implemented by pinned MIT upstream cc3block/cc4block; the
// format-capability policy is new.
using System;
using System.Text;

namespace MdfLib.Writing
{
    // A validated MdfConversionDefinition bound to one output revision: wire type, values and encoded texts.
    internal sealed class WriterConversion
    {
        internal MdfConversionKind Kind;
        internal int WireType; // MDF 4 cc_type or MDF 3 conversion type.
        internal double[] Values;
        internal byte[][] Texts; // NUL-terminated; null = NIL. MDF 4 cc_ref order; MDF 3 TABX/RTABX/formula text.
        internal long[] TextAddresses;
        // MDF 4 cc_tx_name (link 0), cc_md_unit (link 1) and cc_md_comment (link 2); null = none.
        internal byte[] Name, Unit, Comment;
        internal long NameAddress, UnitAddress, CommentAddress;
        // field writes: cc_precision, the caller's cc_flags bits, cc_phy_range_min/max, and cc_inverse (a block of its own).
        internal bool HasPrecision;
        internal byte Precision;
        internal ushort Flags;
        internal double[] Range;
        internal WriterConversion Inverse;
        internal long InverseAddress;

        internal static WriterConversion Create(MdfConversionDefinition definition, MdfValueKind channelKind, bool master, WriterSettings settings, out ulong charge)
        {
            MdfConversionKind kind = definition.Kind;
            bool textChannel = channelKind == MdfValueKind.Text;
            bool takesText = kind == MdfConversionKind.TextToValue || kind == MdfConversionKind.TextToText;
            bool numericChannel = channelKind == MdfValueKind.UnsignedInteger || channelKind == MdfValueKind.SignedInteger || channelKind == MdfValueKind.FloatingPoint;
            if (takesText ? !textChannel : !numericChannel)
            {
                throw new NotSupportedException(takesText ? "Text to value/text conversions require a text channel."
                    : "Only numeric channels can carry a numeric or value-to-text conversion.");
            }
            if (master && (kind == MdfConversionKind.ValueToText || kind == MdfConversionKind.RangeToText))
            { throw new ArgumentException("A stored master cannot have a text conversion.", nameof(definition)); }
            int type = settings.Mdf3 ? Mdf3Type(kind) : Mdf4Type(kind);
            if (type < 0)
            { throw new NotSupportedException("MDF " + settings.Version + " cannot store a " + kind + " conversion."); }
            WriterConversion result = new WriterConversion { Kind = kind, WireType = type };
            int count = definition.Parameters.Count;
            result.Values = new double[count];
            for (int i = 0; i < count; i++) { result.Values[i] = definition.Parameters[i]; }
            result.HasPrecision = definition.Precision.HasValue;
            result.Precision = definition.Precision ?? 0;
            result.Flags = definition.Flags;
            if (definition.RangeMin.HasValue != definition.RangeMax.HasValue)
            { throw new ArgumentException("RangeMin and RangeMax are set together or not at all.", nameof(definition)); }
            if (definition.RangeMin.HasValue)
            {
                if (double.IsNaN(definition.RangeMin.Value) || double.IsInfinity(definition.RangeMin.Value)
                    || double.IsNaN(definition.RangeMax.Value) || double.IsInfinity(definition.RangeMax.Value))
                { throw new ArgumentException("The conversion range must be finite.", nameof(definition)); }
                result.Range = new[] { definition.RangeMin.Value, definition.RangeMax.Value };
            }
            ulong innerCharge = 0;
            if (definition.Inverse != null)
            {
                if (settings.Mdf3) { throw new NotSupportedException("MDF 3.30 has no inverse conversion block in the managed writer."); }
                if (definition.Inverse.Inverse != null) { throw new NotSupportedException("An inverse of an inverse conversion is not written by the managed writer."); }
                result.Inverse = Create(definition.Inverse, channelKind, false, settings, out innerCharge);
                if (result.Inverse.Texts.Length != 0)
                { throw new NotSupportedException("An inverse conversion with texts (a formula, value-to-text or text table) is not written by the managed writer."); }
                if (result.Inverse.Name != null || result.Inverse.Unit != null || result.Inverse.Comment != null)
                { throw new NotSupportedException("An inverse conversion's name, unit and comment are not written by the managed writer."); }
            }
            if (settings.Mdf3 && (result.HasPrecision || result.Range != null || result.Flags != 0))
            { throw new NotSupportedException("MDF 3.30 conversions cannot store precision, a range or flags in the managed writer."); }
            Encoding encoding = settings.TextEncoding;
            if (kind == MdfConversionKind.Algebraic)
            {
                result.Texts = new[] { Encode(definition.Formula, encoding, settings) };
                if (settings.Mdf3 && result.Texts[0].Length > 256) { throw new NotSupportedException("MDF 3 formulas hold at most 255 Latin-1 bytes."); }
            }
            else
            {
                result.Texts = new byte[definition.Texts.Count][];
                for (int i = 0; i < result.Texts.Length; i++)
                {
                    string text = definition.Texts[i];
                    result.Texts[i] = text == null ? null : Encode(text, encoding, settings);
                }
            }
            if (settings.Mdf3 && kind == MdfConversionKind.ValueToText)
            {
                byte[] last = result.Texts[result.Texts.Length - 1];
                if (last != null && last.Length > 1) { throw new NotSupportedException("MDF 3 value-to-text tables have no default text."); }
                for (int i = 0; i + 1 < result.Texts.Length; i++)
                {
                    if (result.Texts[i] != null && result.Texts[i].Length > 32)
                    { throw new NotSupportedException("MDF 3 value-to-text entries hold at most 31 Latin-1 bytes."); }
                }
            }
            if (settings.Mdf3 && kind == MdfConversionKind.RangeToText && result.Texts.Length > ushort.MaxValue)
            { throw new NotSupportedException("Too many MDF 3 range-to-text entries."); }
            result.TextAddresses = new long[result.Texts.Length];
            if (settings.Mdf3 && (definition.Name.Length != 0 || definition.Unit.Length != 0 || definition.Comment.Length != 0))
            { throw new NotSupportedException("MDF 3 conversion blocks hold no name, unit or comment in the managed writer."); }
            bool tagged = WriterMetadata.HasTags(definition.CommonProperties);
            if (settings.Mdf3 && tagged) { throw new NotSupportedException("MDF 3 conversion blocks hold no common properties in the managed writer."); }
            result.Name = definition.Name.Length == 0 ? null : Encode(definition.Name, encoding, settings);
            result.Unit = definition.Unit.Length == 0 ? null : Encode(definition.Unit, encoding, settings);
            if (tagged)
            {
                // The comment is an MD block of its own: the TX text and the tags in one XML root.
                int commentBytes;
                result.Comment = WriterMetadata.Comment("CCcomment", definition.Comment, definition.CommonProperties, settings, out commentBytes);
            }
            else
            {
                result.Comment = definition.Comment.Length == 0 ? null : Encode(definition.Comment, encoding, settings);
            }
            charge = 256UL + 16UL * (ulong)count + (result.Inverse == null ? 0UL : innerCharge + 256UL);
            foreach (byte[] text in result.Texts) { charge += 64UL + (text == null ? 0UL : (ulong)text.Length); }
            foreach (byte[] text in new[] { result.Name, result.Unit, result.Comment }) { charge += 64UL + (text == null ? 0UL : (ulong)text.Length); }
            return result;
        }

        // An MDF 4 conversion text is not stored for the inverse, so only the numeric shape is checked here.
        private static byte[] Encode(string text, Encoding encoding, WriterSettings settings)
        {
            int size;
            try { size = checked(encoding.GetByteCount(text) + 1); }
            catch (EncoderFallbackException error)
            { throw new ArgumentException("Conversion text is not representable in the selected strict encoding.", nameof(text), error); }
            WriterSettings.Limit(nameof(MdfWriterOptions.MaximumTextBytes), (ulong)settings.MaximumText, (ulong)size);
            if (settings.Mdf3 && size > 65531) { throw new NotSupportedException("The MDF 3 TX block cannot exceed UInt16 length."); }
            byte[] bytes = new byte[size];
            encoding.GetBytes(text, 0, text.Length, bytes, 0);
            return bytes;
        }

        private static int Mdf4Type(MdfConversionKind kind)
        {
            switch (kind)
            {
                case MdfConversionKind.Linear: return 1;
                case MdfConversionKind.Rational: return 2;
                case MdfConversionKind.Algebraic: return 3;
                case MdfConversionKind.TableInterpolation: return 4;
                case MdfConversionKind.Table: return 5;
                case MdfConversionKind.RangeTable: return 6;
                case MdfConversionKind.ValueToText: return 7;
                case MdfConversionKind.RangeToText: return 8;
                case MdfConversionKind.TextToValue: return 9;
                case MdfConversionKind.TextToText: return 10;
                default: return -1;
            }
        }

        private static int Mdf3Type(MdfConversionKind kind)
        {
            switch (kind)
            {
                case MdfConversionKind.TableInterpolation: return 1;
                case MdfConversionKind.Table: return 2;
                case MdfConversionKind.Polynomial: return 6;
                case MdfConversionKind.Exponential: return 7;
                case MdfConversionKind.Logarithmic: return 8;
                case MdfConversionKind.Rational: return 9;
                case MdfConversionKind.Algebraic: return 10;
                case MdfConversionKind.ValueToText: return 11;
                case MdfConversionKind.RangeToText: return 12;
                default: return -1;
            }
        }

        // MDF 4: header, 4 + refs links, 32 fixed data bytes, values. Algebraic has one reference: the formula.
        internal int Mdf4Size { get { return 24 + 8 * (4 + Texts.Length) + 32 + 8 * Values.Length; } }

        // MDF 3: 46 fixed bytes, then parameters, CHAR[256] formula, TABX (double, CHAR[32]) or RTABX (lower, upper, TX link).
        internal int Mdf3Size
        {
            get
            {
                switch (Kind)
                {
                    case MdfConversionKind.Algebraic: return 46 + 256;
                    case MdfConversionKind.ValueToText: return 46 + 40 * Values.Length;
                    case MdfConversionKind.RangeToText: return 46 + 20 * Texts.Length;
                    default: return 46 + 8 * Values.Length;
                }
            }
        }

        // MDF 3 parameter/entry count field.
        internal int Mdf3Count
        {
            get
            {
                switch (Kind)
                {
                    case MdfConversionKind.Algebraic: return 0;
                    case MdfConversionKind.Table:
                    case MdfConversionKind.TableInterpolation: return Values.Length / 2;
                    case MdfConversionKind.RangeToText: return Texts.Length;
                    default: return Values.Length;
                }
            }
        }

        // Texts stored in separate TX blocks: all MDF 4 references, MDF 3 range-to-text texts only.
        internal bool SeparateTexts(bool mdf3)
        {
            return !mdf3 || Kind == MdfConversionKind.RangeToText;
        }
    }
}
