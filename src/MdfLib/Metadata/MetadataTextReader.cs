// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Xml;
using MdfLib.Formats;
using MdfLib.Internal;

namespace MdfLib.Metadata
{
    internal sealed class MetadataTextReader
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly BlockResolver resolver;
        private readonly ReaderLimits limits;
        private readonly ReadBudget budget;
        private readonly CancellationToken token;
        private readonly Dictionary<ulong, TextValue> cache = new Dictionary<ulong, TextValue>();

        internal MetadataTextReader(BlockResolver resolver, ReaderLimits limits, ReadBudget budget, CancellationToken token)
        {
            this.resolver = resolver;
            this.limits = limits;
            this.budget = budget;
            this.token = token;
        }

        internal string Name(ulong offset, int depth)
        {
            if (offset == 0) { return string.Empty; }
            budget.CheckDepth(depth, offset);
            BlockHeader block = resolver.Resolve(offset, "##TX", 24, cancellationToken: token);
            return Read(block).Value;
        }

        internal string Comment(ulong offset, int depth)
        {
            return offset == 0 ? string.Empty : Read(ResolveText(offset, depth)).Value;
        }

        internal string Unit(ulong offset, int depth)
        {
            if (offset == 0) { return string.Empty; }
            TextValue text = Read(ResolveText(offset, depth));
            if (!text.IsXml) { return text.Value; }
            if (!text.IsSimpleUnit)
            {
                throw new NotSupportedException("An XML unit must be CNunit/CCunit with one direct, simple TX element.");
            }
            return text.Unit;
        }

        // a conversion reference is NIL (null), a TX block (its text) or a CC block (nested; not read, flagged).
        internal string Reference(ulong offset, int depth, out bool conversion)
        {
            conversion = false;
            if (offset == 0) { return null; }
            budget.CheckDepth(depth, offset);
            BlockHeader block = resolver.Resolve(offset, minimumSize: 24, cancellationToken: token);
            if (block.Type == "##CC")
            {
                conversion = true;
                return null;
            }
            if (block.Type != "##TX")
            {
                throw new MdfFormatException("A conversion reference must be a TX or CC block.", "MDF005", offset, block.Type);
            }
            return Read(block).Value;
        }

        // direct child element texts of a validated MD root (e.g. FHcomment tool_id). Plain TX, another root or an absent
        // element yields empty strings; nested child markup contributes nothing. Only the first occurrence of each name counts.
        internal string[] RootFields(ulong offset, int depth, string root, string[] names)
        {
            string[] result = new string[names.Length];
            for (int i = 0; i < result.Length; i++) { result[i] = string.Empty; }
            if (offset == 0) { return result; }
            BlockHeader block = ResolveText(offset, depth);
            TextValue text = Read(block);
            if (!text.IsXml) { return result; }
            budget.ReserveMetadata(256UL + (ulong)text.Value.Length * 4, block.Offset);
            bool[] seen = new bool[names.Length];
            int current = -1;
            StringBuilder value = null;
            try
            {
                using (StringReader source = new StringReader(text.Value))
                using (XmlReader xml = XmlReader.Create(source, Settings()))
                {
                    while (xml.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        if (xml.NodeType == XmlNodeType.Element && xml.Depth == 0 && xml.LocalName != root) { return result; }
                        if (xml.NodeType == XmlNodeType.Element && xml.Depth == 1)
                        {
                            current = Array.IndexOf(names, xml.LocalName);
                            if (current >= 0 && seen[current]) { current = -1; }
                            if (current >= 0)
                            {
                                seen[current] = true;
                                if (xml.IsEmptyElement) { current = -1; }
                                else { value = new StringBuilder(); }
                            }
                        }
                        else if (xml.NodeType == XmlNodeType.EndElement && xml.Depth == 1 && current >= 0)
                        {
                            result[current] = value.ToString();
                            current = -1;
                        }
                        else if (current >= 0 && xml.Depth == 2 && (xml.NodeType == XmlNodeType.Text || xml.NodeType == XmlNodeType.CDATA
                            || xml.NodeType == XmlNodeType.Whitespace || xml.NodeType == XmlNodeType.SignificantWhitespace))
                        {
                            value.Append(xml.Value);
                        }
                    }
                }
            }
            catch (XmlException error)
            {
                throw new MdfFormatException("Malformed or prohibited XML metadata.", "MDF012", block.Offset, block.Type, error);
            }
            return result;
        }

