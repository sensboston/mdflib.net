// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
namespace MdfLib
{
    /// <summary>Mutable writer metadata of one MDF 4 source information block (SI), copied by the writer; MDF 3.30 has no
    /// per-channel source, and its channel group source comes from the bus logger. No referenced path is opened or
    /// checked. A text that is set to null is read as the empty string its property defaults to, which stores
    /// nothing for that field.</summary>
    internal sealed class MdfSourceDefinition
    {
        /// <summary>Gets or sets the source name (<c>si_name</c>); default empty, which writes no SI block at all.</summary>
        public string Name { get; set; } = string.Empty;
        /// <summary>Gets or sets the source path (<c>si_path</c>, with the path separator recorded in the owning channel group);
        /// default empty (no path, <c>si_flags</c> bit 1 clear).</summary>
        public string Path { get; set; } = string.Empty;
        /// <summary>Gets or sets the source description: the MDF 4 <c>SIcomment</c> TX element; default empty (no block).</summary>
        public string Comment { get; set; } = string.Empty;
        /// <summary>Gets or sets the common properties of the block's MD payload, written as e elements after the
        /// comment text; default null (none).</summary>
        public System.Collections.Generic.IReadOnlyList<MdfLib.MdfCommonProperty> CommonProperties { get; set; }
        /// <summary>Gets or sets the long identifier of the channel owning this source: the 26-byte
        /// <c>cn_long_identifier</c> tail field of the MDF 4 CN block, so at most 25 bytes with its NUL; default empty
        /// (no field). MDF 3.30 has no such field, and a long identifier there is refused rather than moved into
        /// <c>cn_description</c>, which stores that revision's channel comment. A longer text is refused, never
        /// truncated.</summary>
        public string LongIdentifier { get; set; } = string.Empty;
        /// <summary>Gets or sets <c>si_type</c>: 1 (ECU), 2 (bus), 3 (frame), 4 (file), 5 (infinity); default 2. Only 1..5.</summary>
        public byte SourceType { get; set; } = 2;
        /// <summary>Gets or sets <c>si_bus_type</c> (0 unspecified, 1 CAN, 2 LIN, 3 MOST, 4 FlexRay, 5 Ethernet, 6 Ethernet
        /// power train, 7 SOME/IP); default 1. Values above 7 are refused.</summary>
        public byte BusType { get; set; } = 1;
        /// <summary>Gets or sets whether <c>si_bus_type</c> is a synchronization bus (MDF 4.10 <c>si_flags</c> bit 3); default
        /// false, which is what an offline ECU/file source means.</summary>
        public bool IsSyncBus { get; set; }

        /// <summary>Gets or sets the si_flags byte, written as it is. Null (default) derives the bits from the texts and the
        /// synchronization-bus marker.</summary>
        public byte? Flags { get; set; }
    }
}
