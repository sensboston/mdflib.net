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
    public sealed class FixedArraySampleTests
    {
        [Theory]
        [InlineData(560, 1)]
        [InlineData(879, 1)]
        [InlineData(880, 2)]
        [InlineData(1840, 5)]
        public void OutputRowsAreBoundedByAllElementsRatherThanOnlyLogicalColumnCount(int cap, int rows)
        {
            using (TestReadStream source = new TestReadStream(M5DWire.Read()))
            using (MdfReader reader = MdfReader.Open(source, true, new MdfReaderOptions { MaximumSampleBatchBytes = cap }))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                MdfSampleBatch[] batches = reader.ReadSamples(group, new[] { 8 }, batchSize: int.MaxValue).ToArray();
                Assert.Equal(rows, batches[0].SampleCount);
                Assert.Equal(5, batches.Sum(b => b.SampleCount));
                Assert.All(batches, b => Assert.Single(b.Channels));
                Assert.Equal(9007199254740993UL, batches[0].GetValue(0, 0, 6).UnsignedValue);
                Assert.Equal(0, source.WriteCalls);
            }
        }

        [Theory]
        [InlineData(239, 1048576, 0UL, "MaximumSampleBatchBytes")]
        [InlineData(559, 1048576, 1UL, "MaximumSampleBatchBytes")]
        [InlineData(560, 579, 1UL, "MaximumReadBufferBytes")]
        public void HeaderOneOutputRowAndOneRecordAreEagerlyBoundedBeforeSampleIo(int outputCap, int inputCap, ulong count, string name)
        {
            using (TestReadStream source = new TestReadStream(M5DWire.Read()))
            using (MdfReader reader = MdfReader.Open(source, true, new MdfReaderOptions
                { MaximumSampleBatchBytes = outputCap, MaximumReadBufferBytes = inputCap }))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                int before = source.ReadCalls;
                MdfResourceLimitException error = Assert.Throws<MdfResourceLimitException>(() => reader.ReadSamples(group, new[] { 8 }, sampleCount: count));
                Assert.Equal(name, error.LimitName);
                Assert.Equal(before, source.ReadCalls);
                Assert.Null(group.Layout.DataMap);
            }
        }

        [Theory]
        [InlineData(240)]
        [InlineData(int.MaxValue)]
        public void EmptyRangeDoesNotConvertHugeArrayProductToIntOrReadRecordsOrAllocateElements(int cap)
        {
            byte[] bytes = M5DWire.Read();
            M5DWire.Array(ref bytes, new[] { (ulong)uint.MaxValue });
            M3Wire.Put(bytes, M3Wire.Payload(bytes, M5DWire.Cn(bytes)) + 4, 0, 4);
            M3Wire.Put(bytes, M3Wire.Payload(bytes, M5DWire.Cg(bytes)) + 24, uint.MaxValue, 4);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true, new MdfReaderOptions { MaximumSampleBatchBytes = cap }))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal((ulong)uint.MaxValue, group.Channels[2].Array.ElementCount);
                int before = source.ReadCalls;
                Assert.Empty(reader.ReadSamples(group, new[] { 2 }, startSample: 5, sampleCount: 0));
                Assert.Equal(before, source.ReadCalls);
                Assert.Null(group.Layout.DataMap);
                Assert.Null(group.Layout.ListDataMap);
                Assert.Equal("MaximumSampleBatchBytes", Assert.Throws<MdfResourceLimitException>(() => reader.ReadSamples(group, new[] { 2 }, sampleCount: 1)).LimitName);
                Assert.Equal(before, source.ReadCalls);
            }
        }

        [Theory]
        [InlineData(580, 1)]
        [InlineData(1160, 2)]
        public void InputBufferAlsoReducesArrayBatchRowsAndDoesNotAccumulateSampleMetadataBudget(int cap, int firstRows)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(M5DWire.Read(), false),
                options: new MdfReaderOptions { MaximumReadBufferBytes = cap }))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(firstRows, reader.ReadSamples(group, new[] { 8 }, batchSize: int.MaxValue).First().SampleCount);
                for (int i = 0; i < 100; i++)
                { Assert.Equal(5, reader.ReadSamples(group, new[] { 8, 1, 2 }, batchSize: int.MaxValue).Sum(b => b.SampleCount)); }
            }
        }

        [Fact]
        public void MixedSelectionOffsetsAreSnapshottedAndTwoArgumentGetterNeverSilentlyDropsArrayShape()
        {
            MdfSampleBatch saved;
            using (MdfReader reader = MdfReader.Open(new MemoryStream(M5DWire.Read(), false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                int[] indices = { 8, 1, 23, 2 };
                IEnumerable<MdfSampleBatch> request = reader.ReadSamples(group, indices, startSample: 1, sampleCount: 1);
                indices[0] = 0; indices[1] = 0;
                saved = Assert.Single(request);
                Assert.Equal(new[] { 8, 1, 23, 2 }, saved.Channels.Select(c => c.Index));
                Assert.Equal(1UL, saved.FirstSampleIndex);
                Assert.Equal(9007199254740993UL, saved.GetValue(0, 1).UnsignedValue);
                Assert.Equal(saved.GetValue(0, 1).UnsignedValue, saved.GetValue(0, 1, 0).UnsignedValue);
                Assert.Throws<InvalidOperationException>(() => saved.GetValue(0, 0));
                Assert.Throws<InvalidOperationException>(() => saved.GetValue(0, 2)); // Even the singleton array needs element zero.
                Assert.Throws<NotSupportedException>(() => ((IList<MdfChannel>)saved.Channels).Clear());
                Assert.Equal(5, reader.ReadSamples(group, new[] { 8 }, batchSize: 1).Sum(b => b.SampleCount));
            }
            Assert.Equal(255UL, saved.GetValue(0, 2, 0).UnsignedValue);
            Assert.False(saved.GetValue(0, 2, 0).IsValid);
            Assert.Equal(8UL, saved.Channels[0].Array.ElementCount);
        }

        [Theory]
        [InlineData(-1, 0, 0UL, "sampleIndex")]
        [InlineData(1, 0, 0UL, "sampleIndex")]
        [InlineData(0, -1, 0UL, "channelIndex")]
        [InlineData(0, 3, 0UL, "channelIndex")]
        [InlineData(0, 0, 8UL, "elementIndex")]
        [InlineData(0, 1, 1UL, "elementIndex")]
        [InlineData(0, 2, 1UL, "elementIndex")]
        [InlineData(0, 0, ulong.MaxValue, "elementIndex")]
        public void EveryExplicitElementIndexIsCheckedBeforeAccess(int row, int column, ulong element, string name)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(M5DWire.Read(), false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                MdfSampleBatch batch = reader.ReadSamples(group, new[] { 8, 1, 23 }, sampleCount: 1).Single();
                Assert.Equal(name, Assert.Throws<ArgumentOutOfRangeException>(() => batch.GetValue(row, column, element)).ParamName);
            }
        }

        [Theory]
        [InlineData("m4-asammdf330")]
        [InlineData("m5d-arrays410")]
        public void ExplicitElementZeroAlsoWorksForScalarOnlyBatchesWithoutAnOffsetsArray(string id)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(MdfLib.TestSupport.FixtureCorpus.Read(id), false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                MdfSampleBatch batch = reader.ReadSamples(group, new[] { 0 }, sampleCount: 1).Single();
                Assert.Equal(batch.GetValue(0, 0).FloatingPointValue, batch.GetValue(0, 0, 0).FloatingPointValue);
                Assert.Throws<ArgumentOutOfRangeException>(() => batch.GetValue(0, 0, 1));
            }
        }

        [Fact]
        public void LinearConversionAppliesPerElementAndSharesInvalidityWithoutErasingUnderlyingValues()
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(M5DWire.Read(), false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                MdfSampleBatch raw = reader.ReadSamples(group, new[] { 22 }, 2, 1).Single();
                MdfSampleBatch physical = reader.ReadSamples(group, new[] { 22 }, 2, 1, representation: MdfValueRepresentation.Physical).Single();
                Assert.Equal(1L, raw.GetValue(0, 0, 0).SignedValue);
                Assert.Equal(-37L, raw.GetValue(0, 0, 1).SignedValue);
                Assert.Equal(-1.75, physical.GetValue(0, 0, 0).FloatingPointValue);
                Assert.Equal(-11.25, physical.GetValue(0, 0, 1).FloatingPointValue);
                Assert.False(raw.GetValue(0, 0, 0).IsValid);
                Assert.False(physical.GetValue(0, 0, 1).IsValid);
            }
        }

        [Fact]
        public void UnsupportedArrayConversionAllowsRawAndRefusesPhysicalEagerly()
        {
            byte[] bytes = M5DWire.Read();
            int cc = (int)M3Wire.U64(bytes, M3Wire.Link(M5DWire.Cn(bytes, 22), 4));
            bytes[M3Wire.Payload(bytes, cc)] = 200; // unknown kind stays unsupported (3 is algebraic since)
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.True(group.Channels[22].CanReadRaw);
                Assert.False(group.Channels[22].CanReadPhysical);
                int before = source.ReadCalls;
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 22 }, representation: MdfValueRepresentation.Physical));
                Assert.Equal(before, source.ReadCalls);
                Assert.Equal(int.MinValue, reader.ReadSamples(group, new[] { 22 }, sampleCount: 1).Single().GetValue(0, 0, 0).SignedValue);
            }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(17)]
        public void ShortReadsDoNotChangeExactWideArrayValues(int maximum)
        {
            using (TestReadStream source = new TestReadStream(M5DWire.Read()) { MaximumBytesPerRead = maximum })
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(9007199254740993UL, reader.ReadSamples(group, new[] { 8 }, sampleCount: 1).Single().GetValue(0, 0, 6).UnsignedValue);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CancelOrDisposeDuringSampleIoCannotPublishAnArrayBatchAndReleasesTheGate(bool dispose)
        {
            using (TestReadStream source = new TestReadStream(M5DWire.Read()))
            using (MdfReader reader = MdfReader.Open(source, true))
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                source.AfterRead = () => { source.AfterRead = null; if (dispose) { reader.Dispose(); } else { cancellation.Cancel(); } };
                if (dispose) { Assert.Throws<ObjectDisposedException>(() => reader.ReadSamples(group, new[] { 8 }, cancellationToken: cancellation.Token).ToList()); }
                else
                {
                    OperationCanceledException error = Assert.ThrowsAny<OperationCanceledException>(() => reader.ReadSamples(group, new[] { 8 }, cancellationToken: cancellation.Token).ToList());
                    Assert.Equal(cancellation.Token, error.CancellationToken);
                    Assert.Equal(5, reader.ReadSamples(group, new[] { 8 }).Sum(b => b.SampleCount));
                }
                Assert.Equal(0, source.DisposeCalls);
            }
        }

        [Fact]
        public void ArrayEnumerationHasTheSameGateEarlyDisposeAndRepeatableIndependentBatches()
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(M5DWire.Read(), false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                IEnumerable<MdfSampleBatch> request = reader.ReadSamples(group, new[] { 8 }, batchSize: 1);
                MdfSampleBatch saved;
                using (IEnumerator<MdfSampleBatch> enumerator = request.GetEnumerator())
                {
                    Assert.True(enumerator.MoveNext());
                    saved = enumerator.Current;
                    Assert.Throws<InvalidOperationException>(() => reader.ReadMetadata());
                    Assert.Throws<InvalidOperationException>(() => reader.ReadSamples(group, new[] { 8 }));
                }
                Assert.Equal(5, request.Sum(b => b.SampleCount));
                Assert.Equal(9007199254740993UL, saved.GetValue(0, 0, 6).UnsignedValue);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TerminalMoveNextStillObservesCancellationOrDisposalForArrays(bool dispose)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(M5DWire.Read(), false)))
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                using (IEnumerator<MdfSampleBatch> e = reader.ReadSamples(group, new[] { 8 }, sampleCount: 1, cancellationToken: cancellation.Token).GetEnumerator())
                {
                    Assert.True(e.MoveNext());
                    if (dispose) { reader.Dispose(); Assert.Throws<ObjectDisposedException>(() => e.MoveNext()); }
                    else { cancellation.Cancel(); Assert.ThrowsAny<OperationCanceledException>(() => e.MoveNext()); }
                }
            }
        }

        [Fact]
        public void SampleIoCauseIsPreservedAndFailedMapDoesNotPublishAndCanBeRetried()
        {
            using (TestReadStream source = new TestReadStream(M5DWire.Read()))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                IOException cause = new IOException("actual array I/O failure");
                source.ReadFailure = cause;
                Assert.Same(cause, Assert.Throws<IOException>(() => reader.ReadSamples(group, new[] { 8 }).ToList()));
                Assert.Null(group.Layout.DataMap);
                source.ReadFailure = null;
                Assert.Equal(5, reader.ReadSamples(group, new[] { 8 }).Sum(b => b.SampleCount));
            }
        }

        [Fact]
        public void SampleCallbacksCannotReenterTheReaderEvenForDifferentSelectedShape()
        {
            using (TestReadStream source = new TestReadStream(M5DWire.Read()))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                bool invoked = false;
                source.AfterRead = () =>
                {
                    source.AfterRead = null;
                    invoked = true;
                    Assert.Throws<InvalidOperationException>(() => reader.ReadMetadata());
                    Assert.Throws<InvalidOperationException>(() => reader.ReadSamples(group, new[] { 1 }));
                };
                Assert.Equal(5, reader.ReadSamples(group, new[] { 8 }).Sum(b => b.SampleCount));
                Assert.True(invoked);
            }
        }

        [Fact]
        public void MultiTerabyteHighOrdinalArraysReadOnlyThreeRequestedRecordsWithBoundedOwnedOutput()
        {
            byte[] metadata = M5DWire.Read();
            const ulong cycles = (1UL << 34) + 17;
            const long root = 1L << 42;
            const ulong record = 580;
            ulong dataBytes = cycles * record;
            M3Wire.Put(metadata, M3Wire.Link(M5DWire.Dg(metadata), 2), (ulong)root);
            M3Wire.Put(metadata, M3Wire.Payload(metadata, M5DWire.Cg(metadata)) + 8, cycles);
            byte[] header = new byte[24];
            header[0] = header[1] = (byte)'#'; header[2] = (byte)'D'; header[3] = (byte)'T';
            M3Wire.Put(header, 8, dataBytes + 24);
            byte[] original = M5DWire.Read();
            byte[] last = M5DWire.Records(original).Take(3 * (int)record).ToArray();
            using (SparseReadStream source = new SparseReadStream(root + 24 + (long)dataBytes))
            {
                source.Add(0, metadata);
                source.Add(root, header);
                source.Add(root + 24 + (long)((cycles - 3) * record), last);
                MdfReaderOptions options = new MdfReaderOptions { MaximumReadBufferBytes = 1160, MaximumSampleBatchBytes = 1520 };
                using (MdfReader reader = MdfReader.Open(source, true, options))
                {
                    MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                    long before = source.BytesRead;
                    IEnumerable<MdfSampleBatch> request = reader.ReadSamples(group, new[] { 8, 1, 2 }, cycles - 3, 3, int.MaxValue);
                    options.MaximumReadBufferBytes = 1; options.MaximumSampleBatchBytes = 1;
                    Assert.Equal(before, source.BytesRead);
                    MdfSampleBatch[] batches = request.ToArray();
                    Assert.Equal(new[] { 2, 1 }, batches.Select(b => b.SampleCount));
                    Assert.Equal(cycles - 3, batches[0].FirstSampleIndex);
                    Assert.Equal(9007199254740993UL, batches[0].GetValue(0, 0, 6).UnsignedValue);
                    Assert.Equal(9007199254740993UL, batches[0].GetValue(1, 1).UnsignedValue);
                    Assert.Equal(24L + 3 * (long)record, source.BytesRead - before);
                    before = source.BytesRead;
                    Assert.Equal(3, request.Sum(b => b.SampleCount));
                    Assert.Equal(3 * (long)record, source.BytesRead - before);
                    Assert.InRange(source.LargestReadRequest, 1, 1160);
                }
            }
        }
    }
}
