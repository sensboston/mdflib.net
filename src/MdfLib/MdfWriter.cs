// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using MdfLib.Writing;

namespace MdfLib
{
    /// <summary>Synchronously writes a new, bounded MDF 3.30/4.10/4.20 measurement of numeric, complex (MDF 4)
    /// and fixed-length text/byte-array channels; MDF 4 record data can be zlib-compressed (DZ/DL, managed encoder).</summary>
    /// <remarks>One sorted DG per CG; MDF 4 can hold several channel groups (see <see cref="MdfLib.MdfWriter.AddChannelGroup(MdfLib.MdfChannelGroupDefinition)"/>).
    /// Append is explicit (<see cref="Append"/>); records can be buffered (<see cref="MdfWriterOptions.WriteBufferBytes"/>) and
    /// queued to a worker thread (<see cref="MdfBackgroundWriter"/>). No implicit timestamps or automatic finalization.
    /// A writer/destination is not thread-safe; parallel/reentrant operations are refused.</remarks>
    internal sealed class MdfWriter : IDisposable
    {
        private readonly Stream stream;
        private readonly bool leaveOpen;
        private readonly WriterSettings settings;
        private readonly List<WriteGroup> groups = new List<WriteGroup> { new WriteGroup() };
        private readonly byte[] patch = new byte[8];
        private readonly byte[] zeros = new byte[8];
        private WriterPlan plan;
        private ulong metadataAllowance = 1024;
        private int activeOperation, state, channelCount;
        private bool blocked; // records go to per-group DT/DZ blocks instead of one growing DT.
        private long expectedLength;
        private byte[] writeBuffer; // pending contiguous records (single-group uncompressed output only).
        private int bufferLength;
        private long appendLink = -1; // absolute offset of the null DG link that publishes appended groups.
        // attachments/events written behind the data at finalization; append chains them after the existing ones.
        private readonly List<WriteAttachment> attachments = new List<WriteAttachment>();
        private readonly List<WriteEvent> events = new List<WriteEvent>();
        private long appendAttachmentLink = 64 + 24 + 8 * 3, appendEventLink = 64 + 24 + 8 * 4;

        private MdfWriter(Stream stream, bool leaveOpen, WriterSettings settings)
        {
            this.stream = stream;
            this.leaveOpen = leaveOpen;
            this.settings = settings;
        }

        /// <summary>Gets the explicit lifecycle state, including after disposal.</summary>
        public MdfWriterState State { get { return (MdfWriterState)Volatile.Read(ref state); } }
        /// <summary>Gets the number of completely accepted raw records of all channel groups, not a durability guarantee.</summary>
        public ulong SampleCount { get; private set; }
        /// <summary>Gets the number of snapshotted channel definitions of all channel groups.</summary>
        public int ChannelCount { get { return channelCount; } }
        /// <summary>Gets the single-record byte count of channel group 0, including MDF 4 invalidation.</summary>
        public int RecordByteCount { get { return groups[0].RecordBytes; } }
        /// <summary>Gets the number of channel groups; group 0 always exists.</summary>
        public int ChannelGroupCount { get { return groups.Count; } }
        /// <summary>Gets the exact output revision snapshotted at creation.</summary>
        public string Version { get { return settings.Version; } }

        /// <summary>Creates a writer over a writable, seekable, empty destination without writing any bytes.</summary>
        /// <remarks>Invalid arguments/settings/capabilities do not transfer ownership. Never truncates a destination.
        /// Offsets start at zero; caller must not change stream contents while this writer is active.</remarks>
        public static MdfWriter Create(Stream stream, bool leaveOpen = false, MdfWriterOptions options = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            WriterSettings settings = new WriterSettings(options);
            cancellationToken.ThrowIfCancellationRequested();
            if (stream == null) { throw new ArgumentNullException(nameof(stream)); }
            if (!stream.CanWrite || !stream.CanSeek) { throw new ArgumentException("The destination must be writable and seekable.", nameof(stream)); }
            if (stream.Length != 0) { throw new ArgumentException("The destination must be empty; existing data is never truncated.", nameof(stream)); }
            cancellationToken.ThrowIfCancellationRequested();
            return new MdfWriter(stream, leaveOpen, settings);
        }

