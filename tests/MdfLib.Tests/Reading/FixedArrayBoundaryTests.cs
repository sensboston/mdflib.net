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
    public sealed class FixedArrayBoundaryTests
    {
        [Theory]
        [InlineData(1UL, "MDF007")]
        [InlineData(65UL, "MDF008")]
        [InlineData(ulong.MaxValue, "MDF007")]
        public void CaReferencesStillUseResolverAddressBoundsAndAlignment(ulong address, string code)
        {
            byte[] bytes = M5DWire.Read();
            M3Wire.Put(bytes, M3Wire.Link(M5DWire.Cn(bytes), 1), address);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            { Assert.Equal(code, Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode); }
        }

        [Fact]
        public void NonCaCompositionRemainsUnsupportedAndDoesNotInventAnArrayDescriptor()
        {
            byte[] bytes = M5DWire.Read();
            int cn = M5DWire.Cn(bytes);
            M3Wire.Put(bytes, M3Wire.Link(cn, 1), (ulong)cn);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Null(group.Channels[2].Array);
                Assert.False(group.Channels[2].CanReadRaw);
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 2 }));
            }
        }

        [Fact]
        public void AnOrphanCaIsNotAutomaticallyAppliedToAScalarWhoseCompositionLinkIsNull()
        {
            byte[] bytes = M5DWire.Read();
            M3Wire.Put(bytes, M3Wire.Link(M5DWire.Cn(bytes), 1), 0);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Null(group.Channels[2].Array);
                Assert.Equal(0UL, reader.ReadSamples(group, new[] { 2 }, sampleCount: 1).Single().GetValue(0, 0).UnsignedValue);
            }
        }

        [Fact]
        public void ChannelSpecificDataCannotBeIgnoredEvenWhenTheCaShapeIsSupported()
        {
            byte[] bytes = M5DWire.Read();
            M3Wire.Put(bytes, M3Wire.Link(M5DWire.Cn(bytes), 5), (ulong)M5DWire.Root(bytes));
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.NotNull(group.Channels[2].Array);
                Assert.False(group.Channels[2].CanReadRaw);
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 2 }));
            }
        }

        [Theory]
        [InlineData(1U)]
        [InlineData(3U)]
        public void AllInvalidArrayFlagPreservesEveryUnderlyingElementWithoutTouchingUnusedInvalidationPosition(uint flags)
        {
            byte[] bytes = M5DWire.Read();
            int fields = M3Wire.Payload(bytes, M5DWire.Cn(bytes));
            M3Wire.Put(bytes, fields + 12, flags, 4);
            M3Wire.Put(bytes, fields + 16, uint.MaxValue, 4);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                MdfSampleBatch[] batches = reader.ReadSamples(group, new[] { 2 }, batchSize: 1).ToArray();
                Assert.Equal(5, batches.Length);
                foreach (MdfSampleBatch batch in batches)
                for (ulong e = 0; e < 6; e++) { Assert.False(batch.GetValue(0, 0, e).IsValid); }
                Assert.Equal(255UL, batches[0].GetValue(0, 0, 2).UnsignedValue);
                Assert.Equal(128UL, batches[1].GetValue(0, 0, 0).UnsignedValue);
            }
        }

        [Theory]
        [InlineData(24U)]
        [InlineData(uint.MaxValue)]
        public void UsedArrayInvalidationPositionMustFitTheSeparateInvalidationArea(uint bit)
        {
            byte[] bytes = M5DWire.Read();
            M3Wire.Put(bytes, M3Wire.Payload(bytes, M5DWire.Cn(bytes)) + 16, bit, 4);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            { Assert.Equal("MDF013", Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode); }
        }

        [Theory]
        [InlineData(919, false)]
        [InlineData(920, true)]
        public void TwoArraysMustFitTheirCombinedOutputNotMerelyEachOneSeparately(int cap, bool fits)
        {
            using (TestReadStream source = new TestReadStream(M5DWire.Read()))
            using (MdfReader reader = MdfReader.Open(source, true, new MdfReaderOptions { MaximumSampleBatchBytes = cap }))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(5, reader.ReadSamples(group, new[] { 8 }).Sum(b => b.SampleCount));
                Assert.Equal(5, reader.ReadSamples(group, new[] { 9 }).Sum(b => b.SampleCount));
                int before = source.ReadCalls;
                if (!fits)
                {
                    Assert.Equal("MaximumSampleBatchBytes", Assert.Throws<MdfResourceLimitException>(() => reader.ReadSamples(group, new[] { 8, 9 })).LimitName);
                    Assert.Equal(before, source.ReadCalls);
                }
                else
                {
                    MdfSampleBatch[] batches = reader.ReadSamples(group, new[] { 8, 9 }, batchSize: int.MaxValue).ToArray();
                    Assert.Equal(5, batches.Length);
                    Assert.All(batches, b => Assert.Equal(1, b.SampleCount));
                    Assert.Equal(9007199254740993UL, batches[0].GetValue(0, 0, 6).UnsignedValue);
                    Assert.Equal(9007199254740993UL, batches[0].GetValue(0, 1, 6).UnsignedValue);
                }
            }
        }

        [Theory]
        [InlineData("m5d-arrays410", 0)]
        [InlineData("m5d-arrays410", 1)]
        [InlineData("m5d-arrays410", 2)]
        [InlineData("m5d-arrays410", 3)]
        [InlineData("m5d-lists411", 0)]
        [InlineData("m5d-lists411", 1)]
        [InlineData("m5d-lists411", 2)]
        [InlineData("m5d-lists411", 3)]
        [InlineData("m5d-arrays420", 0)]
        [InlineData("m5d-arrays420", 1)]
        [InlineData("m5d-arrays420", 2)]
        [InlineData("m5d-arrays420", 3)]
        public void MetadataCaReadHonorsCancellationDisposalIoCauseAndCaughtReentryWithoutPartialCatalog(string id, int action)
        {
            byte[] bytes = M5DWire.Read(id);
            int prefixEnd = M3Wire.Payload(bytes, M5DWire.Ca(bytes)) + 16;
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                IOException cause = new IOException("CA prefix callback I/O failure");
                bool invoked = false;
                source.AfterRead = () =>
                {
                    if (source.Position != prefixEnd) { return; }
                    source.AfterRead = null;
                    invoked = true;
                    if (action == 0) { cancellation.Cancel(); }
                    else if (action == 1) { reader.Dispose(); }
                    else if (action == 2) { throw cause; }
                    else
                    {
                        Assert.Throws<InvalidOperationException>(() => reader.ReadMetadata());
                        Assert.Throws<InvalidOperationException>(() => reader.ReadSamples(null, new[] { 2 }));
                    }
                };
                if (action == 0)
                {
                    OperationCanceledException error = Assert.ThrowsAny<OperationCanceledException>(() => reader.ReadMetadata(cancellation.Token));
                    Assert.Equal(cancellation.Token, error.CancellationToken);
                }
                else if (action == 1) { Assert.Throws<ObjectDisposedException>(() => reader.ReadMetadata()); }
                else if (action == 2) { Assert.Same(cause, Assert.Throws<IOException>(() => reader.ReadMetadata())); }
                else { Assert.Equal(24, reader.ReadMetadata()[0].ChannelGroups[0].Channels.Count); }
                Assert.True(invoked);
                Assert.Equal(0, source.DisposeCalls);
                if (action != 1)
                {
                    MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                    Assert.Equal(24, group.Channels.Count);
                    Assert.Equal(5, reader.ReadSamples(group, new[] { 2 }).Sum(b => b.SampleCount));
                }
            }
        }

        [Fact]
        public void ArrayMetadataCaCanLiveBeyondFourTerabytesWithoutNarrowingItsLink()
        {
            byte[] metadata = M5DWire.Read();
            int old = M5DWire.Ca(metadata);
            byte[] ca = metadata.Skip(old).Take((int)M3Wire.U64(metadata, old + 8)).ToArray();
            const long high = 1L << 42;
            M3Wire.Put(metadata, M3Wire.Link(M5DWire.Cn(metadata), 1), (ulong)high);
            using (SparseReadStream source = new SparseReadStream(high + ca.Length))
            {
                source.Add(0, metadata);
                source.Add(high, ca);
                using (MdfReader reader = MdfReader.Open(source, true))
                {
                    MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                    Assert.Equal(new ulong[] { 2, 3 }, group.Channels[2].Array.Dimensions);
                    Assert.Equal(255UL, reader.ReadSamples(group, new[] { 2 }, sampleCount: 1).Single().GetValue(0, 0, 2).UnsignedValue);
                    Assert.InRange(source.BytesRead, 1L, 20000L);
                }
            }
        }

        [Fact]
        public void ArrayRangeDoesNotForceDecodingUnneededDzButNeededCorruptDzCannotYieldPartialValues()
        {
            byte[] bytes = M5DWire.Read("m5d-lists411");
            int dz = (int)M3Wire.U64(bytes, M3Wire.Link(M5DWire.Root(bytes), 3));
            Assert.Equal((byte)'Z', bytes[dz + 3]);
            bytes[dz + (int)M3Wire.U64(bytes, dz + 8) - 1] ^= 1;
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Empty(reader.ReadSamples(group, new[] { 8 }, sampleCount: 0));
                Assert.Single(reader.ReadSamples(group, new[] { 8 }, 3, 1));
                using (IEnumerator<MdfSampleBatch> e = reader.ReadSamples(group, new[] { 8 }, sampleCount: 1).GetEnumerator())
                {
                    MdfFormatException error = Assert.Throws<MdfFormatException>(() => e.MoveNext());
                    Assert.Equal("MDF015", error.DiagnosticCode);
                    Assert.IsType<InvalidDataException>(error.InnerException);
                }
                Assert.Single(reader.ReadSamples(group, new[] { 1 }, 3, 1));
            }
        }

        [Fact]
        public void ASelectedArrayReadsOnlyRequestedRawRecordRowsAndNeverCachesItsValuesOnTheReader()
        {
            byte[] bytes = M5DWire.Read();
            int body = M5DWire.Root(bytes) + 24;
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                int before = source.ReadRanges.Count;
                Assert.Single(reader.ReadSamples(group, new[] { 8 }, 2, 1));
                Assert.Equal(24 + 580, source.ReadRanges.Skip(before).Sum(r => r.Item2));
                before = source.ReadRanges.Count;
                Assert.Single(reader.ReadSamples(group, new[] { 8 }, 2, 1));
                Tuple<long, int> range = Assert.Single(source.ReadRanges.Skip(before));
                Assert.Equal((long)body + 2 * 580, range.Item1);
                Assert.Equal(580, range.Item2);
            }
        }
    }
}
