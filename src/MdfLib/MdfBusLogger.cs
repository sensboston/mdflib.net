// Copyright 2025 Ingemar Hedvall
// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Frame member names and byte/bit positions follow pinned MIT mdflib's CAN/LIN/Ethernet/FlexRay/MOST config adapters
// (ASAM MDF 4 bus logging); maximum-length (MLSD) payload storage, validation and the managed writer path are new.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using MdfLib.Writing;

namespace MdfLib
{
    /// <summary>Options of <see cref="MdfBusLogger"/>; values are snapshotted at creation.</summary>
    internal sealed class MdfBusLoggerOptions
    {
        /// <summary>Gets or sets the MDF 4 revision, "4.10" (default) or "4.20".</summary>
        public string Version { get; set; } = "4.10";
        /// <summary>Gets or sets the measurement start time in nanoseconds since 1970-01-01 UTC.</summary>
        public ulong StartTimeNanoseconds { get; set; }
        /// <summary>Gets or sets the network name stored in each group's source information; null uses the bus name.</summary>
        public string NetworkName { get; set; }
        /// <summary>Gets or sets the payload capacity per frame; 0 selects the bus default (CAN 64, Ethernet 1500,
        /// FlexRay 254, MOST 1524). LIN is always 8. Every record reserves this many payload bytes.</summary>
        public int MaxDataLength { get; set; }
        /// <summary>Gets or sets whether record data is zlib-compressed (DZ), which recovers most unused payload space.</summary>
        public bool CompressData { get; set; }
        /// <summary>Gets or sets the uncompressed block size per channel group, as <see cref="MdfWriterOptions.CompressionBlockBytes"/>.</summary>
        public int CompressionBlockBytes { get; set; } = 262144;
        /// <summary>Gets or sets the frame kinds that get a channel group (in enumeration order); null selects the primary
        /// kinds: CAN data/remote/error frames, LIN_Frame, ETH_Frame, FLX_Frame and MOST messages/packets/Ethernet packets.
        /// Every kind must belong to the logged bus.</summary>
        public IList<MdfBusMessageType> MessageTypes { get; set; }
        /// <summary>Gets or sets the record write buffer, as <see cref="MdfWriterOptions.WriteBufferBytes"/>; default 0. It applies
        /// to single-group uncompressed logs.</summary>
        public int WriteBufferBytes { get; set; }
        /// <summary>Gets or sets the frame group layout; <see cref="MdfBusLoggerLayout.Extended"/> by default. The v2.3.0
        /// layout is CAN only and puts all frame groups in one data group.</summary>
        public MdfBusLoggerLayout Layout { get; set; } = MdfBusLoggerLayout.Extended;
        /// <summary>Gets or sets the identification as MDF Library v2.3.0 writes it; see <see cref="MdfWriterOptions.UpstreamIdentification"/>.</summary>
        public bool UpstreamIdentification { get; set; }
        /// <summary>Gets or sets the ID block's prg_id; see <see cref="MdfWriterOptions.ProgramIdentifier"/>.</summary>
        public string ProgramIdentifier { get; set; }
    }

    /// <summary>The frame group layout of a bus log.</summary>
    internal enum MdfBusLoggerLayout
    {
        /// <summary>One data group per frame kind, with every CAN member (FrameDuration, CRC, R0, R1).</summary>
        Extended = 0,
        /// <summary>The layout of the v2.3.0 release: the CAN frame groups share one data group, the payload follows
        /// DataLength, and FrameDuration, CRC, R0 and R1 are not stored.</summary>
        V230 = 1,
    }

    /// <summary>Writes a new ASAM MDF 4 bus logging file for one bus: one sorted channel group per frame
    /// kind with a float64 time master "t", a byte-array frame structure (for example "CAN_DataFrame") and its
    /// components ("CAN_DataFrame.ID",...). The payload is a maximum-length (MLSD) byte array sized by DataLength.</summary>
    /// <remarks>Lifecycle and failure rules are those of <see cref="MdfWriter"/> (explicit start, stop and finalize;
    /// not thread-safe). <see cref="MdfBusLoggerOptions.MessageTypes"/> selects the frame kinds (groups); members without a
    /// dedicated <see cref="MdfBusMessage"/> property are written from <see cref="MdfBusMessage.Fields"/>.</remarks>
    internal sealed class MdfBusLogger : IDisposable
    {
        private readonly MdfWriter writer;
        private readonly int[] groups = new int[BusFrameLayout.All.Length];
        private readonly BusFrameLayout[] layouts;
        private readonly int maxLength;

        private MdfBusLogger(MdfWriter writer, MdfBusType bus, int maxLength, BusFrameLayout[] layouts)
        {
            this.writer = writer;
            BusType = bus;
            this.maxLength = maxLength;
            this.layouts = layouts;
            for (int i = 0; i < groups.Length; i++) { groups[i] = -1; }
        }

        /// <summary>Gets the logged bus.</summary>
        public MdfBusType BusType { get; }
        /// <summary>Gets the payload capacity per frame.</summary>
        public int MaxDataLength { get { return maxLength; } }
        /// <summary>Gets the writer state.</summary>
        public MdfWriterState State { get { return writer.State; } }
        /// <summary>Gets the number of accepted frames of all kinds.</summary>
        public ulong MessageCount { get { return writer.SampleCount; } }

        /// <summary>Creates a logger over a writable, seekable, empty stream.</summary>
        /// <exception cref="ArgumentOutOfRangeException">Unknown bus or invalid payload capacity.</exception>
        public static MdfBusLogger Create(Stream stream, MdfBusType busType, bool leaveOpen = false, MdfBusLoggerOptions options = null)
        {
            options = options ?? new MdfBusLoggerOptions();
            int max = Capacity(busType, options);
            return Configure(MdfWriter.Create(stream, leaveOpen, WriterOptions(options)), busType, max, options);
        }

        /// <summary>Appends this bus's channel groups to a finalized MDF 4 stream; see <see cref="MdfWriter.Append"/>.</summary>
        /// <remarks>The options Version must match the file revision.</remarks>
        public static MdfBusLogger Append(Stream stream, MdfBusType busType, bool leaveOpen = false, MdfBusLoggerOptions options = null)
        {
            options = options ?? new MdfBusLoggerOptions();
            int max = Capacity(busType, options);
            return Configure(MdfWriter.Append(stream, leaveOpen, WriterOptions(options)), busType, max, options);
        }

        /// <summary>Creates a logger writing a new file (CreateNew semantics).</summary>
        public static MdfBusLogger Create(string path, MdfBusType busType, MdfBusLoggerOptions options = null)
        {
            options = options ?? new MdfBusLoggerOptions();
            int max = Capacity(busType, options);
            return Configure(MdfWriter.Create(path, WriterOptions(options)), busType, max, options);
        }

        private static MdfWriterOptions WriterOptions(MdfBusLoggerOptions options)
        {
            if (options.Version != "4.10" && options.Version != "4.20") { throw new NotSupportedException("Bus logging requires MDF 4.10 or 4.20 output."); }
            return new MdfWriterOptions
            {
                Version = options.Version, StartTimeNanoseconds = options.StartTimeNanoseconds,
                CompressData = options.CompressData, CompressionBlockBytes = options.CompressionBlockBytes,
                WriteBufferBytes = options.WriteBufferBytes,
                UpstreamIdentification = options.UpstreamIdentification, ProgramIdentifier = options.ProgramIdentifier
            };
        }

