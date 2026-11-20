// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MdfLib.TestSupport;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Writing
{
    public sealed class FileCopierTests
    {
        private static byte[] Copy(byte[] source, MdfCopyOptions options, out MdfCopyResult result)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(source)))
            using (MemoryStream output = new MemoryStream())
            {
                result = MdfFileCopier.Copy(reader, output, options);
                return output.ToArray();
            }
        }

        private static List<string[]> Rows(MdfReader reader, MdfChannelGroup group, IReadOnlyList<int> channels, bool sanitize = false,
            MdfValueRepresentation representation = MdfValueRepresentation.Raw)
        {
            List<string[]> rows = new List<string[]>();
            foreach (MdfSampleBatch batch in reader.ReadSamples(group, channels, representation: representation))
            {
                for (int r = 0; r < batch.SampleCount; r++)
                {
                    string[] row = new string[channels.Count];
                    for (int c = 0; c < channels.Count; c++)
                    {
                        // an array column lists its elements in row-major order.
                        MdfChannelArray array = batch.Channels[c].Array;
                        List<string> cells = new List<string>();
                        for (ulong e = 0; e < (array == null ? 1UL : array.ElementCount); e++)
                        {
                            MdfSampleValue value = batch.GetValue(r, c, e);
                            string text = MdfCsvExporter.Format(value);
                            if (sanitize && value.Kind == MdfValueKind.Text) { text = MdfFileCopier.Sanitize(text); }
                            cells.Add((value.IsValid ? "" : "!") + value.Kind + ":" + text);
                        }
                        row[c] = (array == null ? "" : string.Join(",", array.Dimensions) + "=") + string.Join("|", cells);
                    }
                    rows.Add(row);
                }
            }
            return rows;
        }

        [Theory]
        [InlineData("4.10", false)]
        [InlineData("4.20", true)]
        public void FullCopyReproducesEveryGroupNameUnitConversionAndValue(string version, bool compress)
        {
            byte[] source = WriterMultiGroupTests.TwoGroups("4.10", false, 40, 10);
            MdfCopyResult result;
            byte[] copy = Copy(source, new MdfCopyOptions { WriterOptions = new MdfWriterOptions { Version = version, CompressData = compress } }, out result);
            Assert.Equal(2, result.ChannelGroupCount);
            Assert.Equal(50UL, result.SampleCount);
            Assert.Empty(result.Skipped);
            WriterMultiGroupTests.AssertTwoGroups(copy, 40, 10);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(copy)))
            {
                MdfChannel temp = reader.ReadMetadata()[1].ChannelGroups[0].Channels[1];
                Assert.Equal("degC", temp.Unit);
                Assert.Equal(MdfConversionKind.Linear, temp.Conversion.Kind);
                Assert.Equal(0.5, temp.Conversion.Factor);
                Assert.Equal(40.0, temp.Conversion.Offset);
            }
        }

        [Fact]
        public void TimeRangeKeepsInclusiveMasterWindowPerGroup()
        {
            byte[] source = WriterMultiGroupTests.TwoGroups("4.10", false, 40, 10);
            MdfCopyResult result;
            byte[] copy = Copy(source, new MdfCopyOptions { StartTime = 2, StopTime = 5, KeepSourceStartTime = false }, out result);
            Assert.Equal(13UL + 4UL, result.SampleCount);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(copy)))
            {
                Assert.Equal(0UL, reader.Header.RawStartTimeNanoseconds);
                var groups = reader.ReadMetadata();
                List<string[]> fast = Rows(reader, groups[0].ChannelGroups[0], new[] { 0, 1 });
                Assert.Equal(13, fast.Count);
                Assert.Equal(new[] { "FloatingPoint:2", "UnsignedInteger:8" }, fast[0]);
                Assert.Equal(new[] { "FloatingPoint:2.25", "!UnsignedInteger:9" }, fast[1]);
                Assert.Equal(new[] { "FloatingPoint:5", "UnsignedInteger:20" }, fast[12]);
                List<string[]> slow = Rows(reader, groups[1].ChannelGroups[0], new[] { 0, 1 });
                Assert.Equal(new[] { "2", "3", "4", "5" }, slow.Select(row => row[0].Split(':')[1]));
                Assert.Equal("SignedInteger:-1", slow[0][1]);
            }
            Copy(source, new MdfCopyOptions { StartTime = 9.5 }, out result);
            Assert.Equal(2UL, result.SampleCount); // Fast 9.5 and 9.75; the last slow sample is t = 9.
            Copy(source, new MdfCopyOptions { StopTime = -1 }, out result);
            Assert.Equal(0UL, result.SampleCount);
        }

        [Fact]
        public void Mdf3OutputKeepsEveryGroupAndDropsRowsWithInvalidSamples()
        {
            byte[] source = WriterMultiGroupTests.TwoGroups("4.10", false, 10, 3);
            MdfCopyResult result;
            byte[] copy = Copy(source, new MdfCopyOptions { WriterOptions = new MdfWriterOptions { Version = "3.30" } }, out result);
            Assert.Equal(2, result.ChannelGroupCount); // one interleaved DG with record IDs.
            Assert.Equal(8UL + 3, result.SampleCount); // Rows 4 and 9 carry an invalid speed.
            Assert.Empty(result.Skipped);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(copy)))
            {
                Assert.Equal("3.30", reader.Header.Version);
                IReadOnlyList<MdfChannelGroup> groups = reader.ReadMetadata().Single().ChannelGroups;
                Assert.Equal(2, groups.Count);
                Assert.Equal(new[] { "0", "1", "2", "3", "5", "6", "7", "8" }, Rows(reader, groups[0], new[] { 1 }).Select(row => row[0].Split(':')[1]));
                Assert.Equal(new[] { "-3", "-2", "-1" }, Rows(reader, groups[1], new[] { 1 }).Select(row => row[0].Split(':')[1]));
                Assert.Equal(MdfConversionKind.Linear, groups[1].Channels[1].Conversion.Kind);
            }
        }

        [Fact]
        public void ArgumentsAndRangesAreValidatedBeforeWriting()
        {
            byte[] source = WriterMultiGroupTests.TwoGroups("4.10", false, 4, 1);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(source)))
            using (MemoryStream output = new MemoryStream())
            {
                Assert.Throws<ArgumentNullException>(() => MdfFileCopier.Copy(null, output));
                Assert.Throws<ArgumentNullException>(() => MdfFileCopier.Copy(reader, null));
                Assert.Throws<ArgumentException>(() => MdfFileCopier.Copy(reader, output, new MdfCopyOptions { StartTime = 3, StopTime = 2 }));
                Assert.Throws<ArgumentException>(() => MdfFileCopier.Copy(reader, output, new MdfCopyOptions { StartTime = double.NaN }));
                Assert.Throws<NotSupportedException>(() => MdfFileCopier.Copy(reader, output, new MdfCopyOptions { WriterOptions = new MdfWriterOptions { Version = "4.00" } }));
                Assert.Equal(0L, output.Length);
                output.WriteByte(1);
                Assert.Throws<ArgumentException>(() => MdfFileCopier.Copy(reader, output)); // The destination must be empty.
            }
            MdfCopyOptions defaults = new MdfCopyOptions();
            Assert.True(defaults.KeepSourceStartTime);
            Assert.Null(defaults.StartTime);
            Assert.Null(defaults.StopTime);
            Assert.Null(defaults.WriterOptions);
        }

        [Fact]
        public void GroupsWithoutAStoredMasterAreSkippedOnlyForTimeRanges()
        {
            TestWriteStream stream = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(stream, true))
            {
                writer.AddChannel(WriterConfigurationTests.Channel("counter", 32));
                writer.StartMeasurement();
                for (int i = 0; i < 5; i++) { writer.SaveSample(new[] { MdfSampleValue.FromUnsigned((ulong)i) }); }
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
            }
            MdfCopyResult result;
            Copy(stream.Snapshot(), null, out result);
            Assert.Equal(5UL, result.SampleCount);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(stream.Snapshot())))
            {
                InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => MdfFileCopier.Copy(reader, new MemoryStream(), new MdfCopyOptions { StartTime = 0 }));
                Assert.Contains("No channel group", error.Message);
            }
        }

        public static IEnumerable<object[]> CorpusFiles()
        {
            foreach (string id in new[] { "m7a-numeric330", "m7a-widths410", "m7a-numeric420", "m5-dz410", "m5b-id1-dt410", "m5c-dt420",
                "m5d-interleaved410", "m5d-arrays420", "m5e-structure410", "m5f-text410", "m6f-reduction410", "m6f-reduction330",
                "m3-asammdf410", "m4-asammdf330", "m5c-asammdf-ld420" })
            { yield return new object[] { id }; }
        }

        // Each copied channel pairs, in order, with the next equally named source channel of the next copied group,
        // and every raw value, kind and validity is identical.
        [Theory]
        [MemberData(nameof(CorpusFiles))]
        public void CorpusFilesCopyToSortedMdf4WithIdenticalRawValues(string id)
        {
            byte[] source = FixtureCorpus.Read(id);
            MdfCopyResult result;
            byte[] copy = Copy(source, new MdfCopyOptions(), out result);
            using (MdfReader original = MdfReader.Open(new MemoryStream(source)))
            using (MdfReader copied = MdfReader.Open(new MemoryStream(copy)))
            {
                List<MdfChannelGroup> sources = original.ReadMetadata().SelectMany(group => group.ChannelGroups).Where(group => group.CanReadSamples).ToList();
                // copied VLSD channels add their VLSD channel groups after the fixed one.
                List<MdfChannelGroup> targets = copied.ReadMetadata().Select(group => group.ChannelGroups[0]).ToList();
                Assert.All(copied.ReadMetadata(), group => Assert.All(group.ChannelGroups.Skip(1), vlsd => Assert.False(vlsd.CanReadSamples)));
                Assert.Equal(result.ChannelGroupCount, targets.Count);
                ulong samples = 0;
                int next = 0;
                foreach (MdfChannelGroup target in targets)
                {
                    MdfChannelGroup match = null;
                    List<int> sourceIndices = null;
                    for (; next < sources.Count && match == null; next++)
                    {
                        List<int> indices = new List<int>();
                        int position = 0;
                        foreach (MdfChannel channel in target.Channels)
                        {
                            while (position < sources[next].Channels.Count && sources[next].Channels[position].Name != channel.Name) { position++; }
                            if (position == sources[next].Channels.Count) { break; }
                            indices.Add(position++);
                        }
                        if (indices.Count == target.Channels.Count && sources[next].SampleCount == target.SampleCount) { match = sources[next]; sourceIndices = indices; }
                    }
                    Assert.NotNull(match);
                    List<int> all = Enumerable.Range(0, target.Channels.Count).ToList();
                    List<string[]> expected = Rows(original, match, sourceIndices, true), actual = Rows(copied, target, all);
                    Assert.Equal(expected.Count, actual.Count);
                    for (int r = 0; r < expected.Count; r++) { Assert.Equal(expected[r], actual[r]); }
                    // conversions are copied with their kind, parameters and texts; physical values agree.
                    List<int> physical = new List<int>();
                    for (int c = 0; c < target.Channels.Count; c++)
                    {
                        MdfChannelConversion from = match.Channels[sourceIndices[c]].Conversion, to = target.Channels[c].Conversion;
                        Assert.Equal(from.Kind, to.Kind);
                        Assert.Equal(from.Parameters, to.Parameters);
                        Assert.Equal(from.Texts, to.Texts);
                        Assert.Equal(from.Formula, to.Formula);
                        if (match.Channels[sourceIndices[c]].CanReadPhysical && target.Channels[c].CanReadPhysical && target.Channels[c].ValueKind != MdfValueKind.Text)
                        { physical.Add(c); }
                    }
                    if (physical.Count > 0)
                    {
                        List<string[]> expectedPhysical = Rows(original, match, physical.Select(c => sourceIndices[c]).ToList(), true, MdfValueRepresentation.Physical);
                        List<string[]> actualPhysical = Rows(copied, target, physical, true, MdfValueRepresentation.Physical);
                        for (int r = 0; r < expectedPhysical.Count; r++) { Assert.Equal(expectedPhysical[r], actualPhysical[r]); }
                    }
                    samples += target.SampleCount;
                }
                Assert.Equal(result.SampleCount, samples);
                Assert.True(targets.Count > 0, id);
            }
        }

        [Fact]
        public void LoneSurrogatesAreReplacedAndPairsKept()
        {
            // Built in code: lone surrogates in theory data would leak into test names and result XML.
            string[][] cases =
            {
                new[] { "plain", "plain" },
                new[] { "", "" },
                new[] { "a\uD800b", "a\uFFFDb" },
                new[] { "\uDC00", "\uFFFD" },
                new[] { "\uD83D\uDE00", "\uD83D\uDE00" },
                new[] { "\uD83D\uDE00\uD83D", "\uD83D\uDE00\uFFFD" },
                new[] { "\uDE00\uD83D", "\uFFFD\uFFFD" },
            };
            foreach (string[] item in cases) { Assert.Equal(item[1], MdfFileCopier.Sanitize(item[0])); }
            Assert.Equal(string.Empty, MdfFileCopier.Sanitize(null));
            string unchanged = "same";
            Assert.Same(unchanged, MdfFileCopier.Sanitize(unchanged));
        }

        [Fact]
        public void UnrepresentableChannelsAreListedNotCopied()
        {
            MdfCopyResult result;
            byte[] arrays = Copy(FixtureCorpus.Read("m5d-arrays420"), null, out result);
            // fixed arrays are copied (with their shape) to MDF 4 and listed for MDF 3.30 output.
            using (MdfReader reader = MdfReader.Open(new MemoryStream(arrays)))
            {
                Assert.Contains(reader.ReadMetadata().SelectMany(g => g.ChannelGroups).SelectMany(g => g.Channels), c => c.Array != null && c.CanReadRaw);
            }
            Copy(FixtureCorpus.Read("m5d-arrays420"), new MdfCopyOptions { WriterOptions = new MdfWriterOptions { Version = "3.30" } }, out result);
            Assert.Contains(result.Skipped, line => line.EndsWith("array channels need MDF 4 output", StringComparison.Ordinal));
            byte[] copy = Copy(FixtureCorpus.Read("m5f-text410"), null, out result);
            // the five VLSD channels (SD blocks) are copied as VLSD channels with their own VLSD channel groups.
            Assert.DoesNotContain(result.Skipped, line => line.Contains(" vlsd: "));
            using (MdfReader reader = MdfReader.Open(new MemoryStream(copy)))
            {
                IReadOnlyList<MdfChannelGroup> groups = reader.ReadMetadata()[0].ChannelGroups;
                Assert.Equal(6, groups.Count);
                Assert.Equal(5, groups[0].Channels.Count(c => c.Name.EndsWith(" vlsd", StringComparison.Ordinal)));
            }
        }
    }
}
