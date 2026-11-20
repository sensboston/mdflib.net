// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;

namespace MdfLib.Reading
{
    // binary64 evaluation in a fixed operation order, no allocation per sample.
    internal static class PhysicalConversion
    {
        /// <summary>Raw value (already widened to binary64) to physical; <paramref name="integer"/> selects inclusive range ends.</summary>
        internal static double Evaluate(MdfChannelConversion conversion, double x, bool integer)
        {
            double[] p = conversion.Values;
            switch (conversion.Kind)
            {
                case MdfConversionKind.Identity:
                    return x;
                case MdfConversionKind.Linear:
                    return conversion.Factor.Value * x + conversion.Offset.Value;
                case MdfConversionKind.Rational:
                {
                    double square = x * x;
                    double numerator = p[0] * square + p[1] * x + p[2];
                    double denominator = p[3] * square + p[4] * x + p[5];
                    return numerator / denominator;
                }
                case MdfConversionKind.Polynomial:
                {
                    double t = x - p[4] - p[5];
                    return (p[1] - p[3] * t) / (p[2] * t - p[0]);
                }
                case MdfConversionKind.TableInterpolation:
                case MdfConversionKind.Table:
                    return Lookup(p, x, conversion.Kind == MdfConversionKind.TableInterpolation);
                case MdfConversionKind.RangeTable:
                    return Range(p, x, integer);
                case MdfConversionKind.Algebraic:
                    return conversion.CompiledFormula.Evaluate(x);
                case MdfConversionKind.Exponential:
                case MdfConversionKind.Logarithmic:
                {
                    // P1..P7 = p[0]..p[6]; Valid guarantees exactly one of P1/P4 is zero.
                    bool exponential = conversion.Kind == MdfConversionKind.Exponential;
                    if (p[3] == 0)
                    {
                        double inner = ((x - p[6]) * p[5] - p[2]) / p[0];
                        return (exponential ? Math.Exp(inner) : Math.Log(inner)) / p[1];
                    }
                    double other = (p[2] / (x - p[6]) - p[5]) / p[3];
                    return (exponential ? Math.Exp(other) : Math.Log(other)) / p[4];
                }
                default:
                    throw new NotSupportedException("The channel conversion is not supported.");
            }
        }

        private static double Lookup(double[] p, double x, bool interpolate)
        {
            if (double.IsNaN(x)) { return double.NaN; }
            int n = p.Length / 2;
            if (x <= p[0]) { return p[1]; }
            if (x >= p[2 * (n - 1)]) { return p[2 * n - 1]; }
            // Invariant: key[low] <= x < key[high]; keys are strictly increasing (validated).
            int low = 0, high = n - 1;
            while (high - low > 1)
            {
                int middle = low + (high - low) / 2;
                if (p[2 * middle] <= x) { low = middle; } else { high = middle; }
            }
            double k0 = p[2 * low], v0 = p[2 * low + 1], k1 = p[2 * high], v1 = p[2 * high + 1];
            if (x == k0) { return v0; }
            if (!interpolate) { return x - k0 <= k1 - x ? v0 : v1; }
            double slope = (v1 - v0) / (k1 - k0);
            return slope * (x - k0) + v0;
        }

        private static double Range(double[] p, double x, bool integer)
        {
            int n = p.Length / 3;
            int hit = RangeIndex(p, 3, n, x, integer);
            return hit < n ? p[3 * hit + 2] : p[p.Length - 1];
        }

        // Upper bounds are non-decreasing (validated): the first candidate is the smallest i with upper_i >= x (integer,
        // inclusive) or upper_i > x (floating point, half-open); later ranges start after it. Returns n for no range/NaN.
        private static int RangeIndex(double[] p, int stride, int n, double x, bool integer)
        {
            if (double.IsNaN(x)) { return n; }
            int low = 0, high = n;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                double upper = p[stride * middle + 1];
                if (integer ? upper >= x : upper > x) { high = middle; } else { low = middle + 1; }
            }
            return low < n && p[stride * low] <= x ? low : n;
        }

