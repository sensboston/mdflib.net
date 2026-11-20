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
    public sealed class Mdf4StorageTests
    {
        [Theory]
        [InlineData("m5-dz410", "4.10")]
        [InlineData("m5-transpose411", "4.11")]
        [InlineData("m5-offset411", "4.11")]
        [InlineData("m5-chain410", "4.10")]
        [InlineData("m5-hl411", "4.11")]
        [InlineData("m5-hl410", "4.10")]
        public void MetadataAndZeroRangesDoNotTouchStorageAndRequestsRemainExactAndRepeatable(string id, string version)
        {
            MdfSampleBatch snapshot;
            using (TestReadStream source = new TestReadStream(M5Wire.Read(id)))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                Assert.Equal(version, reader.Header.Version);
                Assert.Equal(1234567890123456789UL, reader.Header.RawStartTimeNanoseconds);
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(23UL, group.RecordByteCount);
                Assert.True(group.CanReadSamples);
                Assert.True(group.Channels[0].IsMaster);
                Assert.All(source.ReadRanges, range => Assert.True(range.Item1 + range.Item2 <= 1248));
                int before = source.ReadCalls;
                IEnumerable<MdfSampleBatch> pending = reader.ReadSamples(group, new[] { 1 }, 1, 1);
                Assert.Empty(reader.ReadSamples(group, new[] { 1 }, sampleCount: 0));
                Assert.Empty(reader.ReadSamples(group, new[] { 1 }, 7));
                Assert.Equal(before, source.ReadCalls);
                snapshot = pending.Single();
                Assert.Equal(9007199254740993UL, snapshot.GetValue(0, 0).UnsignedValue);
                Assert.NotNull(group.Layout.DataMap);
                Assert.Equal(9007199254740993UL, pending.Single().GetValue(0, 0).UnsignedValue);
                Assert.Equal(0, source.WriteCalls);
            }
            Assert.Equal(9007199254740993UL, snapshot.GetValue(0, 0).UnsignedValue);
        }

        [Theory]
        [InlineData("dz-links", "MDF006")]
        [InlineData("dz-fields", "MDF006")]
        [InlineData("dz-compressed", "MDF013")]
        [InlineData("dz-original", "MDF013")]
        [InlineData("dz-parameter", "MDF015")]
        [InlineData("dl-count", "MDF013")]
        [InlineData("dl-offset", "MDF013")]
        [InlineData("dl-gap", "MDF013")]
        [InlineData("dl-size", "MDF013")]
        [InlineData("dl-null", "MDF013")]
        [InlineData("dl-equal-zero", "MDF013")]
        [InlineData("dl-equal-size", "MDF013")]
        [InlineData("dl-cycle", "MDF010")]
        [InlineData("dl-next-type", "MDF005")]
        [InlineData("overlap", "MDF011")]
        [InlineData("hl-links", "MDF006")]
        [InlineData("hl-null", "MDF013")]
        [InlineData("hl-flags", "MDF013")]
        [InlineData("hl-mode", "MDF013")]
        [InlineData("cycles-overflow", "MDF013")]
        public void MalformedContainersAreRejectedLazilyWithoutPublishingPartialMaps(string mutation, string code)
        {
            string id = mutation.StartsWith("dz-") || mutation == "cycles-overflow" ? "m5-transpose411"
                : mutation.StartsWith("hl-") || mutation.StartsWith("dl-equal") || mutation == "overlap" ? "m5-hl411"
                : mutation == "dl-cycle" || mutation == "dl-next-type" ? "m5-chain410" : "m5-offset411";
            byte[] bytes = M5Wire.Read(id);
            int root = M5Wire.Root(bytes), dl = M5Wire.List(bytes);
            int payload = M3Wire.Payload(bytes, dl);
            if (mutation == "dz-links") { M3Wire.Put(bytes, root + 16, 1); M3Wire.Put(bytes, root + 24, 0); }
            else if (mutation == "dz-fields") { M3Wire.Put(bytes, root + 8, 47); }
            else if (mutation == "dz-compressed") { M3Wire.Put(bytes, root + 40, 1); }
            else if (mutation == "dz-original") { M3Wire.Put(bytes, root + 32, 162); }
            else if (mutation == "dz-parameter") { M3Wire.Put(bytes, root + 28, 0, 4); }
            else if (mutation == "dl-count") { M3Wire.Put(bytes, payload + 4, 3, 4); }
            else if (mutation == "dl-offset") { M3Wire.Put(bytes, payload + 8, 1); }
            else if (mutation == "dl-gap") { M3Wire.Put(bytes, payload + 16, 18); }
            else if (mutation == "dl-size") { M3Wire.Put(bytes, dl + 8, M3Wire.U64(bytes, dl + 8) - 1); }
            else if (mutation == "dl-null") { M3Wire.Put(bytes, dl + 32, 0); }
            else if (mutation == "dl-equal-zero") { M3Wire.Put(bytes, payload + 8, 0); }
            else if (mutation == "dl-equal-size") { M3Wire.Put(bytes, payload + 8, 47); }
            else if (mutation == "dl-cycle") { M3Wire.Put(bytes, dl + 24, (ulong)dl); }
            else if (mutation == "dl-next-type") { M3Wire.Put(bytes, dl + 24, M5Wire.Cg); }
            else if (mutation == "overlap") { M3Wire.Put(bytes, 1248 + 8, (ulong)(bytes.Length - 1248)); }
            else if (mutation == "hl-links") { M3Wire.Put(bytes, root + 16, 0); }
            else if (mutation == "hl-null") { M3Wire.Put(bytes, root + 24, 0); }
            else if (mutation == "hl-flags") { M3Wire.Put(bytes, root + 32, 0, 2); }
            else if (mutation == "hl-mode") { bytes[root + 34] = 0; }
            else { M3Wire.Put(bytes, M3Wire.Payload(bytes, M5Wire.Cg) + 8, ulong.MaxValue); }
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Empty(reader.ReadSamples(group, new[] { 1 }, sampleCount: 0));
                Assert.Equal(code, Assert.Throws<MdfFormatException>(() => reader.ReadSamples(group, new[] { 1 }).ToList()).DiagnosticCode);
                Assert.Null(group.Layout.DataMap);
                Assert.Same(group, reader.ReadMetadata()[0].ChannelGroups[0]);
                Assert.Equal(code, Assert.Throws<MdfFormatException>(() => reader.ReadSamples(group, new[] { 1 }).ToList()).DiagnosticCode);
                Assert.Equal(0, source.DisposeCalls);
            }
        }

        [Theory]
        [InlineData("root")]
        [InlineData("original")]
        [InlineData("zip")]
        [InlineData("dl-flags")]
        [InlineData("hl-flags")]
        [InlineData("hl-zip")]
        [InlineData("nested")]
        public void RequiredUnsupportedTypesAlgorithmsOrExtensionsAreNeverGuessed(string mutation)
        {
            byte[] bytes = M5Wire.Read("m5-hl411");
            int root = M5Wire.Root(bytes), dl = M5Wire.List(bytes);
            if (mutation == "root") { bytes[root + 2] = (byte)'L'; bytes[root + 3] = (byte)'D'; }
            else if (mutation == "original") { bytes[1248 + 25] = (byte)'I'; }
            else if (mutation == "zip") { bytes[1248 + 26] = 2; }
            else if (mutation == "dl-flags") { bytes[M3Wire.Payload(bytes, dl)] = 2; }
            else if (mutation == "hl-flags") { bytes[root + 32] = 2; }
            else if (mutation == "hl-zip") { bytes[root + 34] = 2; }
            else { M3Wire.Put(bytes, dl + 32, (ulong)dl); }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.True(group.CanReadSamples); // Record capability is not a certificate of unread storage.
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 1 }).ToList());
                Assert.Null(group.Layout.DataMap);
            }
        }

        [Fact]
        public void OnlyIntersectingCompressedPayloadsAreReadAndChecksummed()
        {
            byte[] bytes = M5Wire.Read("m5-hl411");
            bytes[1248 + (int)M3Wire.U64(bytes, 1248 + 8) - 1] ^= 1; // Corrupt first DZ checksum, keep all headers intact.
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(1UL << 63, reader.ReadSamples(group, new[] { 1 }, 2, 1).Single().GetValue(0, 0).UnsignedValue);
                Assert.DoesNotContain(source.ReadRanges, r => r.Item1 < 1248 + (long)M3Wire.U64(bytes, 1248 + 8) && r.Item1 + r.Item2 > 1248 + 48);
                MdfFormatException error = Assert.Throws<MdfFormatException>(() => reader.ReadSamples(group, new[] { 1 }, 0, 1).ToList());
                Assert.Equal("MDF015", error.DiagnosticCode);
                Assert.Equal(1248UL, error.Offset);
                Assert.IsType<InvalidDataException>(error.InnerException);
                Assert.Equal(1UL << 63, reader.ReadSamples(group, new[] { 1 }, 2, 1).Single().GetValue(0, 0).UnsignedValue);
            }
        }

        [Fact]
        public void OversizedUnselectedDzIsSkippedButSelectedDzIsRefusedBeforePayloadAllocation()
        {
            byte[] bytes = M5Wire.Read();
            const ulong cycles = 1UL << 32;
            ulong large = cycles * 23;
            int fake = M5Wire.Add(ref bytes, "DZ", new ulong[0], M5Wire.Zipped(new byte[0], large));
            int good = M5Wire.Add(ref bytes, "DZ", new ulong[0], M5Wire.Zipped(M5Wire.FirstRecords(2)));
            int list = M5Wire.Add(ref bytes, "DL", new[] { 0UL, (ulong)fake, (ulong)good }, M5Wire.Offsets(0, large));
            M3Wire.Put(bytes, M3Wire.Link(M5Wire.Dg, 2), (ulong)list);
            M3Wire.Put(bytes, M3Wire.Payload(bytes, M5Wire.Cg) + 8, cycles + 2);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(9007199254740993UL, reader.ReadSamples(group, new[] { 1 }, cycles + 1, 1).Single().GetValue(0, 0).UnsignedValue);
                int before = source.ReadCalls;
                MdfResourceLimitException error = Assert.Throws<MdfResourceLimitException>(() => reader.ReadSamples(group, new[] { 1 }, 0, 1).ToList());
                Assert.Equal("MaximumReadBufferBytes", error.LimitName);
                Assert.Equal(large, error.Requested);
                Assert.Equal(before, source.ReadCalls);
                Assert.DoesNotContain(source.ReadRanges, r => r.Item1 >= fake + 48 && r.Item1 < fake + 59);
            }
        }

        [Fact]
        public void SparseMultiTerabyteDtAndSmallDzCrossBoundaryRangeReadOnlyRequestedBytes()
        {
            byte[] metadata = M5Wire.Read();
            const ulong cycles = (1UL << 32) + 11;
            const long dt = 1L << 42;
            ulong firstSize = (cycles - 2) * 23;
            long dz = (dt + 24 + (long)firstSize + 7) & ~7L;
            byte[] zipped = M5Wire.Zipped(M5Wire.FirstRecords(2));
            byte[] dzBlock = M5Wire.Header("DZ", (ulong)zipped.Length).Concat(zipped).ToArray();
            int list = M5Wire.Add(ref metadata, "DL", new[] { 0UL, (ulong)dt, (ulong)dz }, M5Wire.Offsets(0, firstSize));
            M3Wire.Put(metadata, M3Wire.Link(M5Wire.Dg, 2), (ulong)list);
            M3Wire.Put(metadata, M3Wire.Payload(metadata, M5Wire.Cg) + 8, cycles);
            using (SparseReadStream source = new SparseReadStream(dz + dzBlock.Length))
            {
                source.Add(0, metadata); source.Add(dt, M5Wire.Header("DT", firstSize)); source.Add(dz, dzBlock);
                using (MdfReader reader = MdfReader.Open(source, true, new MdfReaderOptions { MaximumReadBufferBytes = 141 }))
                {
                    MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                    long before = source.BytesRead;
                    var pending = reader.ReadSamples(group, new[] { 1 }, cycles - 3, 3, int.MaxValue);
                    Assert.Equal(before, source.BytesRead);
                    MdfSampleBatch[] batches = pending.ToArray();
                    Assert.Equal(new[] { 3 }, batches.Select(b => b.SampleCount));
                    Assert.Equal(0UL, batches[0].GetValue(0, 0).UnsignedValue);
                    Assert.Equal(0UL, batches[0].GetValue(1, 0).UnsignedValue);
                    Assert.Equal(9007199254740993UL, batches[0].GetValue(2, 0).UnsignedValue);
                    Assert.Equal(232L, source.BytesRead - before); // Header-only map152 + exact DT23 + compressed57.
                    before = source.BytesRead;
                    Assert.Equal(3, pending.Sum(b => b.SampleCount));
                    Assert.Equal(80L, source.BytesRead - before); // Map cached, data decoded again.
                    Assert.InRange(source.LargestReadRequest, 1, 141);
                }
            }
        }

        [Fact]
        public void LiveTruncationHiddenShortReadIoFailureAndLateMapRetryReleaseTheGate()
        {
            byte[] bytes = M5Wire.Read("m5-offset411");
            using (TestReadStream source = new TestReadStream(bytes) { MaximumBytesPerRead = 1 })
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                IOException cause = new IOException("late map payload fault");
                source.AfterRead = () => { if (source.Position == 1400 + 24) { source.ReadFailure = cause; } };
                Assert.Same(cause, Assert.Throws<IOException>(() => reader.ReadSamples(group, new[] { 1 }).ToList()));
                Assert.Null(group.Layout.DataMap);
                source.AfterRead = null; source.ReadFailure = null;
                Assert.Equal(7, reader.ReadSamples(group, new[] { 1 }).Sum(b => b.SampleCount));
                Assert.NotNull(group.Layout.DataMap);
                Assert.Equal(0, source.DisposeCalls);
            }
            using (TestReadStream source = new TestReadStream(M5Wire.Read()))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(7, reader.ReadSamples(group, new[] { 1 }).Sum(b => b.SampleCount));
                source.ReportedLength--;
                int before = source.ReadCalls;
                Assert.Equal("MDF007", Assert.Throws<MdfFormatException>(() => reader.ReadSamples(group, new[] { 1 }).ToList()).DiagnosticCode);
                Assert.Equal(before, source.ReadCalls);
                source.ReportedLength++;
                source.AvailableLength = source.ReportedLength - 1;
                Assert.IsType<EndOfStreamException>(Assert.Throws<MdfFormatException>(() => reader.ReadSamples(group, new[] { 1 }).ToList()).InnerException);
                source.AvailableLength = source.ReportedLength;
                source.LengthFailure = new IOException("current length fault");
                Assert.Empty(reader.ReadSamples(group, new[] { 1 }, sampleCount: 0));
                Assert.Same(source.LengthFailure, Assert.Throws<IOException>(() => reader.ReadSamples(group, new[] { 1 }).ToList()));
                source.LengthFailure = null;
                Assert.Equal(7, reader.ReadSamples(group, new[] { 1 }).Sum(b => b.SampleCount));
            }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void CompressedEnumerationsKeepOneGateAndObserveCancelDisposeAfterEveryYield(bool cancel, bool final)
        {
            using (TestReadStream source = new TestReadStream(M5Wire.Read("m5-hl411")))
            using (MdfReader reader = MdfReader.Open(source, true))
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                using (IEnumerator<MdfSampleBatch> active = reader.ReadSamples(group, new[] { 1 }, sampleCount: final ? 1UL : 7UL, batchSize: 1,
                    cancellationToken: cancellation.Token).GetEnumerator())
                {
                    Assert.True(active.MoveNext());
                    Assert.Throws<InvalidOperationException>(() => reader.ReadMetadata());
                    Assert.Throws<InvalidOperationException>(() => reader.ReadSamples(group, new[] { 0 }));
                    if (cancel)
                    {
                        cancellation.Cancel();
                        Assert.Throws<OperationCanceledException>(() => active.MoveNext());
                        Assert.Same(group, reader.ReadMetadata()[0].ChannelGroups[0]);
                    }
                    else
                    {
                        reader.Dispose();
                        Assert.Throws<ObjectDisposedException>(() => active.MoveNext());
                        Assert.Equal(7UL, group.SampleCount);
                        Assert.Equal(0UL, active.Current.GetValue(0, 0).UnsignedValue);
                    }
                }
                Assert.Equal(0, source.DisposeCalls);
            }
        }

        [Fact]
        public void OneDecodedBlockIsCachedOnlyWithinAnEnumerationAndBatchesNeverAliasIt()
        {
            using (TestReadStream source = new TestReadStream(M5Wire.Read()))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                long before = source.ReadRanges.Sum(r => (long)r.Item2);
                MdfSampleBatch[] batches = reader.ReadSamples(group, new[] { 1 }, batchSize: 1).ToArray();
                Assert.Equal(7, batches.Length);
                Assert.Equal(220L, source.ReadRanges.Sum(r => (long)r.Item2) - before);
                Assert.Equal(0UL, batches[0].GetValue(0, 0).UnsignedValue);
                Assert.Equal(42UL, batches[6].GetValue(0, 0).UnsignedValue);
                before = source.ReadRanges.Sum(r => (long)r.Item2);
                for (int index = 0; index < 100; index++)
                {
                    Assert.Equal(7, reader.ReadSamples(group, new[] { 1 }).Sum(b => b.SampleCount));
                }
                Assert.Equal(17200L, source.ReadRanges.Sum(r => (long)r.Item2) - before);
                Assert.Equal(0UL, batches[0].GetValue(0, 0).UnsignedValue);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CancellationDuringMapOrBeforeInflationIsObservedAndReentryCannotReleaseTheGate(bool afterMap)
        {
            using (TestReadStream source = new TestReadStream(M5Wire.Read()))
            using (MdfReader reader = MdfReader.Open(source, true))
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                source.AfterRead = () =>
                {
                    Assert.Throws<InvalidOperationException>(() => reader.ReadMetadata());
                    Assert.Throws<InvalidOperationException>(() => reader.ReadSamples(group, new[] { 0 }));
                    if (source.Position == (afterMap ? 1468 : 1296)) { cancellation.Cancel(); }
                };
                Assert.Throws<OperationCanceledException>(() => reader.ReadSamples(group, new[] { 1 }, cancellationToken: cancellation.Token).ToList());
                Assert.Equal(afterMap, group.Layout.DataMap != null);
                source.AfterRead = null;
                Assert.Equal(7, reader.ReadSamples(group, new[] { 1 }).Sum(b => b.SampleCount));
                Assert.Equal(0, source.DisposeCalls);
            }
        }

        [Theory]
        [InlineData(60)]
        [InlineData(61)]
        [InlineData(62)]
        [InlineData(63)]
        public void Version411StillRefusesUnfinalizedFlagsBeforeMetadata(int flag)
        {
            byte[] bytes = M5Wire.Read("m5-transpose411"); bytes[flag] = 1;
            using (TestReadStream source = new TestReadStream(bytes))
            {
                Assert.Throws<NotSupportedException>(() => MdfReader.Open(source, true));
                Assert.Equal(0, source.DisposeCalls);
                Assert.All(source.ReadRanges, r => Assert.True(r.Item1 + r.Item2 <= 64));
            }
        }

        [Theory]
        [InlineData("MaximumTraversalDepth")]
        [InlineData("MaximumMetadataBytes")]
        [InlineData("MaximumReadBufferBytes")]
        public void MapBuilderChecksItsOwnDepthModelAndListPayloadBudgetsBeforeMaterialization(string limit)
        {
            byte[] bytes = M5Wire.Read("m5-hl411");
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                MdfReaderOptions options = new MdfReaderOptions();
                if (limit == "MaximumTraversalDepth") { options.MaximumTraversalDepth = 1; }
                else if (limit == "MaximumMetadataBytes") { options.MaximumMetadataBytes = 639; }
                else { options.MaximumReadBufferBytes = 15; }
                var limits = new MdfLib.Internal.ReaderLimits(options);
                var budget = new MdfLib.Internal.ReadBudget(limits);
                Assert.Equal(limit, Assert.Throws<MdfResourceLimitException>(() => MdfLib.Reading.Mdf4DataMap.Build(reader.Resolver,
                    limits, budget, (ulong)M5Wire.Root(bytes), group.SampleCount, group.RecordByteCount, default(CancellationToken))).LimitName);
                Assert.Null(group.Layout.DataMap);
            }
        }

        [Fact]
        public void SmallReadAndStructuralBudgetsDoNotBecomeDecompressionOrMapBypasses()
        {
            using (TestReadStream source = new TestReadStream(M5Wire.Read()))
            using (MdfReader reader = MdfReader.Open(source, true, new MdfReaderOptions { MaximumReadBufferBytes = 160 }))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                MdfResourceLimitException error = Assert.Throws<MdfResourceLimitException>(() => reader.ReadSamples(group, new[] { 1 }).ToList());
                Assert.Equal("MaximumReadBufferBytes", error.LimitName);
                Assert.Equal(172UL, error.Requested);
                Assert.DoesNotContain(source.ReadRanges, r => r.Item1 >= 1296);
            }
            foreach (bool blocks in new[] { false, true })
            {
                MdfReaderOptions options = new MdfReaderOptions();
                if (blocks) { options.MaximumBlockCount = 14; } else { options.MaximumTotalLinks = 56; }
                using (MdfReader reader = MdfReader.Open(new MemoryStream(M5Wire.Read("m5-hl411"), false), options: options))
                {
                    MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                    Assert.Equal(blocks ? "MaximumBlockCount" : "MaximumTotalLinks",
                        Assert.Throws<MdfResourceLimitException>(() => reader.ReadSamples(group, new[] { 1 }).ToList()).LimitName);
                }
            }
        }
    }
}
