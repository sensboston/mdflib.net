// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System.Collections.Generic;

namespace MdfLib
{
    /// <summary>An immutable fixed-array shape. Capability is reported by its owning channel.</summary>
    internal sealed class MdfChannelArray
    {
        internal MdfChannelArray(IReadOnlyList<ulong> dimensions, ulong elementCount)
            : this(dimensions, elementCount, 0, 0, 0)
        {
        }

        internal MdfChannelArray(IReadOnlyList<ulong> dimensions, ulong elementCount, byte type, byte storage, uint flags)
        {
            Dimensions = dimensions;
            ElementCount = elementCount;
            RawType = type;
            RawStorage = storage;
            RawFlags = flags;
        }

        /// <summary>The raw ca_type: 0 array, 1 scaling axis, 2 look-up, 3 interval axis, 4 classification result.</summary>
        internal byte RawType { get; }

        /// <summary>The raw ca_storage: 0 channel template, 1 channel group template, 2 data group template.</summary>
        internal byte RawStorage { get; }

        /// <summary>The raw ca_flags of the array block.</summary>
        internal uint RawFlags { get; }

        /// <summary>Gets positive dimension sizes in stored order; the last index varies fastest in supported arrays.</summary>
        public IReadOnlyList<ulong> Dimensions { get; }

        /// <summary>Gets the exact product of the dimension sizes, without materializing elements.</summary>
        public ulong ElementCount { get; }
    }
}
