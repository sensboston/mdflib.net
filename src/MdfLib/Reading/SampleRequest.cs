// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Threading;
using MdfLib.Internal;

namespace MdfLib.Reading
{
    internal sealed class SampleRequest
    {
        private SampleRequest() { }
        internal MdfChannelGroup Group { get; private set; }
        internal IReadOnlyList<MdfChannel> Channels { get; private set; }
        internal ulong Start { get; private set; }
        internal ulong Count { get; private set; }
        internal int RowsPerBatch { get; private set; }
        internal int ValuesPerRow { get; private set; }
        internal int[] ValueOffsets { get; private set; }
        internal MdfValueRepresentation Representation { get; private set; }
        // output accounting: planned header and per-row bytes; VLSD payload (3 bytes per stored byte) is data-dependent.
        internal ulong HeaderBytes { get; private set; }
        internal ulong RowBytes { get; private set; }
        internal bool HasSignalData { get; private set; }
        internal CancellationToken Token { get; private set; }

        internal static SampleRequest Create(ReaderLimits limits, MdfChannelGroup group, IReadOnlyList<int> channelIndices,
            ulong startSample, ulong sampleCount, int batchSize, MdfValueRepresentation representation, CancellationToken token)
        {
            return Create(limits, group, channelIndices, startSample, sampleCount, batchSize, representation, token,
                group.SampleCount, group.RecordByteCount, 1);
        }

