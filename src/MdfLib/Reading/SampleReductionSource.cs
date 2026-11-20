// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using MdfLib.Formats;
using MdfLib.Internal;
using MdfLib.IO;

namespace MdfLib.Reading
{
    // streams reduction cycles. Each stored reduction record (mean, minimum, maximum record parts and the optional shared
    // invalidation section) is re-framed as three ordinary CG records, which then decode exactly like ReadSamples rows.
    internal sealed class SampleReductionSource
    {
        private readonly MdfReader reader;
        private readonly BinaryInput input;
        private readonly BlockResolver resolver;
        private readonly ReadBudget budget;

        internal SampleReductionSource(MdfReader reader, BinaryInput input, BlockResolver resolver, ReadBudget budget)
        {
            this.reader = reader;
            this.input = input;
            this.resolver = resolver;
            this.budget = budget;
        }

        // Per-cycle input: the stored reduction record plus one re-framed CG record.
        internal static ulong InputBytes(MdfSampleReduction reduction)
        {
            RecordLayout layout = reduction.Group.Layout;
            return reduction.RecordBytes + layout.DataBytes + layout.InvalidationBytes;
        }

        internal IEnumerable<MdfSampleReductionBatch> Read(SampleRequest request, MdfSampleReduction reduction)
        {
            reader.EnterOperation(request.Token);
            try
            {
                if (request.Count == 0) { yield break; }
                RecordLayout layout = reduction.Group.Layout;
                int dataBytes = (int)layout.DataBytes; // The positive Int32 read limit bounded InputBytes eagerly.
                int invalidationBytes = (int)layout.InvalidationBytes;
                int partBytes = dataBytes + invalidationBytes;
                int recordSize = (int)reduction.RecordBytes;
                bool storedInvalidation = reduction.HasInvalidationBytes;
                Mdf4DataCursor cursor = null;
                Mdf4ListDataCursor paired = null;
                if (!reduction.RawData)
                {
                    bool listData = layout.IsMdf420 && (reduction.ListDataMap != null
                        || Mdf4ListDataMap.IsListRoot(resolver, reduction.DataAddress, request.Token));
                    if (listData)
                    {
                        if (reduction.ListDataMap == null)
                        {
                            Mdf4ListDataMap map = Mdf4ListDataMap.Build(resolver, reader.Limits, budget, reduction.DataAddress, reduction.CycleCount,
                                3U * layout.DataBytes, storedInvalidation ? layout.InvalidationBytes : 0U, request.Token, "RV", "RI");
                            reader.CheckOpen();
                            request.Token.ThrowIfCancellationRequested();
                            reduction.ListDataMap = map;
                        }
                        budget.CheckDepth(reduction.ListDataMap.Values.MaximumDepth, reduction.DataAddress);
                        paired = new Mdf4ListDataCursor(reader, input, reduction.ListDataMap, 3U * layout.DataBytes,
                            storedInvalidation ? layout.InvalidationBytes : 0U, request.Token);
                    }
                    else
                    {
                        if (reduction.DataMap == null)
                        {
                            Mdf4DataMap map = Mdf4DataMap.Build(resolver, reader.Limits, budget, reduction.DataAddress, reduction.CycleCount,
                                reduction.RecordBytes, request.Token, false, "RD");
                            reader.CheckOpen();
                            request.Token.ThrowIfCancellationRequested();
                            reduction.DataMap = map;
                        }
                        budget.CheckDepth(reduction.DataMap.MaximumDepth, reduction.DataAddress);
                        cursor = new Mdf4DataCursor(reader, input, reduction.DataMap, request.Token);
                    }
                }
                byte[] stored = new byte[checked(recordSize * request.RowsPerBatch)];
                byte[] framed = new byte[checked(partBytes * request.RowsPerBatch)];
                ulong index = request.Start;
                ulong remaining = request.Count;
                while (remaining != 0)
                {
                    reader.CheckOpen();
                    request.Token.ThrowIfCancellationRequested();
                    int rows = (int)Math.Min(remaining, (ulong)request.RowsPerBatch);
                    int bytes = checked(rows * recordSize);
                    if (paired != null) { paired.ReadRows(index, rows, stored, recordSize); }
                    else if (cursor != null) { cursor.Read(index * reduction.RecordBytes, stored, bytes); }
                    else { FormatInput.ReadAt(input, reduction.DataAddress + index * reduction.RecordBytes, stored, bytes, "SR", request.Token); }
                    MdfSampleBatch[] parts = new MdfSampleBatch[3];
                    for (int part = 0; part < 3; part++)
                    {
                        for (int row = 0; row < rows; row++)
                        {
                            int source = row * recordSize;
                            int target = row * partBytes;
                            Buffer.BlockCopy(stored, source + part * dataBytes, framed, target, dataBytes);
                            if (invalidationBytes == 0) { continue; }
                            if (storedInvalidation) { Buffer.BlockCopy(stored, source + 3 * dataBytes, framed, target + dataBytes, invalidationBytes); }
                            else { Array.Clear(framed, target + dataBytes, invalidationBytes); } // No section: every value is valid.
                        }
                        parts[part] = Decode(request, layout, framed, partBytes, rows, index);
                    }
                    yield return new MdfSampleReductionBatch(parts[0], parts[1], parts[2]);
                    reader.CheckOpen();
                    request.Token.ThrowIfCancellationRequested();
                    index += (ulong)rows;
                    remaining -= (ulong)rows;
                }
            }
            finally
            {
                reader.ExitOperation();
            }
        }

        private static MdfSampleBatch Decode(SampleRequest request, RecordLayout layout, byte[] buffer, int recordSize, int rows, ulong firstCycle)
        {
            MdfSampleValue[] values = new MdfSampleValue[checked(rows * request.ValuesPerRow)];
            int columns = request.Channels.Count;
            for (int row = 0; row < rows; row++)
            {
                int recordOffset = row * recordSize;
                for (int column = 0; column < columns; column++)
                {
                    int first = request.ValueOffsets == null ? column : request.ValueOffsets[column];
                    int count = request.ValueOffsets == null ? 1 : request.ValueOffsets[column + 1] - first;
                    MdfChannel channel = request.Channels[column];
                    int target = row * request.ValuesPerRow + first;
                    for (int element = 0; element < count; element++)
                    {
                        request.Token.ThrowIfCancellationRequested();
                        if (channel.Layout.TextStorage == 0)
                        {
                            values[target + element] = ScalarDecoder.Read(buffer, recordOffset, layout, channel, request.Representation,
                                firstCycle + (ulong)row, element);
                            continue;
                        }
                        MdfSampleValue text = channel.Layout.TextStorage == 1 ? TextDecoder.Fixed(buffer, recordOffset, layout, channel.Layout, element)
                            : TextDecoder.MaxLength(buffer, recordOffset, layout, channel.Layout);
                        values[target + element] = request.Representation == MdfValueRepresentation.Physical && PhysicalConversion.TakesText(channel.Conversion.Kind)
                            ? PhysicalConversion.FromText(channel.Conversion, text) : text;
                    }
                }
            }
            return new MdfSampleBatch(firstCycle, rows, request.Channels, values, request.ValuesPerRow, request.ValueOffsets);
        }
    }
}