        // Checked before any destination is created or touched.
        private static List<MdfBusMessageType> Selection(MdfBusType bus, MdfBusLoggerOptions options)
        {
            if (options.MessageTypes == null) { return null; }
            List<MdfBusMessageType> selected = new List<MdfBusMessageType>(options.MessageTypes);
            if (selected.Count == 0) { throw new ArgumentException("MessageTypes must name at least one frame kind.", nameof(options)); }
            foreach (MdfBusMessageType type in selected)
            {
                int index = (int)type;
                if (index < 0 || index >= BusFrameLayout.All.Length || BusFrameLayout.All[index].Bus != bus)
                { throw new ArgumentException("A " + type + " is not a frame of a " + bus + " log.", nameof(options)); }
            }
            return selected;
        }

        private static int Capacity(MdfBusType bus, MdfBusLoggerOptions options)
        {
            if (!Enum.IsDefined(typeof(MdfBusLoggerLayout), options.Layout)) { throw new ArgumentOutOfRangeException(nameof(options)); }
            if (options.Layout == MdfBusLoggerLayout.V230 && bus != MdfBusType.Can)
            { throw new NotSupportedException("The v2.3.0 bus layout covers the CAN frame groups only."); }
            int requested = options.MaxDataLength;
            int limit, fallback;
            switch (bus)
            {
                case MdfBusType.Can: limit = 64; fallback = 64; break;
                case MdfBusType.Lin: limit = 8; fallback = 8; break;
                case MdfBusType.Ethernet: limit = 65535; fallback = 1500; break;
                case MdfBusType.FlexRay: limit = 254; fallback = 254; break;
                case MdfBusType.Most: limit = 65535; fallback = 1524; break;
                default: throw new ArgumentOutOfRangeException(nameof(bus));
            }
            Selection(bus, options);
            if (bus == MdfBusType.Lin) { return requested == 0 || requested == 8 ? 8 : throw new ArgumentOutOfRangeException(nameof(options), "LIN frames always reserve 8 payload bytes."); }
            if (requested < 0 || requested > limit) { throw new ArgumentOutOfRangeException(nameof(options), "MaxDataLength must be 0 (default) or 1.." + limit + "."); }
            return requested == 0 ? fallback : requested;
        }

        private static MdfBusLogger Configure(MdfWriter writer, MdfBusType bus, int max, MdfBusLoggerOptions options)
        {
            try
            {
                BusFrameLayout[] table = options.Layout == MdfBusLoggerLayout.V230 ? BusFrameLayout.Release230 : BusFrameLayout.All;
                MdfBusLogger logger = new MdfBusLogger(writer, bus, max, table);
                List<MdfBusMessageType> selected = Selection(bus, options);
                // the v2.3.0 layout keeps every frame group in the data group of the first one, as the release does.
                bool shared = options.Layout == MdfBusLoggerLayout.V230;
                int first = -1;
                foreach (BusFrameLayout layout in table)
                {
                    if (layout.Bus != bus || (selected == null ? !layout.Primary : !selected.Contains(layout.Type))) { continue; }
                    int index = writer.AddRawGroup(layout.Group(max, options.NetworkName), shared ? first : -1);
                    if (first < 0) { first = index; }
                    logger.groups[(int)layout.Type] = index;
                }
                return logger;
            }
            catch
            {
                writer.Dispose();
                throw;
            }
        }

        /// <summary>Writes the metadata and starts accepting frames.</summary>
        public void StartMeasurement(CancellationToken cancellationToken = default(CancellationToken)) { writer.StartMeasurement(cancellationToken); }

        // for the bus log: the header is already written, so its start time is patched in place by the writer that owns it,
        // exactly as for an ordinary MDF 4 writer (the bus log has no MDF 3 form).
        internal void PatchStartTime(ulong nanoseconds) { writer.PatchStartTime(nanoseconds); }

        /// <summary>Validates and writes one frame; an invalid frame writes nothing.</summary>
        /// <exception cref="ArgumentException">The frame kind is not part of this bus, or a member is out of range.</exception>
        public void Write(MdfBusMessage message, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (message == null) { throw new ArgumentNullException(nameof(message)); }
            int type = (int)message.Type;
            if (type < 0 || type >= groups.Length || groups[type] < 0)
            { throw new ArgumentException("A " + message.Type + " is not a frame of a " + BusType + " log.", nameof(message)); }
            BusFrameLayout layout = layouts[type];
            writer.SaveRecord(groups[type], layout.Encode(message, maxLength), layout.Payload(message), cancellationToken);
        }

        /// <summary>Stops accepting frames.</summary>
        public void StopMeasurement(CancellationToken cancellationToken = default(CancellationToken)) { writer.StopMeasurement(cancellationToken); }
        /// <summary>Completes and publishes the file, as <see cref="MdfWriter.FinalizeMeasurement"/>.</summary>
        public void FinalizeMeasurement(CancellationToken cancellationToken = default(CancellationToken)) { writer.FinalizeMeasurement(cancellationToken); }
        /// <summary>Closes an owned destination; never finalizes an unfinished file.</summary>
        public void Dispose() { writer.Dispose(); }
    }

    /// <summary>Reads ASAM MDF 4 bus logging frames (every CAN, LIN, Ethernet, FlexRay and MOST frame kind of <see cref="MdfBusMessageType"/>),
    /// including files of other writers whose payloads use fixed, VLSD or MLSD storage that the reader supports.</summary>
    internal static class MdfBusReader
    {
        /// <summary>Enumerates the frames group by group in file order (each group in record order); groups are not merged by time.</summary>
        /// <remarks>A group is recognized by a top-level frame channel such as "CAN_DataFrame"; members are matched by the
        /// name after the frame name and '.', so absent or unreadable members stay zero (a payload stays null).
        /// The reader must stay open and idle (no other enumeration) while the sequence is enumerated.</remarks>
        public static IEnumerable<MdfBusMessage> ReadMessages(MdfReader reader, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (reader == null) { throw new ArgumentNullException(nameof(reader)); }
            return Read(reader, cancellationToken);
        }

