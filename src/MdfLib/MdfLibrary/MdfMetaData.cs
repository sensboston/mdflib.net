// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// The MD block as a list of tags. The header's tags reach the engine as
// common properties (the four HD fields, and the others through MdfWriterOptions.CommonProperties); a block's tags
// reach its definition. A standalone MdfMetaData (public constructor) is not attached to any block, so nothing is
// written from it.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
namespace MdfLibrary
{
    /// <summary>A list of MD tags.</summary>
    public class MdfMetaData
    {
        // The HD common properties the engine writes, by the names the engine's header XML uses.
        private static readonly string[] HeaderNames = { "author", "department", "project", "subject" };

        // Set only for the header's own MD block: its tags are written when the file is created.
        private readonly FacadeContext owner_;
        private readonly List<MdfETag> tags_ = new List<MdfETag>();

        /// <summary>Creates an MD block. A block created through a CreateMetaData member writes its tags with that block;
        /// a standalone block is not attached to any file.</summary>
        public MdfMetaData()
        {
        }

        /// <summary>The header's own MD block, which is written with the file.</summary>
        internal MdfMetaData(FacadeContext owner)
        {
            owner_ = owner;
        }

        /// <summary>Adds a common property; a tag with the same name is replaced.</summary>
        public void AddCommonProperty(MdfETag tag)
        {
            if (tag == null) { throw new ArgumentNullException("tag"); }
            Store(tag);
        }

        /// <summary>The common properties: the tags whose names are the HD common property names.</summary>
        public MdfETag[] CommonProperties
        {
            get
            {
                return tags_.FindAll(tag => Array.IndexOf(HeaderNames, tag.Name) >= 0).ToArray();
            }
            set
            {
                if (value == null) { throw new ArgumentNullException("value"); }
                Changing();
                foreach (MdfETag tag in value)
                {
                    if (tag == null) { throw new ArgumentNullException("value"); }
                        }
                tags_.RemoveAll(tag => Array.IndexOf(HeaderNames, tag.Name) >= 0);
                foreach (MdfETag tag in value) { Store(tag); }
            }
        }

        /// <summary>The tag with the given name, or null when there is none.</summary>
        public MdfETag GetCommonProperty(string name)
        {
            return Find(name);
        }

        /// <summary>The text of the tag with the given name; null when there is none, and setting null removes it.</summary>
        public string this[string key]
        {
            get
            {
                MdfETag tag = Find(key);
                return tag == null ? null : tag.ValueAsString;
            }
            set
            {
                if (key == null) { throw new ArgumentNullException("key"); }
                if (value == null)
                {
                    Changing();
                    tags_.RemoveAll(tag => tag.Name == key);
                    return;
                }
                Store(new MdfETag { Name = key, DataType = ETagDataType.StringType, ValueAsString = value });
            }
        }

        /// <summary>All tags of the block, in the order they were added.</summary>
        public MdfETag[] Properties
        {
            get { return tags_.ToArray(); }
        }

        /// <summary>The XML of the block's common properties: the snippet the tags make. Setting it replaces the tags
        /// with the e elements of its common_properties (name, type and value); the snippet's TX text is refused, because
        /// the comment of the owning block is set through its Description. Setting null removes all tags.</summary>
        public string XmlSnippet
        {
            get
            {
                if (tags_.Count == 0) { return null; }
                StringBuilder xml = new StringBuilder("<common_properties>");
                foreach (MdfETag tag in tags_)
                {
                    MdfLib.MdfCommonProperty property = tag.ToWriterTag();
                    xml.Append("<e name=\"").Append(SecurityElementText(property.Name)).Append('"');
                    if (property.Type != null) { xml.Append(" type=\"").Append(property.Type).Append('"'); }
                    xml.Append('>').Append(SecurityElementText(property.Value)).Append("</e>");
                }
                return xml.Append("</common_properties>").ToString();
            }
            set
            {
                if (value == null)
                {
                    Changing();
                    tags_.Clear();
                    return;
                }
                List<MdfETag> parsed = ParseSnippet(value);
                Changing();
                tags_.Clear();
                foreach (MdfETag tag in parsed) { Store(tag); }
            }
        }

        private static string SecurityElementText(string text)
        {
            return System.Security.SecurityElement.Escape(text ?? string.Empty);
        }

        // Reads a snippet with its e elements and nothing else: DTDs are refused, and so is every element but
        // common_properties, whose children must be e elements with a name and an optional type.
        private static List<MdfETag> ParseSnippet(string snippet)
        {
            List<MdfETag> result = new List<MdfETag>();
            XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            try
            {
                using (XmlReader xml = XmlReader.Create(new StringReader(snippet), settings))
                {
                    if (!xml.Read()) { throw new ArgumentException("The XML snippet is empty.", "value"); }
                    if (xml.NodeType != XmlNodeType.Element) { throw new ArgumentException("The XML snippet has no root element.", "value"); }
                    while (xml.Read())
                    {
                        if (xml.NodeType == XmlNodeType.EndElement && xml.Depth == 0) { break; }
                        if (xml.NodeType != XmlNodeType.Element) { continue; }
                        if (xml.Depth == 1 && xml.LocalName == "common_properties") { ReadProperties(xml, result); continue; }
                        throw new NotSupportedException("The XML snippet element " + xml.LocalName + " has no tag form in the managed writer; only common_properties is read. A TX text belongs in the owning block's Description.");
                    }
                }
            }
            catch (XmlException error)
            {
                throw new ArgumentException("The XML snippet is not well formed: " + error.Message, "value", error);
            }
            return result;
        }

