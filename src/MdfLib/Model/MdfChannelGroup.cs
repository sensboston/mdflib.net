// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System.Collections.Generic;
using MdfLib.Reading;

namespace MdfLib
{
    /// <summary>Describes one record layout and its immutable channel snapshots.</summary>
    internal sealed class MdfChannelGroup
    {
        internal MdfChannelGroup() { }
        internal object Document { get; set; }
        internal RecordLayout Layout { get; set; }
        internal IReadOnlyList<MdfSampleReduction> SampleReductions { get; set; } // cached SR chain.
        /// <summary>Gets the zero-based index within its data group.</summary>
        public int Index { get; internal set; }
        /// <summary>Gets MDF 4's acquisition name with whitespace preserved; empty for MDF 3.</summary>
        public string Name { get; internal set; }
        /// <summary>Gets the decoded raw TX or validated XML comment.</summary>
        public string Comment { get; internal set; }
        /// <summary>Gets optional acquisition-source information. Its path is never opened.</summary>
        public MdfSourceInformation Source { get; internal set; }
        /// <summary>Gets MDF 4's raw cg_flags (bit 1 bus event, bit 2 plain bus event, bit 3 remote master); 0 for MDF 3.</summary>
        public ushort Flags { get; internal set; }
        /// <summary>Gets MDF 4's declared path separator (for example '.' in bus logging names), or '\0' when none.</summary>
        public char PathSeparator { get; internal set; }
        /// <summary>Gets the declared cycle count, without rounding through double.</summary>
        public ulong SampleCount { get; internal set; }
        /// <summary>Gets the normalized fixed record size including invalidation bytes, excluding record-ID framing; physical storage may be separate.</summary>
        public ulong RecordByteCount { get; internal set; }
        /// <summary>Gets whether the record layout is supported, not a certification of unread sample values.</summary>
        public bool CanReadSamples => UnsupportedReason == null;
        /// <summary>Gets an unsupported-layout explanation, or null for a supported record layout.</summary>
        public string UnsupportedReason { get; internal set; }
        /// <summary>Gets immutable channels; duplicate names are legal.</summary>
        public IReadOnlyList<MdfChannel> Channels { get; internal set; }
    }
}
