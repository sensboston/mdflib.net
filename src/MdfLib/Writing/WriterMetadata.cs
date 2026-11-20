// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Header description/properties and the file-history entry of a new file. XML element names follow the ASAM MDF 4
// HDcomment/FHcomment schema as written by upstream hd4block/fhcomment; the encoding and validation policy is new.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MdfLib.Writing
{
    internal sealed class WriterMetadata
    {
        internal const int Mdf3FieldBytes = 32; // HD author/organization/project/subject CHAR[32].
        internal const int Mdf3DescriptionBytes = 128; // CN cn_description CHAR[128] at 58: where MDF 3.30 stores the comment.
        internal const int Mdf4IdentifierBytes = 26; // CN tail field at cn+134 of the 160-byte CN block: cn_long_identifier.
        internal const int Mdf3MetadataBytes = 129; // A CN/CG metadata text may span the 128-byte fixed field plus its NUL.

        // MDF 3: Latin-1 fixed fields (without NUL, at most 31 bytes) and a TX payload; MDF 4: MD payloads. All null when unset.
        internal byte[] Author, Organization, Project, Subject;
        internal byte[] HeaderComment; // MDF 3 TX text or MDF 4 HDcomment XML, NUL-terminated.
        internal byte[] HistoryComment; // MDF 4 FHcomment XML, NUL-terminated; null = no FH block.
        internal ulong HistoryTime; // FH change time in UTC nanoseconds; the start time unless the facade chose one.
        internal long? RecorderIndex; // MDF 4 only: the Recorder.FileIndex common property.
        internal double? StartAngle, StartDistance; // MDF 4 only: HD start angle (rad) and distance (m); null = not stored.

        internal static WriterMetadata Create(MdfWriterOptions options, WriterSettings settings)
        {
            WriterMetadata result = new WriterMetadata();
            result.HistoryTime = options.HistoryTimeNanoseconds ?? settings.StartTime;
            string[] properties = { options.Author, options.Organization, options.Project, options.Subject };
            string[] names = { nameof(options.Author), nameof(options.Organization), nameof(options.Project), nameof(options.Subject) };
            string[] historyFields = { options.ToolIdentifier, options.ToolVendor, options.ToolVersion, options.UserName, options.HistoryComment };
            string[] historyNames = { nameof(options.ToolIdentifier), nameof(options.ToolVendor), nameof(options.ToolVersion), nameof(options.UserName), nameof(options.HistoryComment) };
            for (int i = 0; i < properties.Length; i++) { Check(properties[i], names[i]); }
            Check(options.Comment, nameof(options.Comment));
            for (int i = 0; i < historyFields.Length; i++) { Check(historyFields[i], historyNames[i]); }
            if (settings.Mdf3)
            {
                if (HasTags(options.CommonProperties)) { throw new NotSupportedException("MDF 3.30 headers have no common properties beyond author, department, project and subject."); }
                byte[][] fields = new byte[4][];
                for (int i = 0; i < properties.Length; i++)
                {
                    if (properties[i] == null) { continue; }
                    fields[i] = Latin1(properties[i], names[i]);
                    if (fields[i].Length >= Mdf3FieldBytes)
                    { throw new ArgumentException("MDF 3 header fields hold at most 31 Latin-1 bytes.", names[i]); }
                }
                result.Author = fields[0];
                result.Organization = fields[1];
                result.Project = fields[2];
                result.Subject = fields[3];
                if (options.Comment != null)
                {
                    byte[] text = Latin1(options.Comment, nameof(options.Comment));
                    result.HeaderComment = Terminated(text, settings);
                    if (result.HeaderComment.Length > 65531) { throw new NotSupportedException("The MDF 3 TX block cannot exceed UInt16 length."); }
                }
                return result; // MDF 3 has no file-history block; the history options are validated but not stored.
            }
            Check(options.MeasurementId, nameof(options.MeasurementId));
            Check(options.RecorderId, nameof(options.RecorderId));
            result.StartAngle = options.StartAngle;
            result.StartDistance = options.StartDistance;
            result.RecorderIndex = options.RecorderIndex;
            if (options.Comment != null || Array.Exists(properties, p => p != null) || options.MeasurementId != null || options.RecorderId != null || options.RecorderIndex.HasValue || HasTags(options.CommonProperties))
            {
                StringBuilder xml = new StringBuilder("<HDcomment><TX>");
                Escape(xml, options.Comment ?? string.Empty);
                xml.Append("</TX>");
                string[] keys = { "author", "department", "project", "subject" };
                if (Array.Exists(properties, p => p != null) || options.MeasurementId != null || options.RecorderId != null || options.RecorderIndex.HasValue || HasTags(options.CommonProperties))
                {
                    xml.Append("<common_properties>");
                    for (int i = 0; i < properties.Length; i++)
                    {
                        if (properties[i] == null) { continue; }
                        xml.Append("<e name=\"").Append(keys[i]).Append("\">");
                        Escape(xml, properties[i]);
                        xml.Append("</e>");
                    }
                    if (options.MeasurementId != null) { AppendCommon(xml, "Measurement.UUID", "string", options.MeasurementId); }
                    if (options.RecorderId != null) { AppendCommon(xml, "Recorder.UUID", "string", options.RecorderId); }
                    if (options.RecorderIndex.HasValue) { AppendCommon(xml, "Recorder.FileIndex", "integer", options.RecorderIndex.Value.ToString(CultureInfo.InvariantCulture)); }
                    if (HasTags(options.CommonProperties)) { foreach (MdfCommonProperty tag in options.CommonProperties) { AppendTag(xml, tag); } }
                    xml.Append("</common_properties>");
                }
                xml.Append("</HDcomment>");
                result.HeaderComment = Terminated(Encoding.UTF8.GetBytes(xml.ToString()), settings);
            }
            bool historyTags = HasTags(options.HistoryCommonProperties);
            if (settings.Mdf3 && historyTags) { throw new NotSupportedException("MDF 3.30 has no file-history common properties."); }
            if (Array.Exists(historyFields, f => f != null) || historyTags)
            {
                StringBuilder xml = new StringBuilder("<FHcomment><TX>");
                Escape(xml, options.HistoryComment ?? string.Empty);
                xml.Append("</TX><tool_id>");
                Escape(xml, options.ToolIdentifier ?? "MdfLib"); // tool_id is mandatory in FHcomment.
                xml.Append("</tool_id>");
                string[] tags = { "tool_vendor", "tool_version", "user_name" };
                for (int i = 0; i < tags.Length; i++)
                {
                    if (historyFields[i + 1] == null) { continue; }
                    xml.Append('<').Append(tags[i]).Append('>');
                    Escape(xml, historyFields[i + 1]);
                    xml.Append("</").Append(tags[i]).Append('>');
                }
                if (historyTags)
                {
                    xml.Append("<common_properties>");
                    foreach (MdfCommonProperty tag in options.HistoryCommonProperties) { AppendTag(xml, tag); }
                    xml.Append("</common_properties>");
                }
                xml.Append("</FHcomment>");
                result.HistoryComment = Terminated(Encoding.UTF8.GetBytes(xml.ToString()), settings);
            }
            return result;
        }

        // One common property: <e name="..."> with its type attribute when it has one, and the escaped text value.
        private static void AppendTag(StringBuilder xml, MdfCommonProperty tag)
        {
            if (string.IsNullOrEmpty(tag.Name)) { throw new ArgumentException("A common property needs a name.", "Name"); }
            Check(tag.Name, "Name");
            Check(tag.Value, "Value");
            xml.Append("<e name=\"");
            Escape(xml, tag.Name);
            xml.Append('"');
            if (tag.Type != null) { xml.Append(" type=\"").Append(tag.Type).Append('"'); }
            xml.Append('>');
            Escape(xml, tag.Value ?? string.Empty);
            xml.Append("</e>");
        }

        // XML 1.0 characters only: no NUL or other C0 controls except tab/LF/CR, no U+FFFE/U+FFFF, no lone surrogates.
        // One typed common property (the upstream SetCommonProperty form: <e name="..." type="...">value</e>).
        private static void AppendCommon(StringBuilder xml, string name, string type, string value)
        {
            xml.Append("<e name=\"").Append(name).Append("\" type=\"").Append(type).Append("\">");
            Escape(xml, value);
            xml.Append("</e>");
        }

        private static void Check(string text, string name)
        {
            if (text == null) { return; }
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                bool control = c < 0x20 && c != '\t' && c != '\n' && c != '\r';
                bool surrogate = char.IsSurrogate(c) && !(char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]));
                if (control || c == '\uFFFE' || c == '\uFFFF' || surrogate)
                { throw new ArgumentException("Metadata text must contain only XML 1.0 characters (no NUL, controls or lone surrogates).", name); }
                if (char.IsHighSurrogate(c)) { i++; }
            }
        }

        private static byte[] Latin1(string text, string name)
        {
            try { return Encoding.GetEncoding(28591, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetBytes(text); }
            catch (EncoderFallbackException error) { throw new ArgumentException("MDF 3 metadata must be Latin-1.", name, error); }
        }

        private static byte[] Terminated(byte[] text, WriterSettings settings)
        {
            WriterSettings.Limit(nameof(MdfWriterOptions.MaximumTextBytes), (ulong)settings.MaximumText, (ulong)text.Length + 1);
            byte[] result = new byte[text.Length + 1];
            Array.Copy(text, result, text.Length);
            return result;
        }

        // One metadata comment of a CG/CN/SI/CE block: the format's XML root around the escaped text (MDF 4, as an MD
        // payload) or the plain Latin-1 TX text (MDF 3). Same validation and limits as the header comment.
        internal static byte[] Comment(string root, string text, WriterSettings settings, out int size)
        {
            return Comment(root, text, null, settings, out size);
        }

        // The same block with the common properties of an MD block. A block with neither text nor tags is absent.
        internal static byte[] Comment(string root, string text, IReadOnlyList<MdfCommonProperty> tags, WriterSettings settings, out int size)
        {
            Check(text, "text");
            if (!HasTags(tags) && (text == null || text.Length == 0)) { size = 0; return null; }
            string xml = Wrap(root, text, tags);
            size = settings.TextSize(xml, TextKind.Comment);
            return settings.Encode(xml, size);
        }

        internal static string Wrap(string root, string text)
        {
            return Wrap(root, text, null);
        }

        internal static bool HasTags(IReadOnlyList<MdfCommonProperty> tags)
        {
            return tags != null && tags.Count > 0;
        }

        // The MD XML of a block: the TX element when there is text, then the common_properties of the tags.
        internal static string Wrap(string root, string text, IReadOnlyList<MdfCommonProperty> tags)
        {
            StringBuilder xml = new StringBuilder("<" + root + ">");
            if (text != null) { xml.Append("<TX>").Append(Escape(text)).Append("</TX>"); }
            if (HasTags(tags))
            {
                xml.Append("<common_properties>");
                foreach (MdfCommonProperty tag in tags) { AppendTag(xml, tag); }
                xml.Append("</common_properties>");
            }
            return xml.Append("</").Append(root).Append('>').ToString();
        }

        private static string Escape(string text)
        {
            StringBuilder xml = new StringBuilder();
            Escape(xml, text);
            return xml.ToString();
        }

        private static void Escape(StringBuilder xml, string text)
        {
            foreach (char c in text)
            {
                switch (c)
                {
                    case '&': xml.Append("&amp;"); break;
                    case '<': xml.Append("&lt;"); break;
                    case '>': xml.Append("&gt;"); break;
                    case '"': xml.Append("&quot;"); break;
                    case '\r': xml.Append("&#13;"); break; // Keep CR through XML end-of-line normalization.
                    default: xml.Append(c); break;
                }
            }
        }
    }
}