        private static void ReadProperties(XmlReader xml, List<MdfETag> result)
        {
            if (xml.IsEmptyElement) { return; }
            while (xml.Read())
            {
                if (xml.NodeType == XmlNodeType.EndElement && xml.Depth == 1) { return; }
                if (xml.NodeType != XmlNodeType.Element) { continue; }
                if (xml.LocalName != "e") { throw new NotSupportedException("The common_properties element " + xml.LocalName + " is not an e element."); }
                string name = xml.GetAttribute("name");
                if (string.IsNullOrEmpty(name)) { throw new ArgumentException("A common property needs a name attribute.", "value"); }
                string type = xml.GetAttribute("type") ?? "string";
                result.Add(TagOf(name, type, ContentOf(xml)));
            }
        }

        // The text of the e element the reader is on. The reader stops on the element's own end tag (depth 2), so the
        // next e element is still ahead of the outer loop; ReadElementContentAsString would step past it.
        private static string ContentOf(XmlReader xml)
        {
            if (xml.IsEmptyElement) { return string.Empty; }
            StringBuilder text = new StringBuilder();
            while (xml.Read())
            {
                if (xml.NodeType == XmlNodeType.EndElement && xml.Depth == 2) { break; }
                if (xml.NodeType == XmlNodeType.Element) { throw new NotSupportedException("A common property holds text only; the element " + xml.LocalName + " inside it has no tag form."); }
                if (xml.NodeType == XmlNodeType.Text || xml.NodeType == XmlNodeType.CDATA || xml.NodeType == XmlNodeType.Whitespace || xml.NodeType == XmlNodeType.SignificantWhitespace)
                {
                    text.Append(xml.Value);
                }
            }
            return text.ToString();
        }

        private static MdfETag TagOf(string name, string type, string value)
        {
            switch (type)
            {
                case "string": return new MdfETag { Name = name, DataType = ETagDataType.StringType, ValueAsString = value };
                case "integer":
                    return new MdfETag { Name = name, DataType = ETagDataType.IntegerType,
                        ValueAsSigned = long.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) };
                case "float":
                    return new MdfETag { Name = name, DataType = ETagDataType.FloatType,
                        ValueAsFloat = double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture) };
                case "decimal":
                    return new MdfETag { Name = name, DataType = ETagDataType.DecimalType,
                        ValueAsFloat = double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture) };
                case "boolean":
                    if (value != "true" && value != "false" && value != "1" && value != "0")
                    { throw new ArgumentException("A boolean common property is true, false, 1 or 0.", "value"); }
                    return new MdfETag { Name = name, DataType = ETagDataType.BooleanType, ValueAsBoolean = value == "true" || value == "1" };
                default:
                    throw new NotSupportedException("The common property type " + type + " has no tag form in the managed writer.");
            }
        }

        /// <summary>The tags as the engine writes them into the MD payload of the block they belong to; null when there are none.</summary>
        internal System.Collections.Generic.List<MdfLib.MdfCommonProperty> WriterTags()
        {
            if (tags_.Count == 0) { return null; }
            System.Collections.Generic.List<MdfLib.MdfCommonProperty> result = new System.Collections.Generic.List<MdfLib.MdfCommonProperty>(tags_.Count);
            foreach (MdfETag tag in tags_) { result.Add(tag.ToWriterTag()); }
            return result;
        }

        /// <summary>The header's tags beyond the four HD fields the engine stores, as the engine writes them; null when none.</summary>
        internal System.Collections.Generic.List<MdfLib.MdfCommonProperty> ExtraWriterTags()
        {
            System.Collections.Generic.List<MdfLib.MdfCommonProperty> result = new System.Collections.Generic.List<MdfLib.MdfCommonProperty>();
            foreach (MdfETag tag in tags_) { if (Array.IndexOf(HeaderNames, tag.Name) < 0) { result.Add(tag.ToWriterTag()); } }
            return result.Count == 0 ? null : result;
        }

        /// <summary>The value of the tag with the given name as a double; 0 when there is none or it holds another kind.</summary>
        public double get_PropertyAsFloat(string index)
        {
            MdfETag tag = Find(index);
            return tag == null ? 0.0 : tag.ValueAsFloat;
        }

        /// <summary>The value of the tag with the given name as text; empty when there is none.</summary>
        public string get_PropertyAsString(string index)
        {
            MdfETag tag = Find(index);
            return tag == null ? string.Empty : tag.ValueAsString;
        }

        /// <summary>Sets the tag with the given name to a float value.</summary>
        public void set_PropertyAsFloat(string index, double prop)
        {
            if (index == null) { throw new ArgumentNullException("index"); }
            Store(new MdfETag { Name = index, DataType = ETagDataType.FloatType, ValueAsFloat = prop });
        }

        /// <summary>Sets the tag with the given name to a text value.</summary>
        public void set_PropertyAsString(string index, string prop)
        {
            if (index == null) { throw new ArgumentNullException("index"); }
            Store(new MdfETag { Name = index, DataType = ETagDataType.StringType, ValueAsString = prop ?? string.Empty });
        }

        /// <summary>The text of one HD common property; null when the block has no such tag. Like v2.3.0 (GetCommonProperty
        /// reads the e element's text whatever its type attribute says), a tag of another kind gives its text value.</summary>
        internal string TextOf(string name)
        {
            MdfETag tag = Find(name);
            return tag == null ? null : tag.ToWriterTag().Value;
        }

        private MdfETag Find(string name)
        {
            if (name == null) { return null; }
            return tags_.Find(tag => tag.Name == name);
        }

        private void Store(MdfETag tag)
        {
            Changing();
            tags_.RemoveAll(existing => existing.Name == tag.Name);
            tags_.Add(tag);
        }

        // A header block that the file already carries cannot take new tags, as the header itself cannot.
        private void Changing()
        {
            if (owner_ != null) { owner_.RejectCreated(); }
        }

    }
}
