// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System.Collections.Generic;

namespace MdfLib
{
    /// <summary>MDF 4 event type (ev_type); unknown wire values are preserved numerically.</summary>
    internal enum MdfEventType
    {
        /// <summary>Recording period.</summary>
        Recording = 0,
        /// <summary>Recording interrupt.</summary>
        RecordingInterrupt = 1,
        /// <summary>Acquisition interrupt.</summary>
        AcquisitionInterrupt = 2,
        /// <summary>Start recording trigger.</summary>
        StartRecordingTrigger = 3,
        /// <summary>Stop recording trigger.</summary>
        StopRecordingTrigger = 4,
        /// <summary>Trigger.</summary>
        Trigger = 5,
        /// <summary>Marker (bookmark).</summary>
        Marker = 6
    }

    /// <summary>MDF 4 event synchronization domain (ev_sync_type); unknown wire values are preserved numerically.</summary>
    internal enum MdfEventSyncType
    {
        /// <summary>Not a specified domain.</summary>
        None = 0,
        /// <summary>Time in seconds.</summary>
        Time = 1,
        /// <summary>Angle in radians.</summary>
        Angle = 2,
        /// <summary>Distance in meters.</summary>
        Distance = 3,
        /// <summary>Zero-based record index.</summary>
        Index = 4
    }

    /// <summary>MDF 4 event range role (ev_range_type); unknown wire values are preserved numerically.</summary>
    internal enum MdfEventRangeType
    {
        /// <summary>A point event.</summary>
        Point = 0,
        /// <summary>The beginning of a range.</summary>
        RangeBegin = 1,
        /// <summary>The end of a range.</summary>
        RangeEnd = 2
    }

    /// <summary>MDF 4 event cause (ev_cause); unknown wire values are preserved numerically.</summary>
    internal enum MdfEventCause
    {
        /// <summary>Other or unknown cause.</summary>
        Other = 0,
        /// <summary>An error.</summary>
        Error = 1,
        /// <summary>The recording tool.</summary>
        Tool = 2,
        /// <summary>A script.</summary>
        Script = 3,
        /// <summary>A user.</summary>
        User = 4
    }

    /// <summary>one MDF 4 event (EV block) with references resolved against the same document's catalog and chains.</summary>
    internal sealed class MdfEvent
    {
        internal MdfEvent() { }
        /// <summary>Gets the zero-based position in the EV chain.</summary>
        public int Index { get; internal set; }
        /// <summary>Gets the decoded name, or empty.</summary>
        public string Name { get; internal set; }
        /// <summary>Gets the decoded raw comment.</summary>
        public string Comment { get; internal set; }
        /// <summary>Gets the MDF 4.20 group name (flag bit 1), or empty.</summary>
        public string GroupName { get; internal set; }
        /// <summary>Gets the event type.</summary>
        public MdfEventType Type { get; internal set; }
        /// <summary>Gets the synchronization domain of the sync value.</summary>
        public MdfEventSyncType SyncType { get; internal set; }
        /// <summary>Gets the range role.</summary>
        public MdfEventRangeType RangeType { get; internal set; }
        /// <summary>Gets the cause.</summary>
        public MdfEventCause Cause { get; internal set; }
        /// <summary>Gets the raw flags (bit 0 post-processing, bit 1 group name present).</summary>
        public byte Flags { get; internal set; }
        /// <summary>Gets the raw creator index into the file history, without validation against its count.</summary>
        public ushort CreatorIndex { get; internal set; }
        /// <summary>Gets the exact raw sync base value.</summary>
        public long SyncBaseValue { get; internal set; }
        /// <summary>Gets the raw sync factor.</summary>
        public double SyncFactor { get; internal set; }
        /// <summary>Gets the sync value SyncBaseValue × SyncFactor in binary64 (the base value is widened to double first).</summary>
        public double SyncValue => SyncBaseValue * SyncFactor;
        /// <summary>Gets the parent event in the same chain, or null.</summary>
        public MdfEvent Parent { get; internal set; }
        /// <summary>Gets the range-begin event of a range end (ev_ev_range), or null.</summary>
        public MdfEvent Range { get; internal set; }
        /// <summary>Gets channel groups in the event scope, in wire order.</summary>
        public IReadOnlyList<MdfChannelGroup> ScopeChannelGroups { get; internal set; }
        /// <summary>Gets channels in the event scope, in wire order.</summary>
        public IReadOnlyList<MdfChannel> ScopeChannels { get; internal set; }
        /// <summary>Gets referenced attachments, in wire order.</summary>
        public IReadOnlyList<MdfAttachment> Attachments { get; internal set; }
    }
}
