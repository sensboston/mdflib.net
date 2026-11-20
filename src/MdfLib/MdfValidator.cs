// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Purpose from pinned MIT mdflib validatingtask; the checks, severities and report shape are original.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;

namespace MdfLib
{
    /// <summary>The weight of one validation finding.</summary>
    internal enum MdfValidationSeverity
    {
        /// <summary>Readable, but suspicious or not supported by this library (the file may still be valid MDF).</summary>
        Warning = 0,
        /// <summary>A structural or integrity failure.</summary>
        Error = 1
    }

    /// <summary>One validation finding.</summary>
    internal sealed class MdfValidationIssue
    {
        internal MdfValidationIssue(MdfValidationSeverity severity, string location, string diagnosticCode, string message)
        {
            Severity = severity;
            Location = location;
            DiagnosticCode = diagnosticCode;
            Message = message;
        }

        /// <summary>Gets the severity.</summary>
        public MdfValidationSeverity Severity { get; }
        /// <summary>Gets where the finding applies: "file", "file model", "AT a", "DG d / CG c[ / channel | / SR s]".</summary>
        public string Location { get; }
        /// <summary>Gets the stable MDFnnn code of an <see cref="MdfFormatException"/>, or null.</summary>
        public string DiagnosticCode { get; }
        /// <summary>Gets the finding text.</summary>
        public string Message { get; }
    }

    /// <summary>The outcome of <see cref="MdfValidator"/>.</summary>
    internal sealed class MdfValidationReport
    {
        internal MdfValidationReport(IList<MdfValidationIssue> issues, int groups, ulong samples, ulong invalidValues)
        {
            Issues = new ReadOnlyCollection<MdfValidationIssue>(issues);
            ChannelGroupCount = groups;
            SampleCount = samples;
            InvalidValueCount = invalidValues;
            foreach (MdfValidationIssue issue in issues) { if (issue.Severity == MdfValidationSeverity.Error) { HasErrors = true; } }
        }

        /// <summary>Gets whether no error was found (warnings allowed).</summary>
        public bool IsValid => !HasErrors;
        /// <summary>Gets whether at least one error was found.</summary>
        public bool HasErrors { get; }
        /// <summary>Gets every finding in discovery order.</summary>
        public IReadOnlyList<MdfValidationIssue> Issues { get; }
        /// <summary>Gets the number of channel groups in the catalog.</summary>
        public int ChannelGroupCount { get; }
        /// <summary>Gets the number of records read from all channel groups.</summary>
        public ulong SampleCount { get; }
        /// <summary>Gets the number of read values (array elements included) flagged invalid.</summary>
        public ulong InvalidValueCount { get; }
    }

    /// <summary>Reads a whole measurement once and reports structural, integrity and plausibility findings.</summary>
    /// <remarks>Checks: metadata catalog; file model (history, attachments, events, hierarchy); embedded attachment
    /// checksums (external files are never opened); every readable channel group's records (all raw-readable channels,
    /// declared versus read record counts); stored master channels (NaN and decreasing physical values); sample reductions.
    /// Unsupported groups/channels are warnings. The source is never modified. Memory is bounded by reader batches.</remarks>
    internal static class MdfValidator
    {
        /// <summary>Opens and validates a stream; open failures become errors instead of exceptions.</summary>
        public static MdfValidationReport Validate(Stream stream, MdfReaderOptions options = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (stream == null) { throw new ArgumentNullException(nameof(stream)); }
            MdfReader reader;
            try { reader = MdfReader.Open(stream, true, options, cancellationToken); }
            catch (Exception error) when (Expected(error))
            {
                MdfFormatException format = error as MdfFormatException;
                List<MdfValidationIssue> issues = new List<MdfValidationIssue>
                { new MdfValidationIssue(MdfValidationSeverity.Error, "file", format == null ? null : format.DiagnosticCode, error.Message) };
                return new MdfValidationReport(issues, 0, 0, 0);
            }
            using (reader) { return Validate(reader, cancellationToken); }
        }

