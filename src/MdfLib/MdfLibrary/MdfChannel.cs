// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// The channel handle. Like the wrapper's live CN object it carries both the
// metadata of the channel and the raw value the next SaveSample will write; the value keeps the type the caller
// used, because the wrapper picks its overload by argument type and converts nothing itself.
using System;
using System.Collections.Generic;
namespace MdfLibrary
{
    /// <summary>One channel of a channel group.</summary>
    public class MdfChannel
    {
        private readonly FacadeContext context_;
        private readonly PendingChannel pending_;

        /// <summary>The handle of one pending channel; only the facade's factories create it.</summary>
        internal MdfChannel(FacadeContext context, PendingChannel pending)
        {
            context_ = context;
            pending_ = pending;
        }

        /// <summary>Adds a component to this channel, which makes it a structure (MDF 4).
        /// The components are packed into the bytes of the structure in the order they were created, and the structure
        /// stores no value of its own. Nested structures, array components and variable length components are written in 1.1.</summary>
        public MdfChannel CreateChannelComposition()
        {
            context_.RejectFlushed();
            if (context_.Reading) { throw new InvalidOperationException("The channels of a file being read have their compositions in the file; none can be added."); }
            if (context_.IsMdf3) { throw new NotSupportedException("MDF 3.30 has no channel composition (structure)."); }
            if (pending_.Parent != null) { throw new NotSupportedException("A structure inside a structure is written in 1.1 (nested compositions)."); }
            if (pending_.Type != ChannelType.FixedLength)
            { throw new NotSupportedException("Only a fixed length channel can be a structure; a master, variable length, virtual, synchronized or max length channel cannot hold components."); }
            if (pending_.Conversion != null) { throw new NotSupportedException("A structure channel has no conversion of its own; put the conversion on its components."); }
            if (pending_.Components == null) { pending_.Components = new List<PendingChannel>(); }
            PendingChannel component = new PendingChannel(context_, pending_.Group);
            component.Parent = pending_;
            pending_.Components.Add(component);
            // The components stay right behind their structure, so the engine order and the sample order are the same.
            List<PendingChannel> channels = pending_.Group.Channels;
            int after = pending_.Components.Count > 1 ? pending_.Components[pending_.Components.Count - 2].Index : pending_.Index;
            channels.Insert(after + 1, component);
            for (int i = 0; i < channels.Count; i++) { channels[i].Index = i; }
            return PendingChannel.FacadeOf(component);
        }

        /// <summary>Creates the CC block of this channel, or reports the one it
        /// already has, as the wrapper does.</summary>
        public MdfChannelConversion CreateChannelConversion()
        {
            context_.RejectFlushed();
            if (pending_.Conversion == null)
            {
                pending_.Conversion = new PendingConversion(context_);
                pending_.Conversion.Index = context_.NewIndex();
                pending_.Conversion.Facade = new MdfChannelConversion(context_, pending_.Conversion);
            }
            return pending_.Conversion.Facade;
        }

        /// <summary>Upstream returns the existing MD block or a stub of a missing one,
        /// and the managed writer writes no MD blocks at all.</summary>
        public MdfMetaData CreateMetaData()
        {
            context_.RejectFlushed();
            if (pending_.Metadata == null) { pending_.Metadata = new MdfMetaData(); }
            return pending_.Metadata;
        }

        /// <summary>Creates the SI block of this channel, or reports the one it
        /// already has. MDF 3.30 has no per-channel source, so it is refused there when the file is written.</summary>
        public MdfSourceInformation CreateSourceInformation()
        {
            context_.RejectFlushed();
            if (pending_.Source == null)
            {
                pending_.Source = new PendingSource(context_);
                pending_.Source.Index = context_.NewIndex();
                pending_.Source.Facade = new MdfSourceInformation(context_, pending_.Source);
            }
            return pending_.Source.Facade;
        }

        /// <summary>The raw signed value the next SaveSample writes.</summary>
        public void SetChannelValue(long value) { SetChannelValue(value, true); }

        /// <summary>The raw signed value and whether the sample is valid.</summary>
        public void SetChannelValue(long value, bool valid) { pending_.Remember(value, valid); }

        /// <summary>The raw unsigned value the next SaveSample writes.</summary>
        public void SetChannelValue(ulong value) { SetChannelValue(value, true); }

        /// <summary>The raw unsigned value and whether the sample is valid.</summary>
        public void SetChannelValue(ulong value, bool valid) { pending_.Remember(value, valid); }