        // `available` rows of `inputRecordBytes` input bytes each, producing `copies` value sets per row
        // (three for sample-reduction mean/minimum/maximum). With (SampleCount, RecordByteCount, 1) this is the record request.
        internal static SampleRequest Create(ReaderLimits limits, MdfChannelGroup group, IReadOnlyList<int> channelIndices,
            ulong startSample, ulong sampleCount, int batchSize, MdfValueRepresentation representation, CancellationToken token,
            ulong available, ulong inputRecordBytes, ulong copies)
        {
            if (channelIndices == null) { throw new ArgumentNullException(nameof(channelIndices)); }
            int selected = channelIndices.Count;
            if (selected < 1 || selected > group.Channels.Count) { throw new ArgumentException("Select at least one distinct channel within the group.", nameof(channelIndices)); }
            if (batchSize <= 0) { throw new ArgumentOutOfRangeException(nameof(batchSize)); }
            if (representation != MdfValueRepresentation.Raw && representation != MdfValueRepresentation.Physical)
            { throw new ArgumentOutOfRangeException(nameof(representation)); }
            if (startSample > available) { throw new ArgumentOutOfRangeException(nameof(startSample)); }
            ulong remaining = available - startSample;
            if (sampleCount == ulong.MaxValue) { sampleCount = remaining; }
            else if (sampleCount > remaining) { throw new ArgumentOutOfRangeException(nameof(sampleCount)); }
            if (!group.CanReadSamples) { throw new NotSupportedException(group.UnsupportedReason); }
            ulong offset = group.Layout.BlockAddress;
            ulong headerBytes = 128UL + 32UL * (ulong)selected;
            // Bound the selection snapshots before allocating, even for an empty range.
            ReadBudget.Check(nameof(MdfReaderOptions.MaximumSampleBatchBytes), (ulong)limits.MaximumSampleBatchBytes,
                headerBytes, sampleCount == 0 ? 0 : 40UL * (ulong)selected, offset);
            MdfChannel[] channels = new MdfChannel[selected];
            HashSet<int> unique = new HashSet<int>();
            bool arrays = false, signalData = false;
            ulong elements = 0;
            ulong textBytes = 0; // 64 + 3*n per Text/ByteArray value with a known fixed width; VLSD plans 64 now.
            for (int index = 0; index < selected; index++)
            {
                token.ThrowIfCancellationRequested();
                int channelIndex = channelIndices[index];
                if (channelIndex < 0 || channelIndex >= group.Channels.Count) { throw new ArgumentOutOfRangeException(nameof(channelIndices)); }
                if (!unique.Add(channelIndex)) { throw new ArgumentException("Duplicate channel indices are not allowed.", nameof(channelIndices)); }
                MdfChannel channel = group.Channels[channelIndex];
                if (!channel.CanReadRaw) { throw new NotSupportedException(channel.UnsupportedReason); }
                if (representation == MdfValueRepresentation.Physical && !channel.CanReadPhysical)
                { throw new NotSupportedException("The selected channel has an unsupported physical conversion; Raw is available."); }
                ulong count = channel.Array == null ? 1 : channel.Array.ElementCount;
                if (elements > ulong.MaxValue - count)
                { throw new MdfResourceLimitException(nameof(MdfReaderOptions.MaximumSampleBatchBytes), (ulong)limits.MaximumSampleBatchBytes, ulong.MaxValue, offset); }
                elements += count;
                arrays |= channel.Array != null;
                if (channel.Layout != null && channel.Layout.TextStorage != 0)
                {
                    signalData |= channel.Layout.TextStorage == 2;
                    ulong fixedText = 64UL + (channel.Layout.TextStorage != 2 ? 3UL * channel.Layout.FieldBytes : 0); // Bounded by uint widths.
                    // a fixed-length text array charges every element (saturating).
                    textBytes = count > (ulong.MaxValue - textBytes) / fixedText ? ulong.MaxValue : textBytes + count * fixedText;
                }
                if (representation == MdfValueRepresentation.Physical && PhysicalConversion.HasTextOutput(channel.Conversion.Kind))
                {
                    // each converted value may carry the longest referenced text (shared bytes, decoded string).
                    ulong each = 64UL + 3UL * (ulong)channel.Conversion.MaximumTextBytes;
                    textBytes = count > (ulong.MaxValue - textBytes) / each ? ulong.MaxValue : textBytes + count * each; // Saturates.
                }
                channels[index] = channel;
            }
            if (arrays) { headerBytes += 64UL + 8UL * ((ulong)selected + 1); }
            // Saturation is diagnostic only: the positive Int32 byte cap rejects it.
            ulong rowBytes = sampleCount == 0 ? 0 : elements > (ulong.MaxValue - textBytes) / 40 ? ulong.MaxValue : elements * 40 + textBytes;
            ulong outputHeader = headerBytes * copies; // Small: bounded by the Int32 cap check above times three at most.
            ulong outputRow = rowBytes > ulong.MaxValue / copies ? ulong.MaxValue : rowBytes * copies;
            ReadBudget.Check(nameof(MdfReaderOptions.MaximumSampleBatchBytes), (ulong)limits.MaximumSampleBatchBytes, outputHeader, outputRow, offset);
            ulong rows = 1;
            int valuesPerRow = 0;
            int[] offsets = null;
            if (sampleCount != 0)
            {
                ReadBudget.Check(nameof(MdfReaderOptions.MaximumReadBufferBytes), (ulong)limits.MaximumReadBufferBytes, 0, inputRecordBytes, offset);
                ulong outputRows = ((ulong)limits.MaximumSampleBatchBytes - outputHeader) / outputRow;
                ulong inputRows = (ulong)limits.MaximumReadBufferBytes / inputRecordBytes;
                rows = Math.Min((ulong)batchSize, Math.Min(inputRows, outputRows));
                rows = Math.Min(rows, sampleCount);
                valuesPerRow = (int)elements; // One output row was bounded by the positive Int32 byte cap.
                if (arrays)
                {
                    offsets = new int[selected + 1];
                    for (int index = 0; index < selected; index++)
                    {
                        token.ThrowIfCancellationRequested();
                        offsets[index + 1] = offsets[index] + (channels[index].Array == null ? 1 : (int)channels[index].Array.ElementCount);
                    }
                }
            }
            // No element product is converted/materialized for an empty range.
            return new SampleRequest
            {
                Group = group, Channels = Array.AsReadOnly(channels), Start = startSample, Count = sampleCount,
                RowsPerBatch = (int)rows, ValuesPerRow = valuesPerRow, ValueOffsets = offsets,
                Representation = representation, Token = token, HeaderBytes = headerBytes, RowBytes = rowBytes, HasSignalData = signalData
            };
        }
    }
}
