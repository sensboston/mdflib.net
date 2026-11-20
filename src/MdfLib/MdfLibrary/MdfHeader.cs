// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// The header handle of the writer. The wrapper mutates the live HD
// block of its file before InitMeasurement; the managed engine takes the whole header as MdfWriterOptions when
// it creates the file, so the facade collects the same values here and refuses them once the file exists.
using System;
namespace MdfLibrary
{
    /// <summary>The HD block of a file.</summary>
    public class MdfHeader
    {
        private readonly FacadeContext context_;

        /// <summary>The header handle of one file; the wrapper creates it with the file, so no public
        /// constructor exists on either side.</summary>
        internal MdfHeader(FacadeContext context)
        {
            context_ = context;
        }

        /// <summary>Adds an attachment to the file (MDF 4 only).</summary>
        public MdfAttachment CreateAttachment()
        {
            return context_.File.CreateAttachment();
        }

        /// <summary>Adds a data group with its first channel group to the file.</summary>
        public MdfDataGroup CreateDataGroup()
        {
            return context_.File.CreateDataGroup();
        }

        /// <summary>Adds an event to the file (MDF 4 only).</summary>
        public MdfEvent CreateEvent()
        {
            context_.RejectFlushed();
            if (context_.IsMdf3) { throw new NotSupportedException("MDF 3.30 has no events."); }
            PendingEvent evt = new PendingEvent(context_);
            context_.Header.Events.Add(evt);
            return PendingEvent.FacadeOf(evt);
        }

        /// <summary>The file-history block of the new file. The engine writes one FH block,
        /// so a second call returns the same object; the tool fields and the FH time are fixed when the file is created.</summary>
        public MdfFileHistory CreateFileHistory()
        {
            if (context_.IsMdf3) { throw new NotSupportedException("MDF 3.30 has no file-history block."); }
            if (context_.Header.History == null)
            {
                context_.RejectCreated();
                context_.Header.History = new MdfFileHistory(context_);
            }
            return context_.Header.History;
        }

        /// <summary>The header's MD block. Its common properties author, department, project
        /// and subject are written with the file; the same object is returned on every call.</summary>
        public MdfMetaData CreateMetaData()
        {
            return context_.Header.Metadata;
        }

        /// <summary>The start time as a typed timestamp. The engine stores one UTC
        /// time, so the UTC form is what the file will carry.</summary>
        public IMdfFileTimestamp GetStartTime()
        {
            return new MdfFileUtcTimestamp(context_.Header.StartTime);
        }

        /// <summary>The start time taken from a timestamp object. Offsets have
        /// nowhere to go in the managed writer, and the time must be set before the file is created.</summary>
        public void SetStartTime(IMdfTimeStamp timestamp)
        {
            if (timestamp == null) { throw new ArgumentNullException("timestamp"); }
            MdfTimezoneTimestamp timezone = timestamp as MdfTimezoneTimestamp;
            if (timezone != null && (timezone.TimezoneOffsetMin != 0 || timezone.DstOffsetMin != 0))
            { throw new NotSupportedException("The managed writer stores a single UTC start time, so a timezone timestamp with non-zero offsets has nowhere to go."); }
            StartTime = FacadeContext.NanosecondsOf(timestamp);
        }

        /// <summary>The attachments of the file, in the order they were created.</summary>
        public MdfAttachment[] Attachments
        {
            get
            {
                MdfAttachment[] result = new MdfAttachment[context_.Header.Attachments.Count];
                for (int i = 0; i < result.Length; i++) { result[i] = PendingAttachment.FacadeOf(context_.Header.Attachments[i]); }
                return result;
            }
        }

        /// <summary>The author, written as the MDF 4 common property and the MDF 3 HD
        /// field. The engine takes it when the file is created.</summary>
        public string Author
        {
            get
            {
                return context_.Header.Author;
            }
            set
            {
                context_.RejectCreated();
                context_.Header.Author = value ?? string.Empty;
            }
        }

        /// <summary>The data groups of the file this header belongs to.</summary>
        public MdfDataGroup[] DataGroups
        {
            get
            {
                return context_.File.DataGroups;
            }
        }

        /// <summary>The department, written as the MDF 4 common property and the MDF 3
        /// HD field.</summary>
        public string Department
        {
            get
            {
                return context_.Header.Department;
            }
            set
            {
                context_.RejectCreated();
                context_.Header.Department = value ?? string.Empty;
            }
        }

        /// <summary>The measurement description: the MDF 4 HDcomment or the MDF 3 HD
        /// comment field.</summary>
        public string Description
        {
            get
            {
                return context_.Header.Description;
            }
            set
            {
                context_.RejectCreated();
                context_.Header.Description = value ?? string.Empty;
            }
        }

        /// <summary>The events of the file, in the order they were created.</summary>
        public MdfEvent[] Events
        {
            get
            {
                MdfEvent[] result = new MdfEvent[context_.Header.Events.Count];
                for (int i = 0; i < result.Length; i++) { result[i] = PendingEvent.FacadeOf(context_.Header.Events[i]); }
                return result;
            }
        }

