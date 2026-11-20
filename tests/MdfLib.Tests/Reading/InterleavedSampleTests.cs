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
    public sealed class InterleavedSampleTests
    {
        [Theory]
        [InlineData("m5b-prefix-le330", false)]
        [InlineData("m5b-prefix-le330", true)]
        [InlineData("m5b-suffix-be330", false)]
        [InlineData("m5b-suffix-be330", true)]
        [InlineData("m5b-id1-dt410", false)]
        [InlineData("m5b-id1-dt410", true)]
        public void UnknownIdsAnywhereInDgFailBeforeFirstYieldEvenForAnEarlierSmallRange(string id, bool late)
        {
            byte[] bytes = M5BWire.Read(id);
            int raw = M5BWire.Raw(bytes);
            int frame = M5BWire.Is3(bytes) ? 23 + (id.Contains("suffix") ? 1 : 0) : 24;
            int at = late ? bytes.Length - frame : raw;
            bytes[at] = 42;
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Empty(reader.ReadSamples(group, new[] { 1 }, sampleCount: 0));
                using (IEnumerator<MdfSampleBatch> samples = reader.ReadSamples(group, new[] { 1 }, 0, 1).GetEnumerator())
                {
                    MdfFormatException error = Assert.Throws<MdfFormatException>(() => samples.MoveNext());
                    Assert.Equal("MDF013", error.DiagnosticCode);
                    Assert.Equal((ulong)M5BWire.Dg(bytes), error.Offset);
                    Assert.Contains("Logical DG byte offset", error.Message);
                }
                if (!M5BWire.Is3(bytes)) { Assert.NotNull(group.Layout.Interleaved.DataMap); }
                Assert.Same(group, reader.ReadMetadata()[0].ChannelGroups[0]);
                Assert.Equal("MDF013", Assert.Throws<MdfFormatException>(() => reader.ReadSamples(group, new[] { 1 }, 0, 1).ToList()).DiagnosticCode);
            }
        }

        [Fact]
        public void MatchingCombinedByteLengthCannotHideIncorrectPerGroupCounts()
        {
            byte[] bytes = M5BWire.Read();
            // 5*24+3*15+2*11 == 3*24+4*15+5*11. Header coverage alone is not a record-count certificate.
            M5BWire.Count(bytes, 0, 3); M5BWire.Count(bytes, 1, 4); M5BWire.Count(bytes, 2, 5);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                using (IEnumerator<MdfSampleBatch> samples = reader.ReadSamples(group, new[] { 1 }, 0, 1).GetEnumerator())
                {
                    MdfFormatException error = Assert.Throws<MdfFormatException>(() => samples.MoveNext());
                    Assert.Equal("MDF013", error.DiagnosticCode);
                    Assert.Contains("cycle count", error.Message);
                    Assert.NotNull(group.Layout.Interleaved.DataMap);
                }
            }
        }

        [Fact]
        public void Mdf3SuffixIsAnIndependentMatchingByteNotPartOfAUInt16Prefix()
        {
            byte[] bytes = M5BWire.Read("m5b-suffix-be330");
            bytes[bytes.Length - 1] = 255;
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                int before = source.ReadCalls;
                Assert.Empty(reader.ReadSamples(group, new[] { 1 }, sampleCount: 0));
                Assert.Equal(before, source.ReadCalls);
                using (IEnumerator<MdfSampleBatch> samples = reader.ReadSamples(group, new[] { 1 }, sampleCount: 1).GetEnumerator())
                {
                    MdfFormatException error = Assert.Throws<MdfFormatException>(() => samples.MoveNext());
                    Assert.Equal("MDF013", error.DiagnosticCode);
                    Assert.Contains("does not match its prefix", error.Message);
                }
                Assert.Same(group, reader.ReadMetadata()[0].ChannelGroups[0]);
            }
        }

        [Theory]
        [InlineData("m5b-prefix-le330", 37)]
        [InlineData("m5b-suffix-be330", 52)]
        [InlineData("m5b-id1-dt410", 62)]
        public void ValidationReadsOnlyIdsAndSuffixesAndSecondPassReadsOnlySelectedDtBody(string id, int expectedFirstBytes)
        {
            byte[] bytes = M5BWire.Read(id);
            int raw = M5BWire.Raw(bytes);
            bool mdf3 = M5BWire.Is3(bytes), suffix = id.Contains("suffix");
            int[] order = { 1, 0, 2, 1, 0, 0, 2, 0, 1, 0 };
            int[] sizes = mdf3 ? new[] { 22, 13, 9 } : new[] { 23, 14, 10 };
            HashSet<long> ids = new HashSet<long>();
            int next = raw, selectedBody = 0;
            for (int index = 0; index < order.Length; index++)
            {
                ids.Add(next);
                if (index == 4) { selectedBody = next + 1; }
                next += sizes[order[index]] + 1;
                if (suffix) { ids.Add(next++); }
            }
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                IEnumerable<MdfSampleBatch> request = reader.ReadSamples(group, new[] { 1 }, 1, 1);
                source.ReadRanges.Clear();
                Assert.Equal(9007199254740993UL, request.Single().GetValue(0, 0).UnsignedValue);
                Assert.Equal(expectedFirstBytes, source.ReadRanges.Sum(range => range.Item2));
                Assert.All(source.ReadRanges, range => Assert.True(
                    range.Item2 == 1 && ids.Contains(range.Item1)
                    || range.Item1 == selectedBody && range.Item2 == sizes[0]
                    || !mdf3 && range.Item1 == raw - 24 && range.Item2 == 24));
                source.ReadRanges.Clear();
                Assert.Equal(9007199254740993UL, request.Single().GetValue(0, 0).UnsignedValue);
                Assert.Equal(expectedFirstBytes - (mdf3 ? 0 : 24), source.ReadRanges.Sum(range => range.Item2));
                Assert.Equal(0, source.WriteCalls);
            }
        }

        [Theory]
        [InlineData(1, false)]
        [InlineData(2, false)]
        [InlineData(4, false)]
        [InlineData(8, false)]
        [InlineData(8, true)]
        public void AllPrefixBytesAndBodiesCanCrossOneByteMixedSegmentsWithExactUnsignedIds(int width, bool collidingLowBits)
        {
            byte[] bytes = M5BWire.Read();
            byte[] old = bytes.Skip(M5BWire.Raw(bytes)).ToArray();
            int[] order = { 1, 0, 2, 1, 0, 0, 2, 0, 1, 0 }, sizes = { 23, 14, 10 };
            ulong[] ids = { 0, width == 8 ? ulong.MaxValue : (1UL << (8 * width)) - 1, width == 8 ? (1UL << 63) + 1 : (1UL << (8 * width - 1)) + 1 };
            if (collidingLowBits) { ids[2] = 1UL << 32; } // ID0 and ID2 have identical low UInt32, but distinct full-width identities.
            List<byte> stream = new List<byte>();
            int offset = 0;
            foreach (int gi in order)
            {
                byte[] prefix = new byte[width]; M5BWire.Put(prefix, 0, ids[gi], width);
                stream.AddRange(prefix); stream.AddRange(old.Skip(offset + 1).Take(sizes[gi]));
                offset += 1 + sizes[gi];
            }
            for (int index = 0; index < 3; index++) { M5BWire.Id(bytes, index, ids[index]); }
            M5BWire.Width(bytes, width);
            bytes = M5BWire.WithSegments(bytes, stream.Select(value => new[] { value }).ToArray(), stream.Select((value, index) => index % 2 != 0).ToArray());
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                IReadOnlyList<MdfChannelGroup> groups = reader.ReadMetadata()[0].ChannelGroups;
                Assert.Equal(ids, groups.Select(group => group.Layout.RecordId));
                foreach (int batch in new[] { 1, 2, 100 })
                {
                    ulong[] actual = reader.ReadSamples(groups[0], new[] { 1 }, batchSize: batch)
                        .SelectMany(item => Enumerable.Range(0, item.SampleCount).Select(row => item.GetValue(row, 0).UnsignedValue)).ToArray();
                    Assert.Equal(new[] { 0UL, 9007199254740993UL, 1UL << 63, ulong.MaxValue, 17UL }, actual);
                    Assert.Equal(3, reader.ReadSamples(groups[1], new[] { 1 }, batchSize: batch).Sum(item => item.SampleCount));
                    Assert.Equal(2, reader.ReadSamples(groups[2], new[] { 1 }, batchSize: batch).Sum(item => item.SampleCount));
                }
            }
        }

        [Fact]
        public void OneDzCacheIsReusedAcrossValidationAndSelectedPassButNeverAcrossEnumerations()
        {
            byte[] bytes = M5BWire.Read("m5b-id2-dz411");
            int root = M5BWire.Root(bytes);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true, new MdfReaderOptions { MaximumReadBufferBytes = 208 }))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                source.ReadRanges.Clear();
                for (int repeat = 0; repeat < 100; repeat++)
                {
                    Assert.Equal(17UL, reader.ReadSamples(group, new[] { 1 }, 4, 1).Single().GetValue(0, 0).UnsignedValue);
                    Assert.Equal(repeat + 1, source.ReadRanges.Count(range => range.Item1 == root + 48 && range.Item2 == 208));
                }
                Assert.NotNull(group.Layout.Interleaved.DataMap);
            }
        }

        [Theory]
        [InlineData(207, false)]
        [InlineData(208, true)]
        public void EvenASmallUnsortedRangeNeedsTheWholeDzContainingAFramingId(int limit, bool allowed)
        {
            byte[] bytes = M5BWire.Read("m5b-id2-dz411");
            int root = M5BWire.Root(bytes);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true, new MdfReaderOptions { MaximumReadBufferBytes = limit }))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                if (allowed) { Assert.Single(reader.ReadSamples(group, new[] { 1 }, sampleCount: 1)); }
                else
                {
                    MdfResourceLimitException error = Assert.Throws<MdfResourceLimitException>(() => reader.ReadSamples(group, new[] { 1 }, sampleCount: 1).ToList());
                    Assert.Equal("MaximumReadBufferBytes", error.LimitName);
                    Assert.Equal(208UL, error.Requested);
                    Assert.NotNull(group.Layout.Interleaved.DataMap);
                    Assert.DoesNotContain(source.ReadRanges, range => range.Item1 >= root + 48 && range.Item1 < bytes.Length);
                }
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void OversizedDzContainingOnlySkippedNeighborBodyIsNotNeededButAnIdInsideItIsNeeded(bool prefixInside)
        {
            byte[] bytes = M5BWire.Read();
            M5BWire.Count(bytes, 0, 1); M5BWire.Count(bytes, 1, 1); M5BWire.Count(bytes, 2, 0);
            M5BWire.Size(bytes, 1, 1024); // One invalidation byte remains: neighboring fixed body=1025, larger than read limit.
            int prefix = M5Wire.Add(ref bytes, "DT", new ulong[0], new byte[] { 255 });
            int large = M5Wire.Add(ref bytes, "DZ", new ulong[0], M5Wire.Zipped(new byte[0], prefixInside ? 1026UL : 1025UL));
            int good = M5Wire.Add(ref bytes, "DT", new ulong[0], new byte[] { 0 }.Concat(M5Wire.FirstRecords(2).Skip(23)).ToArray());
            int list = M5Wire.Add(ref bytes, "DL", prefixInside ? new[] { 0UL, (ulong)large, (ulong)good } : new[] { 0UL, (ulong)prefix, (ulong)large, (ulong)good },
                prefixInside ? M5Wire.Offsets(0, 1026) : M5Wire.Offsets(0, 1, 1026));
            M5BWire.Root(bytes, (ulong)list);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true, new MdfReaderOptions { MaximumReadBufferBytes = 136 }))
            {
                IReadOnlyList<MdfChannelGroup> groups = reader.ReadMetadata()[0].ChannelGroups;
                if (prefixInside)
                {
                    MdfResourceLimitException error = Assert.Throws<MdfResourceLimitException>(() => reader.ReadSamples(groups[0], new[] { 1 }).ToList());
                    Assert.Equal("MaximumReadBufferBytes", error.LimitName);
                    Assert.Equal(1026UL, error.Requested);
                }
                else { Assert.Equal(9007199254740993UL, reader.ReadSamples(groups[0], new[] { 1 }).Single().GetValue(0, 0).UnsignedValue); }
                Assert.DoesNotContain(source.ReadRanges, range => range.Item1 >= large + 48 && range.Item1 < large + 59);
                int before = source.ReadCalls;
                Assert.Throws<MdfResourceLimitException>(() => reader.ReadSamples(groups[1], new[] { 0 }));
                Assert.Equal(before, source.ReadCalls);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CorruptUnselectedNeighborDzFailsOnlyIfItsPayloadIsNeededForAnId(bool prefixInside)
        {
            byte[] bytes = M5BWire.Read();
            M5BWire.Count(bytes, 0, 1); M5BWire.Count(bytes, 1, 1); M5BWire.Count(bytes, 2, 0);
            byte[] raw = new byte[14 + (prefixInside ? 1 : 0)];
            if (prefixInside) { raw[0] = 255; }
            int prefix = M5Wire.Add(ref bytes, "DT", new ulong[0], new byte[] { 255 });
            byte[] compressed = M5Wire.Zipped(raw); compressed[compressed.Length - 1] ^= 1;
            int bad = M5Wire.Add(ref bytes, "DZ", new ulong[0], compressed);
            int good = M5Wire.Add(ref bytes, "DT", new ulong[0], new byte[] { 0 }.Concat(M5Wire.FirstRecords(2).Skip(23)).ToArray());
            int list = M5Wire.Add(ref bytes, "DL", prefixInside ? new[] { 0UL, (ulong)bad, (ulong)good } : new[] { 0UL, (ulong)prefix, (ulong)bad, (ulong)good },
                prefixInside ? M5Wire.Offsets(0, 15) : M5Wire.Offsets(0, 1, 15));
            M5BWire.Root(bytes, (ulong)list);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                if (prefixInside)
                {
                    using (IEnumerator<MdfSampleBatch> samples = reader.ReadSamples(group, new[] { 1 }).GetEnumerator())
                    {
                        MdfFormatException error = Assert.Throws<MdfFormatException>(() => samples.MoveNext());
                        Assert.Equal("MDF015", error.DiagnosticCode);
                        Assert.Equal((ulong)bad, error.Offset);
                        Assert.IsType<InvalidDataException>(error.InnerException);
                    }
                }
                else
                {
                    Assert.Equal(9007199254740993UL, reader.ReadSamples(group, new[] { 1 }).Single().GetValue(0, 0).UnsignedValue);
                    Assert.DoesNotContain(source.ReadRanges, range => range.Item1 >= bad + 48 && range.Item1 < bad + 24 + compressed.Length);
                }
                Assert.NotNull(group.Layout.Interleaved.DataMap);
            }
        }

        [Fact]
        public void SparseMultiTerabyteNeighborBodiesAreSkippedWithOnlyTwoLinearIdPasses()
        {
            byte[] metadata = M5BWire.Read();
            const long dt = 1L << 42;
            const int neighbors = 1024;
            const ulong neighborFrame = (1UL << 32) + 1;
            ulong length = neighbors * neighborFrame + 3 * 24;
            M5BWire.Count(metadata, 0, 3); M5BWire.Count(metadata, 1, neighbors); M5BWire.Count(metadata, 2, 0);
            M5BWire.Size(metadata, 1, uint.MaxValue); // +one invalidation byte, intentionally larger than Int32.
            M5BWire.Root(metadata, (ulong)dt);
            using (SparseReadStream source = new SparseReadStream(dt + 24 + (long)length))
            {
                source.Add(0, metadata); source.Add(dt, M5Wire.Header("DT", length));
                for (int index = 0; index < neighbors; index++) { source.Add(dt + 24 + (long)((ulong)index * neighborFrame), new byte[] { 255 }); }
                byte[] records = M5Wire.FirstRecords(3);
                byte[] selected = Enumerable.Range(0, 3).SelectMany(index => new byte[] { 0 }.Concat(records.Skip(index * 23).Take(23))).ToArray();
                source.Add(dt + 24 + (long)(neighbors * neighborFrame), selected);
                using (MdfReader reader = MdfReader.Open(source, true, new MdfReaderOptions { MaximumReadBufferBytes = 136 }))
                {
                    MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                    long before = source.BytesRead;
                    IEnumerable<MdfSampleBatch> pending = reader.ReadSamples(group, new[] { 1 }, 2, 1);
                    Assert.Equal(before, source.BytesRead);
                    Assert.Equal(1UL << 63, pending.Single().GetValue(0, 0).UnsignedValue);
                    Assert.Equal(24L + 2 * (neighbors + 3) + 23, source.BytesRead - before);
                    before = source.BytesRead;
                    Assert.Equal(1UL << 63, pending.Single().GetValue(0, 0).UnsignedValue);
                    Assert.Equal(2L * (neighbors + 3) + 23, source.BytesRead - before);
                    Assert.InRange(source.LargestReadRequest, 1, 136);
                }
            }
        }

        [Theory]
        [InlineData(136, false)]
        [InlineData(160, true)]
        public void CounterScratchIsBoundedByCgCountBeforeAllocationOrIdScan(int limit, bool allowed)
        {
            byte[] bytes = M5BWire.Read();
            ulong next = 0;
            for (int index = 19; index >= 1; index--)
            {
                byte[] payload = new byte[32]; M5BWire.Put(payload, 0, (ulong)index); M5BWire.Put(payload, 24, 1, 4);
                next = (ulong)M5Wire.Add(ref bytes, "CG", new[] { next, 0UL, 0UL, 0UL, 0UL, 0UL }, payload);
            }
            M5BWire.Put(bytes, M5BWire.Cg(bytes) + 24, next);
            M5BWire.Count(bytes, 0, 1);
            int root = M5Wire.Add(ref bytes, "DT", new ulong[0], new byte[] { 0 }.Concat(M5Wire.FirstRecords(1)).ToArray());
            M5BWire.Root(bytes, (ulong)root);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true, new MdfReaderOptions { MaximumReadBufferBytes = limit }))
            {
                IReadOnlyList<MdfChannelGroup> groups = reader.ReadMetadata()[0].ChannelGroups;
                Assert.Equal(20, groups.Count);
                if (allowed) { Assert.Equal(0UL, reader.ReadSamples(groups[0], new[] { 1 }).Single().GetValue(0, 0).UnsignedValue); }
                else
                {
                    MdfResourceLimitException error = Assert.Throws<MdfResourceLimitException>(() => reader.ReadSamples(groups[0], new[] { 1 }).ToList());
                    Assert.Equal("MaximumReadBufferBytes", error.LimitName);
                    Assert.Equal(160UL, error.Requested);
                    Assert.DoesNotContain(source.ReadRanges, range => range.Item1 >= root + 24);
                }
            }
        }

        [Theory]
        [InlineData("m5b-prefix-le330")]
        [InlineData("m5b-suffix-be330")]
        [InlineData("m5b-id1-dt410")]
        public void HiddenLateIdTruncationAndIoFaultsAreNotMaskedAndFailuresReleaseTheGate(string id)
        {
            byte[] bytes = M5BWire.Read(id);
            int finalFrame = M5BWire.Is3(bytes) ? 23 + (id.Contains("suffix") ? 1 : 0) : 24;
            using (TestReadStream source = new TestReadStream(bytes) { MaximumBytesPerRead = 1 })
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                source.AvailableLength = bytes.Length - finalFrame;
                MdfFormatException error = Assert.Throws<MdfFormatException>(() => reader.ReadSamples(group, new[] { 1 }, sampleCount: 1).ToList());
                Assert.Equal("MDF004", error.DiagnosticCode);
                Assert.IsType<EndOfStreamException>(error.InnerException);
                source.AvailableLength = bytes.Length;
                IOException cause = new IOException("Interleaved I/O fault, not corrupt MDF.");
                source.ReadFailure = cause;
                Assert.Same(cause, Assert.Throws<IOException>(() => reader.ReadSamples(group, new[] { 1 }, sampleCount: 1).ToList()));
                source.ReadFailure = null;
                Assert.Equal(5, reader.ReadSamples(group, new[] { 1 }).Sum(item => item.SampleCount));
                Assert.Equal(0, source.DisposeCalls);
            }
        }

        [Theory]
        [InlineData("m5b-prefix-le330")]
        [InlineData("m5b-suffix-be330")]
        public void Mdf3ReportedShrinkRevalidatesWholeDgBeforeReadingAnyId(string id)
        {
            byte[] bytes = M5BWire.Read(id);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                source.ReportedLength--;
                int before = source.ReadCalls;
                Assert.Empty(reader.ReadSamples(group, new[] { 0 }, sampleCount: 0));
                Assert.Equal("MDF007", Assert.Throws<MdfFormatException>(() => reader.ReadSamples(group, new[] { 1 }, sampleCount: 1).ToList()).DiagnosticCode);
                Assert.Equal(before, source.ReadCalls);
                source.ReportedLength++;
                Assert.Single(reader.ReadSamples(group, new[] { 1 }, sampleCount: 1));
            }
        }

        [Theory]
        [InlineData("m5b-prefix-le330", false)]
        [InlineData("m5b-prefix-le330", true)]
        [InlineData("m5b-id2-dz411", false)]
        [InlineData("m5b-id2-dz411", true)]
        public void CancellationOrDisposalDuringValidationNeverPublishesAPartialBatch(string id, bool dispose)
        {
            using (TestReadStream source = new TestReadStream(M5BWire.Read(id)))
            using (MdfReader reader = MdfReader.Open(source, true))
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                source.AfterRead = () =>
                {
                    source.AfterRead = null;
                    if (dispose) { reader.Dispose(); } else { cancellation.Cancel(); }
                };
                using (IEnumerator<MdfSampleBatch> samples = reader.ReadSamples(group, new[] { 1 }, sampleCount: 1, cancellationToken: cancellation.Token).GetEnumerator())
                {
                    if (dispose) { Assert.Throws<ObjectDisposedException>(() => samples.MoveNext()); }
                    else { Assert.Throws<OperationCanceledException>(() => samples.MoveNext()); }
                }
                if (!dispose) { Assert.Equal(5, reader.ReadSamples(group, new[] { 1 }).Sum(item => item.SampleCount)); }
                Assert.Equal(0, source.DisposeCalls);
            }
        }

        [Theory]
        [InlineData("m5b-suffix-be330", false, false)]
        [InlineData("m5b-suffix-be330", false, true)]
        [InlineData("m5b-suffix-be330", true, false)]
        [InlineData("m5b-suffix-be330", true, true)]
        [InlineData("m5b-id8-hl411", false, false)]
        [InlineData("m5b-id8-hl411", false, true)]
        [InlineData("m5b-id8-hl411", true, false)]
        [InlineData("m5b-id8-hl411", true, true)]
        public void AllCgsShareOneGateAndEachYieldIncludingFinalObservesCancellationOrDisposal(string id, bool cancel, bool final)
        {
            using (TestReadStream source = new TestReadStream(M5BWire.Read(id)))
            using (MdfReader reader = MdfReader.Open(source, true))
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                IReadOnlyList<MdfChannelGroup> groups = reader.ReadMetadata()[0].ChannelGroups;
                IEnumerable<MdfSampleBatch> second = reader.ReadSamples(groups[1], new[] { 1 });
                using (IEnumerator<MdfSampleBatch> samples = reader.ReadSamples(groups[0], new[] { 1 }, sampleCount: final ? 1UL : 5UL,
                    batchSize: 1, cancellationToken: cancellation.Token).GetEnumerator())
                {
                    Assert.True(samples.MoveNext());
                    Assert.Throws<InvalidOperationException>(() => reader.ReadSamples(groups[1], new[] { 0 }));
                    Assert.Throws<InvalidOperationException>(() => reader.ReadMetadata());
                    Assert.Throws<InvalidOperationException>(() => second.ToList());
                    if (cancel)
                    {
                        cancellation.Cancel();
                        Assert.Throws<OperationCanceledException>(() => samples.MoveNext());
                        Assert.Equal(3, second.Sum(item => item.SampleCount));
                    }
                    else { reader.Dispose(); Assert.Throws<ObjectDisposedException>(() => samples.MoveNext()); }
                }
                Assert.Equal(0, source.DisposeCalls);
            }
        }
    }
}
