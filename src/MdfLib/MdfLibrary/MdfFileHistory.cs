// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// The FH handle of the facade. A writer's handle is the one FH block of a new
// MDF 4 file: the engine writes one FH from the tool fields, the description and the FH time, all decided when the file
// is created, and the facade refuses a change once the file exists. A reader's handle is read only and keeps its own node.
using System;
namespace MdfLibrary
{
    /// <summary>The file-history block (FH) of a file the facade writes or reads.</summary>
    public class MdfFileHistory
    {
        private readonly FacadeContext context_;
        private readonly PendingHistory entry_;

        /// <summary>The handle of the writer's single FH block.</summary>
        internal MdfFileHistory(FacadeContext context)
        {
            context_ = context;
        }

        /// <summary>The handle of one FH block read from a file.</summary>
        internal MdfFileHistory(FacadeContext context, PendingHistory entry)
        {
            context_ = context;
            entry_ = entry;
        }

        /// <summary>The comment of the history entry.</summary>
        public string Description
        {
            get { return entry_ != null ? entry_.Description : context_.Header.HistoryDescription; }
            set
            {
                Writable();
                context_.Header.HistoryDescription = value ?? string.Empty;
            }
        }

        /// <summary>The time of the history entry as a timestamp in UTC.</summary>
        public IMdfFileTimestamp GetStartTime()
        {
            return new MdfFileUtcTimestamp(Time);
        }

        /// <summary>The position of the entry in the file.</summary>
        public long Index
        {
            get { return entry_ != null ? entry_.Index : 0; }
        }

        /// <summary>The MD block of the history entry's FHcomment. The writer's own block
        /// gets it on the first call and writes its tags with the block; a block read from a file has none to give.</summary>
        public MdfMetaData MetaData
        {
            get
            {
                if (entry_ != null) { return null; }
                Writable();
                if (context_.Header.HistoryMetadata == null) { context_.Header.HistoryMetadata = new MdfMetaData(); }
                return context_.Header.HistoryMetadata;
            }
        }

        /// <summary>Sets the time of a new history entry from a timestamp.</summary>
        public void SetStartTime(IMdfTimeStamp timestamp)
        {
            if (timestamp == null) { throw new ArgumentNullException("timestamp"); }
            MdfTimezoneTimestamp timezone = timestamp as MdfTimezoneTimestamp;
            if (timezone != null && (timezone.TimezoneOffsetMin != 0 || timezone.DstOffsetMin != 0))
            { throw new NotSupportedException("The engine stores a single UTC time, so a timezone timestamp with non-zero offsets has nowhere to go."); }
            Time = FacadeContext.NanosecondsOf(timestamp);
        }

        /// <summary>The time of the history entry in nanoseconds since 1970 UTC.</summary>
        public ulong Time
        {
            get { return entry_ != null ? entry_.Time : context_.Header.HistoryTime ?? context_.Header.StartTime; }
            set
            {
                Writable();
                context_.Header.HistoryTime = value;
            }
        }

        /// <summary>The tool that wrote the history entry.</summary>
        public string ToolName
        {
            get { return entry_ != null ? entry_.ToolName : context_.Header.ToolName; }
            set
            {
                Writable();
                context_.Header.ToolName = value ?? string.Empty;
            }
        }

        /// <summary>The vendor of the tool.</summary>
        public string ToolVendor
        {
            get { return entry_ != null ? entry_.ToolVendor : context_.Header.ToolVendor; }
            set
            {
                Writable();
                context_.Header.ToolVendor = value ?? string.Empty;
            }
        }

        /// <summary>The version of the tool.</summary>
        public string ToolVersion
        {
            get { return entry_ != null ? entry_.ToolVersion : context_.Header.ToolVersion; }
            set
            {
                Writable();
                context_.Header.ToolVersion = value ?? string.Empty;
            }
        }

        /// <summary>The user that made the change.</summary>
        public string UserName
        {
            get { return entry_ != null ? entry_.UserName : context_.Header.UserName; }
            set
            {
                Writable();
                context_.Header.UserName = value ?? string.Empty;
            }
        }

        // The engine has no FH for MDF 3.30, a created file keeps the FH it was given at creation, and a read entry never
        // changes; the refusal says which of the three applies.
        private void Writable()
        {
            if (entry_ != null) { throw new NotSupportedException("A file-history block read from a file is read-only; the reader never writes."); }
            if (context_.IsMdf3) { throw new NotSupportedException("MDF 3.30 has no file-history block."); }
            context_.RejectCreated();
        }
    }
}
