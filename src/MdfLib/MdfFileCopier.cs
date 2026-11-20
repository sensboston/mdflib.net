// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Purpose from pinned MIT mdflib sortingtask/cuttask; channel mapping, time filter and skip policy are original.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using MdfLib.Writing;

namespace MdfLib
{
    /// <summary>Settings for <see cref="MdfFileCopier"/>.</summary>
    internal sealed class MdfCopyOptions
    {
        /// <summary>Gets or sets the output writer settings (copied); null uses <see cref="MdfWriterOptions"/> defaults.</summary>
        public MdfWriterOptions WriterOptions { get; set; }
        /// <summary>Gets or sets the inclusive lower master-time bound in physical master units (seconds), or null.</summary>
        public double? StartTime { get; set; }
        /// <summary>Gets or sets the inclusive upper master-time bound in physical master units (seconds), or null.</summary>
        public double? StopTime { get; set; }
        /// <summary>Gets or sets whether the output start time is the source's raw start time (default) instead of
        /// <see cref="MdfWriterOptions.StartTimeNanoseconds"/>.</summary>
        public bool KeepSourceStartTime { get; set; } = true;
        /// <summary>Gets or sets whether the source's header description, author, organization, project and subject
        /// (<see cref="MdfFileModel"/>) are copied (default) when <see cref="WriterOptions"/> sets none of them. Metadata
        /// that the output cannot store (for example non-Latin-1 text in MDF 3.30) is dropped and listed in
        /// <see cref="MdfCopyResult.Skipped"/> as "header metadata: reason".</summary>
        public bool CopyHeaderMetadata { get; set; } = true;
        /// <summary>Gets or sets whether file-level attachments (embedded data re-checked by MD5, external references) are
        /// copied to MDF 4 output (default). Channel attachments are not copied.</summary>
        public bool CopyAttachments { get; set; } = true;
        /// <summary>Gets or sets whether events are copied to MDF 4 output (default) with their parent/range links, copied
        /// attachments and copied channel-group scopes; channel scopes and references that cannot be kept are listed in
        /// <see cref="MdfCopyResult.Skipped"/> as "event i: reason". Sync values are copied unchanged.</summary>
        public bool CopyEvents { get; set; } = true;
    }

    /// <summary>The outcome of a completed <see cref="MdfFileCopier.Copy"/>.</summary>
    internal sealed class MdfCopyResult
    {
        internal MdfCopyResult(int groups, ulong samples, IList<string> skipped)
        {
            ChannelGroupCount = groups;
            SampleCount = samples;
            Skipped = new ReadOnlyCollection<string>(skipped);
        }

        /// <summary>Gets the number of written channel groups (each in its own sorted data group).</summary>
        public int ChannelGroupCount { get; }
        /// <summary>Gets the number of written records of all channel groups.</summary>
        public ulong SampleCount { get; }
        /// <summary>Gets one "DG d / CG c[ / channel]: reason" line per channel group or channel that was not copied.</summary>
        public IReadOnlyList<string> Skipped { get; }
    }

