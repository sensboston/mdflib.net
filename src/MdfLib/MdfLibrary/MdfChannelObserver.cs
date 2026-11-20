// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// The channel observer of the facade reader. It reads the engine batches the reader
// loaded for its channel group and answers with the wrapper's rules of IChannelObserver (v2.3.0): the raw value follows
// the channel's data type, the engineering value follows the physical batch when the channel has a readable conversion,
// and a sample the file does not have answers false with the default value.
using System;
using System.Collections.Generic;

namespace MdfLibrary
{
    /// <summary>A channel and the samples of its data group that the reader has
    /// loaded. A null observer (a channel type the wrapper cannot read) keeps the handle with empty answers.</summary>
    public class MdfChannelObserver
    {
        private PendingGroup group_;
        private PendingChannel channel_;

        // Instances come from the facade factories; the wrapper has no public constructor
        // either, so the declaration layer keeps the type non-constructible by name.
        private MdfChannelObserver()
        {
        }

        /// <summary>The observer of one channel of a loaded channel group.</summary>
        internal MdfChannelObserver(PendingGroup group, PendingChannel channel)
        {
            group_ = group;
            channel_ = channel;
        }

        // The observer of a channel, or null when the wrapper would return null: only the data types the wrapper's
        // CreateChannelObserver switch knows (numbers, text, byte arrays and CANopen times) get an observer.
        /// <summary>The observer of one channel, or null when the wrapper would return null for it.</summary>
        internal static MdfChannelObserver Create(PendingGroup group, PendingChannel channel)
        {
            return IsReadable(channel.DataType) ? new MdfChannelObserver(group, channel) : null;
        }

        // The observer of a channel the factory lists without an observer: a handle whose state is empty.
        /// <summary>The observer of one channel, or an empty handle when the wrapper would hand out a null entry.</summary>
        internal static MdfChannelObserver CreateOrEmpty(PendingGroup group, PendingChannel channel)
        {
            return Create(group, channel) ?? new MdfChannelObserver();
        }