        // HDcomment: [0] the direct TX element (or a whole plain TX), then the first common_properties/e value of each name.
        // Nested tree/list elements and other roots contribute nothing.
        internal string[] HeaderProperties(ulong offset, int depth, string[] names)
        {
            string[] result = new string[names.Length + 1];
            for (int i = 0; i < result.Length; i++) { result[i] = string.Empty; }
            if (offset == 0) { return result; }
            BlockHeader block = ResolveText(offset, depth);
            TextValue text = Read(block);
            if (!text.IsXml)
            {
                result[0] = text.Value;
                return result;
            }
            budget.ReserveMetadata(256UL + (ulong)text.Value.Length * 4, block.Offset);
            bool[] seen = new bool[result.Length];
            int current = -1, valueDepth = 0;
            bool common = false;
            StringBuilder value = null;
            try
            {
                using (StringReader source = new StringReader(text.Value))
                using (XmlReader xml = XmlReader.Create(source, Settings()))
                {
                    while (xml.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        if (xml.NodeType == XmlNodeType.Element)
                        {
                            if (xml.Depth == 0 && xml.LocalName != "HDcomment") { return result; }
                            if (xml.Depth == 1)
                            {
                                common = xml.LocalName == "common_properties" && !xml.IsEmptyElement;
                                current = xml.LocalName == "TX" && !seen[0] ? 0 : -1;
                                valueDepth = 2;
                            }
                            else if (xml.Depth == 2 && common && xml.LocalName == "e")
                            {
                                int index = Array.IndexOf(names, xml.GetAttribute("name"));
                                current = index >= 0 && !seen[index + 1] ? index + 1 : -1;
                                valueDepth = 3;
                            }
                            if (current >= 0 && xml.Depth == valueDepth - 1)
                            {
                                seen[current] = true;
                                if (xml.IsEmptyElement) { current = -1; }
                                else { value = new StringBuilder(); }
                            }
                        }
                        else if (xml.NodeType == XmlNodeType.EndElement && current >= 0 && xml.Depth == valueDepth - 1)
                        {
                            result[current] = value.ToString();
                            current = -1;
                        }
                        else if (xml.NodeType == XmlNodeType.EndElement && xml.Depth == 1) { common = false; }
                        else if (current >= 0 && xml.Depth == valueDepth && (xml.NodeType == XmlNodeType.Text || xml.NodeType == XmlNodeType.CDATA
                            || xml.NodeType == XmlNodeType.Whitespace || xml.NodeType == XmlNodeType.SignificantWhitespace))
                        {
                            value.Append(xml.Value);
                        }
                    }
                }
            }
            catch (XmlException error)
            {
                throw new MdfFormatException("Malformed or prohibited XML metadata.", "MDF012", block.Offset, block.Type, error);
            }
            return result;
        }

