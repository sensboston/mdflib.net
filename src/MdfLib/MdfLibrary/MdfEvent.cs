// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
namespace MdfLibrary
{
    /// <summary>The kind of an event (ev_type).</summary>
    public enum EventType : byte
    {
        /// <summary>A recording period.</summary>
        RecordingPeriod = 0,

        /// <summary>A recording interruption.</summary>
        RecordingInterrupt = 1,

        /// <summary>An acquisition interruption.</summary>
        AcquisitionInterrupt = 2,

        /// <summary>Recording started.</summary>
        StartRecording = 3,

        /// <summary>Recording stopped.</summary>
        StopRecording = 4,

        /// <summary>A trigger.</summary>
        Trigger = 5,

        /// <summary>A marker.</summary>
        Marker = 6,
    }

    /// <summary>How an event is synchronized (ev_sync_type).</summary>
    public enum SyncType : byte
    {
        /// <summary>Synchronization by time.</summary>
        SyncTime = 1,

        /// <summary>Synchronization by angle.</summary>
        SyncAngle = 2,

        /// <summary>Synchronization by distance.</summary>
        SyncDistance = 3,

        /// <summary>Synchronization by index.</summary>
        SyncIndex = 4,
    }

    /// <summary>Whether an event is a point or the start or end of a range (ev_range_type).</summary>
    public enum RangeType : byte
    {
        /// <summary>A single point.</summary>
        RangePoint = 0,

        /// <summary>The start of a range.</summary>
        RangeStart = 1,

        /// <summary>The end of a range.</summary>
        RangeEnd = 2,
    }

    /// <summary>What caused an event (ev_cause).</summary>
    public enum EventCause : byte
    {
        /// <summary>Some other cause.</summary>
        CauseOther = 0,

        /// <summary>An error.</summary>
        CauseError = 1,

        /// <summary>The recording tool.</summary>
        CauseTool = 2,

        /// <summary>A script.</summary>
        CauseScript = 3,

        /// <summary>The user.</summary>
        CauseUser = 4,
    }

    /// <summary>An event block (EV) of a file the facade writes or reads.</summary>
    public class MdfEvent
    {
        private readonly PendingEvent pending_;

        // Instances come from the facade factories; the wrapper has no public constructor
        // either, so the declaration layer keeps the type non-constructible by name.
        /// <summary>The handle of one event; the factories create it with the pending node.</summary>
        internal MdfEvent(PendingEvent pending)
        {
            pending_ = pending;
        }

        /// <summary>Adds an attachment that the event refers to.</summary>
        public void AddAttachment(MdfAttachment attachment)
        {
            Writable();
            if (attachment == null) { throw new ArgumentNullException("attachment"); }
            PendingAttachment node = attachment.Pending;
            if (node == null || !ReferenceEquals(node.Context, pending_.Context))
            { throw new ArgumentException("The attachment belongs to another file.", "attachment"); }
            pending_.Attachments.Add(node);
        }

        /// <summary>The attachments the event refers to, in file order.</summary>
        public MdfAttachment[] Attachments
        {
            get
            {
                MdfAttachment[] result = new MdfAttachment[pending_.Attachments.Count];
                for (int i = 0; i < result.Length; i++) { result[i] = PendingAttachment.FacadeOf(pending_.Attachments[i]); }
                return result;
            }
        }

        /// <summary>The cause of the event.</summary>
        public EventCause Cause
        {
            get { return pending_.Cause; }
            set { Writable(); pending_.Cause = value; }
        }

        /// <summary>The creator index of the event, stored in 16 bits as v2.3.0 stores it.</summary>
        public ulong CreatorIndex
        {
            get { return pending_.EventCreatorIndex; }
            set
            {
                Writable();
                pending_.EventCreatorIndex = value;
            }
        }

        /// <summary>The comment of the event.</summary>
        public string Description
        {
            get { return pending_.Description; }
            set { Writable(); pending_.Description = value ?? string.Empty; }
        }

        /// <summary>The name of the group of the event.</summary>
        public string GroupName
        {
            get { return pending_.GroupName; }
            set { Writable(); pending_.GroupName = value ?? string.Empty; }
        }

        /// <summary>The position of the event in the file.</summary>
        public long Index
        {
            get { return pending_.Index; }
        }

        /// <summary>The MD block of the event's comment, created on the first call. An
        /// event read from a file has none to give.</summary>
        public MdfMetaData MetaData
        {
            get
            {
                if (pending_.FromFile) { return null; }
                pending_.Context.RejectFlushed();
                if (pending_.Metadata == null) { pending_.Metadata = new MdfMetaData(); }
                return pending_.Metadata;
            }
        }

        /// <summary>The name of the event.</summary>
        public string Name
        {
            get { return pending_.Name; }
            set { Writable(); pending_.Name = value ?? string.Empty; }
        }

        /// <summary>The parent event of this event.</summary>
        public MdfEvent ParentEvent
        {
            get { return pending_.Parent == null ? null : FacadeOf(pending_.Parent); }
            set { Writable(); pending_.Parent = Node(value); }
        }

        /// <summary>The post-trigger time. The engine stores none, so only zero is accepted.</summary>
        public double PostTrig
        {
            get { return pending_.PostTrig; }
            set { Writable(); RejectTrigger(value); pending_.PostTrig = value; }
        }

        /// <summary>The pre-trigger time. The engine stores none, so only zero is accepted.</summary>
        public double PreTrig
        {
            get { return pending_.PreTrig; }
            set { Writable(); RejectTrigger(value); pending_.PreTrig = value; }
        }

        /// <summary>The range event that begins or ends this event.</summary>
        public MdfEvent RangeEvent
        {
            get { return pending_.RangeEvent == null ? null : FacadeOf(pending_.RangeEvent); }
            set { Writable(); pending_.RangeEvent = Node(value); }
        }

        /// <summary>The range type of the event.</summary>
        public RangeType Range
        {
            get { return pending_.Range; }
            set { Writable(); pending_.Range = value; }
        }

        /// <summary>The factor of the synchronization value.</summary>
        public double SyncFactor
        {
            get { return pending_.SyncFactor; }
            set { Writable(); pending_.SyncFactor = value; }
        }

        /// <summary>The synchronization base value of the event.</summary>
        public long SyncValue
        {
            get { return pending_.SyncValue; }
            set { Writable(); pending_.SyncValue = value; }
        }

        /// <summary>The synchronization type of the event.</summary>
        public SyncType Sync
        {
            get { return pending_.Sync; }
            set { Writable(); pending_.Sync = value; }
        }

        /// <summary>The type of the event.</summary>
        public EventType Type
        {
            get { return pending_.Type; }
            set { Writable(); pending_.Type = value; }
        }

        /// <summary>The handle of a pending event node, created once and reused.</summary>
        internal static MdfEvent FacadeOf(PendingEvent node)
        {
            return PendingEvent.FacadeOf(node);
        }

        private static PendingEvent Node(MdfEvent value)
        {
            if (value == null) { return null; }
            return value.pending_;
        }

        private static void RejectTrigger(double value)
        {
            if (value != 0.0) { throw new NotSupportedException("The engine stores no pre- or post-trigger time for an event, so a non-zero value cannot be written."); }
        }

        // A reader's event never changes, and a written one is fixed when the tree is flushed.
        private void Writable()
        {
            if (pending_.FromFile) { throw new NotSupportedException("An event read from a file is read-only; the reader never writes."); }
            pending_.Context.RejectFlushed();
        }
    }
}
