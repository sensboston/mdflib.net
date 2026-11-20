// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
namespace MdfLibrary
{
    /// <summary>The kind of a channel array (ca_type).</summary>
    public enum ArrayType : byte
    {
        /// <summary>A plain array of values.</summary>
        Array = 0,

        /// <summary>An array of scaling axis values.</summary>
        ScalingAxis = 1,

        /// <summary>A look-up table.</summary>
        LookUp = 2,

        /// <summary>An interval axis.</summary>
        IntervalAxis = 3,

        /// <summary>A classification result.</summary>
        ClassificationResult = 4,
    }

    /// <summary>Where the elements of a channel array are stored (ca_storage).</summary>
    public enum ArrayStorage : byte
    {
        /// <summary>The array elements are stored in the channel's own record.</summary>
        CnTemplate = 0,

        /// <summary>The array elements are stored in channel groups.</summary>
        CgTemplate = 1,

        /// <summary>The array elements are stored in data groups.</summary>
        DgTemplate = 2,
    }

    /// <summary>Channel array flags (ca_flags).</summary>
    public enum CaFlag : uint
    {
        /// <summary>No flags.</summary>
        None = 0,

        /// <summary>The array size can change from record to record.</summary>
        DynamicSize = 1,

        /// <summary>The array has input quantities.</summary>
        InputQuantity = 2,

        /// <summary>The array has an output quantity.</summary>
        OutputQuantity = 4,

        /// <summary>The array has a comparison quantity.</summary>
        ComparisonQuantity = 8,

        /// <summary>The array has axes.</summary>
        Axis = 16,

        /// <summary>The array has fixed axes.</summary>
        FixedAxis = 32,

        /// <summary>Elements are stored in inverse (column major) order.</summary>
        InverseLayout = 64,

        /// <summary>Intervals are open on the left.</summary>
        LeftOpenInterval = 128,

        /// <summary>Standard axes are used.</summary>
        StandardAxis = 65536,
    }

    /// <summary>A channel array block (CA) of a channel.</summary>
    public class MdfChannelArray
    {
        // Instances come from the facade factories; the wrapper has no public constructor
        // either, so the declaration layer keeps the type non-constructible by name.
        private readonly MdfLib.MdfChannelArray engine_;

        /// <summary>The handle of one array shape read from a channel; a null snapshot reports the default values.</summary>
        internal MdfChannelArray(MdfLib.MdfChannelArray engine)
        {
            engine_ = engine;
        }

        /// <summary>Gets or sets the flags of the array.</summary>
        public CaFlag Flags
        {
            get
            {
                return engine_ == null ? CaFlag.None : (CaFlag)engine_.RawFlags;
            }
            set
            {
                throw new NotSupportedException("MdfChannelArray.Flags: writing channel arrays is not supported in 1.0 (planned for 1.1).");
            }
        }

        /// <summary>Gets or sets where the array elements are stored.</summary>
        public ArrayStorage Storage
        {
            get
            {
                return engine_ == null ? ArrayStorage.CnTemplate : (ArrayStorage)engine_.RawStorage;
            }
            set
            {
                throw new NotSupportedException("MdfChannelArray.Storage: writing channel arrays is not supported in 1.0 (planned for 1.1).");
            }
        }

        /// <summary>Gets or sets the kind of array.</summary>
        public ArrayType Type
        {
            get
            {
                return engine_ == null ? ArrayType.Array : (ArrayType)engine_.RawType;
            }
            set
            {
                throw new NotSupportedException("MdfChannelArray.Type: writing channel arrays is not supported in 1.0 (planned for 1.1).");
            }
        }
    }

}
