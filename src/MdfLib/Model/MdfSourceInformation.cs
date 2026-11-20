// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
namespace MdfLib
{
    /// <summary>Immutable source metadata; no referenced path is automatically opened.</summary>
    internal sealed class MdfSourceInformation
    {
        internal MdfSourceInformation() { }
        /// <summary>Gets the decoded source name in the format-specific declared encoding.</summary>
        public string Name { get; internal set; }
        /// <summary>Gets an inert source path string.</summary>
        public string Path { get; internal set; }
        /// <summary>Gets the decoded MDF 4 comment; empty for MDF 3 CE sources.</summary>
        public string Comment { get; internal set; }
        /// <summary>Gets MDF 4 source type, or the normalized MDF 3 CE kind (ECU=1, bus=2).</summary>
        public byte SourceType { get; internal set; }
        /// <summary>Gets MDF 4 bus type, or CAN=2 for MDF 3 Vector CE and 0 for ECU CE.</summary>
        public byte BusType { get; internal set; }
        /// <summary>Gets MDF 4 source flags; 0 for MDF 3 CE sources.</summary>
        public byte Flags { get; internal set; }
    }
}
