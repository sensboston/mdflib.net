// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Writing
{
    public sealed class PreTriggerQueueTests
    {
        private static MdfSampleValue[] Row(double t) { return new[] { MdfSampleValue.FromFloatingPoint(t), MdfSampleValue.FromUnsigned((ulong)(t * 10)) }; }

        private static MdfWriter Writer(TestWriteStream output)
        {
            MdfWriter writer = MdfWriter.Create(output, true);
            writer.AddChannel(WriterMultiGroupTests.Master());
            writer.AddChannel(WriterConfigurationTests.Channel("value", 16));
            return writer;
        }

        private static List<double> Times(byte[] file)
        {
            List<double> times = new List<double>();
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups.Single();
                foreach (MdfSampleBatch batch in reader.ReadSamples(group, new[] { 0, 1 }))
                {
                    for (int i = 0; i < batch.SampleCount; i++)
                    {
                        double t = batch.GetValue(i, 0).FloatingPointValue;
                        Assert.Equal((ulong)(t * 10), batch.GetValue(i, 1).UnsignedValue);
                        times.Add(t);
                    }
                }
            }
            return times;
        }

        [Fact]
        public void TriggerWritesOnlyTheWindowBeforeTheTriggerThenPassesThrough()
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = Writer(output))
            {
                MdfPreTriggerQueue queue = new MdfPreTriggerQueue(writer, 2.5);
                for (int t = 0; t <= 10; t++) { queue.Add(0, t, Row(t)); }
                Assert.Equal(3, queue.Count); // Newest 10: times 8, 9, 10 are inside 2.5 s.
                Assert.Equal(8, queue.DroppedCount);
                Assert.False(queue.IsTriggered);
                writer.StartMeasurement();
                Assert.Equal(0UL, writer.SampleCount);
                Assert.Equal(2, queue.Trigger(11)); // From 8.5: 9 and 10.
                Assert.True(queue.IsTriggered);
                Assert.Equal(9, queue.DroppedCount);
                Assert.Equal(0, queue.Count);
                queue.Add(0, 11, Row(11));
                queue.Add(0, 12, Row(12));
                Assert.Equal(4UL, writer.SampleCount);
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
            }
            Assert.Equal(new[] { 9.0, 10.0, 11.0, 12.0 }, Times(output.Snapshot()));
        }

        [Fact]
        public void CapacityDropsTheOldestRowsAndRowsAreCopied()
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = Writer(output))
            {
                MdfPreTriggerQueue queue = new MdfPreTriggerQueue(writer, 100, 2);
                Assert.Equal(2, queue.Capacity);
                Assert.Equal(100, queue.PreTriggerSeconds);
                MdfSampleValue[] reused = Row(1);
                queue.Add(0, 1, reused);
                reused = Row(2);
                queue.Add(0, 2, reused);
                reused[0] = MdfSampleValue.FromFloatingPoint(3);
                reused[1] = MdfSampleValue.FromUnsigned(30);
                queue.Add(0, 3, reused);
                reused[0] = MdfSampleValue.FromFloatingPoint(99); // Mutating after Add must not change buffered rows.
                Assert.Equal(2, queue.Count);
                Assert.Equal(1, queue.DroppedCount);
                writer.StartMeasurement();
                Assert.Equal(2, queue.Trigger(0));
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
            }
            Assert.Equal(new[] { 2.0, 3.0 }, Times(output.Snapshot()));
        }

        [Fact]
        public void ZeroWindowKeepsOnlyTheNewestTimeAndEmptyTriggerIsAllowed()
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = Writer(output))
            {
                MdfPreTriggerQueue queue = new MdfPreTriggerQueue(writer, 0);
                queue.Add(0, 5, Row(5));
                queue.Add(0, 4, Row(4)); // Older than newest: discarded immediately.
                Assert.Equal(1, queue.Count);
                writer.StartMeasurement();
                Assert.Equal(0, queue.Trigger(6));
                Assert.Equal(0UL, writer.SampleCount);
            }
        }

        [Fact]
        public void InvalidArgumentsAndStatesAreRefused()
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = Writer(output))
            {
                Assert.Throws<ArgumentNullException>(() => new MdfPreTriggerQueue(null, 1));
                Assert.Throws<ArgumentOutOfRangeException>(() => new MdfPreTriggerQueue(writer, -1));
                Assert.Throws<ArgumentOutOfRangeException>(() => new MdfPreTriggerQueue(writer, double.NaN));
                Assert.Throws<ArgumentOutOfRangeException>(() => new MdfPreTriggerQueue(writer, double.PositiveInfinity));
                Assert.Throws<ArgumentOutOfRangeException>(() => new MdfPreTriggerQueue(writer, 1, 0));
                MdfPreTriggerQueue queue = new MdfPreTriggerQueue(writer, 1);
                Assert.Throws<ArgumentNullException>(() => queue.Add(0, 1, null));
                Assert.Throws<ArgumentOutOfRangeException>(() => queue.Add(0, double.NaN, Row(1)));
                Assert.Throws<ArgumentOutOfRangeException>(() => queue.Add(1, 1, Row(1)));
                Assert.Throws<ArgumentOutOfRangeException>(() => queue.Add(-1, 1, Row(1)));
                Assert.Throws<InvalidOperationException>(() => queue.Trigger(1)); // Writer is still configuring.
                Assert.Throws<ArgumentOutOfRangeException>(() => queue.Trigger(double.NaN));
                writer.StartMeasurement();
                queue.Trigger(1);
                Assert.Throws<InvalidOperationException>(() => queue.Trigger(2));
                Assert.Throws<ArgumentException>(() => queue.Add(0, 2, new[] { MdfSampleValue.FromFloatingPoint(2) }));
            }
        }
    }
}
