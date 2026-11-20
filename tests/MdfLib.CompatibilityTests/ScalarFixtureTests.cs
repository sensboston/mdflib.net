// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using MdfLib.TestSupport;
using Xunit;

namespace MdfLib.CompatibilityTests
{
    public sealed class ScalarFixtureTests
    {
        [Theory]
        [InlineData("m3-packed410", 1, MdfValueRepresentation.Raw)]
        [InlineData("m3-packed410", 2, MdfValueRepresentation.Raw)]
        [InlineData("m3-packed410", 1024, MdfValueRepresentation.Raw)]
        [InlineData("m3-packed410", 1, MdfValueRepresentation.Physical)]
        [InlineData("m3-packed410", 2, MdfValueRepresentation.Physical)]
        [InlineData("m3-packed410", 1024, MdfValueRepresentation.Physical)]
        [InlineData("m3-asammdf410", 1, MdfValueRepresentation.Raw)]
        [InlineData("m3-asammdf410", 2, MdfValueRepresentation.Raw)]
        [InlineData("m3-asammdf410", 1024, MdfValueRepresentation.Raw)]
        [InlineData("m3-asammdf410", 1, MdfValueRepresentation.Physical)]
        [InlineData("m3-asammdf410", 2, MdfValueRepresentation.Physical)]
        [InlineData("m3-asammdf410", 1024, MdfValueRepresentation.Physical)]
        [InlineData("m4-packed-le330", 1, MdfValueRepresentation.Raw)]
        [InlineData("m4-packed-le330", 2, MdfValueRepresentation.Raw)]
        [InlineData("m4-packed-le330", 1024, MdfValueRepresentation.Raw)]
        [InlineData("m4-packed-le330", 1, MdfValueRepresentation.Physical)]
        [InlineData("m4-packed-le330", 2, MdfValueRepresentation.Physical)]
        [InlineData("m4-packed-le330", 1024, MdfValueRepresentation.Physical)]
        [InlineData("m4-packed-be330", 1, MdfValueRepresentation.Raw)]
        [InlineData("m4-packed-be330", 2, MdfValueRepresentation.Raw)]
        [InlineData("m4-packed-be330", 1024, MdfValueRepresentation.Raw)]
        [InlineData("m4-packed-be330", 1, MdfValueRepresentation.Physical)]
        [InlineData("m4-packed-be330", 2, MdfValueRepresentation.Physical)]
        [InlineData("m4-packed-be330", 1024, MdfValueRepresentation.Physical)]
        [InlineData("m4-asammdf330", 1, MdfValueRepresentation.Raw)]
        [InlineData("m4-asammdf330", 2, MdfValueRepresentation.Raw)]
        [InlineData("m4-asammdf330", 1024, MdfValueRepresentation.Raw)]
        [InlineData("m4-asammdf330", 1, MdfValueRepresentation.Physical)]
        [InlineData("m4-asammdf330", 2, MdfValueRepresentation.Physical)]
        [InlineData("m4-asammdf330", 1024, MdfValueRepresentation.Physical)]
        [InlineData("m5-dz410", 1, MdfValueRepresentation.Raw)]
        [InlineData("m5-dz410", 2, MdfValueRepresentation.Raw)]
        [InlineData("m5-dz410", 1024, MdfValueRepresentation.Raw)]
        [InlineData("m5-dz410", 1, MdfValueRepresentation.Physical)]
        [InlineData("m5-dz410", 2, MdfValueRepresentation.Physical)]
        [InlineData("m5-dz410", 1024, MdfValueRepresentation.Physical)]
        [InlineData("m5-transpose411", 1, MdfValueRepresentation.Raw)]
        [InlineData("m5-transpose411", 2, MdfValueRepresentation.Raw)]
        [InlineData("m5-transpose411", 1024, MdfValueRepresentation.Raw)]
        [InlineData("m5-transpose411", 1, MdfValueRepresentation.Physical)]
        [InlineData("m5-transpose411", 2, MdfValueRepresentation.Physical)]
        [InlineData("m5-transpose411", 1024, MdfValueRepresentation.Physical)]
        [InlineData("m5-offset411", 1, MdfValueRepresentation.Raw)]
        [InlineData("m5-offset411", 2, MdfValueRepresentation.Raw)]
        [InlineData("m5-offset411", 1024, MdfValueRepresentation.Raw)]
        [InlineData("m5-offset411", 1, MdfValueRepresentation.Physical)]
        [InlineData("m5-offset411", 2, MdfValueRepresentation.Physical)]
        [InlineData("m5-offset411", 1024, MdfValueRepresentation.Physical)]
        [InlineData("m5-chain410", 1, MdfValueRepresentation.Raw)]
        [InlineData("m5-chain410", 2, MdfValueRepresentation.Raw)]
        [InlineData("m5-chain410", 1024, MdfValueRepresentation.Raw)]
        [InlineData("m5-chain410", 1, MdfValueRepresentation.Physical)]
        [InlineData("m5-chain410", 2, MdfValueRepresentation.Physical)]
        [InlineData("m5-chain410", 1024, MdfValueRepresentation.Physical)]
        [InlineData("m5-hl411", 1, MdfValueRepresentation.Raw)]
        [InlineData("m5-hl411", 2, MdfValueRepresentation.Raw)]
        [InlineData("m5-hl411", 1024, MdfValueRepresentation.Raw)]
        [InlineData("m5-hl411", 1, MdfValueRepresentation.Physical)]
        [InlineData("m5-hl411", 2, MdfValueRepresentation.Physical)]
        [InlineData("m5-hl411", 1024, MdfValueRepresentation.Physical)]
        [InlineData("m5-hl410", 1, MdfValueRepresentation.Raw)]
        [InlineData("m5-hl410", 2, MdfValueRepresentation.Raw)]
        [InlineData("m5-hl410", 1024, MdfValueRepresentation.Raw)]
        [InlineData("m5-hl410", 1, MdfValueRepresentation.Physical)]
        [InlineData("m5-hl410", 2, MdfValueRepresentation.Physical)]
        [InlineData("m5-hl410", 1024, MdfValueRepresentation.Physical)]
        [InlineData("m5-asammdf-zlib411", 1, MdfValueRepresentation.Raw)]
        [InlineData("m5-asammdf-zlib411", 2, MdfValueRepresentation.Raw)]
        [InlineData("m5-asammdf-zlib411", 1024, MdfValueRepresentation.Raw)]
        [InlineData("m5-asammdf-zlib411", 1, MdfValueRepresentation.Physical)]
        [InlineData("m5-asammdf-zlib411", 2, MdfValueRepresentation.Physical)]
        [InlineData("m5-asammdf-zlib411", 1024, MdfValueRepresentation.Physical)]
        [InlineData("m5-asammdf-transpose411", 1, MdfValueRepresentation.Raw)]
        [InlineData("m5-asammdf-transpose411", 2, MdfValueRepresentation.Raw)]
        [InlineData("m5-asammdf-transpose411", 1024, MdfValueRepresentation.Raw)]
        [InlineData("m5-asammdf-transpose411", 1, MdfValueRepresentation.Physical)]
        [InlineData("m5-asammdf-transpose411", 2, MdfValueRepresentation.Physical)]
        [InlineData("m5-asammdf-transpose411", 1024, MdfValueRepresentation.Physical)]
        [MemberData(nameof(InterleavedCases))]
        [MemberData(nameof(Mdf420Cases))]
        public void AllSamplesMatchPreImplementationIndependentExpectations(string id, int batchSize, object representationValue)
        {
            MdfValueRepresentation representation = (MdfValueRepresentation)representationValue;
            ExpectedCase expected = ReadExpected(id);
            using (MemoryStream source = new MemoryStream(FixtureCorpus.Read(id), false))
            using (MdfReader reader = MdfReader.Open(source))
            {
                IReadOnlyList<MdfDataGroup> catalog = reader.ReadMetadata();
                Assert.Equal(expected.Groups.Select(group => group.DataGroupIndex).Distinct().Count(), catalog.Count);
                foreach (MdfDataGroup dataGroup in catalog)
                {
                    Assert.Equal(expected.Groups.Count(group => group.DataGroupIndex == dataGroup.Index), dataGroup.ChannelGroups.Count);
                }
                if (expected.Version != null) { Assert.Equal(expected.Version, reader.Header.Version); }
                foreach (ExpectedGroup wanted in expected.Groups)
                {
                    MdfChannelGroup group = catalog[wanted.DataGroupIndex].ChannelGroups[wanted.ChannelGroupIndex];
                    Assert.Equal((ulong)wanted.Samples, group.SampleCount);
                    Assert.True(group.CanReadSamples, group.UnsupportedReason);
                    int[] indices = Enumerable.Range(0, wanted.Channels.Count).ToArray();
                    int next = 0;
                    foreach (MdfSampleBatch batch in reader.ReadSamples(group, indices, batchSize: batchSize, representation: representation))
                    {
                        Assert.Equal((ulong)next, batch.FirstSampleIndex);
                        Assert.InRange(batch.SampleCount, 1, batchSize);
                        for (int row = 0; row < batch.SampleCount; row++)
                        {
                            for (int column = 0; column < indices.Length; column++)
                            {
                                ExpectedChannel channel = wanted.Channels[column];
                                Assert.Equal(channel.Name, batch.Channels[column].Name);
                                MdfValueKind kind = (MdfValueKind)Enum.Parse(typeof(MdfValueKind), channel.Kind);
                                if (representation == MdfValueRepresentation.Physical && group.Channels[column].Conversion.Kind == MdfConversionKind.Linear)
                                {
                                    kind = MdfValueKind.FloatingPoint;
                                }
                                string value = (representation == MdfValueRepresentation.Raw ? channel.Raw : channel.Physical)[next + row];
                                CheckValue(batch.GetValue(row, column), kind, channel.Valid[next + row], value);
                            }
                        }
                        next += batch.SampleCount;
                    }
                    Assert.Equal(wanted.Samples, next);
                }
            }
        }

