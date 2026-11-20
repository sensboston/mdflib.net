// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Internal support of the MdfLibrary facade: the pending tree the wrapper's live objects collect before the
// engine fixes its layout, the build that turns that tree into engine definitions, and the clock conversions of
// the wrapper's MdfNetHelper. The public types under src/MdfLib/MdfLibrary/ are thin handles over this state, so
// api/mdflibrary-api-baseline.txt stays the whole reviewed surface and nothing here is visible from outside.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace MdfLibrary
{
    // The two clock conversions of MdfNetHelper::GetUnixNanoTimestamp and GetLocalNanoTimestamp, shared by both
    // timestamp families. The wrapper's file timestamps read their DateTime straight from the stored nanoseconds
    // without any offset arithmetic (upstream MdfFileTimestamp.cpp), so FromNs is deliberately not the inverse.
    internal static class FacadeClock
    {
        internal static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        internal static ulong UnixNs(DateTime time)
        {
            return (ulong)time.ToUniversalTime().Subtract(Epoch).Ticks * 100UL;
        }

        internal static ulong LocalNs(DateTime time)
        {
            return (ulong)time.ToLocalTime().Subtract(Epoch).Ticks * 100UL;
        }

        internal static DateTime FromNs(ulong nanoseconds)
        {
            return Epoch.AddTicks((long)(nanoseconds / 100UL));
        }

        internal static ulong NowNs()
        {
            return (ulong)DateTime.UtcNow.Subtract(Epoch).Ticks * 100UL;
        }
    }

    // One SI block in the making; a source without a name writes no block at all, as in the engine. The
    // negative type numbers mean "the caller never chose one", which leaves the engine's own default in place.
    internal sealed class PendingSource
    {
        internal readonly FacadeContext Context;
        internal MdfSourceInformation Facade;
        internal MdfMetaData Metadata; // the SI block's common properties, created through CreateMetaData

        // The handle of this node, created once: a reader's node has none until the handle is asked for.
        internal static MdfSourceInformation FacadeOf(FacadeContext context, PendingSource source)
        {
            if (source.Facade == null) { source.Facade = new MdfSourceInformation(context, source); }
            return source.Facade;
        }
        internal string Name = string.Empty, Path = string.Empty, Comment = string.Empty;
        internal byte SiFlags; // si_flags as the file holds it, or as the user sets it; written as it is.
        internal int SourceType = -1, BusType = -1;
        internal bool SyncBus;
        internal long Index;

        internal PendingSource(FacadeContext context)
        {
            Context = context;
        }

        internal bool IsEmpty
        {
            get { return Name.Length == 0 && Path.Length == 0 && Comment.Length == 0 && (Metadata == null || Metadata.Properties.Length == 0); }
        }

        internal MdfLib.MdfSourceDefinition Definition()
        {
            MdfLib.MdfSourceDefinition result = new MdfLib.MdfSourceDefinition();
            result.Name = Name;
            result.Path = Path;
            result.Comment = Comment;
            result.CommonProperties = Metadata == null ? null : Metadata.WriterTags();
            if (SourceType >= 0) { result.SourceType = (byte)SourceType; }
            if (BusType >= 0) { result.BusType = (byte)BusType; }
            result.IsSyncBus = SyncBus;
            result.Flags = SiFlags;
            return result;
        }
    }

    // One CC block in the making. The wrapper's parameter and reference arrays grow when an index beyond the
    // current count is written, so the pending form keeps lists and reports zeros for the holes.
    internal sealed class PendingConversion
    {
        internal readonly FacadeContext Context;
        internal MdfChannelConversion Facade;

        // The handle of this node, created once: a reader's node has none until the handle is asked for.
        internal static MdfChannelConversion FacadeOf(FacadeContext context, PendingConversion conversion)
        {
            if (conversion.Facade == null) { conversion.Facade = new MdfChannelConversion(context, conversion); }
            return conversion.Facade;
        }
        internal ConversionType Type = ConversionType.NoConversion;
        internal string Name = string.Empty, Unit = string.Empty, Description = string.Empty, Formula = string.Empty;
        internal readonly List<double> Parameters = new List<double>();
        internal readonly List<string> References = new List<string>();
        internal readonly List<ulong> RawParameters = new List<ulong>();
        internal byte Precision;
        internal ushort Flags;
        internal bool HasPrecision, HasRange;
        internal PendingConversion Inverse; // cc_inverse, made by CreateInverse
        // A linear conversion is a block of its own only when it carries a precision, a range, flags or an inverse.
        internal bool NeedsBlock { get { return HasPrecision || HasRange || Flags != 0 || Inverse != null; } }
        internal double RangeMin, RangeMax;
        internal long Index;
        internal MdfMetaData Metadata; // the CC block's common properties, created through CreateMetaData

        internal PendingConversion(FacadeContext context)
        {
            Context = context;
        }

        internal double Parameter(ushort index)
        {
            return index < Parameters.Count ? Parameters[index] : 0.0;
        }

        internal void Parameter(ushort index, double value)
        {
            while (Parameters.Count <= index) { Parameters.Add(0.0); }
            Parameters[index] = value;
        }

        internal ulong ParameterUInt(ushort index)
        {
            return index < RawParameters.Count ? RawParameters[index] : 0UL;
        }

        internal void ParameterUInt(ushort index, ulong value)
        {
            while (RawParameters.Count <= index) { RawParameters.Add(0UL); }
            RawParameters[index] = value;
        }

        internal string Reference(ushort index)
        {
            return index < References.Count ? References[index] : string.Empty;
        }

        internal void Reference(ushort index, string value)
        {
            while (References.Count <= index) { References.Add(string.Empty); }
            References[index] = value;
        }

        // Builds the engine definition of this conversion. A linear conversion is a pair of channel fields, not
        // a factory, so PendingChannel handles that case before asking; every refusal names the engine limit.
        internal MdfLib.MdfConversionDefinition Definition()
        {
            MdfLib.MdfConversionDefinition definition = TypedDefinition();
            if (definition == null) { return null; }
            definition = definition.WithTexts(Name, Unit, Description).WithCommonProperties(Metadata == null ? null : Metadata.WriterTags());
            if (HasPrecision) { definition.Precision = Precision; }
            if (HasRange) { definition.RangeMin = RangeMin; definition.RangeMax = RangeMax; }
            definition.Flags = Flags;
            if (Inverse != null) { definition.Inverse = Inverse.Definition(); }
            return definition;
        }

        private MdfLib.MdfConversionDefinition TypedDefinition()
        {
            double[] values = Parameters.ToArray();
            string[] texts = References.ToArray();
            switch (Type)
            {
                case ConversionType.NoConversion: return null;
                case ConversionType.Linear: return LinearDefinition(values);
                case ConversionType.Rational: return MdfLib.MdfConversionDefinition.Rational(One(values, 0), One(values, 1), One(values, 2), One(values, 3), One(values, 4), One(values, 5));
                case ConversionType.Polynomial: return MdfLib.MdfConversionDefinition.Polynomial(One(values, 0), One(values, 1), One(values, 2), One(values, 3), One(values, 4), One(values, 5));
                case ConversionType.Exponential: return MdfLib.MdfConversionDefinition.Exponential(values);
                case ConversionType.Logarithmic: return MdfLib.MdfConversionDefinition.Logarithmic(values);
                case ConversionType.Algebraic: return MdfLib.MdfConversionDefinition.Algebraic(Formula);
                case ConversionType.ValueToValue: return MdfLib.MdfConversionDefinition.Table(Pair(values, 0), Pair(values, 1), false);
                case ConversionType.ValueToValueInterpolation: return MdfLib.MdfConversionDefinition.Table(Pair(values, 0), Pair(values, 1), true);
                case ConversionType.ValueRangeToValue: return MdfLib.MdfConversionDefinition.RangeTable(Triple(values, 0), Triple(values, 1), Triple(values, 2), Default(values));
                case ConversionType.ValueToText:
                {
                    // One key per text (the engine reads n values and n + 1 references), not a key-and-value pair per text.
                    double[] keys = values;
                    string fallback;
                    string[] keyed = KeyedTexts(texts, keys.Length, out fallback);
                    return MdfLib.MdfConversionDefinition.ValueToText(keys, keyed, fallback);
                }
                case ConversionType.ValueRangeToText:
                {
                    double[] lower = Triple(values, 0);
                    string fallback;
                    string[] keyed = KeyedTexts(texts, lower.Length, out fallback);
                    return MdfLib.MdfConversionDefinition.RangeToText(lower, Triple(values, 1), keyed, fallback);
                }
                case ConversionType.TextToValue: return MdfLib.MdfConversionDefinition.TextToValue(texts, values, values.Length == texts.Length + 1 ? values[values.Length - 1] : 0.0);
                case ConversionType.TextToTranslation: return MdfLib.MdfConversionDefinition.TextToText(texts, texts, string.Empty);
                default:
                    throw new NotSupportedException("Conversion type " + Type + " has no writer in the managed engine: bit field, MIME and CANopen calendar conversions stay read-only.");
            }
        }

        // A linear conversion takes the offset and the factor, the pair the channel fields hold as Parameter(0) and (1).
        private static MdfLib.MdfConversionDefinition LinearDefinition(double[] values)
        {
            if (values.Length != 2) { throw new NotSupportedException("A linear conversion takes two parameters: the offset and the factor."); }
            return MdfLib.MdfConversionDefinition.Linear(values[0], values[1]);
        }

        private static double One(double[] values, int index)
        {
            if (values.Length < 6) { throw new NotSupportedException("A rational or polynomial conversion reads six parameters, so NofParameters must be 6 before the tree is written."); }
            return values[index];
        }

        // The wrapper keeps a conversion's default value in the slot after the last pair, which the engine takes
        // as its own argument; with no such slot the engine's own default applies.
        // The wrapper keeps one text per key and, after them, the text of the default case: a conversion with
        // one reference more than keys has that last text as its default. The engine takes the default as its own
        // argument, so the references are split here; any other count goes to the engine unchanged and is refused there.
        private static string[] KeyedTexts(string[] texts, int keys, out string fallback)
        {
            if (texts.Length != keys + 1)
            {
                fallback = string.Empty;
                return texts;
            }
            fallback = texts[keys];
            string[] keyed = new string[keys];
            Array.Copy(texts, keyed, keys);
            return keyed;
        }

        private static double Default(double[] values)
        {
            return values.Length % 3 == 0 ? 0.0 : values[values.Length - 1];
        }

        private static double[] Pair(double[] values, int index)
        {
            double[] result = new double[values.Length / 2];
            for (int i = 0; i < result.Length; i++) { result[i] = values[2 * i + index]; }
            return result;
        }

        private static double[] Triple(double[] values, int index)
        {
            double[] result = new double[(values.Length - 1) / 3];
            for (int i = 0; i < result.Length; i++) { result[i] = values[3 * i + index]; }
            return result;
        }
    }

    // One CN in the making, with the value the wrapper holds until the next SaveSample. The value keeps the
    // type the caller used, because the wrapper picks its overload by argument type and converts nothing.
    internal sealed class PendingChannel
    {
        internal readonly FacadeContext Context;
        internal MdfChannel Facade;
        internal PendingGroup Group;
        internal string Name = "Channel", Unit = string.Empty, Description = string.Empty, DisplayName = string.Empty;
        internal ChannelDataType DataType = ChannelDataType.FloatLe;
        internal ChannelType Type = ChannelType.FixedLength;
        internal ChannelSyncType Sync = ChannelSyncType.None;
        internal CnFlag Flags;
        internal uint BitCount;
        internal MdfMetaData Metadata; // the CN block's common properties, created through CreateMetaData
        internal ushort BitOffset;
        internal double SamplingRate;
        internal ulong VlsdRecordId;
        internal bool HasLimit, HasRange, HasExtLimit;
        internal double LimitMin, LimitMax, RangeMin, RangeMax, ExtLimitMin, ExtLimitMax;
        internal PendingConversion Conversion;
        internal PendingSource Source;
        internal int Index;
        internal bool HasValue, IsValid = true;
        // structures: Parent is set on a component; Components is set (even if empty) on a structure channel.
        internal PendingChannel Parent;
        internal List<PendingChannel> Components;
        internal int WindowOffset;
        internal bool IsStructure { get { return Components != null; } }
        internal object Value;

        internal PendingChannel(FacadeContext context, PendingGroup group)
        {
            Context = context;
            Group = group;
            Index = group.Channels.Count;
        }

        internal static MdfChannel FacadeOf(PendingChannel channel)
        {
            if (channel.Facade == null) { channel.Facade = new MdfChannel(channel.Context, channel); }
            return channel.Facade;
        }

        // The reviewed surface lets the caller give the width either as cn_bit_count or as the byte count the
        // upstream example uses (MdfChannelSetDataBytes); both name the same field, so the byte count is written as
        // the bit count the engine stores and DataBytes reads back. Overflow is refused, never cut.
        internal void SetWidthFromBytes(ulong bytes)
        {
            if (bytes > uint.MaxValue / 8UL)
            { throw new ArgumentOutOfRangeException("DataBytes", "The width in bytes must fit the bit count of the channel field; a channel of more than 536870911 bytes is not a MDF record."); }
            BitCount = checked((uint)(bytes * 8UL));
        }

        internal void Remember(object value, bool valid)
        {
            HasValue = true;
            Value = value;
            IsValid = valid;
        }

        internal MdfLib.MdfValueKind Kind
        {
            get
            {
                switch ((byte)DataType)
                {
                    case 0: return MdfLib.MdfValueKind.UnsignedInteger;
                    case 1: return MdfLib.MdfValueKind.UnsignedInteger;
                    case 2: return MdfLib.MdfValueKind.SignedInteger;
                    case 3: return MdfLib.MdfValueKind.SignedInteger;
                    case 4: return MdfLib.MdfValueKind.FloatingPoint;
                    case 5: return MdfLib.MdfValueKind.FloatingPoint;
                    case 7: return MdfLib.MdfValueKind.Text;
                    case 10: return MdfLib.MdfValueKind.ByteArray;
                    case 15: return MdfLib.MdfValueKind.Complex;
                    case 16: return MdfLib.MdfValueKind.Complex;
                    default:
                        throw new NotSupportedException("The channel data type " + DataType + " has no writer in the managed engine, which stores unsigned and signed integers, IEEE floats, UTF-8 text, byte arrays and (MDF 4) complex values; ASCII and UTF-16 text, MIME samples and CANopen calendar values stay read-only.");
                }
            }
        }

        internal bool IsBigEndian
        {
            get
            {
                return DataType == ChannelDataType.UnsignedIntegerBe || DataType == ChannelDataType.SignedIntegerBe
                    || DataType == ChannelDataType.FloatBe || DataType == ChannelDataType.ComplexBE;
            }
        }

        // The engine definition of this channel; every refusal the tree cannot satisfy runs in FacadeContext.Validate.
        internal MdfLib.MdfChannelDefinition Definition()
        {
            MdfLib.MdfChannelDefinition result = new MdfLib.MdfChannelDefinition();
            result.Name = Name;
            result.Unit = Unit;
            result.ValueKind = Kind;
            result.IsBigEndian = IsBigEndian;
            result.BitCount = (int)BitCount;
            result.IsMaster = Type == ChannelType.Master;
            result.IsVariableLength = Type == ChannelType.VariableLength;
            result.Comment = Description;
            result.CommonProperties = Metadata == null ? null : Metadata.WriterTags();
            if (Conversion != null)
            {
                if (Conversion.Type == ConversionType.Linear && !Conversion.NeedsBlock)
                {
                    result.HasLinearConversion = true;
                    result.Offset = Conversion.Parameter(0);
                    result.Factor = Conversion.Parameter(1);
                }
                else
                {
                    result.Conversion = Conversion.Definition();
                }
            }
            // The engine's validity bits and the caller's cn_flags bits; the range, limit and sync are the channel's own fields.
            result.SyncType = (byte)Sync;
            if (HasRange) { result.RangeMin = RangeMin; result.RangeMax = RangeMax; }
            if (HasLimit) { result.LimitMin = LimitMin; result.LimitMax = LimitMax; }
            if (HasExtLimit) { result.ExtLimitMin = ExtLimitMin; result.ExtLimitMax = ExtLimitMax; }
            result.ChannelFlags = (uint)Flags;
            MdfLib.MdfSourceDefinition source = Source == null || Source.IsEmpty ? null : Source.Definition();
            if (DisplayName.Length != 0)
            {
                if (source == null) { source = new MdfLib.MdfSourceDefinition(); }
                source.LongIdentifier = DisplayName;
            }
            result.Source = source;
            return result;
        }

        // The sample value the engine will encode. A text or byte array channel takes what the caller stored,
        // a numeric channel takes the number in the width its own type implies; the master channel of a group is
        // fed with the time SaveSample was given, which mdflib also converts to the master's type.
        // The master is computed from the sample time only when its sync is Time, as the upstream writer does; any other
        // master takes the value the caller sets, and it is refused without one.
        internal bool ComputesTime { get { return Type == ChannelType.Master && Sync == ChannelSyncType.Time; } }

        internal MdfLib.MdfSampleValue Sample(ulong? time, double? seconds)
        {
            bool fromTime = time.HasValue && ComputesTime;
            object value = fromTime ? time.Value : Value;
            switch (Kind)
            {
                case MdfLib.MdfValueKind.FloatingPoint:
                    // A time the SaveSample call supplies is the absolute nanosecond stamp; the master channel stores
                    // it as seconds from the start of the measurement, as the wrapper does. A value the caller set on
                    // a float channel is the raw bits of that value.
                    return MdfLib.MdfSampleValue.FromFloatingPoint(fromTime ? seconds.Value : ToDouble(value), IsValid);
                case MdfLib.MdfValueKind.SignedInteger:
                    return MdfLib.MdfSampleValue.FromSigned(SignExtend(Narrow(ToUInt64(value))), IsValid);
                case MdfLib.MdfValueKind.Text:
                    return MdfLib.MdfSampleValue.FromText(value as string ?? ToText(value), IsValid);
                case MdfLib.MdfValueKind.ByteArray:
                    return MdfLib.MdfSampleValue.FromByteArray(value as byte[] ?? new byte[0], IsValid);
                default:
                    return MdfLib.MdfSampleValue.FromUnsigned(Narrow(ToUInt64(value)), IsValid);
            }
        }

        // The seconds from the start of the measurement, as the master channel stores a time (the wrapper's
        // RecalculateTime does the same division).
        // Before the start the difference is negative (a pre-trigger sample); the unsigned difference is taken first.
        internal static double SecondsFromStart(ulong time, ulong start)
        {
            return time >= start ? (double)(time - start) / 1000000000.0 : -(double)(start - time) / 1000000000.0;
        }

        // The wrapper's static_cast<double>(value) for a float channel: integers keep their numeric value.
        private double ToDouble(object value)
        {
            if (value is double) { return (double)value; }
            if (value is long) { return (long)value; }
            if (value is ulong) { return (double)(ulong)value; }
            return 0.0;
        }

        // The wrapper's std::to_string for a text channel: decimal digits for an integer, six decimals for a double.
        private static string ToText(object value)
        {
            if (value is double) { return ((double)value).ToString("F6", System.Globalization.CultureInfo.InvariantCulture); }
            if (value is long) { return ((long)value).ToString(System.Globalization.CultureInfo.InvariantCulture); }
            if (value is ulong) { return ((ulong)value).ToString(System.Globalization.CultureInfo.InvariantCulture); }
            return string.Empty;
        }

        private ulong ToUInt64(object value)
        {
            if (value is ulong) { return (ulong)value; }
            if (value is long) { return unchecked((ulong)(long)value); }
            if (value is double) { return DoubleToUInt64((double)value); }
            return 0UL;
        }

        // The wrapper's static_cast<uint64_t>(value) for a double of an integer channel: the numeric value, truncated.
        // A negative value keeps its two's complement (the signed field sign-extends it); NaN is 0 and an out-of-range
        // value saturates, where the C++ cast is undefined.
        private static ulong DoubleToUInt64(double value)
        {
            if (double.IsNaN(value)) { return 0UL; }
            double truncated = Math.Truncate(value);
            if (truncated < 0) { return truncated < -9223372036854775808.0 ? unchecked((ulong)long.MinValue) : unchecked((ulong)(long)truncated); }
            return truncated >= 18446744073709551615.0 ? ulong.MaxValue : (ulong)truncated;
        }

        // The wrapper writes the value into a field of BitCount bits, so a wider number keeps its low bits and a
        // signed field sign-extends from its own width.
        private ulong Narrow(ulong raw)
        {
            if (BitCount >= 64) { return raw; }
            ulong mask = (1UL << (int)BitCount) - 1UL;
            return raw & mask;
        }

        // A signed field carries its sign in the highest bit of its own width, which is what the wrapper's
        // byte copy leaves in the record; the engine takes the value as a long.
        private long SignExtend(ulong cut)
        {
            if (BitCount >= 64) { return unchecked((long)cut); }
            ulong sign = 1UL << ((int)BitCount - 1);
            if ((cut & sign) == 0) { return unchecked((long)cut); }
            return unchecked((long)(cut | ~((1UL << (int)BitCount) - 1UL)));
        }
    }

    // One CG in the making. The facade's data group is the pair of it, so EngineIndex is the index the engine
    // writer and MdfWriter.SaveSample agree on, and Index is the position inside the data group.
    internal sealed class PendingGroup
    {
        internal readonly FacadeContext Context;
        internal readonly List<PendingChannel> Channels = new List<PendingChannel>();
        internal MdfChannelGroup Facade;
        internal int DataGroupIndex, Index, EngineIndex;
        internal PendingDataGroup DataGroup; // the data group this channel group belongs to
        internal string Name = string.Empty, Description = string.Empty;
        internal ushort Flags;
        internal char PathSeparator;
        internal ulong RecordId;
        internal ulong BusFrames; // frames the bus logger wrote into this CAN frame group (NofSamples)
        // Read side: the sample count of the group and the engine batches read for it, per value representation.
        // The indices are the channel positions the batch columns follow (raw and physical differ when a conversion is not readable).
        internal ulong SampleCount;
        internal readonly List<int> RawIndices = new List<int>();
        internal readonly List<int> PhysIndices = new List<int>();
        internal readonly List<MdfLib.MdfSampleBatch> RawBatches = new List<MdfLib.MdfSampleBatch>();
        internal readonly List<MdfLib.MdfSampleBatch> PhysBatches = new List<MdfLib.MdfSampleBatch>();
        internal PendingSource Source;
        internal MdfMetaData Metadata; // the CG block's common properties, created through CreateMetaData

        internal PendingGroup(FacadeContext context, PendingDataGroup dataGroup)
        {
            Context = context;
            DataGroupIndex = dataGroup.Sequence;
            DataGroup = dataGroup;
            Index = dataGroup.Groups.Count;
        }

        internal static MdfChannelGroup FacadeOf(PendingGroup group)
        {
            if (group.Facade == null) { group.Facade = new MdfChannelGroup(group.Context, group); }
            return group.Facade;
        }
    }

    // One DG in the making. The managed writer knows exactly one channel group per data group, which is what
    // lets the facade keep the two levels apart.
    internal sealed class PendingDataGroup
    {
        internal readonly FacadeContext Context;
        internal readonly List<PendingGroup> Groups = new List<PendingGroup>();
        internal MdfDataGroup Facade;
        internal MdfMetaData Metadata; // the DG block's common properties, created through CreateMetaData
        internal int Sequence, Index;
        // Read side: true once ReadData or ReadPartialData has loaded the samples of this data group.
        internal bool Loaded;

        internal PendingDataGroup(FacadeContext context)
        {
            Context = context;
            Sequence = context.DataGroups.Count;
            Index = Sequence;
        }

        // One handle per data group: the wrapper hands out the same live object every time, so the facade keeps it.
        internal static MdfDataGroup FacadeOf(PendingDataGroup dataGroup)
        {
            if (dataGroup.Facade == null) { dataGroup.Facade = new MdfDataGroup(dataGroup); }
            return dataGroup.Facade;
        }
    }

    // The HD state the facade collects; the engine takes it as options when the file is created.
    internal sealed class PendingHeader
    {
        internal string Author = string.Empty, Department = string.Empty, Project = string.Empty, Subject = string.Empty;
        internal string MeasurementId = string.Empty, RecorderId = string.Empty;
        internal long? RecorderIndex; // null = not stored
        internal double? StartAngle, StartDistance; // null = not stored (the HD flag stays clear)
        internal string Description = string.Empty;
        internal ulong StartTime;
        internal long Index;
        // The header metadata (HD common properties); the facade creates it with the context, never null.
        internal MdfMetaData Metadata;
        // The one file-history block the engine writes; null until CreateFileHistory is called.
        internal MdfFileHistory History;
        internal string ToolName = string.Empty, ToolVendor = string.Empty, ToolVersion = string.Empty;
        internal string UserName = string.Empty, HistoryDescription = string.Empty;
        internal ulong? HistoryTime;
        internal MdfMetaData HistoryMetadata; // the FH block's common properties, created through MdfFileHistory.MetaData
        // The file-history blocks a reader found (the writer keeps its single block in History above).
        internal readonly List<PendingHistory> Histories = new List<PendingHistory>();
        // The attachments and events, in file order; the writer writes them before the measurement starts.
        internal readonly List<PendingAttachment> Attachments = new List<PendingAttachment>();
        internal readonly List<PendingEvent> Events = new List<PendingEvent>();
    }

    // One FH block read from a file; the writer's single block is PendingHeader.History and has no node of its own.
    internal sealed class PendingHistory
    {
        internal MdfFileHistory Facade;
        internal string Description = string.Empty, ToolName = string.Empty, ToolVendor = string.Empty;
        internal string ToolVersion = string.Empty, UserName = string.Empty;
        internal ulong Time;
        internal long Index;

        internal static MdfFileHistory FacadeOf(FacadeContext context, PendingHistory history)
        {
            if (history.Facade == null) { history.Facade = new MdfFileHistory(context, history); }
            return history.Facade;
        }
    }

    // One AT block in the making or read from a file. The writer writes the block with the engine's AddAttachment when
    // the tree is flushed, so Index is the position the engine returned; a reader keeps the flags and the checksum.
    internal sealed class PendingAttachment
    {
        internal readonly FacadeContext Context;
        internal MdfAttachment Facade;
        internal string FileName = string.Empty, MimeType = string.Empty, Comment = string.Empty, Md5 = string.Empty;
        internal bool Embedded, Compressed, FromFile;
        internal ushort CreatorIndex;
        internal long Index;
        internal MdfMetaData Metadata; // the comment's MD block, created through CreateMetaData

        internal PendingAttachment(FacadeContext context)
        {
            Context = context;
            Index = context.Header.Attachments.Count;
        }

        internal static MdfAttachment FacadeOf(PendingAttachment attachment)
        {
            if (attachment.Facade == null) { attachment.Facade = new MdfAttachment(attachment); }
            return attachment.Facade;
        }

        internal MdfLib.MdfAttachmentDefinition Definition()
        {
            MdfLib.MdfAttachmentDefinition definition = new MdfLib.MdfAttachmentDefinition();
            definition.FileName = FileName;
            definition.MimeType = MimeType;
            definition.Comment = Comment;
            definition.CreatorIndex = CreatorIndex;
            definition.CommonProperties = Metadata == null ? null : Metadata.WriterTags();
            // Embedded data is the file's bytes, read when the attachment is written; compression needs embedded data.
            if (Compressed && !Embedded) { throw new NotSupportedException("Only embedded data can be compressed: set Embedded before Compressed."); }
            definition.Compress = Compressed;
            if (Embedded)
            {
                if (string.IsNullOrEmpty(FileName)) { throw new NotSupportedException("An embedded attachment stores the bytes of its file, so it needs a FileName."); }
                definition.Data = System.IO.File.ReadAllBytes(FileName);
            }
            return definition;
        }
    }

    // One EV block in the making or read from a file. Parent, range and attachment references are nodes of the same
    // tree; the writer turns them into the positions the engine uses.
    internal sealed class PendingEvent
    {
        internal readonly FacadeContext Context;
        internal MdfEvent Facade;
        internal string Name = string.Empty, Description = string.Empty, GroupName = string.Empty;
        internal EventType Type = EventType.Trigger;
        internal SyncType Sync = SyncType.SyncTime;
        internal RangeType Range;
        internal EventCause Cause;
        internal ulong EventCreatorIndex;
        internal long SyncValue;
        internal double SyncFactor = 1.0;
        internal double PreTrig, PostTrig;
        internal PendingEvent Parent, RangeEvent;
        internal readonly List<PendingAttachment> Attachments = new List<PendingAttachment>();
        internal bool FromFile;
        internal long Index;
        internal MdfMetaData Metadata; // the comment's MD block, created through MetaData

        internal PendingEvent(FacadeContext context)
        {
            Context = context;
            Index = context.Header.Events.Count;
        }

        internal static MdfEvent FacadeOf(PendingEvent evt)
        {
            if (evt.Facade == null) { evt.Facade = new MdfEvent(evt); }
            return evt.Facade;
        }

        // The engine definition of this event. The engine has no field for the pre- and post-trigger times, so a value there is refused
        // rather than dropped. The creator index is stored in 16 bits, as v2.3.0 stores it; references must belong to this file.
        internal MdfLib.MdfEventDefinition Definition(FacadeContext context)
        {
            if (PreTrig != 0.0 || PostTrig != 0.0)
            { throw new NotSupportedException("The engine stores no pre- or post-trigger time for an event, so PreTrig and PostTrig cannot be written."); }
            MdfLib.MdfEventDefinition definition = new MdfLib.MdfEventDefinition();
            definition.Name = Name;
            definition.Comment = Description;
            definition.CreatorIndex = unchecked((ushort)EventCreatorIndex);
            definition.GroupName = GroupName;
            definition.Type = (MdfLib.MdfEventType)(int)Type;
            definition.SyncType = (MdfLib.MdfEventSyncType)(int)Sync;
            definition.RangeType = (MdfLib.MdfEventRangeType)(int)Range;
            definition.Cause = (MdfLib.MdfEventCause)(int)Cause;
            definition.SyncBaseValue = SyncValue;
            definition.SyncFactor = SyncFactor;
            definition.ParentIndex = PositionOf(context.Header.Events, Parent, "parent");
            definition.RangeIndex = PositionOf(context.Header.Events, RangeEvent, "range");
            int[] attachments = new int[Attachments.Count];
            for (int i = 0; i < attachments.Length; i++)
            {
                attachments[i] = PositionOf(context.Header.Attachments, Attachments[i], "attachment");
                if (attachments[i] < 0) { throw new NotSupportedException("An attachment of the event belongs to another file."); }
            }
            definition.AttachmentIndices = attachments;
            definition.ScopeGroupIndices = new int[0];
            definition.CommonProperties = Metadata == null ? null : Metadata.WriterTags();
            return definition;
        }

        private static int PositionOf<T>(List<T> list, T item, string role) where T : class
        {
            if (item == null) { return -1; }
            int position = list.IndexOf(item);
            if (position < 0) { throw new NotSupportedException("The " + role + " event or attachment belongs to another file."); }
            return position;
        }
    }

    // The writer: the pending tree, the engine writer built from it, and the wrapper's own flags.
    internal sealed class FacadeContext
    {
        internal readonly MdfWriterType Type;
        internal readonly List<PendingDataGroup> DataGroups = new List<PendingDataGroup>();
        internal readonly List<PendingGroup> Groups = new List<PendingGroup>();
        internal readonly PendingHeader Header;
        internal MdfFile File;
        internal string Path, FileName, DisplayName, VersionText;
        internal string ProgramIdentifier = string.Empty; // the ID block's prg_id: read from the file, or set before InitMeasurement.
        internal MdfLib.MdfWriter Writer;
        // The engine's bus logger, for an MdfBusLogger writer; its frame groups are the CAN_* groups below.
        internal MdfLib.MdfBusLogger BusLogger;
        internal bool BusConfigured;
        internal static readonly string[] CanFrameGroupNames = { "CAN_DataFrame", "CAN_RemoteFrame", "CAN_ErrorFrame", "CAN_OverloadFrame" };
        // True once the engine has a file, whichever of its two writers holds it.
        internal bool Created { get { return Writer != null || BusLogger != null; } }
        internal bool FileIsNew, FileExists, Prepared, TreeFlushed, MeasurementStarted, Finalized, Periodic;

        // A sample waits here until the measurement stops or is finalized, as the upstream sample queue does: a sample
        // stamped after the stop time is dropped, and the stop time is only known once StopMeasurement is called.
        internal sealed class PendingSample
        {
            internal int Index;
            internal ulong Time;
            internal MdfLib.MdfSampleValue[] Values;
            internal PendingChannel[] Computed; // the time channels among Values, recomputed when the measurement starts.
            internal MdfLib.MdfBusMessage Frame; // a bus frame saved before the start (Values unused).
        }
        internal readonly System.Collections.Generic.List<PendingSample> Pending = new System.Collections.Generic.List<PendingSample>();
        // StartMeasurement has been called. Until then each group (or frame kind)
        // keeps its samples in its own queue, trimmed to the pre-trigger window, and ReleasePreTrigger moves them on.
        internal bool StartCalled;
        internal readonly System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<PendingSample>> PreTrigger = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<PendingSample>>();
        internal readonly System.Collections.Generic.List<int> PreTriggerOrder = new System.Collections.Generic.List<int>();
        // Set when the handle's Dispose has run: the engine writer object is gone, and every path that would
        // write a byte answers from this flag instead of from the disposed engine.
        internal bool Closed;
        // True for a facade reader: the tree is filled from the engine catalog and nothing is written.
        internal bool Reading;
        internal bool CompressData, MandatoryMemberOnly;
        internal ulong StartTimeNs, StopTimeNs, PreTriggerNs;
        internal uint MaxLength = 8;
        internal MdfBusType BusType;
        internal MdfStorageType StorageType = MdfStorageType.FixedLengthStorage;
        internal long NextIndex = 1;

        internal FacadeContext(MdfWriterType writerType)
        {
            Type = writerType;
            Header = new PendingHeader();
            Header.Metadata = new MdfMetaData(this);
            VersionText = writerType == MdfWriterType.Mdf3Basic ? "3.30" : "4.10";
        }

        // The nanoseconds a facade timestamp carries. The reviewed surface exposes no getter on IMdfTimeStamp,
        // so only the facade's own three classes can be read; any other implementation is refused.
        internal static ulong NanosecondsOf(IMdfTimeStamp timestamp)
        {
            MdfUtcTimestamp utc = timestamp as MdfUtcTimestamp;
            if (utc != null) { return utc.TimestampNs; }
            MdfLocalTimestamp local = timestamp as MdfLocalTimestamp;
            if (local != null) { return local.TimestampNs; }
            MdfTimezoneTimestamp zone = timestamp as MdfTimezoneTimestamp;
            if (zone != null) { return zone.TimestampNs; }
            throw new NotSupportedException("An IMdfTimeStamp implementation outside the facade's three timestamp classes carries no value the writer can read.");
        }

        internal long NewIndex()
        {
            return NextIndex++;
        }

        internal bool IsMdf3
        {
            get { return Type == MdfWriterType.Mdf3Basic; }
        }

        // The file's revision as two digits, which the wrapper exposes as two separate properties.
        internal void SetRevision(int major, int minor)
        {
            SetVersion(major.ToString(CultureInfo.InvariantCulture) + "." + minor.ToString(CultureInfo.InvariantCulture));
        }

        internal void SetVersion(string value)
        {
            if (value == VersionText) { return; }
            if (Created) { throw new InvalidOperationException("The file is created, so its revision cannot change."); }
            if (value != "3.30" && value != "4.10" && value != "4.20")
            { throw new NotSupportedException("The managed writer produces the exact revisions 3.30, 4.10 and 4.20 (MdfWriterOptions.Version), so " + value + " cannot be written."); }
            if ((value == "3.30") != IsMdf3)
            { throw new NotSupportedException("The revision follows the writer type: MdfWriterType.Mdf3Basic writes 3.30 and every MDF 4 writer type writes a 4.x revision."); }
            VersionText = value;
        }

        internal PendingDataGroup AddDataGroup()
        {
            PendingDataGroup dataGroup = new PendingDataGroup(this);
            DataGroups.Add(dataGroup);
            return dataGroup;
        }

        internal PendingGroup AddChannelGroup(PendingDataGroup dataGroup)
        {
            PendingGroup group = new PendingGroup(this, dataGroup);
            dataGroup.Groups.Add(group);
            Groups.Add(group);
            group.EngineIndex = Groups.Count - 1;
            return group;
        }

        // The wrapper's Init only records the name and checks the path, and mdflib writes the metadata blocks in
        // InitMeasurement. The managed engine takes the whole configuration at creation and writes nothing before
        // the measurement starts, so the facade checks the tree and the path here and creates the writer later,
        // when the first start time is known.
        internal bool Prepare()
        {
            if (Prepared) { return true; }
            if (string.IsNullOrEmpty(Path)) { return false; }
            // mdflib fails the same way here: PrepareForWriting refuses a group without channels, and its
            // InitMeasurement reports that as false rather than as an exception. The managed writer cannot even
            // start an empty file, so a tree without a single channel group is reported the same way.
            if (Groups.Count == 0) { return false; }
            // The CAN frame groups of a bus log have no channels by design: the engine writes their layout. The
            // bus log's own checks (names, no channels) run in FlushBus, so only the channel groups are checked here.
            if (Type != MdfWriterType.MdfBusLogger)
            {
                for (int g = 0; g < Groups.Count; g++)
                {
                    if (Groups[g].Channels.Count == 0) { return false; }
                    Validate(Groups[g]);
                }
            }
            if (!Probe()) { return false; }
            // The engine can append to a finalized MDF 4 file, but the facade only wires that with the
            // read side, and MdfLib.MdfWriter.Create would truncate. Writing into a file that
            // already exists is therefore reported the way mdflib reports an unusable one: as false.
            if (FileExists) { return false; }
            Prepared = true;
            return true;
        }

        // What the wrapper's Init does with a name: an existing file is recognised as one, and a new one only
        // needs a directory that exists. Nothing is created, written or parsed here.
        internal bool Probe()
        {
            if (string.IsNullOrEmpty(Path)) { return false; }
            try
            {
                FileExists = System.IO.File.Exists(Path);
                if (FileExists)
                {
                    FileIsNew = false;
                    return true;
                }
                string directory = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) { return false; }
            }
            // Only an illegal name fails here; a file that cannot be read is the business of the open below.
            catch (ArgumentException) { return false; }
            FileIsNew = true;
            return true;
        }

        // A file that cannot be reached is reported as false, exactly like the wrapper's Init, while a
        // definition the engine refuses keeps its exception: a bool alone would not say which field is wrong.
        internal bool Open()
        {
            if (Created) { return true; }
            if (!Prepared && !Prepare()) { return false; }
            try
            {
                if (Type == MdfWriterType.MdfBusLogger) { BusLogger = MdfLib.MdfBusLogger.Create(Path, MdfLib.MdfBusType.Can, BuildBusOptions()); }
                else { Writer = MdfLib.MdfWriter.Create(Path, BuildOptions()); }
            }
            catch (MdfLib.MdfFormatException) { return false; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (ArgumentException) { return false; }
            return true;
        }

        // Everything a new file carries in its header is decided when the engine writer is created; an appended
        // file keeps the header it already has, which is what the engine documents for Append.
        internal MdfLib.MdfWriterOptions BuildOptions()
        {
            MdfLib.MdfWriterOptions options = new MdfLib.MdfWriterOptions();
            options.UpstreamIdentification = true;
            options.ProgramIdentifier = ProgramIdentifier.Length == 0 ? null : ProgramIdentifier;
            options.Version = VersionText;
            options.StartTimeNanoseconds = Header.StartTime;
            options.Comment = TextOrNull(Header.Description);
            options.Author = HeaderText("author", Header.Author);
            options.Organization = HeaderText("department", Header.Department);
            options.Project = HeaderText("project", Header.Project);
            options.Subject = HeaderText("subject", Header.Subject);
            options.MeasurementId = TextOrNull(Header.MeasurementId);
            options.RecorderId = TextOrNull(Header.RecorderId);
            options.RecorderIndex = Header.RecorderIndex;
            options.StartAngle = Header.StartAngle;
            options.StartDistance = Header.StartDistance;
            if (CompressData) { options.CompressData = true; }
            if (Header.History != null)
            {
                // The engine writes an FH block only when one of its history fields is set; tool_id is mandatory.
                options.ToolIdentifier = TextOrNull(Header.ToolName) ?? "MdfLib";
                options.ToolVendor = TextOrNull(Header.ToolVendor);
                options.ToolVersion = TextOrNull(Header.ToolVersion);
                options.UserName = TextOrNull(Header.UserName);
                options.HistoryComment = TextOrNull(Header.HistoryDescription);
                options.HistoryTimeNanoseconds = Header.HistoryTime;
                options.HistoryCommonProperties = Header.HistoryMetadata == null ? null : Header.HistoryMetadata.WriterTags();
            }
            options.CommonProperties = Header.Metadata.ExtraWriterTags();
            return options;
        }

        // A header common property set through MdfMetaData wins over the header field of the same name; the
        // engine has one place for each, and the metadata is the newer API. A tag of another kind is written as its text.
        private string HeaderText(string name, string field)
        {
            string tagged = Header.Metadata.TextOf(name);
            return TextOrNull(tagged ?? field);
        }

        private static string TextOrNull(string value)
        {
            return value == null || value.Length == 0 ? null : value;
        }

        internal void RejectFlushed()
        {
            if (TreeFlushed) { throw new InvalidOperationException("The measurement is initialized, so the channel tree can no longer change."); }
        }

        // The engine reads the header once, at file creation. The wrapper would still mutate its live HD block
        // there, so the facade refuses the change instead of collecting it and dropping it at the next flush.
        internal void RejectCreated()
        {
            if (Created) { throw new InvalidOperationException("The file is created, so its header cannot change any more."); }
        }

        internal PendingGroup GroupOf(MdfChannelGroup group)
        {
            for (int i = 0; i < Groups.Count; i++) { if (Groups[i].Facade == group) { return Groups[i]; } }
            throw new ArgumentException("The channel group does not belong to this writer.", "group");
        }

        // Builds the engine tree from the pending one and starts the measurement, which is where the managed
        // writer writes the ID, HD, DG, CG and CN blocks. A rejected definition leaves the file without metadata.
        internal bool Flush()
        {
            if (TreeFlushed) { return true; }
            // A closed handle does not bring a file up; the Dispose that closed it already let the engine go.
            if (Closed) { return false; }
            if (Type == MdfWriterType.MdfBusLogger) { return FlushBus(); }

            if (!Open()) { return false; }
            MdfLib.MdfWriter writer = Writer;
            for (int g = 0; g < Groups.Count; g++)
            {
                PendingGroup group = Groups[g];
                if (g != 0)
                {
                    // a group that is not the first of its data group joins that group's data group in the engine.
                    PendingGroup first = DataGroups[group.DataGroupIndex].Groups[0];
                    MdfLib.MdfChannelGroupDefinition definition = BuildGroupMetadata(group);
                    int added = first == group ? writer.AddChannelGroup(definition) : writer.AddChannelGroupToDataGroup(first.EngineIndex, definition);
                    if (added != g)
                    { throw new InvalidOperationException("The engine numbered the channel group differently than the facade tree."); }
                }
                else if (!IsMdf3) { writer.SetChannelGroupMetadata(0, BuildGroupMetadata(group)); }
                for (int c = 0; c < group.Channels.Count; c++)
                {
                    PendingChannel channel = group.Channels[c];
                    if (channel.Parent != null) { continue; } // written with its structure, which precedes it
                    if (channel.IsStructure) { WriteStructure(writer, g, channel, c); }
                    else { writer.AddChannel(g, channel.Definition()); }
                }
            }
            WriteAttachmentsAndEvents(writer);
            Start();
            TreeFlushed = true;
            return true;
        }

        // structures: the structure takes the bytes of its components, packed in the order they were created.
        private void WriteStructure(MdfLib.MdfWriter writer, int groupIndex, PendingChannel parent, int position)
        {
            int window = 0;
            foreach (PendingChannel component in parent.Components)
            {
                if (component.BitCount == 0 || component.BitCount % 8 != 0)
                { throw new NotSupportedException("A structure component is a whole number of bytes in the managed writer."); }
                component.WindowOffset = window;
                window += (int)(component.BitCount / 8);
            }
            if (window == 0) { throw new InvalidOperationException("A structure channel has no components."); }
            MdfLib.MdfChannelDefinition definition = parent.Definition();
            definition.ValueKind = MdfLib.MdfValueKind.ByteArray;
            definition.BitCount = window * 8;
            definition.IsMaster = false;
            definition.IsVariableLength = false;
            definition.HasLinearConversion = false;
            definition.Conversion = null;
            definition.ArrayDimensions = null;
            if (writer.AddStructureChannel(groupIndex, definition, window) != position)
            { throw new InvalidOperationException("The engine numbered the structure differently than the facade tree."); }
            foreach (PendingChannel component in parent.Components)
            {
                if (writer.AddComponentChannel(groupIndex, component.Definition(), position, component.WindowOffset) != component.Index)
                { throw new InvalidOperationException("The engine numbered a structure component differently than the facade tree."); }
            }
        }

        // Attachments and events go to the engine in the order they were created; their positions are the ones the engine
        // returns, which the events refer to. The engine accepts both until finalization, the facade before the start.
        private void WriteAttachmentsAndEvents(MdfLib.MdfWriter writer)
        {
            foreach (PendingAttachment attachment in Header.Attachments)
            {
                if (writer.AddAttachment(attachment.Definition()) != attachment.Index)
                { throw new InvalidOperationException("The engine numbered the attachment differently than the facade tree."); }
            }
            foreach (PendingEvent evt in Header.Events)
            {
                if (writer.AddEvent(evt.Definition(this)) != evt.Index)
                { throw new InvalidOperationException("The engine numbered the event differently than the facade tree."); }
            }
        }

        // The bus log writes the frame groups the engine creates for CAN. Channels cannot be added to them, and
        // every group must be one of the CAN frame groups, so nothing the caller set is dropped at the flush.
        private bool FlushBus()
        {
            if (!BusConfigured)
            { throw new InvalidOperationException("Call CreateBusLogConfiguration() before the measurement starts, so the bus log knows its frame groups."); }
            for (int g = 0; g < Groups.Count; g++)
            {
                string name = Groups[g].Name;
                if (!Array.Exists(CanFrameGroupNames, n => n == name))
                { throw new NotSupportedException("A bus log holds the CAN frame groups only; " + name + " cannot be written by the bus logger."); }
                if (Groups[g].Source != null && !Groups[g].Source.IsEmpty)
                { throw new NotSupportedException("The bus log writes no source information for its CAN frame groups (v2.3.0 writes none either), so the source information set on " + name + " cannot be written."); }
                if (Groups[g].Channels.Count != 0)
                { throw new NotSupportedException("The CAN frame groups of a bus log have the layout the engine writes; " + name + " cannot take channels."); }
            }
            if (Header.Attachments.Count != 0 || Header.Events.Count != 0)
            { throw new NotSupportedException("The bus logger writes no attachments or events; create them on an MDF writer."); }
            if (!Open()) { return false; }
            Start();
            TreeFlushed = true;
            return true;
        }

        private MdfLib.MdfBusLoggerOptions BuildBusOptions()
        {
            MdfLib.MdfBusLoggerOptions options = new MdfLib.MdfBusLoggerOptions();
            options.Version = VersionText;
            options.StartTimeNanoseconds = Header.StartTime;
            options.MaxDataLength = (int)MaxLength;
            options.CompressData = CompressData;
            // the facade always writes the v2.3.0 CAN layout (the release's channel list).
            options.Layout = MdfLib.MdfBusLoggerLayout.V230;
            options.UpstreamIdentification = true;
            options.ProgramIdentifier = ProgramIdentifier.Length == 0 ? null : ProgramIdentifier;
            return options;
        }

        // One CAN frame into the bus log. The frame kind must match the group it is saved into. The time is absolute UTC; the engine stores
        // it in seconds from the start of the measurement.
        internal void SaveCanFrame(PendingGroup group, ulong time, CanMessage message)
        {
            if (message == null) { throw new ArgumentNullException("message"); }
            if (Closed) { throw new InvalidOperationException("The writer is closed, so no frame can be written any more."); }
            if (Type != MdfWriterType.MdfBusLogger)
            { throw new NotSupportedException("SaveCanMessage writes into a bus log: create the writer with MdfWriterType.MdfBusLogger."); }
            if (!Array.Exists(CanFrameGroupNames, n => n == group.Name))
            { throw new ArgumentException("The channel group is not one of the CAN frame groups of the bus log.", "group"); }
            if (!TreeFlushed) { Flush(); }
            if (BusLogger == null) { throw new InvalidOperationException("The file is not open, so no frame can be written; give the writer a usable path with Init(string) first."); }
            CanMessageType kind = KindOf(group.Name);
            if (message.TypeOfMessage != kind)
            { throw new ArgumentException("The frame is a " + message.TypeOfMessage + ", but " + group.Name + " takes " + kind + " frames.", "message"); }
            // writes what it is given, and so does the facade: no check of the payload against the DLC, no
            // refusal of a payload above 8 bytes, of a payload on a remote or overload frame, or of BRS/ESI on a classic
            // frame. The release layout has no R0 or R1 bit, and FrameDuration and Crc are not stored; the release
            // ignores them silently, so they are ignored here as well (documented on SaveCanMessage).
            uint identifierLimit = message.ExtendedId ? 0x1FFFFFFFu : 0x7FFu;
            if (message.MessageId > identifierLimit)
            { throw new ArgumentOutOfRangeException("message", "The identifier " + message.MessageId + " does not fit a " + (message.ExtendedId ? "29" : "11") + "-bit CAN identifier."); }
            if (kind != CanMessageType.Can_ErrorFrame && (message.ErrorType != CanErrorType.UNKNOWN_ERROR || message.BitPosition != 0))
            { throw new ArgumentException("ErrorType and BitPosition belong to error frames only.", "message"); }
            byte[] payload = message.DataBytes;
            if (StartCalled && time < Header.StartTime) { throw new ArgumentOutOfRangeException("time", "A frame cannot precede the start of the measurement."); }
            MdfLib.MdfBusMessage frame = new MdfLib.MdfBusMessage();
            frame.Type = EngineFrameKind(kind);
            frame.Timestamp = PendingChannel.SecondsFromStart(time, Header.StartTime);
            frame.BusChannel = message.BusChannel;
            frame.Id = message.MessageId;
            frame.IsExtendedId = message.ExtendedId;
            frame.Direction = message.Dir ? MdfLib.MdfBusDirection.Tx : MdfLib.MdfBusDirection.Rx;
            frame.Data = kind == CanMessageType.Can_RemoteFrame ? null : payload;
            frame.Dlc = message.Dlc;
            frame.IsFd = message.Edl;
            frame.BitRateSwitch = message.Brs;
            frame.ErrorStateIndicator = message.Esi;
            frame.ErrorType = (byte)message.ErrorType;
            frame.BitPosition = message.BitPosition;
            if (kind != CanMessageType.Can_OverloadFrame)
            {
                frame.Fields["SRR"] = MdfLib.MdfSampleValue.FromUnsigned(message.Srr ? 1UL : 0UL);
                frame.Fields["WakeUp"] = MdfLib.MdfSampleValue.FromUnsigned(message.WakeUp ? 1UL : 0UL);
                frame.Fields["SingleWire"] = MdfLib.MdfSampleValue.FromUnsigned(message.SingleWire ? 1UL : 0UL);
            }
            if (kind == CanMessageType.Can_ErrorFrame) { frame.Fields["RTR"] = MdfLib.MdfSampleValue.FromUnsigned(message.Rtr ? 1UL : 0UL); }
            // a frame saved before StartMeasurement waits in its frame kind's queue (see ReleasePreTrigger).
            if (!StartCalled) { QueueBeforeStart((int)frame.Type, new PendingSample { Time = time, Frame = frame }); }
            else { BusLogger.Write(frame); }
            group.BusFrames++;
        }

        private static CanMessageType KindOf(string groupName)
        {
            if (groupName == "CAN_RemoteFrame") { return CanMessageType.Can_RemoteFrame; }
            if (groupName == "CAN_ErrorFrame") { return CanMessageType.Can_ErrorFrame; }
            if (groupName == "CAN_OverloadFrame") { return CanMessageType.Can_OverloadFrame; }
            return CanMessageType.Can_DataFrame;
        }

        private static MdfLib.MdfBusMessageType EngineFrameKind(CanMessageType kind)
        {
            switch (kind)
            {
                case CanMessageType.Can_RemoteFrame: return MdfLib.MdfBusMessageType.CanRemoteFrame;
                case CanMessageType.Can_ErrorFrame: return MdfLib.MdfBusMessageType.CanErrorFrame;
                case CanMessageType.Can_OverloadFrame: return MdfLib.MdfBusMessageType.CanOverloadFrame;
                default: return MdfLib.MdfBusMessageType.CanDataFrame;
            }
        }

        // The engine writer that holds the file, whichever kind it is, for the measurement end and the disposal.
        internal void StopEngine()
        {
            if (BusLogger != null) { BusLogger.StopMeasurement(); } else { Writer.StopMeasurement(); }
        }

        internal void FinalizeEngine()
        {
            if (BusLogger != null) { BusLogger.FinalizeMeasurement(); } else { Writer.FinalizeMeasurement(); }
        }

        internal void DisposeEngine()
        {
            if (BusLogger != null) { BusLogger.Dispose(); }
            else if (Writer != null) { Writer.Dispose(); }
        }

        // The engine only accepts samples while the measurement runs, so the facade always starts it; mdflib's
        // own writer thread is not part of the managed design.
        // Writes the waiting samples to the engine, in the order they were given. A sample stamped after the stop time
        // is not kept; with no stop time every sample is kept.
        internal void Drain()
        {
            foreach (PendingSample sample in Pending)
            {
                if (StopTimeNs > 0 && sample.Time > StopTimeNs) { continue; }
                Writer.SaveSample(sample.Index, sample.Values);
            }
            Pending.Clear();
        }

        // the samples before the start are kept in their queue while the window (PreTriggerNs) allows it. The
        // newest sample older than the window is kept too, as the upstream queue keeps its start sample.
        private void QueueBeforeStart(int key, PendingSample sample)
        {
            List<PendingSample> queue;
            if (!PreTrigger.TryGetValue(key, out queue))
            {
                queue = new List<PendingSample>();
                PreTrigger.Add(key, queue);
                PreTriggerOrder.Add(key);
            }
            queue.Add(sample);
            ulong last = queue[queue.Count - 1].Time;
            while (queue.Count > 1 && last > queue[1].Time && last - queue[1].Time > PreTriggerNs) { queue.RemoveAt(0); }
        }

        private static bool BeforeWindow(ulong time, ulong start, ulong window)
        {
            return time < start && start - time > window;
        }

        // called when StartMeasurement has started the engine. The queues are trimmed to the window before the
        // start (upstream TrimQueue), their time channels get the time from the start, and the samples join the stream.
        internal void ReleasePreTrigger()
        {
            ulong start = Header.StartTime;
            foreach (int key in PreTriggerOrder)
            {
                List<PendingSample> queue = PreTrigger[key];
                while (queue.Count > 1 && BeforeWindow(queue[1].Time, start, PreTriggerNs)) { queue.RemoveAt(0); }
                foreach (PendingSample sample in queue)
                {
                    if (sample.Frame != null)
                    {
                        sample.Frame.Timestamp = PendingChannel.SecondsFromStart(sample.Time, start);
                        BusLogger.Write(sample.Frame);
                        continue;
                    }
                    if (sample.Computed != null)
                    {
                        for (int i = 0; i < sample.Computed.Length; i++)
                        {
                            if (sample.Computed[i] != null) { sample.Values[i] = sample.Computed[i].Sample(sample.Time, PendingChannel.SecondsFromStart(sample.Time, start)); }
                        }
                    }
                    Pending.Add(sample);
                }
            }
            PreTrigger.Clear();
            PreTriggerOrder.Clear();
        }

        internal void Start()
        {
            if (MeasurementStarted || !Created) { return; }
            if (BusLogger != null) { BusLogger.StartMeasurement(); } else { Writer.StartMeasurement(); }
            MeasurementStarted = true;
        }

        // the header of an open file is written; its start time is patched by the engine that owns the file.
        internal void PatchStartTime(ulong nanoseconds)
        {
            if (BusLogger != null) { BusLogger.PatchStartTime(nanoseconds); } else { Writer.PatchStartTime(nanoseconds); }
        }

        private void Validate(PendingGroup group)
        {
            if (group.Flags != 0)
            { throw new NotSupportedException("cg_flags is written only by the bus logger's raw groups, so an ordinary channel group cannot carry flags."); }
            if (group.RecordId != 0)
            { throw new NotSupportedException("The record ID follows from the layout the engine computes (one data group per channel group), so it cannot be chosen."); }
            if (group.PathSeparator != 0)
            { throw new NotSupportedException("cg_path_separator follows from the source path of the group and cannot be set on the group itself."); }
            if (group.Description.Length != 0 && IsMdf3)
            { throw new NotSupportedException("MDF 3.30 has no comment block for a channel group."); }
            if (group.Name.Length != 0 && IsMdf3)
            { throw new NotSupportedException("MDF 3.30 channel groups have no acquisition name."); }
            if (group.Source != null && !group.Source.IsEmpty && IsMdf3)
            { throw new NotSupportedException("MDF 3.30 has no source information block for a channel group."); }
            int masters = 0;
            foreach (PendingChannel channel in group.Channels)
            {
                MdfLib.MdfValueKind kind = channel.Kind;
                if (channel.Type == ChannelType.Master) { masters++; }
                if (channel.Type == ChannelType.MaxLength)
                { throw new NotSupportedException("Max length (bit block) channels are read-only in the engine; the writer knows fixed and variable length storage."); }
                if (channel.Type == ChannelType.VirtualData || channel.Type == ChannelType.VirtualMaster)
                { throw new NotSupportedException("Virtual channels are read-only in the engine."); }
                if (channel.Type == ChannelType.Sync)
                { throw new NotSupportedException("A synchronized channel (cn_type 4) needs a synchronization master, which the managed writer does not store."); }
                if (channel.BitOffset != 0)
                { throw new NotSupportedException("Bit-packed channels (a non-zero cn_bit_offset) are read-only in the managed writer."); }
                if (channel.VlsdRecordId != 0)
                { throw new NotSupportedException("cn_vlsd_dg_rec is read-only: the engine keeps variable length records in the channel group's own data group."); }
                if (channel.Type == ChannelType.VariableLength && IsMdf3)
                { throw new NotSupportedException("MDF 3.30 has no variable length signal data; only an MDF 4 writer can create a VLSD channel."); }
                if (channel.Source != null && !channel.Source.IsEmpty && IsMdf3)
                { throw new NotSupportedException("MDF 3.30 has no per-channel source information block; only the description and the long identifier are fixed fields there."); }
                if (channel.DisplayName.Length != 0 && IsMdf3)
                { throw new NotSupportedException("MDF 3.30 stores the long identifier behind the channel source, which that revision does not have; use MdfWriterType.Mdf4Basic."); }
                if (channel.Parent != null && (channel.Type == ChannelType.Master || channel.Type == ChannelType.VariableLength))
                { throw new NotSupportedException("A structure component is a fixed length channel that is not the master."); }
                if (channel.IsStructure && channel.Conversion != null)
                { throw new NotSupportedException("A structure channel has no conversion of its own; put the conversion on its components."); }
                if (channel.IsStructure) { continue; } // its record window is the sum of its components, checked below
                if (kind == MdfLib.MdfValueKind.FloatingPoint && channel.BitCount != 32 && channel.BitCount != 64)
                { throw new NotSupportedException("An IEEE float channel is 32 or 64 bits wide in the managed writer."); }
                if (kind == MdfLib.MdfValueKind.Complex && channel.BitCount != 64 && channel.BitCount != 128)
                { throw new NotSupportedException("A complex channel is 64 or 128 bits wide in the managed writer."); }
                if (channel.Conversion != null) { ValidateConversion(channel.Conversion); }
            }
            if (masters != 1)
            { throw new NotSupportedException("Exactly one channel of a group must have Type = Master, which is the engine's stored time role."); }
        }

        // The inverse conversion is a numeric block of its own; its texts, names, units, comment and tags are not written yet.
        private static void ValidateInverse(PendingConversion conversion)
        {
            PendingConversion inverse = conversion.Inverse;
            if (conversion.Type == ConversionType.NoConversion) { throw new NotSupportedException("An inverse conversion needs the conversion it inverts to have a type."); }
            if (inverse.Type == ConversionType.NoConversion) { throw new NotSupportedException("An inverse conversion needs a type."); }
            if (inverse.Inverse != null) { throw new NotSupportedException("An inverse of an inverse conversion is not written by the managed writer."); }
            if (inverse.References.Count != 0 || inverse.Type == ConversionType.Algebraic)
            { throw new NotSupportedException("An inverse conversion with texts or a formula is not written by the managed writer yet (planned for 1.1)."); }
            if (inverse.Name.Length != 0 || inverse.Unit.Length != 0 || inverse.Description.Length != 0
                || (inverse.Metadata != null && inverse.Metadata.WriterTags() != null))
            { throw new NotSupportedException("An inverse conversion's name, unit, comment and tags are not written by the managed writer yet (planned for 1.1)."); }
            ValidateConversion(inverse);
        }

        private static void ValidateConversion(PendingConversion conversion)
        {
            // A conversion the caller only filled with Parameter has no type, and the engine writes a
            // conversion block only for a type; inventing a linear one from two numbers would be a guess.
            if (conversion.Type == ConversionType.NoConversion &&
                (conversion.Parameters.Count != 0 || conversion.References.Count != 0))
            { throw new NotSupportedException("A conversion created with Parameter() alone keeps Type = NoConversion, which writes no conversion block; say which conversion it is (Linear is the pair of raw values the wrapper fills)."); }
            if (conversion.Type == ConversionType.NoConversion && (conversion.Name.Length != 0 || conversion.Description.Length != 0 || conversion.Unit.Length != 0))
            { throw new NotSupportedException("A conversion's name, unit or comment needs a conversion type: without one the writer writes no conversion block."); }
            if (conversion.Type == ConversionType.Linear && (conversion.Name.Length != 0 || conversion.Description.Length != 0 || conversion.Unit.Length != 0))
            { throw new NotSupportedException("A linear conversion is stored as channel fields, with no block for its name, unit or comment; the managed writer writes them only on a conversion block of another type."); }
            if (conversion.Metadata != null && conversion.Metadata.WriterTags() != null && (conversion.Type == ConversionType.NoConversion || conversion.Type == ConversionType.Linear))
            { throw new NotSupportedException("A conversion's common properties need a conversion block of a type other than Linear: without one, or for a linear conversion stored as channel fields, the writer writes no MD block for them."); }
            if (conversion.Inverse != null) { ValidateInverse(conversion); }
            // A linear conversion is not a block of its own but the pair of channel fields Definition above fills
            // from Parameter(0) and Parameter(1), so there is nothing left for the definition builder to check.
            if (conversion.Type != ConversionType.Linear) { conversion.Definition(); }
        }

        private MdfLib.MdfChannelGroupDefinition BuildGroupMetadata(PendingGroup group)
        {
            MdfLib.MdfChannelGroupDefinition result = new MdfLib.MdfChannelGroupDefinition();
            if (group.Name.Length != 0) { result.Name = group.Name; }
            if (group.Description.Length != 0) { result.Comment = group.Description; }
            result.CommonProperties = group.Metadata == null ? null : group.Metadata.WriterTags();
            if (group.Index == 0 && group.DataGroup != null && group.DataGroup.Metadata != null) { result.DataGroupCommonProperties = group.DataGroup.Metadata.WriterTags(); }
            if (group.Source != null && !group.Source.IsEmpty) { result.Source = group.Source.Definition(); }
            return result;
        }

        internal void SaveSample(PendingGroup group, ulong time)
        {
            // The engine would answer ObjectDisposedException here; the handle's own end of work is a fact the
            // facade knows, and it says it in its own words instead of forwarding a disposed object.
            if (Closed) { throw new InvalidOperationException("The writer is closed, so no sample can be written any more."); }
            if (!TreeFlushed) { Flush(); }
            if (BusLogger != null) { throw new NotSupportedException("A bus log takes frames through SaveCanMessage, not samples."); }
            if (Writer == null) { throw new InvalidOperationException("The file is not open, so no sample can be written; give the writer a usable path with Init(string) first."); }
            if (StartCalled && time < Header.StartTime) { throw new ArgumentOutOfRangeException("time", "A sample cannot precede the start of the measurement."); }
            if (StartCalled && !MeasurementStarted) { throw new InvalidOperationException("The measurement has ended, so no sample can be written any more."); }
            int valueCount = 0;
            foreach (PendingChannel channel in group.Channels) { if (!channel.IsStructure) { valueCount++; } }
            MdfLib.MdfSampleValue[] values = new MdfLib.MdfSampleValue[valueCount];
            PendingChannel[] timeChannels = null;
            int next = 0;
            for (int i = 0; i < group.Channels.Count; i++)
            {
                PendingChannel channel = group.Channels[i];
                if (channel.IsStructure) { continue; } // a structure stores no value of its own
                bool isTime = channel.ComputesTime;
                if (!isTime && !channel.HasValue)
                { throw new InvalidOperationException("Channel " + (i + 1) + " of the group holds no value yet; every channel of a group must be set before SaveSample()."); }
                values[next] = isTime ? channel.Sample(time, PendingChannel.SecondsFromStart(time, Header.StartTime)) : channel.Sample(null, null);
                if (isTime)
                {
                    if (timeChannels == null) { timeChannels = new PendingChannel[values.Length]; }
                    timeChannels[next] = channel;
                }
                next++;
            }
            PendingSample pending = new PendingSample { Index = group.EngineIndex, Time = time, Values = values, Computed = timeChannels };
            if (!StartCalled) { QueueBeforeStart(group.EngineIndex, pending); return; }
            Pending.Add(pending);
        }
    }
}
