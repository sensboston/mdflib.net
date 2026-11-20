// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// The conversion handle. The wrapper edits a live CC block through its raw
// parameter and reference arrays; the facade keeps the same arrays and turns them into the engine's factory call
// for the type it was set to, so a conversion the managed writer cannot encode is refused when the file is built.
using System;
namespace MdfLibrary
{
    /// <summary>One CC block of a file.</summary>
    public class MdfChannelConversion
    {
        private readonly FacadeContext context_;
        private readonly PendingConversion pending_;

        /// <summary>The handle of one pending conversion; only the facade's factories create it.</summary>
        internal MdfChannelConversion(FacadeContext context, PendingConversion pending)
        {
            context_ = context;
            pending_ = pending;
        }

        /// <summary>The cc_flags bit marking cc_decimals as written.</summary>
        public const ushort PrecisionValid = 1;

        /// <summary>The cc_flags bit marking the conversion range as written.</summary>
        public const ushort RangeValid = 2;

        /// <summary>The cc_flags bit of a status string conversion.</summary>
        public const ushort StatusString = 4;

        /// <summary>The inverse CC block, which the managed writer does not write.</summary>
        public MdfChannelConversion CreateInverse()
        {
            context_.RejectFlushed();
            if (context_.IsMdf3) { throw new NotSupportedException("MDF 3.30 has no inverse conversion block."); }
            pending_.Inverse = new PendingConversion(context_);
            return new MdfChannelConversion(context_, pending_.Inverse);
        }

        /// <summary>Creates the MD block of the conversion, or reports the one it already has.
        /// Its tags are written with the conversion's comment block.</summary>
        public MdfMetaData CreateMetaData()
        {
            context_.RejectFlushed();
            if (pending_.Metadata == null) { pending_.Metadata = new MdfMetaData(); }
            return pending_.Metadata;
        }

        /// <summary>One entry of the integer parameter array.</summary>
        public ulong ParameterUInt(ushort index)
        {
            return pending_.ParameterUInt(index);
        }

        /// <summary>Writes one entry of the integer parameter array, growing it as the
        /// wrapper does.</summary>
        public void ParameterUInt(ushort index, ulong parameter)
        {
            context_.RejectFlushed();
            pending_.ParameterUInt(index, parameter);
        }

        /// <summary>One entry of the raw parameter array; zero for an index the
        /// conversion does not have.</summary>
        public double Parameter(ushort index)
        {
            return pending_.Parameter(index);
        }

        /// <summary>Writes one entry of the raw parameter array, growing it as the
        /// wrapper does; the engine factory of the conversion type reads the values back.</summary>
        public void Parameter(ushort index, double parameter)
        {
            context_.RejectFlushed();
            pending_.Parameter(index, parameter);
        }

        /// <summary>One text of the conversion, empty for an index it does not have.</summary>
        public string Reference(ushort index)
        {
            return pending_.Reference(index);
        }

        /// <summary>Writes one text of the conversion, growing the list as the wrapper
        /// does.</summary>
        public void Reference(ushort index, string reference)
        {
            context_.RejectFlushed();
            pending_.Reference(index, reference ?? string.Empty);
        }

        /// <summary>The comment of the conversion. MDF 4 links it as a TX/MD block,
        /// which the writer of a conversion definition does not create, so it is refused when the file is built.</summary>
        public string Description
        {
            get
            {
                return pending_.Description;
            }
            set
            {
                context_.RejectFlushed();
                pending_.Description = value ?? string.Empty;
            }
        }

        /// <summary>Cc_flags. The managed writer sets the bits it needs itself, so a
        /// conversion cannot carry any.</summary>
        public ushort Flags
        {
            get
            {
                return pending_.Flags;
            }
            set
            {
                context_.RejectFlushed();
                pending_.Flags = value;
            }
        }

        /// <summary>The formula of an algebraic conversion (MDF 4 cc_formula).</summary>
        public string Formula
        {
            get
            {
                return pending_.Formula;
            }
            set
            {
                context_.RejectFlushed();
                pending_.Formula = value ?? string.Empty;
            }
        }

        /// <summary>The id of the live block, which the facade numbers as it creates
        /// them; the file has no such field.</summary>
        public long Index
        {
            get
            {
                return pending_.Index;
            }
        }

        /// <summary>The inverse conversion, which a writer never has.</summary>
        public MdfChannelConversion Inverse
        {
            get
            {
                return null;
            }
        }

