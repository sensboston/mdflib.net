// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using MdfLib.Formats;
using MdfLib.Internal;
using MdfLib.IO;
using MdfLib.Reading;

namespace MdfLib
{
    /// <summary>Opens MDF 3.00/3.10/3.20/3.30/4.00/4.10/4.11/4.20; explicitly reads limited metadata, fixed scalars and simple MDF 4 numeric arrays (4.20 sorted only).</summary>
    /// <remarks>One reader/stream is not thread-safe. Reading never writes or repairs the source.
    /// The caller must not modify the source contents while the reader uses its cached metadata/ranges.</remarks>
    internal sealed class MdfReader : IDisposable
    {
        private readonly BinaryInput input;
        private readonly ReadBudget budget;
        private readonly object document = new object();
        private IReadOnlyList<MdfDataGroup> catalog;
        private MdfFileModel fileModel;
        private int activeOperation;
        private bool disposed;

        private MdfReader(BinaryInput input, MdfFileHeader header, BlockResolver resolver, ReaderLimits limits, ReadBudget budget)
        {
            this.input = input;
            this.budget = budget;
            Header = header;
            Resolver = resolver;
            Limits = limits;
            if (limits.DecodedBlockCacheBytes > 0) { BlockCache = new DecodedBlockCache(limits.DecodedBlockCacheBytes); }
        }

        /// <summary>Gets the materialized snapshot, which remains available after Dispose.</summary>
        public MdfFileHeader Header { get; }

        internal BlockResolver Resolver { get; }
        internal ReaderLimits Limits { get; }
        // null unless MdfReaderOptions.DecodedBlockCacheBytes is positive.
        internal DecodedBlockCache BlockCache { get; private set; }

        /// <summary>Opens a readable, seekable stream and reads ID/HD from offset zero.</summary>
        /// <param name="stream">Source with MDF offsets relative to its beginning.</param>
        /// <param name="leaveOpen">Whether to leave the source open after disposal or a parse failure.</param>
        /// <param name="options">Positive finite resource settings, snapshotted before ownership; null uses defaults.</param>
        /// <param name="cancellationToken">Cooperative cancellation between synchronous reads.</param>
        /// <returns>A reader with a materialized header; samples are not loaded.</returns>
        /// <exception cref="ArgumentException">Arguments, source capabilities or settings are invalid.</exception>
        /// <exception cref="MdfFormatException">The required MDF structure is malformed.</exception>
        /// <exception cref="MdfResourceLimitException">A configured resource limit is exceeded.</exception>
        /// <exception cref="NotSupportedException">The revision, encoding, float format or unfinalized state is unsupported.</exception>
        public static MdfReader Open(Stream stream, bool leaveOpen = false, MdfReaderOptions options = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            ReaderLimits limits = new ReaderLimits(options);
            cancellationToken.ThrowIfCancellationRequested();
            BinaryInput input = new BinaryInput(stream, leaveOpen, limits.MaximumReadBufferBytes);
            try
            {
                ReadBudget budget = new ReadBudget(limits);
                IdentificationBlock id = IdentificationBlock.Read(input, budget, cancellationToken);
                BlockResolver resolver = new BlockResolver(input, id.Version >= 400, budget);
                MdfFileHeader header = new MdfFileHeader
                {
                    Version = id.VersionText,
                    ProgramIdentifier = id.ProgramIdentifier,
                    DefaultDataIsBigEndian = id.DefaultDataIsBigEndian
                };
                if (id.Version < 400)
                {
                    Formats.Mdf3.HeaderCodec.Read(resolver, id, header, cancellationToken);
                }
                else
                {
                    Formats.Mdf4.HeaderCodec.Read(resolver, header, cancellationToken);
                }
                return new MdfReader(input, header, resolver, limits, budget);
            }
            catch
            {
                // Preserve the parse/I/O/cancellation cause even if cleanup itself fails.
                try { input.Dispose(); }
                catch (Exception) { }
                throw;
            }
        }

        /// <summary>Explicitly loads and caches immutable MDF 3.x/4.00/4.10/4.11/4.20 metadata, never sample payloads.</summary>
        /// <param name="cancellationToken">Cooperative synchronous cancellation, including cache hits.</param>
        /// <returns>A document-owned catalog; successful snapshots survive reader disposal.</returns>
        /// <remarks>Failures do not publish a partial catalog or close the reader; spent resource budgets are not rolled back.
        /// MDF 3 headerless record extents are validated here without reading samples; MDF 4 indexed storage validation is lazy.</remarks>
        public IReadOnlyList<MdfDataGroup> ReadMetadata(CancellationToken cancellationToken = default(CancellationToken))
        {
            EnterOperation(cancellationToken);
            try
            {
                if (catalog != null) { return catalog; }
                budget.ReserveMetadata(4096, 64);
                IReadOnlyList<MdfDataGroup> result = Header.Version[0] == '3'
                    ? new Formats.Mdf3.MetadataReader(Resolver, Limits, budget, Header, document, cancellationToken).Read()
                    : new Formats.Mdf4.MetadataReader(Resolver, Limits, budget, document, Header.Version == "4.20", cancellationToken).Read();
                CheckOpen();
                cancellationToken.ThrowIfCancellationRequested();
                catalog = result;
                return catalog;
            }
            finally { ExitOperation(); }
        }

