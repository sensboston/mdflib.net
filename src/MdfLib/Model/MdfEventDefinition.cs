// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System.Collections.Generic;

namespace MdfLib
{
    /// <summary>Mutable writer event (MDF 4 EV block) configuration, copied by <see cref="MdfWriter.AddEvent"/>.</summary>
    /// <remarks>Indices refer to this writer: channel groups, attachments and earlier events (parent and range begin).</remarks>
    internal sealed class MdfEventDefinition
    {
        /// <summary>Gets or sets the optional event name.</summary>
        public string Name { get; set; } = string.Empty;
        /// <summary>Gets or sets the optional comment.</summary>
        public string Comment { get; set; } = string.Empty;
        /// <summary>Gets or sets the optional group name (MDF 4.20 only).</summary>
        public string GroupName { get; set; } = string.Empty;
        /// <summary>Gets or sets the event type; default Marker.</summary>
        public MdfEventType Type { get; set; } = MdfEventType.Marker;
        /// <summary>Gets or sets the synchronization domain of the sync value; default Time.</summary>
        public MdfEventSyncType SyncType { get; set; } = MdfEventSyncType.Time;
        /// <summary>Gets or sets whether the event is a point or begins/ends a range; default Point.</summary>
        public MdfEventRangeType RangeType { get; set; }
        /// <summary>Gets or sets the cause; default Other.</summary>
        public MdfEventCause Cause { get; set; }
        /// <summary>Gets or sets the integer sync base value; the sync value is SyncBaseValue * SyncFactor.</summary>
        public long SyncBaseValue { get; set; }
        /// <summary>Gets or sets the finite sync factor; default 1.</summary>
        public double SyncFactor { get; set; } = 1;
        /// <summary>Gets or sets the creator index the event stores, as 16 bits.</summary>
        public ushort CreatorIndex { get; set; }
        /// <summary>Gets or sets the index of an earlier parent event, or -1 (default).</summary>
        public int ParentIndex { get; set; } = -1;
        /// <summary>Gets or sets the index of the earlier range-begin event of a range end, or -1 (default).</summary>
        public int RangeIndex { get; set; } = -1;
        /// <summary>Gets or sets the indices of the channel groups the event applies to; null or empty for the whole file.</summary>
        public IReadOnlyList<int> ScopeGroupIndices { get; set; }
        /// <summary>Gets or sets the indices of attachments of this writer referenced by the event.</summary>
        public IReadOnlyList<int> AttachmentIndices { get; set; }
        /// <summary>Gets or sets the common properties of the comment's MD block; null or empty writes plain text.</summary>
        public IReadOnlyList<MdfCommonProperty> CommonProperties { get; set; }
    }
}
