// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Text;

namespace MdfLib.Writing
{
    internal sealed class WriterSettings
    {
        internal readonly string Version;
        internal readonly bool Mdf3;
        internal readonly ulong StartTime;
        internal readonly ulong MaximumMetadata;
        internal readonly int MaximumRecord;
        internal readonly int MaximumChannels;
        internal readonly int MaximumText;
        internal readonly bool Compress;
        internal readonly int CompressionBlock;
        internal readonly int WriteBuffer; // 0 = unbuffered.
        internal readonly WriterMetadata Header; // Header description/properties and file history.
        internal readonly bool Upstream; // UpstreamIdentification: v2.3.0 identification and empty timer id.
        internal readonly string ProgramIdentifier; // prg_id as the caller sets it; null takes the default.
        private readonly Encoding encoding;

        internal WriterSettings(MdfWriterOptions options)
        {
            options = options ?? new MdfWriterOptions();
            Version = options.Version;
            Upstream = options.UpstreamIdentification;
            if (options.ProgramIdentifier != null && (options.ProgramIdentifier.Length > 8 || !IsAscii(options.ProgramIdentifier)))
            { throw new NotSupportedException("The ID block's prg_id holds eight ASCII characters at most."); }
            ProgramIdentifier = options.ProgramIdentifier;
            if (Version != "3.30" && Version != "4.10" && Version != "4.20")
            { throw new NotSupportedException("The writer supports only exact MDF 3.30, 4.10 and 4.20."); }
            if (options.MaximumMetadataBytes <= 0 || options.MaximumMetadataBytes > int.MaxValue)
            { throw new ArgumentOutOfRangeException(nameof(options.MaximumMetadataBytes)); }
            MaximumMetadata = (ulong)options.MaximumMetadataBytes;
            MaximumRecord = Positive(options.MaximumRecordBytes, nameof(options.MaximumRecordBytes));
            MaximumChannels = Positive(options.MaximumChannelCount, nameof(options.MaximumChannelCount));
            MaximumText = Positive(options.MaximumTextBytes, nameof(options.MaximumTextBytes));
            CompressionBlock = Positive(options.CompressionBlockBytes, nameof(options.CompressionBlockBytes));
            if (options.WriteBufferBytes < 0) { throw new ArgumentOutOfRangeException(nameof(options.WriteBufferBytes)); }
            WriteBuffer = options.WriteBufferBytes;
            Compress = options.CompressData;
            if (Compress && Version == "3.30") { throw new NotSupportedException("MDF 3.30 has no compressed data blocks."); }
            StartTime = options.StartTimeNanoseconds;
            Mdf3 = Version == "3.30";
            encoding = Mdf3 ? Encoding.GetEncoding(28591, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                : new UTF8Encoding(false, true);
            Metadata(1024);
            Header = WriterMetadata.Create(options, this);
        }

        private static bool IsAscii(string text)
        {
            foreach (char c in text) { if (c > 127) { return false; } }
            return true;
        }

        private static int Positive(int value, string name)
        {
            if (value <= 0) { throw new ArgumentOutOfRangeException(name); }
            return value;
        }

        internal void Metadata(ulong requested)
        {
            Limit(nameof(MdfWriterOptions.MaximumMetadataBytes), MaximumMetadata, requested);
        }

        internal static void Limit(string name, ulong maximum, ulong requested)
        {
            if (requested > maximum) { throw new MdfResourceLimitException(name, maximum, requested, 0); }
        }

        // one strict text policy for every metadata string. A name must be nonempty; a unit and a metadata
        // text (comment, long identifier, source name/path) may be empty, which the caller stores as nothing.
        internal int TextSize(string text, TextKind kind)
        {
            string name = kind == TextKind.Name ? "Name" : kind == TextKind.Unit ? "Unit" : "text";
            if (text == null) { throw new ArgumentNullException(name); }
            if (kind == TextKind.Name && text.Length == 0) { throw new ArgumentException("A name must be nonempty and text cannot contain NUL."); }
            if (text.IndexOf('\0') >= 0) { throw new ArgumentException("Text cannot contain NUL."); }
            if (Mdf3 && text.EndsWith(" ", StringComparison.Ordinal))
            { throw new NotSupportedException("MDF 3.30 names/units/metadata with trailing ASCII-space padding are not supported."); }
            int size;
            try { size = checked(encoding.GetByteCount(text) + 1); }
            catch (EncoderFallbackException error) { throw new ArgumentException("Text is not representable in the selected strict encoding.", error); }
            Limit(nameof(MdfWriterOptions.MaximumTextBytes), (ulong)MaximumText, (ulong)size);
            int maximum = !Mdf3 ? int.MaxValue : kind == TextKind.Name ? 65531 : kind == TextKind.Unit ? 20 : WriterMetadata.Mdf3MetadataBytes;
            if (size > maximum)
            {
                throw new NotSupportedException(kind == TextKind.Name ? "The MDF 3 TX block cannot exceed UInt16 length."
                    : kind == TextKind.Unit ? "MDF 3 units require at most 19 Latin-1 bytes."
                    : "MDF 3 metadata requires at most 128 Latin-1 bytes.");
            }
            return size;
        }

        // encodes a metadata text; null for an empty one, so no block and no link is written for it.
        internal byte[] EncodeMetadata(string text, TextKind kind, out int size)
        {
            if (text == null) { throw new ArgumentNullException("text", "A metadata text must not be null; use an empty string for none."); }
            if (text.Length == 0) { size = 0; return null; }
            size = TextSize(text, kind);
            return Encode(text, size);
        }

        internal Encoding TextEncoding { get { return encoding; } }

        // The stored text of an encoded metadata block, NUL stripped: what the group already holds before a replacement.
        internal string Text(byte[] bytes)
        {
            int end = Array.IndexOf(bytes, (byte)0);
            return encoding.GetString(bytes, 0, end < 0 ? bytes.Length : end);
        }

        internal byte[] Encode(string text, int size)
        {
            byte[] bytes = new byte[size];
            encoding.GetBytes(text, 0, text.Length, bytes, 0);
            return bytes; // Last byte is the required NUL, not lossy encoder fallback.
        }
    }

