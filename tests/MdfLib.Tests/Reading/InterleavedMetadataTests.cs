// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MdfLib.Internal;
using MdfLib.Reading;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Reading
{
    public sealed class InterleavedMetadataTests
    {
        [Theory]
        [InlineData("m5b-prefix-le330", 1, 0)]
        [InlineData("m5b-suffix-be330", 1, 1)]
        [InlineData("m5b-id1-dt410", 1, 0)]
        [InlineData("m5b-id2-dz411", 2, 0)]
        [InlineData("m5b-id4-dl410", 4, 0)]
        [InlineData("m5b-id8-hl411", 8, 0)]
        [InlineData("m5b-wide-id8-hl411", 8, 0)]
        [InlineData("m5b-asammdf-derived330", 1, 1)]
        [InlineData("m5b-asammdf-derived411", 8, 0)]
        public void ExplicitCatalogHasSharedFramingButReadsNoStorageAndZeroRangesDoNotScan(string id, int prefix, int suffix)
        {
            byte[] bytes = M5BWire.Read(id);
            int first = M5BWire.Root(bytes);
            if (!M5BWire.Is3(bytes) && bytes[first + 2] == (byte)'H') { first = (int)M5BWire.UInt(bytes, first + 24); }
            if (!M5BWire.Is3(bytes) && bytes[first + 2] == (byte)'D' && bytes[first + 3] == (byte)'L')
            { first = (int)M5BWire.UInt(bytes, first + 32); }
            // Derived files append new storage after all metadata and inert orphan sorted records.
            using (TestReadStream source = new TestReadStream(bytes) { MaximumBytesPerRead = 3 })
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                Assert.All(source.ReadRanges, range => Assert.True(range.Item1 + range.Item2 <= (M5BWire.Is3(bytes) ? 272 : 168)));
                IReadOnlyList<MdfChannelGroup> groups = Assert.Single(reader.ReadMetadata()).ChannelGroups;
                Assert.Equal(id.Contains("derived") ? 2 : 3, groups.Count);
                InterleavedLayout layout = groups[0].Layout.Interleaved;
                Assert.NotNull(layout);
                Assert.Equal(prefix, layout.PrefixBytes);
                Assert.Equal(suffix, layout.SuffixBytes);
                Assert.Equal((ulong)M5BWire.Dg(bytes), layout.BlockAddress);
                ulong sum = 0;
                foreach (MdfChannelGroup group in groups)
                {
                    Assert.Same(layout, group.Layout.Interleaved);
                    Assert.True(group.CanReadSamples, group.UnsupportedReason);
                    sum += group.SampleCount * (group.RecordByteCount + (ulong)(prefix + suffix));
                    Assert.True(group.Channels[0].IsMaster);
                    Assert.Null(group.Layout.DataMap);
                }
                Assert.Equal(sum, layout.TotalBytes);
                Assert.Null(layout.DataMap);
                Assert.All(source.ReadRanges, range => Assert.True(range.Item1 + range.Item2 <= first));
                int before = source.ReadCalls;
                source.ReadFailure = new IOException("Zero ranges must not access IDs or storage.");
                IEnumerable<MdfSampleBatch> pending = reader.ReadSamples(groups[0], new[] { 1 }, 1, 1);
                Assert.Empty(reader.ReadSamples(groups[0], new[] { 1 }, sampleCount: 0));
                Assert.Empty(reader.ReadSamples(groups[1], new[] { 0 }, groups[1].SampleCount));
                Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadSamples(groups[0], new[] { 1 }, 5, 1));
                Assert.Throws<ArgumentException>(() => reader.ReadSamples(groups[0], new[] { 1, 1 }));
                Assert.Equal(before, source.ReadCalls);
                source.ReadFailure = null;
                Assert.Equal(9007199254740993UL, pending.Single().GetValue(0, 0).UnsignedValue);
                Mdf4DataMap map = layout.DataMap;
                if (!M5BWire.Is3(bytes)) { Assert.NotNull(map); }
                Assert.Single(reader.ReadSamples(groups[1], new[] { 1 }, sampleCount: 1));
                Assert.Same(map, groups[1].Layout.Interleaved.DataMap);
                Assert.Equal(0, source.WriteCalls);
                Assert.Equal(0, source.FlushCalls);
            }
        }

        [Theory]
        [InlineData("m5b-prefix-le330")]
        [InlineData("m5b-suffix-be330")]
        [InlineData("m5b-id1-dt410")]
        [InlineData("m5b-id2-dz411")]
        [InlineData("m5b-id4-dl410")]
        [InlineData("m5b-wide-id8-hl411")]
        public void DuplicateScopedIdsAreMetadataErrorsAndPartialCatalogIsNeverPublished(string id)
        {
            byte[] bytes = M5BWire.Read(id);
            M5BWire.Id(bytes, 1, 0);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                for (int retry = 0; retry < 2; retry++)
                {
                    MdfFormatException error = Assert.Throws<MdfFormatException>(() => reader.ReadMetadata());
                    Assert.Equal("MDF013", error.DiagnosticCode);
                    Assert.Equal((ulong)M5BWire.Cg(bytes, 1), error.Offset);
                }
                Assert.Equal(0, source.DisposeCalls);
            }
        }

        [Theory]
        [InlineData("m5b-prefix-le330", 256UL)]
        [InlineData("m5b-suffix-be330", 65535UL)]
        [InlineData("m5b-id1-dt410", 256UL)]
        [InlineData("m5b-id2-dz411", 65536UL)]
        [InlineData("m5b-id4-dl410", 4294967296UL)]
        public void MetadataIdMustFitActualPrefixWidthWithoutNarrowing(string id, ulong value)
        {
            byte[] bytes = M5BWire.Read(id);
            M5BWire.Id(bytes, 0, value);
            MetadataError(bytes, "MDF013");
        }

        [Theory]
        [InlineData("m5b-prefix-le330", 0)]
        [InlineData("m5b-prefix-le330", 3)]
        [InlineData("m5b-prefix-le330", 65535)]
        [InlineData("m5b-id1-dt410", 0)]
        [InlineData("m5b-id1-dt410", 3)]
        [InlineData("m5b-id1-dt410", 5)]
        [InlineData("m5b-id1-dt410", 255)]
        public void UnknownWidthsOrMultipleGroupsWithoutIdsRemainExplicitlyUnsupported(string id, int width)
        {
            byte[] bytes = M5BWire.Read(id);
            M5BWire.Width(bytes, width);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                IReadOnlyList<MdfChannelGroup> groups = reader.ReadMetadata()[0].ChannelGroups;
                int before = source.ReadCalls;
                foreach (MdfChannelGroup group in groups)
                {
                    Assert.False(group.CanReadSamples);
                    Assert.Null(group.Layout.Interleaved);
                    Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 0 }));
                }
                Assert.Equal(before, source.ReadCalls);
            }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(16)] // bus-event bits 1/2 are layout-neutral; event signal (bit 4) is not supported.
        [InlineData(65535)]
        public void UnknownVariableOrExtendedNeighborFramingDisablesTheWholeDg(int flags)
        {
            byte[] bytes = M5BWire.Read();
            M5BWire.Put(bytes, M5BWire.Cg(bytes, 2) + 88, (ulong)flags, 2);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                IReadOnlyList<MdfChannelGroup> groups = reader.ReadMetadata()[0].ChannelGroups;
                int before = source.ReadCalls;
                Assert.All(groups, group =>
                {
                    Assert.False(group.CanReadSamples);
                    Assert.Contains("framing", group.UnsupportedReason);
                    Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 0 }));
                });
                Assert.Equal(before, source.ReadCalls);
            }
        }

        [Theory]
        [InlineData("m5b-prefix-le330")]
        [InlineData("m5b-id1-dt410")]
        public void KnownFixedNeighborWithUnsupportedScalarStillCanBeSkipped(string id)
        {
            byte[] bytes = M5BWire.Read(id);
            int cn = M5BWire.Cn(bytes, 2);
            if (M5BWire.Is3(bytes)) { M5BWire.Put(bytes, cn + 190, 7, 2); }
            else { bytes[cn + 88 + 2] = 7; }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                IReadOnlyList<MdfChannelGroup> groups = reader.ReadMetadata()[0].ChannelGroups;
                Assert.All(groups, group => Assert.True(group.CanReadSamples));
                Assert.False(groups[2].Channels[0].CanReadRaw);
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(groups[2], new[] { 0 }));
                Assert.Equal(ulong.MaxValue, reader.ReadSamples(groups[0], new[] { 1 }, 3, 1).Single().GetValue(0, 0).UnsignedValue);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ProductAndCombinedExtentOverflowAreRejectedBeforeStorageIo(bool combined)
        {
            byte[] bytes = M5BWire.Read();
            M5BWire.Count(bytes, 0, combined ? ulong.MaxValue / 24 : ulong.MaxValue);
            if (combined) { M5BWire.Count(bytes, 1, ulong.MaxValue / 15); }
            MetadataError(bytes, "MDF013");
        }

        [Theory]
        [InlineData("missing", "MDF013")]
        [InlineData("truncated", "MDF007")]
        [InlineData("overlap", "MDF011")]
        [InlineData("cycles", "MDF007")]
        public void Mdf3ReservesTheCombinedWholeDgRangeAgainstKnownMetadataAndSource(string mutation, string code)
        {
            byte[] bytes = M5BWire.Read("m5b-prefix-le330");
            if (mutation == "missing") { M5BWire.Root(bytes, 0); }
            else if (mutation == "truncated") { Array.Resize(ref bytes, bytes.Length - 1); }
            else if (mutation == "overlap") { M5BWire.Root(bytes, (ulong)M5BWire.Cg(bytes)); }
            else { M5BWire.Count(bytes, 0, uint.MaxValue); }
            MetadataError(bytes, code);
        }

        [Theory]
        [InlineData("m5b-prefix-le330")]
        [InlineData("m5b-id1-dt410")]
        public void ZeroCyclesNeedNoStorageButFramingRemainsDocumentOwned(string id)
        {
            byte[] bytes = M5BWire.Read(id);
            for (int index = 0; index < 3; index++) { M5BWire.Count(bytes, index, 0); }
            M5BWire.Root(bytes, 0);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                IReadOnlyList<MdfChannelGroup> groups = reader.ReadMetadata()[0].ChannelGroups;
                int before = source.ReadCalls;
                foreach (MdfChannelGroup group in groups)
                {
                    Assert.True(group.CanReadSamples);
                    Assert.Equal(0UL, group.Layout.Interleaved.TotalBytes);
                    Assert.Empty(reader.ReadSamples(group, new[] { 0 }));
                }
                Assert.Equal(before, source.ReadCalls);
            }
        }

        [Fact]
        public void DescriptorDictionaryAllowanceIsReservedBeforeAnyGroupAttachment()
        {
            MdfChannelGroup[] groups = Enumerable.Range(0, 3).Select(index => new MdfChannelGroup
            {
                Index = index, RecordByteCount = 1, SampleCount = 1, Layout = new RecordLayout { RecordId = (ulong)index }
            }).ToArray();
            ReadBudget budget = new ReadBudget(new ReaderLimits(new MdfReaderOptions { MaximumMetadataBytes = 639 }));
            MdfResourceLimitException error = Assert.Throws<MdfResourceLimitException>(() =>
                InterleavedLayout.Attach(groups, 1, 0, budget, 168, "##DG", CancellationToken.None));
            Assert.Equal("MaximumMetadataBytes", error.LimitName);
            Assert.Equal(640UL, error.Requested);
            Assert.All(groups, group => Assert.Null(group.Layout.Interleaved));
            budget = new ReadBudget(new ReaderLimits(new MdfReaderOptions { MaximumMetadataBytes = 640 }));
            InterleavedLayout.Attach(groups, 1, 0, budget, 168, "##DG", CancellationToken.None);
            Assert.Equal(6UL, groups[0].Layout.Interleaved.TotalBytes);
        }

        [Theory]
        [InlineData("m5b-prefix-le330")]
        [InlineData("m5b-id1-dt410")]
        public void EmptyDgChannelChainIsInspectableWithoutInventingARecordLayout(string id)
        {
            byte[] bytes = M5BWire.Read(id);
            M5BWire.Put(bytes, M5BWire.Dg(bytes) + (M5BWire.Is3(bytes) ? 8 : 32), 0, M5BWire.Is3(bytes) ? 4 : 8);
            if (M5BWire.Is3(bytes)) { M5BWire.Put(bytes, M5BWire.Dg(bytes) + 20, 0, 2); }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                Assert.Empty(Assert.Single(reader.ReadMetadata()).ChannelGroups);
            }
        }

        [Theory]
        [InlineData("m5b-prefix-le330")]
        [InlineData("m5b-id1-dt410")]
        public void TheSameRecordIdInDifferentDataGroupsIsLegalAndDoesNotShareFraming(string id)
        {
            byte[] bytes = M5BWire.Read(id);
            AddSecondDg(ref bytes, null);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                IReadOnlyList<MdfDataGroup> catalog = reader.ReadMetadata();
                Assert.Equal(2, catalog.Count);
                MdfChannelGroup first = catalog[0].ChannelGroups[0], second = Assert.Single(catalog[1].ChannelGroups);
                Assert.Equal(0UL, first.Layout.RecordId);
                Assert.Equal(first.Layout.RecordId, second.Layout.RecordId);
                Assert.NotSame(first.Layout.Interleaved, second.Layout.Interleaved);
                Assert.Equal(2UL, second.Layout.Interleaved.TotalBytes);
                Assert.Equal(17UL, reader.ReadSamples(first, new[] { 1 }, 4, 1).Single().GetValue(0, 0).UnsignedValue);
            }
        }

        [Theory]
        [InlineData(false, "MDF014")]
        [InlineData(true, "MDF011")]
        public void DistinctMdf3DgsCannotOwnTheSameOrIntersectingHeaderlessExtent(bool shifted, string code)
        {
            byte[] bytes = M5BWire.Read("m5b-prefix-le330");
            ulong raw = (ulong)M5BWire.Root(bytes);
            AddSecondDg(ref bytes, raw + (shifted ? 1UL : 0));
            MetadataError(bytes, code);
        }

        [Fact]
        public void Mdf3StillReservesNonemptyNeighborsWhenTheFirstCgHasZeroCycles()
        {
            byte[] bytes = M5BWire.Read("m5b-prefix-le330");
            M5BWire.Count(bytes, 0, 0); M5BWire.Root(bytes, 0);
            MetadataError(bytes, "MDF013");
        }

        private static void AddSecondDg(ref byte[] bytes, ulong? sharedRaw)
        {
            int first = M5BWire.Dg(bytes);
            if (M5BWire.Is3(bytes))
            {
                int cg = bytes.Length, dg = cg + 26, raw = dg + 24;
                Array.Resize(ref bytes, raw + 2);
                bytes[cg] = (byte)'C'; bytes[cg + 1] = (byte)'G'; M5BWire.Put(bytes, cg + 2, 26, 2);
                M5BWire.Put(bytes, cg + 20, 1, 2); M5BWire.Put(bytes, cg + 22, 1, 4);
                bytes[dg] = (byte)'D'; bytes[dg + 1] = (byte)'G'; M5BWire.Put(bytes, dg + 2, 24, 2);
                M5BWire.Put(bytes, dg + 8, (ulong)cg, 4); M5BWire.Put(bytes, dg + 16, sharedRaw ?? (ulong)raw, 4);
                M5BWire.Put(bytes, dg + 20, 1, 2); M5BWire.Put(bytes, dg + 22, 1, 2);
                bytes[raw + 1] = 42;
                M5BWire.Put(bytes, first + 4, (ulong)dg, 4); M5BWire.Put(bytes, 80, 2, 2);
            }
            else
            {
                int data = M5Wire.Add(ref bytes, "DT", new ulong[0], new byte[] { 0, 42 });
                byte[] payload = new byte[32]; M5BWire.Put(payload, 8, 1); M5BWire.Put(payload, 24, 1, 4);
                int cg = M5Wire.Add(ref bytes, "CG", new ulong[6], payload);
                int dg = M5Wire.Add(ref bytes, "DG", new[] { 0UL, (ulong)cg, (ulong)data, 0UL }, new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 });
                M5BWire.Put(bytes, first + 24, (ulong)dg);
            }
        }

        private static void MetadataError(byte[] bytes, string code)
        {
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                Assert.Equal(code, Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
                Assert.Equal(0, source.DisposeCalls);
                Assert.Equal(0, source.WriteCalls);
            }
        }
    }
}
