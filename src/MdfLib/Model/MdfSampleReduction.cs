// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using MdfLib.Reading;

namespace MdfLib
{
    /// <summary>the domain of a sample reduction's cycle interval (sr_sync_type); unknown wire values are preserved numerically.</summary>
    internal enum MdfReductionSyncType
    {
        /// <summary>Not a specified domain.</summary>
        None = 0,
        /// <summary>Time in seconds (always for MDF 3).</summary>
        Time = 1,
        /// <summary>Angle in radians.</summary>
        Angle = 2,
        /// <summary>Distance in meters.</summary>
        Distance = 3,
        /// <summary>Record index.</summary>
        Index = 4
    }

    /// <summary>one sample reduction (SR block) of a channel group: per cycle, mean/minimum/maximum records of the group's layout.</summary>
    internal sealed class MdfSampleReduction
    {
        internal MdfSampleReduction() { }
        internal object Document { get; set; }
        internal ulong BlockAddress { get; set; }
        internal ulong DataAddress { get; set; }
        // One stored reduction record: three record parts plus the optional shared invalidation section.
        internal ulong RecordBytes { get; set; }
        internal bool RawData { get; set; } // MDF 3 headerless bytes.
        internal Mdf4DataMap DataMap { get; set; }
        internal Mdf4ListDataMap ListDataMap { get; set; }

        /// <summary>Gets the owning channel group (the same catalog instance).</summary>
        public MdfChannelGroup Group { get; internal set; }
        /// <summary>Gets the zero-based position in the group's SR chain.</summary>
        public int Index { get; internal set; }
        /// <summary>Gets the declared number of reduction cycles.</summary>
        public ulong CycleCount { get; internal set; }
        /// <summary>Gets the declared cycle interval length in the units of <see cref="SyncType"/>.</summary>
        public double Interval { get; internal set; }
        /// <summary>Gets the interval domain.</summary>
        public MdfReductionSyncType SyncType { get; internal set; }
        /// <summary>Gets the raw SR flags (bit 0 invalidation bytes present, bit 1 dominant invalidation bit); zero for MDF 3.</summary>
        public int Flags { get; internal set; }
        /// <summary>Gets whether each reduction record stores one invalidation section shared by its mean/minimum/maximum parts.</summary>
        public bool HasInvalidationBytes => (Flags & 1) != 0;
        /// <summary>Gets whether a set invalidation bit means "at least one" (rather than "all") original values were invalid.</summary>
        public bool IsInvalidationDominant => (Flags & 2) != 0;
        /// <summary>Gets whether reduction values can be read.</summary>
        public bool CanReadValues => UnsupportedReason == null;
        /// <summary>Gets an explanation when values cannot be read, otherwise null.</summary>
        public string UnsupportedReason { get; internal set; }
    }

    /// <summary>an immutable batch of reduction cycles; each part is a batch over the same cycles and selected channels.</summary>
    internal sealed class MdfSampleReductionBatch
    {
        internal MdfSampleReductionBatch(MdfSampleBatch mean, MdfSampleBatch minimum, MdfSampleBatch maximum)
        {
            Mean = mean;
            Minimum = minimum;
            Maximum = maximum;
        }

        /// <summary>Gets the first zero-based cycle index of this batch.</summary>
        public ulong FirstCycleIndex => Mean.FirstSampleIndex;
        /// <summary>Gets the number of cycles in this batch.</summary>
        public int CycleCount => Mean.SampleCount;
        /// <summary>Gets the mean values; row i is cycle FirstCycleIndex + i.</summary>
        public MdfSampleBatch Mean { get; }
        /// <summary>Gets the minimum values.</summary>
        public MdfSampleBatch Minimum { get; }
        /// <summary>Gets the maximum values.</summary>
        public MdfSampleBatch Maximum { get; }
    }
}