        private static bool IsReadable(ChannelDataType type)
        {
            switch (type)
            {
                case ChannelDataType.UnsignedIntegerLe:
                case ChannelDataType.UnsignedIntegerBe:
                case ChannelDataType.SignedIntegerLe:
                case ChannelDataType.SignedIntegerBe:
                case ChannelDataType.FloatLe:
                case ChannelDataType.FloatBe:
                case ChannelDataType.StringAscii:
                case ChannelDataType.StringUTF8:
                case ChannelDataType.StringUTF16Le:
                case ChannelDataType.StringUTF16Be:
                case ChannelDataType.ByteArray:
                case ChannelDataType.MimeSample:
                case ChannelDataType.MimeStream:
                case ChannelDataType.CanOpenDate:
                case ChannelDataType.CanOpenTime:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>The channel of the observer, or null for a null observer.</summary>
        public MdfChannel Channel
        {
            get
            {
                return channel_ == null ? null : PendingChannel.FacadeOf(channel_);
            }
        }

        /// <summary>Releases the observer. The samples stay with the reader.</summary>
        public void Dispose()
        {
            group_ = null;
            channel_ = null;
        }

        /// <summary>The raw bytes of a byte-array channel sample, false for other types.</summary>
        public bool GetChannelValueAsArray(ulong sample, ref byte[] value)
        {
            value = new byte[0];
            MdfLib.MdfSampleValue sampleValue;
            if (!Find(false, sample, out sampleValue)) { return false; }
            // The wrapper answers bytes for a byte-array channel only: a fixed CANopen date or time is a number (see CanOpenValue).
            if (IsCanOpenType() && channel_.Type != ChannelType.VariableLength && channel_.Type != ChannelType.MaxLength) { return false; }
            // A byte-array sample is read as bytes whatever the channel's type: a CANopen date or time in a VLSD holds bytes.
            if (!IsByteArrayType() && sampleValue.Kind != MdfLib.MdfValueKind.ByteArray) { return false; }
            value = sampleValue.Kind == MdfLib.MdfValueKind.ByteArray ? sampleValue.GetByteArrayValue() : new byte[0];
            return sampleValue.IsValid;
        }

        /// <summary>The raw value as a float, false for a channel the wrapper cannot read as one.</summary>
        public bool GetChannelValueAsFloat(ulong sample, ref double value)
        {
            bool valid;
            value = RawAsFloat(sample, out valid);
            return valid;
        }

        /// <summary>The raw value as a signed integer.</summary>
        public bool GetChannelValueAsSigned(ulong sample, ref long value)
        {
            bool valid;
            value = RawAsSigned(sample, out valid);
            return valid;
        }

        /// <summary>The raw value as text, with the wrapper's formatting of each data type.</summary>
        public bool GetChannelValueAsString(ulong sample, ref string value)
        {
            bool valid;
            value = RawAsString(sample, out valid);
            return valid;
        }

        /// <summary>The raw value as an unsigned integer.</summary>
        public bool GetChannelValueAsUnsigned(ulong sample, ref ulong value)
        {
            bool valid;
            value = RawAsUnsigned(sample, out valid);
            return valid;
        }

        /// <summary>The wrapper returns the raw bytes here as well (GetEngValue of a byte array is the
        /// channel value), so this is the same answer as GetChannelValueAsArray.</summary>
        public bool GetEngValueAsArray(ulong sample, ref byte[] value)
        {
            return GetChannelValueAsArray(sample, ref value);
        }

        /// <summary>The engineering value as a float (the raw value without a readable conversion).</summary>
        public bool GetEngValueAsFloat(ulong sample, ref double value)
        {
            if (!HasEngineering()) { return GetChannelValueAsFloat(sample, ref value); }
            MdfLib.MdfSampleValue sampleValue;
            bool found = Find(true, sample, out sampleValue);
            value = found ? PhysicalAsFloat(sampleValue) : 0.0;
            return found && sampleValue.IsValid;
        }

        /// <summary>The engineering value as a signed integer.</summary>
        public bool GetEngValueAsSigned(ulong sample, ref long value)
        {
            if (!HasEngineering()) { return GetChannelValueAsSigned(sample, ref value); }
            MdfLib.MdfSampleValue sampleValue;
            bool found = Find(true, sample, out sampleValue);
            value = found ? PhysicalAsSigned(sampleValue) : 0L;
            return found && sampleValue.IsValid;
        }

        /// <summary>The engineering value as text (the conversion's text, or a formatted number).</summary>
        public bool GetEngValueAsString(ulong sample, ref string value)
        {
            if (!HasEngineering()) { return GetChannelValueAsString(sample, ref value); }
            MdfLib.MdfSampleValue sampleValue;
            bool found = Find(true, sample, out sampleValue);
            value = found ? PhysicalAsString(sampleValue) : string.Empty;
            return found && sampleValue.IsValid;
        }

        /// <summary>The engineering value as an unsigned integer.</summary>
        public bool GetEngValueAsUnsigned(ulong sample, ref ulong value)
        {
            if (!HasEngineering()) { return GetChannelValueAsUnsigned(sample, ref value); }
            MdfLib.MdfSampleValue sampleValue;
            bool found = Find(true, sample, out sampleValue);
            value = found ? PhysicalAsUnsigned(sampleValue) : 0UL;
            return found && sampleValue.IsValid;
        }

        /// <summary>True for a master channel (a time, virtual time or other master channel).</summary>
        public bool IsMaster()
        {
            return channel_ != null && (channel_.Type == ChannelType.Master || channel_.Type == ChannelType.VirtualMaster);
        }

        /// <summary>The channel name, empty for a null observer.</summary>
        public string Name
        {
            get
            {
                return channel_ == null ? string.Empty : channel_.Name;
            }
        }

        /// <summary>The number of samples of the channel group, which the reader has loaded or not.</summary>
        public ulong NofSamples
        {
            get
            {
                return group_ == null ? 0UL : group_.SampleCount;
            }
        }

        /// <summary>The channel unit, empty for a null observer.</summary>
        public string Unit
        {
            get
            {
                return channel_ == null ? string.Empty : channel_.Unit;
            }
        }

        // ---- sample access -------------------------------------------------------------------------------------------

        // The sample of this channel from the raw or the physical batches of its group. False when the reader has not
        // loaded the sample or the channel has no column in those batches.
        private bool Find(bool physical, ulong sample, out MdfLib.MdfSampleValue value)
        {
            value = default(MdfLib.MdfSampleValue);
            if (group_ == null || channel_ == null) { return false; }
            List<int> indices = physical ? group_.PhysIndices : group_.RawIndices;
            List<MdfLib.MdfSampleBatch> batches = physical ? group_.PhysBatches : group_.RawBatches;
            int column = indices.IndexOf(channel_.Index);
            if (column < 0) { return false; }
            foreach (MdfLib.MdfSampleBatch batch in batches)
            {
                ulong first = batch.FirstSampleIndex;
                if (sample < first || sample - first >= (ulong)batch.SampleCount) { continue; }
                // The first element of an array channel is the value the wrapper reads, as its array index is zero.
                value = batch.GetValue((int)(sample - first), column, 0UL);
                return true;
            }
            return false;
        }

        private bool HasEngineering()
        {
            if (channel_ == null || channel_.Conversion == null) { return false; }
            if (channel_.Conversion.Type == ConversionType.NoConversion) { return false; }
            switch (channel_.DataType)
            {
                case ChannelDataType.UnsignedIntegerLe:
                case ChannelDataType.UnsignedIntegerBe:
                case ChannelDataType.SignedIntegerLe:
                case ChannelDataType.SignedIntegerBe:
                case ChannelDataType.FloatLe:
                case ChannelDataType.FloatBe:
                case ChannelDataType.StringAscii:
                case ChannelDataType.StringUTF8:
                case ChannelDataType.StringUTF16Le:
                case ChannelDataType.StringUTF16Be:
                    return true;
                default:
                    return false;
            }
        }

        private bool IsByteArrayType()
        {
            return channel_ != null && (channel_.DataType == ChannelDataType.ByteArray
                || channel_.DataType == ChannelDataType.MimeSample || channel_.DataType == ChannelDataType.MimeStream);
        }

        private bool IsNumericType()
        {
            return channel_ != null && (channel_.DataType == ChannelDataType.UnsignedIntegerLe
                || channel_.DataType == ChannelDataType.UnsignedIntegerBe || channel_.DataType == ChannelDataType.SignedIntegerLe
                || channel_.DataType == ChannelDataType.SignedIntegerBe || channel_.DataType == ChannelDataType.FloatLe
                || channel_.DataType == ChannelDataType.FloatBe);
        }

        private bool IsCanOpenType()
        {
            return channel_ != null && (channel_.DataType == ChannelDataType.CanOpenDate || channel_.DataType == ChannelDataType.CanOpenTime);
        }

        private bool IsTextType()
        {
            return channel_ != null && (channel_.DataType == ChannelDataType.StringAscii || channel_.DataType == ChannelDataType.StringUTF8
                || channel_.DataType == ChannelDataType.StringUTF16Le || channel_.DataType == ChannelDataType.StringUTF16Be);
        }

        // ---- raw values, following IChannelObserver::GetChannelValue<V> of the data type -----------------------------

        private ulong RawAsUnsigned(ulong sample, out bool valid)
        {
            valid = false;
            MdfLib.MdfSampleValue value;
            if (!Find(false, sample, out value)) { return 0UL; }
            valid = value.IsValid;
            ulong nanoseconds;
            if (CanOpenValue(value, out nanoseconds)) { return nanoseconds; }
            if (IsNumericType() || IsCanOpenType())
            {
                switch (value.Kind)
                {
                    case MdfLib.MdfValueKind.UnsignedInteger: return value.UnsignedValue;
                    case MdfLib.MdfValueKind.SignedInteger: return unchecked((ulong)value.SignedValue);
                    case MdfLib.MdfValueKind.FloatingPoint: return DoubleToUnsigned(value.FloatingPointValue);
                }
                return 0UL;
            }
            if (IsTextType()) { return FacadeFormat.ParseUnsigned(TextOf(value)); }
            if (IsByteArrayType()) { return FirstByte(value); }
            valid = false;
            return 0UL;
        }

        // The wrapper reads a fixed CANopen date or time as its nanoseconds (IChannel::GetCanOpenDate and GetCanOpenTime).
        private bool CanOpenValue(MdfLib.MdfSampleValue value, out ulong nanoseconds)
        {
            nanoseconds = 0UL;
            if (!IsCanOpenType() || value.Kind != MdfLib.MdfValueKind.ByteArray) { return false; }
            nanoseconds = FacadeFormat.CanOpenNs(BytesOf(value), channel_.DataType == ChannelDataType.CanOpenDate);
            return true;
        }

        private long RawAsSigned(ulong sample, out bool valid)
        {
            valid = false;
            MdfLib.MdfSampleValue value;
            if (!Find(false, sample, out value)) { return 0L; }
            valid = value.IsValid;
            ulong nanoseconds;
            if (CanOpenValue(value, out nanoseconds)) { return unchecked((long)nanoseconds); }
            if (IsNumericType() || IsCanOpenType())
            {
                switch (value.Kind)
                {
                    case MdfLib.MdfValueKind.UnsignedInteger: return unchecked((long)value.UnsignedValue);
                    case MdfLib.MdfValueKind.SignedInteger: return value.SignedValue;
                    case MdfLib.MdfValueKind.FloatingPoint: return DoubleToSigned(value.FloatingPointValue);
                }
                return 0L;
            }
            if (IsTextType()) { return FacadeFormat.ParseSigned(TextOf(value)); }
            if (IsByteArrayType()) { return (long)FirstByte(value); }
            valid = false;
            return 0L;
        }

        private double RawAsFloat(ulong sample, out bool valid)
        {
            valid = false;
            MdfLib.MdfSampleValue value;
            if (!Find(false, sample, out value)) { return 0.0; }
            valid = value.IsValid;
            ulong nanoseconds;
            if (CanOpenValue(value, out nanoseconds)) { return (double)nanoseconds; }
            if (IsNumericType() || IsCanOpenType())
            {
                switch (value.Kind)
                {
                    case MdfLib.MdfValueKind.UnsignedInteger: return (double)value.UnsignedValue;
                    case MdfLib.MdfValueKind.SignedInteger: return (double)value.SignedValue;
                    case MdfLib.MdfValueKind.FloatingPoint: return value.FloatingPointValue;
                }
                return 0.0;
            }
            if (IsTextType()) { return FacadeFormat.ParseDouble(TextOf(value)); }
            if (IsByteArrayType()) { return (double)FirstByte(value); }
            valid = false;
            return 0.0;
        }

        private string RawAsString(ulong sample, out bool valid)
        {
            valid = false;
            MdfLib.MdfSampleValue value;
            if (!Find(false, sample, out value)) { return string.Empty; }
            valid = value.IsValid;
            if (value.Kind == MdfLib.MdfValueKind.ByteArray) { return FacadeFormat.HexText(BytesOf(value)); }
            if (IsCanOpenType()) { return value.Kind == MdfLib.MdfValueKind.UnsignedInteger ? FacadeFormat.NsToLocalIsoTime(value.UnsignedValue) : string.Empty; }
            if (IsNumericType())
            {
                switch (value.Kind)
                {
                    case MdfLib.MdfValueKind.UnsignedInteger: return value.UnsignedValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    case MdfLib.MdfValueKind.SignedInteger: return value.SignedValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    case MdfLib.MdfValueKind.FloatingPoint: return FacadeFormat.FormatDouble(value.FloatingPointValue, 6);
                }
                return string.Empty;
            }
            if (IsTextType()) { return TextOf(value); }
            if (IsByteArrayType()) { return FacadeFormat.HexText(BytesOf(value)); }
            valid = false;
            return string.Empty;
        }

        // ---- engineering values of a physical batch, following IChannelObserver::GetEngValue<V> -----------------------

        private static ulong PhysicalAsUnsigned(MdfLib.MdfSampleValue value)
        {
            switch (value.Kind)
            {
                case MdfLib.MdfValueKind.UnsignedInteger: return value.UnsignedValue;
                case MdfLib.MdfValueKind.SignedInteger: return unchecked((ulong)value.SignedValue);
                case MdfLib.MdfValueKind.FloatingPoint: return DoubleToUnsigned(value.FloatingPointValue);
                case MdfLib.MdfValueKind.Text: return FacadeFormat.ParseUnsigned(value.TextValue ?? string.Empty);
            }
            return 0UL;
        }

        private static long PhysicalAsSigned(MdfLib.MdfSampleValue value)
        {
            switch (value.Kind)
            {
                case MdfLib.MdfValueKind.UnsignedInteger: return unchecked((long)value.UnsignedValue);
                case MdfLib.MdfValueKind.SignedInteger: return value.SignedValue;
                case MdfLib.MdfValueKind.FloatingPoint: return DoubleToSigned(value.FloatingPointValue);
                case MdfLib.MdfValueKind.Text: return FacadeFormat.ParseSigned(value.TextValue ?? string.Empty);
            }
            return 0L;
        }

        private static double PhysicalAsFloat(MdfLib.MdfSampleValue value)
        {
            switch (value.Kind)
            {
                case MdfLib.MdfValueKind.UnsignedInteger: return (double)value.UnsignedValue;
                case MdfLib.MdfValueKind.SignedInteger: return (double)value.SignedValue;
                case MdfLib.MdfValueKind.FloatingPoint: return value.FloatingPointValue;
                case MdfLib.MdfValueKind.Text: return FacadeFormat.ParseDouble(value.TextValue ?? string.Empty);
            }
            return 0.0;
        }

        private static string PhysicalAsString(MdfLib.MdfSampleValue value)
        {
            switch (value.Kind)
            {
                case MdfLib.MdfValueKind.Text: return value.TextValue ?? string.Empty;
                case MdfLib.MdfValueKind.UnsignedInteger: return value.UnsignedValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case MdfLib.MdfValueKind.SignedInteger: return value.SignedValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case MdfLib.MdfValueKind.FloatingPoint: return FacadeFormat.FormatDouble(value.FloatingPointValue, 6);
            }
            return string.Empty;
        }

        // ---- conversions of the stored values: C++ static_cast semantics, saturated where C++ is undefined ----------

        private static ulong DoubleToUnsigned(double value)
        {
            if (double.IsNaN(value)) { return 0UL; }
            if (value < 0.0) { return unchecked((ulong)DoubleToSigned(value)); }
            if (value >= 18446744073709551615.0) { return ulong.MaxValue; }
            return (ulong)value;
        }

        private static long DoubleToSigned(double value)
        {
            if (double.IsNaN(value)) { return 0L; }
            if (value <= -9223372036854775808.0) { return long.MinValue; }
            if (value >= 9223372036854775807.0) { return long.MaxValue; }
            return (long)value;
        }

        private static string TextOf(MdfLib.MdfSampleValue value)
        {
            return value.Kind == MdfLib.MdfValueKind.Text ? (value.TextValue ?? string.Empty) : string.Empty;
        }

        private static byte[] BytesOf(MdfLib.MdfSampleValue value)
        {
            return value.Kind == MdfLib.MdfValueKind.ByteArray ? value.GetByteArrayValue() : new byte[0];
        }

        private static ulong FirstByte(MdfLib.MdfSampleValue value)
        {
            byte[] bytes = BytesOf(value);
            return bytes.Length == 0 ? 0UL : bytes[0];
        }
    }
}
