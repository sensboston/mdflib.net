// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Linq;
using MdfLib.Tests.TestDoubles;
using MdfLib.Tests.Writing;
using Xunit;

namespace MdfLib.Tests.Reading
{
    // ASAM bus-event classification flags carry no layout semantics and must not disable a group or channel.
    public sealed class BusEventFlagTests
    {
        private const int Cg = 64 + 104 + 64; // ID, HD, DG; then the CG and the first CN of a one-group writer file.
        private const int Cn = Cg + 104;

        private static byte[] File()
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(output, true))
            {
                writer.AddChannel(WriterMultiGroupTests.Master());
                writer.AddChannel(WriterConfigurationTests.Channel("v", 16));
                writer.StartMeasurement();
                for (int i = 0; i < 3; i++) { writer.SaveSample(new[] { MdfSampleValue.FromFloatingPoint(i), MdfSampleValue.FromUnsigned((ulong)(10 + i)) }); }
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
            }
            byte[] bytes = output.Snapshot();
            Assert.Equal("##CG", System.Text.Encoding.ASCII.GetString(bytes, Cg, 4));
            Assert.Equal("##CN", System.Text.Encoding.ASCII.GetString(bytes, Cn, 4));
            return bytes;
        }

        [Theory]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(6)]
        public void BusEventGroupFlagsKeepTheGroupReadable(int flags)
        {
            byte[] bytes = File();
            BitConverter.GetBytes((ushort)flags).CopyTo(bytes, Cg + 88);
            BitConverter.GetBytes((ushort)'.').CopyTo(bytes, Cg + 90);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes)))
            {
                MdfChannelGroup group = reader.ReadMetadata().Single().ChannelGroups.Single();
                Assert.True(group.CanReadSamples, group.UnsupportedReason);
                Assert.Equal((ushort)flags, group.Flags);
                Assert.Equal('.', group.PathSeparator);
                Assert.Equal(3, reader.ReadSamples(group, new[] { 1 }).Sum(b => b.SampleCount));
            }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(16)]
        [InlineData(18)]
        public void OtherGroupFlagsStayUnsupported(int flags)
        {
            byte[] bytes = File();
            BitConverter.GetBytes((ushort)flags).CopyTo(bytes, Cg + 88);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes)))
            {
                MdfChannelGroup group = reader.ReadMetadata().Single().ChannelGroups.Single();
                Assert.False(group.CanReadSamples);
                Assert.Equal((ushort)flags, group.Flags);
            }
        }

        [Fact]
        public void PlainWriterGroupsReportNoFlags()
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(File())))
            {
                MdfChannelGroup group = reader.ReadMetadata().Single().ChannelGroups.Single();
                Assert.Equal((ushort)0, group.Flags);
                Assert.Equal('\0', group.PathSeparator);
            }
        }

        [Theory]
        [InlineData(0x400U, true)]  // bus event
        [InlineData(0x402U, true)]  // bus event + invalidation bit valid
        [InlineData(0x200U, false)] // virtual
        [InlineData(0x2000U, false)]
        public void OnlyTheBusEventChannelFlagIsLayoutNeutral(uint flags, bool readable)
        {
            byte[] bytes = File();
            BitConverter.GetBytes(flags).CopyTo(bytes, Cn + 100);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes)))
            {
                MdfChannel master = reader.ReadMetadata().Single().ChannelGroups.Single().Channels[0];
                Assert.Equal(readable, master.CanReadRaw);
            }
        }
    }
}
