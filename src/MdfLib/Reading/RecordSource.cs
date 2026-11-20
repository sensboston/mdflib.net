// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using MdfLib.Formats;
using MdfLib.IO;
using MdfLib.Internal;

namespace MdfLib.Reading
{
    internal sealed class RecordSource
    {
        private readonly MdfReader reader;
        private readonly BinaryInput input;
        private readonly BlockResolver resolver;
        private readonly ReadBudget budget;

        internal RecordSource(MdfReader reader, BinaryInput input, BlockResolver resolver, ReadBudget budget)
        {
            this.reader = reader;
            this.input = input;
            this.resolver = resolver;
            this.budget = budget;
        }

        internal IEnumerable<MdfSampleBatch> Read(SampleRequest request)
        {
            reader.EnterOperation(request.Token);
            try
            {
                if (request.Count == 0) { yield break; }
                MdfChannelGroup group = request.Group;
                RecordLayout layout = group.Layout;
                if (layout.DataAddress == 0)
                {
                    throw new MdfFormatException("A nonempty measurement has no data range.", "MDF013", layout.BlockAddress, layout.HeaderlessData ? "CG" : "##CG");
                }
                ulong recordBytes = group.RecordByteCount;
                InterleavedLayout interleaved = layout.Interleaved;
                ulong dataOffset = layout.DataAddress;
                string dataType = "DT";
                Mdf4DataCursor cursor = null;
                Mdf4ListDataCursor paired = null;
                bool listData = false;
                if (layout.IsMdf420)
                {
                    listData = layout.ListDataMap != null || Mdf4ListDataMap.IsListRoot(resolver, layout.DataAddress, request.Token);
                    if (listData && interleaved != null)
                    { throw new NotSupportedException("LD storage is not supported for unsorted MDF 4.20 data groups with record IDs."); }
                    if (layout.HasRemoteMaster && !listData)
                    { throw new NotSupportedException("Remote-master channel group storage must be LD in the MDF 4.20 subset."); }
                }
                if (listData)
                {
                    if (layout.ListDataMap == null)
                    {
                        Mdf4ListDataMap map = Mdf4ListDataMap.Build(resolver, reader.Limits, budget, layout.DataAddress,
                            group.SampleCount, layout.DataBytes, layout.InvalidationBytes, request.Token);
                        reader.CheckOpen();
                        request.Token.ThrowIfCancellationRequested();
                        layout.ListDataMap = map;
                    }
                    budget.CheckDepth(layout.ListDataMap.Values.MaximumDepth, layout.DataAddress);
                    paired = new Mdf4ListDataCursor(reader, input, layout, request.Token);
                }
                else if (layout.HeaderlessData)
                {
                    if (interleaved == null && group.SampleCount > ulong.MaxValue / recordBytes)
                    {
                        throw new MdfFormatException("Declared raw record range overflows.", "MDF013", layout.BlockAddress, "CG");
                    }
                    // Metadata reserved this complete DG extent; revalidate even on repeat enumeration.
                    resolver.RegisterRawDataRange(dataOffset, interleaved == null ? group.SampleCount * recordBytes : interleaved.TotalBytes, request.Token);
                }
                else
                {
                    Mdf4DataMap map = interleaved == null ? layout.DataMap : interleaved.DataMap;
                    if (map == null)
                    {
                        // VLSD CG records have no metadata-derived extent; the exact two-pass scan proves the stream end.
                        map = interleaved != null && interleaved.HasVariableRecords
                            ? Mdf4DataMap.BuildStream(resolver, reader.Limits, budget, layout.DataAddress, "DT", request.Token, layout.IsMdf420)
                            : Mdf4DataMap.Build(resolver, reader.Limits, budget, layout.DataAddress,
                            interleaved == null ? group.SampleCount : interleaved.TotalBytes, interleaved == null ? recordBytes : 1, request.Token,
                            layout.IsMdf420); // DL summaries only in exact 4.20 record data.
                        reader.CheckOpen();
                        request.Token.ThrowIfCancellationRequested();
                        if (interleaved == null) { layout.DataMap = map; }
                        else { interleaved.DataMap = map; }
                    }
                    budget.CheckDepth(map.MaximumDepth, layout.DataAddress);
                    cursor = new Mdf4DataCursor(reader, input, map, request.Token);
                }
                InterleavedCursor scan = interleaved == null ? null : new InterleavedCursor(reader, input, layout, cursor, request.Token);
                if (scan != null) { scan.Validate(); } // Exact framing/counts before allocating a batch or publishing its first row.
                int recordSize = (int)recordBytes; // Eager request validation bounded one record by the positive int read limit.
                SignalDataReader[] signals = request.HasSignalData ? SignalReaders(request) : null;
                byte[] buffer = new byte[checked(recordSize * request.RowsPerBatch)];
                ulong index = request.Start;
                ulong remaining = request.Count;
                int carried = 0; // Records already read after an early VLSD batch split; sequential scans never rewind.
                while (remaining != 0)
                {
                    reader.CheckOpen();
                    request.Token.ThrowIfCancellationRequested();
                    int rows = (int)Math.Min(remaining, (ulong)request.RowsPerBatch);
                    int fresh = rows - carried;
                    ulong next = index + (ulong)carried;
                    if (fresh > 0)
                    {
                        int bytes = checked(fresh * recordSize);
                        if (scan != null) { scan.ReadRows(group.Index, next, fresh, buffer, recordSize, carried); }
                        else if (paired != null) { paired.ReadRows(next, fresh, buffer, recordSize, carried); }
                        else if (cursor == null)
                        {
                            ulong address = dataOffset + next * recordBytes; // Proven within the MDF 3 raw source extent.
                            FormatInput.ReadAt(input, address, buffer, bytes, dataType, request.Token, carried * recordSize);
                        }
                        else { cursor.Read(next * recordBytes, buffer, bytes, carried * recordSize); }
                    }
                    reader.CheckOpen();
                    request.Token.ThrowIfCancellationRequested();
                    MdfSampleValue[] values = new MdfSampleValue[checked(rows * request.ValuesPerRow)];
                    int decoded = Decode(request, layout, buffer, recordSize, rows, values, signals, index);
                    if (decoded < rows)
                    {
                        Array.Resize(ref values, checked(decoded * request.ValuesPerRow));
                        carried = rows - decoded;
                        Buffer.BlockCopy(buffer, decoded * recordSize, buffer, 0, carried * recordSize);
                    }
                    else { carried = 0; }
                    yield return new MdfSampleBatch(index, decoded, request.Channels, values, request.ValuesPerRow, request.ValueOffsets);
                    // Even the MoveNext following the last batch must observe disposal/cancellation.
                    reader.CheckOpen();
                    request.Token.ThrowIfCancellationRequested();
                    index += (ulong)decoded;
                    remaining -= (ulong)decoded;
                }
            }
            finally
            {
                reader.ExitOperation();
            }
        }

