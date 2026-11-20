// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// One common property of an MD block: the e element of common_properties with its name, XML type and text value.
namespace MdfLib
{
    /// <summary>One common property of an MD block: an e element of the block's common_properties.</summary>
    internal sealed class MdfCommonProperty
    {
        /// <summary>Gets or sets the property name (the e element's name attribute); required.</summary>
        public string Name { get; set; }

        /// <summary>Gets or sets the XML type attribute (string, decimal, integer, float, boolean, date, time or dateTime);
        /// null writes no type attribute.</summary>
        public string Type { get; set; }

        /// <summary>Gets or sets the text value of the property.</summary>
        public string Value { get; set; }
    }
}