    internal sealed class WriteChannel
    {
        internal MdfValueKind Kind;
        internal int Bits, DataOffset;
        internal bool Big, Master, Linear;
        internal double Factor, Offset;
        internal byte[] Name, Unit;
        // metadata: Comment/Description are MDF 3 fixed CHAR fields, CommentText/Source* are MDF 4 blocks.
        internal byte[] Comment, CommentText, Description, SourceId, SourcePath, SourceComment;
        internal byte SourceType, BusType, SourceFlags; // SI payload bytes; flags: 1 name, 2 path, 4 comment, 8 sync bus.
        internal Encoding TextEncoding;
        internal long ChannelAddress, NameAddress, UnitAddress, ConversionAddress;
        internal long CommentAddress, SourceAddress, SourceNameAddress, SourcePathAddress, SourceCommentAddress;
        internal WriterConversion Conversion; // nonlinear conversion, null for identity/linear.
        // VLSD text/byte channel: the record holds a UInt64 offset into the channel's own VLSD channel group.
        internal bool Vlsd;
        internal long VlsdGroupAddress, VlsdCountOffset;
        internal ulong VlsdCount, VlsdBytes;
        internal byte[] VlsdValue; // Encoded value of the row being saved.
        // fixed array (CN template CA): Elements contiguous fields of Stride bytes and bits InvalidationBit + element.
        internal ulong[] Dimensions;
        internal int Elements = 1, Stride, InvalidationBit;
        internal long ArrayAddress;
        // explicit layout (bus logging). Type is cn_type, Parent/Size are channel indices in the group or -1.
        internal bool Explicit;
        internal int BitOffset, Type, Parent = -1, Size = -1;
        // structures: a structure channel reserves its window in the record and stores no value (Elements 0).
        internal bool Structure;
        internal uint Flags;
        internal bool Sd; // the record holds the SD offset of the value (see RawChannel.Sd).
        // field writes of an ordinary MDF 4 channel: cn_sync_type, cn_precision, the caller's cn_flags bits, and
        // the value range, limit and extended limit as {min, max} (null = not set).
        internal byte SyncType;
        internal bool HasPrecision;
        internal byte Precision;
        internal uint ChannelFlags;
        internal double[] Range, Limit, ExtLimit;