        [Theory]
        [InlineData("m3-packed410")]
        [InlineData("m3-asammdf410")]
        [InlineData("m4-packed-le330")]
        [InlineData("m4-packed-be330")]
        [InlineData("m4-asammdf330")]
        [InlineData("m5-dz410")]
        [InlineData("m5-transpose411")]
        [InlineData("m5-offset411")]
        [InlineData("m5-chain410")]
        [InlineData("m5-hl411")]
        [InlineData("m5-hl410")]
        [InlineData("m5-asammdf-zlib411")]
        [InlineData("m5-asammdf-transpose411")]
        public void SelectedReverseOrderSubrangesMatchTheSameIndependentArrays(string id)
        {
            ExpectedGroup wanted = ReadExpected(id).Groups[0];
            using (MemoryStream source = new MemoryStream(FixtureCorpus.Read(id), false))
            using (MdfReader reader = MdfReader.Open(source))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                int[] indices = { wanted.Channels.Count - 1, 1, 0 };
                List<MdfSampleBatch> batches = reader.ReadSamples(group, indices, 1, 3, 2).ToList();
                Assert.Equal(new[] { 2, 1 }, batches.Select(batch => batch.SampleCount));
                int sample = 1;
                foreach (MdfSampleBatch batch in batches)
                {
                    for (int row = 0; row < batch.SampleCount; row++, sample++)
                    {
                        for (int column = 0; column < indices.Length; column++)
                        {
                            ExpectedChannel channel = wanted.Channels[indices[column]];
                            Assert.Same(group.Channels[indices[column]], batch.Channels[column]);
                            CheckValue(batch.GetValue(row, column), (MdfValueKind)Enum.Parse(typeof(MdfValueKind), channel.Kind), channel.Valid[sample], channel.Raw[sample]);
                        }
                    }
                }
            }
        }

