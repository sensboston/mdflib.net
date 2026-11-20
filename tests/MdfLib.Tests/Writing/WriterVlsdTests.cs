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
    // variable-length (VLSD) text and byte-array channels written as VLSD channel groups.
    public sealed class WriterVlsdTests
    {
        private static string Text(int row) { return row % 5 == 3 ? string.Empty : "row " + row + " \u00fc" + new string('x', (row * 37) % 300); }
        private static byte[] Bytes(int row) { return Enumerable.Range(0, (row * 11) % 70).Select(i => (byte)(i ^ row)).ToArray(); }

        private static MdfChannelDefinition VlsdText() { return new MdfChannelDefinition { Name = "message", ValueKind = MdfValueKind.Text, BitCount = 64, IsVariableLength = true }; }
        private static MdfChannelDefinition VlsdBytes() { return new MdfChannelDefinition { Name = "payload", ValueKind = MdfValueKind.ByteArray, BitCount = 64, IsVariableLength = true }; }

        private static void Configure(MdfWriter writer, int group)
        {
            writer.AddChannel(group, WriterMultiGroupTests.Master());
            writer.AddChannel(group, VlsdText());
            writer.AddChannel(group, WriterConfigurationTests.Channel("counter", 16));
            writer.AddChannel(group, VlsdBytes());
        }

        private static MdfSampleValue[] Row(int row)
        {
            return new[]
            {
                MdfSampleValue.FromFloatingPoint(row * 0.1), MdfSampleValue.FromText(Text(row), row % 7 != 6),
                MdfSampleValue.FromUnsigned((ulong)row), MdfSampleValue.FromByteArray(Bytes(row))
            };
        }

        private static byte[] Write(string version, int rows, bool compress = false, bool second = false, int block = 256, int buffer = 0, bool finalize = true)
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(output, true, new MdfWriterOptions
            { Version = version, CompressData = compress, CompressionBlockBytes = block, WriteBufferBytes = buffer }))
            {
                Configure(writer, 0);
                int other = second ? writer.AddChannelGroup("other") : -1;
                if (second) { writer.AddChannel(other, WriterConfigurationTests.Channel("v", 8)); }
                writer.StartMeasurement();
                for (int r = 0; r < rows; r++)
                {
                    writer.SaveSample(Row(r));
                    if (second && r % 2 == 0) { writer.SaveSample(other, new[] { MdfSampleValue.FromUnsigned((ulong)(r & 0xFF)) }); }
                }
                if (finalize)
                {
                    writer.StopMeasurement();
                    writer.FinalizeMeasurement();
                }
            }
            return output.Snapshot();
        }

        internal static void AssertRows(MdfReader reader, MdfChannelGroup group, int rows)
        {
            Assert.Equal((ulong)rows, group.SampleCount);
            Assert.Equal(new[] { "t", "message", "counter", "payload" }, group.Channels.Select(c => c.Name));
            Assert.All(group.Channels, c => Assert.True(c.CanReadRaw, c.Name + ": " + c.UnsupportedReason));
            int row = 0;
            foreach (MdfSampleBatch batch in reader.ReadSamples(group, new[] { 0, 1, 2, 3 }, batchSize: 7))
            {
                for (int i = 0; i < batch.SampleCount; i++, row++)
                {
                    Assert.Equal(row * 0.1, batch.GetValue(i, 0).FloatingPointValue);
                    MdfSampleValue text = batch.GetValue(i, 1);
                    Assert.Equal(row % 7 != 6, text.IsValid);
                    if (text.IsValid) { Assert.Equal(Text(row), text.TextValue); }
                    else { Assert.Empty(text.GetByteArrayValue()); } // Invalid offsets are never followed.
                    Assert.Equal((ulong)row, batch.GetValue(i, 2).UnsignedValue);
                    Assert.Equal(Bytes(row), batch.GetValue(i, 3).GetByteArrayValue());
                }
            }
            Assert.Equal(rows, row);
        }

        private static void AssertFile(byte[] file, int rows, bool second = false)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file)))
            {
                Assert.True(MdfValidator.Validate(reader).IsValid);
                IReadOnlyList<MdfDataGroup> groups = reader.ReadMetadata();
                Assert.Equal(second ? 2 : 1, groups.Count);
                Assert.Equal(3, groups[0].ChannelGroups.Count); // The fixed CG and two VLSD CGs.
                AssertRows(reader, groups[0].ChannelGroups[0], rows);
                Assert.False(groups[0].ChannelGroups[1].CanReadSamples);
                if (second) { Assert.Equal((ulong)((rows + 1) / 2), groups[1].ChannelGroups.Single().SampleCount); }
            }
        }

        [Theory]
        [InlineData("4.10", 0, false, false, 256, 0)]
        [InlineData("4.10", 1, false, false, 256, 0)]
        [InlineData("4.10", 40, false, false, 256, 0)]
        [InlineData("4.20", 40, false, false, 256, 100)]
        [InlineData("4.10", 40, true, false, 64, 0)] // Most rows exceed the block: one DZ each, DL with offsets.
        [InlineData("4.20", 40, true, false, 100000, 0)]
        [InlineData("4.10", 40, false, true, 64, 0)] // Per-group DT blocks.
        [InlineData("4.20", 40, true, true, 128, 0)]
        public void VariableLengthValuesRoundTrip(string version, int rows, bool compress, bool second, int block, int buffer)
        {
            AssertFile(Write(version, rows, compress, second, block, buffer), rows, second);
        }

        [Fact]
        public void BufferingNeverChangesTheOutput()
        {
            Assert.Equal(Write("4.10", 25), Write("4.10", 25, buffer: 64));
        }

        [Fact]
        public void WireLayoutIsAnUnsortedGroupWithVlsdChannelGroups()
        {
            byte[] file = Write("4.10", 3);
            long dg = (long)BitConverter.ToUInt64(file, 64 + 24);
            Assert.Equal(1, file[dg + 56]); // dg_rec_id_size.
            long cg = (long)BitConverter.ToUInt64(file, (int)dg + 24 + 8);
            Assert.Equal(1UL, BitConverter.ToUInt64(file, (int)cg + 72));
            long[] vlsd = new long[2];
            vlsd[0] = (long)BitConverter.ToUInt64(file, (int)cg + 24);
            vlsd[1] = (long)BitConverter.ToUInt64(file, (int)vlsd[0] + 24);
            Assert.Equal(0UL, BitConverter.ToUInt64(file, (int)vlsd[1] + 24));
            ulong textBytes = (ulong)Enumerable.Range(0, 3).Sum(r => 4 + Encoding.UTF8.GetByteCount(Text(r)));
            ulong byteBytes = (ulong)Enumerable.Range(0, 3).Sum(r => 4 + Bytes(r).Length);
            ulong[] totals = { textBytes, byteBytes };
            for (int k = 0; k < 2; k++)
            {
                int at = (int)vlsd[k];
                Assert.Equal((ulong)(k + 2), BitConverter.ToUInt64(file, at + 72));
                Assert.Equal(3UL, BitConverter.ToUInt64(file, at + 80));
                Assert.Equal(1, BitConverter.ToUInt16(file, at + 88));
                Assert.Equal(totals[k], BitConverter.ToUInt64(file, at + 96));
            }
            long cn = (long)BitConverter.ToUInt64(file, (int)cg + 32); // First channel: the master.
            cn = (long)BitConverter.ToUInt64(file, (int)cn + 24); // message.
            Assert.Equal(1, file[cn + 88]); // cn_type VLSD.
            Assert.Equal(vlsd[0], (long)BitConverter.ToUInt64(file, (int)cn + 24 + 40)); // cn_data -> VLSD CG.
        }

        [Fact]
        public void InvalidDefinitionsAndValuesAreRefused()
        {
            using (MdfWriter writer = MdfWriter.Create(new TestWriteStream(), true, new MdfWriterOptions { Version = "3.30" }))
            {
                Assert.Throws<NotSupportedException>(() => writer.AddChannel(VlsdText()));
            }
            using (MdfWriter writer = MdfWriter.Create(new TestWriteStream(), true, new MdfWriterOptions { MaximumTextBytes = 16 }))
            {
                Assert.Throws<NotSupportedException>(() => writer.AddChannel(new MdfChannelDefinition { Name = "n", ValueKind = MdfValueKind.UnsignedInteger, BitCount = 8, IsVariableLength = true }));
                MdfChannelDefinition narrow = VlsdText();
                narrow.BitCount = 32;
                Assert.Throws<ArgumentOutOfRangeException>(() => writer.AddChannel(narrow));
                MdfChannelDefinition big = VlsdBytes();
                big.IsBigEndian = true;
                Assert.Throws<ArgumentException>(() => writer.AddChannel(big));
                writer.AddChannel(VlsdText());
                writer.StartMeasurement();
                Assert.Equal(nameof(MdfWriterOptions.MaximumTextBytes),
                    Assert.Throws<MdfResourceLimitException>(() => writer.SaveSample(new[] { MdfSampleValue.FromText(new string('a', 17)) })).LimitName);
                Assert.Throws<ArgumentException>(() => writer.SaveSample(new[] { MdfSampleValue.FromText("a\0b") }));
                Assert.Throws<ArgumentException>(() => writer.SaveSample(new[] { MdfSampleValue.FromByteArray(new byte[1]) }));
                writer.SaveSample(new[] { MdfSampleValue.FromText(new string('a', 16)) }); // Refused rows left no trace.
                Assert.Equal(1UL, writer.GetSampleCount(0));
            }
        }

        [Theory]
        [InlineData(false, 0)]
        [InlineData(false, 13)]
        [InlineData(true, 13)]
        public void UnfinalizedOutputIsRecovered(bool compress, int rows)
        {
            byte[] bytes = Write("4.10", rows, compress, block: 64, finalize: false);
            Assert.Equal("UnFinMF ", Encoding.ASCII.GetString(bytes, 0, 8));
            Assert.Equal(0x20, BitConverter.ToUInt16(bytes, 60) & 0x20);
            using (MemoryStream stream = new MemoryStream())
            {
                stream.Write(bytes, 0, bytes.Length);
                MdfRecovery.FinalizeFile(stream);
                bytes = stream.ToArray();
            }
            Assert.Equal("MDF     ", Encoding.ASCII.GetString(bytes, 0, 8));
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                // Compressed output keeps only completed blocks; a plain DT keeps every complete row.
                int recovered = (int)group.SampleCount;
                Assert.True(compress ? recovered <= rows : recovered == rows, recovered.ToString());
                AssertRows(reader, group, recovered);
            }
        }

        [Fact]
        public void AppendAddsVlsdGroups()
        {
            byte[] original = WriterMultiGroupTests.TwoGroups("4.10", false, 6, 2);
            MemoryStream file = new MemoryStream();
            file.Write(original, 0, original.Length);
            using (MdfWriter writer = MdfWriter.Append(file, true))
            {
                Configure(writer, 0);
                writer.StartMeasurement();
                for (int r = 0; r < 9; r++) { writer.SaveSample(Row(r)); }
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file.ToArray())))
            {
                IReadOnlyList<MdfDataGroup> groups = reader.ReadMetadata();
                Assert.Equal(3, groups.Count);
                AssertRows(reader, groups[2].ChannelGroups[0], 9);
            }
        }

        [Fact]
        public void CopierKeepsVlsdChannelsForMdf4AndListsThemForMdf3()
        {
            byte[] source = Write("4.20", 12, compress: true, block: 80);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(source)))
            using (MemoryStream copy = new MemoryStream())
            {
                MdfCopyResult result = MdfFileCopier.Copy(reader, copy, new MdfCopyOptions());
                Assert.Empty(result.Skipped);
                AssertFile(copy.ToArray(), 12);
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(source)))
            using (MemoryStream copy = new MemoryStream())
            {
                MdfCopyResult result = MdfFileCopier.Copy(reader, copy, new MdfCopyOptions { WriterOptions = new MdfWriterOptions { Version = "3.30" } });
                Assert.Equal(2, result.Skipped.Count(line => line.EndsWith("variable-length channels need MDF 4 output", StringComparison.Ordinal)));
            }
        }
    }
}
