// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// The read side of the facade. The engine reader is opened for one step at a time
// and its catalog is copied into the same pending tree the writer builds, so every read-side handle is the handle the
// writer has. Samples are kept per channel group as engine batches, raw and physical. The formatting helpers are the
// wrapper's MdfHelper functions of v2.3.0 (FormatDouble, NsToLocalIsoTime) and the text-to-number rule of std::istream.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace MdfLibrary
{
    // Copies the engine's catalog and header into the pending tree. Indices are the catalog's own: data group N of the
    // engine is DataGroups[N] of the facade, and channel group M inside it is Groups[M] of that data group.
    internal static class FacadeLoader
    {
        internal static void FillHeader(FacadeContext context, MdfLib.MdfFileHeader header)
        {
            context.ProgramIdentifier = FacadeFormat.Trim(header.ProgramIdentifier ?? string.Empty);
            PendingHeader target = context.Header;
            target.Author = FacadeFormat.Trim(header.Author ?? string.Empty);
            target.Department = FacadeFormat.Trim(header.Organization ?? string.Empty);
            target.Project = FacadeFormat.Trim(header.Project ?? string.Empty);
            target.Subject = FacadeFormat.Trim(header.Subject ?? string.Empty);
            target.StartTime = header.RawStartTimeNanoseconds;
            target.StartAngle = header.StartAngle;
            target.StartDistance = header.StartDistance;
            context.VersionText = header.Version ?? context.VersionText;
        }

        internal static void FillDescription(FacadeContext context, MdfLib.MdfFileModel model)
        {
            FillBlocks(context, model);
            // The measurement description is the TX text of the HDcomment (or the whole plain comment), not the raw XML.
            context.Header.Description = FacadeFormat.Trim(model.Description ?? string.Empty);
            context.Header.MeasurementId = FacadeFormat.Trim(model.MeasurementId ?? string.Empty);
            context.Header.RecorderId = FacadeFormat.Trim(model.RecorderId ?? string.Empty);
            context.Header.RecorderIndex = model.RecorderIndex;
            context.Header.Author = model.Author == null ? context.Header.Author : FacadeFormat.Trim(model.Author);
            context.Header.Department = model.Organization == null ? context.Header.Department : FacadeFormat.Trim(model.Organization);
            context.Header.Project = model.Project == null ? context.Header.Project : FacadeFormat.Trim(model.Project);
            context.Header.Subject = model.Subject == null ? context.Header.Subject : FacadeFormat.Trim(model.Subject);
        }

        // The file-history, attachment and event blocks of the read model. Attachments come first, because an event
        // refers to them; parents and range events are linked once every event has its node.
        private static void FillBlocks(FacadeContext context, MdfLib.MdfFileModel model)
        {
            PendingHeader header = context.Header;
            header.Histories.Clear();
            header.Attachments.Clear();
            header.Events.Clear();
            if (model.FileHistory != null)
            {
                foreach (MdfLib.MdfFileHistory engine in model.FileHistory)
                {
                    PendingHistory history = new PendingHistory();
                    history.Index = engine.Index;
                    history.Time = engine.RawTimeNanoseconds;
                    history.Description = engine.Comment ?? string.Empty;
                    history.ToolName = engine.ToolIdentifier ?? string.Empty;
                    history.ToolVendor = engine.ToolVendor ?? string.Empty;
                    history.ToolVersion = engine.ToolVersion ?? string.Empty;
                    history.UserName = engine.UserName ?? string.Empty;
                    header.Histories.Add(history);
                }
            }
            Dictionary<MdfLib.MdfAttachment, PendingAttachment> attachments = new Dictionary<MdfLib.MdfAttachment, PendingAttachment>();
            if (model.Attachments != null)
            {
                foreach (MdfLib.MdfAttachment engine in model.Attachments)
                {
                    PendingAttachment node = new PendingAttachment(context);
                    node.FileName = engine.FileName ?? string.Empty;
                    node.MimeType = engine.MimeType ?? string.Empty;
                    node.Comment = FacadeFormat.TxText(engine.Comment);
                    node.Embedded = engine.IsEmbedded;
                    node.Compressed = engine.IsCompressed;
                    node.CreatorIndex = engine.CreatorIndex;
                    node.Md5 = engine.HasChecksum ? FacadeFormat.HexText(engine.GetChecksum()) : string.Empty;
                    node.FromFile = true;
                    header.Attachments.Add(node);
                    attachments[engine] = node;
                }
            }
            Dictionary<MdfLib.MdfEvent, PendingEvent> events = new Dictionary<MdfLib.MdfEvent, PendingEvent>();
            if (model.Events != null)
            {
                foreach (MdfLib.MdfEvent engine in model.Events)
                {
                    PendingEvent node = new PendingEvent(context);
                    node.Name = engine.Name ?? string.Empty;
                    node.Description = FacadeFormat.TxText(engine.Comment);
                    node.GroupName = engine.GroupName ?? string.Empty;
                    node.Type = (EventType)(int)engine.Type;
                    node.Sync = (SyncType)(int)engine.SyncType;
                    node.Range = (RangeType)(int)engine.RangeType;
                    node.Cause = (EventCause)(int)engine.Cause;
                    node.EventCreatorIndex = engine.CreatorIndex;
                    node.SyncValue = engine.SyncBaseValue;
                    node.SyncFactor = engine.SyncFactor;
                    node.FromFile = true;
                    header.Events.Add(node);
                    events[engine] = node;
                }
                foreach (MdfLib.MdfEvent engine in model.Events)
                {
                    PendingEvent node = events[engine];
                    PendingEvent parent, range;
                    if (engine.Parent != null && events.TryGetValue(engine.Parent, out parent)) { node.Parent = parent; }
                    if (engine.Range != null && events.TryGetValue(engine.Range, out range)) { node.RangeEvent = range; }
                    if (engine.Attachments != null)
                    {
                        foreach (MdfLib.MdfAttachment engineAttachment in engine.Attachments)
                        {
                            PendingAttachment attachment;
                            if (attachments.TryGetValue(engineAttachment, out attachment)) { node.Attachments.Add(attachment); }
                        }
                    }
                }
            }
        }

        internal static void FillGroups(FacadeContext context, MdfLib.MdfFileHeader header, IReadOnlyList<MdfLib.MdfDataGroup> catalog)
        {
            context.DataGroups.Clear();
            context.Groups.Clear();
            foreach (MdfLib.MdfDataGroup engineGroup in catalog)
            {
                PendingDataGroup dataGroup = context.AddDataGroup();
                foreach (MdfLib.MdfChannelGroup engineCg in engineGroup.ChannelGroups)
                {
                    PendingGroup group = context.AddChannelGroup(dataGroup);
                    group.Name = FacadeFormat.Trim(engineCg.Name ?? string.Empty);
                    group.Description = FacadeFormat.Trim(FacadeFormat.TxText(engineCg.Comment));
                    group.Flags = engineCg.Flags;
                    group.PathSeparator = engineCg.PathSeparator;
                    group.SampleCount = engineCg.SampleCount;
                    group.Source = SourceOf(context, engineCg.Source);
                    foreach (int engineIndex in FacadeFormat.ChannelOrder(engineCg.Channels))
                    {
                        MdfLib.MdfChannel engineCn = engineCg.Channels[engineIndex];
                        PendingChannel channel = new PendingChannel(context, group);
                        group.Channels.Add(channel);
                        FillChannel(context, channel, engineCn, header);
                    }
                }
            }
        }

        private static void FillChannel(FacadeContext context, PendingChannel channel, MdfLib.MdfChannel engine, MdfLib.MdfFileHeader header)
        {
            channel.Name = FacadeFormat.Trim(engine.Name ?? string.Empty);
            channel.Unit = FacadeFormat.Trim(UnitOf(engine, header));
            channel.Description = FacadeFormat.Trim(IsMdf3(header) ? (engine.FixedDescription ?? string.Empty) : FacadeFormat.TxText(engine.Comment));
            channel.DataType = (ChannelDataType)FacadeDataTypeOf(engine, header);
            // cn_type as stored; a master is reported as Master (2) whichever revision wrote it, except the virtual master (3).
            byte storedType = engine.StoredChannelType;
            if (engine.IsMaster && storedType != 3) { storedType = 2; }
            channel.Type = (ChannelType)storedType;
            channel.Sync = (ChannelSyncType)engine.SynchronizationType;
            channel.Flags = (CnFlag)engine.StoredFlags;
            channel.BitCount = engine.BitCount;
            channel.BitOffset = (ushort)engine.StoredBitOffset;
            channel.Source = SourceOf(context, engine.Source);
            channel.Conversion = ConversionOf(context, engine.Conversion, engine.HasConversionBlock);
        }

        // The wrapper's Cn3Block::DataType: an MDF 3 code 0-6 or 9-16 maps to the byte order of the file's ID block
        // (codes 9-12 are Motorola, 13-16 Intel); code 7 is ASCII text, 8 a byte array (a CANopen date or time is a
        // conversion the MDF 3 reader does not model). An MDF 4 cn_data_type is already the facade code.
        private static byte FacadeDataTypeOf(MdfLib.MdfChannel engine, MdfLib.MdfFileHeader header)
        {
            byte stored = engine.StoredDataType;
            if (header == null || !header.Version.StartsWith("3.", StringComparison.Ordinal)) { return stored; }
            bool big = header.DefaultDataIsBigEndian;
            switch (stored)
            {
                case 0: return (byte)(big ? 1 : 0);
                case 1: return (byte)(big ? 3 : 2);
                case 2: case 3: case 4: case 5: case 6: return (byte)(big ? 5 : 4);
                case 7: return 6;
                case 8: return 10;
                case 9: return 1;
                case 10: return 3;
                case 11: case 12: return 5;
                case 13: return 0;
                case 14: return 2;
                case 15: case 16: return 4;
                default: return stored;
            }
        }

        private static bool IsMdf3(MdfLib.MdfFileHeader header)
        {
            return header != null && header.Version != null && header.Version.StartsWith("3.", StringComparison.Ordinal);
        }

        // The wrapper's default unit of a channel that stores none. MDF 4: the engine's reader already applies the
        // synchronization default (MetadataReader.DefaultUnit). MDF 3: the conversion's unit, else a master's second.
        private static string UnitOf(MdfLib.MdfChannel engine, MdfLib.MdfFileHeader header)
        {
            if (!string.IsNullOrEmpty(engine.Unit)) { return engine.Unit; }
            if (IsMdf3(header)) { return engine.IsMaster ? "s" : string.Empty; }
            return string.Empty;
        }

        private static PendingSource SourceOf(FacadeContext context, MdfLib.MdfSourceInformation engine)
        {
            if (engine == null) { return null; }
            PendingSource source = new PendingSource(context);
            source.Name = engine.Name ?? string.Empty;
            source.Path = engine.Path ?? string.Empty;
            source.Comment = engine.Comment ?? string.Empty;
            source.SourceType = engine.SourceType;
            source.BusType = engine.BusType;
            source.SiFlags = engine.Flags;
            source.Index = context.NewIndex();
            return source;
        }

        // The engine's conversion kinds map one to one onto the reviewed ConversionType, except that the engine's
        // Identity is the absence of a conversion for the facade and an unsupported block reads as none.
        private static PendingConversion ConversionOf(FacadeContext context, MdfLib.MdfChannelConversion engine, bool present)
        {
            if (engine == null || engine.Kind == MdfLib.MdfConversionKind.Unsupported || (engine.Kind == MdfLib.MdfConversionKind.Identity && !present))
            {
                return null;
            }
            PendingConversion conversion = new PendingConversion(context);
            conversion.Index = context.NewIndex();
            conversion.Name = engine.Name ?? string.Empty;
            conversion.Unit = engine.Unit ?? string.Empty;
            // The engine returns an MD comment as its XML root; the wrapper reads the TX text, as it does for CG and CN.
            conversion.Description = FacadeFormat.TxText(engine.Comment);
            conversion.Type = TypeOf(engine.Kind);
            if (engine.Kind == MdfLib.MdfConversionKind.Identity) { return conversion; }
            if (engine.Kind == MdfLib.MdfConversionKind.Linear)
            {
                // A linear conversion is the pair of raw values the wrapper keeps: offset first, factor second.
                conversion.Parameter(0, engine.Offset.GetValueOrDefault());
                conversion.Parameter(1, engine.Factor.GetValueOrDefault());
                return conversion;
            }
            for (int i = 0; i < engine.Parameters.Count; i++) { conversion.Parameter((ushort)i, engine.Parameters[i]); }
            for (int i = 0; i < engine.Texts.Count; i++) { conversion.Reference((ushort)i, engine.Texts[i] ?? string.Empty); }
            conversion.Formula = engine.Formula ?? string.Empty;
            return conversion;
        }

        private static ConversionType TypeOf(MdfLib.MdfConversionKind kind)
        {
            switch (kind)
            {
                case MdfLib.MdfConversionKind.Linear: return ConversionType.Linear;
                case MdfLib.MdfConversionKind.Rational: return ConversionType.Rational;
                case MdfLib.MdfConversionKind.Algebraic: return ConversionType.Algebraic;
                case MdfLib.MdfConversionKind.TableInterpolation: return ConversionType.ValueToValueInterpolation;
                case MdfLib.MdfConversionKind.Table: return ConversionType.ValueToValue;
                case MdfLib.MdfConversionKind.RangeTable: return ConversionType.ValueRangeToValue;
                case MdfLib.MdfConversionKind.Polynomial: return ConversionType.Polynomial;
                case MdfLib.MdfConversionKind.ValueToText: return ConversionType.ValueToText;
                case MdfLib.MdfConversionKind.RangeToText: return ConversionType.ValueRangeToText;
                case MdfLib.MdfConversionKind.TextToValue: return ConversionType.TextToValue;
                case MdfLib.MdfConversionKind.TextToText: return ConversionType.TextToTranslation;
                case MdfLib.MdfConversionKind.BitfieldToText: return ConversionType.BitfieldToText;
                case MdfLib.MdfConversionKind.Exponential: return ConversionType.Exponential;
                case MdfLib.MdfConversionKind.Logarithmic: return ConversionType.Logarithmic;
                default: return ConversionType.NoConversion;
            }
        }
    }

    // The value formats of the wrapper's observer: MdfHelper::FormatDouble, MdfHelper::NsToLocalIsoTime and the
    // text-to-number rule of std::istringstream (leading blanks skipped, the longest numeric prefix read, 0 if none).
    internal static class FacadeFormat
    {
        // MdfHelper::Trim, applied by the wrapper to every TX text (Tx4Block::Text, Tx3Block::Text): the white space at
        // both ends is removed. The characters are those of the C isspace of the "C" locale.
        internal static string Trim(string text)
        {
            if (string.IsNullOrEmpty(text)) { return text ?? string.Empty; }
            int start = 0;
            int end = text.Length;
            while (start < end && IsCSpace(text[start])) { start++; }
            while (end > start && IsCSpace(text[end - 1])) { end--; }
            return text.Substring(start, end - start);
        }

        private static bool IsCSpace(char c)
        {
            return c == ' ' || c == '\t' || c == '\n' || c == '\v' || c == '\f' || c == '\r';
        }

        // The order of the wrapper's channel list: each channel, followed by its components (the members of a structure
        // come right after the structure), then any channel the walk did not reach. The result holds the engine index
        // of each facade position.
        internal static int[] ChannelOrder(IReadOnlyList<MdfLib.MdfChannel> channels)
        {
            Dictionary<MdfLib.MdfChannel, int> positions = new Dictionary<MdfLib.MdfChannel, int>();
            for (int i = 0; i < channels.Count; i++) { positions[channels[i]] = i; }
            bool[] placed = new bool[channels.Count];
            List<int> order = new List<int>();
            for (int i = 0; i < channels.Count; i++)
            {
                if (channels[i].Parent == null) { PlaceChannel(channels, positions, placed, order, i); }
            }
            for (int i = 0; i < channels.Count; i++) { PlaceChannel(channels, positions, placed, order, i); }
            return order.ToArray();
        }

        private static void PlaceChannel(IReadOnlyList<MdfLib.MdfChannel> channels, Dictionary<MdfLib.MdfChannel, int> positions,
            bool[] placed, List<int> order, int index)
        {
            if (placed[index]) { return; }
            placed[index] = true;
            order.Add(index);
            if (channels[index].Components == null) { return; }
            foreach (MdfLib.MdfChannel component in channels[index].Components)
            {
                int position;
                if (component != null && positions.TryGetValue(component, out position)) { PlaceChannel(channels, positions, placed, order, position); }
            }
        }

        // MdfHelper::CanOpenDateArrayToNs (7 bytes) and CanOpenTimeArrayToNs (6 bytes): a CANopen date as nanoseconds
        // since 1970, a CANopen time as nanoseconds since 1984-01-01, both in local time as mktime reads them.
        internal static ulong CanOpenNs(byte[] bytes, bool date)
        {
            int size = date ? 7 : 6;
            byte[] b = new byte[size];
            Array.Copy(bytes, b, Math.Min(bytes == null ? 0 : bytes.Length, size));
            if (date)
            {
                ulong msSinceMinute = (uint)(b[0] | (b[1] << 8));
                int minute = b[2] & 0x3F;
                int hour = b[3] & 0x1F;
                int day = b[4] & 0x1F;
                int month = b[5] & 0x3F;
                int year = b[6] & 0x7F;
                DateTime local = new DateTime(year >= 70 ? 1900 + year : 2000 + year, 1, 1)
                    .AddMonths(month - 1).AddDays(day - 1).AddHours(hour).AddMinutes(minute);
                return msSinceMinute * 1000000UL + SecondsSince1970(local) * 1000000000UL;
            }
            ulong ms = (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
            ulong days = (uint)(b[4] | (b[5] << 8));
            ulong sec1984 = SecondsSince1970(new DateTime(1984, 1, 1));
            return sec1984 * 1000000000UL + days * 86400000000000UL + ms * 1000000UL;
        }

        private static ulong SecondsSince1970(DateTime local)
        {
            DateTime utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.Local);
            return (ulong)((utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Ticks / TimeSpan.TicksPerSecond);
        }

        // The text of an MD comment: the TX element of an XML comment, which is the text the wrapper reports; a comment
        // without a TX element, or one that is not XML, is reported as written.
        internal static string TxText(string comment)
        {
            if (string.IsNullOrEmpty(comment)) { return string.Empty; }
            string trimmed = comment.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] != '<') { return comment; }
            try
            {
                System.Xml.XmlReaderSettings settings = new System.Xml.XmlReaderSettings();
                settings.DtdProcessing = System.Xml.DtdProcessing.Prohibit;
                settings.XmlResolver = null;
                using (System.Xml.XmlReader xml = System.Xml.XmlReader.Create(new System.IO.StringReader(comment), settings))
                {
                    while (xml.Read())
                    {
                        if (xml.NodeType == System.Xml.XmlNodeType.Element && xml.LocalName == "TX") { return xml.ReadElementContentAsString(); }
                    }
                }
            }
            catch (System.Xml.XmlException) { return comment; }
            return comment;
        }

        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        internal static string FormatDouble(double value, int decimals)
        {
            return FormatDouble(value, decimals, false);
        }

        internal static string FormatDouble(double value, int decimals, bool fixedDecimals)
        {
            if (decimals > 20) { decimals = 20; }
            if (decimals == 0) { fixedDecimals = false; }
            string text;
            long valueInt = (long)value;
            if (value == valueInt && !fixedDecimals)
            {
                text = valueInt.ToString(CultureInfo.InvariantCulture);
            }
            else if (decimals == 0)
            {
                text = ((long)Math.Floor(value + 0.5)).ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                text = value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            }
            // Too many digits switch to the %G form, which the wrapper's snprintf writes with six significant digits.
            if (text.Length > 6 + decimals)
            {
                text = value.ToString("G6", CultureInfo.InvariantCulture);
            }
            bool haveDecimal = text.IndexOf('.') >= 0 && text.IndexOf('E') < 0;
            if (!fixedDecimals && haveDecimal)
            {
                while (text.Length > 0 && text[text.Length - 1] == '0') { text = text.Substring(0, text.Length - 1); }
            }
            else if (fixedDecimals && haveDecimal)
            {
                int decPos = text.IndexOf('.');
                int nofDecimals = text.Length - decPos - 1;
                for (; nofDecimals < decimals; nofDecimals++) { text += "0"; }
            }
            return text;
        }

        internal static string NsToLocalIsoTime(ulong nanoseconds)
        {
            ulong milliseconds = (nanoseconds / 1000000UL) % 1000UL;
            long seconds = (long)(nanoseconds / 1000000000UL);
            DateTime local = Epoch.AddSeconds(seconds).ToLocalTime();
            string text = local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            if (milliseconds > 0) { text += "." + milliseconds.ToString("000", CultureInfo.InvariantCulture); }
            return text;
        }

        // The unsigned read of std::istream: a sign is accepted (a negative number wraps), digits stop at the first
        // other character, overflow saturates, and text without digits reads as zero.
        internal static ulong ParseUnsigned(string text)
        {
            int i = SkipBlanks(text);
            bool negative = false;
            if (i < text.Length && (text[i] == '+' || text[i] == '-')) { negative = text[i] == '-'; i++; }
            ulong value = 0;
            bool overflow = false;
            int digits = 0;
            for (; i < text.Length && text[i] >= '0' && text[i] <= '9'; i++, digits++)
            {
                ulong digit = (ulong)(text[i] - '0');
                if (value > (ulong.MaxValue - digit) / 10UL) { overflow = true; }
                else { value = value * 10UL + digit; }
            }
            if (digits == 0) { return 0; }
            if (overflow) { return ulong.MaxValue; }
            return negative ? unchecked(0UL - value) : value;
        }

        internal static long ParseSigned(string text)
        {
            int i = SkipBlanks(text);
            bool negative = false;
            if (i < text.Length && (text[i] == '+' || text[i] == '-')) { negative = text[i] == '-'; i++; }
            ulong value = 0;
            bool overflow = false;
            int digits = 0;
            for (; i < text.Length && text[i] >= '0' && text[i] <= '9'; i++, digits++)
            {
                ulong digit = (ulong)(text[i] - '0');
                if (value > (ulong.MaxValue - digit) / 10UL) { overflow = true; }
                else { value = value * 10UL + digit; }
            }
            if (digits == 0) { return 0; }
            ulong limit = negative ? 9223372036854775808UL : 9223372036854775807UL;
            if (overflow || value > limit) { return negative ? long.MinValue : long.MaxValue; }
            return negative ? unchecked(-(long)value) : (long)value;
        }

        internal static double ParseDouble(string text)
        {
            int i = SkipBlanks(text);
            int start = i;
            if (i < text.Length && (text[i] == '+' || text[i] == '-')) { i++; }
            int digits = 0;
            while (i < text.Length && char.IsDigit(text[i])) { i++; digits++; }
            if (i < text.Length && text[i] == '.')
            {
                i++;
                while (i < text.Length && char.IsDigit(text[i])) { i++; digits++; }
            }
            if (digits == 0) { return 0.0; }
            if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
            {
                int exponent = i + 1;
                if (exponent < text.Length && (text[exponent] == '+' || text[exponent] == '-')) { exponent++; }
                if (exponent < text.Length && char.IsDigit(text[exponent]))
                {
                    while (exponent < text.Length && char.IsDigit(text[exponent])) { exponent++; }
                    i = exponent;
                }
            }
            double value;
            return double.TryParse(text.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : 0.0;
        }

        private static int SkipBlanks(string text)
        {
            int i = 0;
            while (i < text.Length && char.IsWhiteSpace(text[i])) { i++; }
            return i;
        }

        // Upper-case hexadecimal of every byte, which is what the wrapper prints for a byte-array sample as text.
        internal static string HexText(byte[] bytes)
        {
            if (bytes == null) { return string.Empty; }
            System.Text.StringBuilder text = new System.Text.StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes) { text.Append(value.ToString("X2", CultureInfo.InvariantCulture)); }
            return text.ToString();
        }
    }
}