    /// <summary>Sorts and/or cuts a measurement into a new, sorted, finalized MDF file through <see cref="MdfWriter"/>.</summary>
    /// <remarks>Every readable channel group of every data group (sorted or interleaved) becomes its own data group, in
    /// source order. Raw values are copied exactly (output is little-endian); every conversion the writer can store
    /// (see <see cref="MdfConversionDefinition"/>) is kept. VLSD text/byte channels become VLSD channels (MDF 4 output only).
    /// Fixed arrays are copied element by element (MDF 4 output only). Virtual channels, channels with bitfield, nested
    /// or unsupported conversions (or conversions the output revision cannot store) and fields the writer cannot represent
    /// are skipped and listed in <see cref="MdfCopyResult.Skipped"/>. A time range keeps the records whose stored master's
    /// physical value (identity/linear) lies inside it; groups without such a master are skipped when a range is given.
    /// Channel names, units, conversions, group names, the start time, the header description/properties and (MDF 4
    /// output) file-level attachments and events are copied; the file history and the channel hierarchy are not. Unpaired UTF-16
    /// surrogates in text values become U+FFFD. Other text that the output
    /// encoding cannot represent (MDF 3.30 Latin-1) aborts the copy with <see cref="ArgumentException"/>.</remarks>
    internal static class MdfFileCopier
    {
        /// <summary>Copies into an empty, writable, seekable destination that stays open, and returns a summary.</summary>
        /// <exception cref="ArgumentException">The time range is invalid.</exception>
        /// <exception cref="InvalidOperationException">No channel group can be copied.</exception>
        public static MdfCopyResult Copy(MdfReader reader, Stream destination, MdfCopyOptions options = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (reader == null) { throw new ArgumentNullException(nameof(reader)); }
            if (destination == null) { throw new ArgumentNullException(nameof(destination)); }
            options = options ?? new MdfCopyOptions();
            double start = options.StartTime ?? double.NegativeInfinity;
            double stop = options.StopTime ?? double.PositiveInfinity;
            bool ranged = options.StartTime.HasValue || options.StopTime.HasValue;
            if (double.IsNaN(start) || double.IsNaN(stop) || start > stop)
            { throw new ArgumentException("The time range must be ordered and not NaN.", nameof(options)); }
            MdfWriterOptions writerOptions = Clone(options.WriterOptions);
            if (options.KeepSourceStartTime) { writerOptions.StartTimeNanoseconds = reader.Header.RawStartTimeNanoseconds; }
            List<string> skipped = new List<string>();
            bool copiedMetadata = options.CopyHeaderMetadata && CopyHeader(reader, writerOptions, skipped, cancellationToken);
            WriterSettings settings;
            try { settings = new WriterSettings(writerOptions); } // Validates options before touching the destination.
            catch (Exception error) when (copiedMetadata && (error is ArgumentException || error is NotSupportedException || error is MdfResourceLimitException))
            {
                writerOptions.Comment = writerOptions.Author = writerOptions.Organization = writerOptions.Project = writerOptions.Subject = null;
                settings = new WriterSettings(writerOptions); // The caller's own options still fail here.
                skipped.Add("header metadata: " + error.Message);
            }

            List<GroupPlan> plans = new List<GroupPlan>();
            foreach (MdfDataGroup dataGroup in reader.ReadMetadata(cancellationToken))
            {
                foreach (MdfChannelGroup group in dataGroup.ChannelGroups)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string where = "DG " + dataGroup.Index + " / CG " + group.Index;
                    // a VLSD channel group's data travels with its (copied or listed) VLSD channel.
                    if (group.UnsupportedReason == Formats.Mdf4.MetadataReader.VlsdGroupReason) { continue; }
                    if (!group.CanReadSamples) { skipped.Add(where + ": " + group.UnsupportedReason); continue; }
                    GroupPlan plan = new GroupPlan { Source = group, Name = string.IsNullOrEmpty(group.Name) ? null : group.Name };
                    foreach (MdfChannel channel in group.Channels)
                    {
                        string reason;
                        MdfChannelDefinition definition = Define(channel, settings, plan.HasMaster, out reason);
                        if (definition == null) { skipped.Add(where + " / " + channel.Name + ": " + reason); continue; }
                        if (definition.IsMaster)
                        {
                            plan.HasMaster = true;
                            plan.MasterColumn = plan.ValueCount;
                            plan.MasterLinear = definition.Conversion == null;
                            plan.MasterFactor = channel.Conversion.Kind == MdfConversionKind.Linear ? channel.Conversion.Factor.Value : 1;
                            plan.MasterOffset = channel.Conversion.Kind == MdfConversionKind.Linear ? channel.Conversion.Offset.Value : 0;
                        }
                        int elements = channel.Array == null ? 1 : (int)channel.Array.ElementCount;
                        if (plan.ValueCount > int.MaxValue - elements) { skipped.Add(where + " / " + channel.Name + ": the row has too many values"); continue; }
                        plan.Indices.Add(channel.Index);
                        plan.Definitions.Add(definition);
                        plan.Elements.Add(elements);
                        plan.ValueCount += elements;
                    }
                    if (plan.Indices.Count == 0) { skipped.Add(where + ": no channel can be copied"); continue; }
                    if (ranged && !(plan.HasMaster && plan.MasterLinear)) { skipped.Add(where + ": a time range needs a stored identity/linear master"); continue; }
                    if (settings.Mdf3 && plans.Count == 255) { skipped.Add(where + ": MDF 3.30 output holds at most 255 channel groups"); continue; }
                    plans.Add(plan);
                }
            }
            if (plans.Count == 0) { throw new InvalidOperationException("No channel group of the source can be copied."); }