        /// <summary>Validates an open reader; no reader operation may be active.</summary>
        public static MdfValidationReport Validate(MdfReader reader, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (reader == null) { throw new ArgumentNullException(nameof(reader)); }
            List<MdfValidationIssue> issues = new List<MdfValidationIssue>();
            IReadOnlyList<MdfDataGroup> groups;
            try { groups = reader.ReadMetadata(cancellationToken); }
            catch (Exception error) when (Expected(error))
            {
                issues.Add(Issue("file", error));
                return new MdfValidationReport(issues, 0, 0, 0);
            }
            CheckFileModel(reader, issues, cancellationToken);
            int groupCount = 0;
            ulong samples = 0, invalid = 0;
            foreach (MdfDataGroup dataGroup in groups)
            {
                foreach (MdfChannelGroup group in dataGroup.ChannelGroups)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    groupCount++;
                    string where = "DG " + dataGroup.Index + " / CG " + group.Index;
                    if (!group.CanReadSamples)
                    {
                        issues.Add(new MdfValidationIssue(MdfValidationSeverity.Warning, where, null, "Samples are not readable: " + group.UnsupportedReason));
                        continue;
                    }
                    List<int> selection = new List<int>();
                    foreach (MdfChannel channel in group.Channels)
                    {
                        if (channel.CanReadRaw) { selection.Add(channel.Index); }
                        else { issues.Add(new MdfValidationIssue(MdfValidationSeverity.Warning, where + " / " + channel.Name, null, "Not readable: " + channel.UnsupportedReason)); }
                    }
                    if (selection.Count == 0) { continue; }
                    ulong read = 0;
                    try
                    {
                        foreach (MdfSampleBatch batch in reader.ReadSamples(group, selection, 0, ulong.MaxValue, 1024, MdfValueRepresentation.Raw, cancellationToken))
                        {
                            for (int c = 0; c < batch.Channels.Count; c++)
                            {
                                ulong elements = batch.Channels[c].Array == null ? 1 : batch.Channels[c].Array.ElementCount;
                                for (int r = 0; r < batch.SampleCount; r++)
                                {
                                    for (ulong e = 0; e < elements; e++) { if (!batch.GetValue(r, c, e).IsValid) { invalid++; } }
                                }
                            }
                            read += (ulong)batch.SampleCount;
                        }
                        if (read != group.SampleCount)
                        {
                            issues.Add(new MdfValidationIssue(MdfValidationSeverity.Error, where, null,
                                "Declared " + group.SampleCount + " records but read " + read + "."));
                        }
                    }
                    catch (Exception error) when (Expected(error)) { issues.Add(Issue(where, error)); }
                    samples += read;
                    CheckMaster(reader, group, where, issues, cancellationToken);
                    CheckReductions(reader, group, where, issues, cancellationToken);
                }
            }
            return new MdfValidationReport(issues, groupCount, samples, invalid);
        }

        private static void CheckFileModel(MdfReader reader, List<MdfValidationIssue> issues, CancellationToken token)
        {
            MdfFileModel model;
            try { model = reader.ReadFileModel(token); }
            catch (Exception error) when (Expected(error))
            {
                issues.Add(Issue("file model", error));
                return;
            }
            foreach (MdfAttachment attachment in model.Attachments)
            {
                if (!attachment.IsEmbedded) { continue; } // Never dereference external files.
                try { reader.ReadAttachment(attachment, true, token); }
                catch (Exception error) when (Expected(error)) { issues.Add(Issue("AT " + attachment.Index, error)); }
            }
        }

