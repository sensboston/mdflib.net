// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System.IO;
using System.Linq;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Writing
{
    // channel groups that share one unsorted data group. The records of every group go to one record stream, each
    // behind the record ID of its group; a third group in its own data group makes the file write blocked streams.
    public sealed class WriterSharedDataGroupTests
    {
        private const int Fast = 10, Slow = 5;

        [Theory]
        [InlineData("4.10", false, false)]
        [InlineData("4.20", true, false)]
        [InlineData("4.10", false, true)]
        [InlineData("4.20", true, true)]
        public void GroupsOfOneDataGroupReadBackTheirOwnRows(string version, bool compress, bool ownDataGroup)
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(output, true, new MdfWriterOptions { Version = version, CompressData = compress, CompressionBlockBytes = 64 }))
            {
                writer.SetChannelGroupName(0, "fast");
                writer.AddChannel(WriterMultiGroupTests.Master());
                writer.AddChannel(WriterConfigurationTests.Channel("speed", 16));
                int slow = writer.AddChannelGroupToDataGroup(0, new MdfChannelGroupDefinition { Name = "slow" });
                Assert.Equal(1, slow);
                writer.AddChannel(slow, WriterMultiGroupTests.Master());
                writer.AddChannel(slow, WriterConfigurationTests.Channel("count", 8));
                int own = -1;
                if (ownDataGroup)
                {
                    own = writer.AddChannelGroup(new MdfChannelGroupDefinition { Name = "own" });
                    writer.AddChannel(own, WriterMultiGroupTests.Master());
                    writer.AddChannel(own, WriterConfigurationTests.Channel("flag", 8));
                }
                writer.StartMeasurement();
                for (int i = 0; i < Fast; i++)
                {
                    writer.SaveSample(new[] { MdfSampleValue.FromFloatingPoint(i * 0.25), MdfSampleValue.FromUnsigned((ulong)i, i % 5 != 4) });
                    if (i % 2 == 0)
                    {
                        writer.SaveSample(slow, new[] { MdfSampleValue.FromFloatingPoint(i / 2), MdfSampleValue.FromUnsigned((ulong)(i / 2 + 10), true) });
                    }
                    if (own >= 0)
                    {
                        writer.SaveSample(own, new[] { MdfSampleValue.FromFloatingPoint(i), MdfSampleValue.FromUnsigned((ulong)(i % 2), true) });
                    }
                }
                Assert.Equal((ulong)Fast, writer.GetSampleCount(0));
                Assert.Equal((ulong)Slow, writer.GetSampleCount(1));
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
            }

            using (MdfReader reader = MdfReader.Open(new MemoryStream(output.Snapshot())))
            {
                var groups = reader.ReadMetadata();
                Assert.Equal(ownDataGroup ? 2 : 1, groups.Count);
                var shared = groups[0].ChannelGroups.ToList();
                Assert.Equal(2, shared.Count);
                Assert.Equal("fast", shared[0].Name);
                Assert.Equal("slow", shared[1].Name);
                Assert.Equal((ulong)Fast, shared[0].SampleCount);
                Assert.Equal((ulong)Slow, shared[1].SampleCount);

                int row = 0;
                foreach (MdfSampleBatch batch in reader.ReadSamples(shared[0], new[] { 0, 1 }, batchSize: 7))
                {
                    for (int i = 0; i < batch.SampleCount; i++, row++)
                    {
                        Assert.Equal(row * 0.25, batch.GetValue(i, 0).FloatingPointValue);
                        Assert.Equal((ulong)row, batch.GetValue(i, 1).UnsignedValue);
                        Assert.Equal(row % 5 != 4, batch.GetValue(i, 1).IsValid);
                    }
                }
                Assert.Equal(Fast, row);

                row = 0;
                foreach (MdfSampleBatch batch in reader.ReadSamples(shared[1], new[] { 0, 1 }))
                {
                    for (int i = 0; i < batch.SampleCount; i++, row++)
                    {
                        Assert.Equal((double)row, batch.GetValue(i, 0).FloatingPointValue);
                        Assert.Equal((ulong)(row + 10), batch.GetValue(i, 1).UnsignedValue);
                    }
                }
                Assert.Equal(Slow, row);

                if (ownDataGroup)
                {
                    var alone = groups[1].ChannelGroups.Single();
                    row = 0;
                    foreach (MdfSampleBatch batch in reader.ReadSamples(alone, new[] { 0, 1 }))
                    {
                        for (int i = 0; i < batch.SampleCount; i++, row++)
                        {
                            Assert.Equal((double)row, batch.GetValue(i, 0).FloatingPointValue);
                            Assert.Equal((ulong)(row % 2), batch.GetValue(i, 1).UnsignedValue);
                        }
                    }
                    Assert.Equal(Fast, row);
                }
            }
        }
    }
}
