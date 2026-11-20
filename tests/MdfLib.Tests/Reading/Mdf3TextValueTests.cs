// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MdfLib.Tests.TestDoubles;
using MdfLib.TestSupport;
using Xunit;

namespace MdfLib.Tests.Reading
{
    // MDF 3.30 String (7) / Byte Array (8) values over literal mutations of the frozen pre-parser m5g files.
    public sealed class Mdf3TextValueTests
    {
        private const int CnText = 558;
        private const int CnShort = 786;
        private const int CnBytes = 1004;
        private const int CnCounter = 1232;
        private const int IdentityCc = 1916;
        private const int Raw = 2024;
        private const int RecordBytes = 40;
        private const string Unaligned = "MDF 3 string and byte-array fields must be nonempty, byte-aligned whole bytes.";

        private static byte[] Fixture(string id = "m5g-latin330") { return FixtureCorpus.Read(id); }

        private static MdfChannelGroup Group(MdfReader reader) { return reader.ReadMetadata()[0].ChannelGroups[0]; }

        private static MdfSampleValue Single(byte[] bytes, int channel, int row)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                return reader.ReadSamples(Group(reader), new[] { channel }, (ulong)row, 1).Single().GetValue(0, 0);
            }
        }

        [Theory]
        [InlineData(0, "\u007f\u0080")]
        [InlineData(28591, "\u007f\u0080")]
        [InlineData(20127, "\u007f\uFFFD")]
        [InlineData(65001, "\u007f\uFFFD")]
        public void TheIdCodePageSelectsTheSampleDecoder(int codePage, string expected)
        {
            byte[] bytes = Fixture();
            M3Wire.Put(bytes, 30, (ulong)codePage, 2);
            MdfSampleValue value = Single(bytes, 2, 3); // Stored 7F 80 00 01: the first NUL ends the text.
            Assert.Equal(MdfValueKind.Text, value.Kind);
            Assert.Equal(expected, value.TextValue);
            Assert.Equal(new byte[] { 0x7F, 0x80, 0x00, 0x01 }, value.GetByteArrayValue());
            Assert.True(value.IsValid);
        }

        [Fact]
        public void SampleTextIsNeverTrimmedAndIllFormedSamplesAreNotErrors()
        {
            Assert.Equal("  pad  ", Single(Fixture(), 1, 3).TextValue);
            Assert.Equal(string.Empty, Single(Fixture(), 1, 2).TextValue);
            Assert.Equal("\u20AC\uD83D\uDE00\uFFFD\uFFFD\uFFFD\uFFFD\uFFFD", Single(Fixture("m5g-utf8330"), 1, 4).TextValue);
            Assert.Equal("\uFEFFZ", Single(Fixture("m5g-utf8330"), 2, 4).TextValue);
            Assert.Equal("\uFFFD\uFFFD\uFFFD\uFFFD\uFFFD\uFFFD\uFFFD\uFFFD\uFFFD\uFFFD\uFFFD\uFFFD", Single(Fixture("m5g-ascii330"), 1, 4).TextValue);
        }

        [Fact]
        public void KindsGettersAndRawEqualsPhysical()
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(Fixture("m5g-ascii330"), false)))
            {
                MdfChannelGroup group = Group(reader);
                Assert.Equal(MdfValueKind.Text, group.Channels[1].ValueKind);
                Assert.Equal(MdfValueKind.ByteArray, group.Channels[3].ValueKind);
                Assert.True(group.Channels[3].CanReadPhysical);
                MdfSampleBatch raw = reader.ReadSamples(group, new[] { 3, 1, 4 }).Single();
                MdfSampleBatch physical = reader.ReadSamples(group, new[] { 3, 1, 4 }, representation: MdfValueRepresentation.Physical).Single();
                for (int row = 0; row < 5; row++)
                {
                    Assert.Equal(raw.GetValue(row, 0).GetByteArrayValue(), physical.GetValue(row, 0).GetByteArrayValue());
                    Assert.Equal(raw.GetValue(row, 1).TextValue, physical.GetValue(row, 1).TextValue);
                }
                MdfSampleValue bytes = raw.GetValue(1, 0);
                Assert.Equal(new byte[] { 0, 1, 2, 3, 4, 5 }, bytes.GetByteArrayValue());
                Assert.Throws<InvalidOperationException>(() => bytes.TextValue);
                Assert.Throws<InvalidOperationException>(() => raw.GetValue(0, 1).UnsignedValue);
                Assert.Equal(0x1234UL, raw.GetValue(2, 2).UnsignedValue); // Big-endian numeric neighbour.
            }
        }

        [Theory]
        [InlineData("role", CnText + 24, 2UL, "Only stored value string and byte-array channels are supported.")]
        [InlineData("zero", CnText + 188, 0UL, Unaligned)]
        [InlineData("partial", CnText + 188, 92UL, Unaligned)]
        [InlineData("shift", CnBytes + 186, 24UL * 8 + 4, Unaligned)]
        [InlineData("dependency", CnBytes + 16, (ulong)IdentityCc, "Text and byte-array arrays are not supported.")] // CD not resolved.
        [InlineData("linear", CnShort + 8, 1962UL, "String and byte-array channels support only identity conversion.")]
        public void IneligibleShapesStayInspectableButUnsupported(string name, int field, ulong value, string reason)
        {
            byte[] bytes = Fixture();
            M3Wire.Put(bytes, field, value, name == "dependency" || name == "linear" ? 4 : 2);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = Group(reader);
                MdfChannel channel = group.Channels.Single(c => c.UnsupportedReason != null && c.Index < 5);
                Assert.Equal(reason, channel.UnsupportedReason);
                Assert.False(channel.CanReadRaw);
                Assert.True(channel.ValueKind == MdfValueKind.Text || channel.ValueKind == MdfValueKind.ByteArray);
                int before = source.ReadCalls;
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { channel.Index }).ToList());
                Assert.Equal(before, source.ReadCalls);
                Assert.Equal(5, reader.ReadSamples(group, new[] { 0, 4 }).Sum(b => b.SampleCount));
            }
        }

        [Theory]
        [InlineData(CnText + 186, 30 * 8, 2)]
        [InlineData(CnBytes + 188, 17 * 8, 2)]
        [InlineData(CnText + 226, 8192, 2)]
        public void FieldsBeyondTheRecordAreMdf013BeforeSampleIo(int field, int value, int width)
        {
            byte[] bytes = Fixture();
            M3Wire.Put(bytes, field, (ulong)value, width);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                Assert.Equal("MDF013", Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
            }
        }

        [Fact]
        public void NarrowerDeclaredFieldsCopyOnlyTheirOwnBytes()
        {
            byte[] bytes = Fixture();
            M3Wire.Put(bytes, CnText + 188, 32, 2);
            MdfSampleValue value = Single(bytes, 1, 1);
            Assert.Equal("ABCD", value.TextValue);
            Assert.Equal(4, value.GetByteArrayValue().Length);
        }

        [Fact]
        public void IdentityConversionOrNoConversionAreBothEligible()
        {
            byte[] bytes = Fixture();
            M3Wire.Put(bytes, CnText + 8, 0, 4);
            Assert.Equal("ABCDEFGHIJKL", Single(bytes, 1, 1).TextValue);
        }

        [Fact]
        public void Mdf330NumericSlotsRetypedAsStringOrBytesBecomeTextValues()
        {
            foreach (int wireKind in new[] { 7, 8 })
            {
                byte[] bytes = M4Wire.Read();
                M4Wire.Put(bytes, M4Wire.SecondCn + 190, (ulong)wireKind, 2);
                using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
                {
                    MdfChannel channel = reader.ReadMetadata()[0].ChannelGroups[0].Channels[1];
                    Assert.Null(channel.UnsupportedReason);
                    Assert.Equal(wireKind == 7 ? MdfValueKind.Text : MdfValueKind.ByteArray, channel.ValueKind);
                    MdfSampleValue value = reader.ReadSamples(reader.ReadMetadata()[0].ChannelGroups[0], new[] { 1 }, 1, 1).Single().GetValue(0, 0);
                    Assert.Equal(new byte[] { 0x01, 0, 0, 0, 0, 0, 0x20, 0 }, value.GetByteArrayValue()); // 9007199254740993 LE.
                }
            }
        }

        [Fact]
        public void FixedWidthIsPlannedUpFrontAndCancellationIsObserved()
        {
            // Header 160 + one row (40 + 64 + 3*12 = 140): 440 allows exactly two rows per batch.
            using (MdfReader reader = MdfReader.Open(new MemoryStream(Fixture(), false), false, new MdfReaderOptions { MaximumSampleBatchBytes = 440 }))
            {
                MdfChannelGroup group = Group(reader);
                Assert.Equal(new[] { 2, 2, 1 }, reader.ReadSamples(group, new[] { 1 }).Select(b => b.SampleCount));
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    cancel.Cancel();
                    Assert.ThrowsAny<OperationCanceledException>(() => reader.ReadSamples(group, new[] { 1 }, cancellationToken: cancel.Token).ToList());
                }
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(Fixture(), false), false, new MdfReaderOptions { MaximumSampleBatchBytes = 299 }))
            {
                Assert.Equal(nameof(MdfReaderOptions.MaximumSampleBatchBytes),
                    Assert.Throws<MdfResourceLimitException>(() => reader.ReadSamples(Group(reader), new[] { 1 }).ToList()).LimitName);
            }
        }

        [Fact]
        public void InterleavedRecordsDecodeTextBesideANeighbourGroupAndOutliveDispose()
        {
            List<MdfSampleBatch> batches;
            using (MdfReader reader = MdfReader.Open(new MemoryStream(Fixture("m5g-interleaved330"), false)))
            {
                IReadOnlyList<MdfChannelGroup> groups = reader.ReadMetadata()[0].ChannelGroups;
                Assert.Equal(2, groups.Count);
                batches = reader.ReadSamples(groups[0], new[] { 3, 1 }, batchSize: 2).ToList();
                Assert.Equal(42UL, reader.ReadSamples(groups[1], new[] { 1 }, 2, 1).Single().GetValue(0, 0).UnsignedValue);
            }
            Assert.Equal(new[] { 2, 2, 1 }, batches.Select(b => b.SampleCount));
            Assert.Equal("Caf\u00e9", batches[0].GetValue(0, 1).TextValue);
            byte[] copy = batches[2].GetValue(0, 0).GetByteArrayValue();
            copy[0] = 0xEE;
            Assert.Equal((byte)'A', batches[2].GetValue(0, 0).GetByteArrayValue()[0]);
        }

        [Fact]
        public void RecordBytesAreReadExactlyFromTheHeaderlessRange()
        {
            byte[] bytes = Fixture();
            bytes[Raw + RecordBytes + 8] = (byte)'Z'; // Row 1 text, first byte.
            Assert.Equal("ZBCDEFGHIJKL", Single(bytes, 1, 1).TextValue);
        }
    }
}
