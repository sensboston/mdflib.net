// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
namespace MdfLib
{
    /// <summary>Mutable writer metadata of one MDF 4 channel group (name, comment and source), copied by
    /// <see cref="MdfWriter.SetChannelGroupMetadata"/>; MDF 3.30 has none of these blocks and is refused.</summary>
    internal sealed class MdfChannelGroupDefinition
    {
        /// <summary>Gets or sets the acquisition name (<c>cg_acq_name</c>); null keeps the current name, empty removes it.</summary>
        public string Name { get; set; }
        /// <summary>Gets or sets the group description: the MDF 4 <c>CGcomment</c> TX element; default empty (no block).</summary>
        public string Comment { get; set; } = string.Empty;
        /// <summary>Gets or sets the common properties of the block's MD payload, written as e elements after the
        /// comment text; default null (none).</summary>
        public System.Collections.Generic.IReadOnlyList<MdfLib.MdfCommonProperty> CommonProperties { get; set; }

        /// <summary>Gets or sets the common properties of the data group block's MD payload. A data group shared by
        /// several channel groups refuses them; the facade gives them to the first group only. Default null.</summary>
        public System.Collections.Generic.IReadOnlyList<MdfLib.MdfCommonProperty> DataGroupCommonProperties { get; set; }
        /// <summary>Gets or sets the group source (<c>cg_source</c>); default null (no SI block).</summary>
        public MdfSourceDefinition Source { get; set; }
    }
}