        private static IEnumerable<MdfBusMessage> Read(MdfReader reader, CancellationToken token)
        {
            foreach (MdfDataGroup dataGroup in reader.ReadMetadata(token))
            {
                foreach (MdfChannelGroup group in dataGroup.ChannelGroups)
                {
                    if (!group.CanReadSamples) { continue; }
                    BusFrameLayout layout = null;
                    MdfChannel frame = null, master = null;
                    foreach (MdfChannel channel in group.Channels)
                    {
                        if (channel.Parent != null) { continue; }
                        if (channel.IsMaster && channel.CanReadPhysical && master == null) { master = channel; }
                        BusFrameLayout match = BusFrameLayout.Find(channel.Name);
                        if (match != null && layout == null && channel.Components.Count != 0) { layout = match; frame = channel; }
                    }
                    if (layout == null || master == null) { continue; }
                    List<int> selection = new List<int> { master.Index };
                    List<BusField> fields = new List<BusField> { BusField.Time };
                    List<string> suffixes = new List<string> { "t" };
                    foreach (MdfChannel member in Descendants(frame))
                    {
                        string name = member.Name ?? string.Empty;
                        if (!member.CanReadPhysical || member.Components.Count != 0) { continue; }
                        // Upstream writers emit a few misspelled members ("LIN_ChecksumError..DataBytes", "LIN_Transmission.ID").
                        string suffix = name.StartsWith(frame.Name + ".", StringComparison.Ordinal)
                            ? name.Substring(frame.Name.Length + 1).TrimStart('.') : name.Substring(name.LastIndexOf('.') + 1);
                        if (suffix.Length == 0 || suffixes.Contains(suffix)) { continue; }
                        BusField field = BusFrameLayout.Classify(layout, suffix);
                        if (field != BusField.Extra && field != BusField.DataBytes && fields.Contains(field)) { continue; }
                        if (field == BusField.DataBytes && fields.Contains(BusField.DataBytes)) { continue; }
                        selection.Add(member.Index);
                        fields.Add(field);
                        suffixes.Add(suffix);
                    }
                    foreach (MdfSampleBatch batch in reader.ReadSamples(group, selection, 0, ulong.MaxValue, 1024, MdfValueRepresentation.Physical, token))
                    {
                        for (int row = 0; row < batch.SampleCount; row++)
                        {
                            MdfBusMessage message = new MdfBusMessage { Type = layout.Type };
                            for (int column = 0; column < fields.Count; column++)
                            { BusFrameLayout.Decode(message, fields[column], suffixes[column], batch.GetValue(row, column)); }
                            if (layout.Bus == MdfBusType.Can && !fields.Contains(BusField.Dlc))
                            {
                                int length = message.Data == null ? 0 : message.Data.Length;
                                message.Dlc = (byte)Math.Max(0, MdfBusMessage.CanFdCode(length));
                            }
                            yield return message;
                        }
                    }
                }
            }
        }

        private static IEnumerable<MdfChannel> Descendants(MdfChannel root)
        {
            Stack<MdfChannel> stack = new Stack<MdfChannel>();
            for (int i = root.Components.Count - 1; i >= 0; i--) { stack.Push(root.Components[i]); }
            while (stack.Count != 0)
            {
                MdfChannel channel = stack.Pop();
                yield return channel;
                for (int i = channel.Components.Count - 1; i >= 0; i--) { stack.Push(channel.Components[i]); }
            }
        }
    }

    internal enum BusField
    {
        Time, Extra, BusChannel, Id, Ide, Dlc, DataLength, Dir, Edl, Brs, Esi, ErrorType, BitPosition, FrameDuration, Crc,
        ReceivedCount, Checksum, Source, Destination, EtherType, Cycle, FlxChannel, PayloadLength, DataBytes
    }

    internal enum BusValue { Unsigned, Signed, Float, Mac }

    internal sealed class BusFieldLayout
    {
        internal readonly string Suffix, Unit;
        internal readonly BusField Field;
        internal readonly BusValue Value;
        internal readonly int Byte, Bit, Bits;
        internal bool Code, Table; // Code stores the DLC code; Table maps it to the length.

        internal BusFieldLayout(string suffix, BusField field, int byteOffset, int bit, int bits, string unit, BusValue value)
        {
            Suffix = suffix; Field = field; Byte = byteOffset; Bit = bit; Bits = bits; Unit = unit; Value = value;
        }
    }

    internal sealed class BusFrameLayout
    {
        internal readonly MdfBusMessageType Type;
        internal readonly MdfBusType Bus;
        internal readonly string Name, DataName;
        internal readonly int DataOffset; // Record offset of the MLSD payload, or -1.
        internal readonly bool Primary; // Created by default.
        internal readonly BusFieldLayout[] Fields;
        internal bool Release; // the v2.3.0 CAN layout (payload after DataLength, source only with a network name).

        private BusFrameLayout(MdfBusMessageType type, MdfBusType bus, string name, bool primary, int dataOffset, string dataName, params BusFieldLayout[] fields)
        {
            Type = type; Bus = bus; Name = name; Primary = primary; DataOffset = dataOffset; DataName = dataName; Fields = fields;
        }

        private static BusFieldLayout F(string suffix, BusField field, int byteOffset, int bit, int bits, string unit = null)
        { return new BusFieldLayout(suffix, field, byteOffset, bit, bits, unit, BusValue.Unsigned); }
        private static BusFieldLayout X(string suffix, int byteOffset, int bit, int bits, string unit = null)
        { return new BusFieldLayout(suffix, BusField.Extra, byteOffset, bit, bits, unit, BusValue.Unsigned); }
        private static BusFieldLayout S(string suffix, int byteOffset, int bit, int bits)
        { return new BusFieldLayout(suffix, BusField.Extra, byteOffset, bit, bits, null, BusValue.Signed); }
        private static BusFieldLayout R(string suffix, int byteOffset) // float32 baud rate
        { return new BusFieldLayout(suffix, BusField.Extra, byteOffset, 0, 32, "bits/s", BusValue.Float); }
        private static BusFieldLayout M(string suffix, BusField field, int byteOffset)
        { return new BusFieldLayout(suffix, field, byteOffset, 0, 48, null, BusValue.Mac); }
        private static BusFieldLayout Ch() { return F("BusChannel", BusField.BusChannel, 8, 0, 8); }
        private static BusFieldLayout Ns(string suffix, int byteOffset, int bits = 32) { return X(suffix, byteOffset, 0, bits, "ns"); }

        // the release shares the DLC nibble of byte 12 between DLC and DataLength; DataLength maps the code to bytes.
        private static BusFieldLayout DlcLength(bool table)
        { return new BusFieldLayout("DataLength", BusField.DataLength, 12, 0, 4, null, BusValue.Unsigned) { Code = true, Table = table }; }

        private static BusFieldLayout ReleaseBus(int byteOffset)
        { return F("BusChannel", BusField.BusChannel, byteOffset, 4, 4); }

        private static BusFrameLayout V230(MdfBusMessageType type, string name, int dataOffset, params BusFieldLayout[] fields)
        {
            BusFrameLayout layout = new BusFrameLayout(type, MdfBusType.Can, name, true, dataOffset, dataOffset < 0 ? null : "DataBytes", fields);
            layout.Release = true;
            return layout;
        }

        private static BusFrameLayout L(MdfBusMessageType type, MdfBusType bus, string name, bool primary, int dataOffset, params BusFieldLayout[] fields)
        { return new BusFrameLayout(type, bus, name, primary, dataOffset, dataOffset < 0 ? null : "DataBytes", fields); }
        private static BusFrameLayout L(MdfBusMessageType type, MdfBusType bus, string name, int dataOffset, string dataName, params BusFieldLayout[] fields)
        { return new BusFrameLayout(type, bus, name, false, dataOffset, dataName, fields); }

