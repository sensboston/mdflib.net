// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MdfLib.TestSupport;
using MdfLib.Tests.TestDoubles;
using MdfLib.Tests.Writing;
using Xunit;

namespace MdfLib.Tests.Reading
{
    public sealed class ValidatorTests
    {
        private static byte[] Measurement(double[] times, bool compress = false)
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(output, true, new MdfWriterOptions { CompressData = compress }))
            {
                writer.AddChannel(WriterMultiGroupTests.Master());
                writer.AddChannel(WriterConfigurationTests.Channel("v", 16));
                writer.StartMeasurement();
                for (int i = 0; i < times.Length; i++)
                { writer.SaveSample(new[] { MdfSampleValue.FromFloatingPoint(times[i]), MdfSampleValue.FromUnsigned((ulong)i, i % 3 != 2) }); }
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
            }
            return output.Snapshot();
        }

        private static MdfValidationReport Validate(byte[] file)
        {
            return MdfValidator.Validate(new MemoryStream(file));
        }

        [Fact]
        public void CleanMeasurementHasNoFindingsAndCountsInvalidValues()
        {
            MdfValidationReport report = Validate(Measurement(Enumerable.Range(0, 9).Select(i => i * 0.5).ToArray()));
            Assert.True(report.IsValid);
            Assert.False(report.HasErrors);
            Assert.Empty(report.Issues);
            Assert.Equal(1, report.ChannelGroupCount);
            Assert.Equal(9UL, report.SampleCount);
            Assert.Equal(3UL, report.InvalidValueCount);
        }

        [Fact]
        public void DecreasingAndNaNMastersAreReportedOnceAsWarnings()
        {
            MdfValidationReport report = Validate(Measurement(new[] { 0, 1, double.NaN, 3, 2, 1, double.NaN }));
            Assert.True(report.IsValid);
            Assert.Equal(2, report.Issues.Count);
            Assert.All(report.Issues, issue => Assert.Equal(MdfValidationSeverity.Warning, issue.Severity));
            Assert.Equal("DG 0 / CG 0 / t", report.Issues[0].Location);
            Assert.Contains("NaN at record 2", report.Issues[0].Message);
            Assert.Contains("decrease at record 4", report.Issues[1].Message);
            Assert.Null(report.Issues[1].DiagnosticCode);
        }

        [Fact]
        public void CorruptCompressedDataIsAnErrorWithItsDiagnosticCode()
        {
            byte[] file = Measurement(Enumerable.Range(0, 200).Select(i => (double)i).ToArray(), true);
            int dz = -1;
            for (int i = 0; i + 4 <= file.Length && dz < 0; i += 8) { if (file[i] == '#' && file[i + 1] == '#' && file[i + 2] == 'D' && file[i + 3] == 'Z') { dz = i; } }
            Assert.True(dz > 0);
            file[dz + 48 + 10] ^= 0x5A;
            MdfValidationReport report = Validate(file);
            Assert.False(report.IsValid);
            MdfValidationIssue error = report.Issues.First(issue => issue.Severity == MdfValidationSeverity.Error);
            Assert.StartsWith("DG 0 / CG 0", error.Location);
            Assert.StartsWith("MDF", error.DiagnosticCode);
        }

        [Fact]
        public void MetadataAndOpenFailuresBecomeFileErrors()
        {
            byte[] file = Measurement(new[] { 0.0, 1.0 });
            BitConverter.GetBytes(0xFFFFFFF8UL).CopyTo(file, 232 + 24 + 8); // CG -> first CN link far outside the file.
            MdfValidationReport report = Validate(file);
            Assert.False(report.IsValid);
            Assert.Equal("file", report.Issues.Single().Location);
            Assert.Equal("MDF007", report.Issues.Single().DiagnosticCode);
            Assert.Equal(0, report.ChannelGroupCount);

            MdfValidationReport broken = Validate(FixtureCorpus.Read("m2-mdf410-bad-link"));
            Assert.False(broken.IsValid);
            Assert.Equal("MDF007", broken.Issues.Single().DiagnosticCode);
            MdfValidationReport unfinalized = Validate(FixtureCorpus.Read("m2-mdf410-unfinalized"));
            Assert.Equal(MdfValidationSeverity.Error, unfinalized.Issues.Single().Severity);
            Assert.Null(unfinalized.Issues.Single().DiagnosticCode);
        }

        [Fact]
        public void DeclaredRecordCountMismatchIsAnError()
        {
            byte[] file = Measurement(new[] { 0.0, 1.0, 2.0 });
            int dt = file.Length - 3 * 11 - 24;
            while (dt % 8 != 0 || file[dt] != '#') { dt--; }
            Assert.Equal((byte)'T', file[dt + 3]);
            BitConverter.GetBytes((ulong)(24 + 2 * 11)).CopyTo(file, dt + 8); // The DT now holds two records, the CG still declares three.
            MdfValidationReport report = Validate(file);
            Assert.False(report.IsValid);
            Assert.Contains(report.Issues, issue => issue.Severity == MdfValidationSeverity.Error && issue.Location == "DG 0 / CG 0");
        }

        [Fact]
        public void ArgumentsAreChecked()
        {
            Assert.Throws<ArgumentNullException>(() => MdfValidator.Validate((Stream)null));
            Assert.Throws<ArgumentNullException>(() => MdfValidator.Validate((MdfReader)null));
        }

        public static IEnumerable<object[]> CorpusFiles()
        {
            foreach (string id in new[] { "m6e-model410", "m6e-model420", "m6e-comment330", "m6f-reduction410", "m6f-reduction420", "m6f-reduction330",
                "m5d-interleaved410", "m5f-text410", "m5c-ld-null420", "m4-asammdf330", "m3-asammdf410", "m7a-empty420" })
            { yield return new object[] { id }; }
        }

        [Theory]
        [MemberData(nameof(CorpusFiles))]
        public void ValidCorpusFilesHaveNoErrors(string id)
        {
            byte[] file = FixtureCorpus.Read(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file)))
            {
                MdfValidationReport report = MdfValidator.Validate(reader);
                Assert.True(report.IsValid, string.Join("\n", report.Issues));
                ulong declared = 0;
                foreach (MdfDataGroup group in reader.ReadMetadata())
                foreach (MdfChannelGroup channelGroup in group.ChannelGroups)
                { if (channelGroup.CanReadSamples && channelGroup.Channels.Any(channel => channel.CanReadRaw)) { declared += channelGroup.SampleCount; } }
                Assert.Equal(declared, report.SampleCount);
            }
        }
    }
}