        private SignalDataReader[] SignalReaders(SampleRequest request)
        {
            SignalDataReader[] readers = new SignalDataReader[request.Channels.Count];
            for (int column = 0; column < readers.Length; column++)
            {
                request.Token.ThrowIfCancellationRequested();
                ChannelLayout channel = request.Channels[column].Layout;
                if (channel.TextStorage != 2) { continue; }
                if (channel.SignalGroup != null)
                {
                    // Metadata proved the VLSD CG belongs to this DG's interleaved layout, whose map is already built.
                    InterleavedLayout shared = request.Group.Layout.Interleaved;
                    int member;
                    if (shared == null || channel.SignalGroup.Interleaved != shared || shared.DataMap == null || !shared.Find(channel.SignalGroup.RecordId, out member))
                    { throw new InvalidOperationException("A VLSD channel group is not part of the selected data group's framing."); }
                    Mdf4DataCursor data = new Mdf4DataCursor(reader, input, shared.DataMap, request.Token);
                    InterleavedCursor frames = new InterleavedCursor(reader, input, request.Group.Layout, data, request.Token);
                    readers[column] = new SignalDataReader(reader.Limits, channel, data, frames, member);
                    continue;
                }
                if (channel.SignalData == null && channel.SignalDataAddress != 0)
                {
                    Mdf4DataMap map = Mdf4DataMap.BuildStream(resolver, reader.Limits, budget, channel.SignalDataAddress, "SD", request.Token,
                        listData: request.Group.Layout.IsMdf420); // LD signal data only in exact 4.20.
                    reader.CheckOpen();
                    request.Token.ThrowIfCancellationRequested();
                    channel.SignalData = map;
                }
                Mdf4DataCursor cursor = null;
                if (channel.SignalData != null)
                {
                    budget.CheckDepth(channel.SignalData.MaximumDepth, channel.SignalDataAddress);
                    cursor = new Mdf4DataCursor(reader, input, channel.SignalData, request.Token);
                }
                readers[column] = new SignalDataReader(reader.Limits, channel, cursor);
            }
            return readers;
        }

