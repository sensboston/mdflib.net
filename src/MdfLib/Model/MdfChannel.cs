// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System.Collections.Generic;
using System.Collections.ObjectModel;
using MdfLib.Reading;

namespace MdfLib
{
    /// <summary>A logical channel snapshot. Identity is parent/index, not its possibly duplicate name.</summary>
    internal sealed class MdfChannel
    {
        internal static readonly IReadOnlyList<MdfChannel> NoComponents = new ReadOnlyCollection<MdfChannel>(new MdfChannel[0]);
        internal MdfChannel() { }
        internal ChannelLayout Layout { get; set; }
        internal bool BlocksComponents { get; set; }
        // MDF 4 CN address (hierarchy/event resolution) and cn_at_reference links; null when none.
        internal ulong BlockAddress { get; set; }
        internal ulong[] AttachmentAddresses { get; set; }
        /// <summary>Gets the zero-based index within its channel group.</summary>
        public int Index { get; internal set; }
        /// <summary>Gets the decoded name: strict UTF-8 for MDF 4; declared code page and padding rules for MDF 3.</summary>
        public string Name { get; internal set; }
        /// <summary>Gets the channel unit, falling back to its conversion unit when absent.</summary>
        public string Unit { get; internal set; }
        /// <summary>Gets the decoded raw TX or validated XML comment.</summary>
        public string Comment { get; internal set; }
        /// <summary>Gets optional source information; external paths remain inert metadata.</summary>
        public MdfSourceInformation Source { get; internal set; }
        /// <summary>Gets the immediate MDF 4 structure-composition parent in the same group, or null for a top-level channel.</summary>
        public MdfChannel Parent { get; internal set; }
        /// <summary>Gets immutable immediate structure components in wire order; empty for non-structures. Components are also selectable by their group Index.</summary>
        public IReadOnlyList<MdfChannel> Components { get; internal set; } = NoComponents;
        /// <summary>Gets an optional recognized array shape, including inspectable unsupported layouts; null for scalars/non-array composition.</summary>
        public MdfChannelArray Array { get; internal set; }
        /// <summary>Gets the declared scalar or array-element bit width.</summary>
        public uint BitCount { get; internal set; }

        /// <summary>Gets the cn_type exactly as the file stores it (MDF 3: the record role); zero for a channel without a layout.</summary>
        public byte StoredChannelType { get { return Layout == null ? (byte)0 : Layout.CnType; } }

        /// <summary>Gets the cn_data_type exactly as the file stores it, before the engine normalizes it for decoding.</summary>
        public byte StoredDataType { get { return Layout == null ? (byte)0 : Layout.CnDataType; } }

        /// <summary>Gets the stored cn_bit_offset of the channel inside its record, in bits.</summary>
        public int StoredBitOffset { get { return Layout == null ? 0 : Layout.BitOffset; } }

        /// <summary>Gets the stored cn_flags of the channel.</summary>
        public uint StoredFlags { get { return Layout == null ? 0U : Layout.Flags; } }
        /// <summary>Gets the raw value kind: exact numeric, MDF 4 Complex, MDF 3/4 Text/ByteArray (a recognized MDF 4 structure parent is ByteArray: its record window), or Unsupported.</summary>
        public MdfValueKind ValueKind { get; internal set; }
        /// <summary>Gets the calendar encoding of the channel's byte values (MDF 4 CANopen date/time), else None.</summary>
        public MdfCalendarKind CalendarKind { get; internal set; }
        /// <summary>Gets whether this is a stored master channel or an MDF 4 virtual master (its values are record indices through its conversion).</summary>
        public bool IsMaster { get; internal set; }
        /// <summary>Gets MDF 4's synchronization kind, or time=1 for an MDF 3 time master; no UTC conversion is inferred.</summary>
        public byte SynchronizationType { get; internal set; }
        /// <summary>Gets the conversion information, including explicitly unsupported conversions.</summary>
        public MdfChannelConversion Conversion { get; internal set; }

        /// <summary>Gets whether the channel stores a conversion block (CC); an identity block is still a block.</summary>
        public bool HasConversionBlock { get; internal set; }

        /// <summary>Gets the MDF 3 fixed description field of the CN block (128 bytes, trimmed); empty for MDF 4.</summary>
        public string FixedDescription { get; internal set; } = string.Empty;
        /// <summary>Gets scalar/fixed-array storage capability. The group's record-layout capability is separate.</summary>
        public bool CanReadRaw => UnsupportedReason == null;
        /// <summary>Gets storage/conversion capability, excluding the group's separate layout capability.</summary>
        public bool CanReadPhysical => CanReadRaw && Conversion.Kind != MdfConversionKind.Unsupported && !ConversionMismatch;
        /// <summary>a text-input conversion on a numeric channel (raw stays readable, physical does not).</summary>
        internal bool ConversionMismatch { get; set; }
        /// <summary>Gets a storage explanation, or null when supported. See Conversion for physical limits.</summary>
        public string UnsupportedReason { get; internal set; }
    }
}