        // Strictly increasing keys (validated); NaN never matches; returns n when no key equals x.
        private static int KeyIndex(double[] keys, double x)
        {
            int low = 0, high = keys.Length - 1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2;
                if (keys[middle] == x) { return middle; }
                if (keys[middle] < x) { low = middle + 1; } else { high = middle - 1; }
            }
            return keys.Length;
        }

        internal static bool HasTextOutput(MdfConversionKind kind)
        {
            return kind == MdfConversionKind.ValueToText || kind == MdfConversionKind.RangeToText || kind == MdfConversionKind.TextToText
                || kind == MdfConversionKind.BitfieldToText;
        }

        internal static bool TakesText(MdfConversionKind kind)
        {
            return kind == MdfConversionKind.TextToValue || kind == MdfConversionKind.TextToText;
        }

        /// <summary>value/range to text: the selected reference as Text (NIL = empty), validity unchanged.</summary>
        internal static MdfSampleValue ToText(MdfChannelConversion conversion, double x, bool integer, bool valid)
        {
            double[] p = conversion.Values;
            int hit = conversion.Kind == MdfConversionKind.ValueToText ? KeyIndex(p, x) : RangeIndex(p, 2, p.Length / 2, x, integer);
            return Reference(conversion, hit, valid);
        }

        /// <summary>text to value/text from a decoded Text input; a NIL text-to-text output keeps the input value.</summary>
        internal static MdfSampleValue FromText(MdfChannelConversion conversion, MdfSampleValue input)
        {
            if (conversion.Kind == MdfConversionKind.Identity) { return input; }
            int key;
            bool found = conversion.KeyIndex.TryGetValue(input.TextValue, out key);
            if (conversion.Kind == MdfConversionKind.TextToValue)
            {
                double[] p = conversion.Values;
                return new MdfSampleValue(MdfValueKind.FloatingPoint, input.IsValid, 0, found ? p[key] : p[p.Length - 1]);
            }
            int output = found ? key + 1 : conversion.References.Length - 1;
            return conversion.References[output] == null ? input : Reference(conversion, output, input.IsValid);
        }

        /// <summary>
        /// bitfield to text over the 64-bit two's complement raw pattern: part i converts (bits &amp; mask_i) as an
        /// integer with its value/range to text conversion; non-empty texts give "name=text" (or "text" when unnamed),
        /// empty texts give "name=" (or nothing); parts are joined with '|'. Validity is unchanged.
        /// </summary>
        internal static MdfSampleValue Bitfield(MdfChannelConversion conversion, ulong bits, bool valid)
        {
            MdfChannelConversion[] parts = conversion.Parts;
            ulong[] masks = conversion.Masks;
            byte[][] names = conversion.NameBytes;
            int length = 0, written = 0;
            byte[][] selected = new byte[parts.Length][];
            for (int i = 0; i < parts.Length; i++)
            {
                MdfChannelConversion part = parts[i];
                double x = bits & masks[i];
                int hit = part.Kind == MdfConversionKind.ValueToText ? KeyIndex(part.Values, x) : RangeIndex(part.Values, 2, part.Values.Length / 2, x, true);
                byte[] text = part.ReferenceBytes[hit] ?? NoBytes;
                if (text.Length == 0 && names[i] == null) { continue; }
                selected[i] = text;
                length += (written++ > 0 ? 1 : 0) + (names[i] == null ? 0 : names[i].Length) + text.Length;
            }
            byte[] output = length == 0 ? NoBytes : new byte[length];
            int position = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                if (selected[i] == null) { continue; }
                if (position > 0) { output[position++] = (byte)'|'; }
                if (names[i] != null) { Buffer.BlockCopy(names[i], 0, output, position, names[i].Length); position += names[i].Length; }
                Buffer.BlockCopy(selected[i], 0, output, position, selected[i].Length);
                position += selected[i].Length;
            }
            return new MdfSampleValue(valid, length == 0 ? string.Empty : Utf8.GetString(output), output);
        }

        private static readonly System.Text.Encoding Utf8 = new System.Text.UTF8Encoding(false, false);

        private static MdfSampleValue Reference(MdfChannelConversion conversion, int index, bool valid)
        {
            string text = conversion.References[index];
            return text == null ? new MdfSampleValue(valid, string.Empty, NoBytes) : new MdfSampleValue(valid, text, conversion.ReferenceBytes[index]);
        }

        private static readonly byte[] NoBytes = new byte[0];

        /// <summary>
        /// Semantic validity of wire parameters (structure/counts are checked by the codecs): finite values, strictly
        /// increasing table keys, ordered non-overlapping ranges. Invalid parameters make the conversion Unsupported.
        /// </summary>
        internal static bool Valid(MdfConversionKind kind, double[] p)
        {
            for (int i = 0; i < p.Length; i++)
            {
                if (double.IsNaN(p[i]) || double.IsInfinity(p[i])) { return false; }
            }
            if (kind == MdfConversionKind.Table || kind == MdfConversionKind.TableInterpolation)
            {
                for (int i = 2; i < p.Length; i += 2)
                {
                    if (!(p[i - 2] < p[i])) { return false; }
                }
            }
            else if (kind == MdfConversionKind.RangeTable || kind == MdfConversionKind.RangeToText)
            {
                int stride = kind == MdfConversionKind.RangeTable ? 3 : 2;
                int n = p.Length / stride;
                for (int i = 0; i < n; i++)
                {
                    if (p[stride * i] > p[stride * i + 1]) { return false; }
                    if (i > 0 && p[stride * (i - 1) + 1] > p[stride * i]) { return false; }
                }
            }
            else if (kind == MdfConversionKind.Exponential || kind == MdfConversionKind.Logarithmic)
            {
                return p.Length == 7 && ((p[3] == 0 && p[0] != 0) || (p[0] == 0 && p[3] != 0));
            }
            else if (kind == MdfConversionKind.ValueToText)
            {
                for (int i = 1; i < p.Length; i++)
                {
                    if (!(p[i - 1] < p[i])) { return false; }
                }
            }
            return true;
        }
    }
}
