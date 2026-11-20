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
    // MDF 3.30 output with several channel groups in one data group, told apart by a UINT8 record ID.
    public sealed class Mdf3MultiGroupWriterTests
    {
        // Group 0: t (f64 master) + speed (u16); group 1: t + temp (i8, linear); group 2: name (text 4) + flag (u1).
        private static byte[] Write(int rows, bool finalize = true, int bufferBytes = 0)
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(output, true, new MdfWriterOptions { Version = "3.30", WriteBufferBytes = bufferBytes }))
            {
                writer.AddChannel(WriterMultiGroupTests.Master());
                writer.AddChannel(WriterConfigurationTests.Channel("speed", 16));
                int slow = writer.AddChannelGroup();
                writer.AddChannel(slow, WriterMultiGroupTests.Master());
                writer.AddChannel(slow, new MdfChannelDefinition { Name = "temp", Unit = "degC", BitCount = 8, ValueKind = MdfValueKind.SignedInteger, HasLinearConversion = true, Factor = 0.5, Offset = 40 });
                int text = writer.AddChannelGroup();
                writer.AddChannel(text, new MdfChannelDefinition { Name = "name", ValueKind = MdfValueKind.Text, BitCount = 32 });
                writer.AddChannel(text, WriterConfigurationTests.Channel("flag", 1));
                Assert.Equal(3, writer.ChannelGroupCount);
                writer.StartMeasurement();
                for (int i = 0; i < rows; i++)
                {
                    writer.SaveSample(new[] { MdfSampleValue.FromFloatingPoint(i * 0.25), MdfSampleValue.FromUnsigned((ulong)(1000 + i)) });
                    if (i % 3 == 0) { writer.SaveSample(slow, new[] { MdfSampleValue.FromFloatingPoint(i * 0.25), MdfSampleValue.FromSigned(i / 3 - 5) }); }
                    if (i % 4 == 1) { writer.SaveSample(text, new[] { MdfSampleValue.FromText("n" + i), MdfSampleValue.FromUnsigned((ulong)(i & 1)) }); }
                }
                if (!finalize) { return output.Snapshot(); }
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
            }
            return output.Snapshot();
        }

        private static int Count(int rows, int modulo, int remainder) { return Enumerable.Range(0, rows).Count(i => i % modulo == remainder); }

        private static void AssertGroups(byte[] file, int rows)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file)))
            {
                Assert.True(MdfValidator.Validate(reader).IsValid);
                MdfDataGroup dataGroup = reader.ReadMetadata().Single();
                Assert.Equal(3, dataGroup.ChannelGroups.Count);
                MdfChannelGroup fast = dataGroup.ChannelGroups[0], slow = dataGroup.ChannelGroups[1], text = dataGroup.ChannelGroups[2];
                Assert.Equal((ulong)rows, fast.SampleCount);
                Assert.Equal((ulong)Count(rows, 3, 0), slow.SampleCount);
                Assert.Equal((ulong)Count(rows, 4, 1), text.SampleCount);
                Assert.Equal(new[] { "t", "temp" }, slow.Channels.Select(c => c.Name));
                Assert.Equal(MdfConversionKind.Linear, slow.Channels[1].Conversion.Kind);
                int row = 0;
                foreach (MdfSampleBatch batch in reader.ReadSamples(fast, new[] { 0, 1 }))
                {
                    for (int i = 0; i < batch.SampleCount; i++, row++)
                    {
                        Assert.Equal(row * 0.25, batch.GetValue(i, 0).FloatingPointValue);
                        Assert.Equal((ulong)(1000 + row), batch.GetValue(i, 1).UnsignedValue);
                    }
                }
                Assert.Equal(rows, row);
                List<double> temps = reader.ReadSamples(slow, new[] { 1 }, representation: MdfValueRepresentation.Physical)
                    .SelectMany(b => Enumerable.Range(0, b.SampleCount).Select(i => b.GetValue(i, 0).FloatingPointValue)).ToList();
                Assert.Equal(Enumerable.Range(0, Count(rows, 3, 0)).Select(k => (k - 5) * 0.5 + 40), temps);
                List<string> names = reader.ReadSamples(text, new[] { 0, 1 })
                    .SelectMany(b => Enumerable.Range(0, b.SampleCount).Select(i => b.GetValue(i, 0).TextValue + "/" + b.GetValue(i, 1).UnsignedValue)).ToList();
                Assert.Equal(Enumerable.Range(0, rows).Where(i => i % 4 == 1).Select(i => "n" + i + "/" + (i & 1)), names);
            }
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 0)]
        [InlineData(37, 0)]
        [InlineData(37, 64)]
        public void GroupsShareOneDataGroupWithRecordIds(int rows, int bufferBytes)
        {
            byte[] file = Write(rows, bufferBytes: bufferBytes);
            AssertGroups(file, rows);
            Assert.Equal(Write(rows), file); // Buffering never changes the output.
        }

        [Fact]
        public void WireLayoutUsesOnePrefixRecordIdByte()
        {
            byte[] file = Write(2);
            long dg = BitConverter.ToUInt32(file, 68);
            Assert.Equal(3, BitConverter.ToUInt16(file, (int)dg + 20)); // Channel groups.
            Assert.Equal(1, BitConverter.ToUInt16(file, (int)dg + 22)); // Record IDs.
            long cg = BitConverter.ToUInt32(file, (int)dg + 8);
            for (int g = 1; g <= 3; g++)
            {
                Assert.Equal(g, BitConverter.ToUInt16(file, (int)cg + 16));
                cg = BitConverter.ToUInt32(file, (int)cg + 4);
            }
            Assert.Equal(0, cg);
            long data = BitConverter.ToUInt32(file, (int)dg + 16);
            // Row 0: fast (ID 1, 10 bytes), slow (ID 2, 9 bytes); row 1: fast, text (ID 3, 5 bytes).
            Assert.Equal(new byte[] { 1, 2, 1, 3 }, new[] { data, data + 11, data + 21, data + 32 }.Select(at => file[at]));
            Assert.Equal(data + 11 + 10 + 11 + 6, file.Length);
        }

        [Fact]
        public void SingleGroupOutputIsUnchanged()
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(output, true, new MdfWriterOptions { Version = "3.30" }))
            {
                writer.AddChannel(WriterConfigurationTests.Channel("x", 8));
                writer.StartMeasurement();
                writer.SaveSample(new[] { MdfSampleValue.FromUnsigned(7) });
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
            }
            byte[] file = output.Snapshot();
            long dg = BitConverter.ToUInt32(file, 68);
            Assert.Equal(0, BitConverter.ToUInt16(file, (int)dg + 22));
            Assert.Equal(7, file[file.Length - 1]);
            Assert.Equal(BitConverter.ToUInt32(file, (int)dg + 16) + 1, (uint)file.Length);
        }

        [Fact]
        public void GroupLimitAndNamesAreEnforced()
        {
            using (MdfWriter writer = MdfWriter.Create(new TestWriteStream(), true, new MdfWriterOptions { Version = "3.30" }))
            {
                for (int g = 1; g < 255; g++) { Assert.Equal(g, writer.AddChannelGroup()); }
                Assert.Throws<NotSupportedException>(() => writer.AddChannelGroup());
                Assert.Throws<NotSupportedException>(() => writer.AddChannelGroup("named"));
                Assert.Equal(255, writer.ChannelGroupCount);
            }
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(10, 0)]
        [InlineData(10, 3)]
        [InlineData(10, 11)]
        public void UnfinalizedOutputIsRecoveredUpToTheLastCompleteRecord(int rows, int tornBytes)
        {
            byte[] bytes = Write(rows, finalize: false);
            Assert.Equal("UnFinMF ", Encoding.ASCII.GetString(bytes, 0, 8));
            if (tornBytes > 0)
            {
                byte[] torn = new byte[bytes.Length + tornBytes];
                Array.Copy(bytes, torn, bytes.Length);
                torn[bytes.Length] = tornBytes < 11 ? (byte)1 : (byte)0; // A fast record cut short, or an unknown ID 0.
                bytes = torn;
            }
            MdfRecoveryResult result;
            using (MemoryStream stream = new MemoryStream())
            {
                stream.Write(bytes, 0, bytes.Length);
                result = MdfRecovery.FinalizeFile(stream);
                bytes = stream.ToArray();
            }
            Assert.Equal("MDF     ", Encoding.ASCII.GetString(bytes, 0, 8));
            Assert.Equal(rows == 0 ? 0 : 3, result.Actions.Count(a => a.Contains("cycle count set to")));
            AssertGroups(bytes, rows); // The torn tail stays in the file, outside every channel group.
        }

        [Fact]
        public void Mdf3CopiesOfMultiGroupSourcesKeepEveryGroup()
        {
            byte[] source = Write(20);
            MdfCopyResult result;
            using (MdfReader reader = MdfReader.Open(new MemoryStream(source)))
            using (MemoryStream copy = new MemoryStream())
            {
                result = MdfFileCopier.Copy(reader, copy, new MdfCopyOptions { WriterOptions = new MdfWriterOptions { Version = "3.30" } });
                Assert.Equal(3, result.ChannelGroupCount);
                Assert.Empty(result.Skipped);
                AssertGroups(copy.ToArray(), 20);
            }
        }
    }
}
