// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System.Collections.Generic;

namespace MdfLib
{
    /// <summary>Mutable writer channel configuration, copied by AddChannel; never a mutable reader channel.</summary>
    internal sealed class MdfChannelDefinition
    {
        /// <summary>Gets or sets a nonempty channel name; duplicate names are allowed.</summary>
        public string Name { get; set; } = string.Empty;
        /// <summary>Gets or sets the unit; default empty. MDF 3.30 requires at most 19 Latin-1 bytes.</summary>
        public string Unit { get; set; } = string.Empty;
        /// <summary>Gets or sets the raw kind; default UnsignedInteger. Text and ByteArray are fixed-length fields;
        /// Complex (MDF 4 only) stores two IEEE parts.</summary>
        public MdfValueKind ValueKind { get; set; }
        /// <summary>Gets or sets an integer width 1..64, IEEE width 32/64, complex width 64/128, or a whole-byte
        /// text/byte-array field width; default 64.</summary>
        public int BitCount { get; set; } = 64;
        /// <summary>Gets or sets big-endian sample storage; metadata remains little endian.</summary>
        public bool IsBigEndian { get; set; }
        /// <summary>Gets or sets the optional stored time-master role; at most one channel may be a master.</summary>
        public bool IsMaster { get; set; }
        /// <summary>Gets or sets whether to serialize the linear conversion Factor*raw+Offset; samples remain raw.</summary>
        public bool HasLinearConversion { get; set; }
        /// <summary>Gets or sets the finite linear factor; default 1, required to be 1 without a linear conversion.</summary>
        public double Factor { get; set; } = 1;
        /// <summary>Gets or sets the finite linear offset; default 0, required to be 0 without a linear conversion.</summary>
        public double Offset { get; set; }
        /// <summary>Gets or sets a nonlinear conversion (rational, polynomial, exponential, logarithmic, algebraic, tables,
        /// value/range to text, text to value/text); default null. It excludes <see cref="HasLinearConversion"/>; numeric
        /// conversions need a numeric channel and text-to-value/text conversions an MDF 4 text channel. Samples remain raw.</summary>
        public MdfConversionDefinition Conversion { get; set; }
        /// <summary>Gets or sets whether a text or byte-array channel stores each value with its own length as variable-length
        /// signal data (MDF 4 only; default false). The record then holds a 64-bit offset (keep <see cref="BitCount"/> at 64)
        /// into a VLSD channel group of the same data group, whose records follow in the data stream behind one-byte record
        /// IDs. Values may have any length up to <see cref="MdfWriterOptions.MaximumTextBytes"/>.</summary>
        public bool IsVariableLength { get; set; }
        /// <summary>Gets or sets the channel description: the MDF 4 <c>CNcomment</c> TX element, stored as an MD block behind
        /// <c>cn_comment</c>, or the MDF 3.30 <c>cn_description</c> field (at most 127 Latin-1 bytes, no TX extension);
        /// default empty (nothing is stored).</summary>
        public string Comment { get; set; } = string.Empty;
        /// <summary>Gets or sets the common properties of the block's MD payload, written as e elements after the
        /// comment text; default null (none).</summary>
        public System.Collections.Generic.IReadOnlyList<MdfLib.MdfCommonProperty> CommonProperties { get; set; }
        /// <summary>Gets or sets the MDF 4 source metadata of this channel (an SI block behind <c>cn_source</c>); default null.
        /// MDF 3.30 has no per-channel source block, so a non-null value there is refused.</summary>
        public MdfSourceDefinition Source { get; set; }
        /// <summary>Gets or sets the dimension sizes of a fixed array channel (MDF 4 only; default null for a scalar). Each
        /// record then stores the product of the sizes as contiguous elements of this kind and width in row-major order
        /// (the last index varies fastest), each with its own invalidation bit; a row passes the elements of the channel as
        /// that many consecutive values. Array channels cannot be the master or variable-length.</summary>
        public IReadOnlyList<int> ArrayDimensions { get; set; }

        /// <summary>Gets or sets cn_sync_type: 0 none (default), 1 time, 2 angle, 3 distance, 4 index. A time master
        /// keeps 1 when this is 0.</summary>
        public byte SyncType { get; set; }

        /// <summary>Gets or sets cn_precision (decimal places); null leaves the precision unused (cn_flags bit 0x0004).</summary>
        public byte? Precision { get; set; }

        /// <summary>Gets or sets the value range cn_val_range_min/max; both or neither (cn_flags bit 0x0008).</summary>
        public double? RangeMin { get; set; }

        /// <summary>Gets or sets the value range maximum, with <see cref="RangeMin"/>.</summary>
        public double? RangeMax { get; set; }

        /// <summary>Gets or sets the limit cn_limit_min/max; both or neither (cn_flags bit 0x0010).</summary>
        public double? LimitMin { get; set; }

        /// <summary>Gets or sets the limit maximum, with <see cref="LimitMin"/>.</summary>
        public double? LimitMax { get; set; }

        /// <summary>Gets or sets the extended limit cn_limit_ext_min/max; both or neither (cn_flags bit 0x0020).</summary>
        public double? ExtLimitMin { get; set; }

        /// <summary>Gets or sets the extended limit maximum, with <see cref="ExtLimitMin"/>.</summary>
        public double? ExtLimitMax { get; set; }

        /// <summary>Gets or sets cn_flags bits the caller sets (event, bus event, virtual, monotony, default X); the
        /// validity bits follow from the values above, and the bits are written as they are.</summary>
        public uint ChannelFlags { get; set; }
    }
}
