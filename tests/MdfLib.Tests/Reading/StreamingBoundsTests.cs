// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Linq;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Reading
{
    public sealed class StreamingBoundsTests
    {
        [Fact]
        public void MultiTerabyteVirtualDtReadsOnlyTheRequestedHighIndexRangeInBoundedBatches()
        {
            byte[] metadata = M3Wire.Read();
            const ulong cycles = (1UL << 34) + 17;
            const long dtAddress = 1L << 42;
            const ulong recordBytes = 47;
            ulong dataLength = cycles * recordBytes;
            M3Wire.Put(metadata, M3Wire.Link(M3Wire.Dg, 0), 0);
            M3Wire.Put(metadata, M3Wire.Link(M3Wire.Dg, 2), (ulong)dtAddress);
            M3Wire.Put(metadata, M3Wire.Payload(metadata, M3Wire.Cg) + 8, cycles);
            byte[] header = new byte[24];
            header[0] = header[1] = (byte)'#'; header[2] = (byte)'D'; header[3] = (byte)'T';
            M3Wire.Put(header, 8, 24 + dataLength);
            byte[] lastRecords = new byte[3 * (int)recordBytes];
            Array.Copy(metadata, M3Wire.Dt + 24, lastRecords, 0, lastRecords.Length);
            using (SparseReadStream source = new SparseReadStream(dtAddress + 24 + (long)dataLength))
            {
                source.Add(0, metadata);
                source.Add(dtAddress, header);
                source.Add(dtAddress + 24 + (long)((cycles - 3) * recordBytes), lastRecords);
                MdfReaderOptions options = new MdfReaderOptions { MaximumReadBufferBytes = 141, MaximumSampleBatchBytes = 240 };
                using (MdfReader reader = MdfReader.Open(source, true, options))
                {
                    MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                    Assert.Equal(cycles, group.SampleCount);
                    long before = source.BytesRead;
                    // Options were snapshotted by Open; mutating the caller's object must not change planning.
                    options.MaximumReadBufferBytes = 1; options.MaximumSampleBatchBytes = 1;
                    var request = reader.ReadSamples(group, new[] { 3 }, cycles - 3, 3, int.MaxValue);
                    Assert.Equal(before, source.BytesRead);
                    MdfSampleBatch[] batches = request.ToArray();
                    Assert.Equal(new[] { 2, 1 }, batches.Select(batch => batch.SampleCount));
                    Assert.Equal(cycles - 3, batches[0].FirstSampleIndex);
                    Assert.Equal(cycles - 1, batches[1].FirstSampleIndex);
                    Assert.Equal(0UL, batches[0].GetValue(0, 0).UnsignedValue);
                    Assert.Equal(9_007_199_254_740_993UL, batches[0].GetValue(1, 0).UnsignedValue);
                    Assert.Equal(1UL << 63, batches[1].GetValue(0, 0).UnsignedValue);
                    Assert.Equal(24L + lastRecords.Length, source.BytesRead - before);
                    Assert.InRange(source.LargestReadRequest, 1, 141);
                    before = source.BytesRead;
                    Assert.Equal(3, request.Sum(batch => batch.SampleCount));
                    Assert.Equal((long)lastRecords.Length, source.BytesRead - before); // Header cache, not sample-data cache.
                }
            }
        }

        [Fact]
        public void InputBudgetAlsoReducesBatchRowsWithoutUsingCumulativeMetadataAllowance()
        {
            byte[] bytes = M3Wire.Read();
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true, new MdfReaderOptions { MaximumReadBufferBytes = 94 }))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(new[] { 2, 2, 1 }, reader.ReadSamples(group, new[] { 0 }, batchSize: int.MaxValue).Select(batch => batch.SampleCount));
                for (int index = 0; index < 100; index++)
                {
                    Assert.Equal(5, reader.ReadSamples(group, new[] { 0 }).Sum(batch => batch.SampleCount));
                }
                Assert.InRange(source.LargestReadRequest, 1, 94);
                Assert.Equal(0, source.WriteCalls);
            }
        }
    }
}