        public static IEnumerable<object[]> InterleavedCases()
        {
            string[] ids = { "m5b-prefix-le330", "m5b-suffix-be330", "m5b-id1-dt410", "m5b-id2-dz411", "m5b-id4-dl410",
                "m5b-id8-hl411", "m5b-wide-id8-hl411", "m5b-asammdf-derived330", "m5b-asammdf-derived411",
                "m5h-id1-dt420", "m5h-id2-dz420", "m5h-id4-dl420", "m5h-id8-hl420", "m5h-wide-id8-hl420" };
            foreach (string id in ids)
            {
                foreach (int batch in new[] { 1, 2, 1024 })
                {
                    foreach (MdfValueRepresentation representation in new[] { MdfValueRepresentation.Raw, MdfValueRepresentation.Physical })
                    {
                        yield return new object[] { id, batch, representation };
                    }
                }
            }
        }

        public static IEnumerable<object[]> Mdf420Ids()
        {
            string[] ids = { "m5c-dt420", "m5c-hl420", "m5c-ld-equal420", "m5c-ld-offset420", "m5c-ld-chain420", "m5c-ld-null420",
                "m5c-ld-noinval420", "m5c-ld-remote420", "m5c-ld-plain-offset420", "m5c-ld-plain-chain420", "m5c-ld-single420",
                "m5c-ld-zsingle420", "m5c-ld-tsingle420", "m5c-asammdf-row420", "m5c-asammdf-ld420", "m5c-asammdf-zld420", "m5c-asammdf-transpose420" };
            foreach (string id in ids) { yield return new object[] { id }; }
        }

