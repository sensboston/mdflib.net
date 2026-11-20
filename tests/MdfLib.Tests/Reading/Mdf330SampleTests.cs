// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Reading
{
    public sealed class Mdf330SampleTests
    {
        [Theory]
        [InlineData(0, false)]
        [InlineData(0, true)]
        [InlineData(1, false)]
        [InlineData(1, true)]
        [InlineData(9, false)]
        [InlineData(9, true)]
        [InlineData(10, false)]
        [InlineData(10, true)]
        [InlineData(13, false)]
        [InlineData(13, true)]
        [InlineData(14, false)]
        [InlineData(14, true)]
        public void AllMdf3IntegerWireKindsWidthsAndOffsetsNormalizeToTheSameExactCommonDecoder(int wireKind, bool defaultBigEndian)
        {
            bool big = wireKind <= 1 ? defaultBigEndian : wireKind == 9 || wireKind == 10;
            bool signed = wireKind == 1 || wireKind == 10 || wireKind == 14;
            byte[] template = M4Wire.ScalarTemplate();
            Random random = new Random(330734);
            for (int bits = 1; bits <= 64; bits++)
            {
                for (int shift = 0; shift <= 7; shift++)
                {
                    byte[] bytes = (byte[])template.Clone();
                    M4Wire.Put(bytes, 24, defaultBigEndian ? 1UL : 0, 2);
                    M4Wire.Put(bytes, M4Wire.Cn + 186, (ulong)shift, 2);
                    M4Wire.Put(bytes, M4Wire.Cn + 188, (ulong)bits, 2);
                    M4Wire.Put(bytes, M4Wire.Cn + 190, (ulong)wireKind, 2);
                    byte[] record = new byte[9];
                    random.NextBytes(record);
                    Array.Copy(record, 0, bytes, M4Wire.ScalarRaw, record.Length);
                    int span = (bits + shift + 7) / 8;
                    byte[] positive = new byte[span + 1];
                    Array.Copy(record, positive, span);
                    if (big) { Array.Reverse(positive, 0, span); }
                    BigInteger expected = (new BigInteger(positive) >> shift) & ((BigInteger.One << bits) - 1);
                    if (signed && expected >= (BigInteger.One << (bits - 1))) { expected -= BigInteger.One << bits; }
                    using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
                    {
                        MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                        MdfSampleValue value = reader.ReadSamples(group, new[] { 0 }, representation: MdfValueRepresentation.Physical).Single().GetValue(0, 0);
                        Assert.True(value.IsValid);
                        Assert.Equal((uint)bits, group.Channels[0].BitCount);
                        if (signed) { Assert.Equal((long)expected, value.SignedValue); }
                        else { Assert.Equal((ulong)expected, value.UnsignedValue); }
                    }
                }
            }
        }

        [Theory]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(17)]
        [InlineData(255)]
        [InlineData(65535)]
        public void UnknownOrNonIeeeEncodingsRemainInspectableButNeverBecomeNumericSamples(int wireKind)
        {
            byte[] bytes = M4Wire.Read();
            M4Wire.Put(bytes, M4Wire.SecondCn + 190, (ulong)wireKind, 2);
            UnsupportedNeighbor(bytes);
        }

        [Theory]
        [InlineData("role")]
        [InlineData("wide")]
        [InlineData("float-shift")]
        [InlineData("float-width")]
        public void UnsupportedSelectedLayoutsDoNotDisableOtherStoredNeighbors(string mutation)
        {
            byte[] bytes = M4Wire.Read();
            if (mutation == "role") { M4Wire.Put(bytes, M4Wire.SecondCn + 24, 2, 2); }
            else if (mutation == "wide") { M4Wire.Put(bytes, M4Wire.SecondCn + 188, 65, 2); }
            else
            {
                M4Wire.Put(bytes, M4Wire.SecondCn + 190, 2, 2);
                M4Wire.Put(bytes, M4Wire.SecondCn + 188, mutation == "float-width" ? 64UL : 32UL, 2);
                if (mutation == "float-shift") { M4Wire.Put(bytes, M4Wire.SecondCn + 186, 65, 2); }
            }
            UnsupportedNeighbor(bytes);
        }

        [Theory]
        [InlineData(188, 0)]
        [InlineData(186, 65535)]
        [InlineData(226, 65535)]
        [InlineData(188, 65535)]
        public void NumericRangesAreValidatedBeforeAnyDataEnumeration(int field, int value)
        {
            byte[] bytes = M4Wire.Read();
            M4Wire.Put(bytes, M4Wire.SecondCn + field, (ulong)value, 2);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                Assert.Equal("MDF013", Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
            }
        }

        [Theory]
        [InlineData(3)]
        [InlineData(17)]
        [InlineData(65535)]
        public void UnsupportedRecordIdCountsAreRejectedBeforeSampleIo(int recordIds)
        {
            byte[] bytes = M4Wire.Read();
            M4Wire.Put(bytes, M4Wire.Dg + 22, (ulong)recordIds, 2);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.False(group.CanReadSamples);
                int before = source.ReadCalls;
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 0 }));
                Assert.Equal(before, source.ReadCalls);
            }
        }

        [Fact]
        public void MultipleCgLayoutsAreNotGuessedAsOneSortedStream()
        {
            byte[] bytes = M4Wire.Read();
            M4Wire.FirstOnly(bytes);
            M4Wire.Put(bytes, M4Wire.Dg + 20, 2, 2);
            M4Wire.Put(bytes, M4Wire.Cg + 4, M4Wire.Cg2);
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
        public void UnknownConversionsAllowExactRawButNeverPretendToBePhysicalIdentity()
        {
            byte[] bytes = M4Wire.Read();
            M4Wire.Put(bytes, M4Wire.LinearCc + 42, 10, 2);
            M4Wire.Put(bytes, M4Wire.LinearCc + 44, 65535, 2); // Opaque unsupported formula tail, not an allocation count.
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                MdfChannel channel = group.Channels[9];
                Assert.True(channel.CanReadRaw);
                Assert.False(channel.CanReadPhysical);
                Assert.Equal(MdfConversionKind.Unsupported, channel.Conversion.Kind);
                Assert.Null(channel.Conversion.Factor);
                Assert.Null(channel.Conversion.Offset);
                int before = source.ReadCalls;
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 9 }, representation: MdfValueRepresentation.Physical));
                Assert.Equal(before, source.ReadCalls);
                Assert.Equal(-32768L, reader.ReadSamples(group, new[] { 9 }, sampleCount: 1).Single().GetValue(0, 0).SignedValue);
            }
        }

        [Fact]
        public void RequestsAreEagerlyValidatedLazyRepeatableAndSnapshotsSurviveDisposal()
        {
            TestReadStream source = new TestReadStream(M4Wire.Read());
            MdfReader reader = MdfReader.Open(source);
            MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
            using (MdfReader other = MdfReader.Open(new MemoryStream(M4Wire.Read(), false)))
            {
                int before = source.ReadCalls;
                Assert.Throws<ArgumentException>(() => reader.ReadSamples(other.ReadMetadata()[0].ChannelGroups[0], new[] { 0 }));
                Assert.Throws<ArgumentException>(() => reader.ReadSamples(group, new[] { 1, 1 }));
                Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadSamples(group, new[] { 16 }));
                Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadSamples(group, new[] { 1 }, 4, 2));
                List<int> selected = new List<int> { 1, 6, 10 };
                IEnumerable<MdfSampleBatch> request = reader.ReadSamples(group, selected, 1, 3, 1);
                selected.Clear(); selected.Add(0);
                Assert.Equal(before, source.ReadCalls);
                Assert.Empty(reader.ReadSamples(group, new[] { 0 }, group.SampleCount));
                Assert.Empty(reader.ReadSamples(group, new[] { 0 }, sampleCount: 0));
                Assert.Equal(before, source.ReadCalls);
                MdfSampleBatch[] snapshots = request.ToArray();
                Assert.Equal(3, snapshots.Length);
                Assert.Equal(9_007_199_254_740_993UL, snapshots[0].GetValue(0, 0).UnsignedValue);
                Assert.Equal(-9_007_199_254_740_993L, snapshots[0].GetValue(0, 1).SignedValue);
                Assert.Equal(9_007_199_254_740_993UL, snapshots[0].GetValue(0, 2).UnsignedValue);
                Assert.Equal(ulong.MaxValue, snapshots[2].GetValue(0, 0).UnsignedValue);
                Assert.Equal(3, request.Sum(batch => batch.SampleCount));
                reader.Dispose(); reader.Dispose();
                Assert.Equal(1, source.DisposeCalls);
                Assert.Equal("default_counter", snapshots[0].Channels[0].Name);
                Assert.Equal(9_007_199_254_740_993UL, snapshots[0].GetValue(0, 0).UnsignedValue);
                Assert.Throws<ObjectDisposedException>(() => reader.ReadMetadata());
                Assert.Throws<ObjectDisposedException>(() => request.ToList());
            }
        }

        [Fact]
        public void OperationGateIsSharedAcrossMetadataAndAllHeaderlessEnumerations()
        {
            using (TestReadStream source = new TestReadStream(M4Wire.Read()))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                IEnumerable<MdfSampleBatch> first = reader.ReadSamples(group, new[] { 0 }, batchSize: 1);
                IEnumerable<MdfSampleBatch> second = reader.ReadSamples(group, new[] { 1 });
                bool reentered = false;
                source.AfterRead = () =>
                {
                    reentered = true;
                    Assert.Throws<InvalidOperationException>(() => reader.ReadMetadata());
                    Assert.Throws<InvalidOperationException>(() => reader.ReadSamples(group, new[] { 0 }));
                    source.AfterRead = null;
                };
                using (IEnumerator<MdfSampleBatch> samples = first.GetEnumerator())
                {
                    Assert.True(samples.MoveNext());
                    Assert.True(reentered);
                    Assert.Throws<InvalidOperationException>(() => second.ToList());
                    Assert.True(samples.MoveNext());
                }
                Assert.Equal(5, second.Sum(batch => batch.SampleCount));
                Assert.Same(group, reader.ReadMetadata()[0].ChannelGroups[0]);
            }
        }

        [Fact]
        public void OneByteShortReadsCancellationIoErrorsAndTruncationReleaseTheGate()
        {
            using (TestReadStream source = new TestReadStream(M4Wire.Read()) { MaximumBytesPerRead = 1 })
            using (MdfReader reader = MdfReader.Open(source, true))
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                source.AfterRead = cancellation.Cancel;
                Assert.Throws<OperationCanceledException>(() => reader.ReadSamples(group, new[] { 1 }, cancellationToken: cancellation.Token).ToList());
                source.AfterRead = null;
                IOException cause = new IOException("injected MDF 3 sample I/O");
                source.ReadFailure = cause;
                Assert.Same(cause, Assert.Throws<IOException>(() => reader.ReadSamples(group, new[] { 1 }).ToList()));
                source.ReadFailure = null;
                source.AvailableLength = M4Wire.Raw + 1;
                MdfFormatException error = Assert.Throws<MdfFormatException>(() => reader.ReadSamples(group, new[] { 1 }).ToList());
                Assert.Equal("MDF004", error.DiagnosticCode);
                Assert.IsType<EndOfStreamException>(error.InnerException);
                source.AvailableLength = source.ReportedLength;
                Assert.Equal(5, reader.ReadSamples(group, new[] { 1 }).Sum(batch => batch.SampleCount));
                int before = source.ReadCalls;
                source.ReportedLength = M4Wire.Raw + 1;
                Assert.Equal("MDF007", Assert.Throws<MdfFormatException>(() => reader.ReadSamples(group, new[] { 1 }).ToList()).DiagnosticCode);
                Assert.Equal(before, source.ReadCalls); // Even a cached raw extent revalidates length before reading.
                Assert.Equal(0, source.DisposeCalls);
            }
        }

        [Fact]
        public void LiveLengthIoFailuresAreNotMaskedAndZeroRangesDoNotQueryDataStorage()
        {
            using (TestReadStream source = new TestReadStream(M4Wire.Read()))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                int before = source.ReadCalls;
                IOException cause = new IOException("live data-range length fault");
                source.LengthFailure = cause;
                Assert.Empty(reader.ReadSamples(group, new[] { 0 }, sampleCount: 0));
                Assert.Same(cause, Assert.Throws<IOException>(() => reader.ReadSamples(group, new[] { 0 }).ToList()));
                Assert.Same(group, reader.ReadMetadata()[0].ChannelGroups[0]);
                Assert.Equal(before, source.ReadCalls);
                source.LengthFailure = null;
                source.ReportedLength = -1;
                Assert.Throws<IOException>(() => reader.ReadSamples(group, new[] { 0 }).ToList());
                source.ReportedLength = 21561;
                Assert.Equal(5, reader.ReadSamples(group, new[] { 0 }).Sum(batch => batch.SampleCount));
                Assert.Equal(0, source.DisposeCalls);
            }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void DisposalAndCancellationAreObservedAfterAnyYieldIncludingTheFinalOne(bool cancel, bool final)
        {
            using (TestReadStream source = new TestReadStream(M4Wire.Read()))
            using (MdfReader reader = MdfReader.Open(source, true))
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                using (IEnumerator<MdfSampleBatch> samples = reader.ReadSamples(group, new[] { 1 }, sampleCount: final ? 1UL : 5UL,
                    batchSize: 1, cancellationToken: cancellation.Token).GetEnumerator())
                {
                    Assert.True(samples.MoveNext());
                    MdfSampleBatch snapshot = samples.Current;
                    if (cancel)
                    {
                        cancellation.Cancel();
                        Assert.Throws<OperationCanceledException>(() => samples.MoveNext());
                        Assert.Same(group, reader.ReadMetadata()[0].ChannelGroups[0]);
                    }
                    else
                    {
                        reader.Dispose();
                        Assert.Throws<ObjectDisposedException>(() => samples.MoveNext());
                    }
                    Assert.Equal(0UL, snapshot.GetValue(0, 0).UnsignedValue);
                    Assert.True(snapshot.GetValue(0, 0).IsValid);
                }
            }
        }

        [Fact]
        public void HugeVirtualMdf3CountAndHighUInt32AddressReadOnlyThreeRequestedRecords()
        {
            const ulong cycles = uint.MaxValue;
            const uint address = 0xfffff000;
            const ulong recordBytes = 88;
            byte[] metadata = M4Wire.Read();
            M4Wire.FirstOnly(metadata);
            M4Wire.Put(metadata, M4Wire.Dg + 16, address);
            M4Wire.Put(metadata, M4Wire.Cg + 22, cycles);
            byte[] lastRecords = new byte[3 * (int)recordBytes];
            Array.Copy(metadata, M4Wire.Raw, lastRecords, 0, lastRecords.Length);
            long end = (long)((ulong)address + cycles * recordBytes);
            using (SparseReadStream source = new SparseReadStream(end + 19)) // Unrelated trailing bytes are not another record.
            {
                source.Add(0, metadata);
                source.Add(end - lastRecords.Length, lastRecords);
                MdfReaderOptions options = new MdfReaderOptions { MaximumReadBufferBytes = 264, MaximumSampleBatchBytes = 240 };
                using (MdfReader reader = MdfReader.Open(source, true, options))
                {
                    MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                    Assert.Equal(cycles, group.SampleCount);
                    long before = source.BytesRead;
                    options.MaximumReadBufferBytes = 1; options.MaximumSampleBatchBytes = 1;
                    IEnumerable<MdfSampleBatch> request = reader.ReadSamples(group, new[] { 1 }, cycles - 3, 3, int.MaxValue);
                    Assert.Equal(before, source.BytesRead);
                    MdfSampleBatch[] batches = request.ToArray();
                    Assert.Equal(new[] { 2, 1 }, batches.Select(batch => batch.SampleCount));
                    Assert.Equal(cycles - 3, batches[0].FirstSampleIndex);
                    Assert.Equal(cycles - 1, batches[1].FirstSampleIndex);
                    Assert.Equal(0UL, batches[0].GetValue(0, 0).UnsignedValue);
                    Assert.Equal(9_007_199_254_740_993UL, batches[0].GetValue(1, 0).UnsignedValue);
                    Assert.Equal(1UL << 63, batches[1].GetValue(0, 0).UnsignedValue);
                    Assert.Equal((long)lastRecords.Length, source.BytesRead - before); // No invented 4/24-byte DT header.
                    before = source.BytesRead;
                    Assert.Equal(3, request.Sum(batch => batch.SampleCount));
                    Assert.Equal((long)lastRecords.Length, source.BytesRead - before);
                    Assert.InRange(source.LargestReadRequest, 1, 264);
                }
            }
        }

        [Fact]
        public void InputAndOutputBudgetsBoundEachBatchWithoutCumulativeSampleCharges()
        {
            using (TestReadStream source = new TestReadStream(M4Wire.Read()))
            using (MdfReader reader = MdfReader.Open(source, true, new MdfReaderOptions { MaximumReadBufferBytes = 256 }))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(new[] { 2, 2, 1 }, reader.ReadSamples(group, new[] { 0 }, batchSize: int.MaxValue).Select(batch => batch.SampleCount));
                for (int index = 0; index < 100; index++) { Assert.Equal(5, reader.ReadSamples(group, new[] { 1 }).Sum(batch => batch.SampleCount)); }
                Assert.InRange(source.LargestReadRequest, 1, 256);
            }
            using (TestReadStream source = new TestReadStream(M4Wire.Read()))
            using (MdfReader reader = MdfReader.Open(source, true, new MdfReaderOptions { MaximumSampleBatchBytes = 199 }))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                int before = source.ReadCalls;
                Assert.Equal("MaximumSampleBatchBytes", Assert.Throws<MdfResourceLimitException>(() => reader.ReadSamples(group, new[] { 0 })).LimitName);
                Assert.Empty(reader.ReadSamples(group, new[] { 0 }, sampleCount: 0));
                Assert.Equal(before, source.ReadCalls);
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(M4Wire.Read(), false), options: new MdfReaderOptions { MaximumReadBufferBytes = 256 }))
            {
                MdfChannelGroup high = reader.ReadMetadata()[1].ChannelGroups[0];
                Assert.Equal("MaximumReadBufferBytes", Assert.Throws<MdfResourceLimitException>(() => reader.ReadSamples(high, new[] { 0 })).LimitName);
            }
        }

        private static void UnsupportedNeighbor(byte[] bytes)
        {
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.False(group.Channels[1].CanReadRaw);
                Assert.NotNull(group.Channels[1].UnsupportedReason);
                int before = source.ReadCalls;
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 1 }));
                Assert.Equal(before, source.ReadCalls);
                Assert.Equal(5, reader.ReadSamples(group, new[] { 0 }).Sum(batch => batch.SampleCount));
            }
        }
    }
}
