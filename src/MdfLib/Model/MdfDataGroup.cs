// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System.Collections.Generic;

namespace MdfLib
{
    /// <summary>A read-only logical data group, detached from physical block inheritance.</summary>
    internal sealed class MdfDataGroup
    {
        internal MdfDataGroup() { }
        internal ulong BlockAddress { get; set; } // MDF 4 DG address for hierarchy triples.
        /// <summary>Gets the zero-based data-group index in the catalog.</summary>
        public int Index { get; internal set; }
        /// <summary>Gets the decoded raw comment (XML is validated, not converted to prose).</summary>
        public string Comment { get; internal set; }
        /// <summary>Gets the immutable channel-group collection.</summary>
        public IReadOnlyList<MdfChannelGroup> ChannelGroups { get; internal set; }
    }
}
