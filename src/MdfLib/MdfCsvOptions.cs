// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
namespace MdfLib
{
    /// <summary>Formatting settings for <see cref="MdfCsvExporter"/>; values are copied when an export starts.</summary>
    internal sealed class MdfCsvOptions
    {
        /// <summary>Gets or sets the exported representation; default physical.</summary>
        public MdfValueRepresentation Representation { get; set; } = MdfValueRepresentation.Physical;
        /// <summary>Gets or sets the field delimiter; default comma. Quote, CR and LF are refused.</summary>
        public char Delimiter { get; set; } = ',';
        /// <summary>Gets or sets whether a header row of channel names is written first; default true.</summary>
        public bool IncludeHeader { get; set; } = true;
        /// <summary>Gets or sets whether a nonempty unit is appended to header names as " [unit]"; default true.</summary>
        public bool IncludeUnits { get; set; } = true;
        /// <summary>Gets or sets whether invalid samples are written as empty fields (default) instead of their stored value.</summary>
        public bool EmptyForInvalid { get; set; } = true;
        /// <summary>Gets or sets the row terminator: "\r\n" (default, RFC 4180) or "\n".</summary>
        public string NewLine { get; set; } = "\r\n";
        /// <summary>Gets or sets the number of samples read per batch; default 1024.</summary>
        public int BatchSize { get; set; } = 1024;
    }
}