        // Returns the number of fully decoded rows; fewer than requested only when the next row's VLSD payload
        // would exceed the batch byte cap. A single row over the cap is refused before allocating its payloads.
        private int Decode(SampleRequest request, RecordLayout layout, byte[] buffer, int recordSize, int rows,
            MdfSampleValue[] values, SignalDataReader[] signals, ulong firstRecord)
        {
            ulong cap = (ulong)reader.Limits.MaximumSampleBatchBytes;
            ulong used = request.HeaderBytes;
            int columns = request.Channels.Count;
            ulong[] offsets = signals == null ? null : new ulong[columns];
            uint[] lengths = signals == null ? null : new uint[columns];
            bool[] valid = signals == null ? null : new bool[columns];
            for (int row = 0; row < rows; row++)
            {
                int recordOffset = row * recordSize;
                if (signals != null)
                {
                    ulong extra = 0;
                    for (int column = 0; column < columns; column++)
                    {
                        request.Token.ThrowIfCancellationRequested();
                        if (signals[column] == null) { continue; }
                        ChannelLayout channel = request.Channels[column].Layout;
                        valid[column] = TextDecoder.IsValid(buffer, recordOffset, layout, channel);
                        lengths[column] = 0;
                        if (!valid[column]) { continue; } // An invalid row's stored offset is never dereferenced.
                        offsets[column] = TextDecoder.Offset(buffer, recordOffset, channel);
                        lengths[column] = signals[column].Length(offsets[column]);
                        extra += 3UL * lengths[column];
                    }
                    ulong rowCost = request.RowBytes + extra;
                    if (used > cap || rowCost > cap - used)
                    {
                        if (row == 0)
                        {
                            throw new MdfResourceLimitException(nameof(MdfReaderOptions.MaximumSampleBatchBytes), cap,
                                request.HeaderBytes + rowCost, request.Group.Layout.BlockAddress);
                        }
                        return row;
                    }
                    used += rowCost;
                }
                for (int column = 0; column < columns; column++)
                {
                    int first = request.ValueOffsets == null ? column : request.ValueOffsets[column];
                    int count = request.ValueOffsets == null ? 1 : request.ValueOffsets[column + 1] - first;
                    MdfChannel channel = request.Channels[column];
                    int target = row * request.ValuesPerRow + first;
                    if (channel.Layout.TextStorage != 0)
                    {
                        for (int element = 0; element < count; element++) // only fixed-length text arrays have count > 1.
                        {
                            request.Token.ThrowIfCancellationRequested();
                            MdfSampleValue text = channel.Layout.TextStorage == 1 ? TextDecoder.Fixed(buffer, recordOffset, layout, channel.Layout, element)
                                : channel.Layout.TextStorage == 3 ? TextDecoder.MaxLength(buffer, recordOffset, layout, channel.Layout)
                                : valid[column] ? TextDecoder.Create(true, channel.Layout.DataType, signals[column].Payload(offsets[column], lengths[column]))
                                : TextDecoder.Empty(channel.Layout.DataType);
                            // text to value/text applies to the decoded text; byte arrays and identity pass through.
                            values[target + element] = request.Representation == MdfValueRepresentation.Physical && PhysicalConversion.TakesText(channel.Conversion.Kind)
                                ? PhysicalConversion.FromText(channel.Conversion, text) : text;
                        }
                        continue;
                    }
                    for (int element = 0; element < count; element++)
                    {
                        request.Token.ThrowIfCancellationRequested();
                        values[target + element] = ScalarDecoder.Read(buffer, recordOffset, layout, channel, request.Representation,
                            firstRecord + (ulong)row, element); // absolute record index for virtual channels.
                    }
                }
            }
            return rows;
        }
    }
}
