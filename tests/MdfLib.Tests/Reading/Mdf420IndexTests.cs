// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Reading
{
    public sealed class Mdf420IndexTests
    {
        [Fact]
        public void RawPairedRangesReadExactlySelectedValueAndInvalidationRecordsAndReuseOnlyHeaders()
        {
            byte[] bytes = M5CWire.Read("m5c-ld-single420");
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                long before = BytesRead(source);
                MdfSampleBatch batch = reader.ReadSamples(group, new[] { 1 }, 1, 3, 1024).Single();
                Assert.Equal(9007199254740993UL, batch.GetValue(0, 0).UnsignedValue);
                Assert.Equal(1UL << 63, batch.GetValue(1, 0).UnsignedValue);
                Assert.Equal(ulong.MaxValue, batch.GetValue(2, 0).UnsignedValue);
                Assert.Equal(192L, BytesRead(source) - before); // Header-only map120 + selected3x(22+2), no pruning promise.
                before = BytesRead(source);
                int first = source.ReadRanges.Count;
                Assert.Single(reader.ReadSamples(group, new[] { 1 }, 1, 3));
                Assert.Equal(72L, BytesRead(source) - before);
                int dv = M5CWire.Leaf(bytes) + 24, di = M5CWire.Leaf(bytes, invalidation: true) + 24;
                Assert.Equal(new[] { dv + 22L, di + 2L, dv + 44L, di + 4L, dv + 66L, di + 6L }, source.ReadRanges.Skip(first).Select(r => r.Item1));
                Assert.Equal(new[] { 22, 2, 22, 2, 22, 2 }, source.ReadRanges.Skip(first).Select(r => r.Item2));
                Assert.NotNull(group.Layout.ListDataMap);
                Assert.Null(group.Layout.DataMap);
            }
        }

        [Theory]
        [InlineData("m5c-ld-zsingle420")]
        [InlineData("m5c-ld-tsingle420")]
        public void BothDecodedCachesSurviveAlternatingStreamsOnlyInsideOneEnumerationAndBatchesAreSnapshots(string id)
        {
            using (TestReadStream source = new TestReadStream(M5CWire.Read(id)))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                long before = BytesRead(source);
                MdfSampleBatch[] batches = reader.ReadSamples(group, new[] { 0, 1, 2, 3 }, batchSize: 1).ToArray();
                Assert.Equal(7, batches.Length);
                AssertOriginalRecords(batches, false);
                Assert.Equal(358L, BytesRead(source) - before); // Map168 + one compressed DV165 and DI25, not14inflations.
                before = BytesRead(source);
                for (int repeat = 0; repeat < 100; repeat++)
                { Assert.Equal(7, reader.ReadSamples(group, new[] { 1 }, batchSize: 1).Sum(b => b.SampleCount)); }
                Assert.Equal(19000L, BytesRead(source) - before); // Immutable header maps only on reader, new two-stream caches each time.
                AssertOriginalRecords(batches, false);
                reader.Dispose();
                AssertOriginalRecords(batches, false);
            }
        }

        [Fact]
        public void NullDiClearsTheReusedNormalizedBatchAreaInsteadOfReusingPreviousInvalidBits()
        {
            byte[] bytes = M5CWire.Read("m5c-ld-single420");
            byte[] values = M5CWire.Values(), invalidations = M5CWire.Invalidations();
            int first = M5Wire.Add(ref bytes, "DV", new ulong[0], values.Take(66).ToArray());
            int second = M5Wire.Add(ref bytes, "DV", new ulong[0], values.Skip(66).ToArray());
            int di = M5Wire.Add(ref bytes, "DI", new ulong[0], invalidations.Take(6).ToArray());
            int list = M5CWire.List(ref bytes, new[] { (ulong)first, (ulong)second }, new[] { (ulong)di, 0UL }, new[] { 0UL, 3UL });
            M5CWire.SetRoot(bytes, list);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                MdfSampleBatch[] batches = reader.ReadSamples(group, new[] { 2, 3 }, batchSize: 1).ToArray();
                Assert.False(batches[1].GetValue(0, 0).IsValid);
                Assert.False(batches[2].GetValue(0, 1).IsValid);
                foreach (MdfSampleBatch batch in batches.Skip(3))
                { Assert.True(batch.GetValue(0, 0).IsValid); Assert.True(batch.GetValue(0, 1).IsValid); }
                Assert.Null(group.Layout.ListDataMap.Invalidations.Segments[1].Block);
                Assert.Equal(8UL, group.Layout.ListDataMap.Invalidations.Segments[1].Length);
            }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void EachRoleHasIndependentCompressedAndOriginalCapsBeforeThatPayloadIsRead(bool invalidation, bool compressed)
        {
            byte[] bytes = M5CWire.Read("m5c-ld-single420");
            int rows = invalidation ? 200 : 7;
            uint dataBytes = invalidation ? 1U : 22U;
            if (invalidation)
            {
                int cg = M5CWire.Cg(bytes), cn = (int)M3Wire.U64(bytes, M3Wire.Link(cg, 1));
                M3Wire.Put(bytes, M3Wire.Link(cn, 0), 0);
                int field = M3Wire.Payload(bytes, cn);
                bytes[field] = 0; bytes[field + 2] = 0;
                M3Wire.Put(bytes, field + 4, 0, 4); M3Wire.Put(bytes, field + 8, 8, 4);
                M3Wire.Put(bytes, M3Wire.Payload(bytes, cg) + 24, 1, 4);
            }
            M3Wire.Put(bytes, M3Wire.Payload(bytes, M5CWire.Cg(bytes)) + 8, (ulong)rows);
            byte[] value = invalidation ? new byte[rows] : M5CWire.Values();
            byte[] mask = new byte[rows * 2];
            int bad = M5Wire.Add(ref bytes, "DZ", new ulong[0], M5CWire.Zipped(compressed ? (invalidation ? mask : value) : new byte[0],
                invalidation ? "DI" : "DV", (ulong)(invalidation ? mask.Length : value.Length)));
            int good = M5Wire.Add(ref bytes, invalidation ? "DV" : "DI", new ulong[0], invalidation ? value : mask);
            int list = M5CWire.List(ref bytes, new[] { (ulong)(invalidation ? good : bad) }, new[] { (ulong)(invalidation ? bad : good) }, null, (ulong)rows);
            M5CWire.SetRoot(bytes, list);
            int cap = invalidation ? compressed ? 400 : 399 : compressed ? 154 : 153;
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true, new MdfReaderOptions { MaximumReadBufferBytes = cap }))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal((ulong)dataBytes + 2, group.RecordByteCount);
                MdfResourceLimitException error = Assert.Throws<MdfResourceLimitException>(() => reader.ReadSamples(group, new[] { 0 }, sampleCount: 1).ToList());
                Assert.Equal("MaximumReadBufferBytes", error.LimitName);
                Assert.Equal((ulong)(invalidation ? compressed ? 411 : 400 : compressed ? 165 : 154), error.Requested);
                Assert.Equal((ulong)bad, error.Offset);
                Assert.NotNull(group.Layout.ListDataMap);
                Assert.DoesNotContain(source.ReadRanges, r => r.Item1 >= bad + 48 && r.Item1 < bad + (long)M3Wire.U64(bytes, bad + 8));
            }
        }

        [Fact]
        public void HugeUnselectedValueAndInvalidationDzAreHeaderOnlyButNeededOversizeIsRefused()
        {
            byte[] bytes = M5CWire.Read("m5c-ld-single420");
            const ulong skipped = 1UL << 40;
            int hugeValue = M5Wire.Add(ref bytes, "DZ", new ulong[0], M5CWire.Zipped(new byte[0], "DV", skipped * 22));
            int hugeDi = M5Wire.Add(ref bytes, "DZ", new ulong[0], M5CWire.Zipped(new byte[0], "DI", skipped * 2));
            int goodValue = M5Wire.Add(ref bytes, "DZ", new ulong[0], M5CWire.Zipped(M5CWire.Values(), "DV"));
            int goodDi = M5Wire.Add(ref bytes, "DZ", new ulong[0], M5CWire.Zipped(M5CWire.Invalidations(), "DI"));
            int list = M5CWire.List(ref bytes, new[] { (ulong)hugeValue, (ulong)goodValue }, new[] { (ulong)hugeDi, (ulong)goodDi }, new[] { 0UL, skipped });
            M5CWire.SetRoot(bytes, list);
            M3Wire.Put(bytes, M3Wire.Payload(bytes, M5CWire.Cg(bytes)) + 8, skipped + 7);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(9007199254740993UL, reader.ReadSamples(group, new[] { 1 }, skipped + 1, 1).Single().GetValue(0, 0).UnsignedValue);
                int before = source.ReadCalls;
                MdfResourceLimitException error = Assert.Throws<MdfResourceLimitException>(() => reader.ReadSamples(group, new[] { 1 }, sampleCount: 1).ToList());
                Assert.Equal(skipped * 22, error.Requested);
                Assert.Equal(before, source.ReadCalls);
                foreach (int bad in new[] { hugeValue, hugeDi })
                { Assert.DoesNotContain(source.ReadRanges, r => r.Item1 >= bad + 48 && r.Item1 < bad + 59); }
                Assert.Equal(7UL, reader.ReadSamples(group, new[] { 1 }, skipped + 6, 1).Single().GetValue(0, 0).UnsignedValue);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void MultiTerabyteSeparateStreamsAndSparseNullDiReadOnlyThreeHighOrdinalRows(bool nullDi)
        {
            byte[] metadata = M5CWire.Read("m5c-ld-single420");
            const ulong rows = (1UL << 38) + 11;
            const long value = 1L << 42;
            ulong valueLength = rows * 22, invalidationLength = rows * 2;
            long di = (value + 24 + (long)valueLength + 7) & ~7L;
            int list = M5CWire.List(ref metadata, new[] { (ulong)value }, new[] { nullDi ? 0UL : (ulong)di }, null, rows);
            M5CWire.SetRoot(metadata, list);
            M3Wire.Put(metadata, M3Wire.Payload(metadata, M5CWire.Cg(metadata)) + 8, rows);
            using (SparseReadStream source = new SparseReadStream(di + 24 + (long)invalidationLength))
            {
                source.Add(0, metadata);
                source.Add(value, M5Wire.Header("DV", valueLength));
                source.Add(value + 24 + (long)(rows - 3) * 22, M5CWire.Values().Skip(4 * 22).ToArray());
                if (!nullDi)
                {
                    source.Add(di, M5Wire.Header("DI", invalidationLength));
                    source.Add(di + 24 + (long)(rows - 3) * 2, M5CWire.Invalidations().Skip(8).ToArray());
                }
                using (MdfReader reader = MdfReader.Open(source, true, new MdfReaderOptions { MaximumReadBufferBytes = 128 }))
                {
                    MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                    long before = source.BytesRead;
                    var pending = reader.ReadSamples(group, new[] { 1, 2, 3 }, rows - 3, 3, int.MaxValue);
                    Assert.Equal(before, source.BytesRead);
                    MdfSampleBatch batch = pending.Single();
                    Assert.Equal(rows - 3, batch.FirstSampleIndex);
                    Assert.Equal(17UL, batch.GetValue(0, 0).UnsignedValue);
                    Assert.Equal(42UL, batch.GetValue(1, 0).UnsignedValue);
                    Assert.Equal(7UL, batch.GetValue(2, 0).UnsignedValue);
                    Assert.Equal(nullDi, batch.GetValue(1, 2).IsValid);
                    Assert.Equal(nullDi, batch.GetValue(2, 1).IsValid);
                    Assert.Equal(nullDi ? 162L : 192L, source.BytesRead - before); // Map96/120 + DV66 and optional DI6 only.
                    before = source.BytesRead;
                    Assert.Single(pending);
                    Assert.Equal(nullDi ? 66L : 72L, source.BytesRead - before);
                    Assert.Single(group.Layout.ListDataMap.Values.Segments);
                    Assert.Single(group.Layout.ListDataMap.Invalidations.Segments);
                    Assert.InRange(source.LargestReadRequest, 1, 128);
                }
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void FiveThousandLdSiblingsUseTwoShallowBinarySearchMapsAndLegalSharedLeaves(bool compressed)
        {
            byte[] bytes = M5CWire.Read(compressed ? "m5c-ld-tsingle420" : "m5c-ld-single420");
            int value = M5CWire.Leaf(bytes), di = M5CWire.Leaf(bytes, invalidation: true);
            for (int cn = (int)M3Wire.U64(bytes, M3Wire.Link(M5CWire.Cg(bytes), 1)); cn != 0; cn = (int)M3Wire.U64(bytes, M3Wire.Link(cn, 0)))
            { M3Wire.Put(bytes, M3Wire.Link(cn, 2), 0); M3Wire.Put(bytes, M3Wire.Link(cn, 4), 0); }
            const int count = 5000, size = 64;
            int first = (bytes.Length + 7) & ~7;
            Array.Resize(ref bytes, first + count * size);
            for (int index = 0; index < count; index++)
            {
                int address = first + index * size;
                Array.Copy(M5Wire.Header("LD", 40), 0, bytes, address, 24);
                M3Wire.Put(bytes, address + 16, 3);
                M3Wire.Put(bytes, address + 24, index + 1 == count ? 0UL : (ulong)(address + size));
                M3Wire.Put(bytes, address + 32, (ulong)value); M3Wire.Put(bytes, address + 40, (ulong)di);
                M3Wire.Put(bytes, address + 48, 0x80000000, 4); M3Wire.Put(bytes, address + 52, 1, 4);
                M3Wire.Put(bytes, address + 56, (ulong)index * 7);
            }
            M5CWire.SetRoot(bytes, first);
            M3Wire.Put(bytes, M3Wire.Payload(bytes, M5CWire.Cg(bytes)) + 8, count * 7);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true, new MdfReaderOptions { MaximumTraversalDepth = 3 }))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                var pending = reader.ReadSamples(group, new[] { 1 }, (ulong)(count * 7 - 8), 3, 1);
                var batches = pending.ToArray();
                Assert.Equal(new[] { 7UL, 0UL, 9007199254740993UL }, batches.Select(b => b.GetValue(0, 0).UnsignedValue));
                Assert.Equal(count, group.Layout.ListDataMap.Values.Segments.Length);
                Assert.Equal(count, group.Layout.ListDataMap.Invalidations.Segments.Length);
                Assert.Equal(2, group.Layout.ListDataMap.Values.MaximumDepth);
                if (compressed) { Assert.Equal(1, source.ReadRanges.Count(r => r.Item1 == value + 48)); Assert.Equal(1, source.ReadRanges.Count(r => r.Item1 == di + 48)); }
                long before = BytesRead(source);
                Assert.Equal(3, pending.Sum(b => b.SampleCount));
                Assert.Equal(compressed ? 190L : 72L, BytesRead(source) - before);
            }
        }

        [Theory]
        [InlineData(1U)]
        [InlineData(2U)]
        [InlineData(3U)]
        [InlineData(13U)]
        [InlineData(17U)]
        [InlineData(153U)]
        [InlineData(154U)]
        [InlineData(155U)]
        [InlineData(uint.MaxValue)]
        public void EachStreamTransposeHasItsOwnGeometryAndUnchangedTail(uint parameter)
        {
            byte[] bytes = M5CWire.Read("m5c-ld-single420");
            int value = M5Wire.Add(ref bytes, "DZ", new ulong[0], M5CWire.Zipped(M5CWire.Values(), "DV", parameter: parameter));
            int di = M5Wire.Add(ref bytes, "DZ", new ulong[0], M5CWire.Zipped(M5CWire.Invalidations(), "DI", parameter: parameter));
            int list = M5CWire.List(ref bytes, new[] { (ulong)value }, new[] { (ulong)di }, null, 7);
            M5CWire.SetRoot(bytes, list);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                AssertOriginalRecords(reader.ReadSamples(group, new[] { 0, 1, 2, 3 }, batchSize: 1), false);
                AssertOriginalRecords(reader.ReadSamples(group, new[] { 0, 1, 2, 3 }, batchSize: 2, representation: MdfValueRepresentation.Physical), true);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Inline420DirectDzAndDlReuseTheSameM5aMapAndCommonDecoder(bool directDz)
        {
            byte[] bytes = M5CWire.Read(directDz ? "m5c-dt420" : "m5c-hl420");
            int root;
            if (directDz)
            {
                byte[] raw = bytes.Skip(M5CWire.Root(bytes) + 24).Take(168).ToArray();
                root = M5Wire.Add(ref bytes, "DZ", new ulong[0], M5CWire.Zipped(raw, "DT", parameter: 17));
            }
            else { root = (int)M3Wire.U64(bytes, M3Wire.Link(M5CWire.Root(bytes), 0)); }
            M5CWire.SetRoot(bytes, root);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                AssertOriginalRecords(reader.ReadSamples(group, new[] { 0, 1, 2, 3 }, batchSize: 1), false);
                Assert.NotNull(group.Layout.DataMap);
                Assert.Null(group.Layout.ListDataMap);
            }
        }

        internal static long BytesRead(TestReadStream source) { return source.ReadRanges.Sum(r => (long)r.Item2); }
        internal static void AssertOriginalRecords(IEnumerable<MdfSampleBatch> batches, bool physical)
        {
            // Literal independent pre-C# arrays; deliberately unsorted master ordinals and raw invalid values.
            double[] time = { .5, 0, .75, .25, 1, .625, .125 };
            ulong[] exact = { 0, 9007199254740993UL, 1UL << 63, ulong.MaxValue, 17, 42, 7 };
            long[] packed = { -4096, -1, 0, 4095, 17, -37, 2048 };
            long[] linear = { int.MinValue, -1, 0, 10, int.MaxValue, 100, -50 };
            int[] masks = { 0, 1, 512, 513, 0, 512, 1 };
            int index = 0;
            foreach (MdfSampleBatch batch in batches)
            {
                Assert.Equal((ulong)index, batch.FirstSampleIndex);
                for (int row = 0; row < batch.SampleCount; row++, index++)
                {
                    Assert.Equal(time[index], batch.GetValue(row, 0).FloatingPointValue);
                    Assert.Equal(exact[index], batch.GetValue(row, 1).UnsignedValue);
                    Assert.Equal(packed[index], batch.GetValue(row, 2).SignedValue);
                    Assert.Equal((masks[index] & 1) == 0, batch.GetValue(row, 2).IsValid);
                    if (physical) { Assert.Equal(.25 * linear[index] - 2, batch.GetValue(row, 3).FloatingPointValue); }
                    else { Assert.Equal(linear[index], batch.GetValue(row, 3).SignedValue); }
                    Assert.Equal((masks[index] & 512) == 0, batch.GetValue(row, 3).IsValid);
                }
            }
            Assert.Equal(7, index);
        }
    }
}
