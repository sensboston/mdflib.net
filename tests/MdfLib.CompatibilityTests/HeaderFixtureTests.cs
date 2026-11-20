// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.IO;
using MdfLib.TestSupport;
using Xunit;

namespace MdfLib.CompatibilityTests
{
    public sealed class HeaderFixtureTests
    {
        [Fact]
        public void EveryDeclaredFixtureHasTheExactManifestSizeAndHash()
        {
            Assert.NotEmpty(FixtureCorpus.Entries());
            foreach (FixtureCorpus.Entry entry in FixtureCorpus.Entries())
            {
                Assert.NotEmpty(FixtureCorpus.Read(entry.Id));
            }
        }

        [Theory]
        [InlineData("m2-mdf330-le", false, false)]
        [InlineData("m2-mdf330-be-data", true, false)]
        [InlineData("m2-mdf330-utf8", false, true)]
        public void Mdf330HeadersMatchIndependentStructuralExpectations(string fixture, bool bigEndianData, bool utf8)
        {
            using (MemoryStream stream = new MemoryStream(FixtureCorpus.Read(fixture), false))
            using (MdfReader reader = MdfReader.Open(stream))
            {
                MdfFileHeader header = reader.Header;
                Assert.Equal("3.30", header.Version);
                Assert.Equal("M2py-v1", header.ProgramIdentifier);
                Assert.Equal(bigEndianData, header.DefaultDataIsBigEndian);
                Assert.Equal((int?)(utf8 ? 65001 : 0), header.CodePage);
                Assert.Equal((int?)0, header.DataGroupCount);
                Assert.Equal(9_007_199_254_740_993UL, header.RawStartTimeNanoseconds);
                Assert.Equal(-300, header.UtcOffsetMinutes);
                Assert.Equal((ushort)16, header.TimeQuality);
                Assert.Equal("16:11:2026", header.DateText);
                Assert.Equal("12:34:56", header.TimeText);
                Assert.Equal(utf8 ? "  Иван" : "  Author é", header.Author);
                Assert.Equal("Organization", header.Organization);
                Assert.Equal("Project", header.Project);
                Assert.Equal("Subject", header.Subject);
                Assert.Equal("Timer", header.TimerIdentification);
                Assert.Null(header.DaylightSavingOffsetMinutes);
                Assert.Null(header.TimeFlags);
                Assert.Null(header.Flags);
                Assert.Null(header.StartAngle);
                Assert.Null(header.StartDistance);
            }
        }

        [Theory]
        [InlineData("m2-mdf410-header")]
        [InlineData("m2-mdf410-linked-metadata")]
        public void Mdf410HeadersMatchIndependentStructuralExpectationsWithoutLinkedMetadata(string fixture)
        {
            using (MemoryStream stream = new MemoryStream(FixtureCorpus.Read(fixture), false))
            using (MdfReader reader = MdfReader.Open(stream))
            {
                MdfFileHeader header = reader.Header;
                Assert.Equal("4.10", header.Version);
                Assert.Equal("M2py-v1", header.ProgramIdentifier);
                Assert.False(header.DefaultDataIsBigEndian);
                Assert.Null(header.CodePage);
                Assert.Null(header.DataGroupCount);
                Assert.Equal(9_007_199_254_740_993UL, header.RawStartTimeNanoseconds);
                Assert.Equal(-300, header.UtcOffsetMinutes);
                Assert.Equal((int?)60, header.DaylightSavingOffsetMinutes);
                Assert.Equal((byte?)2, header.TimeFlags);
                Assert.Equal((ushort)16, header.TimeQuality);
                Assert.Equal((byte?)3, header.Flags);
                Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(header.StartAngle.Value));
                Assert.Equal((double?)1234.5, header.StartDistance);
                Assert.Null(header.DateText);
                Assert.Null(header.TimeText);
                Assert.Null(header.Author);
                Assert.Null(header.Organization);
                Assert.Null(header.Project);
                Assert.Null(header.Subject);
                Assert.Null(header.TimerIdentification);
                Assert.Equal(168L, stream.Position);
            }
        }

        [Theory]
        [InlineData("m2-mdf410-bad-link", "MDF007", 88UL)]
        [InlineData("m2-mdf330-truncated-hd", "MDF006", 64UL)]
        public void MalformedCorpusHasTheExpectedStableDiagnostic(string fixture, string code, ulong offset)
        {
            using (MemoryStream stream = new MemoryStream(FixtureCorpus.Read(fixture), false))
            {
                MdfFormatException error = Assert.Throws<MdfFormatException>(() => MdfReader.Open(stream, true));
                Assert.Equal(code, error.DiagnosticCode);
                Assert.Equal(offset, error.Offset);
                Assert.True(stream.CanRead);
            }
        }

        [Fact]
        public void UnfinalizedCorpusIsNeverSilentlyRepaired()
        {
            byte[] bytes = FixtureCorpus.Read("m2-mdf410-unfinalized");
            byte[] before = (byte[])bytes.Clone();
            using (MemoryStream stream = new MemoryStream(bytes, true))
            {
                Assert.True(MdfReader.IsMdfFile(stream));
                Assert.Throws<NotSupportedException>(() => MdfReader.Open(stream, true));
                Assert.Equal(before, bytes);
            }
        }
    }
}
