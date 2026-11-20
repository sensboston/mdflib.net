// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace MdfLib
{
    /// <summary>the document-level MDF model beyond data groups: header comment, file history, attachments, events and channel hierarchy.</summary>
    /// <remarks>An immutable snapshot that survives reader disposal. MDF 3 files have only a header comment; their lists are empty.
    /// Attachment payloads are read explicitly through <see cref="MdfReader.ReadAttachment"/>; external files are never opened.</remarks>
    internal sealed class MdfFileModel
    {
        internal static readonly IReadOnlyList<MdfAttachment> NoAttachments = new ReadOnlyCollection<MdfAttachment>(new MdfAttachment[0]);
        private Dictionary<MdfChannel, IReadOnlyList<MdfAttachment>> channelAttachments;

        internal MdfFileModel() { }

        /// <summary>Gets the decoded raw HD comment: an MDF 4 TX/validated XML (HDcomment) text, or MDF 3's code-page TX text; empty when absent.</summary>
        public string Comment { get; internal set; }
        /// <summary>Gets the measurement description: the MDF 4 HDcomment TX element (or the whole plain TX comment), or MDF 3's
        /// HD comment text; empty when absent.</summary>
        public string Description { get; internal set; } = string.Empty;
        /// <summary>Gets the author: the MDF 4 HDcomment common property "author" or the MDF 3 HD field; empty when absent.</summary>
        public string Author { get; internal set; } = string.Empty;
        /// <summary>Gets the organization: the MDF 4 common property "department" or the MDF 3 HD field; empty when absent.</summary>
        public string Organization { get; internal set; } = string.Empty;
        /// <summary>Gets the project: the MDF 4 common property "project" or the MDF 3 HD field; empty when absent.</summary>
        public string Project { get; internal set; } = string.Empty;
        /// <summary>Gets the subject: the MDF 4 common property "subject" or the MDF 3 HD field; empty when absent.</summary>
        public string Subject { get; internal set; } = string.Empty;
        /// <summary>Gets the HD common property Measurement.UUID, or empty when the file has none.</summary>
        public string MeasurementId { get; internal set; } = string.Empty;

        /// <summary>Gets the HD common property Recorder.UUID, or empty when the file has none.</summary>
        public string RecorderId { get; internal set; } = string.Empty;

        /// <summary>Gets the HD common property Recorder.FileIndex, or zero when the file has none.</summary>
        public long RecorderIndex { get; internal set; }

        /// <summary>Gets the MDF 4 file-history entries in chain order (FH); empty for MDF 3.</summary>
        public IReadOnlyList<MdfFileHistory> FileHistory { get; internal set; }
        /// <summary>Gets the MDF 4 attachments in chain order (AT); indices are the format's attachment indices.</summary>
        public IReadOnlyList<MdfAttachment> Attachments { get; internal set; }
        /// <summary>Gets the MDF 4 events in chain order (EV), with resolved parent, range, scope and attachment references.</summary>
        public IReadOnlyList<MdfEvent> Events { get; internal set; }
        /// <summary>Gets the top-level MDF 4 channel-hierarchy nodes (CH) in chain order.</summary>
        public IReadOnlyList<MdfChannelHierarchy> ChannelHierarchy { get; internal set; }

        /// <summary>Gets the attachments referenced by an MDF 4 channel (cn_at_reference), in wire order.</summary>
        /// <param name="channel">A channel of the catalog read by the same reader.</param>
        /// <returns>The resolved attachments; empty when the channel has none or belongs to another catalog.</returns>
        /// <exception cref="System.ArgumentNullException">The channel is null.</exception>
        public IReadOnlyList<MdfAttachment> GetChannelAttachments(MdfChannel channel)
        {
            if (channel == null) { throw new System.ArgumentNullException(nameof(channel)); }
            IReadOnlyList<MdfAttachment> result;
            return channelAttachments != null && channelAttachments.TryGetValue(channel, out result) ? result : NoAttachments;
        }

        internal void SetChannelAttachments(Dictionary<MdfChannel, IReadOnlyList<MdfAttachment>> value) { channelAttachments = value; }
    }
}