        private static BusFieldLayout[] Lin(bool dirBit7)
        {
            return new[]
            {
                Ch(), F("ID", BusField.Id, 9, 0, 6), F("Dir", BusField.Dir, 9, dirBit7 ? 7 : 6, 1),
                F("ReceivedDataByteCount", BusField.ReceivedCount, 10, 0, 4, "B"), F("DataLength", BusField.DataLength, 10, 4, 4, "B"),
                F("Checksum", BusField.Checksum, 19, 0, 8), S("ChecksumModel", 20, 0, 2), Ns("SOF", 21, 64),
                R("Baudrate", 29), R("ResponseBaudrate", 33), Ns("BreakLength", 37), Ns("BreakDelimiterLength", 41)
            };
        }

        private static BusFieldLayout[] FlexRayHeader(string sizeName, int frameLengthByte, params BusFieldLayout[] extra)
        {
            List<BusFieldLayout> fields = new List<BusFieldLayout>
            {
                Ch(), F("ID", BusField.Id, 9, 0, 11), F("Cycle", BusField.Cycle, 11, 0, 6), F("FlxChannel", BusField.FlxChannel, 11, 6, 1),
                F("Dir", BusField.Dir, 11, 7, 1), F("PayloadLength", BusField.PayloadLength, 12, 0, 8, "word"),
                F(sizeName, BusField.DataLength, 13, 0, 8, "B"), F("CRC", BusField.Crc, 14, 0, 24), X("HeaderCRC", 17, 0, 16),
                X("ReservedBitFlag", 19, 0, 1), X("PayloadPreambleFlag", 19, 1, 1), X("NullFrameFlag", 19, 2, 1),
                X("SyncFrameFlag", 19, 3, 1), X("StartUpFrameFlag", 19, 4, 1), F("FrameLength", BusField.FrameDuration, frameLengthByte, 0, 32, "ns")
            };
            fields.AddRange(extra);
            return fields.ToArray();
        }

        private static BusFieldLayout[] MostHeader(int addressBits, params BusFieldLayout[] extra)
        {
            int wide = addressBits == 48 ? 6 : 2;
            List<BusFieldLayout> fields = new List<BusFieldLayout>
            {
                Ch(), X("Speed", 9, 0, 2), X("TransferType", 9, 2, 1), F("Dir", BusField.Dir, 9, 3, 1),
                F("Source", BusField.Source, 10, 0, addressBits), F("Destination", BusField.Destination, 10 + wide, 0, addressBits),
                X("SpecifiedDataByteCount", 10 + 2 * wide, 0, 16, "B"), F("ReceivedDataByteCount", BusField.ReceivedCount, 12 + 2 * wide, 0, 16, "B"),
                F("DataLength", BusField.DataLength, 14 + 2 * wide, 0, 16, "B")
            };
            fields.AddRange(extra);
            return fields.ToArray();
        }