        private XmlReaderSettings Settings()
        {
            return new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = limits.MaximumXmlCharacters,
                MaxCharactersFromEntities = 1024,
                IgnoreWhitespace = false,
                CloseInput = false
            };
        }

        private BlockHeader ResolveText(ulong offset, int depth)
        {
            budget.CheckDepth(depth, offset);
            BlockHeader block = resolver.Resolve(offset, minimumSize: 24, cancellationToken: token);
            if (block.Type != "##TX" && block.Type != "##MD")
            {
                throw new MdfFormatException("Expected a TX or MD metadata block.", "MDF005", offset, block.Type);
            }
            return block;
        }

        private TextValue Read(BlockHeader block)
        {
            token.ThrowIfCancellationRequested();
            if (block.Links.Count != 0)
            {
                throw new MdfFormatException("Text metadata must have no links.", "MDF006", block.Offset, block.Type);
            }
            TextValue cached;
            if (cache.TryGetValue(block.Offset, out cached)) { return cached; }
            ReadBudget.Check(nameof(MdfReaderOptions.MaximumTextBytes), (ulong)limits.MaximumTextBytes, 0, block.DataLength, block.Offset);
            // MaximumTextBytes is a positive int: safe conversion before the payload allocator.
            byte[] bytes = resolver.ReadPayload(block, (int)block.DataLength, token);
            int count = Array.IndexOf(bytes, (byte)0);
            if (count < 0) { count = bytes.Length; }
            string value;
            try
            {
                int characters = Utf8.GetCharCount(bytes, 0, count);
                budget.ReserveMetadata(128UL + (ulong)characters * 2, block.Offset);
                value = Utf8.GetString(bytes, 0, count);
            }
            catch (DecoderFallbackException error)
            {
                throw new MdfFormatException("Invalid UTF-8 metadata.", "MDF009", block.Offset, block.Type, error);
            }
            TextValue result = new TextValue { Value = value, IsXml = block.Type == "##MD" };
            if (result.IsXml) { ValidateXml(block, result); }
            cache.Add(block.Offset, result);
            return result;
        }

        private void ValidateXml(BlockHeader block, TextValue text)
        {
            ReadBudget.Check(nameof(MdfReaderOptions.MaximumXmlCharacters), (ulong)limits.MaximumXmlCharacters, 0, (ulong)text.Value.Length, block.Offset);
            // Account for parser/name-table/string-builder overhead conservatively, not as an exact GC ceiling.
            budget.ReserveMetadata(512UL + (ulong)text.Value.Length * 8, block.Offset);
            XmlReaderSettings settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = limits.MaximumXmlCharacters,
                MaxCharactersFromEntities = 1024,
                IgnoreWhitespace = false,
                CloseInput = false
            };
            bool unitRoot = false;
            bool simple = true;
            bool insideTx = false;
            int txCount = 0;
            StringBuilder unit = null;
            try
            {
                using (StringReader source = new StringReader(text.Value))
                using (XmlReader xml = XmlReader.Create(source, settings))
                {
                    while (xml.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        if (xml.NodeType == XmlNodeType.Element)
                        {
                            ReadBudget.Check(nameof(MdfReaderOptions.MaximumXmlDepth), (ulong)limits.MaximumXmlDepth, 0, (ulong)xml.Depth + 1, block.Offset);
                            if (xml.Depth == 0)
                            {
                                unitRoot = xml.LocalName == "CNunit" || xml.LocalName == "CCunit";
                                if (unitRoot) { unit = new StringBuilder(); }
                            }
                            else if (unitRoot)
                            {
                                if (xml.Depth == 1 && xml.LocalName == "TX")
                                {
                                    txCount++;
                                    insideTx = !xml.IsEmptyElement;
                                }
                                else { simple = false; }
                            }
                        }
                        else if (unitRoot && xml.NodeType == XmlNodeType.EndElement && xml.Depth == 1)
                        {
                            insideTx = false;
                        }
                        else if (unitRoot && (xml.NodeType == XmlNodeType.Text || xml.NodeType == XmlNodeType.CDATA
                            || xml.NodeType == XmlNodeType.Whitespace || xml.NodeType == XmlNodeType.SignificantWhitespace))
                        {
                            if (insideTx && xml.Depth == 2) { unit.Append(xml.Value); }
                            else if (!string.IsNullOrWhiteSpace(xml.Value)) { simple = false; }
                        }
                    }
                }
            }
            catch (XmlException error)
            {
                throw new MdfFormatException("Malformed or prohibited XML metadata.", "MDF012", block.Offset, block.Type, error);
            }
            text.IsSimpleUnit = unitRoot && simple && txCount == 1;
            if (text.IsSimpleUnit) { text.Unit = unit.ToString(); }
        }

        private sealed class TextValue
        {
            internal string Value { get; set; }
            internal bool IsXml { get; set; }
            internal bool IsSimpleUnit { get; set; }
            internal string Unit { get; set; }
        }
    }
}
