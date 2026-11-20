// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using MdfLib.Reading;

namespace MdfLib
{
    /// <summary>An immutable writer conversion beyond identity/linear (see <see cref="MdfChannelDefinition.Conversion"/>).</summary>
    /// <remarks>Parameters and texts use the layout of <see cref="MdfChannelConversion.Parameters"/> and
    /// <see cref="MdfChannelConversion.Texts"/>, so a written conversion reads back with the same kind and values.
    /// Factories validate the shape (counts, finite values, key/range order, formula grammar); the writer checks what
    /// the selected format revision can store. Samples are always written raw.</remarks>
    internal sealed class MdfConversionDefinition
    {
        private static readonly ReadOnlyCollection<double> NoValues = Array.AsReadOnly(new double[0]);
        private static readonly ReadOnlyCollection<string> NoTexts = Array.AsReadOnly(new string[0]);
        private static readonly ReadOnlyCollection<MdfCommonProperty> NoTags = Array.AsReadOnly(new MdfCommonProperty[0]);

        private MdfConversionDefinition(MdfConversionKind kind, double[] parameters, string[] texts, string formula)
        {
            Kind = kind;
            Parameters = parameters == null ? NoValues : Array.AsReadOnly(parameters);
            Texts = texts == null ? NoTexts : Array.AsReadOnly(texts);
            Formula = formula;
        }

        /// <summary>Gets the conversion kind.</summary>
        public MdfConversionKind Kind { get; }
        /// <summary>Gets the numeric parameters in wire order (the <see cref="MdfChannelConversion.Parameters"/> layout).</summary>
        public IReadOnlyList<double> Parameters { get; }
        /// <summary>Gets the texts in wire order, null for a NIL reference (the <see cref="MdfChannelConversion.Texts"/> layout).</summary>
        public IReadOnlyList<string> Texts { get; }
        /// <summary>Gets the algebraic formula; null for other kinds.</summary>
        public string Formula { get; }

        /// <summary>Gets or sets cc_precision (decimal places); null leaves it unused (cc_flags bit 0x0001).</summary>
        public byte? Precision { get; set; }

        /// <summary>Gets or sets the physical range minimum; with <see cref="RangeMax"/>, both or neither (cc_flags bit 0x0002).</summary>
        public double? RangeMin { get; set; }

        /// <summary>Gets or sets the physical range maximum, with <see cref="RangeMin"/>.</summary>
        public double? RangeMax { get; set; }

        /// <summary>Gets or sets cc_flags bits the caller sets; the validity bits follow from the precision and the range.</summary>
        public ushort Flags { get; set; }

        /// <summary>Gets or sets the inverse conversion (cc_inverse) that maps physical values back to raw ones, or null.
        /// The managed writer stores it for numeric conversions without texts, names or units of their own.</summary>
        public MdfConversionDefinition Inverse { get; set; }
        /// <summary>Gets the conversion's name (cc_tx_name, MDF 4 only); empty when none.</summary>
        public string Name { get; private set; } = string.Empty;
        /// <summary>Gets the conversion's unit (cc_md_unit, MDF 4 only); empty when none.</summary>
        public string Unit { get; private set; } = string.Empty;
        /// <summary>Gets the conversion's comment (cc_md_comment, MDF 4 only); empty when none.</summary>
        public string Comment { get; private set; } = string.Empty;
        /// <summary>Gets the common properties of the conversion's MD comment block (MDF 4 cc_md_comment); empty when
        /// none. MDF 3 blocks hold none, so a writer refuses them for MDF 3.</summary>
        public IReadOnlyList<MdfCommonProperty> CommonProperties { get; private set; } = NoTags;

        /// <summary>Returns a copy of this conversion with its name, unit and comment. MDF 3 blocks hold none of them,
        /// so a writer refuses them for MDF 3.</summary>
        public MdfConversionDefinition WithTexts(string name, string unit, string comment)
        {
            CheckText(name, nameof(name));
            CheckText(unit, nameof(unit));
            CheckText(comment, nameof(comment));
            MdfConversionDefinition copy = (MdfConversionDefinition)MemberwiseClone();
            copy.Name = name ?? string.Empty;
            copy.Unit = unit ?? string.Empty;
            copy.Comment = comment ?? string.Empty;
            return copy;
        }