        // Indexed by MdfBusMessageType. Byte.bit positions follow pinned upstream config adapters (all optional members).
        internal static readonly BusFrameLayout[] All =
        {
            L(MdfBusMessageType.CanDataFrame, MdfBusType.Can, "CAN_DataFrame", true, 24,
                Ch(), F("ID", BusField.Id, 9, 0, 29), F("IDE", BusField.Ide, 12, 7, 1),
                F("DLC", BusField.Dlc, 13, 0, 4), F("DataLength", BusField.DataLength, 14, 0, 8, "B"), F("Dir", BusField.Dir, 13, 4, 1),
                F("EDL", BusField.Edl, 13, 6, 1), F("BRS", BusField.Brs, 13, 7, 1), F("ESI", BusField.Esi, 15, 0, 1),
                F("FrameDuration", BusField.FrameDuration, 16, 0, 32, "ns"), F("CRC", BusField.Crc, 20, 0, 32),
                X("SRR", 13, 5, 1), X("WakeUp", 15, 1, 1), X("SingleWire", 15, 2, 1), X("R0", 15, 3, 1), X("R1", 15, 4, 1)),
            L(MdfBusMessageType.CanRemoteFrame, MdfBusType.Can, "CAN_RemoteFrame", true, -1,
                Ch(), F("ID", BusField.Id, 9, 0, 29), F("IDE", BusField.Ide, 12, 7, 1),
                F("DLC", BusField.Dlc, 13, 0, 4), F("DataLength", BusField.DataLength, 14, 0, 8, "B"), F("Dir", BusField.Dir, 13, 4, 1),
                F("FrameDuration", BusField.FrameDuration, 16, 0, 32, "ns"), F("CRC", BusField.Crc, 20, 0, 32),
                X("SRR", 13, 5, 1), X("WakeUp", 13, 6, 1), X("SingleWire", 13, 7, 1), X("R0", 15, 0, 1), X("R1", 15, 1, 1)),
            L(MdfBusMessageType.CanErrorFrame, MdfBusType.Can, "CAN_ErrorFrame", true, 26,
                Ch(), F("ID", BusField.Id, 9, 0, 29), F("IDE", BusField.Ide, 12, 7, 1),
                F("DLC", BusField.Dlc, 13, 0, 4), F("DataLength", BusField.DataLength, 14, 0, 8, "B"), F("Dir", BusField.Dir, 13, 4, 1),
                F("EDL", BusField.Edl, 13, 6, 1), F("BRS", BusField.Brs, 13, 7, 1), F("ESI", BusField.Esi, 15, 0, 1),
                F("ErrorType", BusField.ErrorType, 15, 5, 3), F("FrameDuration", BusField.FrameDuration, 16, 0, 32, "ns"),
                F("BitPosition", BusField.BitPosition, 20, 0, 16), F("CRC", BusField.Crc, 22, 0, 32),
                X("SRR", 13, 5, 1), X("WakeUp", 15, 1, 1), X("SingleWire", 15, 2, 1), X("R0", 15, 3, 1), X("R1", 15, 4, 1)),
            L(MdfBusMessageType.LinFrame, MdfBusType.Lin, "LIN_Frame", true, 11, Lin(false)),
            L(MdfBusMessageType.EthernetFrame, MdfBusType.Ethernet, "ETH_Frame", true, 34,
                Ch(), M("Source", BusField.Source, 9), M("Destination", BusField.Destination, 15),
                F("EtherType", BusField.EtherType, 21, 0, 16), F("ReceivedDataByteCount", BusField.ReceivedCount, 23, 0, 16, "B"),
                F("DataLength", BusField.DataLength, 25, 0, 16, "B"), F("CRC", BusField.Crc, 27, 0, 32), F("Dir", BusField.Dir, 31, 0, 1),
                X("PadByteCount", 32, 0, 16, "B")),
            L(MdfBusMessageType.FlexRayFrame, MdfBusType.FlexRay, "FLX_Frame", true, 24, FlexRayHeader("DataLength", 20)),
            L(MdfBusMessageType.CanOverloadFrame, MdfBusType.Can, "CAN_OverloadFrame", false, -1, Ch(), F("Dir", BusField.Dir, 9, 0, 1)),
            L(MdfBusMessageType.LinChecksumError, MdfBusType.Lin, "LIN_ChecksumError", false, 11, Lin(true)),
            L(MdfBusMessageType.LinReceiveError, MdfBusType.Lin, "LIN_ReceiveError", false, 12,
                Ch(), F("ID", BusField.Id, 9, 0, 6), S("ChecksumModel", 9, 6, 2), F("ReceivedDataByteCount", BusField.ReceivedCount, 10, 0, 4, "B"),
                X("SpecifiedDataByteCount", 10, 4, 4, "B"), F("DataLength", BusField.DataLength, 11, 0, 4, "B"), Ns("SOF", 20, 64),
                R("Baudrate", 28), R("ResponseBaudrate", 32), Ns("BreakLength", 36), Ns("BreakDelimiterLength", 40)),
            L(MdfBusMessageType.LinSyncError, MdfBusType.Lin, "LIN_SyncError", false, -1,
                Ch(), Ns("SOF", 9, 64), R("Baudrate", 17), Ns("BreakLength", 21), Ns("BreakDelimiterLength", 25)),
            L(MdfBusMessageType.LinWakeUp, MdfBusType.Lin, "LIN_WakeUp", false, -1, Ch(), R("Baudrate", 9), Ns("SOF", 13, 64)),
            L(MdfBusMessageType.LinTransmissionError, MdfBusType.Lin, "LIN_TransmissionError", false, -1,
                Ch(), F("ID", BusField.Id, 9, 0, 6), X("SpecifiedDataByteCount", 10, 0, 4, "B"), S("ChecksumModel", 10, 4, 2),
                Ns("SOF", 11, 64), R("Baudrate", 19), Ns("BreakLength", 23), Ns("BreakDelimiterLength", 27)),
            L(MdfBusMessageType.LinSpike, MdfBusType.Lin, "LIN_Spike", false, -1, Ch(), R("Baudrate", 9), Ns("SOF", 13, 64)),
            L(MdfBusMessageType.LinLongDominant, MdfBusType.Lin, "LIN_LongDom", false, -1,
                Ch(), R("Baudrate", 9), Ns("SOF", 13, 64), Ns("Length", 21), X("Type", 25, 0, 2)),
            L(MdfBusMessageType.EthernetChecksumError, MdfBusType.Ethernet, "ETH_ChecksumError", false, 38,
                Ch(), M("Source", BusField.Source, 9), M("Destination", BusField.Destination, 15),
                F("EtherType", BusField.EtherType, 21, 0, 16), F("DataLength", BusField.DataLength, 23, 0, 16, "B"),
                X("ReceivedCRC", 25, 0, 32), X("ExpectedCRC", 29, 0, 32), F("ReceivedDataByteCount", BusField.ReceivedCount, 33, 0, 16, "B"),
                F("Dir", BusField.Dir, 35, 0, 1), X("PadByteCount", 36, 0, 16, "B")),
            L(MdfBusMessageType.EthernetLengthError, MdfBusType.Ethernet, "ETH_LengthError", false, 34,
                Ch(), M("Source", BusField.Source, 9), M("Destination", BusField.Destination, 15),
                F("EtherType", BusField.EtherType, 21, 0, 16), F("ReceivedDataByteCount", BusField.ReceivedCount, 23, 0, 16, "B"),
                F("DataLength", BusField.DataLength, 25, 0, 16, "B"), F("CRC", BusField.Crc, 27, 0, 32), F("Dir", BusField.Dir, 31, 0, 1),
                X("PadByteCount", 32, 0, 16, "B")),
            L(MdfBusMessageType.EthernetReceiveError, MdfBusType.Ethernet, "ETH_ReceiveError", false, 34,
                Ch(), M("Source", BusField.Source, 9), M("Destination", BusField.Destination, 15),
                F("EtherType", BusField.EtherType, 21, 0, 16), F("ReceivedDataByteCount", BusField.ReceivedCount, 23, 0, 16, "B"),
                F("DataLength", BusField.DataLength, 25, 0, 16, "B"), F("CRC", BusField.Crc, 27, 0, 32), F("Dir", BusField.Dir, 31, 0, 1),
                F("ErrorType", BusField.ErrorType, 31, 1, 1), X("PadByteCount", 32, 0, 16, "B")),
            L(MdfBusMessageType.FlexRayPdu, MdfBusType.FlexRay, "FLX_Pdu", false, 20,
                Ch(), F("ID", BusField.Id, 9, 0, 11), F("Cycle", BusField.Cycle, 11, 0, 6), F("FlxChannel", BusField.FlxChannel, 11, 6, 1),
                X("PduLength", 12, 0, 8, "B"), F("DataLength", BusField.DataLength, 13, 0, 8, "B"), X("IsMultiplexed", 14, 0, 1),
                X("ValidFlag", 14, 1, 1), X("PduBitOffset", 14, 2, 3), X("Switch", 15, 0, 16), X("UpdateBitPos", 17, 0, 12),
                X("PduByteOffset", 19, 0, 8)),
            L(MdfBusMessageType.FlexRayFrameHeader, MdfBusType.FlexRay, "FLX_FrameHeader", 24, "FillDataBytes", FlexRayHeader("FillDataLength", 20)),
            L(MdfBusMessageType.FlexRayNullFrame, MdfBusType.FlexRay, "FLX_NullFrame", false, 24, FlexRayHeader("DataLength", 20)),
            L(MdfBusMessageType.FlexRayErrorFrame, MdfBusType.FlexRay, "FLX_ErrorFrame", false, 25, FlexRayHeader("DataLength", 21,
                X("IsSyntaxError", 19, 5, 1), X("IsContentError", 19, 6, 1), X("IsSlotBndViolation", 19, 7, 1),
                X("IsTxConflict", 20, 0, 1), X("IsValidFrame", 20, 1, 1))),
            L(MdfBusMessageType.FlexRaySymbol, MdfBusType.FlexRay, "FLX_Symbol", false, -1,
                Ch(), F("Cycle", BusField.Cycle, 9, 0, 6), X("FlxChannelMask", 9, 6, 2), X("SymbolLength", 10, 0, 8, "MT"), X("SymbolType", 11, 0, 2)),
            L(MdfBusMessageType.MostMessage, MdfBusType.Most, "MOST_Message", true, 27, MostHeader(16,
                F("CRC", BusField.Crc, 20, 0, 32), X("CAck", 24, 0, 3), X("PAck", 24, 3, 3), X("TxFailed", 24, 6, 1),
                X("AckNAck", 25, 0, 4), X("RType", 25, 4, 4), X("PIndex", 26, 0, 8))),
            L(MdfBusMessageType.MostPacket, MdfBusType.Most, "MOST_Packet", true, 27, MostHeader(16,
                F("CRC", BusField.Crc, 20, 0, 32), X("CAck", 24, 0, 3), X("PAck", 24, 3, 3), X("AckNAck", 25, 0, 4), X("PIndex", 26, 0, 8))),
            L(MdfBusMessageType.MostEthernetPacket, MdfBusType.Most, "MOST_EthernetPacket", true, 34, MostHeader(48,
                F("CRC", BusField.Crc, 28, 0, 32), X("CAck", 32, 0, 3), X("PAck", 32, 3, 3), X("AckNAck", 33, 0, 4))),
            L(MdfBusMessageType.MostSignalState, MdfBusType.Most, "MOST_SignalState", false, -1, Ch(), X("State", 9, 0, 8)),
            L(MdfBusMessageType.MostMaxPosInfo, MdfBusType.Most, "MOST_MaxPosInfo", false, -1, Ch(), X("DeviceCount", 9, 0, 8)),
            L(MdfBusMessageType.MostBoundaryDescriptor, MdfBusType.Most, "MOST_BoundDesc", false, -1, Ch(), X("SBC", 9, 0, 16)),
            L(MdfBusMessageType.MostAllocationTable, MdfBusType.Most, "MOST_AllocTable", 14, "TableData",
                Ch(), X("FreeByteCount", 9, 0, 16, "B"), X("TableLayout", 11, 0, 8), F("TableLength", BusField.DataLength, 12, 0, 16, "B")),
            L(MdfBusMessageType.MostSystemLockState, MdfBusType.Most, "MOST_SysLockState", false, -1, Ch(), X("SysLockState", 9, 0, 1)),
            L(MdfBusMessageType.MostShutdownFlag, MdfBusType.Most, "MOST_ShutdownFlag", false, -1, Ch(), X("ShutdownFlag", 9, 0, 1)),
        };

