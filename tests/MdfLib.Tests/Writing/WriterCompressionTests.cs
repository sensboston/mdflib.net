// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MdfLib.Compression;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Writing
{
    public sealed class WriterCompressionTests
    {
        private static byte[] Write(string version, int rows, int blockBytes, bool compress = true)
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(output, true, new MdfWriterOptions { Version = version, CompressData = compress, CompressionBlockBytes = blockBytes }))
            {
                writer.AddChannel(new MdfChannelDefinition { Name = "t", BitCount = 64, ValueKind = MdfValueKind.FloatingPoint, IsMaster = true });
                writer.AddChannel(WriterConfigurationTests.Channel("speed", 16));
                writer.AddChannel(new MdfChannelDefinition { Name = "temp", BitCount = 32, ValueKind = MdfValueKind.SignedInteger, HasLinearConversion = true, Factor = 0.5, Offset = -40 });
                writer.StartMeasurement();
                for (int row = 0; row < rows; row++)
                {
                    writer.SaveSample(new[]
                    {
                        MdfSampleValue.FromFloatingPoint(row * 0.01),
                        MdfSampleValue.FromUnsigned((ulong)(row % 1000), row % 7 != 3),
                        MdfSampleValue.FromSigned(row % 50 - 25),
                    });
                }
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
            }
            return output.Snapshot();
        }

        private static int Count(byte[] bytes, string tag)
        {
            byte[] pattern = Encoding.ASCII.GetBytes(tag);
            int count = 0;
            for (int i = 0; i + pattern.Length <= bytes.Length; i += 8) // Blocks are eight-byte aligned.
            {
                bool match = true;
                for (int j = 0; j < pattern.Length && match; j++) { match = bytes[i + j] == pattern[j]; }
                if (match) { count++; }
            }
            return count;
        }

        private static void AssertRows(byte[] file, int rows)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.True(group.CanReadSamples, group.UnsupportedReason);
                Assert.Equal((ulong)rows, group.SampleCount);
                int seen = 0;
                foreach (MdfSampleBatch batch in reader.ReadSamples(group, new[] { 0, 1, 2 }, batchSize: 333))
                {
                    for (int i = 0; i < batch.SampleCount; i++, seen++)
                    {
                        Assert.Equal((ulong)seen, batch.FirstSampleIndex + (ulong)i);
                        Assert.Equal(seen * 0.01, batch.GetValue(i, 0).FloatingPointValue);
                        MdfSampleValue speed = batch.GetValue(i, 1);
                        Assert.Equal((ulong)(seen % 1000), speed.UnsignedValue);
                        Assert.Equal(seen % 7 != 3, speed.IsValid);
                        Assert.Equal(seen % 50 - 25, batch.GetValue(i, 2).SignedValue);
                    }
                }
                Assert.Equal(rows, seen);
                foreach (MdfSampleBatch batch in reader.ReadSamples(group, new[] { 2 }, (ulong)Math.Max(0, rows - 5), (ulong)Math.Min(5, rows), 2, MdfValueRepresentation.Physical))
                {
                    for (int i = 0; i < batch.SampleCount; i++)
                    {
                        long raw = (long)(batch.FirstSampleIndex + (ulong)i) % 50 - 25;
                        Assert.Equal(0.5 * raw - 40, batch.GetValue(i, 0).FloatingPointValue);
                    }
                }
            }
        }

        [Theory]
        [InlineData("4.10")]
        [InlineData("4.20")]
        public void CompressedMeasurementsRoundTripThroughDzBlocksUnderOneDataList(string version)
        {
            const int Rows = 5000;
            byte[] file = Write(version, Rows, 4096);
            int record = 8 + 2 + 4 + 1;
            int blocks = (Rows + 4096 / record - 1) / (4096 / record);
            Assert.Equal(blocks, Count(file, "##DZ"));
            Assert.Equal(1, Count(file, "##DL"));
            Assert.Equal(0, Count(file, "##DT"));
            AssertRows(file, Rows);
            byte[] plain = Write(version, Rows, 4096, false);
            Assert.True(file.Length < plain.Length, file.Length + " vs " + plain.Length);
            AssertRows(plain, Rows);
        }

        [Fact]
        public void DefaultBlockSizeWritesOneBlockForSmallMeasurementsAndOutputIsDeterministic()
        {
            byte[] first = Write("4.10", 1000, 262144);
            byte[] second = Write("4.10", 1000, 262144);
            Assert.Equal(first, second);
            Assert.Equal(1, Count(first, "##DZ"));
            AssertRows(first, 1000);
        }

        [Fact]
        public void BlockSmallerThanARecordHoldsExactlyOneRecordAndLongListsChain()
        {
            const int Rows = 1100; // > 1024 data links: two chained DL blocks.
            byte[] file = Write("4.10", Rows, 1);
            Assert.Equal(Rows, Count(file, "##DZ"));
            Assert.Equal(2, Count(file, "##DL"));
            AssertRows(file, Rows);
        }

        [Fact]
        public void PartialLastBlockAndExactlyFullLastBlockAreBothReadable()
        {
            int record = 15;
            AssertRows(Write("4.20", 10, record * 5), 10); // Two full blocks, nothing pending.
            AssertRows(Write("4.20", 11, record * 5), 11); // Two full blocks plus one record.
            AssertRows(Write("4.20", 1, record * 5), 1);
        }

        [Fact]
        public void EmptyCompressedMeasurementHasNoDataBlocks()
        {
            byte[] file = Write("4.10", 0, 4096);
            Assert.Equal(0, Count(file, "##DZ"));
            Assert.Equal(0, Count(file, "##DL"));
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(0UL, group.SampleCount);
                Assert.Empty(reader.ReadSamples(group, new[] { 0 }));
            }
        }

        [Fact]
        public void OptionsAreValidatedBeforeAnyDestinationIsTouched()
        {
            MdfWriterOptions defaults = new MdfWriterOptions();
            Assert.False(defaults.CompressData);
            Assert.Equal(262144, defaults.CompressionBlockBytes);
            TestWriteStream output = new TestWriteStream();
            Assert.Throws<NotSupportedException>(() => MdfWriter.Create(output, true, new MdfWriterOptions { Version = "3.30", CompressData = true }));
            Assert.Throws<ArgumentOutOfRangeException>(() => MdfWriter.Create(output, true, new MdfWriterOptions { CompressionBlockBytes = 0 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => MdfWriter.Create(output, true, new MdfWriterOptions { CompressionBlockBytes = -1 }));
            Assert.Equal(0, output.Writes);
            Assert.Equal(0L, output.Length);
        }

        [Fact]
        public void RecordsStayBufferedUntilABlockFillsAndFailuresFaultTheWriter()
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(output, true, new MdfWriterOptions { CompressData = true, CompressionBlockBytes = 8 }))
            {
                writer.AddChannel(WriterConfigurationTests.Channel("x", 8));
                writer.StartMeasurement();
                long prefix = output.Length;
                for (int i = 0; i < 3; i++) { writer.SaveSample(new[] { MdfSampleValue.FromUnsigned((ulong)i) }); }
                Assert.Equal(prefix, output.Length); // Record = 2 bytes, block = 4 records: three pending records stay buffered.
                Assert.Equal(3UL, writer.SampleCount);
                output.FailWriteAt = output.Writes + 1;
                Assert.Throws<IOException>(() => writer.SaveSample(new[] { MdfSampleValue.FromUnsigned(3) }));
                Assert.Equal(MdfWriterState.Faulted, writer.State);
            }
        }

        public static IEnumerable<object[]> Payloads()
        {
            Random random = new Random(7);
            byte[] noise = new byte[70000];
            random.NextBytes(noise);
            byte[] alphabet = new byte[100000];
            for (int i = 0; i < alphabet.Length; i++) { alphabet[i] = (byte)('a' + random.Next(4)); }
            byte[] records = new byte[120000];
            for (int i = 0; i < records.Length; i += 3) { records[i] = (byte)(i / 3 % 7); records[i + 2] = (byte)(i / 3 % 3); }
            byte[] runs = Enumerable.Repeat((byte)0x55, 40000).ToArray();
            byte[] ramp = Enumerable.Range(0, 76800).Select(i => (byte)i).ToArray();
            yield return new object[] { "empty", new byte[0] };
            yield return new object[] { "one", new byte[] { 42 } };
            yield return new object[] { "two", new byte[] { 1, 2 } };
            yield return new object[] { "three", new byte[] { 1, 2, 3 } };
            yield return new object[] { "high-literals", Enumerable.Range(0, 256).Select(i => (byte)(255 - i)).ToArray() };
            yield return new object[] { "noise", noise };
            yield return new object[] { "alphabet", alphabet };
            yield return new object[] { "records", records };
            yield return new object[] { "runs", runs };
            yield return new object[] { "ramp", ramp };
        }

        [Theory]
        [MemberData(nameof(Payloads))]
        public void ManagedDeflaterOutputInflatesExactlyWithTheReaderInflater(string name, byte[] payload)
        {
            byte[] padded = new byte[payload.Length + 6];
            Array.Copy(payload, 0, padded, 3, payload.Length);
            int checkpoints = 0;
            byte[] compressed = ZlibDeflater.Compress(padded, 3, payload.Length, () => checkpoints++);
            Assert.True(compressed.Length <= payload.Length + payload.Length / 8 + 32, name);
            Assert.Equal(0x78, compressed[0]);
            Assert.Equal(0, ((compressed[0] << 8) | compressed[1]) % 31);
            byte[] inflated = ZlibInflater.Inflate(compressed, payload.Length, 1 << 20, () => { });
            Assert.Equal(payload, inflated);
            Assert.Equal(payload.Length / 65536, checkpoints);
        }

        [Fact]
        public void AdlerChecksumMatchesKnownVectorsAndArgumentsAreChecked()
        {
            Assert.Equal(1U, ZlibDeflater.Adler32(new byte[0], 0, 0));
            Assert.Equal(0x11E60398U, ZlibDeflater.Adler32(Encoding.ASCII.GetBytes("Wikipedia"), 0, 9));
            byte[] big = Enumerable.Repeat((byte)0xFF, 100000).ToArray();
            uint a = 1, b = 0;
            foreach (byte value in big) { a = (a + value) % 65521; b = (b + a) % 65521; }
            Assert.Equal((b << 16) | a, ZlibDeflater.Adler32(big, 0, big.Length));
            Assert.Throws<ArgumentNullException>(() => ZlibDeflater.Compress(null, 0, 0, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => ZlibDeflater.Compress(new byte[4], 2, 3, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => ZlibDeflater.Compress(new byte[4], -1, 1, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => ZlibDeflater.Compress(new byte[4], 0, -1, null));
        }

        [Fact]
        public void CheckpointExceptionsPropagateFromLongCompressions()
        {
            byte[] data = new byte[200000];
            Assert.Throws<OperationCanceledException>(() => ZlibDeflater.Compress(data, 0, data.Length, () => { throw new OperationCanceledException(); }));
        }
    }
}
