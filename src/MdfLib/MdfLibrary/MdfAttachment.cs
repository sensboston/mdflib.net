// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// The attachment handle of the facade. A writer's attachment is an external
// reference (the engine stores its name and type); a reader's attachment is read only and keeps the flags and checksum.
using System;
namespace MdfLibrary
{
    /// <summary>An attachment block (AT) of a file the facade writes or reads.</summary>
    public class MdfAttachment
    {
        private readonly PendingAttachment pending_;

        // Instances come from the facade factories; the wrapper has no public constructor
        // either, so the declaration layer keeps the type non-constructible by name.
        /// <summary>The handle of one attachment; the factories create it with the pending node.</summary>
        internal MdfAttachment(PendingAttachment pending)
        {
            pending_ = pending;
        }

        /// <summary>The pending node the handle reads and writes.</summary>
        internal PendingAttachment Pending
        {
            get { return pending_; }
        }

        /// <summary>True when the attachment data is stored compressed.</summary>
        public bool Compressed
        {
            get
            {
                return pending_.Compressed;
            }
            set
            {
                Writable();
                pending_.Compressed = value;
            }
        }

        /// <summary>Creates the MD block of the attachment's comment, or reports the one it has.
        /// Its tags are written with the comment.</summary>
        public MdfMetaData CreateMetaData()
        {
            Writable();
            if (pending_.Metadata == null) { pending_.Metadata = new MdfMetaData(); }
            return pending_.Metadata;
        }

        /// <summary>The creator index the file stores for the attachment.</summary>
        public ushort CreatorIndex
        {
            get
            {
                return pending_.CreatorIndex;
            }
            set
            {
                Writable();
                pending_.CreatorIndex = value;
            }
        }

        /// <summary>True when the attachment data is stored inside the file.</summary>
        public bool Embedded
        {
            get
            {
                return pending_.Embedded;
            }
            set
            {
                Writable();
                pending_.Embedded = value;
            }
        }

        /// <summary>The file name of the attachment.</summary>
        public string FileName
        {
            get
            {
                return pending_.FileName;
            }
            set
            {
                Writable();
                pending_.FileName = value ?? string.Empty;
            }
        }

        /// <summary>The MIME type of the attachment.</summary>
        public string FileType
        {
            get
            {
                return pending_.MimeType;
            }
            set
            {
                Writable();
                pending_.MimeType = value ?? string.Empty;
            }
        }

        /// <summary>The position of the attachment in the file.</summary>
        public long Index
        {
            get
            {
                return pending_.Index;
            }
        }

        /// <summary>The MD5 of the attachment data as hexadecimal text, empty without a checksum.</summary>
        public string Md5
        {
            get
            {
                return pending_.Md5;
            }
        }

        /// <summary>The MD block of the attachment's comment, or null when it has none.</summary>
        public MdfMetaData MetaData
        {
            get
            {
                return pending_.Metadata;
            }
        }

        // A reader's attachment never changes, and a written one is fixed when the tree is flushed.
        private void Writable()
        {
            if (pending_.FromFile) { throw new NotSupportedException("An attachment read from a file is read-only; the reader never writes."); }
            pending_.Context.RejectFlushed();
        }
    }
}