        // the CAN frame groups of the v2.3.0 release, from its CreateCan*Channel functions (mdfwriter.cpp). Byte
        // offsets are record offsets: the 8-byte time comes first, the frame byte array starts at byte 8. Order is the
        // release's channel order. FrameDuration, CRC, R0 and R1 do not exist in this layout.
        internal static readonly double[] DlcLengthTable = { 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 12, 10, 16, 11, 20, 12, 24, 13, 32, 14, 48, 15, 64 };

        internal static readonly BusFrameLayout[] Release230 = Overlay(All,
            V230(MdfBusMessageType.CanDataFrame, "CAN_DataFrame", 14,
                ReleaseBus(12), F("ID", BusField.Id, 8, 0, 29), F("IDE", BusField.Ide, 11, 7, 1),
                F("DLC", BusField.Dlc, 12, 0, 4), DlcLength(true),
                F("Dir", BusField.Dir, 13, 0, 1), X("SRR", 13, 1, 1), F("EDL", BusField.Edl, 13, 2, 1),
                F("BRS", BusField.Brs, 13, 3, 1), F("ESI", BusField.Esi, 13, 4, 1),
                X("WakeUp", 13, 5, 1), X("SingleWire", 13, 6, 1)),
            V230(MdfBusMessageType.CanRemoteFrame, "CAN_RemoteFrame", -1,
                ReleaseBus(12), F("ID", BusField.Id, 8, 0, 29), F("IDE", BusField.Ide, 11, 7, 1),
                F("DLC", BusField.Dlc, 12, 0, 4), DlcLength(true),
                F("Dir", BusField.Dir, 13, 0, 1), X("SRR", 13, 1, 1), X("WakeUp", 13, 5, 1), X("SingleWire", 13, 6, 1)),
            V230(MdfBusMessageType.CanErrorFrame, "CAN_ErrorFrame", 16,
                ReleaseBus(12), F("ID", BusField.Id, 8, 0, 29), F("IDE", BusField.Ide, 11, 7, 1),
                F("DLC", BusField.Dlc, 12, 0, 4), DlcLength(false), // the error frame's DataLength has no conversion
                F("Dir", BusField.Dir, 13, 0, 1), X("RTR", 13, 7, 1), X("SRR", 13, 1, 1), F("EDL", BusField.Edl, 13, 2, 1),
                F("BRS", BusField.Brs, 13, 3, 1), F("ESI", BusField.Esi, 13, 4, 1), X("WakeUp", 13, 5, 1),
                X("SingleWire", 13, 6, 1), F("BitPosition", BusField.BitPosition, 14, 0, 8), F("ErrorType", BusField.ErrorType, 15, 0, 8)),
            V230(MdfBusMessageType.CanOverloadFrame, "CAN_OverloadFrame", -1, ReleaseBus(8), F("Dir", BusField.Dir, 8, 0, 1)));

        private static BusFrameLayout[] Overlay(BusFrameLayout[] basis, params BusFrameLayout[] replacements)
        {
            BusFrameLayout[] table = (BusFrameLayout[])basis.Clone();
            foreach (BusFrameLayout layout in replacements) { table[(int)layout.Type] = layout; }
            return table;
        }

        internal static BusFrameLayout Find(string name)
        {
            foreach (BusFrameLayout layout in All) { if (layout.Name == name) { return layout; } }
            return null;
        }

        internal BusFieldLayout Member(string suffix)
        {
            foreach (BusFieldLayout field in Fields) { if (field.Suffix == suffix) { return field; } }
            return null;
        }

        // The member that sizes the MLSD payload: the DataLength-class member, else (MOST) ReceivedDataByteCount.
        internal BusFieldLayout SizeMember
        {
            get
            {
                if (DataOffset < 0) { return null; }
                BusFieldLayout received = null;
                foreach (BusFieldLayout field in Fields)
                {
                    if (field.Field == BusField.DataLength) { return field; }
                    if (field.Field == BusField.ReceivedCount && received == null) { received = field; }
                }
                return received;
            }
        }

        // the payload is not in the record; the record holds its 8-byte SD offset.
        internal bool Sd { get { return Release && DataOffset >= 0; } }

        internal byte[] Payload(MdfBusMessage message) { return Sd ? (message.Data ?? new byte[0]) : null; }

        internal int RecordBytes(int max)
        {
            int end = DataOffset < 0 ? 0 : DataOffset + (Release ? 8 : max);
            foreach (BusFieldLayout field in Fields) { end = Math.Max(end, field.Byte + (field.Bit + field.Bits + 7) / 8); }
            return end;
        }

        internal RawGroup Group(int max, string network)
        {
            int record = RecordBytes(max);
            // The v2.3.0 layout names a source only when a network name is given; the extended layout always does.
            bool source = !Release || !string.IsNullOrEmpty(network);
            RawGroup group = new RawGroup
            {
                Name = Name, Flags = 6, PathSeparator = '.', DataBytes = record,
                SourceName = !source ? null : string.IsNullOrEmpty(network) ? Bus.ToString().ToUpperInvariant() : network,
                SourcePath = source ? Name : null,
                SourceType = 2, BusType = (byte)Bus
            };
            group.Channels.Add(new RawChannel { Name = "t", Unit = "s", Kind = MdfValueKind.FloatingPoint, Bits = 64, ByteOffset = 0, Type = 2 });
            group.Channels.Add(new RawChannel { Name = Name, Kind = MdfValueKind.ByteArray, Bits = 8 * (record - 8), ByteOffset = 8, Flags = 0x400 });
            int size = -1;
            foreach (BusFieldLayout field in Fields)
            {
                if (field == SizeMember) { size = group.Channels.Count; }
                MdfValueKind kind = field.Value == BusValue.Signed ? MdfValueKind.SignedInteger : field.Value == BusValue.Float ? MdfValueKind.FloatingPoint
                    : field.Value == BusValue.Mac ? MdfValueKind.ByteArray : MdfValueKind.UnsignedInteger;
                group.Channels.Add(new RawChannel
                {
                    Name = Name + "." + field.Suffix, Unit = field.Unit, Kind = kind, Bits = field.Bits,
                    ByteOffset = field.Byte, BitOffset = field.Bit, Parent = 1, Flags = 0x400,
                    Table = field.Table ? DlcLengthTable : null
                });
                // The v2.3.0 layout stores the payload right behind its size member.
                if (Release && field == SizeMember && DataOffset >= 0) { group.Channels.Add(DataChannel(max, size)); }
            }
            if (DataOffset >= 0 && (!Release || size < 0)) { group.Channels.Add(DataChannel(max, size)); }
            return group;
        }

