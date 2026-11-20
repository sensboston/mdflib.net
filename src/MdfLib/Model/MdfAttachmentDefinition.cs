// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System.Collections.Generic;

namespace MdfLib
{
    /// <summary>Mutable writer attachment (MDF 4 AT block) configuration, copied by <see cref="MdfWriter.AddAttachment"/>.</summary>
    internal sealed class MdfAttachmentDefinition
    {
        /// <summary>Gets or sets the file name (or path/URL of an external file); required for an external reference.</summary>
        public string FileName { get; set; } = string.Empty;
        /// <summary>Gets or sets the optional MIME content type, for example "text/plain".</summary>
        public string MimeType { get; set; } = string.Empty;
        /// <summary>Gets or sets the optional comment.</summary>
        public string Comment { get; set; } = string.Empty;
        /// <summary>Gets or sets the embedded bytes (stored with an MD5 checksum); null references an external file.</summary>
        public byte[] Data { get; set; }
        /// <summary>Gets or sets whether embedded bytes are stored zlib-compressed.</summary>
        public bool Compress { get; set; }
        /// <summary>Gets or sets the creator index the attachment stores, as 16 bits; v2.3.0 stores it as given.</summary>
        public ushort CreatorIndex { get; set; }
        /// <summary>Gets or sets the common properties of the comment's MD block; null or empty writes plain text.</summary>
        public IReadOnlyList<MdfCommonProperty> CommonProperties { get; set; }
    }
}
