// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MdfLib.TestSupport;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Writing
{
    public sealed class RecoveryTests
    {
        // Rows: t = 0.5 * i (master), v = 100 + i (u16). The writer is disposed without finalization.
        private static byte[] Unfinalized(string version, int rows, bool stop = false, bool compress = false, bool twoGroups = false)
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(output, true, new MdfWriterOptions { Version = version, CompressData = compress, CompressionBlockBytes = 32 }))
            {
                writer.AddChannel(WriterMultiGroupTests.Master());
                writer.AddChannel(WriterConfigurationTests.Channel("v", 16));
                if (twoGroups) { writer.AddChannel(writer.AddChannelGroup(), WriterConfigurationTests.Channel("w")); }
                writer.StartMeasurement();
                for (int i = 0; i < rows; i++) { writer.SaveSample(new[] { MdfSampleValue.FromFloatingPoint(i * 0.5), MdfSampleValue.FromUnsigned((ulong)(100 + i)) }); }
                if (stop) { writer.StopMeasurement(); }
            }
            byte[] bytes = output.Snapshot();
            Assert.Equal("UnFinMF ", Encoding.ASCII.GetString(bytes, 0, 8));
            return bytes;
        }

        private static MdfRecoveryResult Recover(ref byte[] bytes)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                stream.Write(bytes, 0, bytes.Length);
                MdfRecoveryResult result = MdfRecovery.FinalizeFile(stream);
                bytes = stream.ToArray();
                return result;
            }
        }

        private static void AssertRows(byte[] bytes, int rows)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes)))
            {
                MdfChannelGroup group = reader.ReadMetadata().Single().ChannelGroups.Single();
                Assert.Equal((ulong)rows, group.SampleCount);
                int row = 0;
                foreach (MdfSampleBatch batch in reader.ReadSamples(group, new[] { 0, 1 }))
                {
                    for (int i = 0; i < batch.SampleCount; i++, row++)
                    {
                        Assert.Equal(row * 0.5, batch.GetValue(i, 0).FloatingPointValue);
                        Assert.Equal((ulong)(100 + row), batch.GetValue(i, 1).UnsignedValue);
                    }
                }
                Assert.Equal(rows, row);
                Assert.True(MdfValidator.Validate(reader).IsValid);
            }
        }

        [Theory]
        [InlineData("4.10", 7, false)]
        [InlineData("4.20", 7, true)]
        [InlineData("3.30", 7, false)]
        [InlineData("4.10", 0, true)]
        [InlineData("3.30", 0, false)]
        [InlineData("4.10", 1000, false)]
        public void UnfinalizedWriterOutputBecomesAReadableFinalizedFile(string version, int rows, bool stop)
        {
            byte[] bytes = Unfinalized(version, rows, stop);
            Assert.Equal(version == "3.30" ? 1 : 5, bytes[60]);
            Assert.Throws<NotSupportedException>(() => MdfReader.Open(new MemoryStream(bytes)));
            MdfRecoveryResult result = Recover(ref bytes);
            Assert.True(result.Changed);
            Assert.Equal("ID: identification set to \"MDF     \"", result.Actions[result.Actions.Count - 2]);
            Assert.Contains(result.Actions, action => action.EndsWith("cycle count set to " + rows, StringComparison.Ordinal) || (rows == 0 && version == "3.30"));
            Assert.Equal("MDF     ", Encoding.ASCII.GetString(bytes, 0, 8));
            Assert.Equal(0, bytes[60] | bytes[61] | bytes[62] | bytes[63]);
            AssertRows(bytes, rows);
            byte[] again = (byte[])bytes.Clone();
            MdfRecoveryResult second = Recover(ref again);
            Assert.False(second.Changed);
            Assert.Empty(second.Actions);
            Assert.Equal(bytes, again);
        }

        [Theory]
        [InlineData("4.10")]
        [InlineData("3.30")]
        public void TrailingPartialRecordIsLeftOutsideTheData(string version)
        {
            byte[] bytes = Unfinalized(version, 5);
            Array.Resize(ref bytes, bytes.Length + 7); // A torn sixth record (11 bytes in MDF 4, 10 in MDF 3).
            Recover(ref bytes);
            AssertRows(bytes, 5);
        }

        [Fact]
        public void RecoveredFileMatchesTheNormallyFinalizedRecordArea()
        {
            byte[] recovered = Unfinalized("4.10", 9, true);
            Recover(ref recovered);
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(output, true, new MdfWriterOptions { CompressionBlockBytes = 32 }))
            {
                writer.AddChannel(WriterMultiGroupTests.Master());
                writer.AddChannel(WriterConfigurationTests.Channel("v", 16));
                writer.StartMeasurement();
                for (int i = 0; i < 9; i++) { writer.SaveSample(new[] { MdfSampleValue.FromFloatingPoint(i * 0.5), MdfSampleValue.FromUnsigned((ulong)(100 + i)) }); }
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
            }
            byte[] finalized = output.Snapshot();
            Assert.Equal(finalized.Take(recovered.Length), recovered); // Same bytes; the finalized file only adds alignment padding.
        }

        [Theory]
        [InlineData(true, false, 21)]
        [InlineData(false, true, 21)]
        [InlineData(true, true, 7)]
        public void BlockedWriterOutputKeepsEveryCompletedBlock(bool compress, bool twoGroups, int rows)
        {
            byte[] bytes = Unfinalized("4.10", rows, false, compress, twoGroups);
            Assert.Equal(0x11, bytes[60]); // Cycle counters and the last (preallocated) DL.
            Assert.Equal(0, bytes[62]);
            MdfRecoveryResult result = Recover(ref bytes);
            Assert.Contains(result.Actions, a => a.Contains("compacted to"));
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes)))
            {
                IReadOnlyList<MdfDataGroup> groups = reader.ReadMetadata();
                Assert.Equal(twoGroups ? 2 : 1, groups.Count);
                MdfChannelGroup group = groups[0].ChannelGroups.Single();
                int perBlock = Math.Max(1, 32 / (int)group.RecordByteCount);
                int expected = rows - rows % perBlock; // The pending partial block was never written.
                Assert.Equal((ulong)expected, group.SampleCount);
                int row = 0;
                foreach (MdfSampleBatch batch in reader.ReadSamples(group, new[] { 0, 1 }))
                {
                    for (int i = 0; i < batch.SampleCount; i++, row++) { Assert.Equal((ulong)(100 + row), batch.GetValue(i, 1).UnsignedValue); }
                }
                Assert.Equal(expected, row);
                if (twoGroups) { Assert.Equal(0UL, groups[1].ChannelGroups.Single().SampleCount); }
                Assert.True(MdfValidator.Validate(reader).IsValid);
            }
        }

        [Fact]
        public void LongBlockChainsAreRecoveredThroughSeveralDataLists()
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(output, true, new MdfWriterOptions { CompressData = true, CompressionBlockBytes = 1 }))
            {
                writer.AddChannel(WriterMultiGroupTests.Master());
                writer.AddChannel(WriterConfigurationTests.Channel("v", 16));
                writer.StartMeasurement();
                for (int i = 0; i < 1500; i++) { writer.SaveSample(new[] { MdfSampleValue.FromFloatingPoint(i * 0.5), MdfSampleValue.FromUnsigned((ulong)(100 + i)) }); }
            }
            byte[] bytes = output.Snapshot();
            Recover(ref bytes);
            AssertRows(bytes, 1500);
        }

        [Fact]
        public void LinkPublishedBeforeItsCountIsStillRecovered()
        {
            byte[] bytes = Unfinalized("4.10", 8, false, true);
            int dl = IndexOf(bytes, "##DL");
            int countOffset = dl + 32 + 8 * 1024 + 4;
            uint count = BitConverter.ToUInt32(bytes, countOffset);
            Assert.True(count > 1);
            BitConverter.GetBytes(count - 1).CopyTo(bytes, countOffset); // Crash between the link and dl_count stores.
            Recover(ref bytes);
            int record;
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes))) { record = (int)reader.ReadMetadata()[0].ChannelGroups[0].RecordByteCount; }
            int perBlock = Math.Max(1, 32 / record);
            Assert.Equal(8 / perBlock, (int)count); // Every block is full: the last link (count) is recovered despite the stale count.
            AssertRows(bytes, (int)count * perBlock);
            byte[] broken = Unfinalized("4.10", 8, false, true);
            BitConverter.GetBytes(count + 1).CopyTo(broken, countOffset); // dl_count beyond the used links.
            byte[] copy = (byte[])broken.Clone();
            Assert.Equal("MDF006", Assert.Throws<MdfFormatException>(() => Recover(ref copy)).DiagnosticCode);
            Assert.Equal(broken, copy);
        }

        private static int IndexOf(byte[] bytes, string text)
        {
            byte[] pattern = Encoding.ASCII.GetBytes(text);
            for (int i = 0; i + pattern.Length <= bytes.Length; i++)
            {
                int k = 0;
                while (k < pattern.Length && bytes[i + k] == pattern[k]) { k++; }
                if (k == pattern.Length) { return i; }
            }
            return -1;
        }

        // Turns a finalized MDF 4 fixture into an interrupted one: UnFinMF, cycle-counter flag and zero CG cycle counts.
        private static byte[] Unfinalize(byte[] file, ushort flags, bool vlsdBytes = false)
        {
            byte[] bytes = (byte[])file.Clone();
            Encoding.ASCII.GetBytes("UnFinMF ").CopyTo(bytes, 0);
            BitConverter.GetBytes(flags).CopyTo(bytes, 60);
            long dg = BitConverter.ToInt64(bytes, 88);
            while (dg != 0)
            {
                long cg = BitConverter.ToInt64(bytes, (int)dg + 32);
                while (cg != 0)
                {
                    int data = (int)cg + 24 + 8 * (int)BitConverter.ToInt64(bytes, (int)cg + 16);
                    new byte[8].CopyTo(bytes, data + 8);
                    if (vlsdBytes && (bytes[data + 16] & 1) != 0) { new byte[8].CopyTo(bytes, data + 24); }
                    cg = BitConverter.ToInt64(bytes, (int)cg + 24);
                }
                dg = BitConverter.ToInt64(bytes, (int)dg + 24);
            }
            return bytes;
        }

        [Theory]
        [InlineData("m5b-id1-dt410")]
        [InlineData("m5b-id2-dz411")]
        [InlineData("m5b-id4-dl410")]
        [InlineData("m5b-id8-hl411")]
        [InlineData("m5j-id1-dt410")]
        [InlineData("m5j-id2-dl411")]
        [InlineData("m5j-id4-hl420")]
        [InlineData("m5j-shared-id8-dz410")]
        [InlineData("m5-dz410")]
        [InlineData("m5-transpose411")]
        [InlineData("m5-chain410")]
        [InlineData("m5-hl411")]
        [InlineData("m5d-interleaved410")]
        public void InterleavedListedAndCompressedDataRegainTheirCycleCounts(string fixture)
        {
            byte[] original = FixtureCorpus.Read(fixture);
            byte[] bytes = Unfinalize(original, 1);
            MdfRecoveryResult result = Recover(ref bytes);
            Assert.True(result.Changed);
            Assert.Equal(original.Skip(8).ToArray(), bytes.Skip(8).ToArray()); // Every count restored; only the ID text differs.
            Assert.Equal("MDF     ", Encoding.ASCII.GetString(bytes, 0, 8));
        }

        [Fact]
        public void TornInterleavedTailInsideTheGrowingBlockIsCut()
        {
            byte[] original = FixtureCorpus.Read("m5b-id1-dt410");
            long dg = BitConverter.ToInt64(original, 88);
            long dt = BitConverter.ToInt64(original, (int)dg + 40);
            Assert.Equal(original.Length, dt + (long)BitConverter.ToUInt64(original, (int)dt + 8)); // The DT ends the file.
            byte[] bytes = Unfinalize(original, 5);
            BitConverter.GetBytes((ulong)24).CopyTo(bytes, (int)dt + 8); // The DT length was never updated.
            int length = bytes.Length;
            Array.Resize(ref bytes, length + 3);
            bytes[length] = 0xEE; // A torn record: an unknown record ID and two bytes.
            MdfRecoveryResult result = Recover(ref bytes);
            Assert.Contains("DT @" + dt + ": length set to " + (length - dt), result.Actions);
            Assert.Equal(original.Skip(8).ToArray(), bytes.Skip(8).Take(original.Length - 8).ToArray());

            byte[] vlsd = FixtureCorpus.Read("m5j-id1-dt410");
            byte[] zeroed = Unfinalize(vlsd, 0x25, true); // Bit 5: VLSD byte totals (length prefixes included).
            Recover(ref zeroed);
            Assert.Equal(vlsd.Skip(8).ToArray(), zeroed.Skip(8).ToArray());
        }

        [Fact]
        public void UnsupportedFlagsBadInputAndArgumentsAreRejected()
        {
            byte[] bytes = Unfinalized("4.10", 2);
            bytes[60] |= 0x40; // Update VLSD offsets.
            byte[] copy = (byte[])bytes.Clone();
            Assert.Throws<NotSupportedException>(() => Recover(ref copy));
            Assert.Equal(bytes, copy);

            byte[] garbage = Enumerable.Repeat((byte)'x', 128).ToArray();
            MdfFormatException error = Assert.Throws<MdfFormatException>(() => Recover(ref garbage));
            Assert.Equal("MDF001", error.DiagnosticCode);
            byte[] tiny = new byte[10];
            Assert.Equal("MDF007", Assert.Throws<MdfFormatException>(() => Recover(ref tiny)).DiagnosticCode);

            byte[] cyclic = Unfinalized("4.10", 2);
            int dg = (int)BitConverter.ToUInt64(cyclic, 88);
            BitConverter.GetBytes((ulong)dg).CopyTo(cyclic, dg + 24); // DG -> next DG = itself.
            Assert.Equal("MDF010", Assert.Throws<MdfFormatException>(() => Recover(ref cyclic)).DiagnosticCode);

            Assert.Throws<ArgumentNullException>(() => MdfRecovery.FinalizeFile(null));
            Assert.Throws<ArgumentException>(() => MdfRecovery.FinalizeFile(new MemoryStream(new byte[64], false)));
            byte[] corpus = FixtureCorpus.Read("m2-mdf410-unfinalized"); // ID + HD, no groups, standard flag 1.
            MdfRecoveryResult result = Recover(ref corpus);
            Assert.Equal(2, result.Actions.Count);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(corpus))) { Assert.Empty(reader.ReadMetadata()); }
        }
    }
}