        public static IEnumerable<object[]> Mdf420Cases()
        {
            foreach (object[] row in Mdf420Ids())
            {
                foreach (int batch in new[] { 1, 2, 1024 })
                {
                    foreach (MdfValueRepresentation representation in new[] { MdfValueRepresentation.Raw, MdfValueRepresentation.Physical })
                    { yield return new object[] { row[0], batch, representation }; }
                }
            }
        }

        [Theory]
        [InlineData("m5b-prefix-le330")]
        [InlineData("m5b-suffix-be330")]
        [InlineData("m5b-id1-dt410")]
        [InlineData("m5b-id2-dz411")]
        [InlineData("m5b-id4-dl410")]
        [InlineData("m5b-id8-hl411")]
        [InlineData("m5b-wide-id8-hl411")]
        [InlineData("m5b-asammdf-derived330")]
        [InlineData("m5b-asammdf-derived411")]
        [InlineData("m5h-id1-dt420")]
        [InlineData("m5h-id2-dz420")]
        [InlineData("m5h-id4-dl420")]
        [InlineData("m5h-id8-hl420")]
        [InlineData("m5h-wide-id8-hl420")]
        [MemberData(nameof(Mdf420Ids))]
        public void EveryGroupRangeBatchAndRepresentationMatchesUnchangedPreParserArrays(string id)
        {
            ExpectedCase expected = ReadExpected(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                IReadOnlyList<MdfDataGroup> catalog = reader.ReadMetadata();
                foreach (ExpectedGroup wanted in expected.Groups)
                {
                    MdfChannelGroup group = catalog[wanted.DataGroupIndex].ChannelGroups[wanted.ChannelGroupIndex];
                    int[] indices = Enumerable.Range(0, wanted.Channels.Count).Reverse().ToArray();
                    for (int start = 0; start <= wanted.Samples; start++)
                    {
                        for (int count = 0; count <= wanted.Samples - start; count++)
                        {
                            foreach (int size in new[] { 1, 2, 1024 })
                            {
                                foreach (MdfValueRepresentation representation in new[] { MdfValueRepresentation.Raw, MdfValueRepresentation.Physical })
                                {
                                    int next = start;
                                    foreach (MdfSampleBatch batch in reader.ReadSamples(group, indices, (ulong)start, (ulong)count, size, representation))
                                    {
                                        Assert.Equal((ulong)next, batch.FirstSampleIndex);
                                        Assert.InRange(batch.SampleCount, 1, size);
                                        for (int row = 0; row < batch.SampleCount; row++)
                                        {
                                            for (int column = 0; column < indices.Length; column++)
                                            {
                                                int index = indices[column];
                                                ExpectedChannel channel = wanted.Channels[index];
                                                Assert.Same(group.Channels[index], batch.Channels[column]);
                                                MdfValueKind kind = representation == MdfValueRepresentation.Physical
                                                    && group.Channels[index].Conversion.Kind == MdfConversionKind.Linear ? MdfValueKind.FloatingPoint
                                                    : (MdfValueKind)Enum.Parse(typeof(MdfValueKind), channel.Kind);
                                                CheckValue(batch.GetValue(row, column), kind, channel.Valid[next + row],
                                                    (representation == MdfValueRepresentation.Raw ? channel.Raw : channel.Physical)[next + row]);
                                            }
                                        }
                                        next += batch.SampleCount;
                                    }
                                    Assert.Equal(start + count, next);
                                }
                            }
                        }
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(Mdf420Ids))]
        public void Mdf420StoredMasterArraysAreExplicitAndRemoteGroupsReadOnlyTheirOwnOrdinalChannels(string id)
        {
            ExpectedCase expected = ReadExpected(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                IReadOnlyList<MdfDataGroup> catalog = reader.ReadMetadata();
                foreach (ExpectedGroup wanted in expected.Groups)
                {
                    MdfChannelGroup group = catalog[wanted.DataGroupIndex].ChannelGroups[wanted.ChannelGroupIndex];
                    Assert.Equal((ulong)wanted.DataBytes + wanted.InvalidationBytes, group.RecordByteCount);
                    Assert.All(group.Channels, channel => Assert.True(channel.CanReadRaw));
                    MdfChannelGroup master = wanted.RemoteMasterDataGroupIndex.HasValue
                        ? catalog[wanted.RemoteMasterDataGroupIndex.Value].ChannelGroups[0] : group;
                    int[] masterIndices = master.Channels.Where(channel => channel.IsMaster).Select(channel => channel.Index).ToArray();
                    Assert.Single(masterIndices);
                    if (wanted.RemoteMasterDataGroupIndex.HasValue)
                    {
                        Assert.All(group.Channels, channel => Assert.False(channel.IsMaster));
                        Assert.DoesNotContain(group.Channels, channel => channel.Name == "time");
                    }
                    int index = 0;
                    foreach (MdfSampleBatch batch in reader.ReadSamples(master, masterIndices, batchSize: 2))
                    {
                        for (int row = 0; row < batch.SampleCount; row++, index++)
                        { CheckValue(batch.GetValue(row, 0), MdfValueKind.FloatingPoint, true, wanted.Timestamps[index]); }
                    }
                    Assert.Equal(wanted.Samples, index); // Original unsorted-in-time order, not an implicit join/resample.
                }
            }
        }

        [Fact]
        public void OriginalMetadataUnitsSharedSourcesAndDuplicateNamesAreNotGuessedOrFlattened()
        {
            using (MemoryStream source = new MemoryStream(FixtureCorpus.Read("m3-packed410"), false))
            using (MdfReader reader = MdfReader.Open(source))
            {
                IReadOnlyList<MdfDataGroup> catalog = reader.ReadMetadata();
                MdfChannelGroup group = catalog[0].ChannelGroups[0];
                Assert.Equal("  Scalar измерение ", group.Name);
                Assert.Equal(" group comment ", group.Comment);
                Assert.Contains("<DGcomment>", catalog[0].Comment);
                Assert.Contains("μ values", group.Channels[0].Comment);
                Assert.Equal("°C", group.Channels[8].Unit);
                Assert.Equal("fallback unit", group.Channels[8].Conversion.Unit);
                Assert.Equal((double?)0.5, group.Channels[8].Conversion.Factor);
                Assert.Equal((double?)(-10), group.Channels[8].Conversion.Offset);
                Assert.True(group.Channels[0].IsMaster);
                Assert.Equal((byte)1, group.Channels[0].SynchronizationType);
                Assert.Equal(47UL, group.RecordByteCount);
                Assert.Equal("https://example.invalid/never-opened", group.Source.Path);
                Assert.All(group.Channels, channel => Assert.Same(group.Source, channel.Source));
                Assert.Equal(group.Channels[1].Name, catalog[1].ChannelGroups[0].Channels[0].Name);
                Assert.Equal(MdfConversionKind.Identity, catalog[1].ChannelGroups[0].Channels[0].Conversion.Kind);
            }
        }

        [Theory]
        [InlineData("m4-packed-le330", false)]
        [InlineData("m4-packed-be330", true)]
        public void Mdf330MetadataRetainsCodePageSourceUnitsMasterAndExactHeaderTime(string id, bool bigEndian)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                Assert.Equal("3.30", reader.Header.Version);
                Assert.Equal(bigEndian, reader.Header.DefaultDataIsBigEndian);
                Assert.Equal((int?)28591, reader.Header.CodePage);
                Assert.Equal((int?)2, reader.Header.DataGroupCount);
                Assert.Equal(9_007_199_254_740_993UL, reader.Header.RawStartTimeNanoseconds);
                IReadOnlyList<MdfDataGroup> catalog = reader.ReadMetadata();
                MdfChannelGroup group = catalog[0].ChannelGroups[0];
                Assert.Equal(string.Empty, catalog[0].Comment); // DG link 2 is TR, not a TX comment.
                Assert.Equal(string.Empty, group.Name);
                Assert.Null(group.Source); // No invented acquisition source from the first channel.
                Assert.Equal("  groupe café", group.Comment);
                Assert.Equal("<!DOCTYPE inert><CNcomment> café </CNcomment>", group.Channels[0].Comment);
                Assert.Equal("explicit_be_double", group.Channels[14].Name); // Long name, not display alias.
                Assert.Equal("°C", group.Channels[9].Unit);
                Assert.Equal((double?)0.5, group.Channels[9].Conversion.Factor);
                Assert.Equal((double?)(-10), group.Channels[9].Conversion.Offset);
                Assert.Equal(MdfConversionKind.Identity, group.Channels[1].Conversion.Kind);
                Assert.Equal("count", group.Channels[1].Unit);
                Assert.True(group.Channels[0].IsMaster);
                Assert.Equal((byte)1, group.Channels[0].SynchronizationType);
                Assert.All(group.Channels.Skip(1), channel => { Assert.False(channel.IsMaster); Assert.Equal((byte)0, channel.SynchronizationType); });
                Assert.Equal(88UL, group.RecordByteCount);
                MdfSourceInformation ecu = group.Channels[0].Source;
                Assert.Equal("synthetic ECU", ecu.Name);
                Assert.Equal("https://example.invalid/no-open", ecu.Path);
                Assert.Equal((byte)1, ecu.SourceType);
                Assert.Equal((byte)0, ecu.BusType);
                Assert.All(group.Channels, channel => Assert.Same(ecu, channel.Source));
                MdfChannelGroup second = catalog[1].ChannelGroups[0];
                Assert.Equal(8202UL, second.RecordByteCount);
                Assert.Equal(group.Channels[3].Name, second.Channels[0].Name);
                Assert.Equal("synthetic CAN message", second.Channels[0].Source.Name);
                Assert.Equal("inert://sender", second.Channels[0].Source.Path);
                Assert.Equal((byte)2, second.Channels[0].Source.SourceType);
                Assert.Equal((byte)2, second.Channels[0].Source.BusType);
                Assert.All(reader.ReadSamples(group, new[] { 15 }), batch =>
                {
                    for (int row = 0; row < batch.SampleCount; row++) { Assert.True(batch.GetValue(row, 0).IsValid); }
                }); // Range metadata never becomes an invalidity mask.
            }
        }