        /// <summary>Opens a finalized MDF 4.10/4.20 destination for appending new channel groups.</summary>
        /// <remarks>The destination must be readable, writable and seekable; existing bytes are never modified until
        /// finalization patches the last data group's null next link, so an interrupted append leaves the original file valid.
        /// The file revision is used when <paramref name="options"/> is null; otherwise its Version must match the file.
        /// The start time, header metadata and file-history options are ignored: the existing header is kept.</remarks>
        public static MdfWriter Append(Stream stream, bool leaveOpen = false, MdfWriterOptions options = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stream == null) { throw new ArgumentNullException(nameof(stream)); }
            if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek)
            { throw new ArgumentException("The destination must be readable, writable and seekable.", nameof(stream)); }
            long length = stream.Length;
            byte[] id = new byte[64];
            if (length < 168) { throw new MdfFormatException("The destination is not a finalized MDF 4 file.", "append-short", 0); }
            ReadExact(stream, 0, id, 64);
            string magic = System.Text.Encoding.ASCII.GetString(id, 0, 8);
            string version = System.Text.Encoding.ASCII.GetString(id, 8, 8).TrimEnd(' ', '\0');
            if (magic == "UnFinMF ") { throw new NotSupportedException("Unfinalized files must be recovered with MdfRecovery before appending."); }
            if (magic != "MDF     ") { throw new MdfFormatException("The destination is not an MDF file.", "append-id", 0); }
            if (version != "4.10" && version != "4.20") { throw new NotSupportedException("Appending supports only MDF 4.10 and 4.20 files."); }
            if (options == null) { options = new MdfWriterOptions { Version = version }; }
            else if (options.Version != version) { throw new ArgumentException("The writer Version must match the file revision " + version + ".", nameof(options)); }
            WriterSettings settings = new WriterSettings(options);
            byte[] block = new byte[32];
            byte[] header = new byte[72];
            ReadExact(stream, 64, header, 72);
            if (System.Text.Encoding.ASCII.GetString(header, 0, 4) != "##HD") { throw new MdfFormatException("The header block is missing.", "append-hd", 64, "HD"); }
            long attachmentLink = ChainEnd(stream, length, Le64(header, 24 + 8 * 3), 64 + 24 + 8 * 3, "AT", cancellationToken);
            long eventLink = ChainEnd(stream, length, Le64(header, 24 + 8 * 4), 64 + 24 + 8 * 4, "EV", cancellationToken);
            long link = 64 + 24;
            ulong next = Le64(header, 24);
            HashSet<ulong> seen = new HashSet<ulong>();
            while (next != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (next > (ulong)(length - 32) || !seen.Add(next))
                { throw new MdfFormatException("The data group chain is out of range or cyclic.", "append-dg-chain", next, "DG"); }
                ReadExact(stream, (long)next, block, 32);
                if (System.Text.Encoding.ASCII.GetString(block, 0, 4) != "##DG")
                { throw new MdfFormatException("A data group link does not reference a DG block.", "append-dg", next, "DG"); }
                link = (long)next + 24;
                next = Le64(block, 24);
            }
            MdfWriter writer = new MdfWriter(stream, leaveOpen, settings);
            writer.appendLink = link;
            writer.appendAttachmentLink = attachmentLink;
            writer.appendEventLink = eventLink;
            writer.expectedLength = length;
            return writer;
        }

        // the absolute offset of the null next link that ends an existing AT or EV chain (or the HD link itself).
        private static long ChainEnd(Stream stream, long length, ulong next, long link, string type, CancellationToken token)
        {
            byte[] block = new byte[32];
            HashSet<ulong> seen = new HashSet<ulong>();
            while (next != 0)
            {
                token.ThrowIfCancellationRequested();
                if (next > (ulong)(length - 32) || !seen.Add(next))
                { throw new MdfFormatException("The " + type + " chain is out of range or cyclic.", "append-" + type.ToLowerInvariant() + "-chain", next, type); }
                ReadExact(stream, (long)next, block, 32);
                if (System.Text.Encoding.ASCII.GetString(block, 0, 4) != "##" + type || Le64(block, 16) < 1)
                { throw new MdfFormatException("A chain link does not reference a " + type + " block.", "append-" + type.ToLowerInvariant(), next, type); }
                link = (long)next + 24;
                next = Le64(block, 24);
            }
            return link;
        }

        /// <summary>Adds an embedded or external attachment (MDF 4 AT block) and returns its index for event references.</summary>
        /// <remarks>Allowed until finalization. The data is copied (and hashed/compressed) now and charged twice against
        /// <see cref="MdfWriterOptions.MaximumMetadataBytes"/>; attachments are written behind the data at finalization, so an
        /// interrupted recording has none. Appending chains them after the attachments of the existing file.</remarks>
        /// <exception cref="NotSupportedException">The output is MDF 3.30.</exception>
        public int AddAttachment(MdfAttachmentDefinition attachment)
        {
            Enter(CancellationToken.None);
            try
            {
                RequireOpen();
                if (settings.Mdf3) { throw new NotSupportedException("MDF 3.30 has no attachments."); }
                if (attachments.Count >= ushort.MaxValue) { throw new NotSupportedException("At most 65535 attachments are written."); }
                ulong charge;
                WriteAttachment snapshot = WriteAttachment.Create(attachment, settings, metadataAllowance, () => Checkpoint(CancellationToken.None), out charge);
                attachments.Add(snapshot);
                metadataAllowance += charge;
                return attachments.Count - 1;
            }
            finally { Exit(); }
        }

        /// <summary>Adds an event (MDF 4 EV block), for example a marker at a time, and returns its index.</summary>
        /// <remarks>Allowed until finalization, including while recording; events are written behind the data at
        /// finalization. Scope and attachment indices refer to this writer's channel groups and attachments; parent and range
        /// indices to earlier events.</remarks>
        /// <exception cref="NotSupportedException">The output is MDF 3.30, or a group name is given for MDF 4.10.</exception>
        public int AddEvent(MdfEventDefinition definition)
        {
            Enter(CancellationToken.None);
            try
            {
                RequireOpen();
                if (settings.Mdf3) { throw new NotSupportedException("MDF 3.30 has no events."); }
                ulong charge;
                WriteEvent snapshot = WriteEvent.Create(definition, settings, events.Count, groups.Count, attachments.Count, metadataAllowance, out charge);
                events.Add(snapshot);
                metadataAllowance += charge;
                return events.Count - 1;
            }
            finally { Exit(); }
        }

        /// <summary>Gets the number of attachments added to this writer.</summary>
        public int AttachmentCount { get { return attachments.Count; } }
        /// <summary>Gets the number of events added to this writer.</summary>
        public int EventCount { get { return events.Count; } }

        private void RequireOpen()
        {
            MdfWriterState current = State;
            if (current != MdfWriterState.Configuring && current != MdfWriterState.Recording && current != MdfWriterState.Stopped)
            { throw new InvalidOperationException("Attachments and events can be added only before finalization, not " + current + "."); }
        }

        private static ulong Le64(byte[] bytes, int offset)
        {
            ulong value = 0;
            for (int i = 7; i >= 0; i--) { value = (value << 8) | bytes[offset + i]; }
            return value;
        }

        private static void ReadExact(Stream stream, long offset, byte[] buffer, int count)
        {
            stream.Seek(offset, SeekOrigin.Begin);
            int done = 0;
            while (done < count)
            {
                int read = stream.Read(buffer, done, count - done);
                if (read <= 0) { throw new MdfFormatException("Unexpected end of the destination.", "append-eof", (ulong)offset); }
                done += read;
            }
        }

        /// <summary>Creates a new file with CreateNew semantics; never overwrites or appends an existing file.</summary>
        /// <remarks>Options/cancellation are checked before file creation. On later failure an empty/partial new file may remain.</remarks>
        public static MdfWriter Create(string path, MdfWriterOptions options = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            WriterSettings settings = new WriterSettings(options);
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path)) { throw new ArgumentException("A destination path is required.", nameof(path)); }
            FileStream output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            try { cancellationToken.ThrowIfCancellationRequested(); return new MdfWriter(output, false, settings); }
            catch
            {
                try { output.Dispose(); }
                catch (Exception) { }
                throw;
            }
        }

        /// <summary>Copies a channel definition into channel group 0 and returns its zero-based index; only permitted before StartMeasurement.</summary>
        public int AddChannel(MdfChannelDefinition channel) { return AddChannel(0, channel); }

        /// <summary>Copies a channel definition into a channel group and returns its zero-based index within that group.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="groupIndex"/> is not an existing group.</exception>
        public int AddChannel(int groupIndex, MdfChannelDefinition channel)
        {
            Enter(CancellationToken.None);
            try
            {
                Require(MdfWriterState.Configuring);
                WriteGroup group = Group(groupIndex);
                if (channel == null) { throw new ArgumentNullException(nameof(channel)); }
                if (group.Raw) { throw new InvalidOperationException("A bus logging channel group has a fixed layout."); }
                WriterSettings.Limit(nameof(MdfWriterOptions.MaximumChannelCount), (ulong)settings.MaximumChannels, (ulong)channelCount + 1);
                if (settings.Mdf3 && group.Channels.Count >= ushort.MaxValue) { throw new NotSupportedException("MDF 3 channel count exceeds UInt16."); }
                if (channel.IsMaster && group.HasMaster) { throw new ArgumentException("Only one stored master is allowed per channel group.", nameof(channel)); }
                ulong charge;
                WriteChannel snapshot = WriteChannel.Create(channel, settings, group.DataBytes, metadataAllowance, out charge);
                if (snapshot.Vlsd)
                {
                    if (group.Owner.Members > 1) { throw new NotSupportedException("A data group shared by channel groups cannot hold variable length channels (its record IDs are taken)."); }
                    if (group.VlsdChannels.Count >= 254) { throw new NotSupportedException("A channel group holds at most 254 VLSD channels (one-byte record IDs)."); }
                    charge += 104 + 64;
                    settings.Metadata(checked(metadataAllowance + charge));
                }
                // an array stores Elements fields of Stride bytes and one invalidation bit per element.
                long nextData = (long)group.DataBytes + (long)snapshot.Stride * snapshot.Elements;
                long bitsAfter = (long)group.InvalidationBits + snapshot.Elements;
                long nextRecord = nextData + (settings.Mdf3 ? 0 : (bitsAfter + 7) / 8);
                WriterSettings.Limit(nameof(MdfWriterOptions.MaximumRecordBytes), (ulong)settings.MaximumRecord, (ulong)nextRecord);
                if (settings.Mdf3 && nextData > ushort.MaxValue) { throw new NotSupportedException("MDF 3 records cannot exceed 65535 bytes."); }
                int index = group.Channels.Count;
                group.Channels.Add(snapshot);
                channelCount++;
                metadataAllowance += charge;
                group.DataBytes = checked((int)nextData);
                group.RecordBytes = checked((int)nextRecord);
                group.HasMaster |= snapshot.Master;
                if (snapshot.Vlsd) { group.VlsdChannels.Add(snapshot); }
                snapshot.InvalidationBit = group.InvalidationBits;
                group.InvalidationBits = checked((int)bitsAfter);
                group.ValueCount = checked(group.ValueCount + snapshot.Elements);
                return index;
            }
            finally { Exit(); }
        }

        /// <summary>Adds a structure channel (MDF 4) that reserves windowBytes of the record for its components and stores
        /// no value of its own, so SaveSample takes no value for it. Add its components with AddComponentChannel.</summary>
        /// <exception cref="NotSupportedException">MDF 3.30 output: it has no channel composition.</exception>
        public int AddStructureChannel(int groupIndex, MdfChannelDefinition channel, int windowBytes)
        {
            Enter(CancellationToken.None);
            try
            {
                Require(MdfWriterState.Configuring);
                WriteGroup group = Group(groupIndex);
                if (channel == null) { throw new ArgumentNullException(nameof(channel)); }
                if (group.Raw) { throw new InvalidOperationException("A bus logging channel group has a fixed layout."); }
                if (settings.Mdf3) { throw new NotSupportedException("MDF 3.30 has no channel composition."); }
                if (windowBytes < 1) { throw new ArgumentOutOfRangeException(nameof(windowBytes), "A structure needs at least one byte."); }
                if (channel.IsMaster || channel.IsVariableLength) { throw new ArgumentException("A structure channel is fixed length and cannot be the master.", nameof(channel)); }
                WriterSettings.Limit(nameof(MdfWriterOptions.MaximumChannelCount), (ulong)settings.MaximumChannels, (ulong)channelCount + 1);
                ulong charge;
                WriteChannel snapshot = WriteChannel.Create(channel, settings, group.DataBytes, metadataAllowance, out charge);
                if (snapshot.Bits != 8 * windowBytes || snapshot.Kind != MdfValueKind.ByteArray) { throw new InvalidOperationException("The structure window does not match its byte array."); }
                snapshot.Elements = 0;
                snapshot.Structure = true;
                long nextData = (long)group.DataBytes + windowBytes;
                long nextRecord = nextData + (((long)group.InvalidationBits) + 7) / 8;
                WriterSettings.Limit(nameof(MdfWriterOptions.MaximumRecordBytes), (ulong)settings.MaximumRecord, (ulong)nextRecord);
                int index = group.Channels.Count;
                group.Channels.Add(snapshot);
                channelCount++;
                metadataAllowance += charge;
                group.DataBytes = checked((int)nextData);
                group.RecordBytes = checked((int)nextRecord);
                snapshot.InvalidationBit = group.InvalidationBits;
                return index;
            }
            finally { Exit(); }
        }

        /// <summary>Adds a component (MDF 4) inside the window of the structure channel parentIndex, at offsetInWindow bytes
        /// from the start of that window. A component takes one value and one invalidation bit, like a top-level channel.</summary>
        public int AddComponentChannel(int groupIndex, MdfChannelDefinition channel, int parentIndex, int offsetInWindow)
        {
            Enter(CancellationToken.None);
            try
            {
                Require(MdfWriterState.Configuring);
                WriteGroup group = Group(groupIndex);
                if (channel == null) { throw new ArgumentNullException(nameof(channel)); }
                if (group.Raw) { throw new InvalidOperationException("A bus logging channel group has a fixed layout."); }
                if (parentIndex < 0 || parentIndex >= group.Channels.Count || !group.Channels[parentIndex].Structure)
                { throw new ArgumentOutOfRangeException(nameof(parentIndex), "The parent is not a structure channel of this group."); }
                if (channel.IsMaster || channel.IsVariableLength) { throw new ArgumentException("A structure component cannot be the master or variable length.", nameof(channel)); }
                if (channel.ArrayDimensions != null) { throw new NotSupportedException("An array component of a structure is written in 1.1 (CA on write)."); }
                WriteChannel parent = group.Channels[parentIndex];
                WriterSettings.Limit(nameof(MdfWriterOptions.MaximumChannelCount), (ulong)settings.MaximumChannels, (ulong)channelCount + 1);
                ulong charge;
                WriteChannel snapshot = WriteChannel.Create(channel, settings, parent.DataOffset + offsetInWindow, metadataAllowance, out charge);
                int bytes = (snapshot.Bits + 7) / 8;
                if (snapshot.Vlsd) { throw new NotSupportedException("A variable length component of a structure is written in 1.1."); }
                if (snapshot.Elements != 1 || offsetInWindow < 0 || offsetInWindow + bytes > parent.Bits / 8)
                { throw new ArgumentOutOfRangeException(nameof(offsetInWindow), "The component does not fit the structure window."); }
                snapshot.Parent = parentIndex;
                int index = group.Channels.Count;
                group.Channels.Add(snapshot);
                channelCount++;
                metadataAllowance += charge;
                snapshot.InvalidationBit = group.InvalidationBits;
                long bitsAfter = (long)group.InvalidationBits + snapshot.Elements;
                long nextRecord = (long)group.DataBytes + (bitsAfter + 7) / 8;
                WriterSettings.Limit(nameof(MdfWriterOptions.MaximumRecordBytes), (ulong)settings.MaximumRecord, (ulong)nextRecord);
                group.InvalidationBits = checked((int)bitsAfter);
                group.RecordBytes = checked((int)nextRecord);
                group.ValueCount = checked(group.ValueCount + snapshot.Elements);
                return index;
            }
            finally { Exit(); }
        }

        /// <summary>Adds a channel group and returns its index; group 0 always exists.</summary>
        /// <remarks>Each group needs at least one channel and may have its own master. MDF 4: each group is its own sorted
        /// data group; records of every group are buffered into whole-record DT (or DZ when compressing) blocks of
        /// <see cref="MdfWriterOptions.CompressionBlockBytes"/>, so up to one block per group is held in memory. MDF 3.30:
        /// all groups share one unsorted data group and every record is written immediately behind a one-byte record ID
        /// (group index + 1), so at most 255 groups exist and they have no name.</remarks>
        /// <exception cref="NotSupportedException">MDF 3.30 output with a name or a 256th group.</exception>
        public int AddChannelGroup(string name = null)
        {
            return AddChannelGroupCore(name, null, -1);
        }

        /// <summary>Adds a channel group to the data group of the channel group <paramref name="groupIndex"/> and returns its index.
        /// Both groups then share one unsorted MDF 4 data group: the records of its groups are written in one stream, each
        /// behind the one-byte record ID of its group, as the upstream writer does when a data group holds several channel
        /// groups. MDF 3.30 already shares one data group, so the call adds the same kind of group there.</summary>
        /// <remarks>The shared group takes the data group's record stream; a data group with variable length channels holds
        /// one channel group only. The group is described by <paramref name="metadata"/> as in <see cref="AddChannelGroup(MdfChannelGroupDefinition)"/>.</remarks>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="groupIndex"/> is not an existing group.</exception>
        /// <exception cref="NotSupportedException">The data group holds variable length channels, or 255 channel groups already.</exception>
        public int AddChannelGroupToDataGroup(int groupIndex, MdfChannelGroupDefinition metadata)
        {
            return AddChannelGroupCore(metadata == null ? null : metadata.Name, metadata, groupIndex);
        }

        /// <summary>Adds an MDF 4 channel group described by <paramref name="metadata"/> and returns its index; a null
        /// argument adds a plain group. MDF 3.30 has neither a name, a comment nor a source block for a channel group.</summary>
        /// <remarks>Each group needs at least one channel. One reservation covers the whole definition, so a rejected
        /// definition adds no group at all.</remarks>
        /// <exception cref="NotSupportedException">MDF 3.30 output with a name, comment or source, or a 256th group.</exception>
        public int AddChannelGroup(MdfChannelGroupDefinition metadata)
        {
            if (metadata == null) { return AddChannelGroupCore(null, null, -1); }
            return AddChannelGroupCore(metadata.Name, metadata, -1);
        }

        // The group a new channel group shares its data group with, or null for a group with a data group of its own.
        // MDF 3.30 shares the one data group of group 0 by design.
        private WriteGroup SharedOwner(int dataGroupOf)
        {
            if (settings.Mdf3) { return groups[0]; }
            if (dataGroupOf < 0) { return null; }
            WriteGroup owner = Group(dataGroupOf).Owner;
            if (owner.Members >= 255) { throw new NotSupportedException("A data group holds at most 255 channel groups (one-byte record IDs)."); }
            if (owner.VlsdChannels.Count != 0) { throw new NotSupportedException("A data group with variable length channels holds one channel group: the record IDs of its VLSD channels are taken."); }
            return owner;
        }

        private int AddChannelGroupCore(string name, MdfChannelGroupDefinition metadata, int dataGroupOf)
        {
            Enter(CancellationToken.None);
            try
            {
                Require(MdfWriterState.Configuring);
                if (settings.Mdf3 && name != null) { throw new NotSupportedException("MDF 3.30 channel groups have no acquisition name."); }
                if (settings.Mdf3 && groups.Count >= 255) { throw new NotSupportedException("MDF 3.30 output holds at most 255 channel groups (one-byte record IDs)."); }
                WriteGroup owner = SharedOwner(dataGroupOf);
                WriterSettings.Limit(nameof(MdfWriterOptions.MaximumChannelCount), (ulong)settings.MaximumChannels, (ulong)groups.Count + 1);
                if (owner != null && metadata != null && WriterMetadata.HasTags(metadata.DataGroupCommonProperties))
                { throw new NotSupportedException("A data group shared by several channel groups cannot take DataGroupCommonProperties."); }
                WriteGroup group = new WriteGroup();
                ulong charge = 1024UL + 64 + 104;
                settings.Metadata(checked(metadataAllowance + charge));
                if (metadata == null && name != null) { Name(group, name, charge); }
                if (metadata != null) { Describe(metadata, group); }
                // The owner's counts change last, so a rejected definition leaves the data group as it was.
                if (owner != null)
                {
                    group.DataOwner = owner;
                    owner.Members++;
                    group.RecordId = checked((byte)owner.Members);
                }
                groups.Add(group);
                metadataAllowance += charge;
                return groups.Count - 1;
            }
            finally { Exit(); }
        }

        /// <summary>Sets or replaces an MDF 4 channel group's acquisition name before StartMeasurement; null removes it.</summary>
        /// <exception cref="NotSupportedException">The output is MDF 3.30, which has no channel group name.</exception>
        public void SetChannelGroupName(int groupIndex, string name)
        {
            Enter(CancellationToken.None);
            try
            {
                Require(MdfWriterState.Configuring);
                WriteGroup group = Group(groupIndex);
                if (settings.Mdf3) { throw new NotSupportedException("MDF 3.30 channel groups have no acquisition name."); }
                if (name == null) { group.Name = null; return; } // Removal only: comment and source keep what they hold.
                int size = settings.TextSize(name, TextKind.Name);
                ulong charge = 24UL + (ulong)size + 4UL * (ulong)name.Length;
                settings.Metadata(checked(metadataAllowance + charge));
                group.Name = settings.Encode(name, size);
                metadataAllowance += charge;
            }
            finally { Exit(); }
        }

        /// <summary>Replaces the MDF 4 acquisition name, the <c>CGcomment</c> MD block and the <c>cg_source</c> SI block of one
        /// channel group before StartMeasurement. A null member keeps what is stored; an empty text removes it. Metadata is
        /// never applied partly: a rejected definition leaves the group unchanged.</summary>
        /// <remarks>The path separator of <c>si_path</c> is recorded as <c>cg_path_separator</c> ('/' or '\\'). MDF 3.30 has
        /// none of these blocks. Bus-logged groups keep the source their layout declared; this method may replace it.</remarks>
        /// <exception cref="NotSupportedException">The output is MDF 3.30.</exception>
        public void SetChannelGroupMetadata(int groupIndex, MdfChannelGroupDefinition metadata)
        {
            Enter(CancellationToken.None);
            try
            {
                Require(MdfWriterState.Configuring);
                if (metadata == null) { throw new ArgumentNullException(nameof(metadata)); }
                Describe(metadata, Group(groupIndex));
            }
            finally { Exit(); }
        }

        // The body of both metadata entry points; the caller holds the operation lock. Every member of the
        // definition is optional: null keeps what the group already holds, which is charged nothing because the
        // block that stays was charged when it was described, while an empty text removes the block it filled.
        private void Describe(MdfChannelGroupDefinition metadata, WriteGroup group)
        {
            if (settings.Mdf3) { throw new NotSupportedException("MDF 3.30 channel groups have no name, comment or source block."); }
            byte[] commentText = group.CommentText;
            int commentTextBytes = 0;
            if (metadata.Comment != null || (metadata.CommonProperties != null && metadata.CommonProperties.Count > 0))
            {
                MetadataText.EncodeBlockComment(settings, "CGcomment", metadata.Comment, metadata.CommonProperties, out commentText, out commentTextBytes);
            }
            byte[] sourceId = group.SourceName, sourcePath = group.SourcePath, sourceComment = group.SourceComment;
            int sourceBytes = 0, sourcePathBytes = 0, sourceCommentBytes = 0;
            bool replaceSource = metadata.Source != null;
            if (replaceSource)
            {
                MetadataText.EncodeSource(settings, metadata.Source, out sourceId, out sourceBytes, out sourcePath,
                    out sourcePathBytes, out sourceComment, out sourceCommentBytes);
            }
            string name = metadata.Name;
            int nameSize = MetadataText.IsSet(name) ? settings.TextSize(name, TextKind.Name) : 0;
            byte[] dataGroupComment = group.DataGroupComment;
            int dataGroupCommentBytes = 0;
            if (WriterMetadata.HasTags(metadata.DataGroupCommonProperties))
            {
                if (group.DataOwner != null) { throw new NotSupportedException("A data group shared by several channel groups cannot take DataGroupCommonProperties."); }
                dataGroupComment = WriterMetadata.Comment("DGcomment", null, metadata.DataGroupCommonProperties, settings, out dataGroupCommentBytes);
            }
            ulong charge = (ulong)nameSize + (ulong)commentTextBytes + (ulong)sourceBytes + (ulong)sourcePathBytes + (ulong)dataGroupCommentBytes
                + (ulong)sourceCommentBytes;
            if (replaceSource)
            {
                charge += (sourceId == null && sourceCommentBytes == 0 ? 0UL : 80UL) + (sourcePathBytes == 0 ? 0UL : 24UL)
                    + (sourceCommentBytes == 0 ? 0UL : 24UL);
            }
            // si_path costs the extra link of the SI block and decides cg_path_separator; both are only touched
            // together with the source, so a kept source keeps the separator it was written with.
            string path = replaceSource ? metadata.Source.Path : null;
            bool hasPath = MetadataText.IsSet(path);
            if (hasPath) { charge += 8UL; }
            settings.Metadata(checked(metadataAllowance + charge));
            if (name != null) { group.Name = name.Length == 0 ? null : settings.Encode(name, nameSize); }
            group.CommentText = commentText;
            group.DataGroupComment = dataGroupComment;
            if (!replaceSource) { metadataAllowance += charge; return; }
            group.SourceName = sourceId;
            group.SourcePath = sourcePath;
            group.SourceComment = sourceComment;
            group.SourceType = MetadataText.SourceTypeOf(metadata.Source);
            group.BusType = MetadataText.BusTypeOf(metadata.Source);
            group.SourceFlags = MetadataText.FlagsOf(metadata.Source);
            if (hasPath)
            {
                int slash = path.IndexOf('/');
                int backslash = path.IndexOf('\\');
                group.PathSeparator = slash >= 0 && (backslash < 0 || slash < backslash) ? (ushort)'/' : (ushort)'\\';
            }
            else { group.PathSeparator = 0; }
            metadataAllowance += charge;
        }

        private void Name(WriteGroup group, string name, ulong reserved)
        {
            int size = settings.TextSize(name, TextKind.Name);
            ulong charge = 24UL + (ulong)size + 4UL * (ulong)name.Length;
            settings.Metadata(checked(metadataAllowance + reserved + charge));
            group.Name = settings.Encode(name, size);
            metadataAllowance += charge;
        }

        /// <summary>Gets the number of completely accepted raw records of one channel group.</summary>
        public ulong GetSampleCount(int groupIndex)
        {
            return Group(groupIndex).SampleCount;
        }

        /// <summary>Freezes configuration and writes bounded metadata with an unpublished identification marker.</summary>
        public void StartMeasurement(CancellationToken cancellationToken = default(CancellationToken))
        {
            Enter(cancellationToken);
            try
            {
                Require(MdfWriterState.Configuring);
                foreach (WriteGroup group in groups)
                { if (group.Channels.Count == 0) { throw new InvalidOperationException("At least one channel is required in every channel group."); } }
                long origin = appendLink < 0 ? 0 : (expectedLength + 7) & ~7L;
                WriterPlan candidate = WriterPlan.Create(settings, groups, metadataAllowance, cancellationToken, origin);
                bool candidateBlocked = settings.Compress || (WriteGroup.CountDataGroups(groups) > 1 && !settings.Mdf3);
                for (int g = 0; g < groups.Count; g++)
                {
                    WriteGroup group = groups[g];
                    group.Record = new byte[group.RecordBytes];
                    // records of a shared data group carry the record ID of their group (MDF 3.30 always shares).
                    group.Identified = group.Owner.Members > 1 ? new byte[group.RecordBytes + 1] : null;
                    if (group.Identified != null) { group.Identified[0] = group.RecordId; }
                    group.BlockBytes = group.VlsdChannels.Count != 0 || group.Owner.Members > 1 ? Math.Max(64, settings.CompressionBlock)
                        : checked(Math.Max(1, settings.CompressionBlock / group.RecordBytes) * group.RecordBytes);
                }
                Checkpoint(cancellationToken);
                try
                {
                    if (origin > expectedLength) { WriteAt(expectedLength, zeros, (int)(origin - expectedLength), cancellationToken); }
                    WriteAt(origin, candidate.Prefix, candidate.Prefix.Length, cancellationToken);
                    candidate.Prefix = null; // Do not retain the metadata wire buffer while recording.
                    plan = candidate;
                    blocked = candidateBlocked;
                    if (!blocked && settings.WriteBuffer > 0) { writeBuffer = new byte[settings.WriteBuffer]; }
                    Transition(MdfWriterState.Configuring, MdfWriterState.Recording);
                }
                catch { Fault(); throw; }
            }
            finally { Exit(); }
        }

        /// <summary>Validates and saves one complete raw row of channel group 0; wrong kind/range/count/validity writes no partial row.</summary>
        /// <remarks>Float32 rounds by IEEE cast; finite values outside [-Single.MaxValue, Single.MaxValue] are refused.
        /// MDF 3 invalid samples are refused. There is no inverse physical conversion or automatic time-master value.
        /// Text is strictly re-encoded (MDF 4 UTF-8, MDF 3.30 Latin-1), must fit its field without NUL and is NUL-padded;
        /// a byte array must have exactly the field width. An array channel takes its elements as consecutive values in
        /// row-major order (see <see cref="MdfChannelDefinition.ArrayDimensions"/>).</remarks>
        public void SaveSample(IReadOnlyList<MdfSampleValue> values, CancellationToken cancellationToken = default(CancellationToken))
        {
            SaveSample(0, values, cancellationToken);
        }

        /// <summary>Validates and saves one complete raw row of a channel group, with the same rules as the group 0 overload.</summary>
        public void SaveSample(int groupIndex, IReadOnlyList<MdfSampleValue> values, CancellationToken cancellationToken = default(CancellationToken))
        {
            Enter(cancellationToken);
            try
            {
                Require(MdfWriterState.Recording);
                WriteGroup group = Group(groupIndex);
                if (group.Raw) { throw new InvalidOperationException("This channel group is written through its bus logger."); }
                if (values == null) { throw new ArgumentNullException(nameof(values)); }
                List<WriteChannel> channels = group.Channels;
                byte[] record = group.Record;
                int valueCount = values.Count;
                Checkpoint(cancellationToken); // Count is caller code too; it may cancel, dispose or attempt reentry.
                if (valueCount != group.ValueCount)
                { throw new ArgumentException("A row must supply every configured channel (and every array element) exactly once.", nameof(values)); }
                Array.Clear(record, 0, record.Length);
                int next = 0;
                for (int i = 0; i < channels.Count; i++)
                {
                    WriteChannel channel = channels[i];
                    for (int element = 0; element < channel.Elements; element++)
                    {
                        Checkpoint(cancellationToken);
                        MdfSampleValue value = values[next++];
                        Checkpoint(cancellationToken);
                        if (settings.Mdf3 && !value.IsValid) { throw new NotSupportedException("MDF 3.30 cannot store per-sample invalidity."); }
                        if (channel.Vlsd)
                        {
                            // the offset of this value's VLSD record within its VLSD channel group.
                            channel.VlsdValue = ScalarEncoder.Variable(channel, value, settings.MaximumText);
                            WriterWire.UInt(record, channel.DataOffset, channel.VlsdBytes, 8);
                        }
                        else { ScalarEncoder.Write(record, channel, value, channel.DataOffset + element * channel.Stride); }
                        int bit = channel.InvalidationBit + element;
                        if (!value.IsValid) { record[group.DataBytes + bit / 8] |= (byte)(1 << (bit & 7)); }
                    }
                }
                int finalCount = values.Count;
                Checkpoint(cancellationToken);
                if (finalCount != group.ValueCount) { throw new ArgumentException("The row collection changed while being copied.", nameof(values)); }
                AppendRecord(group, cancellationToken);
            }
            finally { Exit(); }
        }

        // explicit-layout groups (bus logging) supply complete records; layout tables guarantee field ranges.
        internal int AddRawGroup(RawGroup spec, int dataGroupOf = -1)
        {
            Enter(CancellationToken.None);
            try
            {
                Require(MdfWriterState.Configuring);
                if (settings.Mdf3) { throw new NotSupportedException("Bus logging requires MDF 4 output."); }
                WriterSettings.Limit(nameof(MdfWriterOptions.MaximumRecordBytes), (ulong)settings.MaximumRecord, (ulong)spec.DataBytes);
                WriterSettings.Limit(nameof(MdfWriterOptions.MaximumChannelCount), (ulong)settings.MaximumChannels, (ulong)channelCount + (ulong)spec.Channels.Count);
                WriteGroup owner = SharedOwner(dataGroupOf);
                bool reuse = owner == null && groups.Count == 1 && groups[0].Channels.Count == 0;
                WriteGroup group = reuse ? groups[0] : new WriteGroup();
                ulong allowance = metadataAllowance + (reuse ? 0UL : 1024UL + 64 + 104);
                settings.Metadata(allowance);
                List<WriteChannel> channels = new List<WriteChannel>(spec.Channels.Count);
                foreach (RawChannel raw in spec.Channels)
                {
                    ulong charge;
                    channels.Add(WriteChannel.CreateExplicit(raw, settings, allowance, out charge));
                    allowance += charge;
                }
                if (spec.Name != null)
                {
                    int size = settings.TextSize(spec.Name, TextKind.Name);
                    allowance += 24UL + (ulong)size;
                    settings.Metadata(allowance);
                    group.Name = settings.Encode(spec.Name, size);
                }
                int sourceCommentSize = spec.SourceComment == null ? 0
                    : settings.TextSize(WriterMetadata.Wrap("SIcomment", spec.SourceComment), TextKind.Comment);
                int groupCommentSize = spec.Comment == null ? 0
                    : settings.TextSize(WriterMetadata.Wrap("CGcomment", spec.Comment), TextKind.Comment);
                if (spec.SourceName != null)
                {
                    int nameSize = settings.TextSize(spec.SourceName, TextKind.Name);
                    int pathSize = spec.SourcePath == null ? 0 : settings.TextSize(spec.SourcePath, TextKind.Name);
                    allowance += 56UL + 48UL + (ulong)nameSize + (ulong)pathSize + 24UL
                        + (ulong)(spec.SourceComment == null ? 0 : sourceCommentSize + 24);
                    settings.Metadata(allowance);
                    group.SourceName = settings.Encode(spec.SourceName, nameSize);
                    group.SourcePath = spec.SourcePath == null ? null : settings.Encode(spec.SourcePath, pathSize);
                    group.SourceComment = spec.SourceComment == null ? null
                        : settings.Encode(WriterMetadata.Wrap("SIcomment", spec.SourceComment), sourceCommentSize);
                    group.SourceType = spec.SourceType;
                    group.BusType = spec.BusType;
                    // si_flags validity bits, as the block is linked here: always a name, a path only when stored.
                    group.SourceFlags = spec.SourceFlags == 0 ? (byte)(1 | (group.SourcePath == null ? 0 : 2)) : spec.SourceFlags;
                }
                if (spec.Comment != null)
                {
                    allowance += 24UL + (ulong)groupCommentSize;
                    settings.Metadata(allowance);
                    group.CommentText = settings.Encode(WriterMetadata.Wrap("CGcomment", spec.Comment), groupCommentSize);
                }
                group.Raw = true;
                group.Flags = spec.Flags;
                group.PathSeparator = spec.PathSeparator;
                group.DataBytes = spec.DataBytes;
                group.RecordBytes = spec.DataBytes;
                group.HasMaster = true;
                group.Channels.AddRange(channels);
                // a group keeps one SD channel; its payloads are appended to one SD block at finalization.
                foreach (WriteChannel channel in channels)
                {
                    if (!channel.Sd) { continue; }
                    if (group.SdChannel != null) { throw new NotSupportedException("A channel group holds one SD channel."); }
                    group.SdChannel = channel;
                    group.SdData = group.SdData ?? new System.IO.MemoryStream();
                }
                channelCount += channels.Count;
                metadataAllowance = allowance;
                if (owner != null)
                {
                    group.DataOwner = owner;
                    owner.Members++;
                    group.RecordId = checked((byte)owner.Members);
                }
                if (!reuse) { groups.Add(group); }
                return groups.IndexOf(group);
            }
            finally { Exit(); }
        }

        internal void SaveRecord(int groupIndex, byte[] record, byte[] payload, CancellationToken cancellationToken)
        {
            Enter(cancellationToken);
            try
            {
                Require(MdfWriterState.Recording);
                WriteGroup group = Group(groupIndex);
                if (!group.Raw || record.Length != group.RecordBytes) { throw new InvalidOperationException("Raw records require a matching explicit-layout group."); }
                if (group.SdChannel == null && payload != null && payload.Length != 0) { throw new InvalidOperationException("This group has no SD channel for the payload."); }
                Buffer.BlockCopy(record, 0, group.Record, 0, record.Length);
                if (group.SdChannel != null) { StorePayload(group, payload ?? new byte[0]); }
                AppendRecord(group, cancellationToken);
            }
            finally { Exit(); }
        }

        private void AppendRecord(WriteGroup group, CancellationToken cancellationToken)
        {
            byte[] record = group.Record;
            int length = record.Length;
            if (group.Identified != null)
            {
                Buffer.BlockCopy(record, 0, group.Identified, 1, record.Length);
                record = group.Identified;
                length = record.Length;
            }
            else if (group.VlsdChannels.Count != 0) { record = VlsdRow(group, out length); }
            WriterPlan.CheckAppend(settings.Mdf3, group.SampleCount, expectedLength + bufferLength, length);
            try
            {
                if (!blocked && writeBuffer == null) { WriteAt(expectedLength, record, length, cancellationToken); }
                else if (!blocked)
                {
                    if (bufferLength + length > writeBuffer.Length) { FlushBuffer(cancellationToken); }
                    if (length > writeBuffer.Length) { WriteAt(expectedLength, record, length, cancellationToken); }
                    else
                    {
                        Buffer.BlockCopy(record, 0, writeBuffer, bufferLength, length);
                        bufferLength += length;
                    }
                }
                else
                {
                    // Every block holds whole rows, so each completed block is recoverable on its own. Fixed groups fill
                    // whole-record blocks; VLSD rows close a block they do not fit, and a larger row gets its own block.
                    // the rows of a shared data group go to the owner's stream, so its blocks hold every group's rows.
                    WriteGroup stream = group.Owner;
                    if (stream.Chunk == null) { stream.Chunk = new byte[stream.BlockBytes]; }
                    if (stream.ChunkLength != 0 && stream.ChunkLength + length > stream.Chunk.Length) { WriteDataBlock(stream, cancellationToken); }
                    if (length > stream.Chunk.Length)
                    {
                        byte[] chunk = stream.Chunk;
                        stream.Chunk = record;
                        stream.ChunkLength = length;
                        try { WriteDataBlock(stream, cancellationToken); }
                        finally { stream.Chunk = chunk; }
                    }
                    else
                    {
                        Buffer.BlockCopy(record, 0, stream.Chunk, stream.ChunkLength, length);
                        stream.ChunkLength += length;
                        if (stream.ChunkLength == stream.Chunk.Length) { WriteDataBlock(stream, cancellationToken); }
                    }
                }
                foreach (WriteChannel channel in group.VlsdChannels)
                {
                    channel.VlsdCount++;
                    channel.VlsdBytes += 4UL + (ulong)channel.VlsdValue.Length;
                    channel.VlsdValue = null;
                }
                group.SampleCount++;
                SampleCount++;
            }
            catch { Fault(); throw; }
        }

        // one row of a group with VLSD channels: each value as [ID k+2][UInt32 length][bytes], then [ID 1][fixed record].
        private byte[] VlsdRow(WriteGroup group, out int length)
        {
            long total = 1 + group.Record.Length;
            foreach (WriteChannel channel in group.VlsdChannels) { total += 5 + channel.VlsdValue.Length; }
            if (total > int.MaxValue) { throw new NotSupportedException("A row with its VLSD values exceeds Int32.MaxValue bytes."); }
            length = (int)total;
            if (group.Wire == null || group.Wire.Length < length) { group.Wire = new byte[Math.Max(length, group.Wire == null ? 0 : Math.Min(int.MaxValue / 2, group.Wire.Length) * 2)]; }
            byte[] wire = group.Wire;
            int at = 0;
            for (int k = 0; k < group.VlsdChannels.Count; k++)
            {
                byte[] value = group.VlsdChannels[k].VlsdValue;
                wire[at] = (byte)(k + 2);
                WriterWire.UInt(wire, at + 1, (ulong)value.Length, 4);
                Buffer.BlockCopy(value, 0, wire, at + 5, value.Length);
                at += 5 + value.Length;
            }
            wire[at] = 1;
            Buffer.BlockCopy(group.Record, 0, wire, at + 1, group.Record.Length);
            return wire;
        }

        /// <summary>Stops accepting samples without implicit finalization or a durability guarantee.</summary>
        public void StopMeasurement(CancellationToken cancellationToken = default(CancellationToken))
        {
            Enter(cancellationToken);
            try
            {
                Require(MdfWriterState.Recording);
                try { FlushBuffer(cancellationToken); } catch { Fault(); throw; }
                Transition(MdfWriterState.Recording, MdfWriterState.Stopped);
            }
            finally { Exit(); }
        }

        /// <summary>Explicitly patches counts/lengths, flushes, publishes finalized identification and flushes again.</summary>
        /// <remarks>Requires Stopped; idempotent after successful Finalized. On any output failure no success is returned,
        /// even if part/all of the final identification reached the destination. Not a file-system transaction or recovery API.</remarks>
        public void FinalizeMeasurement(CancellationToken cancellationToken = default(CancellationToken))
        {
            Enter(cancellationToken);
            try
            {
                if (State == MdfWriterState.Finalized) { return; }
                Require(MdfWriterState.Stopped);
                Checkpoint(cancellationToken);
                try
                {
                    if (blocked)
                    {
                        foreach (WriteGroup group in groups)
                        {
                            if (group.ChunkLength != 0) { WriteDataBlock(group, cancellationToken); }
                            CompactDataList(group, cancellationToken);
                        }
                    }
                    long dataEnd = expectedLength;
                    if (!settings.Mdf3)
                    {
                        int padding = (int)((8 - (expectedLength & 7)) & 7);
                        if (padding != 0) { WriteAt(expectedLength, zeros, padding, cancellationToken); }
                    }
                    foreach (WriteGroup group in groups)
                    {
                        WriterWire.UInt(patch, 0, group.SampleCount, settings.Mdf3 ? 4 : 8);
                        WriteAt(group.CountOffset, patch, settings.Mdf3 ? 4 : 8, cancellationToken);
                        foreach (WriteChannel channel in group.VlsdChannels)
                        {
                            // VLSD CG cycle count and its UInt64 byte total (cg_data_bytes + cg_inval_bytes).
                            WriterWire.UInt(patch, 0, channel.VlsdCount, 8);
                            WriteAt(channel.VlsdCountOffset, patch, 8, cancellationToken);
                            WriterWire.UInt(patch, 0, channel.VlsdBytes, 8);
                            WriteAt(channel.VlsdCountOffset + 16, patch, 8, cancellationToken);
                        }
                    }
                    if (!settings.Mdf3 && plan.DataBlockAddress != 0)
                    {
                        WriterWire.UInt(patch, 0, (ulong)(dataEnd - plan.DataBlockAddress), 8);
                        WriteAt(plan.DataBlockAddress + 8, patch, 8, cancellationToken);
                    }
                    foreach (WriteGroup group in groups) { if (group.SdChannel != null && group.SdData.Length != 0) { WriteSdBlock(group, cancellationToken); } }
                    if (attachments.Count != 0 || events.Count != 0)
                    {
                        // the data is final; clear the recovery flags first so an interrupted tail never becomes records.
                        if (appendLink < 0)
                        {
                            Flush(cancellationToken);
                            WriterWire.UInt(patch, 0, 0, 4);
                            WriteAt(60, patch, 4, cancellationToken);
                        }
                        long start = (expectedLength + 7) & ~7L;
                        byte[] tail = WriterTail.Build(start, attachments, events, groups, cancellationToken);
                        if (start > expectedLength) { WriteAt(expectedLength, zeros, (int)(start - expectedLength), cancellationToken); }
                        WriteAt(start, tail, tail.Length, cancellationToken);
                    }
                    if (attachments.Count != 0)
                    {
                        Flush(cancellationToken);
                        WriterWire.UInt(patch, 0, (ulong)attachments[0].Address, 8);
                        WriteAt(appendAttachmentLink, patch, 8, cancellationToken);
                    }
                    if (events.Count != 0)
                    {
                        Flush(cancellationToken);
                        WriterWire.UInt(patch, 0, (ulong)events[0].Address, 8);
                        WriteAt(appendEventLink, patch, 8, cancellationToken);
                    }
                    Flush(cancellationToken);
                    if (appendLink < 0) { WriteAt(0, plan.FinalIdentification, 64, cancellationToken); }
                    else
                    {
                        WriterWire.UInt(patch, 0, (ulong)groups[0].DataGroupAddress, 8);
                        WriteAt(appendLink, patch, 8, cancellationToken); // One aligned link store publishes all appended groups.
                    }
                    Seek(expectedLength, cancellationToken);
                    Flush(cancellationToken);
                    Transition(MdfWriterState.Stopped, MdfWriterState.Finalized);
                }
                catch { Fault(); throw; }
            }
            finally { Exit(); }
        }

        private void FlushBuffer(CancellationToken token)
        {
            if (bufferLength == 0) { return; }
            WriteAt(expectedLength, writeBuffer, bufferLength, token);
            bufferLength = 0;
        }

        // one block of whole records at the next eight-byte boundary: zlib DZ(DT) when compressing, else DT.
        private void WriteDataBlock(WriteGroup group, CancellationToken token)
        {
            int padding = (int)((8 - (expectedLength & 7)) & 7);
            if (padding != 0) { WriteAt(expectedLength, zeros, padding, token); }
            long address = expectedLength;
            if (settings.Compress)
            {
                byte[] compressed = Compression.ZlibDeflater.Compress(group.Chunk, 0, group.ChunkLength, () => Checkpoint(token));
                byte[] header = new byte[48];
                WriterWire.Ascii(header, 0, "##DZ");
                WriterWire.UInt(header, 8, checked((ulong)(48 + compressed.Length)), 8);
                WriterWire.Ascii(header, 24, "DT");
                WriterWire.UInt(header, 32, (ulong)group.ChunkLength, 8);
                WriterWire.UInt(header, 40, (ulong)compressed.Length, 8);
                WriteAt(address, header, header.Length, token);
                WriteAt(expectedLength, compressed, compressed.Length, token);
            }
            else
            {
                byte[] header = new byte[24];
                WriterWire.Ascii(header, 0, "##DT");
                WriterWire.UInt(header, 8, checked((ulong)(24 + group.ChunkLength)), 8);
                WriteAt(address, header, header.Length, token);
                WriteAt(expectedLength, group.Chunk, group.ChunkLength, token);
            }
            group.Blocks.Add(address);
            long logical = group.LogicalBytes;
            group.LogicalBytes += group.ChunkLength;
            group.ChunkLength = 0;
            LinkDataBlock(group, address, logical, token);
        }

        // equal-length DL blocks of ListCapacity data links, preallocated after a group's first block (and when the
        // previous DL is full) and linked from the DG (or the previous DL) at once. Each written block is then published by
        // patching its link and dl_count, so an interrupted file keeps every completed block reachable (id_unfi_flags bit 4).
        internal const int ListCapacity = 1024;

        internal static int ListLength(int links, bool offsets = false) { return 24 + 8 * (1 + links) + (offsets ? 8 + 8 * links : 16); }

        private void LinkDataBlock(WriteGroup group, long address, long logical, CancellationToken token)
        {
            bool offsets = group.VlsdChannels.Count != 0 || group.Members > 1; // shared streams hold rows of mixed size
            if (group.ListAddress == 0 || group.ListCount == ListCapacity)
            {
                int padding = (int)((8 - (expectedLength & 7)) & 7);
                if (padding != 0) { WriteAt(expectedLength, zeros, padding, token); }
                long list = expectedLength;
                int length = ListLength(ListCapacity, offsets);
                byte[] block = new byte[length];
                WriterWire.Ascii(block, 0, "##DL");
                WriterWire.UInt(block, 8, (ulong)length, 8);
                WriterWire.UInt(block, 16, 1 + ListCapacity, 8);
                int fields = 32 + 8 * ListCapacity;
                if (!offsets)
                {
                    block[fields] = 1; // Equal length: every block but the last holds exactly one full buffer.
                    WriterWire.UInt(block, fields + 8, (ulong)group.BlockBytes, 8);
                }
                WriteAt(list, block, length, token);
                WriterWire.UInt(patch, 0, (ulong)list, 8);
                WriteAt(group.ListAddress == 0 ? group.DataGroupAddress + 24 + 16 : group.ListAddress + 24, patch, 8, token);
                group.ListAddress = list;
                group.ListCount = 0;
                group.ListOffsets.Clear();
            }
            if (offsets)
            {
                // the block's logical offset is published before its link (recovery counts linked blocks).
                WriterWire.UInt(patch, 0, (ulong)logical, 8);
                WriteAt(group.ListAddress + 32 + 8 * ListCapacity + 8 + 8 * group.ListCount, patch, 8, token);
                group.ListOffsets.Add(logical);
            }
            WriterWire.UInt(patch, 0, (ulong)address, 8);
            WriteAt(group.ListAddress + 32 + 8 * group.ListCount, patch, 8, token);
            group.ListCount++;
            WriterWire.UInt(patch, 0, (ulong)group.ListCount, 4);
            WriteAt(group.ListAddress + 32 + 8 * ListCapacity + 4, patch, 4, token);
            Seek(expectedLength, token);
        }

        // Shrinks the group's last DL in place to its used links (header length/link count and a moved data section).
        private void CompactDataList(WriteGroup group, CancellationToken token)
        {
            if (group.ListAddress == 0 || group.ListCount == ListCapacity) { return; }
            int count = group.ListCount;
            bool offsets = group.VlsdChannels.Count != 0 || group.Members > 1;
            byte[] header = new byte[16];
            WriterWire.UInt(header, 0, (ulong)ListLength(count, offsets), 8);
            WriterWire.UInt(header, 8, (ulong)(1 + count), 8);
            byte[] fields = new byte[offsets ? 8 + 8 * count : 16];
            if (offsets) { for (int i = 0; i < count; i++) { WriterWire.UInt(fields, 8 + 8 * i, (ulong)group.ListOffsets[i], 8); } }
            else
            {
                fields[0] = 1;
                WriterWire.UInt(fields, 8, (ulong)group.BlockBytes, 8);
            }
            WriterWire.UInt(fields, 4, (ulong)count, 4);
            WriteAt(group.ListAddress + 32 + 8 * count, fields, fields.Length, token);
            WriteAt(group.ListAddress + 8, header, 16, token);
            Seek(expectedLength, token);
        }

        private WriteGroup Group(int index)
        {
            if (index < 0 || index >= groups.Count) { throw new ArgumentOutOfRangeException("groupIndex"); }
            return groups[index];
        }

        // the record holds the SD offset of its payload (the SD data length before it); the payload is [UInt32 length][bytes].
        private static void StorePayload(WriteGroup group, byte[] payload)
        {
            if ((ulong)payload.Length > uint.MaxValue) { throw new ArgumentOutOfRangeException("payload", "A payload holds at most UInt32.MaxValue bytes."); }
            ulong index = (ulong)group.SdData.Length;
            byte[] length = new byte[4];
            WriterWire.UInt(length, 0, (ulong)payload.Length, 4);
            group.SdData.Write(length, 0, 4);
            group.SdData.Write(payload, 0, payload.Length);
            WriterWire.UInt(group.Record, group.SdChannel.DataOffset, index, 8);
        }

        // one ##SD block (link count 0, length 24 + data) after the data; cn_data of the SD channel points to it.
        private void WriteSdBlock(WriteGroup group, CancellationToken token)
        {
            int padding = (int)((8 - (expectedLength & 7)) & 7);
            if (padding != 0) { WriteAt(expectedLength, zeros, padding, token); }
            byte[] body = group.SdData.ToArray();
            int total = 24 + body.Length;
            byte[] block = new byte[total + ((8 - (total & 7)) & 7)];
            block[0] = (byte)'#'; block[1] = (byte)'#'; block[2] = (byte)'S'; block[3] = (byte)'D';
            WriterWire.UInt(block, 8, (ulong)total, 8);
            Array.Copy(body, 0, block, 24, body.Length);
            long address = expectedLength;
            WriteAt(address, block, block.Length, token);
            WriterWire.UInt(patch, 0, (ulong)address, 8);
            WriteAt(group.SdChannel.ChannelAddress + 24 + 8 * 5, patch, 8, token); // cn_data is link 5
        }

        // the start time changes after the header was written (the wrapper's StartMeasurement after InitMeasurement).
        // The HD start time is a fixed-width field, so it is patched in place: MDF 4 at 136; MDF 3 at 228, with the date
        // and time texts at 82 and 92 formatted as the header writer formats them. Nothing else in the file depends on it.
        internal void PatchStartTime(ulong nanoseconds)
        {
            byte[] field = new byte[8];
            WriterWire.UInt(field, 0, nanoseconds, 8);
            if (!settings.Mdf3)
            {
                WriteAt(136, field, 8, CancellationToken.None);
                return;
            }
            DateTime time = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks((long)(nanoseconds / 100));
            byte[] date = System.Text.Encoding.ASCII.GetBytes(time.ToString("dd:MM:yyyy", System.Globalization.CultureInfo.InvariantCulture));
            byte[] clock = System.Text.Encoding.ASCII.GetBytes(time.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
            WriteAt(82, date, date.Length, CancellationToken.None);
            WriteAt(92, clock, clock.Length, CancellationToken.None);
            WriteAt(228, field, 8, CancellationToken.None);
        }

        private void WriteAt(long offset, byte[] buffer, int count, CancellationToken token)
        {
            Seek(offset, token);
            stream.Write(buffer, 0, count);
            Checkpoint(token);
            long end = checked(offset + count);
            long length = Math.Max(expectedLength, end);
            if (stream.Position != end || stream.Length != length) { throw new IOException("The destination did not complete the exact write."); }
            Checkpoint(token);
            expectedLength = length;
        }

        private void Seek(long offset, CancellationToken token)
        {
            Checkpoint(token);
            if (stream.Length != expectedLength) { throw new IOException("The destination length changed outside this writer."); }
            Checkpoint(token);
            if (stream.Seek(offset, SeekOrigin.Begin) != offset || stream.Position != offset)
            { throw new IOException("The destination did not complete the exact seek."); }
            Checkpoint(token);
            if (stream.Length != expectedLength) { throw new IOException("The destination length changed during seek."); }
            Checkpoint(token);
        }

        private void Flush(CancellationToken token)
        {
            Checkpoint(token);
            long position = stream.Position;
            Checkpoint(token); // Position is caller code too; do not flush after its cancellation or disposal.
            stream.Flush();
            Checkpoint(token);
            if (stream.Position != position || stream.Length != expectedLength) { throw new IOException("The destination changed during flush."); }
            Checkpoint(token);
        }

        private void Checkpoint(CancellationToken token)
        {
            if (State == MdfWriterState.Disposed) { throw new ObjectDisposedException(nameof(MdfWriter)); }
            token.ThrowIfCancellationRequested();
        }
        private void Enter(CancellationToken token)
        {
            Checkpoint(token);
            if (Interlocked.CompareExchange(ref activeOperation, 1, 0) != 0)
            { throw new InvalidOperationException("Another writer operation is active."); }
            try { Checkpoint(token); }
            catch { Exit(); throw; }
        }
        private void Exit() { Interlocked.Exchange(ref activeOperation, 0); }
        private void Require(MdfWriterState required)
        {
            Checkpoint(CancellationToken.None);
            if (State != required) { throw new InvalidOperationException("The writer must be " + required + ", not " + State + "."); }
        }
        private void Transition(MdfWriterState from, MdfWriterState to)
        {
            Require(from);
            if (Interlocked.CompareExchange(ref state, (int)to, (int)from) != (int)from) { Checkpoint(CancellationToken.None); throw new InvalidOperationException("Writer state changed."); }
        }
        private void Fault()
        {
            while (true)
            {
                int previous = Volatile.Read(ref state);
                if (previous == (int)MdfWriterState.Disposed) { return; }
                if (Interlocked.CompareExchange(ref state, (int)MdfWriterState.Faulted, previous) == previous) { return; }
            }
        }

        /// <summary>Closes an owned destination once. Never stops, finalizes, repairs or publishes an unfinished file.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref state, (int)MdfWriterState.Disposed) == (int)MdfWriterState.Disposed) { return; }
            foreach (WriteGroup group in groups) { group.Record = null; group.Chunk = null; }
            writeBuffer = null;
            if (!leaveOpen) { stream.Dispose(); }
        }
    }
}