            ulong samples = 0;
            using (MdfWriter writer = MdfWriter.Create(destination, true, writerOptions, cancellationToken))
            {
                for (int g = 0; g < plans.Count; g++)
                {
                    GroupPlan plan = plans[g];
                    plan.Target = g == 0 ? 0 : writer.AddChannelGroup();
                    if (plan.Name != null && !settings.Mdf3) { writer.SetChannelGroupName(plan.Target, plan.Name); }
                    foreach (MdfChannelDefinition definition in plan.Definitions) { writer.AddChannel(plan.Target, definition); }
                }
                if (options.CopyAttachments || options.CopyEvents) { CopyFileModel(reader, writer, plans, options, settings, skipped, cancellationToken); }
                writer.StartMeasurement(cancellationToken);
                foreach (GroupPlan plan in plans)
                {
                    MdfSampleValue[] row = new MdfSampleValue[plan.ValueCount];
                    foreach (MdfSampleBatch batch in reader.ReadSamples(plan.Source, plan.Indices, 0, ulong.MaxValue, 1024,
                        MdfValueRepresentation.Raw, cancellationToken))
                    {
                        for (int r = 0; r < batch.SampleCount; r++)
                        {
                            int next = 0;
                            for (int c = 0; c < plan.Elements.Count; c++)
                            {
                                for (int e = 0; e < plan.Elements[c]; e++)
                                {
                                    MdfSampleValue value = batch.GetValue(r, c, (ulong)e);
                                    if (value.Kind == MdfValueKind.Text)
                                    {
                                        string text = Sanitize(value.TextValue);
                                        if (!ReferenceEquals(text, value.TextValue)) { value = MdfSampleValue.FromText(text, value.IsValid); }
                                    }
                                    row[next++] = value;
                                }
                            }
                            if (ranged)
                            {
                                double time = plan.MasterOffset + plan.MasterFactor * Number(row[plan.MasterColumn]);
                                if (!(time >= start && time <= stop)) { continue; }
                            }
                            if (settings.Mdf3 && !AllValid(row)) { continue; } // MDF 3 cannot store invalid samples.
                            writer.SaveSample(plan.Target, row, cancellationToken);
                            samples++;
                        }
                    }
                }
                writer.StopMeasurement(cancellationToken);
                writer.FinalizeMeasurement(cancellationToken);
            }
            return new MdfCopyResult(plans.Count, samples, skipped);
        }

        private sealed class GroupPlan
        {
            internal MdfChannelGroup Source;
            internal string Name;
            internal int Target, MasterColumn = -1;
            internal bool HasMaster, MasterLinear;
            internal double MasterFactor = 1, MasterOffset;
            internal readonly List<int> Indices = new List<int>();
            internal readonly List<MdfChannelDefinition> Definitions = new List<MdfChannelDefinition>();
            internal readonly List<int> Elements = new List<int>(); // values per copied column (array elements).
            internal int ValueCount;
        }