        /// <summary>Streams selected scalar/fixed-array channels over a record-row range in independent bounded batches.</summary>
        /// <param name="group">The same group instance returned by this reader's catalog.</param>
        /// <param name="channelIndices">Nonempty, distinct zero-based channel indices, in desired column order; snapshotted eagerly.</param>
        /// <param name="startSample">First row index, not a timestamp; may equal SampleCount for an empty range.</param>
        /// <param name="sampleCount">Number of rows, or UInt64.MaxValue for all remaining rows.</param>
        /// <param name="batchSize">Positive desired row count; resource limits can reduce actual batches.</param>
        /// <param name="representation">Raw exact-kind values, or explicit identity/linear physical values.</param>
        /// <param name="cancellationToken">Cooperative cancellation during synchronous I/O and decoding.</param>
        /// <returns>A lazy repeatable enumerable. Each enumeration holds the reader's operation gate until completed or disposed.</returns>
        /// <remarks>No rows are dropped for invalidity. An array occupies one logical column and includes every element;
        /// use the batch's explicit element getter. Output limits account for the sum of selected element counts.
        /// Arguments/capabilities are checked now; storage/integrity errors occur during enumeration.
        /// For interleaved records, each nonempty enumeration validates all DG record IDs/counts before its first batch,
        /// then scans to the selected CG-local ordinal range; it does not sort by timestamp or index all records.
        /// DZ blocks needed for this ID scan are decoded under the same whole-block limits even for unselected CGs.
        /// One active I/O operation per reader is allowed. Dispose enumerators when stopping early.</remarks>
        public IEnumerable<MdfSampleBatch> ReadSamples(MdfChannelGroup group, IReadOnlyList<int> channelIndices,
            ulong startSample = 0, ulong sampleCount = ulong.MaxValue, int batchSize = 1024,
            MdfValueRepresentation representation = MdfValueRepresentation.Raw, CancellationToken cancellationToken = default(CancellationToken))
        {
            CheckOpen();
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref activeOperation) != 0) { throw new InvalidOperationException("Another reader operation/enumeration is active."); }
            if (group == null) { throw new ArgumentNullException(nameof(group)); }
            if (!ReferenceEquals(group.Document, document)) { throw new ArgumentException("The group belongs to another document.", nameof(group)); }
            SampleRequest request = SampleRequest.Create(Limits, group, channelIndices, startSample, sampleCount, batchSize, representation, cancellationToken);
            CheckOpen();
            return new RecordSource(this, input, Resolver, budget).Read(request);
        }

        /// <summary>explicitly loads and caches the document model beyond data groups (header comment, FH, AT, EV, CH).</summary>
        /// <param name="cancellationToken">Cooperative synchronous cancellation, including cache hits.</param>
        /// <returns>A document-owned snapshot. MDF 3 provides only its header comment.</returns>
        /// <remarks>Loads the metadata catalog first (see <see cref="ReadMetadata"/>); event scopes and hierarchy elements resolve only
        /// to catalog groups/channels. Attachment payloads are not read here. Failures do not publish a partial model.</remarks>
        /// <exception cref="MdfFormatException">A model block or reference is malformed or does not resolve.</exception>
        /// <exception cref="MdfResourceLimitException">A configured resource limit is exceeded.</exception>
        public MdfFileModel ReadFileModel(CancellationToken cancellationToken = default(CancellationToken))
        {
            IReadOnlyList<MdfDataGroup> groups = ReadMetadata(cancellationToken);
            EnterOperation(cancellationToken);
            try
            {
                if (fileModel != null) { return fileModel; }
                budget.ReserveMetadata(1024, 64);
                MdfFileModel result = Header.Version[0] == '3'
                    ? Formats.Mdf3.FileModelReader.Read(Resolver, Limits, budget, Header, cancellationToken)
                    : new Formats.Mdf4.FileModelReader(Resolver, Limits, budget, document, groups, Header.Version == "4.20", cancellationToken).Read();
                CheckOpen();
                cancellationToken.ThrowIfCancellationRequested();
                fileModel = result;
                return fileModel;
            }
            finally { ExitOperation(); }
        }

        /// <summary>reads one embedded attachment payload, inflating compressed data with the managed zlib decoder.</summary>
        /// <param name="attachment">An attachment of this reader's file model.</param>
        /// <param name="verifyChecksum">Whether to compare a declared-valid MD5 checksum (managed implementation) with the original payload.</param>
        /// <param name="cancellationToken">Cooperative synchronous cancellation.</param>
        /// <returns>A new array holding the original (uncompressed) payload.</returns>
        /// <remarks>Embedded and original sizes must each fit MaximumReadBufferBytes. Nothing is cached.</remarks>
        /// <exception cref="NotSupportedException">The attachment is external; its path is never opened.</exception>
        /// <exception cref="MdfFormatException">The payload is truncated, its zlib data is invalid (MDF015) or the checksum differs (MDF016).</exception>
        /// <exception cref="MdfResourceLimitException">A declared size exceeds MaximumReadBufferBytes.</exception>
        public byte[] ReadAttachment(MdfAttachment attachment, bool verifyChecksum = true, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (attachment == null) { throw new ArgumentNullException(nameof(attachment)); }
            CheckOpen();
            if (!ReferenceEquals(attachment.Document, document)) { throw new ArgumentException("The attachment belongs to another document.", nameof(attachment)); }
            if (!attachment.IsEmbedded) { throw new NotSupportedException("External attachment files are never opened by the reader."); }
            EnterOperation(cancellationToken);
            try
            {
                ulong maximum = (ulong)Limits.MaximumReadBufferBytes;
                ulong needed = Math.Max(attachment.EmbeddedSize, attachment.IsCompressed ? attachment.OriginalSize : 0);
                if (needed > maximum)
                { throw new MdfResourceLimitException(nameof(MdfReaderOptions.MaximumReadBufferBytes), maximum, needed, attachment.BlockAddress); }
                byte[] stored = new byte[(int)attachment.EmbeddedSize];
                FormatInput.ReadAt(input, attachment.DataOffset, stored, stored.Length, "##AT", cancellationToken);
                byte[] result = stored;
                Action checkpoint = () => { CheckOpen(); cancellationToken.ThrowIfCancellationRequested(); };
                if (attachment.IsCompressed)
                {
                    try { result = Compression.ZlibInflater.Inflate(stored, (int)attachment.OriginalSize, (int)maximum, checkpoint); }
                    catch (InvalidDataException error)
                    { throw new MdfFormatException("Invalid zlib attachment data, declared original size or checksum.", "MDF015", attachment.BlockAddress, "##AT", error); }
                }
                if (verifyChecksum && attachment.HasChecksum)
                {
                    byte[] actual = Md5.Hash(result, result.Length, checkpoint);
                    byte[] expected = attachment.GetChecksum();
                    for (int i = 0; i < actual.Length; i++)
                    {
                        if (actual[i] != expected[i])
                        { throw new MdfFormatException("The attachment payload does not match its MD5 checksum.", "MDF016", attachment.BlockAddress, "##AT"); }
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }
            finally { ExitOperation(); }
        }

        /// <summary>explicitly loads and caches the sample reductions (SR chain) of one channel group.</summary>
        /// <param name="group">The same group instance returned by this reader's catalog.</param>
        /// <param name="cancellationToken">Cooperative synchronous cancellation, including cache hits.</param>
        /// <returns>A group-owned snapshot in chain order; empty when the group has no SR blocks.</returns>
        /// <remarks>Reduction data is not read here (MDF 3 raw ranges are bounds-checked). Failures do not publish a partial chain.</remarks>
        /// <exception cref="MdfFormatException">An SR block or its chain is malformed.</exception>
        /// <exception cref="MdfResourceLimitException">A configured resource limit is exceeded.</exception>
        public IReadOnlyList<MdfSampleReduction> ReadSampleReductions(MdfChannelGroup group, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (group == null) { throw new ArgumentNullException(nameof(group)); }
            CheckOpen();
            if (!ReferenceEquals(group.Document, document)) { throw new ArgumentException("The group belongs to another document.", nameof(group)); }
            EnterOperation(cancellationToken);
            try
            {
                if (group.SampleReductions != null) { return group.SampleReductions; }
                IReadOnlyList<MdfSampleReduction> result = Formats.SampleReductionReader.Read(Resolver, budget, document, group,
                    Header.Version[0] != '3', cancellationToken);
                CheckOpen();
                cancellationToken.ThrowIfCancellationRequested();
                group.SampleReductions = result;
                return result;
            }
            finally { ExitOperation(); }
        }

        /// <summary>streams the mean/minimum/maximum values of selected channels over a range of reduction cycles.</summary>
        /// <param name="reduction">A reduction returned by <see cref="ReadSampleReductions"/> of this reader.</param>
        /// <param name="channelIndices">Nonempty, distinct zero-based channel indices of the reduction's group, in column order.</param>
        /// <param name="startCycle">First cycle index; may equal CycleCount for an empty range.</param>
        /// <param name="cycleCount">Number of cycles, or UInt64.MaxValue for all remaining cycles.</param>
        /// <param name="batchSize">Positive desired cycle count per batch; resource limits can reduce actual batches.</param>
        /// <param name="representation">Raw exact-kind values, or physical values through each channel's conversion.</param>
        /// <param name="cancellationToken">Cooperative cancellation during synchronous I/O and decoding.</param>
        /// <returns>A lazy repeatable enumerable holding the reader's operation gate while enumerating.</returns>
        /// <remarks>Each part decodes exactly like a record of the group. Without stored invalidation bytes every value is valid;
        /// otherwise the one stored section applies to all three parts. Output limits account for all three parts.</remarks>
        /// <exception cref="NotSupportedException">The reduction, group or a selected channel (VLSD, virtual) cannot be reduced.</exception>
        public IEnumerable<MdfSampleReductionBatch> ReadSampleReduction(MdfSampleReduction reduction, IReadOnlyList<int> channelIndices,
            ulong startCycle = 0, ulong cycleCount = ulong.MaxValue, int batchSize = 1024,
            MdfValueRepresentation representation = MdfValueRepresentation.Raw, CancellationToken cancellationToken = default(CancellationToken))
        {
            CheckOpen();
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref activeOperation) != 0) { throw new InvalidOperationException("Another reader operation/enumeration is active."); }
            if (reduction == null) { throw new ArgumentNullException(nameof(reduction)); }
            if (!ReferenceEquals(reduction.Document, document)) { throw new ArgumentException("The reduction belongs to another document.", nameof(reduction)); }
            if (!reduction.CanReadValues) { throw new NotSupportedException(reduction.UnsupportedReason); }
            MdfChannelGroup group = reduction.Group;
            if (channelIndices != null)
            {
                foreach (int index in channelIndices)
                {
                    if (index < 0 || index >= group.Channels.Count) { continue; } // Reported by the common request validation.
                    Reading.ChannelLayout layout = group.Channels[index].Layout;
                    if (layout != null && (layout.Virtual || layout.TextStorage == 2))
                    { throw new NotSupportedException("Virtual and VLSD channels have no reduction values."); }
                }
            }
            SampleRequest request = SampleRequest.Create(Limits, group, channelIndices, startCycle, cycleCount, batchSize, representation,
                cancellationToken, reduction.CycleCount, SampleReductionSource.InputBytes(reduction), 3);
            CheckOpen();
            return new SampleReductionSource(this, input, Resolver, budget).Read(request, reduction);
        }

        internal void CheckOpen()
        {
            if (disposed) { throw new ObjectDisposedException(nameof(MdfReader)); }
        }

        internal void EnterOperation(CancellationToken token)
        {
            CheckOpen();
            token.ThrowIfCancellationRequested();
            if (Interlocked.CompareExchange(ref activeOperation, 1, 0) != 0)
            {
                throw new InvalidOperationException("Another reader operation/enumeration is active.");
            }
            try { CheckOpen(); token.ThrowIfCancellationRequested(); }
            catch { ExitOperation(); throw; }
        }

        internal void ExitOperation() { Interlocked.Exchange(ref activeOperation, 0); }

        /// <summary>Checks only the eight-byte MDF signature, preserving position and caller ownership.</summary>
        /// <param name="stream">Readable, seekable source.</param>
        /// <returns>Whether an MDF or unfinalized-MDF signature is present; not a conformance or version check.</returns>
        /// <remarks>I/O and position-restoration failures propagate; unknown revisions can return true.</remarks>
        public static bool IsMdfFile(Stream stream)
        {
            using (BinaryInput input = new BinaryInput(stream, true))
            {
                long initial = stream.Position;
                if (initial < 0) { throw new IOException("The stream reported a negative position."); }
                try
                {
                    if (input.Length < 8) { return false; }
                    input.Seek(0);
                    byte[] signature = input.ReadBytes(8);
                    return IdentificationBlock.HasSignature(signature);
                }
                finally
                {
                    if (stream.Seek(initial, SeekOrigin.Begin) != initial)
                    {
                        throw new IOException("The probe could not restore the source position.");
                    }
                }
            }
        }

        /// <summary>Closes an owned source exactly once; materialized header/catalog/batches remain available.</summary>
        public void Dispose()
        {
            if (disposed) { return; }
            disposed = true;
            input.Dispose();
        }
    }
}
