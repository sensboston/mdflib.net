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
    public sealed class SampleReaderTests
    {
        [Fact]
        public void SelectionIsSnapshottedEagerlyAndBatchesAreIndependentAfterDispose()
        {
            MdfReader reader = MdfReader.Open(new MemoryStream(M3Wire.Read(), false));
            MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
            List<int> indices = new List<int> { 3, 4, 8 };
            IEnumerable<MdfSampleBatch> pending = reader.ReadSamples(group, indices, batchSize: 1);
            indices.Clear(); indices.Add(0);
            List<MdfSampleBatch> batches = pending.ToList();
            reader.Dispose();
            Assert.Equal(5, batches.Count);
            Assert.Same(group.Channels[3], batches[0].Channels[0]);
            Assert.Equal(0UL, batches[0].GetValue(0, 0).UnsignedValue);
            Assert.Equal(long.MinValue, batches[0].GetValue(0, 1).SignedValue);
            Assert.Equal(ulong.MaxValue, batches[3].GetValue(0, 0).UnsignedValue);
            Assert.Equal(long.MaxValue, batches[4].GetValue(0, 1).SignedValue);
            Assert.Equal(0UL, batches[0].GetValue(0, 0).UnsignedValue); // Later buffer reuse did not mutate it.
            Assert.Throws<NotSupportedException>(() => ((IList<MdfChannel>)batches[0].Channels).Clear());
            Assert.Throws<ObjectDisposedException>(() => reader.ReadMetadata());
            Assert.Throws<ObjectDisposedException>(() => reader.ReadSamples(group, new[] { 0 }));
            Assert.Throws<ObjectDisposedException>(() => pending.ToList());
        }

        [Fact]
        public void RequestsAreLazyRepeatableAndZeroRangesPerformNoDtIo()
        {
            byte[] bytes = M3Wire.Read();
            bytes[M3Wire.Dt + 2] = (byte)'D'; bytes[M3Wire.Dt + 3] = (byte)'Z';
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                int before = source.ReadCalls;
                IEnumerable<MdfSampleBatch> pending = reader.ReadSamples(group, new[] { 0 });
                Assert.Equal(before, source.ReadCalls);
                Assert.Empty(reader.ReadSamples(group, new[] { 0 }, group.SampleCount));
                Assert.Empty(reader.ReadSamples(group, new[] { 0 }, sampleCount: 0));
                Assert.Equal(before, source.ReadCalls);
                Assert.Throws<NotSupportedException>(() => pending.ToList());
                // Failure released the gate. A second attempt still refuses, not identity/empty data.
                Assert.Throws<NotSupportedException>(() => pending.ToList());
                Assert.Same(group, reader.ReadMetadata()[0].ChannelGroups[0]);
            }
        }

        [Fact]
        public void InvalidArgumentsForeignGroupsAndUnsupportedEnumsFailBeforeDataIo()
        {
            using (TestReadStream source = new TestReadStream(M3Wire.Read()))
            using (MdfReader reader = MdfReader.Open(source, true))
            using (MdfReader other = MdfReader.Open(new MemoryStream(M3Wire.Read(), false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                MdfChannelGroup foreign = other.ReadMetadata()[0].ChannelGroups[0];
                int before = source.ReadCalls;
                Assert.Throws<ArgumentNullException>(() => reader.ReadSamples(null, new[] { 0 }));
                Assert.Throws<ArgumentException>(() => reader.ReadSamples(foreign, new[] { 0 }));
                Assert.Throws<ArgumentNullException>(() => reader.ReadSamples(group, null));
                Assert.Throws<ArgumentException>(() => reader.ReadSamples(group, new int[0]));
                Assert.Throws<ArgumentException>(() => reader.ReadSamples(group, new[] { 0, 0 }));
                Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadSamples(group, new[] { -1 }));
                Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadSamples(group, new[] { group.Channels.Count }));
                Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadSamples(group, new[] { 0 }, 6));
                Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadSamples(group, new[] { 0 }, 4, 2));
                Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadSamples(group, new[] { 0 }, batchSize: 0));
                Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadSamples(group, new[] { 0 }, representation: (MdfValueRepresentation)3));
                Assert.Equal(before, source.ReadCalls);
            }
        }

        [Theory]
        [InlineData(0, 4, 1)] // sync role without sync type/AT link (refusal; virtual 3/6 are)
        [InlineData(2, 13, 1)] // CANopen/complex/unknown encoding (dt6-10 are text/byte kinds, 11/12 MIME byte arrays)
        [InlineData(12, 512, 4)] // virtual flag
        public void UnsupportedSelectedFieldsDoNotPreventReadingOtherStoredChannels(int field, int value, int width)
        {
            byte[] bytes = M3Wire.Read();
            M3Wire.Put(bytes, M3Wire.Payload(bytes, 528) + field, (ulong)value, width);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.False(group.Channels[1].CanReadRaw);
                Assert.NotNull(group.Channels[1].UnsupportedReason);
                int before = source.ReadCalls;
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 1 }));
                Assert.Equal(before, source.ReadCalls);
                Assert.Equal(5, reader.ReadSamples(group, new[] { 3 }).Sum(batch => batch.SampleCount));
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void UnsupportedRecordWidthsOrExtendedGroupsRemainInspectableButCannotReadSamples(bool recordId)
        {
            byte[] bytes = M3Wire.Read();
            if (recordId) { bytes[M3Wire.Payload(bytes, M3Wire.Dg)] = 3; }
            else { M3Wire.Put(bytes, M3Wire.Payload(bytes, M3Wire.Cg) + 16, 1, 2); }
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.False(group.CanReadSamples);
                Assert.NotNull(group.UnsupportedReason);
                int before = source.ReadCalls;
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 0 }));
                Assert.Equal(before, source.ReadCalls);
            }
        }

        [Fact]
        public void MultipleChannelGroupsAreNeverMisreadAsSortedSingleGroupData()
        {
            byte[] bytes = M3Wire.Read();
            M3Wire.Put(bytes, M3Wire.Link(M3Wire.Dg, 0), 0);
            M3Wire.Put(bytes, M3Wire.Link(M3Wire.Cg, 0), M3Wire.Cg2);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                IReadOnlyList<MdfChannelGroup> groups = reader.ReadMetadata()[0].ChannelGroups;
                Assert.Equal(2, groups.Count);
                Assert.All(groups, group =>
                {
                    Assert.False(group.CanReadSamples);
                    Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 0 }));
                });
            }
        }

        [Fact]
        public void UnsupportedConversionAllowsExactRawButNeverPretendsToBePhysicalIdentity()
        {
            byte[] bytes = M3Wire.Read();
            bytes[M3Wire.Payload(bytes, M3Wire.Cc)] = 200; // unknown kind stays unsupported (2 rational, 3 algebraic).
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.True(group.Channels[8].CanReadRaw);
                Assert.False(group.Channels[8].CanReadPhysical);
                Assert.Equal(MdfConversionKind.Unsupported, group.Channels[8].Conversion.Kind);
                Assert.Null(group.Channels[8].Conversion.Factor);
                Assert.Null(group.Channels[8].Conversion.Offset);
                int before = source.ReadCalls;
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 8 }, representation: MdfValueRepresentation.Physical));
                Assert.Equal(before, source.ReadCalls);
                Assert.Equal(-32768L, reader.ReadSamples(group, new[] { 8 }, sampleCount: 1).Single().GetValue(0, 0).SignedValue);
            }
        }

        [Theory]
        [InlineData("count")]
        [InlineData("extra")]
        [InlineData("short")]
        [InlineData("missing")]
        [InlineData("links")]
        public void DtLengthLinksAndCycleCountsAreValidatedLazilyBeforeRecordAllocation(string mutation)
        {
            byte[] bytes = M3Wire.Read();
            if (mutation == "count") { M3Wire.Put(bytes, M3Wire.Payload(bytes, M3Wire.Cg) + 8, ulong.MaxValue); }
            else if (mutation == "extra") { M3Wire.Put(bytes, M3Wire.Dt + 8, 260); }
            else if (mutation == "short") { M3Wire.Put(bytes, M3Wire.Dt + 8, 258); }
            else if (mutation == "missing") { M3Wire.Put(bytes, M3Wire.Link(M3Wire.Dg, 2), 0); }
            else { M3Wire.Put(bytes, M3Wire.Dt + 16, 1); }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                IEnumerable<MdfSampleBatch> pending = reader.ReadSamples(group, new[] { 0 });
                Assert.Equal(mutation == "links" ? "MDF006" : "MDF013", Assert.Throws<MdfFormatException>(() => pending.ToList()).DiagnosticCode);
            }
        }

        [Fact]
        public void ConcurrentEnumerationsAndMetadataUseOneGateUntilEnumeratorDispose()
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(M3Wire.Read(), false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                IEnumerable<MdfSampleBatch> first = reader.ReadSamples(group, new[] { 0 }, batchSize: 1);
                IEnumerable<MdfSampleBatch> second = reader.ReadSamples(group, new[] { 3 });
                using (IEnumerator<MdfSampleBatch> active = first.GetEnumerator())
                {
                    Assert.True(active.MoveNext());
                    Assert.Throws<InvalidOperationException>(() => reader.ReadMetadata());
                    Assert.Throws<InvalidOperationException>(() => reader.ReadSamples(group, new[] { 0 }));
                    Assert.Throws<InvalidOperationException>(() => second.ToList());
                    Assert.True(active.MoveNext()); // A rejected competing operation did not release this gate.
                }
                Assert.Equal(5, second.Sum(batch => batch.SampleCount));
                Assert.Same(group, reader.ReadMetadata()[0].ChannelGroups[0]);
            }
        }

        [Fact]
        public void ShortReadsCancellationIoErrorsAndActualTruncationReleaseTheGateWithoutClosingReader()
        {
            using (TestReadStream source = new TestReadStream(M3Wire.Read()) { MaximumBytesPerRead = 1 })
            using (MdfReader reader = MdfReader.Open(source, true))
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                IEnumerable<MdfSampleBatch> pending = reader.ReadSamples(group, new[] { 0 }, cancellationToken: cancellation.Token);
                source.AfterRead = cancellation.Cancel;
                Assert.Throws<OperationCanceledException>(() => pending.ToList());
                source.AfterRead = null;
                int before = source.ReadCalls;
                Assert.Throws<OperationCanceledException>(() => reader.ReadSamples(group, new[] { 0 }, cancellationToken: cancellation.Token));
                Assert.Equal(before, source.ReadCalls);
                IOException cause = new IOException("injected sample I/O");
                source.ReadFailure = cause;
                Assert.Same(cause, Assert.Throws<IOException>(() => reader.ReadSamples(group, new[] { 0 }).ToList()));
                source.ReadFailure = null;
                Assert.Equal(5, reader.ReadSamples(group, new[] { 0 }).Sum(batch => batch.SampleCount));
                source.AvailableLength = M3Wire.Dt + 25;
                MdfFormatException truncated = Assert.Throws<MdfFormatException>(() => reader.ReadSamples(group, new[] { 0 }).ToList());
                Assert.Equal("MDF004", truncated.DiagnosticCode);
                Assert.IsType<EndOfStreamException>(truncated.InnerException);
                source.AvailableLength = source.ReportedLength;
                Assert.Equal(5, reader.ReadSamples(group, new[] { 0 }).Sum(batch => batch.SampleCount));
                Assert.Equal(0, source.DisposeCalls);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void DisposalIsObservedBeforeNextBatchIncludingAfterTheLastYield(bool oneRow)
        {
            TestReadStream source = new TestReadStream(M3Wire.Read());
            MdfReader reader = MdfReader.Open(source);
            MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
            using (IEnumerator<MdfSampleBatch> samples = reader.ReadSamples(group, new[] { 0 }, sampleCount: oneRow ? 1UL : 5UL, batchSize: 1).GetEnumerator())
            {
                Assert.True(samples.MoveNext());
                MdfSampleBatch snapshot = samples.Current;
                reader.Dispose(); reader.Dispose();
                Assert.Equal(1, source.DisposeCalls);
                Assert.Throws<ObjectDisposedException>(() => samples.MoveNext());
                Assert.Equal(0.0, snapshot.GetValue(0, 0).FloatingPointValue);
                Assert.Equal("time", group.Channels[0].Name);
            }
        }

        [Theory]
        [InlineData(200, "MaximumSampleBatchBytes")]
        [InlineData(300, "MaximumReadBufferBytes")]
        public void EvenOneOutputRowOrRecordMustFitBeforeAnyDataRead(int sampleBudget, string limit)
        {
            byte[] bytes = M3Wire.Read();
            MdfReaderOptions options = new MdfReaderOptions { MaximumSampleBatchBytes = sampleBudget, MaximumReadBufferBytes = 128 };
            if (limit == "MaximumReadBufferBytes")
            {
                // Metadata stays small, but the declared fixed record includes 200 data bytes.
                M3Wire.Put(bytes, M3Wire.Payload(bytes, M3Wire.Cg) + 24, 200, 4);
            }
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true, options))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                int before = source.ReadCalls;
                int[] selection = limit == "MaximumSampleBatchBytes" ? new[] { 0, 1 } : new[] { 0 };
                Assert.Equal(limit, Assert.Throws<MdfResourceLimitException>(() => reader.ReadSamples(group, selection)).LimitName);
                Assert.Equal(before, source.ReadCalls);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ReentrantSampleCallbacksCannotEnterAnotherOperationAndDisposalCannotYieldABatch(bool dispose)
        {
            using (TestReadStream source = new TestReadStream(M3Wire.Read()))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                bool invoked = false;
                source.AfterRead = () =>
                {
                    invoked = true;
                    source.AfterRead = null;
                    if (dispose) { reader.Dispose(); }
                    else
                    {
                        Assert.Throws<InvalidOperationException>(() => reader.ReadMetadata());
                        Assert.Throws<InvalidOperationException>(() => reader.ReadSamples(group, new[] { 1 }));
                    }
                };
                if (dispose) { Assert.Throws<ObjectDisposedException>(() => reader.ReadSamples(group, new[] { 0 }).ToList()); }
                else { Assert.Single(reader.ReadSamples(group, new[] { 0 }, sampleCount: 1)); }
                Assert.True(invoked);
                Assert.Equal(0, source.DisposeCalls); // leaveOpen applies even during a reentrant reader Dispose.
            }
        }

        [Theory]
        [InlineData(0, 65)]
        [InlineData(4, 8)]   // 16-bit IEEE is binary16 and readable; 8-bit IEEE has no format
        [InlineData(5, 32)]
        public void WiderIntegersOddWidthFloatsAndUnalignedIeeeAreExplicitlyUnsupported(int dataType, int bitCount)
        {
            byte[] bytes = M3Wire.Read();
            int payload = M3Wire.Payload(bytes, 528);
            bytes[payload + 2] = (byte)dataType;
            bytes[payload + 3] = (byte)(dataType == 5 ? 1 : 0);
            M3Wire.Put(bytes, payload + 8, (ulong)bitCount, 4);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.False(group.Channels[1].CanReadRaw);
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 1 }));
                Assert.Equal(5, reader.ReadSamples(group, new[] { 0 }).Sum(batch => batch.SampleCount));
            }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(5)]
        public void CompositionAndChannelSpecificDataLinksDoNotFallBackToNumericDecoding(int link)
        {
            byte[] bytes = M3Wire.Read();
            M3Wire.Put(bytes, M3Wire.Link(528, link), 528); // The optional edge is deliberately cyclic, but not followed by this subset.
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.False(group.Channels[1].CanReadRaw);
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 1 }));
            }
        }

        [Fact]
        public void CancellationAfterTheLastYieldIsStillObservedAndGateIsReleased()
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(M3Wire.Read(), false)))
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                using (IEnumerator<MdfSampleBatch> enumerator = reader.ReadSamples(group, new[] { 3 }, sampleCount: 1, cancellationToken: cancellation.Token).GetEnumerator())
                {
                    Assert.True(enumerator.MoveNext());
                    cancellation.Cancel();
                    Assert.Throws<OperationCanceledException>(() => enumerator.MoveNext());
                }
                Assert.Same(group, reader.ReadMetadata()[0].ChannelGroups[0]);
            }
        }

        [Fact]
        public void ValueGettersAndBatchIndicesDoNotSilentlyCoerceOrOverflow()
        {
            MdfSampleValue empty = default(MdfSampleValue);
            Assert.False(empty.IsValid);
            Assert.Equal(0UL, empty.UnsignedValue);
            Assert.Throws<InvalidOperationException>(() => empty.SignedValue);
            Assert.Throws<InvalidOperationException>(() => empty.FloatingPointValue);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(M3Wire.Read(), false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                MdfSampleBatch batch = reader.ReadSamples(group, new[] { 1, 2, 5 }, sampleCount: 1).Single();
                Assert.Throws<InvalidOperationException>(() => batch.GetValue(0, 1).UnsignedValue);
                Assert.Throws<InvalidOperationException>(() => batch.GetValue(0, 2).SignedValue);
                Assert.Throws<ArgumentOutOfRangeException>(() => batch.GetValue(-1, 0));
                Assert.Throws<ArgumentOutOfRangeException>(() => batch.GetValue(1, 0));
                Assert.Throws<ArgumentOutOfRangeException>(() => batch.GetValue(0, -1));
                Assert.Throws<ArgumentOutOfRangeException>(() => batch.GetValue(0, int.MaxValue));
            }
        }
    }
}
