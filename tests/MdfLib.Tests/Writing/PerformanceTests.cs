// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Writing
{
    // write buffering, the background queue and the decoded-block read cache. Effort is asserted through call
    // counts, never wall-clock time, so the tests are deterministic on every CI cell.
    public sealed class PerformanceTests
    {
        private static MdfSampleValue[] Row(int i) { return new[] { MdfSampleValue.FromFloatingPoint(i * 0.5), MdfSampleValue.FromUnsigned((ulong)(i % 65536)) }; }

        private static MdfWriter Writer(TestWriteStream output, MdfWriterOptions options)
        {
            MdfWriter writer = MdfWriter.Create(output, true, options);
            writer.AddChannel(WriterMultiGroupTests.Master());
            writer.AddChannel(WriterConfigurationTests.Channel("value", 16));
            return writer;
        }

        private static byte[] Write(int rows, MdfWriterOptions options, out int writes)
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = Writer(output, options))
            {
                writer.StartMeasurement();
                int before = output.Writes;
                for (int i = 0; i < rows; i++) { writer.SaveSample(Row(i)); }
                writes = output.Writes - before;
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
            }
            return output.Snapshot();
        }

        private static void AssertRows(byte[] file, int rows)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups.Single();
                Assert.Equal((ulong)rows, group.SampleCount);
                int n = 0;
                foreach (MdfSampleBatch batch in reader.ReadSamples(group, new[] { 0, 1 }))
                {
                    for (int i = 0; i < batch.SampleCount; i++, n++)
                    {
                        Assert.Equal(n * 0.5, batch.GetValue(i, 0).FloatingPointValue);
                        Assert.Equal((ulong)(n % 65536), batch.GetValue(i, 1).UnsignedValue);
                    }
                }
                Assert.Equal(rows, n);
                Assert.True(MdfValidator.Validate(reader).IsValid);
            }
        }

        [Theory]
        [InlineData("4.10")]
        [InlineData("4.20")]
        [InlineData("3.30")]
        public void WriteBufferProducesIdenticalFilesWithFarFewerWrites(string version)
        {
            int plainWrites, bufferedWrites;
            byte[] plain = Write(1000, new MdfWriterOptions { Version = version }, out plainWrites);
            byte[] buffered = Write(1000, new MdfWriterOptions { Version = version, WriteBufferBytes = 1000 }, out bufferedWrites);
            Assert.Equal(plain, buffered);
            Assert.Equal(1000, plainWrites);
            Assert.InRange(bufferedWrites, 1, 15); // 10/11-byte records, 90+ per buffer, the rest at stop.
            AssertRows(buffered, 1000);
        }

        [Fact]
        public void BufferedRecordsReachTheDestinationOnlyWhenFullOrStopped()
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = Writer(output, new MdfWriterOptions { WriteBufferBytes = 25 }))
            {
                int r = writer.RecordByteCount; // 11: two records fit, three do not.
                Assert.Equal(11, r);
                writer.StartMeasurement();
                long start = output.WrittenBytes;
                writer.SaveSample(Row(0));
                writer.SaveSample(Row(1));
                Assert.Equal(start, output.WrittenBytes);
                Assert.Equal(2UL, writer.SampleCount);
                writer.SaveSample(Row(2)); // 30 > 25: the first two are written, the third waits.
                Assert.Equal(start + 2 * r, output.WrittenBytes);
                writer.StopMeasurement();
                Assert.Equal(start + 3 * r, output.WrittenBytes);
                writer.FinalizeMeasurement();
            }
            AssertRows(output.Snapshot(), 3);
        }

        [Fact]
        public void RecordsLargerThanTheBufferAreWrittenDirectly()
        {
            int writes;
            byte[] file = Write(7, new MdfWriterOptions { WriteBufferBytes = 3 }, out writes);
            Assert.Equal(7, writes);
            AssertRows(file, 7);
        }

        [Fact]
        public void BufferedFlushFailureFaultsTheWriter()
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = Writer(output, new MdfWriterOptions { WriteBufferBytes = 100 }))
            {
                writer.StartMeasurement();
                writer.SaveSample(Row(0));
                output.FailWriteAt = output.Writes + 1;
                Assert.Throws<IOException>(() => writer.StopMeasurement());
                Assert.Equal(MdfWriterState.Faulted, writer.State);
            }
        }

        [Fact]
        public void NegativeBufferAndCacheSizesAreRefused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => MdfWriter.Create(new TestWriteStream(), true, new MdfWriterOptions { WriteBufferBytes = -1 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => MdfBusLogger.Create(new TestWriteStream(), MdfBusType.Can, true, new MdfBusLoggerOptions { WriteBufferBytes = -1 }));
            byte[] file = Write(3, null, out int _);
            Assert.Throws<ArgumentOutOfRangeException>(() => MdfReader.Open(new MemoryStream(file), false, new MdfReaderOptions { DecodedBlockCacheBytes = -1 }));
        }

        [Fact]
        public void BusLoggerBufferingKeepsFrames()
        {
            List<MdfBusMessage> frames = Enumerable.Range(0, 300).Select(i => new MdfBusMessage
            {
                Type = MdfBusMessageType.CanDataFrame, Timestamp = i * 0.001, Id = (uint)i, Data = new[] { (byte)i, (byte)(i >> 8) }
            }).ToList();
            TestWriteStream output = new TestWriteStream();
            MdfBusLoggerOptions options = new MdfBusLoggerOptions { WriteBufferBytes = 4096, MessageTypes = new List<MdfBusMessageType> { MdfBusMessageType.CanDataFrame } };
            using (MdfBusLogger logger = MdfBusLogger.Create(output, MdfBusType.Can, true, options))
            {
                logger.StartMeasurement();
                int before = output.Writes;
                foreach (MdfBusMessage frame in frames) { logger.Write(frame); }
                Assert.True(output.Writes - before < 150, "frames were not buffered");
                logger.StopMeasurement();
                logger.FinalizeMeasurement();
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(output.Snapshot())))
            {
                List<MdfBusMessage> read = MdfBusReader.ReadMessages(reader).ToList();
                Assert.Equal(frames.Select(f => f.Id), read.Select(f => f.Id));
                Assert.Equal(frames.Select(f => f.Data), read.Select(f => f.Data));
            }
        }

        private static byte[] Compressed(int rows, int blockBytes)
        {
            int writes;
            return Write(rows, new MdfWriterOptions { CompressData = true, CompressionBlockBytes = blockBytes }, out writes);
        }

        private static List<ulong> Column(MdfReader reader, int channel, bool master = true)
        {
            MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups.Single();
            List<ulong> values = new List<ulong>();
            foreach (MdfSampleBatch batch in reader.ReadSamples(group, new[] { channel }))
            {
                for (int i = 0; i < batch.SampleCount; i++)
                {
                    MdfSampleValue value = batch.GetValue(i, 0);
                    values.Add(channel == 0 && master ? (ulong)(value.FloatingPointValue * 2) : value.UnsignedValue);
                }
            }
            return values;
        }

        [Fact]
        public void ReadCacheInflatesEachBlockOnceAcrossEnumerations()
        {
            byte[] file = Compressed(500, 110); // 10 eleven-byte records per DZ: 50 blocks.
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file), false, new MdfReaderOptions { DecodedBlockCacheBytes = 1 << 20 }))
            {
                Assert.Equal(Enumerable.Range(0, 500).Select(i => (ulong)i), Column(reader, 0));
                Assert.Equal(50L, reader.BlockCache.Misses);
                long hits = reader.BlockCache.Hits;
                Assert.Equal(Enumerable.Range(0, 500).Select(i => (ulong)(i % 65536)), Column(reader, 1));
                Assert.Equal(50L, reader.BlockCache.Misses);
                Assert.True(reader.BlockCache.Hits >= hits + 50);
                Assert.Equal(50, reader.BlockCache.Count);
                Assert.Equal(5500L, reader.BlockCache.Bytes);
            }
        }

        [Fact]
        public void ReadCacheEvictsLeastRecentlyUsedBlocksWithinItsBudget()
        {
            byte[] file = Compressed(500, 110);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file), false, new MdfReaderOptions { DecodedBlockCacheBytes = 275 }))
            {
                Column(reader, 0);
                Column(reader, 1);
                Assert.Equal(2, reader.BlockCache.Count); // 275 / 110 bytes per block.
                Assert.Equal(220L, reader.BlockCache.Bytes);
                Assert.Equal(100L, reader.BlockCache.Misses); // Sequential scans larger than the cache never hit.
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file), false, new MdfReaderOptions { DecodedBlockCacheBytes = 109 }))
            {
                Assert.Equal(Enumerable.Range(0, 500).Select(i => (ulong)i), Column(reader, 0));
                Assert.Equal(0, reader.BlockCache.Count); // A block above the budget is never retained.
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file)))
            {
                Assert.Null(reader.BlockCache);
                Assert.Equal(Enumerable.Range(0, 500).Select(i => (ulong)i), Column(reader, 0));
            }
        }

        [Fact]
        public void BackgroundWriterSavesQueuedSamplesInOrder()
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = Writer(output, new MdfWriterOptions { WriteBufferBytes = 4096 }))
            {
                writer.StartMeasurement();
                using (MdfBackgroundWriter queue = new MdfBackgroundWriter(writer, 16))
                {
                    Assert.Equal(16, queue.Capacity);
                    MdfSampleValue[] reused = new MdfSampleValue[2];
                    for (int i = 0; i < 2000; i++)
                    {
                        Row(i).CopyTo(reused, 0);
                        queue.SaveSample(reused); // Copied when queued.
                    }
                    queue.Complete();
                    Assert.Equal(2000L, queue.AcceptedCount);
                    Assert.Equal(2000L, queue.WrittenCount);
                    Assert.Equal(0, queue.PendingCount);
                    Assert.Null(queue.Fault);
                    Assert.Throws<InvalidOperationException>(() => queue.SaveSample(Row(0)));
                }
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
            }
            AssertRows(output.Snapshot(), 2000);
        }

        [Fact]
        public void BackgroundWriterAcceptsConcurrentProducers()
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(output, true))
            {
                writer.AddChannel(WriterConfigurationTests.Channel("value", 32));
                writer.StartMeasurement();
                using (MdfBackgroundWriter queue = new MdfBackgroundWriter(writer, 8))
                {
                    Parallel.For(0, 4, p =>
                    {
                        for (int i = 0; i < 250; i++) { queue.SaveSample(new[] { MdfSampleValue.FromUnsigned((ulong)(p * 1000 + i)) }); }
                    });
                    queue.Complete();
                }
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(output.Snapshot())))
            {
                List<ulong> values = Column(reader, 0, false);
                Assert.Equal(Enumerable.Range(0, 4).SelectMany(p => Enumerable.Range(p * 1000, 250)).Select(v => (ulong)v), values.OrderBy(v => v));
                foreach (int p in Enumerable.Range(0, 4))
                {
                    List<ulong> own = values.Where(v => v / 1000 == (ulong)p).ToList();
                    Assert.Equal(own.OrderBy(v => v), own); // Each producer's order is kept.
                }
            }
        }

        [Fact]
        public void BackgroundWriterReportsTheFirstWorkerFailure()
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = Writer(output, null))
            {
                writer.StartMeasurement();
                MdfBackgroundWriter queue = new MdfBackgroundWriter(writer);
                queue.SaveSample(Row(0));
                queue.SaveSample(new[] { MdfSampleValue.FromUnsigned(1) }); // Wrong width: refused by the writer.
                InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => queue.Complete());
                Assert.IsType<ArgumentException>(error.InnerException);
                Assert.Same(error.InnerException, queue.Fault);
                Assert.Equal(1L, queue.WrittenCount);
                Assert.Throws<InvalidOperationException>(() => queue.SaveSample(Row(3)));
                queue.Dispose();
                queue.Dispose();
                Assert.Equal(1UL, writer.SampleCount);
            }
        }

        [Fact]
        public void BackgroundWriterTryMethodsDoNotBlockWhenFull()
        {
            TestWriteStream output = new TestWriteStream();
            using (ManualResetEventSlim gate = new ManualResetEventSlim(false))
            using (MdfWriter writer = Writer(output, null))
            {
                writer.StartMeasurement();
                output.BeforeWrite = s => gate.Wait();
                using (MdfBackgroundWriter queue = new MdfBackgroundWriter(writer, 2))
                {
                    try
                    {
                    int accepted = 0;
                    for (int i = 0; i < 10; i++) { if (queue.TrySaveSample(0, Row(i))) { accepted++; } }
                    Assert.InRange(accepted, 2, 3); // Two queued plus at most one taken by the blocked worker.
                    Assert.Throws<InvalidOperationException>(() => queue.TryWrite(new MdfBusMessage()));
                    Assert.Throws<ArgumentNullException>(() => queue.TrySaveSample(0, null));
                    gate.Set();
                    queue.Complete();
                    Assert.Equal(accepted, (int)queue.WrittenCount);
                    }
                    finally { gate.Set(); }
                }
                output.BeforeWrite = null;
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
            }
        }

        [Fact]
        public void BackgroundWriterLogsCopiedBusFrames()
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfBusLogger logger = MdfBusLogger.Create(output, MdfBusType.Can, true))
            {
                logger.StartMeasurement();
                using (MdfBackgroundWriter queue = new MdfBackgroundWriter(logger, 4))
                {
                    MdfBusMessage frame = new MdfBusMessage { Type = MdfBusMessageType.CanDataFrame, Data = new byte[] { 1, 2 } };
                    for (int i = 0; i < 100; i++)
                    {
                        frame.Timestamp = i * 0.01;
                        frame.Id = (uint)i;
                        frame.Data[0] = (byte)i;
                        frame.Fields["SRR"] = MdfSampleValue.FromUnsigned((ulong)(i & 1));
                        queue.Write(frame);
                    }
                    Assert.Throws<InvalidOperationException>(() => queue.SaveSample(Row(0)));
                    Assert.Throws<ArgumentNullException>(() => queue.Write(null));
                    queue.Complete();
                }
                logger.StopMeasurement();
                logger.FinalizeMeasurement();
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(output.Snapshot())))
            {
                List<MdfBusMessage> read = MdfBusReader.ReadMessages(reader).Take(100).ToList();
                Assert.Equal(Enumerable.Range(0, 100).Select(i => (uint)i), read.Select(m => m.Id));
                Assert.Equal(Enumerable.Range(0, 100).Select(i => (byte)i), read.Select(m => m.Data[0]));
                Assert.Equal(Enumerable.Range(0, 100).Select(i => (ulong)(i & 1)), read.Select(m => m.Fields["SRR"].UnsignedValue));
            }
        }

        [Fact]
        public void BackgroundWriterArgumentsAreValidated()
        {
            Assert.Throws<ArgumentNullException>(() => new MdfBackgroundWriter((MdfWriter)null));
            Assert.Throws<ArgumentNullException>(() => new MdfBackgroundWriter((MdfBusLogger)null));
            using (MdfWriter writer = Writer(new TestWriteStream(), null))
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => new MdfBackgroundWriter(writer, 0));
            }
        }
    }
}