        /// <summary>Returns a copy of this conversion with the common properties of its MD comment block. The tags are
        /// copied; null or an empty list writes no MD block for them.</summary>
        public MdfConversionDefinition WithCommonProperties(IReadOnlyList<MdfCommonProperty> tags)
        {
            List<MdfCommonProperty> copied = new List<MdfCommonProperty>();
            if (tags != null)
            {
                foreach (MdfCommonProperty tag in tags)
                {
                    if (tag == null) { throw new ArgumentException("A common property must not be null.", nameof(tags)); }
                    copied.Add(new MdfCommonProperty { Name = tag.Name, Type = tag.Type, Value = tag.Value });
                }
            }
            MdfConversionDefinition copy = (MdfConversionDefinition)MemberwiseClone();
            copy.CommonProperties = copied.Count == 0 ? NoTags : copied.AsReadOnly();
            return copy;
        }

        /// <summary>Creates the MDF 4 linear conversion (cc_type 1): physical = Offset + Factor * raw. It is written as a
        /// conversion block only when it carries a precision, a range, flags or an inverse; a plain linear conversion stays
        /// a pair of channel fields.</summary>
        public static MdfConversionDefinition Linear(double offset, double factor)
        {
            if (double.IsNaN(offset) || double.IsInfinity(offset) || double.IsNaN(factor) || double.IsInfinity(factor))
            { throw new ArgumentException("Linear coefficients must be finite.", nameof(offset)); }
            return new MdfConversionDefinition(MdfConversionKind.Linear, new[] { offset, factor }, null, null);
        }

        /// <summary>Creates (P1*x*x + P2*x + P3) / (P4*x*x + P5*x + P6) (MDF 4 cc_type 2, MDF 3 type 9).</summary>
        public static MdfConversionDefinition Rational(double p1, double p2, double p3, double p4, double p5, double p6)
        {
            return Numeric(MdfConversionKind.Rational, new[] { p1, p2, p3, p4, p5, p6 });
        }

        /// <summary>Creates the MDF 3 polynomial (P2 - P4*t) / (P3*t - P1) with t = x - P5 - P6 (MDF 3 type 6 only).</summary>
        public static MdfConversionDefinition Polynomial(double p1, double p2, double p3, double p4, double p5, double p6)
        {
            return Numeric(MdfConversionKind.Polynomial, new[] { p1, p2, p3, p4, p5, p6 });
        }

        /// <summary>Creates the MDF 3 exponential conversion (type 7 only); exactly one of P1 and P4 must be zero.</summary>
        /// <param name="parameters">The seven parameters P1..P7.</param>
        public static MdfConversionDefinition Exponential(IReadOnlyList<double> parameters)
        {
            return Numeric(MdfConversionKind.Exponential, Copy(parameters, nameof(parameters), 7));
        }

        /// <summary>Creates the MDF 3 logarithmic conversion (type 8 only); exactly one of P1 and P4 must be zero.</summary>
        /// <param name="parameters">The seven parameters P1..P7.</param>
        public static MdfConversionDefinition Logarithmic(IReadOnlyList<double> parameters)
        {
            return Numeric(MdfConversionKind.Logarithmic, Copy(parameters, nameof(parameters), 7));
        }

        /// <summary>Creates an algebraic formula of the raw value X (MDF 4 cc_type 3, MDF 3 type 10).</summary>
        /// <param name="formula">A formula inside the reader's supported grammar (for example "X * 2 + sin(X)").</param>
        public static MdfConversionDefinition Algebraic(string formula)
        {
            if (formula == null) { throw new ArgumentNullException(nameof(formula)); }
            if (formula.IndexOf('\0') >= 0 || AlgebraicFormula.Compile(formula) == null)
            { throw new ArgumentException("The formula is outside the supported algebraic grammar.", nameof(formula)); }
            return new MdfConversionDefinition(MdfConversionKind.Algebraic, null, null, formula);
        }

        /// <summary>Creates a key/value table, interpolated (MDF 4 cc_type 4, MDF 3 type 1) or nearest key (cc_type 5, type 2).</summary>
        /// <param name="keys">Strictly increasing finite raw keys (at least one).</param>
        /// <param name="values">Finite physical values, one per key.</param>
        /// <param name="interpolate">True for linear interpolation between keys.</param>
        public static MdfConversionDefinition Table(IReadOnlyList<double> keys, IReadOnlyList<double> values, bool interpolate)
        {
            double[] k = Copy(keys, nameof(keys), -1), v = Copy(values, nameof(values), k.Length);
            if (k.Length == 0) { throw new ArgumentException("A table needs at least one entry.", nameof(keys)); }
            double[] wire = new double[2 * k.Length];
            for (int i = 0; i < k.Length; i++) { wire[2 * i] = k[i]; wire[2 * i + 1] = v[i]; }
            return Numeric(interpolate ? MdfConversionKind.TableInterpolation : MdfConversionKind.Table, wire);
        }

