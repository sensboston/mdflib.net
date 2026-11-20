// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;

namespace MdfLib
{
    /// <summary>one MDF 4 attachment (AT block). Metadata only; the payload is read explicitly and external paths stay inert.</summary>
    internal sealed class MdfAttachment
    {
        private byte[] checksum;

        internal MdfAttachment() { }
        internal object Document { get; set; }
        internal ulong BlockAddress { get; set; }
        internal ulong DataOffset { get; set; }

        /// <summary>Gets the zero-based position in the AT chain.</summary>
        public int Index { get; internal set; }
        /// <summary>Gets the decoded file name or path text; an external path is never opened or normalized.</summary>
        public string FileName { get; internal set; }
        /// <summary>Gets the decoded MIME content type, or empty.</summary>
        public string MimeType { get; internal set; }
        /// <summary>Gets the decoded raw comment.</summary>
        public string Comment { get; internal set; }
        /// <summary>Gets the raw attachment flags (bit 0 embedded, bit 1 compressed, bit 2 MD5 valid).</summary>
        public ushort Flags { get; internal set; }
        /// <summary>Gets the raw creator index into the file history, without validation against its count.</summary>
        public ushort CreatorIndex { get; internal set; }
        /// <summary>Gets whether the payload is embedded in this file.</summary>
        public bool IsEmbedded => (Flags & 1) != 0;
        /// <summary>Gets whether the embedded payload is zlib-compressed (decoded by the managed inflater).</summary>
        public bool IsCompressed => IsEmbedded && (Flags & 2) != 0;
        /// <summary>Gets whether the stored MD5 checksum is declared valid.</summary>
        public bool HasChecksum => (Flags & 4) != 0;
        /// <summary>Gets the declared original (uncompressed) payload size in bytes.</summary>
        public ulong OriginalSize { get; internal set; }
        /// <summary>Gets the declared embedded (stored) payload size in bytes; zero for external attachments.</summary>
        public ulong EmbeddedSize { get; internal set; }

        /// <summary>Gets a copy of the stored 16-byte MD5 checksum of the original payload (meaningful only when HasChecksum).</summary>
        /// <returns>A new array on each call.</returns>
        public byte[] GetChecksum() { return (byte[])checksum.Clone(); }

        internal void SetChecksum(byte[] value)
        {
            if (value == null || value.Length != 16) { throw new ArgumentException("An MD5 checksum has 16 bytes.", nameof(value)); }
            checksum = value;
        }
    }
}