        /// <summary>The MD block of the conversion, or null when it has none.</summary>
        public MdfMetaData MetaData
        {
            get
            {
                return pending_.Metadata;
            }
        }

        /// <summary>Cc_name. Written as cc_tx_name on an MDF 4 conversion block; refused for MDF 3 and linear conversions.</summary>
        public string Name
        {
            get
            {
                return pending_.Name;
            }
            set
            {
                context_.RejectFlushed();
                pending_.Name = value ?? string.Empty;
            }
        }

        /// <summary>The number of raw parameters the conversion holds.</summary>
        public ushort NofParameters
        {
            get
            {
                return (ushort)pending_.Parameters.Count;
            }
        }

        /// <summary>The number of text references the conversion holds.</summary>
        public ushort NofReferences
        {
            get
            {
                return (ushort)pending_.References.Count;
            }
        }

        /// <summary>Whether cc_decimals is marked valid in cc_flags.</summary>
        public bool PrecisionUsed
        {
            get
            {
                return (pending_.Flags & PrecisionValid) != 0;
            }
        }

        /// <summary>Cc_decimals, which the managed writer does not store; like the
        /// wrapper the getter answers the default two digits until a value is set.</summary>
        public byte Precision
        {
            get
            {
                return pending_.HasPrecision ? pending_.Precision : (byte)2;
            }
            set
            {
                context_.RejectFlushed();
                pending_.HasPrecision = true;
                pending_.Precision = value;
            }
        }

        /// <summary>Whether the conversion range is marked valid.</summary>
        public bool RangeUsed
        {
            get
            {
                return pending_.HasRange || (pending_.Flags & RangeValid) != 0;
            }
        }

        /// <summary>Cc_range_min and cc_range_max, read-only in the engine.</summary>
        public System.Tuple<double, double> Range
        {
            get
            {
                return pending_.HasRange ? System.Tuple.Create(pending_.RangeMin, pending_.RangeMax) : System.Tuple.Create(0.0, 0.0);
            }
            set
            {
                context_.RejectFlushed();
                pending_.HasRange = value != null && (value.Item1 != 0 || value.Item2 != 0);
                if (value != null) { pending_.RangeMin = value.Item1; pending_.RangeMax = value.Item2; }
            }
        }

        /// <summary>Cc_type, which decides which engine factory the parameters go to.</summary>
        public ConversionType Type
        {
            get
            {
                return pending_.Type;
            }
            set
            {
                context_.RejectFlushed();
                pending_.Type = value;
            }
        }

        /// <summary>Whether a unit is set on the conversion.</summary>
        public bool UnitUsed
        {
            get
            {
                return pending_.Unit.Length != 0;
            }
        }

        /// <summary>Cc_unit. Written as cc_md_unit on an MDF 4 conversion block; refused for MDF 3 and linear conversions.</summary>
        public string Unit
        {
            get
            {
                return pending_.Unit;
            }
            set
            {
                context_.RejectFlushed();
                pending_.Unit = value ?? string.Empty;
            }
        }
    }

    /// <summary>The kind of a channel conversion (cc_type).</summary>
    public enum ConversionType : byte
    {
        /// <summary>One to one: the physical value is the raw value.</summary>
        NoConversion = 0,

        /// <summary>Linear: p = a * r + b.</summary>
        Linear = 1,

        /// <summary>Rational: a ratio of two second degree polynomials.</summary>
        Rational = 2,

        /// <summary>Algebraic: a formula in text form.</summary>
        Algebraic = 3,

        /// <summary>Value to value, interpolated between table rows.</summary>
        ValueToValueInterpolation = 4,

        /// <summary>Value to value, without interpolation.</summary>
        ValueToValue = 5,

        /// <summary>Value range to value.</summary>
        ValueRangeToValue = 6,

        /// <summary>Value to text.</summary>
        ValueToText = 7,

        /// <summary>Value range to text.</summary>
        ValueRangeToText = 8,

        /// <summary>Text to value.</summary>
        TextToValue = 9,

        /// <summary>Text to text (translation).</summary>
        TextToTranslation = 10,

        /// <summary>Bitfield to text.</summary>
        BitfieldToText = 11,

        /// <summary>MDF 3 polynomial.</summary>
        Polynomial = 30,

        /// <summary>MDF 3 exponential.</summary>
        Exponential = 31,

        /// <summary>MDF 3 logarithmic.</summary>
        Logarithmic = 32,

        /// <summary>MDF 3 date conversion.</summary>
        DateConversion = 33,

        /// <summary>MDF 3 time conversion.</summary>
        TimeConversion = 34,
    }
}