        /// <summary>Creates a value-range table with a default value (MDF 4 cc_type 6 only).</summary>
        /// <param name="lower">Inclusive lower bounds.</param>
        /// <param name="upper">Upper bounds (inclusive for integer channels); ranges are ordered and nonoverlapping.</param>
        /// <param name="values">Physical values, one per range.</param>
        /// <param name="defaultValue">The value outside all ranges.</param>
        public static MdfConversionDefinition RangeTable(IReadOnlyList<double> lower, IReadOnlyList<double> upper, IReadOnlyList<double> values, double defaultValue)
        {
            double[] lo = Copy(lower, nameof(lower), -1), hi = Copy(upper, nameof(upper), lo.Length), v = Copy(values, nameof(values), lo.Length);
            double[] wire = new double[3 * lo.Length + 1];
            for (int i = 0; i < lo.Length; i++) { wire[3 * i] = lo[i]; wire[3 * i + 1] = hi[i]; wire[3 * i + 2] = v[i]; }
            wire[wire.Length - 1] = defaultValue;
            return Numeric(MdfConversionKind.RangeTable, wire);
        }

        /// <summary>Creates an exact value to text table (MDF 4 cc_type 7, MDF 3 type 11).</summary>
        /// <param name="keys">Strictly increasing finite raw keys.</param>
        /// <param name="texts">The text of each key; null is a NIL reference (empty text).</param>
        /// <param name="defaultText">The text of other values; MDF 3 has no default and requires null or empty.</param>
        public static MdfConversionDefinition ValueToText(IReadOnlyList<double> keys, IReadOnlyList<string> texts, string defaultText)
        {
            double[] k = Copy(keys, nameof(keys), -1);
            string[] t = CopyTexts(texts, nameof(texts), k.Length, false, defaultText, true);
            return Checked(MdfConversionKind.ValueToText, k, t, nameof(keys));
        }

        /// <summary>Creates a value range to text table (MDF 4 cc_type 8, MDF 3 type 12).</summary>
        /// <param name="lower">Inclusive lower bounds.</param>
        /// <param name="upper">Upper bounds (inclusive for integer channels); ranges are ordered and nonoverlapping.</param>
        /// <param name="texts">The text of each range; null is a NIL reference.</param>
        /// <param name="defaultText">The text outside all ranges; null is a NIL reference.</param>
        public static MdfConversionDefinition RangeToText(IReadOnlyList<double> lower, IReadOnlyList<double> upper, IReadOnlyList<string> texts, string defaultText)
        {
            double[] lo = Copy(lower, nameof(lower), -1), hi = Copy(upper, nameof(upper), lo.Length);
            string[] t = CopyTexts(texts, nameof(texts), lo.Length, false, defaultText, true);
            double[] wire = new double[2 * lo.Length];
            for (int i = 0; i < lo.Length; i++) { wire[2 * i] = lo[i]; wire[2 * i + 1] = hi[i]; }
            return Checked(MdfConversionKind.RangeToText, wire, t, nameof(lower));
        }

        /// <summary>Creates a text-channel conversion from exact (ordinal) key texts to values (MDF 4 cc_type 9 only).</summary>
        /// <param name="keys">Nonnull key texts; the first of equal keys wins.</param>
        /// <param name="values">Finite values, one per key.</param>
        /// <param name="defaultValue">The value of other texts.</param>
        public static MdfConversionDefinition TextToValue(IReadOnlyList<string> keys, IReadOnlyList<double> values, double defaultValue)
        {
            string[] k = CopyTexts(keys, nameof(keys), -1, true, null, false);
            double[] v = Copy(values, nameof(values), k.Length);
            double[] wire = new double[v.Length + 1];
            Array.Copy(v, wire, v.Length);
            wire[v.Length] = defaultValue;
            return Checked(MdfConversionKind.TextToValue, wire, k, nameof(values));
        }