        private static void CheckMaster(MdfReader reader, MdfChannelGroup group, string where, List<MdfValidationIssue> issues, CancellationToken token)
        {
            MdfChannel master = null;
            foreach (MdfChannel channel in group.Channels)
            {
                bool numeric = channel.ValueKind == MdfValueKind.UnsignedInteger || channel.ValueKind == MdfValueKind.SignedInteger
                    || channel.ValueKind == MdfValueKind.FloatingPoint;
                if (channel.IsMaster && channel.Array == null && numeric && channel.CanReadPhysical) { master = channel; break; }
            }
            if (master == null || group.SampleCount == 0) { return; }
            string location = where + " / " + master.Name;
            try
            {
                double previous = double.NegativeInfinity;
                bool reportedNaN = false, reportedOrder = false;
                foreach (MdfSampleBatch batch in reader.ReadSamples(group, new[] { master.Index }, 0, ulong.MaxValue, 4096, MdfValueRepresentation.Physical, token))
                {
                    for (int r = 0; r < batch.SampleCount; r++)
                    {
                        MdfSampleValue value = batch.GetValue(r, 0);
                        if (!value.IsValid || value.Kind != MdfValueKind.FloatingPoint && value.Kind != MdfValueKind.UnsignedInteger && value.Kind != MdfValueKind.SignedInteger) { continue; }
                        double time = value.Kind == MdfValueKind.FloatingPoint ? value.FloatingPointValue
                            : value.Kind == MdfValueKind.UnsignedInteger ? value.UnsignedValue : (double)value.SignedValue;
                        ulong index = batch.FirstSampleIndex + (ulong)r;
                        if (double.IsNaN(time))
                        {
                            if (!reportedNaN) { issues.Add(new MdfValidationIssue(MdfValidationSeverity.Warning, location, null, "Master value is NaN at record " + index + ".")); }
                            reportedNaN = true;
                            continue;
                        }
                        if (time < previous && !reportedOrder)
                        {
                            issues.Add(new MdfValidationIssue(MdfValidationSeverity.Warning, location, null, "Master values decrease at record " + index + "."));
                            reportedOrder = true;
                        }
                        previous = time;
                    }
                }
            }
            catch (Exception error) when (Expected(error)) { issues.Add(Issue(location, error)); }
        }

        private static void CheckReductions(MdfReader reader, MdfChannelGroup group, string where, List<MdfValidationIssue> issues, CancellationToken token)
        {
            IReadOnlyList<MdfSampleReduction> reductions;
            try { reductions = reader.ReadSampleReductions(group, token); }
            catch (Exception error) when (Expected(error))
            {
                issues.Add(Issue(where + " / SR", error));
                return;
            }
            List<int> selection = new List<int>();
            foreach (MdfChannel channel in group.Channels)
            {
                Reading.ChannelLayout layout = channel.Layout;
                if (channel.CanReadRaw && channel.Array == null && layout != null && !layout.Virtual && layout.TextStorage != 2) { selection.Add(channel.Index); }
            }
            for (int i = 0; i < reductions.Count; i++)
            {
                MdfSampleReduction reduction = reductions[i];
                string location = where + " / SR " + i;
                if (reduction.UnsupportedReason != null)
                {
                    issues.Add(new MdfValidationIssue(MdfValidationSeverity.Warning, location, null, "Not readable: " + reduction.UnsupportedReason));
                    continue;
                }
                if (selection.Count == 0 || reduction.CycleCount == 0) { continue; }
                try
                {
                    ulong cycles = 0;
                    foreach (MdfSampleReductionBatch batch in reader.ReadSampleReduction(reduction, selection, 0, ulong.MaxValue, 1024, MdfValueRepresentation.Raw, token))
                    { cycles += (ulong)batch.CycleCount; }
                    if (cycles != reduction.CycleCount)
                    { issues.Add(new MdfValidationIssue(MdfValidationSeverity.Error, location, null, "Declared " + reduction.CycleCount + " cycles but read " + cycles + ".")); }
                }
                catch (Exception error) when (Expected(error)) { issues.Add(Issue(location, error)); }
            }
        }

        private static bool Expected(Exception error)
        {
            return error is MdfFormatException || error is NotSupportedException || error is MdfResourceLimitException
                || error is IOException || error is InvalidDataException;
        }

        private static MdfValidationIssue Issue(string location, Exception error)
        {
            MdfFormatException format = error as MdfFormatException;
            MdfValidationSeverity severity = error is NotSupportedException ? MdfValidationSeverity.Warning : MdfValidationSeverity.Error;
            return new MdfValidationIssue(severity, location, format == null ? null : format.DiagnosticCode, error.Message);
        }
    }
}