        internal static WriteChannel CreateExplicit(RawChannel value, WriterSettings settings, ulong allowance, out ulong charge)
        {
            int nameBytes = settings.TextSize(value.Name, TextKind.Name);
            int unitBytes = settings.TextSize(value.Unit ?? string.Empty, TextKind.Unit);
            // The MDF 4 value-to-value table (cc_type 5) of a raw member, as the v2.3.0 release writes its DLC map.
            WriterConversion conversion = null;
            ulong tableCharge = 0;
            if (value.Table != null)
            {
                conversion = new WriterConversion { Kind = MdfConversionKind.Table, WireType = 5, Values = value.Table, Texts = new byte[0][], TextAddresses = new long[0] };
                tableCharge = 128UL + 16UL * (ulong)value.Table.Length;
            }
            charge = 512UL + (ulong)nameBytes + (ulong)unitBytes + 160UL + 50 + tableCharge;
            settings.Metadata(checked(allowance + charge));
            return new WriteChannel
            {
                Kind = value.Kind, Bits = value.Bits, DataOffset = value.ByteOffset, BitOffset = value.BitOffset, Master = value.Type == 2,
                Name = settings.Encode(value.Name, nameBytes), Unit = settings.Encode(value.Unit ?? string.Empty, unitBytes),
                TextEncoding = settings.TextEncoding, Explicit = true, Type = value.Type, Parent = value.Parent, Size = value.Size,
                Flags = value.Flags, Sd = value.Sd, Conversion = conversion
            };
        }

