// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Linq;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Writing
{
    public sealed class WriterMultiGroupTests
    {
        internal static MdfChannelDefinition Master() { return new MdfChannelDefinition { Name = "t", Unit = "s", BitCount = 64, ValueKind = MdfValueKind.FloatingPoint, IsMaster = true }; }

        // Group 0 "fast": t = 0.25 * i, speed = i (invalid when i % 5 == 4); group 1 "slow": t = i, temp = i - 3 (physical 0.5 * raw + 40).
        internal static byte[] TwoGroups(string version, bool compress, int fast, int slow, int blockBytes = 64)
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(output, true, new MdfWriterOptions { Version = version, CompressData = compress, CompressionBlockBytes = blockBytes, StartTimeNanoseconds = 1234567890 }))
            {
                writer.SetChannelGroupName(0, "fast");
                writer.AddChannel(Master());
                writer.AddChannel(WriterConfigurationTests.Channel("speed", 16));
                int slowGroup = writer.AddChannelGroup("slow");
                Assert.Equal(1, slowGroup);
                Assert.Equal(2, writer.ChannelGroupCount);
                writer.AddChannel(slowGroup, Master());
                Assert.Equal(1, writer.AddChannel(slowGroup, new MdfChannelDefinition { Name = "temp", Unit = "degC", BitCount = 8, ValueKind = MdfValueKind.SignedInteger, HasLinearConversion = true, Factor = 0.5, Offset = 40 }));
                writer.StartMeasurement();
                int f = 0, s = 0;
                while (f < fast || s < slow)
                {
                    if (f < fast) { writer.SaveSample(new[] { MdfSampleValue.FromFloatingPoint(f * 0.25), MdfSampleValue.FromUnsigned((ulong)f, f % 5 != 4) }); f++; }
                    if (s < slow && (f % 4 == 0 || f >= fast))
                    { writer.SaveSample(slowGroup, new[] { MdfSampleValue.FromFloatingPoint(s), MdfSampleValue.FromSigned(s - 3) }); s++; }
                }
                Assert.Equal((ulong)fast, writer.GetSampleCount(0));
                Assert.Equal((ulong)slow, writer.GetSampleCount(1));
                Assert.Equal((ulong)(fast + slow), writer.SampleCount);
                Assert.Equal(4, writer.ChannelCount);
                Assert.Equal(8 + 2 + 1, writer.RecordByteCount);
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
            }
            return output.Snapshot();
        }

        internal static void AssertTwoGroups(byte[] file, int fast, int slow)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file)))
            {
                Assert.Equal(1234567890UL, reader.Header.RawStartTimeNanoseconds);
                var groups = reader.ReadMetadata();
                Assert.Equal(2, groups.Count);
                MdfChannelGroup first = groups[0].ChannelGroups.Single(), second = groups[1].ChannelGroups.Single();
                Assert.Equal("fast", first.Name);
                Assert.Equal("slow", second.Name);
                Assert.Equal((ulong)fast, first.SampleCount);
                Assert.Equal((ulong)slow, second.SampleCount);
                Assert.True(first.Channels[0].IsMaster);
                Assert.True(second.Channels[0].IsMaster);
                int row = 0;
                foreach (MdfSampleBatch batch in reader.ReadSamples(first, new[] { 0, 1 }, batchSize: 7))
                {
                    for (int i = 0; i < batch.SampleCount; i++, row++)
                    {
                        Assert.Equal(row * 0.25, batch.GetValue(i, 0).FloatingPointValue);
                        Assert.Equal((ulong)row, batch.GetValue(i, 1).UnsignedValue);
                        Assert.Equal(row % 5 != 4, batch.GetValue(i, 1).IsValid);
                    }
                }
                Assert.Equal(fast, row);
                row = 0;
                foreach (MdfSampleBatch batch in reader.ReadSamples(second, new[] { 0, 1 }, representation: MdfValueRepresentation.Physical))
                {
                    for (int i = 0; i < batch.SampleCount; i++, row++)
                    {
                        Assert.Equal((double)row, batch.GetValue(i, 0).FloatingPointValue);
                        Assert.Equal(0.5 * (row - 3) + 40, batch.GetValue(i, 1).FloatingPointValue);
                    }
                }
                Assert.Equal(slow, row);
            }
        }

        [Theory]
        [InlineData("4.10", false, 100, 25)]
        [InlineData("4.20", false, 100, 25)]
        [InlineData("4.10", true, 100, 25)]
        [InlineData("4.20", true, 3, 1)]
        [InlineData("4.10", false, 6, 0)]
        public void InterleavedSavesOfSeveralGroupsAreWrittenAsSortedDataGroups(string version, bool compress, int fast, int slow)
        {
            byte[] file = TwoGroups(version, compress, fast, slow);
            AssertTwoGroups(file, fast, slow);
        }

        [Fact]
        public void LargeBlocksHoldWholeGroupsAndOutputIsDeterministic()
        {
            byte[] first = TwoGroups("4.10", false, 50, 10, 262144);
            Assert.Equal(first, TwoGroups("4.10", false, 50, 10, 262144));
            AssertTwoGroups(first, 50, 10);
        }

        [Fact]
        public void SingleNamedGroupKeepsTheContiguousDataBlock()
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(output, true))
            {
                writer.SetChannelGroupName(0, "only");
                writer.SetChannelGroupName(0, null);
                writer.SetChannelGroupName(0, "only");
                writer.AddChannel(WriterConfigurationTests.Channel());
                writer.StartMeasurement();
                writer.SaveSample(0, new[] { MdfSampleValue.FromUnsigned(7) });
                writer.SaveSample(new[] { MdfSampleValue.FromUnsigned(8) });
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(output.Snapshot())))
            {
                MdfChannelGroup group = reader.ReadMetadata().Single().ChannelGroups.Single();
                Assert.Equal("only", group.Name);
                MdfSampleBatch batch = reader.ReadSamples(group, new[] { 0 }).Single();
                Assert.Equal(8UL, batch.GetValue(1, 0).UnsignedValue);
            }
        }

        [Fact]
        public void GroupArgumentsStatesAndRevisionsAreValidated()
        {
            using (MdfWriter writer = MdfWriter.Create(new TestWriteStream(), true))
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => writer.AddChannel(1, WriterConfigurationTests.Channel()));
                Assert.Throws<ArgumentOutOfRangeException>(() => writer.AddChannel(-1, WriterConfigurationTests.Channel()));
                Assert.Throws<ArgumentOutOfRangeException>(() => writer.SetChannelGroupName(3, "x"));
                Assert.Throws<ArgumentOutOfRangeException>(() => writer.GetSampleCount(1));
                Assert.Throws<ArgumentException>(() => writer.SetChannelGroupName(0, string.Empty));
                Assert.Throws<ArgumentException>(() => writer.AddChannelGroup("a\0b"));
                Assert.Equal(1, writer.ChannelGroupCount);
                int group = writer.AddChannelGroup();
                writer.AddChannel(group, Master());
                Assert.Throws<ArgumentException>(() => writer.AddChannel(group, Master()));
                writer.AddChannel(0, Master()); // Each group may have its own master.
                writer.AddChannelGroup();
                Assert.Throws<InvalidOperationException>(() => writer.StartMeasurement()); // Group 2 has no channel.
                Assert.Equal(MdfWriterState.Configuring, writer.State);
            }
            using (MdfWriter writer = MdfWriter.Create(new TestWriteStream(), true, new MdfWriterOptions { Version = "3.30" }))
            {
                Assert.Throws<NotSupportedException>(() => writer.AddChannelGroup("x")); // MDF 3 groups have no name.
                Assert.Throws<NotSupportedException>(() => writer.SetChannelGroupName(0, "x"));
                Assert.Equal(1, writer.AddChannelGroup()); // unnamed groups behind record IDs.
            }
            using (MdfWriter writer = MdfWriter.Create(new TestWriteStream(), true, new MdfWriterOptions { MaximumChannelCount = 2 }))
            {
                writer.AddChannelGroup();
                Assert.Throws<MdfResourceLimitException>(() => writer.AddChannelGroup());
            }
            using (MdfWriter writer = MdfWriter.Create(new TestWriteStream(), true))
            {
                int group = writer.AddChannelGroup();
                writer.AddChannel(WriterConfigurationTests.Channel());
                writer.AddChannel(group, WriterConfigurationTests.Channel());
                writer.StartMeasurement();
                Assert.Throws<InvalidOperationException>(() => writer.AddChannelGroup());
                Assert.Throws<InvalidOperationException>(() => writer.SetChannelGroupName(0, "late"));
                Assert.Throws<ArgumentOutOfRangeException>(() => writer.SaveSample(2, new[] { MdfSampleValue.FromUnsigned(1) }));
                Assert.Throws<ArgumentException>(() => writer.SaveSample(group, new[] { MdfSampleValue.FromUnsigned(1), MdfSampleValue.FromUnsigned(1) }));
                writer.SaveSample(group, new[] { MdfSampleValue.FromUnsigned(1) });
                Assert.Equal(0UL, writer.GetSampleCount(0));
                Assert.Equal(1UL, writer.GetSampleCount(group));
            }
        }
    }
}