        /// <summary>The raw floating point value the next SaveSample writes.</summary>
        public void SetChannelValue(double value) { SetChannelValue(value, true); }

        /// <summary>The raw floating point value and whether the sample is valid.</summary>
        public void SetChannelValue(double value, bool valid) { pending_.Remember(value, valid); }

        /// <summary>The text the next SaveSample writes, as strict UTF-8.</summary>
        public void SetChannelValue(string value) { SetChannelValue(value, true); }

        /// <summary>The text and whether the sample is valid. As in the wrapper a null
        /// text is refused, since the value the channel holds would otherwise be lost.</summary>
        public void SetChannelValue(string value, bool valid)
        {
            if (value == null) { throw new ArgumentNullException("value"); }
            pending_.Remember(value, valid);
        }

        /// <summary>The bytes the next SaveSample writes.</summary>
        public void SetChannelValue(byte[] value) { SetChannelValue(value, true); }

        /// <summary>The bytes and whether the sample is valid. The wrapper reads the
        /// length of its argument before it checks it, so a null array is a NullReferenceException there and an
        /// ArgumentNullException here.</summary>
        public void SetChannelValue(byte[] value, bool valid)
        {
            if (value == null) { throw new ArgumentNullException("value"); }
            pending_.Remember(value, valid);
        }

        /// <summary>The SI block of this channel, or null while none was created.</summary>
        public MdfSourceInformation SourceInformation
        {
            get
            {
                return pending_.Source == null ? null : PendingSource.FacadeOf(context_, pending_.Source);
            }
        }

        /// <summary>The CC block of this channel, or null while none was created.</summary>
        public MdfChannelConversion ChannelConversion
        {
            get
            {
                return pending_.Conversion == null ? null : PendingConversion.FacadeOf(context_, pending_.Conversion);
            }
        }

        /// <summary>The structure members of the channel: the components a writer added with
        /// CreateChannelComposition, in creation order. A channel of a file being read has none in this facade.</summary>
        public MdfChannel[] ChannelCompositions
        {
            get
            {
                if (pending_.Components == null) { return new MdfChannel[0]; }
                MdfChannel[] result = new MdfChannel[pending_.Components.Count];
                for (int i = 0; i < result.Length; i++) { result[i] = PendingChannel.FacadeOf(pending_.Components[i]); }
                return result;
            }
        }

        /// <summary>Cn_bit_count, the width of the field in the record.</summary>
        public uint BitCount
        {
            get
            {
                return pending_.BitCount;
            }
            set
            {
                context_.RejectFlushed();
                pending_.BitCount = value;
            }
        }

        /// <summary>Cn_bit_offset. The managed writer packs a record at byte
        /// boundaries, so only zero is possible.</summary>
        public ushort BitOffset
        {
            get
            {
                return pending_.BitOffset;
            }
            set
            {
                context_.RejectFlushed();
                pending_.BitOffset = value;
            }
        }

        /// <summary>Cn_data_bytes, the width of the channel field in bytes. It is the same
        /// field as <see cref="BitCount"/> in another unit: setting it writes the bit count the engine stores, and
        /// reading it is that count divided by eight, so a width that is not a whole number of bytes reads back
        /// narrowed (as the wrapper's own byte count does).</summary>
        public ulong DataBytes
        {
            get
            {
                return pending_.BitCount / 8UL;
            }
            set
            {
                context_.RejectFlushed();
                pending_.SetWidthFromBytes(value);
            }
        }

        /// <summary>Cn_data_type, which decides both the width rules and how the raw
        /// value is read back.</summary>
        public ChannelDataType DataType
        {
            get
            {
                return pending_.DataType;
            }
            set
            {
                context_.RejectFlushed();
                pending_.DataType = value;
            }
        }

        /// <summary>The comment of the channel: the MDF 4 CNcomment block or the
        /// 32-byte MDF 3.30 field the engine reads back as the comment.</summary>
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

        /// <summary>The long identifier of the channel, which the engine stores behind
        /// the channel source; MDF 3.30 has no such field for a group without one.</summary>
        public string DisplayName
        {
            get
            {
                return pending_.DisplayName;
            }
            set
            {
                context_.RejectFlushed();
                pending_.DisplayName = value ?? string.Empty;
            }
        }

        /// <summary>Whether an extended limit is stored. The engine has no field for
        /// one, so it never is.</summary>
        public bool ExtLimitUsed
        {
            get
            {
                return pending_.HasExtLimit;
            }
        }

