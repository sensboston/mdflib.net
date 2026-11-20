// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// managed sample: a text equivalent of the upstream mdfviewer tree (file -> header -> history/attachments/events ->
// data groups -> channel groups -> channels) and its sample table, on the public MdfLib API.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MdfLib.Samples
{
    /// <summary>mdfinfo: prints the block tree of an MDF file and optionally the first samples of one channel group.</summary>
    public static class MdfInfoTool
    {
        /// <summary>Usage: mdfinfo &lt;file&gt; [--samples &lt;group&gt; [count]] [--validate]. Returns 0, or 1 on failure.</summary>
        public static int Run(string[] args, TextWriter output, TextWriter error)
        {
            if (args == null || args.Length == 0 || args[0].StartsWith("-", StringComparison.Ordinal))
            {
                error.WriteLine("Usage: mdfinfo <file> [--samples <group> [count]] [--validate]");
                return 1;
            }
            int group = -1, count = 10;
            bool validate = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--validate") { validate = true; }
                else if (args[i] == "--samples" && i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out group))
                {
                    i++;
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out count)) { i++; }
                }
                else { error.WriteLine("Unknown argument " + args[i] + "."); return 1; }
            }
            try
            {
                using (MdfReader reader = MdfReader.Open(File.OpenRead(args[0])))
                {
                    Describe(reader, output);
                    if (group >= 0)
                    {
                        MdfChannelGroup selected = reader.ReadMetadata().SelectMany(d => d.ChannelGroups).ElementAtOrDefault(group);
                        if (selected == null) { error.WriteLine("No channel group " + group + "."); return 1; }
                        PrintSamples(reader, selected, count, output);
                    }
                    if (validate)
                    {
                        MdfValidationReport result = MdfValidator.Validate(reader);
                        foreach (MdfValidationIssue issue in result.Issues) { output.WriteLine(issue.Severity + " " + issue.Location + ": " + issue.Message); }
                        output.WriteLine(result.IsValid ? "Valid." : "Invalid.");
                        if (!result.IsValid) { return 1; }
                    }
                }
                return 0;
            }
            catch (Exception failure) when (failure is MdfFormatException || failure is MdfResourceLimitException || failure is IOException || failure is NotSupportedException)
            {
                error.WriteLine("Cannot read " + args[0] + ": " + failure.Message);
                return 1;
            }
        }

        /// <summary>Prints the header, file model and catalog, one indented line per block.</summary>
        internal static void Describe(MdfReader reader, TextWriter output)
        {
            MdfFileHeader header = reader.Header;
            output.WriteLine("MDF " + header.Version + " (" + header.ProgramIdentifier + ")");
            output.WriteLine("  Start: " + header.RawStartTimeNanoseconds.ToString(CultureInfo.InvariantCulture) + " ns");
            Optional(output, "  Author: ", header.Author);
            Optional(output, "  Organization: ", header.Organization);
            Optional(output, "  Project: ", header.Project);
            Optional(output, "  Subject: ", header.Subject);
            IReadOnlyList<MdfDataGroup> groups = reader.ReadMetadata();
            if (header.Version.StartsWith("4", StringComparison.Ordinal))
            {
                MdfFileModel model = reader.ReadFileModel();
                foreach (MdfFileHistory history in model.FileHistory)
                {
                    output.WriteLine("  History: " + history.ToolIdentifier + " " + history.ToolVersion + " (" + history.ToolVendor + ") " + history.UserName);
                }
                foreach (MdfAttachment attachment in model.Attachments) { output.WriteLine("  Attachment: " + attachment.FileName); }
                foreach (MdfEvent item in model.Events) { output.WriteLine("  Event: " + item.Name); }
            }
            foreach (MdfDataGroup dataGroup in groups)
            {
                output.WriteLine("  DG " + dataGroup.Index.ToString(CultureInfo.InvariantCulture) + Suffix(dataGroup.Comment));
                foreach (MdfChannelGroup group in dataGroup.ChannelGroups)
                {
                    output.WriteLine("    CG " + group.Index.ToString(CultureInfo.InvariantCulture) + " " + (group.Name ?? "") + " samples=" + group.SampleCount.ToString(CultureInfo.InvariantCulture)
                        + " flags=" + group.Flags.ToString(CultureInfo.InvariantCulture) + (group.Source != null ? " source=" + group.Source.Name : "")
                        + (group.CanReadSamples ? "" : " unsupported: " + group.UnsupportedReason));
                    foreach (MdfChannel channel in group.Channels)
                    {
                        string indent = new string(' ', 6 + 2 * Depth(channel));
                        output.WriteLine(indent + "CN " + channel.Name + (string.IsNullOrEmpty(channel.Unit) ? "" : " [" + channel.Unit + "]") + " " + channel.ValueKind + " "
                            + channel.BitCount.ToString(CultureInfo.InvariantCulture) + " bits" + (channel.IsMaster ? " master" : "")
                            + (channel.Conversion != null && channel.Conversion.Kind != MdfConversionKind.Identity ? " cc=" + channel.Conversion.Kind : ""));
                    }
                }
            }
        }

        /// <summary>Prints a tab-separated table of the first <paramref name="count"/> physical samples (raw where not convertible).</summary>
        internal static void PrintSamples(MdfReader reader, MdfChannelGroup group, int count, TextWriter output)
        {
            List<int> channels = Enumerable.Range(0, group.Channels.Count).Where(i => group.Channels[i].CanReadRaw).ToList();
            output.WriteLine(string.Join("\t", channels.Select(i => group.Channels[i].Name)));
            if (!group.CanReadSamples || channels.Count == 0 || count <= 0) { return; }
            ulong rows = Math.Min((ulong)count, group.SampleCount);
            List<int> physical = channels.Where(i => group.Channels[i].CanReadPhysical).ToList();
            List<int> raw = channels.Except(physical).ToList();
            List<string[]> table = new List<string[]>();
            for (ulong r = 0; r < rows; r++) { table.Add(new string[channels.Count]); }
            Fill(reader, group, physical, channels, MdfValueRepresentation.Physical, rows, table);
            Fill(reader, group, raw, channels, MdfValueRepresentation.Raw, rows, table);
            foreach (string[] row in table) { output.WriteLine(string.Join("\t", row)); }
        }

        private static void Fill(MdfReader reader, MdfChannelGroup group, List<int> selected, List<int> columns, MdfValueRepresentation representation, ulong rows, List<string[]> table)
        {
            if (selected.Count == 0) { return; }
            int row = 0;
            foreach (MdfSampleBatch batch in reader.ReadSamples(group, selected, 0, rows, 1024, representation))
            {
                for (int i = 0; i < batch.SampleCount; i++, row++)
                {
                    for (int c = 0; c < selected.Count; c++) { table[row][columns.IndexOf(selected[c])] = MdfCsvExporter.Format(batch.GetValue(i, c)); }
                }
            }
        }

        private static int Depth(MdfChannel channel)
        {
            int depth = 0;
            for (MdfChannel parent = channel.Parent; parent != null; parent = parent.Parent) { depth++; }
            return depth;
        }

        private static void Optional(TextWriter output, string label, string value)
        {
            if (!string.IsNullOrEmpty(value)) { output.WriteLine(label + value); }
        }

        private static string Suffix(string comment)
        {
            return string.IsNullOrEmpty(comment) ? "" : " " + comment.Replace("\r", " ").Replace("\n", " ");
        }
    }
}
