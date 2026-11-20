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
    public sealed class Mdf4StorageIndexTests
    {
        [Fact]
        public void FiveThousandListSiblingsAndSharedPhysicalSegmentsRemainShallowAndIndexed()
        {
            byte[] bytes = M5Wire.Read();
            int dz = M5Wire.Root(bytes);
            // Keep this synthetic depth=3 case independent of optional CN text/CC children at depth=4.
            // The unchanged corpus still exercises all original names/conversions in compatibility tests.
            for (int channel = (int)M3Wire.U64(bytes, M3Wire.Link(M5Wire.Cg, 1)); channel != 0;
                channel = (int)M3Wire.U64(bytes, M3Wire.Link(channel, 0)))
            {
                M3Wire.Put(bytes, M3Wire.Link(channel, 2), 0);
                M3Wire.Put(bytes, M3Wire.Link(channel, 4), 0);
            }
            const int count = 5000;
            const int listBytes = 56; // Common24 + next/data links16 + count/one logical offset16.
            int first = (bytes.Length + 7) & ~7;
            Array.Resize(ref bytes, first + count * listBytes);
            for (int index = 0; index < count; index++)
            {
                int address = first + index * listBytes;
                Array.Copy(M5Wire.Header("DL", 32), 0, bytes, address, 24);
                M3Wire.Put(bytes, address + 16, 2);
                M3Wire.Put(bytes, address + 24, index + 1 == count ? 0UL : (ulong)(address + listBytes));
                M3Wire.Put(bytes, address + 32, (ulong)dz);
                M3Wire.Put(bytes, address + 44, 1, 4);
                M3Wire.Put(bytes, address + 48, (ulong)index * 161);
            }
            M3Wire.Put(bytes, M3Wire.Link(M5Wire.Dg, 2), (ulong)first);
            M3Wire.Put(bytes, M3Wire.Payload(bytes, M5Wire.Cg) + 8, count * 7);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true, new MdfReaderOptions { MaximumTraversalDepth = 3 }))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                IEnumerable<MdfSampleBatch> pending = reader.ReadSamples(group, new[] { 1 }, (ulong)(count * 7 - 8), 3);
                MdfSampleBatch batch = pending.Single();
                Assert.Equal(42UL, batch.GetValue(0, 0).UnsignedValue);
                Assert.Equal(0UL, batch.GetValue(1, 0).UnsignedValue);
                Assert.Equal(9007199254740993UL, batch.GetValue(2, 0).UnsignedValue);
                Assert.Equal(count, group.Layout.DataMap.Segments.Length);
                Assert.Equal(2, group.Layout.DataMap.MaximumDepth);
                Assert.Equal(1, source.ReadRanges.Count(range => range.Item1 == dz + 48));
                long before = source.ReadRanges.Sum(range => (long)range.Item2);
                Assert.Equal(3, pending.Single().SampleCount);
                Assert.Equal(172L, source.ReadRanges.Sum(range => (long)range.Item2) - before);
                Assert.Equal(2, source.ReadRanges.Count(range => range.Item1 == dz + 48));
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ZeroLengthListChildrenAreRejectedEvenWhenOffsetsAndTotalWouldOtherwiseMatch(bool compressed)
        {
            byte[] bytes = M5Wire.Read();
            int good = M5Wire.Root(bytes);
            int empty = M5Wire.Add(ref bytes, compressed ? "DZ" : "DT", new ulong[0],
                compressed ? M5Wire.Zipped(new byte[0]) : new byte[0]);
            int list = M5Wire.Add(ref bytes, "DL", new[] { 0UL, (ulong)empty, (ulong)good }, M5Wire.Offsets(0, 0));
            M3Wire.Put(bytes, M3Wire.Link(M5Wire.Dg, 2), (ulong)list);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal("MDF013", Assert.Throws<MdfFormatException>(() => reader.ReadSamples(group, new[] { 1 }).ToList()).DiagnosticCode);
                Assert.Null(group.Layout.DataMap);
                Assert.Same(group, reader.ReadMetadata()[0].ChannelGroups[0]);
            }
        }

        [Theory]
        [InlineData(1U)]
        [InlineData(2U)]
        [InlineData(3U)]
        [InlineData(17U)]
        [InlineData(23U)]
        [InlineData(80U)]
        [InlineData(160U)]
        [InlineData(161U)]
        [InlineData(162U)]
        [InlineData(uint.MaxValue)]
        public void TransposeGeometryIncludesIdentitySingleRowEmptyMatrixAndUnchangedRemainder(uint parameter)
        {
            byte[] bytes = M5Wire.Read();
            byte[] raw = M5Wire.FirstRecords(7);
            int rows = (int)((ulong)raw.Length / parameter);
            int matrix = (int)((ulong)rows * parameter);
            byte[] transposed = (byte[])raw.Clone();
            // Independent flattened-index oracle over literal pre-parser records, not the decoder's nested loops.
            for (int index = 0; index < matrix; index++)
            {
                transposed[index] = raw[(index % rows) * (int)parameter + index / rows];
            }
            byte[] payload = M5Wire.Zipped(transposed);
            payload[2] = 1;
            M3Wire.Put(payload, 4, parameter, 4);
            int block = M5Wire.Add(ref bytes, "DZ", new ulong[0], payload);
            M3Wire.Put(bytes, M3Wire.Link(M5Wire.Dg, 2), (ulong)block);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                AssertRecords(reader.ReadSamples(group, new[] { 0, 1, 2, 3 }, batchSize: 2), 0, 7, false);
                AssertRecords(reader.ReadSamples(group, new[] { 0, 1, 2, 3 }, batchSize: 1,
                    representation: MdfValueRepresentation.Physical), 0, 7, true);
            }
        }

        [Fact]
        public void EveryRecordByteBoundaryCanBeStitchedAcrossMixedDtDzForAllContiguousSubranges()
        {
            byte[] bytes = M5Wire.Read();
            byte[] raw = M5Wire.FirstRecords(7);
            ulong[] links = new ulong[raw.Length + 1];
            ulong[] offsets = new ulong[raw.Length];
            for (int index = 0; index < raw.Length; index++)
            {
                bool compressed = (index & 1) != 0;
                byte[] part = { raw[index] };
                links[index + 1] = (ulong)M5Wire.Add(ref bytes, compressed ? "DZ" : "DT", new ulong[0],
                    compressed ? M5Wire.Zipped(part) : part);
                offsets[index] = (ulong)index;
            }
            int list = M5Wire.Add(ref bytes, "DL", links, M5Wire.Offsets(offsets));
            M3Wire.Put(bytes, M3Wire.Link(M5Wire.Dg, 2), (ulong)list);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                foreach (bool physical in new[] { false, true })
                {
                    foreach (int batchSize in new[] { 1, 3, 1024 })
                    {
                        for (int start = 0; start <= 7; start++)
                        {
                            for (int count = 0; count <= 7 - start; count++)
                            {
                                AssertRecords(reader.ReadSamples(group, new[] { 0, 1, 2, 3 }, (ulong)start, (ulong)count, batchSize,
                                    physical ? MdfValueRepresentation.Physical : MdfValueRepresentation.Raw), start, count, physical);
                            }
                        }
                    }
                }
                Assert.Equal(161, group.Layout.DataMap.Segments.Length);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CorruptExactOutputSizeUsesMdf015WithCauseWithoutDestroyingTheHeaderMap(bool overrun)
        {
            byte[] bytes = M5Wire.Read();
            byte[] raw = M5Wire.FirstRecords(overrun ? 7 : 6);
            if (overrun) { Array.Resize(ref raw, raw.Length + 1); }
            byte[] payload = M5Wire.Zipped(raw, 161);
            int block = M5Wire.Add(ref bytes, "DZ", new ulong[0], payload);
            M3Wire.Put(bytes, M3Wire.Link(M5Wire.Dg, 2), (ulong)block);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                MdfFormatException error = Assert.Throws<MdfFormatException>(() => reader.ReadSamples(group, new[] { 1 }).ToList());
                Assert.Equal("MDF015", error.DiagnosticCode);
                Assert.Equal((ulong)block, error.Offset);
                Assert.Equal("##DZ", error.BlockType);
                Assert.IsType<InvalidDataException>(error.InnerException);
                Assert.NotNull(group.Layout.DataMap);
                Assert.Same(group, reader.ReadMetadata()[0].ChannelGroups[0]);
                Assert.Equal("MDF015", Assert.Throws<MdfFormatException>(() => reader.ReadSamples(group, new[] { 1 }).ToList()).DiagnosticCode);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SelectedCompressedInputAndDeclaredOriginalOutputAreIndependentlyCappedBeforePayloadRead(bool compressedTooLarge)
        {
            byte[] bytes = M5Wire.Read();
            // A tiny stream with a large declared output is deliberate: resource refusal must precede any decode/I/O.
            byte[] payload = M5Wire.Zipped(compressedTooLarge ? M5Wire.FirstRecords(7) : new byte[0], 161);
            int block = M5Wire.Add(ref bytes, "DZ", new ulong[0], payload);
            M3Wire.Put(bytes, M3Wire.Link(M5Wire.Dg, 2), (ulong)block);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true,
                new MdfReaderOptions { MaximumReadBufferBytes = compressedTooLarge ? 161 : 160 }))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                MdfResourceLimitException error = Assert.Throws<MdfResourceLimitException>(() => reader.ReadSamples(group, new[] { 1 }).ToList());
                Assert.Equal("MaximumReadBufferBytes", error.LimitName);
                Assert.Equal(compressedTooLarge ? 172UL : 161UL, error.Requested);
                Assert.NotNull(group.Layout.DataMap);
                Assert.DoesNotContain(source.ReadRanges, range => range.Item1 >= block + 48);
                Assert.Same(group, reader.ReadMetadata()[0].ChannelGroups[0]);
            }
        }

        private static void AssertRecords(IEnumerable<MdfSampleBatch> batches, int first, int count, bool physical)
        {
            // Literal values from the independently committed arrays, including invalid samples' unmodified bits.
            ulong[] exact = { 0, 9007199254740993UL, 1UL << 63, ulong.MaxValue, 17, ulong.MaxValue - 1, 42 };
            long[] signed = { -4096, -1, 0, 4095, 17, -37, 2048 };
            int[] linear = { int.MinValue, -1, 0, 10, int.MaxValue, 100, -50 };
            int[] invalid = { 0, 1, 2, 3, 0, 0, 2 };
            int index = first;
            foreach (MdfSampleBatch batch in batches)
            {
                Assert.Equal((ulong)index, batch.FirstSampleIndex);
                for (int row = 0; row < batch.SampleCount; row++, index++)
                {
                    Assert.Equal(index / 8.0, batch.GetValue(row, 0).FloatingPointValue);
                    Assert.True(batch.GetValue(row, 0).IsValid);
                    Assert.Equal(exact[index], batch.GetValue(row, 1).UnsignedValue);
                    Assert.True(batch.GetValue(row, 1).IsValid);
                    Assert.Equal(signed[index], batch.GetValue(row, 2).SignedValue);
                    Assert.Equal((invalid[index] & 1) == 0, batch.GetValue(row, 2).IsValid);
                    if (physical) { Assert.Equal(.25 * linear[index] - 2, batch.GetValue(row, 3).FloatingPointValue); }
                    else { Assert.Equal((long)linear[index], batch.GetValue(row, 3).SignedValue); }
                    Assert.Equal((invalid[index] & 2) == 0, batch.GetValue(row, 3).IsValid);
                }
            }
            Assert.Equal(first + count, index);
        }
    }
}