        internal static WriteChannel Create(MdfChannelDefinition value, WriterSettings settings, int dataOffset, ulong allowance, out ulong charge)
        {
            if (value == null) { throw new ArgumentNullException(nameof(value)); }
            MdfValueKind kind = value.ValueKind;
            bool numeric = kind == MdfValueKind.UnsignedInteger || kind == MdfValueKind.SignedInteger || kind == MdfValueKind.FloatingPoint;
            bool field = kind == MdfValueKind.Text || kind == MdfValueKind.ByteArray;
            if (!numeric && !field && kind != MdfValueKind.Complex)
            { throw new NotSupportedException("Only numeric, complex, text and byte-array channels can be written."); }
            int bits = value.BitCount;
            if (numeric && (bits < 1 || bits > 64 || (kind == MdfValueKind.FloatingPoint && bits != 32 && bits != 64)))
            { throw new ArgumentOutOfRangeException(nameof(value.BitCount)); }
            if (!numeric)
            {
                // whole-byte fixed fields (bounded by MaximumTextBytes, MDF 3 UInt16 bit count) and 2 x binary32/64.
                if (kind == MdfValueKind.Complex && settings.Mdf3) { throw new NotSupportedException("MDF 3.30 has no complex data type."); }
                if (kind == MdfValueKind.Complex ? bits != 64 && bits != 128 : bits < 8 || bits % 8 != 0)
                { throw new ArgumentOutOfRangeException(nameof(value.BitCount)); }
                if (field && value.IsVariableLength)
                {
                    if (settings.Mdf3) { throw new NotSupportedException("MDF 3.30 has no variable-length signal data."); }
                    if (bits != 64) { throw new ArgumentOutOfRangeException(nameof(value.BitCount), "A VLSD channel stores a 64-bit offset; keep BitCount at 64."); }
                    if (value.IsBigEndian) { throw new ArgumentException("Text and byte-array values have no byte order.", nameof(value)); }
                }
                else if (field)
                {
                    WriterSettings.Limit(nameof(MdfWriterOptions.MaximumTextBytes), (ulong)settings.MaximumText, (ulong)(bits / 8));
                    if (settings.Mdf3 && bits > ushort.MaxValue) { throw new NotSupportedException("MDF 3 field bit counts cannot exceed UInt16."); }
                    if (value.IsBigEndian) { throw new ArgumentException("Text and byte-array fields have no byte order.", nameof(value)); }
                }
                if (value.IsMaster) { throw new ArgumentException("Only a numeric channel can be the stored master.", nameof(value)); }
                if (value.Conversion != null && kind != MdfValueKind.Text)
                { throw new NotSupportedException("Complex and byte-array channels cannot carry a conversion."); }
                if (value.HasLinearConversion) { throw new NotSupportedException("Only numeric channels can carry a linear conversion."); }
            }
            if (value.IsVariableLength && !field) { throw new NotSupportedException("Only text and byte-array channels can be variable-length."); }
            if (value.SyncType > 4) { throw new ArgumentOutOfRangeException(nameof(value.SyncType), "cn_sync_type is 0 to 4."); }
            bool fieldsOnly = value.Precision.HasValue || value.ChannelFlags != 0 || value.RangeMin.HasValue || value.LimitMin.HasValue
                || value.ExtLimitMin.HasValue || value.SyncType > 1;
            if (settings.Mdf3 && fieldsOnly)
            { throw new NotSupportedException("MDF 3.30 channel precision, flags, ranges, limits and synchronization are not written by the managed writer."); }
            ulong[] dimensions = null;
            int elements = 1;
            if (value.ArrayDimensions != null)
            {
                if (settings.Mdf3) { throw new NotSupportedException("Array channels are written only to MDF 4."); }
                if (value.IsMaster) { throw new ArgumentException("A stored master must be scalar.", nameof(value)); }
                if (value.IsVariableLength) { throw new NotSupportedException("Variable-length channels cannot be arrays."); }
                int rank = value.ArrayDimensions.Count;
                if (rank < 1 || rank > 255) { throw new ArgumentOutOfRangeException(nameof(value.ArrayDimensions), "An array has 1 to 255 dimensions."); }
                dimensions = new ulong[rank];
                long product = 1;
                for (int axis = 0; axis < rank; axis++)
                {
                    int size = value.ArrayDimensions[axis];
                    if (size < 1) { throw new ArgumentOutOfRangeException(nameof(value.ArrayDimensions), "Array dimension sizes must be positive."); }
                    product *= size;
                    if (product > int.MaxValue) { throw new ArgumentOutOfRangeException(nameof(value.ArrayDimensions), "An array holds at most Int32.MaxValue elements."); }
                    dimensions[axis] = (ulong)size;
                }
                elements = (int)product;
            }
            if (double.IsNaN(value.Factor) || double.IsInfinity(value.Factor) || double.IsNaN(value.Offset) || double.IsInfinity(value.Offset))
            { throw new ArgumentException("Conversion coefficients must be finite.", nameof(value)); }
            if (!value.HasLinearConversion && (value.Factor != 1 || value.Offset != 0))
            { throw new ArgumentException("Nonidentity coefficients require an explicit linear conversion.", nameof(value)); }
            if (value.Conversion != null && value.HasLinearConversion)
            { throw new ArgumentException("A channel has either a linear conversion or a Conversion definition, not both.", nameof(value)); }
            ulong conversionCharge = 0;
            WriterConversion conversion = value.Conversion == null ? null
                : WriterConversion.Create(value.Conversion, kind, value.IsMaster, settings, out conversionCharge);
            int nameBytes = settings.TextSize(value.Name, TextKind.Name);
            int unitBytes = settings.TextSize(value.Unit, TextKind.Unit);
            MdfSourceDefinition source = value.Source;
            byte[] comment, commentText, description, sourceId, sourcePath, sourceComment;
            int commentTextBytes, descriptionBytes, sourceBytes, sourcePathBytes, sourceCommentBytes;
            MetadataText.EncodeChannel(settings, value.Comment, value.CommonProperties, source, out comment, out commentText, out commentTextBytes,
                out description, out descriptionBytes);
            MetadataText.EncodeSource(settings, source, out sourceId, out sourceBytes, out sourcePath, out sourcePathBytes,
                out sourceComment, out sourceCommentBytes);
            charge = conversionCharge + 1024UL + 4UL * ((ulong)value.Name.Length + (ulong)value.Unit.Length)
                + (ulong)nameBytes + (ulong)unitBytes + (settings.Mdf3 ? 228UL + 5 + 62 : 160UL + 25 + 25 + 96)
                + (dimensions == null ? 0UL : 64UL + 8UL * (ulong)dimensions.Length)
                + (ulong)(comment == null ? 0 : comment.Length) + (ulong)commentTextBytes + (ulong)descriptionBytes
                + (ulong)sourceBytes + (ulong)sourcePathBytes + (ulong)sourceCommentBytes
                + (sourceId == null && sourceCommentBytes == 0 ? 0UL : 80UL) + (sourcePathBytes == 0 ? 0UL : 24UL) + (sourceCommentBytes == 0 ? 0UL : 24UL);
            settings.Metadata(checked(allowance + charge)); // Reserve before cloning/encoding/list capacity growth.
            double[] range = MetadataText.RangeOf(value.RangeMin, value.RangeMax, "RangeMin", "RangeMax");
            double[] limit = MetadataText.RangeOf(value.LimitMin, value.LimitMax, "LimitMin", "LimitMax");
            double[] extLimit = MetadataText.RangeOf(value.ExtLimitMin, value.ExtLimitMax, "ExtLimitMin", "ExtLimitMax");
            return new WriteChannel
            {
                Kind = kind, Bits = bits, DataOffset = dataOffset, Big = value.IsBigEndian, Master = value.IsMaster,
                Linear = value.HasLinearConversion, Factor = value.Factor, Offset = value.Offset, Conversion = conversion,
                Vlsd = value.IsVariableLength, Dimensions = dimensions, Elements = elements, Stride = (bits + 7) / 8,
                Name = settings.Encode(value.Name, nameBytes), Unit = settings.Encode(value.Unit, unitBytes),
                TextEncoding = settings.TextEncoding, Comment = comment, CommentText = commentText, Description = description,
                SourceId = sourceId, SourcePath = sourcePath, SourceComment = sourceComment,
                SourceType = MetadataText.SourceTypeOf(source), BusType = MetadataText.BusTypeOf(source),
                SourceFlags = MetadataText.FlagsOf(source),
                SyncType = value.SyncType, HasPrecision = value.Precision.HasValue, Precision = value.Precision ?? 0,
                ChannelFlags = value.ChannelFlags, Range = range, Limit = limit, ExtLimit = extLimit
            };
        }
    }
}

namespace MdfLib.Writing
{
    // the format-specific storage of one metadata text. MDF 3.30 has no comment or description TX link for a
    // CN, so its comment is the cn_description CHAR[128] field, which the reader reports back as MdfChannel.Comment, and a
    // long identifier has no field in that revision at all. MDF 4 links an MD block for the comment -- the schema's root
    // element around the escaped TX, exactly as the upstream library writes one, and MetadataTextReader.Comment reports
    // that TX rather than the markup -- and keeps the long identifier in the tail field of its CN block. An over-long text
    // is refused in every case, never truncated.
    internal static class MetadataText
    {
        // The single-comment payload of a CG/CE block: only the MD form exists there (MDF 3 has no comment for them).
        internal static void EncodeBlockComment(WriterSettings settings, string root, string text, IReadOnlyList<MdfCommonProperty> tags, out byte[] block, out int blockBytes)
        {
            if (settings.Mdf3) { throw new NotSupportedException("MDF 3.30 has no comment block for a channel group."); }
            block = WriterMetadata.Comment(root, text, tags, settings, out blockBytes);
        }