        /// <summary>Cn_ext_limit_min and cn_ext_limit_max, read-only in the engine.</summary>
        public System.Tuple<double, double> ExtLimit
        {
            get
            {
                return pending_.HasExtLimit ? System.Tuple.Create(pending_.ExtLimitMin, pending_.ExtLimitMax) : System.Tuple.Create(0.0, 0.0);
            }
            set
            {
                context_.RejectFlushed();
                pending_.HasExtLimit = value != null && (value.Item1 != 0 || value.Item2 != 0);
                if (value != null) { pending_.ExtLimitMin = value.Item1; pending_.ExtLimitMax = value.Item2; }
            }
        }

        /// <summary>Cn_flags. Most bits of a channel of an ordinary group are
        /// read-only in the engine, which refuses them when the file is written.</summary>
        public CnFlag Flags
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

        /// <summary>The position of the channel inside its group.</summary>
        public long Index
        {
            get
            {
                return pending_.Index;
            }
        }

        /// <summary>Whether a limit is stored, which the engine cannot write.</summary>
        public bool LimitUsed
        {
            get
            {
                return pending_.HasLimit;
            }
        }

        /// <summary>Cn_limit_min and cn_limit_max, read-only in the engine.</summary>
        public System.Tuple<double, double> Limit
        {
            get
            {
                return pending_.HasLimit ? System.Tuple.Create(pending_.LimitMin, pending_.LimitMax) : System.Tuple.Create(0.0, 0.0);
            }
            set
            {
                context_.RejectFlushed();
                pending_.HasLimit = value != null && (value.Item1 != 0 || value.Item2 != 0);
                if (value != null) { pending_.LimitMin = value.Item1; pending_.LimitMax = value.Item2; }
            }
        }

        /// <summary>The MD block of the channel, which the writer never creates.</summary>
        public MdfMetaData MetaData
        {
            get
            {
                return pending_.Metadata;
            }
        }

        /// <summary>Cn_short_import_info, the name of the channel.</summary>
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

        /// <summary>Cn_decimal_count, which the engine does not store; like the
        /// wrapper the getter answers the default two digits.</summary>
        public byte Precision
        {
            get
            {
                return 2;
            }
        }

        /// <summary>Whether the precision is marked valid, which it never is in the
        /// managed writer.</summary>
        public bool PrecisionUsed
        {
            get
            {
                return false;
            }
        }

        /// <summary>Whether a range is stored, which the engine cannot write.</summary>
        public bool RangeUsed
        {
            get
            {
                return pending_.HasRange;
            }
        }

        /// <summary>Cn_range_min and cn_range_max, read-only in the engine.</summary>
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

        /// <summary>Cn_sample_rate, which has no field in the managed writer.</summary>
        public double SamplingRate
        {
            get
            {
                // v2.3.0 keeps no sampling rate (Cn4Block::SamplingRate is empty), so the value reads back as 0.
                return 0;
            }
            set
            {
                context_.RejectFlushed();
                pending_.SamplingRate = value;
            }
        }

        /// <summary>Cn_sync_type. The managed writer stores a time master only, so a
        /// synchronization type is refused when the file is written.</summary>
        public ChannelSyncType Sync
        {
            get
            {
                return pending_.Sync;
            }
            set
            {
                context_.RejectFlushed();
                pending_.Sync = value;
            }
        }

        /// <summary>Cn_type. The engine knows fixed length, variable length and the
        /// stored master, so the other kinds are refused when the file is written.</summary>
        public ChannelType Type
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

        /// <summary>Whether a unit is set, which decides whether the file links one.</summary>
        public bool UnitUsed
        {
            get
            {
                return pending_.Unit.Length != 0;
            }
        }

        /// <summary>Cn_unit. MDF 3.30 keeps at most 19 Latin-1 bytes of it.</summary>
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

        /// <summary>Cn_vlsd_dg_rec, read-only: the engine keeps variable length
        /// records in the data group of the channel group itself.</summary>
        public ulong VlsdRecordId
        {
            get
            {
                return pending_.VlsdRecordId;
            }
            set
            {
                context_.RejectFlushed();
                pending_.VlsdRecordId = value;
            }
        }

        /// <summary>The pending state behind this handle, which the writer and the group lookups of the
        /// facade read; it is not part of the reviewed surface.</summary>
        internal PendingChannel Pending { get { return pending_; } }
    }

    /// <summary>How a channel is stored or computed (cn_type).</summary>
    public enum ChannelType : byte
    {
        /// <summary>A channel stored in the record with a fixed size.</summary>
        FixedLength = 0,