        private RawChannel DataChannel(int max, int size)
        {
            if (Release)
            {
                // The v2.3.0 DataBytes channel: VariableLength (cn_type 1), 64 bits, a link to the SD block, no size member.
                return new RawChannel { Name = Name + "." + DataName, Kind = MdfValueKind.ByteArray, Bits = 64, ByteOffset = DataOffset, Type = 1, Parent = 1, Sd = true, Flags = 0x400 };
            }
            return new RawChannel
            {
                Name = Name + "." + DataName, Kind = MdfValueKind.ByteArray, Bits = 8 * max, ByteOffset = DataOffset, Type = 5,
                Parent = 1, Size = size, Flags = 0x400
            };
        }

        internal byte[] Encode(MdfBusMessage message, int max)
        {
            if (double.IsNaN(message.Timestamp) || double.IsInfinity(message.Timestamp))
            { throw new ArgumentException("The timestamp must be finite.", nameof(message)); }
            byte[] data = message.Data ?? new byte[0];
            int length = data.Length;
            int dlc = 0;
            switch (Type)
            {
                case MdfBusMessageType.CanDataFrame:
                case MdfBusMessageType.CanErrorFrame:
                case MdfBusMessageType.CanRemoteFrame:
                    if (message.Id > (message.IsExtendedId ? 0x1FFFFFFFU : 0x7FFU)) { throw Range("Id", "The CAN identifier exceeds its 11/29-bit range."); }
                    if (Release)
                    {
                        // the release stores the DLC as given (its low nibble) and the payload as given, with
                        // no check against the length; a remote frame has no payload and the FD and BRS/ESI bits are stored as they are.
                        dlc = message.Dlc & 0x0F;
                        if (Type == MdfBusMessageType.CanRemoteFrame) { length = 0; }
                    }
                    else if (Type == MdfBusMessageType.CanRemoteFrame)
                    {
                        if (!message.IsFd && (message.BitRateSwitch || message.ErrorStateIndicator)) { throw Range("BitRateSwitch", "BRS and ESI require a CAN FD frame."); }
                        if (length != 0 || message.IsFd) { throw new ArgumentException("A remote frame has no payload and no FD format.", nameof(message)); }
                        if (message.Dlc > 15) { throw Range("Dlc", "A data length code is 0..15."); }
                        dlc = message.Dlc;
                        length = Math.Min(dlc, 8);
                    }
                    else
                    {
                        if (!message.IsFd && (message.BitRateSwitch || message.ErrorStateIndicator)) { throw Range("BitRateSwitch", "BRS and ESI require a CAN FD frame."); }
                        dlc = message.IsFd ? MdfBusMessage.CanFdCode(length) : length <= 8 ? length : -1;
                        if (dlc < 0) { throw Range("Data", message.IsFd ? "A CAN FD payload has 0..8, 12, 16, 20, 24, 32, 48 or 64 bytes." : "A classic CAN payload has at most 8 bytes."); }
                    }
                    if (message.ErrorType > 7) { throw Range("ErrorType", "The error type is 0..7."); }
                    break;
                case MdfBusMessageType.LinFrame:
                    if (message.Id > 63) { throw Range("Id", "The LIN identifier is 0..63."); }
                    break;
                case MdfBusMessageType.FlexRayFrame:
                    if (message.Id > 2047) { throw Range("Id", "The FlexRay slot identifier is 0..2047."); }
                    if (message.Cycle > 63) { throw Range("Cycle", "The FlexRay cycle is 0..63."); }
                    if (message.FlexRayChannel > 1) { throw Range("FlexRayChannel", "The FlexRay channel is 0 (A) or 1 (B)."); }
                    if (message.Crc > 0xFFFFFF) { throw Range("Crc", "The FlexRay frame CRC has 24 bits."); }
                    break;
                case MdfBusMessageType.EthernetFrame:
                    if (message.Source > 0xFFFFFFFFFFFFUL || message.Destination > 0xFFFFFFFFFFFFUL) { throw Range("Source", "MAC addresses have 48 bits."); }
                    break;
            }
            if (!Release && DataOffset < 0 && Type != MdfBusMessageType.CanRemoteFrame && length != 0)
            { throw new ArgumentException("A " + Name + " carries no payload.", nameof(message)); }
            if (DataOffset >= 0 && !Release && data.Length > max) { throw Range("Data", "The payload exceeds MaxDataLength (" + max + ")."); }
            foreach (string key in message.Fields.Keys)
            {
                BusFieldLayout member = Member(key);
                if (member == null || member.Field == BusField.DataLength || member == SizeMember)
                { throw new ArgumentException("MdfBusMessage.Fields: \"" + key + "\" is not a writable member of " + Name + ".", nameof(message)); }
            }
            byte[] record = new byte[RecordBytes(max)];
            Array.Copy(BitConverter.GetBytes(message.Timestamp), 0, record, 0, 8);
            if (!BitConverter.IsLittleEndian) { Array.Reverse(record, 0, 8); }
            foreach (BusFieldLayout field in Fields)
            {
                MdfSampleValue extra;
                ulong raw = message.Fields.TryGetValue(field.Suffix, out extra) ? Raw(field, extra) : field.Code ? (ulong)dlc : Value(message, field.Field, length, dlc);
                if (field.Value == BusValue.Unsigned && field.Bits < 64 && (raw >> field.Bits) != 0)
                { throw Range(field.Suffix, "The value exceeds the " + field.Bits + "-bit member " + Name + "." + field.Suffix + "."); }
                if (field.Value == BusValue.Mac)
                {
                    if (raw > 0xFFFFFFFFFFFFUL) { throw Range(field.Suffix, "MAC addresses have 48 bits."); }
                    for (int i = 0; i < 6; i++) { record[field.Byte + i] = (byte)(raw >> (8 * (5 - i))); }
                    continue;
                }
                Put(record, field, raw);
            }
            if (DataOffset >= 0 && !Release) { Buffer.BlockCopy(data, 0, record, DataOffset, data.Length); }
            return record;
        }

