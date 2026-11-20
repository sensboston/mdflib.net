// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Reading
{
    public sealed class InterleavedBoundaryTests
    {
        [Theory]
        [InlineData("m5b-prefix-le330")]
        [InlineData("m5b-suffix-be330")]
        [InlineData("m5b-id1-dt410")]
        public void AOneCgDgStillHasItsDeclaredRecordIdFraming(string id)
        {
            byte[] bytes = M5BWire.Read(id);
            byte[] selected = TargetFrames(bytes);
            int cg = M5BWire.Cg(bytes);
            M5BWire.Put(bytes, cg + (M5BWire.Is3(bytes) ? 4 : 24), 0, M5BWire.Is3(bytes) ? 4 : 8);
            if (M5BWire.Is3(bytes))
            {
                M5BWire.Put(bytes, M5BWire.Dg(bytes) + 20, 1, 2);
                int raw = bytes.Length; Array.Resize(ref bytes, raw + selected.Length); Array.Copy(selected, 0, bytes, raw, selected.Length);
                M5BWire.Root(bytes, (ulong)raw);
            }
            else
            {
                int root = M5Wire.Add(ref bytes, "DT", new ulong[0], selected);
                M5BWire.Root(bytes, (ulong)root);
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = Assert.Single(Assert.Single(reader.ReadMetadata()).ChannelGroups);
                Assert.NotNull(group.Layout.Interleaved);
                Assert.Equal(new[] { 0UL, 9007199254740993UL, 1UL << 63, ulong.MaxValue, 17UL },
                    reader.ReadSamples(group, new[] { 1 }, batchSize: 2).SelectMany(batch => Enumerable.Range(0, batch.SampleCount).Select(row => batch.GetValue(row, 0).UnsignedValue)));
            }
        }

        [Theory]
        [InlineData("m5b-prefix-le330", false)]
        [InlineData("m5b-suffix-be330", true)]
        public void Mdf3HighUInt32RawAddressAndHugeSkippedFixedNeighborsUseNoNeighborBodyBuffer(string id, bool suffix)
        {
            byte[] metadata = M5BWire.Read(id);
            int frameSize = 23 + (suffix ? 1 : 0);
            byte[] selected = TargetFrames(metadata).Take(3 * frameSize).ToArray();
            const long raw = uint.MaxValue - 15L;
            const int neighbors = 256;
            int neighborFrame = 65536 + (suffix ? 1 : 0);
            long length = (long)neighbors * neighborFrame + selected.Length;
            M5BWire.Size(metadata, 1, ushort.MaxValue);
            M5BWire.Count(metadata, 0, 3); M5BWire.Count(metadata, 1, neighbors); M5BWire.Count(metadata, 2, 0);
            M5BWire.Root(metadata, (ulong)raw);
            using (SparseReadStream source = new SparseReadStream(raw + length))
            {
                source.Add(0, metadata);
                for (int index = 0; index < neighbors; index++)
                {
                    long start = raw + (long)index * neighborFrame;
                    source.Add(start, new byte[] { 255 });
                    if (suffix) { source.Add(start + 65536, new byte[] { 255 }); }
                }
                source.Add(raw + (long)neighbors * neighborFrame, selected);
                using (MdfReader reader = MdfReader.Open(source, true, new MdfReaderOptions { MaximumReadBufferBytes = 224 }))
                {
                    MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                    long before = source.BytesRead;
                    Assert.Equal(1UL << 63, reader.ReadSamples(group, new[] { 1 }, 2, 1).Single().GetValue(0, 0).UnsignedValue);
                    Assert.Equal(2L * (neighbors + 3) * (suffix ? 2 : 1) + 22, source.BytesRead - before);
                    Assert.InRange(source.LargestReadRequest, 1, 224);
                }
            }
        }

        [Fact]
        public void UInt64CycleCountDoesNotAllocateACycleSizedIndexBeforeCooperativeCancellation()
        {
            byte[] bytes = M5BWire.Read();
            int root = M5BWire.Root(bytes);
            const ulong count = (1UL << 32) + 17;
            M5BWire.Count(bytes, 0, count); M5BWire.Count(bytes, 1, 0); M5BWire.Count(bytes, 2, 0);
            M5BWire.Put(bytes, root + 8, count * 24 + 24);
            using (TestReadStream source = new TestReadStream(bytes) { ReportedLength = root + 24 + (long)(count * 24) })
            using (MdfReader reader = MdfReader.Open(source, true, new MdfReaderOptions { MaximumReadBufferBytes = 136 }))
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(count, group.SampleCount);
                source.AfterRead = () => { if (source.Position == root + 25) { cancellation.Cancel(); } };
                using (IEnumerator<MdfSampleBatch> samples = reader.ReadSamples(group, new[] { 1 }, count - 1, 1, int.MaxValue,
                    cancellationToken: cancellation.Token).GetEnumerator())
                {
                    Assert.Throws<OperationCanceledException>(() => samples.MoveNext());
                }
                Assert.NotNull(group.Layout.Interleaved.DataMap);
                Assert.Same(group, reader.ReadMetadata()[0].ChannelGroups[0]);
                Assert.InRange(source.LargestReadRequest, 1, 136);
            }
        }

        [Theory]
        [InlineData(72, 1048576, 3)]
        [InlineData(136, 352, 2)]
        [InlineData(136, 272, 1)]
        public void CommonInputAndOutputBatchReductionAndIndependentSnapshotsApplyToUnsortedRows(int readLimit, int batchLimit, int expectedRows)
        {
            MdfSampleBatch[] snapshots;
            using (MdfReader reader = MdfReader.Open(new MemoryStream(M5BWire.Read(), false), options: new MdfReaderOptions
            {
                MaximumReadBufferBytes = readLimit, MaximumSampleBatchBytes = batchLimit
            }))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                snapshots = reader.ReadSamples(group, new[] { 1, 3 }, batchSize: int.MaxValue).ToArray();
                Assert.Equal(5, snapshots.Sum(batch => batch.SampleCount));
                Assert.All(snapshots, batch => Assert.InRange(batch.SampleCount, 1, expectedRows));
                Assert.Equal(expectedRows, snapshots[0].SampleCount);
                Assert.Equal(0UL, snapshots[0].FirstSampleIndex);
                Assert.Equal(0UL, snapshots[0].GetValue(0, 0).UnsignedValue);
                Assert.Equal(5, reader.ReadSamples(group, new[] { 1, 3 }, batchSize: int.MaxValue).Sum(batch => batch.SampleCount));
            }
            Assert.Equal(0UL, snapshots[0].GetValue(0, 0).UnsignedValue);
            Assert.Equal(-2147483648L, snapshots[0].GetValue(0, 1).SignedValue);
        }

        [Fact]
        public void BelowMinimumOutputBatchLimitFailsEagerlyBeforeReadingIds()
        {
            using (TestReadStream source = new TestReadStream(M5BWire.Read()))
            using (MdfReader reader = MdfReader.Open(source, true, new MdfReaderOptions { MaximumSampleBatchBytes = 271 }))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                int before = source.ReadCalls;
                MdfResourceLimitException error = Assert.Throws<MdfResourceLimitException>(() => reader.ReadSamples(group, new[] { 1, 3 }));
                Assert.Equal("MaximumSampleBatchBytes", error.LimitName);
                Assert.Equal(272UL, error.Requested);
                Assert.Equal(before, source.ReadCalls);
                Assert.Empty(reader.ReadSamples(group, new[] { 1, 3 }, sampleCount: 0));
            }
        }

        [Fact]
        public void FailedStructuralMapIsNeverSharedAsReadyWithOtherCgs()
        {
            byte[] bytes = M5BWire.Read();
            M5BWire.Count(bytes, 0, 4);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                IReadOnlyList<MdfChannelGroup> groups = reader.ReadMetadata()[0].ChannelGroups;
                foreach (MdfChannelGroup group in groups)
                {
                    Assert.Equal("MDF013", Assert.Throws<MdfFormatException>(() => reader.ReadSamples(group, new[] { 1 }, sampleCount: 1).ToList()).DiagnosticCode);
                    Assert.Null(group.Layout.Interleaved.DataMap);
                }
            }
        }

        [Fact]
        public void ReentrancyDuringIdValidationIsRejectedAndDisposingAnEarlyEnumerationResetsAllCounters()
        {
            using (TestReadStream source = new TestReadStream(M5BWire.Read()))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                IReadOnlyList<MdfChannelGroup> groups = reader.ReadMetadata()[0].ChannelGroups;
                int root = M5BWire.Root(M5BWire.Read());
                bool checkedGate = false;
                source.AfterRead = () =>
                {
                    if (source.Position != root + 25) { return; }
                    checkedGate = true;
                    Assert.Throws<InvalidOperationException>(() => reader.ReadMetadata());
                    Assert.Throws<InvalidOperationException>(() => reader.ReadSamples(groups[1], new[] { 0 }));
                };
                using (IEnumerator<MdfSampleBatch> samples = reader.ReadSamples(groups[0], new[] { 1 }, batchSize: 1).GetEnumerator())
                {
                    Assert.True(samples.MoveNext());
                    Assert.Equal(0UL, samples.Current.GetValue(0, 0).UnsignedValue);
                    Assert.True(checkedGate);
                }
                source.AfterRead = null;
                Assert.Equal(5, reader.ReadSamples(groups[0], new[] { 1 }, batchSize: 2).Sum(batch => batch.SampleCount));
                Assert.Equal(3, reader.ReadSamples(groups[1], new[] { 1 }).Sum(batch => batch.SampleCount));
            }
        }

        private static byte[] TargetFrames(byte[] bytes)
        {
            int[] order = { 1, 0, 2, 1, 0, 0, 2, 0, 1, 0 };
            int[] sizes = M5BWire.Is3(bytes) ? new[] { 22, 13, 9 } : new[] { 23, 14, 10 };
            bool suffix = M5BWire.Is3(bytes) && M5BWire.UInt(bytes, M5BWire.Dg(bytes) + 22, 2) == 2;
            List<byte> selected = new List<byte>();
            int position = M5BWire.Raw(bytes);
            foreach (int member in order)
            {
                int size = sizes[member] + 1 + (suffix ? 1 : 0);
                if (member == 0) { selected.AddRange(bytes.Skip(position).Take(size)); }
                position += size;
            }
            return selected.ToArray();
        }
    }
}