        /// <summary>The FH blocks. The managed writer creates its own single entry at
        /// file creation and reports nothing back, so this list stays empty.</summary>
        public MdfFileHistory[] FileHistories
        {
            get
            {
                // A writer's single block is the one the header holds; a reader's blocks are the ones the file has.
                if (context_.Header.History != null) { return new MdfFileHistory[] { context_.Header.History }; }
                MdfFileHistory[] result = new MdfFileHistory[context_.Header.Histories.Count];
                for (int i = 0; i < result.Length; i++) { result[i] = PendingHistory.FacadeOf(context_, context_.Header.Histories[i]); }
                return result;
            }
        }

        /// <summary>The unique id of the live header object, not a file field. The
        /// facade numbers the objects it creates.</summary>
        public long Index
        {
            get
            {
                if (context_.Header.Index == 0) { context_.Header.Index = context_.NewIndex(); }
                return context_.Header.Index;
            }
        }

        /// <summary>Whether a start angle is stored. The managed writer never writes
        /// one, so the answer is always false.</summary>
        public bool IsStartAngleUsed
        {
            get
            {
                return context_.Header.StartAngle.HasValue;
            }
        }

        /// <summary>Whether a start distance is stored, which the managed writer never
        /// writes.</summary>
        public bool IsStartDistanceUsed
        {
            get
            {
                return context_.Header.StartDistance.HasValue;
            }
        }

        /// <summary>The data group the writer adds to next, or null while the file has
        /// none. The wrapper hands out a stub object for an empty list; the facade reports null instead, so a
        /// caller cannot collect channels into a group that does not exist.</summary>
        public MdfDataGroup LastDataGroup
        {
            get
            {
                if (context_.DataGroups.Count == 0) { return null; }
                return PendingDataGroup.FacadeOf(context_.DataGroups[context_.DataGroups.Count - 1]);
            }
        }

        /// <summary>The measurement id (the MDF 4 measurement_id common property). The
        /// managed writer has no option for it.</summary>
        public string MeasurementId
        {
            get
            {
                return context_.Header.MeasurementId;
            }
            set
            {
                context_.RejectCreated();
                if (context_.IsMdf3) { return; } // MDF 3.30 has no common properties; the wrapper ignores the value too.
                context_.Header.MeasurementId = value ?? string.Empty;
            }
        }

        /// <summary>The MD block of the header, which the managed writer does not
        /// create.</summary>
        public MdfMetaData MetaData
        {
            get
            {
                return context_.Header.Metadata;
            }
        }

        /// <summary>The project.</summary>
        public string Project
        {
            get
            {
                return context_.Header.Project;
            }
            set
            {
                context_.RejectCreated();
                context_.Header.Project = value ?? string.Empty;
            }
        }

        /// <summary>The recorder id, which the managed writer has no option for.</summary>
        public string RecorderId
        {
            get
            {
                return context_.Header.RecorderId;
            }
            set
            {
                context_.RejectCreated();
                if (context_.IsMdf3) { return; } // MDF 3.30 has no common properties; the wrapper ignores the value too.
                context_.Header.RecorderId = value ?? string.Empty;
            }
        }

        /// <summary>The recorder index, which the managed writer has no option for.</summary>
        public long RecorderIndex
        {
            get
            {
                return context_.Header.RecorderIndex ?? 0;
            }
            set
            {
                context_.RejectCreated();
                if (context_.IsMdf3) { return; } // MDF 3.30 has no common properties; the wrapper ignores the value too.
                context_.Header.RecorderIndex = value;
            }
        }

        /// <summary>The start angle of the HD block, always zero because the writer
        /// stores none.</summary>
        public double StartAngle
        {
            get
            {
                return context_.Header.StartAngle ?? 0.0;
            }
            set
            {
                context_.RejectCreated();
                if (context_.IsMdf3) { return; } // MDF 3.30 has no HD start angle; the wrapper ignores the value too.
                context_.Header.StartAngle = value;
            }
        }

        /// <summary>The start distance of the HD block, always zero for the same
        /// reason as the start angle.</summary>
        public double StartDistance
        {
            get
            {
                return context_.Header.StartDistance ?? 0.0;
            }
            set
            {
                context_.RejectCreated();
                if (context_.IsMdf3) { return; } // MDF 3.30 has no HD start distance; the wrapper ignores the value too.
                context_.Header.StartDistance = value;
            }
        }

        /// <summary>The start time in nanoseconds since 1970-01-01. It reaches the
        /// file only while the writer has not created it yet.</summary>
        public ulong StartTime
        {
            get
            {
                return context_.Header.StartTime;
            }
            set
            {
                if (context_.TreeFlushed) { throw new InvalidOperationException("The file is created, so its start time cannot change any more."); }
                context_.Header.StartTime = value;
                context_.StartTimeNs = value;
            }
        }

        /// <summary>The subject of the measurement.</summary>
        public string Subject
        {
            get
            {
                return context_.Header.Subject;
            }
            set
            {
                context_.RejectCreated();
                context_.Header.Subject = value ?? string.Empty;
            }
        }
    }
}