        /// <summary>Creates a text-channel conversion from exact key texts to output texts (MDF 4 cc_type 10 only).</summary>
        /// <param name="keys">Nonnull key texts.</param>
        /// <param name="outputs">The output of each key; null keeps the input text.</param>
        /// <param name="defaultText">The output of other texts; null keeps the input text.</param>
        public static MdfConversionDefinition TextToText(IReadOnlyList<string> keys, IReadOnlyList<string> outputs, string defaultText)
        {
            string[] k = CopyTexts(keys, nameof(keys), -1, true, null, false);
            string[] o = CopyTexts(outputs, nameof(outputs), k.Length, false, null, false);
            string[] wire = new string[2 * k.Length + 1];
            for (int i = 0; i < k.Length; i++) { wire[2 * i] = k[i]; wire[2 * i + 1] = o[i]; }
            wire[wire.Length - 1] = defaultText;
            CheckText(defaultText, nameof(defaultText));
            return new MdfConversionDefinition(MdfConversionKind.TextToText, null, wire, null);
        }

        // A reader conversion the writer can represent, or null (identity, linear, unsupported and bitfield conversions).
        // The reader only reports these kinds after validating the same shape rules as the factories.
        internal static MdfConversionDefinition FromConversion(MdfChannelConversion conversion)
        {
            switch (conversion.Kind)
            {
                case MdfConversionKind.Rational:
                case MdfConversionKind.Polynomial:
                case MdfConversionKind.Exponential:
                case MdfConversionKind.Logarithmic:
                case MdfConversionKind.Table:
                case MdfConversionKind.TableInterpolation:
                case MdfConversionKind.RangeTable:
                case MdfConversionKind.ValueToText:
                case MdfConversionKind.RangeToText:
                case MdfConversionKind.TextToValue:
                case MdfConversionKind.TextToText:
                    double[] values = new double[conversion.Parameters.Count];
                    for (int i = 0; i < values.Length; i++) { values[i] = conversion.Parameters[i]; }
                    string[] texts = new string[conversion.Texts.Count];
                    for (int i = 0; i < texts.Length; i++) { texts[i] = conversion.Texts[i]; }
                    return new MdfConversionDefinition(conversion.Kind, values, texts, null);
                case MdfConversionKind.Algebraic:
                    return conversion.Formula == null ? null : new MdfConversionDefinition(conversion.Kind, null, null, conversion.Formula);
                default:
                    return null;
            }
        }

        private static MdfConversionDefinition Numeric(MdfConversionKind kind, double[] wire)
        {
            return Checked(kind, wire, null, "parameters");
        }

        private static MdfConversionDefinition Checked(MdfConversionKind kind, double[] wire, string[] texts, string name)
        {
            if (!PhysicalConversion.Valid(kind, wire))
            { throw new ArgumentException("Conversion values must be finite, with increasing keys and ordered nonoverlapping ranges.", name); }
            return new MdfConversionDefinition(kind, wire, texts, null);
        }

        private static double[] Copy(IReadOnlyList<double> values, string name, int count)
        {
            if (values == null) { throw new ArgumentNullException(name); }
            if (count >= 0 && values.Count != count) { throw new ArgumentException("Expected " + count + " values.", name); }
            if (values.Count > (ushort.MaxValue - 1) / 3) { throw new ArgumentException("Too many conversion entries.", name); }
            double[] result = new double[values.Count];
            for (int i = 0; i < result.Length; i++) { result[i] = values[i]; }
            return result;
        }

        private static string[] CopyTexts(IReadOnlyList<string> texts, string name, int count, bool nonNull, string tail, bool withTail)
        {
            if (texts == null) { throw new ArgumentNullException(name); }
            if (count >= 0 && texts.Count != count) { throw new ArgumentException("Expected " + count + " texts.", name); }
            if (texts.Count > (ushort.MaxValue - 1) / 3) { throw new ArgumentException("Too many conversion entries.", name); }
            string[] result = new string[texts.Count + (withTail ? 1 : 0)];
            for (int i = 0; i < texts.Count; i++)
            {
                if (nonNull && texts[i] == null) { throw new ArgumentException("Key texts cannot be null.", name); }
                CheckText(texts[i], name);
                result[i] = texts[i];
            }
            if (withTail)
            {
                CheckText(tail, "defaultText");
                result[texts.Count] = tail;
            }
            return result;
        }

        private static void CheckText(string text, string name)
        {
            if (text != null && text.IndexOf('\0') >= 0) { throw new ArgumentException("Conversion texts cannot contain NUL.", name); }
        }
    }
}
