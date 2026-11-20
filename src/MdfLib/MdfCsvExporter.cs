// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Purpose from pinned MIT mdflib mdftocsvtask/csvwriter; the streaming API, quoting and number formatting are original.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace MdfLib
{
    /// <summary>Streams one channel group's samples to RFC 4180 style CSV text.</summary>
    /// <remarks>One row per sample and one column per scalar (array columns expand to "name[i]" per flattened element).
    /// Numbers use the invariant culture; floating-point values use round-trip formatting ("R"), complex values "re+imi",
    /// byte arrays uppercase hexadecimal. Fields containing the delimiter, a quote, CR/LF or edge whitespace are quoted.
    /// The output writer is neither flushed nor disposed. Memory is bounded by one sample batch.</remarks>
    internal static class MdfCsvExporter
    {
        /// <summary>Writes the selected channels (default: every channel readable in the requested representation) and returns the row count.</summary>
        /// <exception cref="ArgumentNullException">A required argument is null.</exception>
        /// <exception cref="ArgumentException">Options are invalid, or no channel can be exported.</exception>
        public static ulong Export(MdfReader reader, MdfChannelGroup group, TextWriter output, IReadOnlyList<int> channelIndices = null,
            MdfCsvOptions options = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (reader == null) { throw new ArgumentNullException(nameof(reader)); }
            if (group == null) { throw new ArgumentNullException(nameof(group)); }
            if (output == null) { throw new ArgumentNullException(nameof(output)); }
            options = options ?? new MdfCsvOptions();
            MdfValueRepresentation representation = options.Representation;
            char delimiter = options.Delimiter;
            string newLine = options.NewLine;
            int batchSize = options.BatchSize;
            bool header = options.IncludeHeader, units = options.IncludeUnits, emptyInvalid = options.EmptyForInvalid;
            if (representation != MdfValueRepresentation.Raw && representation != MdfValueRepresentation.Physical)
            { throw new ArgumentException("Unknown value representation.", nameof(options)); }
            if (delimiter == '"' || delimiter == '\r' || delimiter == '\n')
            { throw new ArgumentException("The delimiter cannot be a quote or line break.", nameof(options)); }
            if (newLine != "\r\n" && newLine != "\n") { throw new ArgumentException("The row terminator must be CRLF or LF.", nameof(options)); }
            if (batchSize <= 0) { throw new ArgumentOutOfRangeException(nameof(options), "BatchSize must be positive."); }
            int[] selection = Select(group, channelIndices, representation);
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<MdfSampleBatch> batches = reader.ReadSamples(group, selection, 0, ulong.MaxValue, batchSize, representation, cancellationToken);
            StringBuilder line = new StringBuilder();
            if (header)
            {
                for (int column = 0; column < selection.Length; column++)
                {
                    MdfChannel channel = group.Channels[selection[column]];
                    string name = channel.Name ?? string.Empty;
                    if (units && !string.IsNullOrEmpty(channel.Unit)) { name += " [" + channel.Unit + "]"; }
                    if (channel.Array == null) { Field(line, column == 0, delimiter, name); }
                    else
                    {
                        for (ulong element = 0; element < channel.Array.ElementCount; element++)
                        { Field(line, column == 0 && element == 0, delimiter, name + "[" + element.ToString(CultureInfo.InvariantCulture) + "]"); }
                    }
                }
                line.Append(newLine);
                output.Write(line.ToString());
            }
            ulong rows = 0;
            foreach (MdfSampleBatch batch in batches)
            {
                for (int row = 0; row < batch.SampleCount; row++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    line.Length = 0;
                    for (int column = 0; column < selection.Length; column++)
                    {
                        MdfChannelArray array = batch.Channels[column].Array;
                        ulong count = array == null ? 1 : array.ElementCount;
                        for (ulong element = 0; element < count; element++)
                        {
                            MdfSampleValue value = batch.GetValue(row, column, element);
                            string text = !value.IsValid && emptyInvalid ? string.Empty : Format(value);
                            Field(line, column == 0 && element == 0, delimiter, text);
                        }
                    }
                    line.Append(newLine);
                    output.Write(line.ToString());
                    rows++;
                }
            }
            return rows;
        }

        /// <summary>Formats one sample value exactly as the exporter writes it, before CSV quoting.</summary>
        public static string Format(MdfSampleValue value)
        {
            switch (value.Kind)
            {
                case MdfValueKind.UnsignedInteger: return value.UnsignedValue.ToString(CultureInfo.InvariantCulture);
                case MdfValueKind.SignedInteger: return value.SignedValue.ToString(CultureInfo.InvariantCulture);
                case MdfValueKind.FloatingPoint: return Number(value.FloatingPointValue);
                case MdfValueKind.Text: return value.TextValue ?? string.Empty;
                case MdfValueKind.Complex:
                    double imaginary = value.ImaginaryPart;
                    bool sign = imaginary < 0 || (imaginary == 0 && double.IsNegativeInfinity(1 / imaginary)) || double.IsNegativeInfinity(imaginary);
                    return Number(value.RealPart) + (sign ? "-" : "+") + Number(Math.Abs(imaginary)) + "i";
                case MdfValueKind.ByteArray:
                    byte[] bytes = value.GetByteArrayValue();
                    StringBuilder hex = new StringBuilder(bytes.Length * 2);
                    foreach (byte b in bytes) { hex.Append(b.ToString("X2", CultureInfo.InvariantCulture)); }
                    return hex.ToString();
                default: return string.Empty;
            }
        }

        private static string Number(double value)
        {
            if (double.IsNaN(value)) { return "NaN"; }
            if (double.IsPositiveInfinity(value)) { return "Infinity"; }
            if (double.IsNegativeInfinity(value)) { return "-Infinity"; }
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static int[] Select(MdfChannelGroup group, IReadOnlyList<int> channelIndices, MdfValueRepresentation representation)
        {
            IReadOnlyList<MdfChannel> channels = group.Channels ?? new MdfChannel[0];
            List<int> selection = new List<int>();
            if (channelIndices == null)
            {
                for (int i = 0; i < channels.Count; i++)
                {
                    MdfChannel channel = channels[i];
                    if (representation == MdfValueRepresentation.Physical ? channel.CanReadPhysical : channel.CanReadRaw) { selection.Add(i); }
                }
            }
            else
            {
                foreach (int index in channelIndices) { selection.Add(index); } // ReadSamples validates range/duplicates/capabilities.
            }
            if (selection.Count == 0) { throw new ArgumentException("No channel of the group can be exported.", nameof(channelIndices)); }
            return selection.ToArray();
        }

        private static void Field(StringBuilder line, bool first, char delimiter, string text)
        {
            if (!first) { line.Append(delimiter); }
            bool quote = text.Length != 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[text.Length - 1]));
            for (int i = 0; i < text.Length && !quote; i++)
            {
                char c = text[i];
                quote = c == delimiter || c == '"' || c == '\r' || c == '\n';
            }
            if (!quote) { line.Append(text); return; }
            line.Append('"');
            foreach (char c in text)
            {
                if (c == '"') { line.Append('"'); }
                line.Append(c);
            }
            line.Append('"');
        }
    }
}