        private static MdfChannelDefinition Define(MdfChannel channel, WriterSettings settings, bool hasMaster, out string reason)
        {
            reason = null;
            Reading.ChannelLayout layout = channel.Layout;
            if (!channel.CanReadRaw) { reason = channel.UnsupportedReason; return null; }
            int[] dimensions = null;
            if (channel.Array != null)
            {
                // fixed arrays are copied element by element in row-major order (MDF 4 output only).
                if (settings.Mdf3) { reason = "array channels need MDF 4 output"; return null; }
                if (channel.Array.ElementCount > int.MaxValue || channel.Array.Dimensions.Count > 255)
                { reason = "the array is too large for the writer"; return null; }
                dimensions = new int[channel.Array.Dimensions.Count];
                for (int axis = 0; axis < dimensions.Length; axis++) { dimensions[axis] = (int)channel.Array.Dimensions[axis]; }
            }
            if (layout != null && layout.Virtual) { reason = "virtual channels are not copied"; return null; }
            MdfValueKind kind = channel.ValueKind;
            bool numeric = kind == MdfValueKind.UnsignedInteger || kind == MdfValueKind.SignedInteger || kind == MdfValueKind.FloatingPoint;
            int bits;
            bool variable = false;
            if (numeric || kind == MdfValueKind.Complex) { bits = checked((int)channel.BitCount); }
            else if ((kind == MdfValueKind.Text || kind == MdfValueKind.ByteArray) && layout != null && layout.TextStorage == 2)
            {
                // VLSD (SD blocks or a VLSD CG) is written as a VLSD channel group of the output.
                if (settings.Mdf3) { reason = "variable-length channels need MDF 4 output"; return null; }
                variable = true;
                bits = 64;
            }
            else if (kind == MdfValueKind.Text || kind == MdfValueKind.ByteArray)
            {
                if (layout == null || !(layout.TextStorage == 1 || (layout.TextStorage == 3 && kind == MdfValueKind.Text)))
                { reason = "only fixed-length text/byte fields are copied"; return null; }
                // UTF-8 output can need three bytes per stored byte (each ill-formed source byte decodes to U+FFFD);
                // Latin-1 (MDF 3) output needs at most one byte per decoded character.
                int growth = kind == MdfValueKind.ByteArray || settings.Mdf3 ? 1 : 3;
                bits = checked((int)Math.Min(int.MaxValue / 8, (long)layout.FieldBytes * growth) * 8);
            }
            else { reason = "unsupported value kind"; return null; }
            MdfChannelConversion conversion = channel.Conversion;
            bool linear = conversion != null && conversion.Kind == MdfConversionKind.Linear && conversion.Factor.HasValue && conversion.Offset.HasValue;
            MdfConversionDefinition other = null;
            if (conversion != null && conversion.Kind != MdfConversionKind.Identity && !(linear && numeric))
            {
                other = linear ? null : MdfConversionDefinition.FromConversion(conversion);
                if (other == null) { reason = "conversion " + conversion.Kind + " is not representable by the writer"; return null; }
            }
            MdfChannelDefinition definition = new MdfChannelDefinition
            {
                Name = string.IsNullOrEmpty(channel.Name) ? "channel" + channel.Index : channel.Name,
                Unit = channel.Unit ?? string.Empty,
                ValueKind = kind,
                BitCount = bits,
                IsMaster = channel.IsMaster && numeric && !hasMaster,
                HasLinearConversion = linear,
                Factor = linear ? conversion.Factor.Value : 1,
                Offset = linear ? conversion.Offset.Value : 0,
                Conversion = other,
                IsVariableLength = variable,
                ArrayDimensions = dimensions,
            };
            try
            {
                ulong charge;
                WriteChannel.Create(definition, settings, 0, 0, out charge);
            }
            catch (Exception error) when (error is ArgumentException || error is NotSupportedException || error is MdfResourceLimitException)
            {
                reason = error.Message;
                return null;
            }
            return definition;
        }

