// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
namespace MdfLib
{
    /// <summary>one MDF 4 file-history entry (FH block) with its FHcomment fields.</summary>
    internal sealed class MdfFileHistory
    {
        internal MdfFileHistory() { }
        /// <summary>Gets the zero-based position in the FH chain (the creator index used by attachments and events).</summary>
        public int Index { get; internal set; }
        /// <summary>Gets the raw change timestamp in nanoseconds, without rounding or an implicit UTC guarantee.</summary>
        public ulong RawTimeNanoseconds { get; internal set; }
        /// <summary>Gets the stored UTC offset in minutes; consult TimeFlags before interpretation.</summary>
        public int UtcOffsetMinutes { get; internal set; }
        /// <summary>Gets the stored daylight-saving offset in minutes; consult TimeFlags before interpretation.</summary>
        public int DaylightSavingOffsetMinutes { get; internal set; }
        /// <summary>Gets the raw time flags (bit 0 local time, bit 1 offsets valid).</summary>
        public byte TimeFlags { get; internal set; }
        /// <summary>Gets the decoded raw comment (normally validated FHcomment XML).</summary>
        public string Comment { get; internal set; }
        /// <summary>Gets the FHcomment TX element text, or empty.</summary>
        public string Text { get; internal set; }
        /// <summary>Gets the FHcomment tool_id element text, or empty.</summary>
        public string ToolIdentifier { get; internal set; }
        /// <summary>Gets the FHcomment tool_vendor element text, or empty.</summary>
        public string ToolVendor { get; internal set; }
        /// <summary>Gets the FHcomment tool_version element text, or empty.</summary>
        public string ToolVersion { get; internal set; }
        /// <summary>Gets the FHcomment user_name element text, or empty.</summary>
        public string UserName { get; internal set; }
    }
}