        internal static void EncodeChannel(WriterSettings settings, string comment, IReadOnlyList<MdfCommonProperty> tags, MdfSourceDefinition source, out byte[] mdf3,
            out byte[] mdf4, out int mdf4Bytes, out byte[] description, out int descriptionBytes)
        {
            mdf3 = Encode(settings, comment, tags, TextKind.Comment, out mdf4Bytes, out mdf4, "CNcomment");
            description = null;
            descriptionBytes = 0;
            if (source == null || !IsSet(source.LongIdentifier)) { return; }
            // MDF 3.30 has no long identifier of its own: cn_description is where that revision keeps the
            // channel comment, and one field cannot hold two texts, so the second one is refused.
            if (settings.Mdf3)
            { throw new NotSupportedException("MDF 3.30 has no cn_long_identifier field; its cn_description stores the channel comment, so a long identifier cannot be stored (MDF 4 keeps both)."); }
            byte[] text = settings.EncodeMetadata(source.LongIdentifier, TextKind.Description, out int size);
            if (text == null) { return; }
            // The CN block this writer emits is 160 bytes and the field starts at 134: 26 bytes, NUL included.
            if (size > WriterMetadata.Mdf4IdentifierBytes)
            { throw new NotSupportedException("The MDF 4 CN block stores cn_long_identifier in its 26-byte tail field, so the text with its NUL must fit 25 bytes; it is refused, not truncated."); }
            description = text;
            descriptionBytes = size;
        }

        // A text a caller may leave out of an object initializer: null and empty both mean "not written", so no
        // entry point has to spell string.Empty to get the default the type documents.
        internal static bool IsSet(string value)
        {
            return value != null && value.Length != 0;
        }

        // The three SI payload texts of one channel or group source; a source without a name stores no SI block at all.
        internal static void EncodeSource(WriterSettings settings, MdfSourceDefinition source, out byte[] name, out int nameBytes,
            out byte[] path, out int pathBytes, out byte[] comment, out int commentBytes)
        {
            name = path = comment = null;
            nameBytes = pathBytes = commentBytes = 0;
            if (source == null) { return; }
            if (settings.Mdf3) { throw new NotSupportedException("MDF 3.30 has no per-channel source information block."); }
            SourceTypeOf(source);
            BusTypeOf(source);
            if (IsSet(source.Name))
            {
                nameBytes = settings.TextSize(source.Name, TextKind.Description);
                name = settings.Encode(source.Name, nameBytes);
            }
            if (IsSet(source.Path))
            {
                pathBytes = settings.TextSize(source.Path, TextKind.Description);
                path = settings.Encode(source.Path, pathBytes);
            }
            bool sourceTags = WriterMetadata.HasTags(source.CommonProperties);
            if (IsSet(source.Comment) || sourceTags)
            {
                comment = WriterMetadata.Comment("SIcomment", source.Comment, source.CommonProperties, settings, out commentBytes);
            }
        }

