// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// One entry of an MD block. The tag keeps the value of the setter it was
// given last, as the wrapper's tag does; the typed getters answer the type default for any other kind.
using System;
namespace MdfLibrary
{
    /// <summary>The kind of value an MD tag holds.</summary>
    public enum ETagDataType : byte
    {
        /// <summary>A text value.</summary>
        StringType = 0,

        /// <summary>A decimal value, stored as text.</summary>
        DecimalType = 1,

        /// <summary>A signed or unsigned integer value.</summary>
        IntegerType = 2,

        /// <summary>A floating-point value.</summary>
        FloatType = 3,

        /// <summary>A true or false value.</summary>
        BooleanType = 4,

        /// <summary>A calendar date.</summary>
        DateType = 5,

        /// <summary>A time of day.</summary>
        TimeType = 6,

        /// <summary>A date and time.</summary>
        DateTimeType = 7,
    }

    /// <summary>One name/value entry of an MD block.</summary>
    public class MdfETag
    {
        private ETagDataType dataType_;
        private string name_ = string.Empty, description_ = string.Empty, language_ = string.Empty;
        private string unit_ = string.Empty, unitRef_ = string.Empty, type_ = string.Empty;
        private bool readOnly_;
        private object value_;

        /// <summary>Creates an empty string tag; its name and value are set through the properties.</summary>
        public MdfETag()
        {
        }

        /// <summary>The type of the value the tag holds.</summary>
        public ETagDataType DataType
        {
            get { return dataType_; }
            set { dataType_ = value; }
        }

        /// <summary>The description of the tag.</summary>
        public string Description
        {
            get { return description_; }
            set { description_ = value ?? string.Empty; }
        }

        /// <summary>Releases the value the tag holds; the tag itself stays usable.</summary>
        public void Dispose()
        {
            value_ = null;
        }

        /// <summary>The language of the description and the value, as a BCP-47 tag.</summary>
        public string Language
        {
            get { return language_; }
            set { language_ = value ?? string.Empty; }
        }

        /// <summary>The name of the tag; the header common properties are looked up by it.</summary>
        public string Name
        {
            get { return name_; }
            set { name_ = value ?? string.Empty; }
        }

        /// <summary>True when the tag must not be changed by a reader or an editor.</summary>
        public bool ReadOnly
        {
            get { return readOnly_; }
            set { readOnly_ = value; }
        }

        /// <summary>The type name of the tag, free text in the MD block.</summary>
        public string Type
        {
            get { return type_; }
            set { type_ = value ?? string.Empty; }
        }

        /// <summary>The unit of the value.</summary>
        public string Unit
        {
            get { return unit_; }
            set { unit_ = value ?? string.Empty; }
        }

        /// <summary>A reference to the unit definition, as in the MD block.</summary>
        public string UnitRef
        {
            get { return unitRef_; }
            set { unitRef_ = value ?? string.Empty; }
        }

        /// <summary>The value as a boolean; the default for a tag that holds another kind.</summary>
        public bool ValueAsBoolean
        {
            get { return value_ is bool && (bool)value_; }
            set { value_ = value; }
        }

        /// <summary>The value as a double; the default for a tag that holds another kind.</summary>
        public double ValueAsFloat
        {
            get { return value_ is double ? (double)value_ : 0.0; }
            set { value_ = value; }
        }

        /// <summary>The value as a signed integer; the default for a tag that holds another kind.</summary>
        public long ValueAsSigned
        {
            get { return value_ is long ? (long)value_ : 0L; }
            set { value_ = value; }
        }

        /// <summary>The value as text; empty for a tag that holds another kind.</summary>
        public string ValueAsString
        {
            get { return value_ as string ?? string.Empty; }
            set { value_ = value ?? string.Empty; }
        }

        /// <summary>The value as an unsigned integer; the default for a tag that holds another kind.</summary>
        public ulong ValueAsUnsigned
        {
            get { return value_ is ulong ? (ulong)value_ : 0UL; }
            set { value_ = value; }
        }

        /// <summary>The DataType the tag was given; the writer checks it before it stores a header value.</summary>
        internal ETagDataType Kind { get { return dataType_; } }

        /// <summary>True when the tag holds a text value, whatever its DataType says.</summary>
        internal bool HoldsText { get { return value_ is string; } }

        /// <summary>The engine form of this tag: the XML type of its DataType (the names v2.3.0 writes: string,
        /// decimal, integer, float, boolean, date, time, dateTime) and the text of its value.</summary>
        internal MdfLib.MdfCommonProperty ToWriterTag()
        {
            MdfLib.MdfCommonProperty result = new MdfLib.MdfCommonProperty { Name = Name, Value = TextValue() };
            switch (dataType_)
            {
                case ETagDataType.StringType: result.Type = "string"; break;
                case ETagDataType.DecimalType: result.Type = "decimal"; break;
                case ETagDataType.IntegerType: result.Type = "integer"; break;
                case ETagDataType.FloatType: result.Type = "float"; break;
                case ETagDataType.BooleanType: result.Type = "boolean"; break;
                case ETagDataType.DateType: result.Type = "date"; break;
                case ETagDataType.TimeType: result.Type = "time"; break;
                case ETagDataType.DateTimeType: result.Type = "dateTime"; break;
            }
            return result;
        }

        private string TextValue()
        {
            if (value_ is string) { return (string)value_; }
            if (value_ is bool) { return (bool)value_ ? "true" : "false"; }
            if (value_ is long) { return ((long)value_).ToString(System.Globalization.CultureInfo.InvariantCulture); }
            if (value_ is ulong) { return ((ulong)value_).ToString(System.Globalization.CultureInfo.InvariantCulture); }
            if (value_ is double) { return ((double)value_).ToString("R", System.Globalization.CultureInfo.InvariantCulture); }
            return string.Empty;
        }
    }
}
