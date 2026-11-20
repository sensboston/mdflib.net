// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
namespace MdfLib
{
    /// <summary>Identifies the exact representation of a value: numeric kinds, text or byte array.</summary>
    internal enum MdfValueKind
    {
        /// <summary>An exact unsigned integer, without conversion through double.</summary>
        UnsignedInteger = 0,
        /// <summary>An exact signed integer.</summary>
        SignedInteger = 1,
        /// <summary>An IEEE floating-point value represented as double.</summary>
        FloatingPoint = 2,
        /// <summary>A storage encoding outside the supported scalar subset.</summary>
        Unsupported = 3,
        /// <summary>Decoded text (MDF 4 Latin-1/UTF-8/UTF-16LE/BE; MDF 3 by the ID code page); exact stored bytes remain available.</summary>
        Text = 4,
        /// <summary>An exact MDF 3/4 byte array without text decoding.</summary>
        ByteArray = 5,
        /// <summary>An MDF 4 complex number (data types 15/16): IEEE real and imaginary parts widened exactly to double.</summary>
        Complex = 6
    }

    /// <summary>Identifies channel bytes that encode a calendar value (see <see cref="MdfCalendar"/>).</summary>
    internal enum MdfCalendarKind
    {
        /// <summary>The channel does not store a calendar value.</summary>
        None = 0,
        /// <summary>MDF 4 CANopen date (data type 13): 7 bytes of milliseconds, minute, hour, day, month and year.</summary>
        CanOpenDate = 1,
        /// <summary>MDF 4 CANopen time (data type 14): 6 bytes of milliseconds after midnight and days since 1984-01-01.</summary>
        CanOpenTime = 2
    }

    /// <summary>Identifies a supported raw-to-physical conversion.</summary>
    internal enum MdfConversionKind
    {
        /// <summary>No numerical conversion; exact integer kind is retained.</summary>
        Identity = 0,
        /// <summary>Physical = factor * raw + offset, evaluated as double.</summary>
        Linear = 1,
        /// <summary>An unevaluated conversion outside this reader's subset.</summary>
        Unsupported = 2,
        /// <summary>(P1*x*x + P2*x + P3) / (P4*x*x + P5*x + P6) with IEEE division (MDF 4 type 2, MDF 3 type 9).</summary>
        Rational = 3,
        /// <summary>Key/value table with linear interpolation, clamped to the end values (MDF 4 type 4, MDF 3 type 1).</summary>
        TableInterpolation = 4,
        /// <summary>Key/value table, nearest key, midpoint selects the lower key (MDF 4 type 5, MDF 3 type 2).</summary>
        Table = 5,
        /// <summary>First matching value range (inclusive upper bound for integers), else the default (MDF 4 type 6).</summary>
        RangeTable = 6,
        /// <summary>(P2 - P4*t) / (P3*t - P1) with t = x - P5 - P6 (MDF 3 type 6).</summary>
        Polynomial = 7,
        /// <summary>Exact key to TX text, else the default text; NIL is empty text (MDF 4 type 7). Physical kind Text.</summary>
        ValueToText = 8,
        /// <summary>First matching value range to TX text, else the default text (MDF 4 type 8). Physical kind Text.</summary>
        RangeToText = 9,
        /// <summary>Text channel: exact (ordinal) key text to value, else the default value (MDF 4 type 9). Physical kind FloatingPoint.</summary>
        TextToValue = 10,
        /// <summary>Text channel: exact key text to output text, else the default; a NIL output keeps the input (MDF 4 type 10).</summary>
        TextToText = 11,

        /// <summary>MDF 4 bitfield to text (cc_type 11): masked parts converted by value/range to text conversions, joined with '|'.</summary>
        BitfieldToText = 12,

        /// <summary>Algebraic formula of the raw value (MDF 4 cc_type 3, MDF 3 cc_type 10), evaluated in binary64.</summary>
        Algebraic = 13,

        /// <summary>MDF 3 exponential (cc_type 7): P4 = 0: exp(((x - P7) P6 - P3) / P1) / P2; P1 = 0: exp((P3 / (x - P7) - P6) / P4) / P5.</summary>
        Exponential = 14,

        /// <summary>MDF 3 logarithmic (cc_type 8): the exponential layout with the natural logarithm.</summary>
        Logarithmic = 15
    }

    /// <summary>Selects stored values or explicitly converted physical values.</summary>
    internal enum MdfValueRepresentation
    {
        /// <summary>Stored numeric values and their separate validity flags.</summary>
        Raw = 0,
        /// <summary>Identity or linear physical values; invalid rows are not dropped.</summary>
        Physical = 1
    }
}
