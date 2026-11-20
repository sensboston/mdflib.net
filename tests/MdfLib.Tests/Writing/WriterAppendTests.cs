// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Writing
{
    public sealed class WriterAppendTests
    {
        private static MemoryStream Copy(byte[] file)
        {
            MemoryStream stream = new MemoryStream();
            stream.Write(file, 0, file.Length);
            return stream;
        }

        private static ulong Le64(byte[] bytes, int offset) { return BitConverter.ToUInt64(bytes, offset); }

        private static void AppendRamp(MemoryStream stream, string name, int count, MdfWriterOptions options = null, bool finalize = true)
        {
            using (MdfWriter writer = MdfWriter.Append(stream, true, options))
            {
                writer.SetChannelGroupName(0, name);
                writer.AddChannel(WriterMultiGroupTests.Master());
                writer.AddChannel(WriterConfigurationTests.Channel("level", 16));
                writer.StartMeasurement();
                for (int i = 0; i < count; i++)
                { writer.SaveSample(new[] { MdfSampleValue.FromFloatingPoint(100 + i), MdfSampleValue.FromUnsigned((ulong)(3 * i)) }); }
                if (!finalize) { return; }
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
                Assert.Equal(MdfWriterState.Finalized, writer.State);
            }
        }

        private static void AssertRamp(MdfReader reader, MdfChannelGroup group, string name, int count)
        {
            Assert.Equal(name, group.Name);
            Assert.Equal((ulong)count, group.SampleCount);
            int row = 0;
            foreach (MdfSampleBatch batch in reader.ReadSamples(group, new[] { 0, 1 }))
            {
                for (int i = 0; i < batch.SampleCount; i++, row++)
                {
                    Assert.Equal(100.0 + row, batch.GetValue(i, 0).FloatingPointValue);
                    Assert.Equal((ulong)(3 * row), batch.GetValue(i, 1).UnsignedValue);
                }
            }
            Assert.Equal(count, row);
        }

        [Theory]
        [InlineData("4.10", false)]
        [InlineData("4.20", true)]
        public void AppendedGroupFollowsExistingGroupsWhichStayIntact(string version, bool compress)
        {
            byte[] original = WriterMultiGroupTests.TwoGroups(version, compress, 20, 6);
            MemoryStream stream = Copy(original);
            AppendRamp(stream, "appended", 9, new MdfWriterOptions { Version = version, CompressData = compress, CompressionBlockBytes = 40 });
            byte[] file = stream.ToArray();
            Assert.Equal(original.Take(64).ToArray(), file.Take(64).ToArray());
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file)))
            {
                var groups = reader.ReadMetadata();
                Assert.Equal(3, groups.Count);
                Assert.Equal(20UL, groups[0].ChannelGroups.Single().SampleCount);
                Assert.Equal(6UL, groups[1].ChannelGroups.Single().SampleCount);
                AssertRamp(reader, groups[2].ChannelGroups.Single(), "appended", 9);
                Assert.True(MdfValidator.Validate(reader).IsValid);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AppendBeyondTwoGibibytesUsesSixtyFourBitAddresses(bool compress)
        {
            byte[] original = WriterMultiGroupTests.TwoGroups("4.10", false, 4, 3);
            long tail = (3L << 30) + 5; // An unaligned end beyond 3 GiB (e.g. an unreferenced attachment area).
            PagedStream stream = new PagedStream(original, tail);
            using (MdfWriter writer = MdfWriter.Append(stream, true, new MdfWriterOptions { CompressData = compress, CompressionBlockBytes = 40 }))
            {
                writer.SetChannelGroupName(0, "far");
                writer.AddChannel(WriterMultiGroupTests.Master());
                writer.AddChannel(WriterConfigurationTests.Channel("level", 16));
                writer.StartMeasurement();
                for (int i = 0; i < 25; i++) { writer.SaveSample(new[] { MdfSampleValue.FromFloatingPoint(100 + i), MdfSampleValue.FromUnsigned((ulong)(3 * i)) }); }
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
            }
            Assert.True(stream.Length > tail);
            Assert.True(stream.Pages < 16, stream.Pages.ToString());
            stream.Position = 0;
            using (MdfReader reader = MdfReader.Open(stream, true))
            {
                var groups = reader.ReadMetadata();
                Assert.Equal(3, groups.Count);
                Assert.Equal(4UL, groups[0].ChannelGroups.Single().SampleCount);
                AssertRamp(reader, groups[2].ChannelGroups.Single(), "far", 25);
            }
            byte[] head = new byte[64];
            stream.Position = 0;
            Assert.Equal(64, stream.Read(head, 0, 64));
            Assert.Equal("MDF     ", Encoding.ASCII.GetString(head, 0, 8));
        }

        [Fact]
        public void RepeatedAppendsChainAndUnalignedTailIsPadded()
        {
            MemoryStream stream = Copy(WriterMultiGroupTests.TwoGroups("4.10", false, 4, 2));
            stream.Write(new byte[] { 0xAA, 0xBB, 0xCC }, 0, 3);
            long tail = stream.Length;
            AppendRamp(stream, "second", 3);
            Assert.Equal(0, stream.ToArray()[tail]); // Zero padding to the next eight-byte boundary.
            AppendRamp(stream, "third", 5);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(stream.ToArray())))
            {
                var groups = reader.ReadMetadata();
                Assert.Equal(4, groups.Count);
                AssertRamp(reader, groups[2].ChannelGroups.Single(), "second", 3);
                AssertRamp(reader, groups[3].ChannelGroups.Single(), "third", 5);
            }
        }

        [Fact]
        public void InterruptedAppendLeavesTheOriginalFileReadable()
        {
            byte[] original = WriterMultiGroupTests.TwoGroups("4.10", false, 7, 3);
            MemoryStream stream = Copy(original);
            AppendRamp(stream, "lost", 4, finalize: false);
            byte[] file = stream.ToArray();
            Assert.True(file.Length > original.Length);
            Assert.Equal(original, file.Take(original.Length).ToArray());
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file)))
            { Assert.Equal(2, reader.ReadMetadata().Count); }
        }

        [Fact]
        public void EmptyAppendedGroupIsPublishedWithZeroSamples()
        {
            MemoryStream stream = Copy(WriterMultiGroupTests.TwoGroups("4.10", true, 3, 1));
            AppendRamp(stream, "none", 0, new MdfWriterOptions { CompressData = true });
            using (MdfReader reader = MdfReader.Open(new MemoryStream(stream.ToArray())))
            { AssertRamp(reader, reader.ReadMetadata()[2].ChannelGroups.Single(), "none", 0); }
        }

        [Fact]
        public void BusLoggerAppendsFramesToAnExistingMeasurement()
        {
            MemoryStream stream = Copy(WriterMultiGroupTests.TwoGroups("4.10", false, 5, 2));
            using (MdfBusLogger logger = MdfBusLogger.Append(stream, MdfBusType.Can, true))
            {
                logger.StartMeasurement();
                logger.Write(new MdfBusMessage { Type = MdfBusMessageType.CanDataFrame, Timestamp = 0.5, Id = 0x123, Data = new byte[] { 1, 2, 3 } });
                logger.Write(new MdfBusMessage { Type = MdfBusMessageType.CanDataFrame, Timestamp = 0.75, Id = 0x7FF, Data = new byte[] { 9 } });
                logger.StopMeasurement();
                logger.FinalizeMeasurement();
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(stream.ToArray())))
            {
                List<MdfBusMessage> messages = MdfBusReader.ReadMessages(reader).ToList();
                Assert.Equal(2, messages.Count);
                Assert.Equal(0x123U, messages[0].Id);
                Assert.Equal(new byte[] { 1, 2, 3 }, messages[0].Data);
                Assert.Equal(0.75, messages[1].Timestamp);
                Assert.Equal(5UL, reader.ReadMetadata()[0].ChannelGroups.Single().SampleCount);
            }
        }

        [Fact]
        public void AppendRefusesInvalidDestinations()
        {
            byte[] valid = WriterMultiGroupTests.TwoGroups("4.10", false, 2, 1);
            Assert.Throws<ArgumentNullException>(() => MdfWriter.Append(null));
            Assert.Throws<ArgumentException>(() => MdfWriter.Append(new TestWriteStream()));
            Assert.Throws<ArgumentException>(() => MdfWriter.Append(new MemoryStream(valid, false)));
            Assert.Throws<MdfFormatException>(() => MdfWriter.Append(new MemoryStream()));
            Assert.Throws<ArgumentException>(() => MdfWriter.Append(Copy(valid), true, new MdfWriterOptions { Version = "4.20" }));

            byte[] foreign = (byte[])valid.Clone();
            foreign[0] = (byte)'X';
            Assert.Throws<MdfFormatException>(() => MdfWriter.Append(Copy(foreign)));

            byte[] unfinished = (byte[])valid.Clone();
            Encoding.ASCII.GetBytes("UnFinMF ").CopyTo(unfinished, 0);
            Assert.Throws<NotSupportedException>(() => MdfWriter.Append(Copy(unfinished)));

            byte[] noHeader = (byte[])valid.Clone();
            noHeader[66] = (byte)'X';
            Assert.Throws<MdfFormatException>(() => MdfWriter.Append(Copy(noHeader)));

            byte[] cyclic = (byte[])valid.Clone();
            int first = (int)Le64(cyclic, 88), second = (int)Le64(cyclic, first + 24);
            BitConverter.GetBytes((ulong)first).CopyTo(cyclic, second + 24);
            Assert.Throws<MdfFormatException>(() => MdfWriter.Append(Copy(cyclic)));

            byte[] outside = (byte[])valid.Clone();
            BitConverter.GetBytes((ulong)outside.Length).CopyTo(outside, second + 24);
            Assert.Throws<MdfFormatException>(() => MdfWriter.Append(Copy(outside)));

            byte[] notDg = (byte[])valid.Clone();
            BitConverter.GetBytes(64UL).CopyTo(notDg, second + 24);
            Assert.Throws<MdfFormatException>(() => MdfWriter.Append(Copy(notDg)));

            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(output, true, new MdfWriterOptions { Version = "3.30" }))
            {
                writer.AddChannel(WriterMultiGroupTests.Master());
                writer.StartMeasurement();
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
            }
            Assert.Throws<NotSupportedException>(() => MdfWriter.Append(Copy(output.Snapshot())));
            Assert.Equal(valid, Copy(valid).ToArray()); // Refusals never write.
        }

        [Fact]
        public void AppendRefusesWriterOperationsBeforeConfiguration()
        {
            MemoryStream stream = Copy(WriterMultiGroupTests.TwoGroups("4.10", false, 2, 1));
            long length = stream.Length;
            using (MdfWriter writer = MdfWriter.Append(stream, true))
            {
                Assert.Equal("4.10", writer.Version);
                Assert.Throws<InvalidOperationException>(() => writer.StartMeasurement());
            }
            Assert.Equal(length, stream.Length);
            Assert.True(stream.CanRead); // leaveOpen keeps the caller's stream.
        }
    }
}