        internal static byte SourceTypeOf(MdfSourceDefinition source)
        {
            if (source == null) { return 0; }
            if (source.SourceType < 1 || source.SourceType > 5) { throw new ArgumentOutOfRangeException("Source.SourceType", "A source type is 1..5."); }
            return source.SourceType;
        }

        internal static byte BusTypeOf(MdfSourceDefinition source)
        {
            if (source == null) { return 0; }
            if (source.BusType > 7) { throw new ArgumentOutOfRangeException("Source.BusType", "The MDF 4 bus type is 0..7."); }
            return source.BusType;
        }

        // A value range, limit or extended limit is both or neither of its two numbers, and finite.
        internal static double[] RangeOf(double? min, double? max, string minName, string maxName)
        {
            if (!min.HasValue && !max.HasValue) { return null; }
            if (!min.HasValue || !max.HasValue) { throw new ArgumentException(minName + " and " + maxName + " are set together or not at all.", minName); }
            if (double.IsNaN(min.Value) || double.IsInfinity(min.Value) || double.IsNaN(max.Value) || double.IsInfinity(max.Value))
            { throw new ArgumentException(minName + " and " + maxName + " must be finite.", minName); }
            return new[] { min.Value, max.Value };
        }

        // si_flags: the validity bits of the fields that are stored, plus the MDF 4.10 synchronization-bus marker (bit 3).
        internal static byte FlagsOf(MdfSourceDefinition source)
        {
            if (source == null) { return 0; }
            if (source.Flags.HasValue) { return source.Flags.Value; }
            byte flags = (byte)(IsSet(source.Name) ? 1 : 0);
            if (IsSet(source.Path)) { flags |= 2; }
            if (IsSet(source.Comment) || WriterMetadata.HasTags(source.CommonProperties)) { flags |= 4; }
            if (source.IsSyncBus) { flags |= 8; }
            return flags;
        }

        // The MDF 3 fixed-field text, or the MDF 4 MD payload and its byte count; both null when the text is unset.
        // Common properties make an MD block even without text; MDF 3.30 has no MD block for them.
        private static byte[] Encode(WriterSettings settings, string text, IReadOnlyList<MdfCommonProperty> tags, TextKind kind,
            out int blockBytes, out byte[] block, string root)
        {
            block = null;
            blockBytes = 0;
            bool hasTags = WriterMetadata.HasTags(tags);
            if (settings.Mdf3)
            {
                if (hasTags) { throw new NotSupportedException("MDF 3.30 has no MD block for common properties; only MDF 4 stores them."); }
                return settings.EncodeMetadata(text, kind, out int size);
            }
            if (!hasTags && !IsSet(text)) { return null; }
            // A comment of a CN is the cn_description CHAR[128] of that revision; longer text is refused, not cut (MDF 3 above).
            string xml = WriterMetadata.Wrap(root, text, tags);
            blockBytes = settings.TextSize(xml, kind);
            block = settings.Encode(xml, blockBytes);
            return null;
        }
    }

    /// <summary>Which metadata text a string is; it selects the strict size and padding policy of the writer.</summary>
    internal enum TextKind { Name, Unit, Comment, Description }

    // one channel of an explicit-layout group, validated by the layout tables that build it (MdfBusLogger).
    internal sealed class RawChannel
    {
        internal string Name, Unit;
        internal MdfValueKind Kind;
        internal int Bits, ByteOffset, BitOffset, Type, Parent = -1, Size = -1;
        internal uint Flags;
        internal double[] Table; // value-to-value pairs (raw, physical) of an MDF 4 table conversion, or null.
        // a VariableLength channel whose value lives in the group's SD block; the record holds its 8-byte offset.
        internal bool Sd;
    }

    internal sealed class RawGroup
    {
        internal string Name, SourceName, SourcePath, SourceComment, Comment;
        internal ushort Flags, PathSeparator;
        internal byte SourceType, BusType, SourceFlags;
        internal int DataBytes;
        internal readonly System.Collections.Generic.List<RawChannel> Channels = new System.Collections.Generic.List<RawChannel>();
    }
}