        private static void CheckValue(MdfSampleValue value, MdfValueKind kind, bool valid, string expected)
        {
            Assert.Equal(kind, value.Kind);
            Assert.Equal(valid, value.IsValid);
            if (kind == MdfValueKind.UnsignedInteger) { Assert.Equal(ulong.Parse(expected, CultureInfo.InvariantCulture), value.UnsignedValue); }
            else if (kind == MdfValueKind.SignedInteger) { Assert.Equal(long.Parse(expected, CultureInfo.InvariantCulture), value.SignedValue); }
            else if (expected == "NaN") { Assert.True(double.IsNaN(value.FloatingPointValue)); }
            else if (expected == "Infinity") { Assert.Equal(double.PositiveInfinity, value.FloatingPointValue); }
            else if (expected == "-Infinity") { Assert.Equal(double.NegativeInfinity, value.FloatingPointValue); }
            else if (expected == "-0") { Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(value.FloatingPointValue)); }
            else { Assert.Equal(double.Parse(expected, CultureInfo.InvariantCulture), value.FloatingPointValue); }
        }

        private static ExpectedCase ReadExpected(string id)
        {
            using (Stream source = File.OpenRead(Path.Combine(AppContext.BaseDirectory, id.StartsWith("m5c-", StringComparison.Ordinal) ? "m5c-expectations.json"
                : id.StartsWith("m5b-", StringComparison.Ordinal) ? "m5b-expectations.json"
                : id.StartsWith("m5h-", StringComparison.Ordinal) ? "m5h-expectations.json"
                : id.StartsWith("m5-", StringComparison.Ordinal) ? "m5-expectations.json"
                : id.StartsWith("m4-", StringComparison.Ordinal) ? "m4-expectations.json" : "m3-expectations.json")))
            {
                ExpectedCorpus corpus = (ExpectedCorpus)new DataContractJsonSerializer(typeof(ExpectedCorpus)).ReadObject(source);
                Assert.Equal(1, corpus.SchemaVersion);
                ExpectedCase found = corpus.Cases.Single(item => item.Id == id);
                // m5h-expectations.json also holds the text case, so value arrays are required per selected case here.
                foreach (ExpectedGroup group in found.Groups)
                {
                    foreach (ExpectedChannel channel in group.Channels)
                    {
                        Assert.Equal(group.Samples, channel.Raw.Count);
                        Assert.Equal(group.Samples, channel.Physical.Count);
                        Assert.Equal(group.Samples, channel.Valid.Count);
                    }
                }
                return found;
            }
        }

        [DataContract]
        private sealed class ExpectedCorpus
        {
            [DataMember(Name = "schemaVersion", IsRequired = true)] public int SchemaVersion { get; set; }
            [DataMember(Name = "cases", IsRequired = true)] public List<ExpectedCase> Cases { get; set; }
        }
        [DataContract]
        private sealed class ExpectedCase
        {
            [DataMember(Name = "id", IsRequired = true)] public string Id { get; set; }
            [DataMember(Name = "version")] public string Version { get; set; }
            [DataMember(Name = "groups", IsRequired = true)] public List<ExpectedGroup> Groups { get; set; }
        }
        [DataContract]
        private sealed class ExpectedGroup
        {
            [DataMember(Name = "dataGroupIndex", IsRequired = true)] public int DataGroupIndex { get; set; }
            [DataMember(Name = "channelGroupIndex", IsRequired = true)] public int ChannelGroupIndex { get; set; }
            [DataMember(Name = "samples", IsRequired = true)] public int Samples { get; set; }
            [DataMember(Name = "timestamps")] public List<string> Timestamps { get; set; }
            [DataMember(Name = "dataBytes")] public uint DataBytes { get; set; }
            [DataMember(Name = "invalidationBytes")] public uint InvalidationBytes { get; set; }
            [DataMember(Name = "remoteMasterDataGroupIndex")] public int? RemoteMasterDataGroupIndex { get; set; }
            [DataMember(Name = "channels", IsRequired = true)] public List<ExpectedChannel> Channels { get; set; }
        }
        [DataContract]
        private sealed class ExpectedChannel
        {
            [DataMember(Name = "name", IsRequired = true)] public string Name { get; set; }
            [DataMember(Name = "kind", IsRequired = true)] public string Kind { get; set; }
            [DataMember(Name = "raw")] public List<string> Raw { get; set; }
            [DataMember(Name = "physical")] public List<string> Physical { get; set; }
            [DataMember(Name = "valid")] public List<bool> Valid { get; set; }
        }
    }
}
