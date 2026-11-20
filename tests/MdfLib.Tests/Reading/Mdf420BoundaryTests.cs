// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using MdfLib.Internal;
using MdfLib.Reading;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Reading
{
    public sealed class Mdf420BoundaryTests
    {
        [Theory]
        [InlineData("io")]
        [InlineData("cancel")]
        [InlineData("dispose")]
        public void LastHeaderFailureCannotPublishEitherMapOrLeakTheOperationGate(string failure)
        {
            byte[] bytes = M5CWire.Read("m5c-ld-single420");
            int lastHeaderEnd = M5CWire.Leaf(bytes, invalidation: true) + 24;
            using (TestReadStream source = new TestReadStream(bytes) { MaximumBytesPerRead = 1 })
            using (MdfReader reader = MdfReader.Open(source, true))
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                IOException cause = new IOException("last DI header fault");
                source.AfterRead = () =>
                {
                    Assert.Throws<InvalidOperationException>(() => reader.ReadMetadata());
                    Assert.Throws<InvalidOperationException>(() => reader.ReadSamples(group, new[] { 1 }));
                    if (source.Position != lastHeaderEnd) { return; }
                    if (failure == "io") { throw cause; }
                    if (failure == "cancel") { cancellation.Cancel(); }
                    else { reader.Dispose(); }
                };
                if (failure == "io") { Assert.Same(cause, Assert.Throws<IOException>(() => reader.ReadSamples(group, new[] { 1 }).ToList())); }
                else if (failure == "cancel") { Assert.Throws<OperationCanceledException>(() => reader.ReadSamples(group, new[] { 1 }, cancellationToken: cancellation.Token).ToList()); }
                else { Assert.Throws<ObjectDisposedException>(() => reader.ReadSamples(group, new[] { 1 }).ToList()); }
                Assert.Null(group.Layout.ListDataMap);
                Assert.Null(group.Layout.DataMap);
                source.AfterRead = null;
                if (failure != "dispose")
                {
                    Assert.Same(group, reader.ReadMetadata()[0].ChannelGroups[0]);
                    Assert.Equal(7, reader.ReadSamples(group, new[] { 1 }).Sum(b => b.SampleCount));
                    Assert.NotNull(group.Layout.ListDataMap);
                }
                Assert.Equal(0, source.DisposeCalls);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ReportedAndHiddenLiveTruncationInEitherPairedModeRemainDistinctFromIntegrityAndIo(bool compressed)
        {
            byte[] bytes = M5CWire.Read(compressed ? "m5c-ld-zsingle420" : "m5c-ld-single420");
            int last = M5CWire.Leaf(bytes, invalidation: true);
            long end = last + (long)M3Wire.U64(bytes, last + 8);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Single(reader.ReadSamples(group, new[] { 1 }, sampleCount: 1));
                source.ReportedLength = end - 1;
                MdfFormatException shrink = Assert.Throws<MdfFormatException>(() => reader.ReadSamples(group, new[] { 1 }, sampleCount: 1).ToList());
                Assert.Equal("MDF007", shrink.DiagnosticCode);
                Assert.Equal((ulong)last, shrink.Offset);
                source.ReportedLength = bytes.Length;
                source.AvailableLength = end - 1;
                MdfFormatException hidden = Assert.Throws<MdfFormatException>(() => reader.ReadSamples(group, new[] { 1 }, 6, 1).ToList());
                Assert.Equal("MDF004", hidden.DiagnosticCode);
                Assert.IsType<EndOfStreamException>(hidden.InnerException);
                source.AvailableLength = bytes.Length;
                source.LengthFailure = new IOException("live separate stream length failure");
                Assert.Empty(reader.ReadSamples(group, new[] { 1 }, sampleCount: 0));
                Assert.Same(source.LengthFailure, Assert.Throws<IOException>(() => reader.ReadSamples(group, new[] { 1 }, sampleCount: 1).ToList()));
                source.LengthFailure = null;
                Assert.Single(reader.ReadSamples(group, new[] { 1 }, 6, 1));
                Assert.NotNull(group.Layout.ListDataMap);
            }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void ActivePairedEnumerationObservesCancellationOrDisposeAfterAnyIncludingFinalYield(bool cancel, bool final)
        {
            MdfSampleBatch snapshot;
            using (TestReadStream source = new TestReadStream(M5CWire.Read("m5c-ld-tsingle420")))
            using (MdfReader reader = MdfReader.Open(source, true))
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                using (IEnumerator<MdfSampleBatch> active = reader.ReadSamples(group, new[] { 1 }, 1, final ? 1UL : 6UL, 1,
                    cancellationToken: cancellation.Token).GetEnumerator())
                {
                    Assert.True(active.MoveNext());
                    snapshot = active.Current;
                    Assert.Equal(9007199254740993UL, snapshot.GetValue(0, 0).UnsignedValue);
                    Assert.Throws<InvalidOperationException>(() => reader.ReadMetadata());
                    Assert.Throws<InvalidOperationException>(() => reader.ReadSamples(group, new[] { 1 }));
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
                    }
                }
                Assert.Equal(0, source.DisposeCalls);
            }
            Assert.Equal(9007199254740993UL, snapshot.GetValue(0, 0).UnsignedValue);
        }

        [Fact]
        public void LateDiIntegrityFailurePublishesNoPartialBatchAndKeepsOnlyTheCompleteHeaderMap()
        {
            byte[] bytes = M5CWire.Read();
            int bad = M5CWire.Leaf(bytes, 2, true);
            bytes[bad + (int)M3Wire.U64(bytes, bad + 8) - 1] ^= 1;
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                using (var pending = reader.ReadSamples(group, new[] { 1, 2, 3 }, batchSize: 7).GetEnumerator())
                { Assert.Equal("MDF015", Assert.Throws<MdfFormatException>(() => pending.MoveNext()).DiagnosticCode); }
                Assert.NotNull(group.Layout.ListDataMap);
                Assert.Equal(3, group.Layout.ListDataMap.Invalidations.Segments.Length);
                Assert.Equal(6, reader.ReadSamples(group, new[] { 1 }, sampleCount: 6).Sum(b => b.SampleCount));
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CancellationBetweenValueAndInvalidationOrBeforeInflationNeverYieldsPartialRows(bool secondStream)
        {
            byte[] bytes = M5CWire.Read("m5c-ld-zsingle420");
            int stop = M5CWire.Leaf(bytes, invalidation: secondStream);
            stop += (int)M3Wire.U64(bytes, stop + 8);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                source.AfterRead = () => { if (source.Position == stop) { cancellation.Cancel(); } };
                using (var active = reader.ReadSamples(group, new[] { 1 }, cancellationToken: cancellation.Token).GetEnumerator())
                { Assert.Throws<OperationCanceledException>(() => active.MoveNext()); }
                Assert.NotNull(group.Layout.ListDataMap); // Header publication precedes selected body I/O, never decoded caches on reader.
                source.AfterRead = null;
                Assert.Equal(7, reader.ReadSamples(group, new[] { 1 }).Sum(b => b.SampleCount));
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void PairedInputFaultsRetainIoCauseAndReleaseGateWhileShortReadsStillWork(bool invalidation)
        {
            byte[] bytes = M5CWire.Read("m5c-ld-single420");
            using (TestReadStream source = new TestReadStream(bytes) { MaximumBytesPerRead = 1 })
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Single(reader.ReadSamples(group, new[] { 1 }, sampleCount: 1)); // Build complete map first.
                int trigger = M5CWire.Leaf(bytes, invalidation: invalidation) + 25;
                IOException cause = new IOException("selected paired read failure");
                source.AfterRead = () => { if (source.Position == trigger) { source.ReadFailure = cause; } };
                Assert.Same(cause, Assert.Throws<IOException>(() => reader.ReadSamples(group, new[] { 1 }).ToList()));
                source.AfterRead = null; source.ReadFailure = null;
                Mdf420IndexTests.AssertOriginalRecords(reader.ReadSamples(group, new[] { 0, 1, 2, 3 }, batchSize: 2), false);
                Assert.Equal(0, source.DisposeCalls);
            }
        }

        [Fact]
        public void FailedMapAttemptsSpendTheirAllowanceAndRepeatedSamplesDoNotSpendMetadata()
        {
            byte[] bytes = M5CWire.Read("m5c-ld-single420");
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                ReaderLimits limits = new ReaderLimits(new MdfReaderOptions { MaximumMetadataBytes = 1030 });
                ReadBudget budget = new ReadBudget(limits);
                MdfResourceLimitException first = Assert.Throws<MdfResourceLimitException>(() => Mdf4ListDataMap.Build(reader.Resolver, limits, budget,
                    (ulong)M5CWire.Root(bytes), 7, 22, 2, default(CancellationToken)));
                Assert.Equal(1152UL, first.Requested);
                MdfResourceLimitException second = Assert.Throws<MdfResourceLimitException>(() => Mdf4ListDataMap.Build(reader.Resolver, limits, budget,
                    (ulong)M5CWire.Root(bytes), 7, 22, 2, default(CancellationToken)));
                Assert.Equal(2048UL, second.Requested);
                Assert.Null(group.Layout.ListDataMap);
                Assert.Equal(7, reader.ReadSamples(group, new[] { 1 }).Sum(b => b.SampleCount));
                ReadBudget actual = (ReadBudget)typeof(MdfReader).GetField("budget", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(reader);
                ulong used = Used(actual);
                for (int repeat = 0; repeat < 50; repeat++) { Assert.Single(reader.ReadSamples(group, new[] { 1 }, (ulong)(repeat % 7), 1)); }
                Assert.Equal(used, Used(actual));
            }
        }

        [Fact]
        public void RemoteLookupChargesExactly128PerCatalogCgBeyondOrdinaryModelAllowance()
        {
            byte[] bytes = M5CWire.Read("m5c-ld-remote420");
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                ReaderLimits limits = new ReaderLimits(new MdfReaderOptions());
                ReadBudget old = new ReadBudget(limits), next = new ReadBudget(limits);
                new MdfLib.Formats.Mdf4.MetadataReader(reader.Resolver, limits, old, new object(), false, default(CancellationToken)).Read();
                new MdfLib.Formats.Mdf4.MetadataReader(reader.Resolver, limits, next, new object(), true, default(CancellationToken)).Read();
                Assert.Equal(256UL, Used(next) - Used(old)); // Two CG entries, before adding them to lookup capacity.
                ReaderLimits small = new ReaderLimits(new MdfReaderOptions { MaximumMetadataBytes = (long)Used(next) - 1 });
                ReadBudget cap = new ReadBudget(small);
                Assert.Equal("MaximumMetadataBytes", Assert.Throws<MdfResourceLimitException>(() =>
                    new MdfLib.Formats.Mdf4.MetadataReader(reader.Resolver, small, cap, new object(), true, default(CancellationToken)).Read()).LimitName);
                Assert.Equal(2, reader.ReadMetadata().Count);
            }
        }

        [Theory]
        [InlineData("MaximumBlockCount")]
        [InlineData("MaximumTotalLinks")]
        [InlineData("MaximumLinksPerBlock")]
        public void ResolverBudgetsAlsoApplyToLdAndPairedLeavesWithoutPayloadFallback(string name)
        {
            byte[] bytes = M5CWire.Read("m5c-ld-single420");
            MdfReaderOptions options = new MdfReaderOptions();
            if (name == "MaximumBlockCount") { options.MaximumBlockCount = 14; }
            else if (name == "MaximumTotalLinks") { options.MaximumTotalLinks = 54; }
            else
            {
                options.MaximumLinksPerBlock = 8;
                int value = M5CWire.Leaf(bytes), di = M5CWire.Leaf(bytes, invalidation: true);
                int list = M5CWire.List(ref bytes, Enumerable.Repeat((ulong)value, 4).ToArray(), Enumerable.Repeat((ulong)di, 4).ToArray(), null, 7);
                M5CWire.SetRoot(bytes, list);
            }
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true, options))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(name, Assert.Throws<MdfResourceLimitException>(() => reader.ReadSamples(group, new[] { 1 }).ToList()).LimitName);
                Assert.Null(group.Layout.ListDataMap);
            }
        }

        private static ulong Used(ReadBudget budget)
        { return (ulong)typeof(ReadBudget).GetField("metadataBytes", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(budget); }
    }
}
