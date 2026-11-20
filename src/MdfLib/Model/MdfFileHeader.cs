// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
namespace MdfLib
{
    /// <summary>A materialized ID/HD snapshot, independent of the lifetime of its reader.</summary>
    /// <remarks>Linked comments, data groups and samples have not been read.</remarks>
    internal sealed class MdfFileHeader
    {
        internal MdfFileHeader()
        {
        }

        /// <summary>Gets the exact supported MDF revision, currently 3.00, 3.10, 3.20, 3.30, 4.00, 4.10, 4.11 or 4.20 (limited reader subsets).</summary>
        public string Version { get; internal set; }
        /// <summary>Gets the creating program's bounded ID field.</summary>
        public string ProgramIdentifier { get; internal set; }
        /// <summary>Gets MDF 3's default data order; this does not change metadata byte order.</summary>
        public bool DefaultDataIsBigEndian { get; internal set; }
        /// <summary>Gets MDF 3's code page, or null for MDF 4.</summary>
        public int? CodePage { get; internal set; }
        /// <summary>Gets MDF 3's declared data-group count, or null for MDF 4 (no traversal performed).</summary>
        public int? DataGroupCount { get; internal set; }
        /// <summary>Gets the raw nanosecond timestamp without rounding or an implicit UTC guarantee.</summary>
        public ulong RawStartTimeNanoseconds { get; internal set; }
        /// <summary>Gets the stored UTC offset in minutes; MDF 3's whole hours are converted exactly.</summary>
        public int UtcOffsetMinutes { get; internal set; }
        /// <summary>Gets MDF 4's stored daylight-saving offset in minutes, or null for MDF 3.</summary>
        public int? DaylightSavingOffsetMinutes { get; internal set; }
        /// <summary>Gets MDF 4's raw timestamp flags, or null for MDF 3.</summary>
        public byte? TimeFlags { get; internal set; }
        /// <summary>Gets the raw time-quality field, without substituting a guessed clock source.</summary>
        public ushort TimeQuality { get; internal set; }
        /// <summary>Gets MDF 4's raw header flags, or null for MDF 3.</summary>
        public byte? Flags { get; internal set; }
        /// <summary>Gets MDF 4's raw start angle, or null for MDF 3; consult Flags before interpretation.</summary>
        public double? StartAngle { get; internal set; }
        /// <summary>Gets MDF 4's raw start distance, or null for MDF 3; consult Flags before interpretation.</summary>
        public double? StartDistance { get; internal set; }
        /// <summary>Gets MDF 3's date text without calendar interpretation, or null for MDF 4.</summary>
        public string DateText { get; internal set; }
        /// <summary>Gets MDF 3's time text without clock interpretation, or null for MDF 4.</summary>
        public string TimeText { get; internal set; }
        /// <summary>Gets the fixed MDF 3 author field, or null for MDF 4 (linked metadata not loaded).</summary>
        public string Author { get; internal set; }
        /// <summary>Gets the fixed MDF 3 organization field, or null for MDF 4.</summary>
        public string Organization { get; internal set; }
        /// <summary>Gets the fixed MDF 3 project field, or null for MDF 4.</summary>
        public string Project { get; internal set; }
        /// <summary>Gets the fixed MDF 3 subject field, or null for MDF 4.</summary>
        public string Subject { get; internal set; }
        /// <summary>Gets MDF 3's fixed timer identifier, or null for MDF 4.</summary>
        public string TimerIdentification { get; internal set; }
    }
}