        private ulong Raw(BusFieldLayout field, MdfSampleValue value)
        {
            if (!value.IsValid) { throw Range(field.Suffix, "Invalid values cannot be written."); }
            switch (field.Value)
            {
                case BusValue.Float:
                    double number = value.Kind == MdfValueKind.FloatingPoint ? value.FloatingPointValue
                        : value.Kind == MdfValueKind.UnsignedInteger ? value.UnsignedValue
                        : value.Kind == MdfValueKind.SignedInteger ? value.SignedValue : double.NaN;
                    if (double.IsNaN(number) && value.Kind != MdfValueKind.FloatingPoint) { throw Range(field.Suffix, "A numeric value is required."); }
                    if (!double.IsNaN(number) && !double.IsInfinity(number) && Math.Abs(number) > float.MaxValue) { throw Range(field.Suffix, "The value exceeds float32."); }
                    return (uint)BitConverter.ToInt32(BitConverter.GetBytes((float)number), 0);
                case BusValue.Signed:
                    long signed;
                    if (value.Kind == MdfValueKind.SignedInteger) { signed = value.SignedValue; }
                    else if (value.Kind == MdfValueKind.UnsignedInteger && value.UnsignedValue <= long.MaxValue) { signed = (long)value.UnsignedValue; }
                    else { throw Range(field.Suffix, "An integer value is required."); }
                    long low = -(1L << (field.Bits - 1)), high = (1L << (field.Bits - 1)) - 1;
                    if (signed < low || signed > high) { throw Range(field.Suffix, "The value exceeds the signed " + field.Bits + "-bit member."); }
                    return unchecked((ulong)signed) & (field.Bits == 64 ? ulong.MaxValue : (1UL << field.Bits) - 1);
                case BusValue.Mac:
                    if (value.Kind == MdfValueKind.ByteArray)
                    {
                        byte[] bytes = value.GetByteArrayValue();
                        if (bytes.Length != 6) { throw Range(field.Suffix, "A MAC address has 6 bytes."); }
                        ulong mac = 0;
                        foreach (byte b in bytes) { mac = (mac << 8) | b; }
                        return mac;
                    }
                    goto default;
                default:
                    if (value.Kind == MdfValueKind.UnsignedInteger) { return value.UnsignedValue; }
                    if (value.Kind == MdfValueKind.SignedInteger && value.SignedValue >= 0) { return (ulong)value.SignedValue; }
                    throw Range(field.Suffix, "A non-negative integer value is required.");
            }
        }

        private static ArgumentOutOfRangeException Range(string member, string text) { return new ArgumentOutOfRangeException("message", "MdfBusMessage." + member + ": " + text); }

        private static ulong Value(MdfBusMessage m, BusField field, int length, int dlc)
        {
            switch (field)
            {
                case BusField.BusChannel: return m.BusChannel;
                case BusField.Id: return m.Id;
                case BusField.Ide: return m.IsExtendedId ? 1UL : 0;
                case BusField.Dlc: return (ulong)dlc;
                case BusField.DataLength:
                case BusField.ReceivedCount: return (ulong)length;
                case BusField.Dir: return (ulong)m.Direction & 1;
                case BusField.Edl: return m.IsFd ? 1UL : 0;
                case BusField.Brs: return m.BitRateSwitch ? 1UL : 0;
                case BusField.Esi: return m.ErrorStateIndicator ? 1UL : 0;
                case BusField.ErrorType: return m.ErrorType;
                case BusField.BitPosition: return m.BitPosition;
                case BusField.FrameDuration: return m.FrameDuration;
                case BusField.Crc: return m.Crc;
                case BusField.Checksum: return m.Checksum;
                case BusField.Source: return m.Source;
                case BusField.Destination: return m.Destination;
                case BusField.EtherType: return m.EtherType;
                case BusField.Cycle: return m.Cycle;
                case BusField.FlxChannel: return m.FlexRayChannel;
                case BusField.PayloadLength: return (ulong)(length + 1) / 2;
                default: return 0;
            }
        }

        private static void Put(byte[] record, BusFieldLayout field, ulong value)
        {
            for (int i = 0; i < field.Bits; i++)
            {
                if (((value >> i) & 1) == 0) { continue; }
                int bit = field.Bit + i;
                record[field.Byte + bit / 8] |= (byte)(1 << (bit % 8));
            }
        }

        internal static BusField Classify(BusFrameLayout layout, string suffix)
        {
            if (suffix == layout.DataName || suffix == "DataBytes" || suffix == "FillDataBytes" || suffix == "TableData") { return BusField.DataBytes; }
            BusFieldLayout member = layout.Member(suffix);
            if (member != null) { return member.Field; }
            foreach (BusFrameLayout other in All)
            {
                BusFieldLayout known = other.Member(suffix);
                if (known != null && known.Field != BusField.Extra) { return known.Field; }
            }
            return BusField.Extra;
        }

        internal static void Decode(MdfBusMessage m, BusField field, string suffix, MdfSampleValue value)
        {
            if (field == BusField.DataBytes)
            {
                if (value.Kind == MdfValueKind.ByteArray) { m.Data = value.GetByteArrayValue(); }
                return;
            }
            if (field == BusField.Extra || field == BusField.ReceivedCount) { m.Fields[suffix] = value; return; }
            if (field == BusField.DataLength) { return; }
            double number;
            ulong raw;
            switch (value.Kind)
            {
                case MdfValueKind.UnsignedInteger: raw = value.UnsignedValue; number = raw; break;
                case MdfValueKind.SignedInteger: number = value.SignedValue; raw = unchecked((ulong)value.SignedValue); break;
                case MdfValueKind.FloatingPoint:
                    number = value.FloatingPointValue;
                    raw = number >= 0 && number < 18446744073709551616.0 ? (ulong)number : 0;
                    break;
                case MdfValueKind.ByteArray: // MAC addresses: most significant byte first.
                    byte[] bytes = value.GetByteArrayValue();
                    raw = 0;
                    for (int i = 0; i < bytes.Length && i < 8; i++) { raw = (raw << 8) | bytes[i]; }
                    number = raw;
                    break;
                default: return;
            }
            switch (field)
            {
                case BusField.Time: m.Timestamp = number; break;
                case BusField.BusChannel: m.BusChannel = (byte)raw; break;
                case BusField.Id: m.Id = (uint)raw; break;
                case BusField.Ide: m.IsExtendedId = raw != 0; break;
                case BusField.Dlc: m.Dlc = (byte)raw; break;
                case BusField.Dir: m.Direction = raw != 0 ? MdfBusDirection.Tx : MdfBusDirection.Rx; break;
                case BusField.Edl: m.IsFd = raw != 0; break;
                case BusField.Brs: m.BitRateSwitch = raw != 0; break;
                case BusField.Esi: m.ErrorStateIndicator = raw != 0; break;
                case BusField.ErrorType: m.ErrorType = (byte)raw; break;
                case BusField.BitPosition: m.BitPosition = (ushort)raw; break;
                case BusField.FrameDuration: m.FrameDuration = (uint)raw; break;
                case BusField.Crc: m.Crc = (uint)raw; break;
                case BusField.Checksum: m.Checksum = (byte)raw; break;
                case BusField.Source: m.Source = raw; break;
                case BusField.Destination: m.Destination = raw; break;
                case BusField.EtherType: m.EtherType = (ushort)raw; break;
                case BusField.Cycle: m.Cycle = (byte)raw; break;
                case BusField.FlxChannel: m.FlexRayChannel = (byte)raw; break;
            }
        }
    }
}