        // Header description/properties of the source when the caller sets none; false when nothing was taken.
        private static bool CopyHeader(MdfReader reader, MdfWriterOptions target, List<string> skipped, CancellationToken token)
        {
            if (target.Comment != null || target.Author != null || target.Organization != null || target.Project != null || target.Subject != null)
            { return false; }
            MdfFileModel model;
            try { model = reader.ReadFileModel(token); }
            catch (Exception error) when (error is MdfFormatException || error is MdfResourceLimitException || error is NotSupportedException)
            {
                skipped.Add("header metadata: " + error.Message);
                return false;
            }
            target.Comment = NullIfEmpty(model.Description);
            target.Author = NullIfEmpty(model.Author);
            target.Organization = NullIfEmpty(model.Organization);
            target.Project = NullIfEmpty(model.Project);
            target.Subject = NullIfEmpty(model.Subject);
            return target.Comment != null || target.Author != null || target.Organization != null || target.Project != null || target.Subject != null;
        }

        private static string NullIfEmpty(string text) { return string.IsNullOrEmpty(text) ? null : text; }

        // file-level attachments and events of the source model, with indices remapped to the copied items.
        private static void CopyFileModel(MdfReader reader, MdfWriter writer, List<GroupPlan> plans, MdfCopyOptions options,
            WriterSettings settings, List<string> skipped, CancellationToken token)
        {
            MdfFileModel model;
            try { model = reader.ReadFileModel(token); }
            catch (Exception error) when (error is MdfFormatException || error is MdfResourceLimitException || error is NotSupportedException)
            {
                skipped.Add("attachments and events: " + error.Message);
                return;
            }
            bool attachmentsWanted = options.CopyAttachments && model.Attachments.Count != 0;
            bool eventsWanted = options.CopyEvents && model.Events.Count != 0;
            if (settings.Mdf3)
            {
                if (attachmentsWanted) { skipped.Add("attachments: MDF 3.30 output has no attachments"); }
                if (eventsWanted) { skipped.Add("events: MDF 3.30 output has no events"); }
                return;
            }
            Dictionary<MdfAttachment, int> copiedAttachments = new Dictionary<MdfAttachment, int>();
            if (attachmentsWanted)
            {
                foreach (MdfAttachment attachment in model.Attachments)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        MdfAttachmentDefinition definition = new MdfAttachmentDefinition
                        {
                            FileName = Sanitize(attachment.FileName), MimeType = Sanitize(attachment.MimeType), Comment = Sanitize(attachment.Comment),
                            Data = attachment.IsEmbedded ? reader.ReadAttachment(attachment, true, token) : null,
                            Compress = attachment.IsCompressed
                        };
                        copiedAttachments.Add(attachment, writer.AddAttachment(definition));
                    }
                    catch (Exception error) when (error is MdfFormatException || error is MdfResourceLimitException
                        || error is NotSupportedException || error is ArgumentException)
                    { skipped.Add("attachment " + attachment.Index + ": " + error.Message); }
                }
            }
            if (!eventsWanted) { return; }
            Dictionary<MdfChannelGroup, int> targets = new Dictionary<MdfChannelGroup, int>();
            foreach (GroupPlan plan in plans) { targets[plan.Source] = plan.Target; }
            Dictionary<MdfEvent, int> copiedEvents = new Dictionary<MdfEvent, int>();
            foreach (MdfEvent item in model.Events)
            {
                token.ThrowIfCancellationRequested();
                string where = "event " + item.Index + ": ";
                List<int> scopes = new List<int>();
                foreach (MdfChannelGroup group in item.ScopeChannelGroups)
                {
                    int target;
                    if (targets.TryGetValue(group, out target)) { scopes.Add(target); }
                    else { skipped.Add(where + "a channel-group scope was not copied"); }
                }
                if (item.ScopeChannels.Count != 0) { skipped.Add(where + "channel scopes are not copied"); }
                List<int> references = new List<int>();
                foreach (MdfAttachment attachment in item.Attachments)
                {
                    int target;
                    if (copiedAttachments.TryGetValue(attachment, out target)) { references.Add(target); }
                    else { skipped.Add(where + "an attachment reference was not copied"); }
                }
                int parent = -1, range = -1;
                if (item.Parent != null && !copiedEvents.TryGetValue(item.Parent, out parent))
                {
                    parent = -1;
                    skipped.Add(where + "the parent event follows it or was not copied");
                }
                if (item.Range != null && !copiedEvents.TryGetValue(item.Range, out range))
                {
                    range = -1;
                    skipped.Add(where + "the range-begin event follows it or was not copied");
                }
                string groupName = Sanitize(item.GroupName);
                if (groupName.Length != 0 && settings.Version != "4.20")
                {
                    skipped.Add(where + "the group name needs MDF 4.20 output");
                    groupName = string.Empty;
                }
                try
                {
                    copiedEvents.Add(item, writer.AddEvent(new MdfEventDefinition
                    {
                        Name = Sanitize(item.Name), Comment = Sanitize(item.Comment), GroupName = groupName,
                        Type = item.Type, SyncType = item.SyncType, RangeType = item.RangeType, Cause = item.Cause,
                        SyncBaseValue = item.SyncBaseValue, SyncFactor = item.SyncFactor, ParentIndex = parent, RangeIndex = range,
                        ScopeGroupIndices = scopes, AttachmentIndices = references
                    }));
                }
                catch (Exception error) when (error is ArgumentException || error is NotSupportedException || error is MdfResourceLimitException)
                { skipped.Add(where + error.Message); }
            }
        }

        // UTF-16 sources can decode to unpaired surrogates, which no strict output encoding accepts: each becomes U+FFFD.
        internal static string Sanitize(string text)
        {
            if (text == null) { return string.Empty; }
            char[] chars = null;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                bool paired = char.IsHighSurrogate(c) ? i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
                    : !char.IsLowSurrogate(c) || (i > 0 && char.IsHighSurrogate(text[i - 1]));
                if (char.IsHighSurrogate(c) && paired) { i++; continue; }
                if (paired) { continue; }
                if (chars == null) { chars = text.ToCharArray(); }
                chars[i] = '\uFFFD';
            }
            return chars == null ? text : new string(chars);
        }

        private static double Number(MdfSampleValue value)
        {
            switch (value.Kind)
            {
                case MdfValueKind.UnsignedInteger: return value.UnsignedValue;
                case MdfValueKind.SignedInteger: return value.SignedValue;
                default: return value.FloatingPointValue;
            }
        }

        private static bool AllValid(MdfSampleValue[] row)
        {
            foreach (MdfSampleValue value in row) { if (!value.IsValid) { return false; } }
            return true;
        }

        private static MdfWriterOptions Clone(MdfWriterOptions source)
        {
            source = source ?? new MdfWriterOptions();
            return new MdfWriterOptions
            {
                Version = source.Version,
                StartTimeNanoseconds = source.StartTimeNanoseconds,
                MaximumMetadataBytes = source.MaximumMetadataBytes,
                MaximumRecordBytes = source.MaximumRecordBytes,
                MaximumChannelCount = source.MaximumChannelCount,
                MaximumTextBytes = source.MaximumTextBytes,
                CompressData = source.CompressData,
                CompressionBlockBytes = source.CompressionBlockBytes,
                Comment = source.Comment, Author = source.Author, Organization = source.Organization, Project = source.Project,
                Subject = source.Subject, ToolIdentifier = source.ToolIdentifier, ToolVendor = source.ToolVendor,
                ToolVersion = source.ToolVersion, UserName = source.UserName, HistoryComment = source.HistoryComment,
            };
        }
    }
}
