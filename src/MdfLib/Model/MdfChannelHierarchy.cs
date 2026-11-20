// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System.Collections.Generic;

namespace MdfLib
{
    /// <summary>MDF 4 channel-hierarchy node kind (ch_type); unknown wire values are preserved numerically.</summary>
    internal enum MdfHierarchyType
    {
        /// <summary>A plain group.</summary>
        Group = 0,
        /// <summary>A function.</summary>
        Function = 1,
        /// <summary>A structure.</summary>
        Structure = 2,
        /// <summary>A map list.</summary>
        MapList = 3,
        /// <summary>Input variables of a function.</summary>
        InputVariables = 4,
        /// <summary>Output variables of a function.</summary>
        OutputVariables = 5,
        /// <summary>Local variables of a function.</summary>
        LocalVariables = 6,
        /// <summary>A calibration-object definition.</summary>
        CalibrationDefinition = 7,
        /// <summary>A calibration-object reference.</summary>
        CalibrationReference = 8
    }

    /// <summary>one MDF 4 channel-hierarchy node (CH block); element triples are resolved to catalog channels.</summary>
    internal sealed class MdfChannelHierarchy
    {
        internal MdfChannelHierarchy() { }
        /// <summary>Gets the decoded name, or empty.</summary>
        public string Name { get; internal set; }
        /// <summary>Gets the decoded raw comment.</summary>
        public string Comment { get; internal set; }
        /// <summary>Gets the node kind.</summary>
        public MdfHierarchyType Type { get; internal set; }
        /// <summary>Gets the referenced channels in wire order (each validated against its DG/CG triple).</summary>
        public IReadOnlyList<MdfChannel> Channels { get; internal set; }
        /// <summary>Gets the child nodes in chain order.</summary>
        public IReadOnlyList<MdfChannelHierarchy> Children { get; internal set; }
    }
}
