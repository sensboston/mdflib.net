// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Linq;
using MdfLib.TestSupport;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Reading
{
    // MDF 3.00/3.10/3.20 differ only in the ID code page field (3.30+),
    // the HD length (164 before 3.20, start time fields size-driven) and the DG length; mutations of the frozen corpus.
    public sealed class Mdf3RevisionTests
    {
        private const string Sorted300 = "m5x-sorted300";
        private const string Interleaved310 = "m5x-interleaved310";
        private const string Header320 = "m5x-header320";

        [Theory]
        [InlineData(Sorted300, "3.00", 228L)]
        [InlineData(Interleaved310, "3.10", 272L)]
        [InlineData(Header320, "3.20", 272L)]
        public void EarlierRevisionsOpenWithTheExactVersionAndReadOnlyTheHeaderBlock(string id, string version, long end)
        {
            using (TestReadStream source = new TestReadStream(FixtureCorpus.Read(id)))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                Assert.Equal(version, reader.Header.Version);
                Assert.Equal(end, source.Position);
                Assert.All(source.ReadRanges, range => Assert.True(range.Item1 + range.Item2 <= end));
                Assert.True(reader.ReadMetadata().SelectMany(g => g.ChannelGroups).All(g => g.CanReadSamples));
                Assert.Equal(0, source.WriteCalls);
            }
        }

        [Fact]
        public void ShortHeaderHasNoStartTimeFields()
        {
            using (MdfReader reader = MdfReader.Open(new TestReadStream(FixtureCorpus.Read(Sorted300))))
            {
                Assert.Equal(0UL, reader.Header.RawStartTimeNanoseconds);
                Assert.Equal(0, reader.Header.UtcOffsetMinutes);
                Assert.Equal((ushort)0, reader.Header.TimeQuality);
                Assert.Equal(string.Empty, reader.Header.TimerIdentification);
                Assert.Equal("Auteur \u00e9", reader.Header.Author);
                Assert.Equal("16:11:2026", reader.Header.DateText);
            }
        }

        [Fact]
        public void LongPre320HeaderReadsStartTimeFieldsBySize()
        {
            using (MdfReader reader = MdfReader.Open(new TestReadStream(FixtureCorpus.Read(Interleaved310))))
            {
                Assert.Equal(1763296496123456789UL, reader.Header.RawStartTimeNanoseconds);
                Assert.Equal(-300, reader.Header.UtcOffsetMinutes);
                Assert.Equal((ushort)16, reader.Header.TimeQuality);
                Assert.Equal("Local PC Reference Time", reader.Header.TimerIdentification);
            }
        }

        [Fact]
        public void ShorteningAPre320HeaderDropsOnlyTheStartTimeFields()
        {
            byte[] bytes = FixtureCorpus.Read(Interleaved310);
            Put16(bytes, 66, 164);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                Assert.Equal(228L, source.Position);
                Assert.Equal(0UL, reader.Header.RawStartTimeNanoseconds);
                Assert.Equal(0, reader.Header.UtcOffsetMinutes);
                Assert.Equal((ushort)0, reader.Header.TimeQuality);
                Assert.Equal(string.Empty, reader.Header.TimerIdentification);
                Assert.Equal("Project 3.10", reader.Header.Project);
                Assert.Equal(2, reader.ReadMetadata()[0].ChannelGroups.Count);
            }
        }

        [Theory]
        [InlineData(Sorted300, 163)]
        [InlineData(Interleaved310, 163)]
        [InlineData(Header320, 207)]
        [InlineData(Header320, 164)]
        public void HeaderShorterThanTheRevisionMinimumIsRejected(string id, int length)
        {
            byte[] bytes = FixtureCorpus.Read(id);
            Put16(bytes, 66, length);
            MdfFormatException error = Assert.Throws<MdfFormatException>(() => MdfReader.Open(new TestReadStream(bytes)));
            Assert.Equal("MDF006", error.DiagnosticCode);
        }

        [Fact]
        public void A330FileWithAShortHeaderIsStillRejected()
        {
            byte[] bytes = FixtureCorpus.Read(Header320);
            Version(bytes, "3.30");
            Put16(bytes, 66, 164);
            Assert.Equal("MDF006", Assert.Throws<MdfFormatException>(() => MdfReader.Open(new TestReadStream(bytes))).DiagnosticCode);
        }

        [Theory]
        [InlineData(Sorted300)]
        [InlineData(Interleaved310)]
        [InlineData(Header320)]
        public void CodePageFieldIsReservedBefore330(string id)
        {
            foreach (int field in new[] { 0, 1252, 65001, 65535 })
            {
                byte[] bytes = FixtureCorpus.Read(id);
                Put16(bytes, 30, field);
                using (MdfReader reader = MdfReader.Open(new TestReadStream(bytes)))
                {
                    Assert.Equal(0, reader.Header.CodePage);
                    Assert.NotEmpty(reader.ReadMetadata());
                }
            }
        }

        [Fact]
        public void TheSameCodePageFieldIsHonouredFrom330()
        {
            byte[] bytes = FixtureCorpus.Read(Header320);
            Version(bytes, "3.30");
            using (MdfReader reader = MdfReader.Open(new TestReadStream(bytes)))
            {
                Assert.Equal("3.30", reader.Header.Version);
                Assert.Equal("Ing\u00e9nieur", reader.Header.Author);
            }
            Put16(bytes, 30, 1252);
            Assert.Throws<NotSupportedException>(() => MdfReader.Open(new TestReadStream(bytes)));
            Put16(bytes, 30, 65001); // The Latin-1 author is not UTF-8.
            Assert.Equal("MDF009", Assert.Throws<MdfFormatException>(() => MdfReader.Open(new TestReadStream(bytes))).DiagnosticCode);
        }

        [Theory]
        [InlineData(Sorted300, 26, 1)]
        [InlineData(Interleaved310, 26, 2)]
        [InlineData(Header320, 24, 2)]
        [InlineData(Sorted300, 24, 256)]
        public void ByteOrderAndFloatFormatApplyToEveryRevision(string id, int offset, int value)
        {
            byte[] bytes = FixtureCorpus.Read(id);
            Put16(bytes, offset, value);
            using (TestReadStream source = new TestReadStream(bytes))
            {
                Assert.Throws<NotSupportedException>(() => MdfReader.Open(source, true));
                Assert.Equal(1, source.ReadCalls); // ID only.
            }
        }

        [Fact]
        public void DefaultByteOrderComesFromTheIdentificationBlock()
        {
            byte[] bytes = FixtureCorpus.Read(Interleaved310);
            using (MdfReader reader = MdfReader.Open(new TestReadStream(bytes)))
            {
                Assert.True(reader.Header.DefaultDataIsBigEndian);
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                List<MdfSampleBatch> batches = reader.ReadSamples(group, new[] { 1 }).ToList();
                Assert.Equal(new ulong[] { 0, 1, 4294967295, 2147483648, 12345 },
                    batches.SelectMany(b => Enumerable.Range(0, b.SampleCount).Select(r => b.GetValue(r, 0).UnsignedValue)));
            }
            Put16(bytes, 24, 0); // Little-endian default: the same bytes are reinterpreted, never refused.
            using (MdfReader reader = MdfReader.Open(new TestReadStream(bytes)))
            {
                Assert.False(reader.Header.DefaultDataIsBigEndian);
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                MdfSampleBatch batch = reader.ReadSamples(group, new[] { 1 }).First();
                Assert.Equal(16777216UL, batch.GetValue(1, 0).UnsignedValue);
            }
        }

        [Theory]
        [InlineData("2.10")]
        [InlineData("3.01")]
        [InlineData("3.21")]
        [InlineData("3.31")]
        public void NeighbouringRevisionsAreRefusedAfterTheIdentificationBlock(string version)
        {
            byte[] bytes = FixtureCorpus.Read(Header320);
            Version(bytes, version);
            using (TestReadStream source = new TestReadStream(bytes))
            {
                Assert.Throws<NotSupportedException>(() => MdfReader.Open(source, true));
                Assert.Equal(1, source.ReadCalls);
            }
        }

        [Fact]
        public void MismatchedVersionTextAndNumberAreStillMalformed()
        {
            byte[] bytes = FixtureCorpus.Read(Sorted300);
            Put16(bytes, 28, 310);
            Assert.Equal("MDF003", Assert.Throws<MdfFormatException>(() => MdfReader.Open(new TestReadStream(bytes))).DiagnosticCode);
        }

        private static void Version(byte[] bytes, string version)
        {
            for (int index = 0; index < 4; index++) { bytes[8 + index] = (byte)version[index]; }
            Put16(bytes, 28, (version[0] - '0') * 100 + (version[2] - '0') * 10 + version[3] - '0');
        }

        private static void Put16(byte[] bytes, int offset, int value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
        }
    }
}