        /// <summary>A variable length signal (VLSD): the record holds an offset into separate signal data.</summary>
        VariableLength = 1,

        /// <summary>The master channel of its group (time, angle, distance or index).</summary>
        Master = 2,

        /// <summary>A virtual master channel: its value is computed from the record index.</summary>
        VirtualMaster = 3,

        /// <summary>A synchronization channel that refers to an attachment or a stream.</summary>
        Sync = 4,

        /// <summary>A maximum length channel (MLSD): the value uses as many bytes as another channel says.</summary>
        MaxLength = 5,

        /// <summary>A virtual data channel: its value is computed from the record index.</summary>
        VirtualData = 6,
    }


    /// <summary>What a master or synchronization channel measures (cn_sync_type).</summary>
    public enum ChannelSyncType : byte
    {
        /// <summary>Not a synchronization or master channel.</summary>
        None = 0,

        /// <summary>The channel measures time.</summary>
        Time = 1,

        /// <summary>The channel measures an angle.</summary>
        Angle = 2,

        /// <summary>The channel measures a distance.</summary>
        Distance = 3,

        /// <summary>The channel counts records (an index).</summary>
        Index = 4,
    }


    /// <summary>The data type of a channel value (cn_data_type).</summary>
    public enum ChannelDataType : byte
    {
        /// <summary>Unsigned integer, little endian.</summary>
        UnsignedIntegerLe = 0,

        /// <summary>Unsigned integer, big endian.</summary>
        UnsignedIntegerBe = 1,

        /// <summary>Signed two's complement integer, little endian.</summary>
        SignedIntegerLe = 2,

        /// <summary>Signed two's complement integer, big endian.</summary>
        SignedIntegerBe = 3,

        /// <summary>IEEE 754 floating point, little endian.</summary>
        FloatLe = 4,

        /// <summary>IEEE 754 floating point, big endian.</summary>
        FloatBe = 5,

        /// <summary>Text in ISO 8859-1 (Latin-1), zero terminated.</summary>
        StringAscii = 6,

        /// <summary>Text in UTF-8, zero terminated.</summary>
        StringUTF8 = 7,

        /// <summary>Text in UTF-16, little endian, zero terminated.</summary>
        StringUTF16Le = 8,

        /// <summary>Text in UTF-16, big endian, zero terminated.</summary>
        StringUTF16Be = 9,

        /// <summary>Raw bytes.</summary>
        ByteArray = 10,

        /// <summary>A MIME sample: bytes of one document, its MIME type is the unit.</summary>
        MimeSample = 11,

        /// <summary>A MIME stream: bytes of a stream, its MIME type is the unit.</summary>
        MimeStream = 12,

        /// <summary>A CANopen date (7 bytes).</summary>
        CanOpenDate = 13,

        /// <summary>A CANopen time (6 bytes).</summary>
        CanOpenTime = 14,

        /// <summary>A complex number of two floats, little endian.</summary>
        ComplexLe = 15,

        /// <summary>A complex number of two floats, big endian.</summary>
        ComplexBE = 16,
    }


    /// <summary>Channel flags (cn_flags).</summary>
    public enum CnFlag : uint
    {
        /// <summary>No flags.</summary>
        None = 0,

        /// <summary>Every value of the channel is invalid.</summary>
        AllValuesInvalid = 1,

        /// <summary>The channel has an invalidation bit.</summary>
        InvalidValid = 2,

        /// <summary>The precision field is valid.</summary>
        PrecisionValid = 4,

        /// <summary>The physical range fields are valid.</summary>
        RangeValid = 8,

        /// <summary>The limit fields are valid.</summary>
        LimitValid = 16,

        /// <summary>The extended limit fields are valid.</summary>
        ExtendedLimitValid = 32,

        /// <summary>The values are discrete (no interpolation between samples).</summary>
        Discrete = 64,

        /// <summary>The channel is a calibration object.</summary>
        Calibration = 128,

        /// <summary>The channel is calculated rather than measured.</summary>
        Calculated = 65536,

        /// <summary>The channel is virtual.</summary>
        Virtual = 131072,

        /// <summary>The channel carries a bus event.</summary>
        BusEvent = 262144,

        /// <summary>The values only increase (or only decrease).</summary>
        StrictlyMonotonous = 524288,

        /// <summary>The channel is the default X axis of its group.</summary>
        DefaultX = 1048576,

        /// <summary>The channel holds an event signal.</summary>
        EventSignal = 2097152,

        /// <summary>The signal data is stored as a VLSD stream.</summary>
        VlsdDataStream = 4194304,
    }
}
