// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace MdfLib.Tests.Writing
{
    public sealed class WriterExampleTests
    {
        [Theory]
        [InlineData("3.30")]
        [InlineData("4.10")]
        [InlineData("4.20")]
        public void ReadmeCreateNewStoredMasterAndExactCounterExampleWorksOnEveryWriterRevision(string version)
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + (version == "3.30" ? ".mdf" : ".mf4"));
            try
            {
                // Same API/row as the README example; only its path and exact version are test parameters.
                using (MdfWriter writer = MdfWriter.Create(path, new MdfWriterOptions { Version = version }))
                {
                    writer.AddChannel(new MdfChannelDefinition
                    {
                        Name = "time", Unit = "s", ValueKind = MdfValueKind.FloatingPoint,
                        BitCount = 64, IsMaster = true
                    });
                    writer.AddChannel(new MdfChannelDefinition
                    {
                        Name = "counter", ValueKind = MdfValueKind.UnsignedInteger, BitCount = 64
                    });
                    writer.StartMeasurement();
                    writer.SaveSample(new[]
                    {
                        MdfSampleValue.FromFloatingPoint(0.0),
                        MdfSampleValue.FromUnsigned(9007199254740993UL)
                    });
                    writer.StopMeasurement();
                    writer.FinalizeMeasurement();
                }
                using (Stream input = File.OpenRead(path))
                using (MdfReader reader = MdfReader.Open(input, leaveOpen: true))
                {
                    Assert.Equal(version, reader.Header.Version);
                    Assert.Equal(0UL, reader.Header.RawStartTimeNanoseconds); // No implicit wall clock.
                    MdfChannelGroup group = Assert.Single(Assert.Single(reader.ReadMetadata()).ChannelGroups);
                    Assert.Equal(1UL, group.SampleCount);
                    Assert.True(group.Channels[0].IsMaster); Assert.Equal("s", group.Channels[0].Unit);
                    Assert.False(group.Channels[1].IsMaster);
                    MdfSampleBatch batch = reader.ReadSamples(group, new[] { 0, 1 }).Single();
                    Assert.Equal(0.0, batch.GetValue(0, 0).FloatingPointValue);
                    Assert.Equal(MdfValueKind.UnsignedInteger, batch.GetValue(0, 1).Kind);
                    Assert.Equal(9007199254740993UL, batch.GetValue(0, 1).UnsignedValue);
                    Assert.True(batch.GetValue(0, 1).IsValid);
                }
                byte[] before = File.ReadAllBytes(path);
                Assert.Throws<IOException>(() => MdfWriter.Create(path));
                Assert.Equal(before, File.ReadAllBytes(path));
            }
            finally { if (File.Exists(path)) { File.Delete(path); } }
        }
    }
}
