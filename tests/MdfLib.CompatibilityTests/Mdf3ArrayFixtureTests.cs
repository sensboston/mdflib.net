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
    // every expectation below was frozen before the respective MDF 3 CD parser change existed
    // (tools/fixtures/m5i-expectations.json, tools/fixtures/m5v-expectations.json). explicitly supersedes five frozen
    // outcomes; the expectations stay byte-identical and the supersession list is applied below.
    public sealed class Mdf3ArrayFixtureTests
    {
        private static readonly string[] Ids = { "m5i-arrays-le330", "m5i-interleaved-be330", "m5i-unsupported330",
            "m5v-packed-le330", "m5v-interleaved-be330", "m5v-unsupported330" };

        public static IEnumerable<object[]> FullReads()
        {
            foreach (string id in Ids)
            foreach (int batch in new[] { 1, 2, 1024 })
            foreach (MdfValueRepresentation representation in new[] { MdfValueRepresentation.Raw, MdfValueRepresentation.Physical })
            { yield return new object[] { id, batch, representation }; }
        }
        public static IEnumerable<object[]> AllFiles() { return Ids.Select(id => new object[] { id }); }

        [Theory]
        [MemberData(nameof(AllFiles))]
        public void CatalogShapesAndReasonsAreExactlyTheFrozenOnes(string id)
        {
            ExpectedCase expected = ReadCase(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                Assert.Equal(expected.Version, reader.Header.Version);
                IReadOnlyList<MdfDataGroup> catalog = reader.ReadMetadata();
                foreach (ExpectedGroup values in expected.Groups)
                {
                    MdfChannelGroup group = catalog[values.DataGroup].ChannelGroups[values.ChannelGroup];
                    Assert.True(group.CanReadSamples, group.UnsupportedReason);
                    Assert.Equal((ulong)values.Samples, group.SampleCount);
                    Assert.Equal((ulong)values.DataBytes, group.RecordByteCount);
                    Assert.Equal(values.Channels.Count, group.Channels.Count);
                    for (int index = 0; index < values.Channels.Count; index++)
                    {
                        ExpectedChannel wanted = values.Channels[index];
                        MdfChannel channel = group.Channels[index];
                        Assert.Equal(wanted.Index, channel.Index);
                        Assert.Equal(wanted.Name, channel.Name);
                        Assert.Equal(wanted.Bits, channel.BitCount);
                        Assert.Equal(wanted.ChannelType == 1, channel.IsMaster);
                        Assert.Equal((MdfValueKind)Enum.Parse(typeof(MdfValueKind), wanted.Kind), channel.ValueKind);
                        Assert.Equal(wanted.Conversion == "linear" ? MdfConversionKind.Linear : MdfConversionKind.Identity, channel.Conversion.Kind);
                        Assert.Null(channel.Parent);
                        Assert.Empty(channel.Components);
                        if (wanted.Dimensions == null) { Assert.Null(channel.Array); }
                        else
                        {
                            Assert.Equal(wanted.Dimensions, channel.Array.Dimensions);
                            Assert.Equal(wanted.Dimensions.Aggregate(1UL, (a, b) => a * b), channel.Array.ElementCount);
                        }
                        Assert.Equal(wanted.Readable, channel.CanReadRaw);
                        Assert.Equal(wanted.Reason, channel.UnsupportedReason);
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(FullReads))]
        public void EveryReadableChannelAndArrayElementMatchesTheFrozenValues(string id, int batchSize, object representationValue)
        {
            MdfValueRepresentation representation = (MdfValueRepresentation)representationValue;
            ExpectedCase expected = ReadCase(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                IReadOnlyList<MdfDataGroup> catalog = reader.ReadMetadata();
                foreach (ExpectedGroup values in expected.Groups)
                {
                    MdfChannelGroup group = catalog[values.DataGroup].ChannelGroups[values.ChannelGroup];
                    int[] selected = values.Channels.Where(c => c.Readable).Select(c => c.Index).ToArray();
                    Check(reader.ReadSamples(group, selected, batchSize: batchSize, representation: representation), values, selected, 0, values.Samples, representation);
                }
            }
        }

        [Theory]
        [MemberData(nameof(AllFiles))]
        public void ReorderedSingleAndMixedSelectionsMatchForEverySubrange(string id)
        {
            ExpectedCase expected = ReadCase(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                IReadOnlyList<MdfDataGroup> catalog = reader.ReadMetadata();
                foreach (ExpectedGroup values in expected.Groups)
                {
                    MdfChannelGroup group = catalog[values.DataGroup].ChannelGroups[values.ChannelGroup];
                    int[] readable = values.Channels.Where(c => c.Readable).Select(c => c.Index).ToArray();
                    List<int[]> selections = new List<int[]> { readable.Reverse().ToArray() };
                    selections.AddRange(values.Channels.Where(c => c.Dimensions != null && c.Readable).Select(c => new[] { c.Index }));
                    foreach (int[] selected in selections)
                    foreach (int batch in new[] { 1, 3, 1024 })
                    for (int start = 0; start <= values.Samples; start++)
                    for (int count = 0; count <= values.Samples - start; count++)
                    {
                        Check(reader.ReadSamples(group, selected, (ulong)start, (ulong)count, batch), values, selected, start, count, MdfValueRepresentation.Raw);
                    }
                    foreach (ExpectedChannel rejected in values.Channels.Where(c => !c.Readable))
                    {
                        Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { rejected.Index }).ToList());
                        Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 0, rejected.Index }).ToList());
                    }
                }
            }
        }

        [Fact]
        public void TheRejectedProducerProbeIsTheFrozenWholeFileError()
        {
            ExpectedCase expected = ReadCase("m5i-asammdf-probe330");
            Assert.Equal("MDF013", expected.MetadataError.Diagnostic);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read("m5i-asammdf-probe330"), false)))
            {
                Assert.Equal(expected.Version, reader.Header.Version);
                Assert.Equal(expected.MetadataError.Diagnostic, Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
            }
        }

        private static void Check(IEnumerable<MdfSampleBatch> batches, ExpectedGroup expected, int[] selected, int start, int count,
            MdfValueRepresentation representation)
        {
            int ordinal = start;
            foreach (MdfSampleBatch batch in batches)
            {
                Assert.Equal((ulong)ordinal, batch.FirstSampleIndex);
                Assert.Equal(selected, batch.Channels.Select(c => c.Index));
                Assert.InRange(batch.SampleCount, 1, count);
                for (int column = 0; column < selected.Length; column++)
                {
                    ExpectedChannel wanted = expected.Channels[selected[column]];
                    MdfChannel channel = batch.Channels[column];
                    MdfValueKind kind = representation == MdfValueRepresentation.Physical && wanted.Conversion == "linear"
                        ? MdfValueKind.FloatingPoint : (MdfValueKind)Enum.Parse(typeof(MdfValueKind), wanted.Kind);
                    bool raw = representation == MdfValueRepresentation.Raw;
                    for (int row = 0; row < batch.SampleCount; row++)
                    {
                        int sample = ordinal + row;
                        if (wanted.Dimensions == null)
                        {
                            Assert.Null(channel.Array);
                            Same(kind, (raw ? wanted.Raw : wanted.Physical)[sample], wanted.Valid[sample], batch.GetValue(row, column));
                            Same(kind, (raw ? wanted.Raw : wanted.Physical)[sample], wanted.Valid[sample], batch.GetValue(row, column, 0));
                            continue;
                        }
                        string[] elements = (raw ? wanted.ElementRaw : wanted.ElementPhysical)[sample];
                        Assert.Equal((ulong)elements.Length, channel.Array.ElementCount);
                        Assert.Throws<InvalidOperationException>(() => batch.GetValue(row, column));
                        for (int element = 0; element < elements.Length; element++)
                        {
                            Same(kind, elements[element], wanted.Valid[sample], batch.GetValue(row, column, (ulong)element));
                        }
                        Assert.Throws<ArgumentOutOfRangeException>(() => batch.GetValue(row, column, (ulong)elements.Length));
                    }
                }
                ordinal += batch.SampleCount;
            }
            Assert.Equal(start + count, ordinal);
        }

        private static void Same(MdfValueKind kind, string text, bool valid, MdfSampleValue actual)
        {
            Assert.Equal(kind, actual.Kind);
            Assert.Equal(valid, actual.IsValid);
            if (kind == MdfValueKind.UnsignedInteger) { Assert.Equal(ulong.Parse(text, CultureInfo.InvariantCulture), actual.UnsignedValue); }
            else if (kind == MdfValueKind.SignedInteger) { Assert.Equal(long.Parse(text, CultureInfo.InvariantCulture), actual.SignedValue); }
            else
            {
                Assert.Equal(BitConverter.DoubleToInt64Bits(double.Parse(text, CultureInfo.InvariantCulture)),
                    BitConverter.DoubleToInt64Bits(actual.FloatingPointValue));
            }
        }

        [Fact]
        public void M5VSupersedesExactlyFiveFrozenM5IOutcomes()
        {
            ExpectedSupersession set = Assert.Single(Load("m5v-expectations.json").Supersedes);
            Assert.Equal("m5i-unsupported330", set.Id);
            Assert.Equal(new[] { "gap", "mixed", "conv", "packed", "nest" }, set.Channels.Select(c => c.Key));
            ExpectedCase frozen = Load("m5i-expectations.json").Cases.Single(c => c.Id == set.Id);
            ExpectedCase current = ReadCase(set.Id);
            int changed = 0;
            for (int index = 0; index < frozen.Groups[0].Channels.Count; index++)
            {
                ExpectedChannel before = frozen.Groups[0].Channels[index];
                ExpectedChannel after = current.Groups[0].Channels[index];
                if (before.Readable == after.Readable && before.Reason == after.Reason) { continue; }
                changed++;
                Assert.Contains(set.Channels, c => c.Index == index);
            }
            Assert.Equal(5, changed);
            // The now-readable gap array is exactly its two element scalars.
            ExpectedChannel gap = current.Groups[0].Channels[set.Channels[0].Index];
            Assert.True(gap.Readable);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(set.Id), false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                MdfSampleBatch batch = reader.ReadSamples(group, new[] { gap.Index }.Concat(gap.Dependency.References).ToArray()).Single();
                for (int row = 0; row < batch.SampleCount; row++)
                {
                    for (int element = 0; element < gap.Dependency.References.Length; element++)
                    {
                        Assert.Equal(batch.GetValue(row, 1 + element).UnsignedValue, batch.GetValue(row, 0, (ulong)element).UnsignedValue);
                    }
                }
            }
        }

        [Theory]
        [InlineData("m5v-packed-le330")]
        [InlineData("m5v-interleaved-be330")]
        [InlineData("m5i-unsupported330")]
        public void EveryReadableArrayElementIsItsReferencedScalarChannel(string id)
        {
            ExpectedCase expected = ReadCase(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                IReadOnlyList<MdfDataGroup> catalog = reader.ReadMetadata();
                foreach (ExpectedGroup values in expected.Groups)
                {
                    MdfChannelGroup group = catalog[values.DataGroup].ChannelGroups[values.ChannelGroup];
                    foreach (ExpectedChannel parent in values.Channels.Where(c => c.Dimensions != null && c.Readable))
                    {
                        int[] references = parent.Dependency.References;
                        if (references.Any(r => !values.Channels[r].Readable)) { continue; }
                        foreach (MdfValueRepresentation representation in new[] { MdfValueRepresentation.Raw, MdfValueRepresentation.Physical })
                        foreach (MdfSampleBatch batch in reader.ReadSamples(group, new[] { parent.Index }.Concat(references).ToArray(), batchSize: 2, representation: representation))
                        {
                            for (int row = 0; row < batch.SampleCount; row++)
                            {
                                for (int element = 0; element < references.Length; element++)
                                {
                                    Identical(batch.GetValue(row, 1 + element), batch.GetValue(row, 0, (ulong)element));
                                }
                            }
                        }
                    }
                }
            }
        }

        private static void Identical(MdfSampleValue wanted, MdfSampleValue actual)
        {
            Assert.Equal(wanted.Kind, actual.Kind);
            Assert.Equal(wanted.IsValid, actual.IsValid);
            if (wanted.Kind == MdfValueKind.UnsignedInteger) { Assert.Equal(wanted.UnsignedValue, actual.UnsignedValue); }
            else if (wanted.Kind == MdfValueKind.SignedInteger) { Assert.Equal(wanted.SignedValue, actual.SignedValue); }
            else { Assert.Equal(BitConverter.DoubleToInt64Bits(wanted.FloatingPointValue), BitConverter.DoubleToInt64Bits(actual.FloatingPointValue)); }
        }

        private static ExpectedFile Load(string name)
        {
            using (FileStream stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, name)))
            { return (ExpectedFile)new DataContractJsonSerializer(typeof(ExpectedFile)).ReadObject(stream); }
        }

        private static ExpectedCase ReadCase(string id)
        {
            ExpectedCase found = Load(id.StartsWith("m5v-", StringComparison.Ordinal) ? "m5v-expectations.json" : "m5i-expectations.json")
                .Cases.Single(c => c.Id == id);
            // supersedes these frozen outcomes.
            foreach (ExpectedSupersession set in Load("m5v-expectations.json").Supersedes.Where(s => s.Id == id))
            {
                foreach (ExpectedChannel change in set.Channels)
                {
                    ExpectedChannel target = found.Groups[set.GroupIndex].Channels[change.Index];
                    Assert.Equal(change.M5iReason, target.Reason);
                    target.Readable = change.Readable;
                    target.Reason = change.Reason;
                    target.ElementRaw = change.ElementRaw;
                    target.ElementPhysical = change.ElementPhysical;
                    target.Valid = change.Valid;
                }
            }
            return found;
        }
        [DataContract]
        private sealed class ExpectedFile
        {
            [DataMember(Name = "cases")] public List<ExpectedCase> Cases { get; set; }
            [DataMember(Name = "supersedes")] public List<ExpectedSupersession> Supersedes { get; set; }
        }
        [DataContract]
        private sealed class ExpectedSupersession
        {
            [DataMember(Name = "id")] public string Id { get; set; }
            [DataMember(Name = "groupIndex")] public int GroupIndex { get; set; }
            [DataMember(Name = "channels")] public List<ExpectedChannel> Channels { get; set; }
        }
        [DataContract]
        private sealed class ExpectedDependency
        {
            [DataMember(Name = "references")] public int[] References { get; set; }
        }
        [DataContract]
        private sealed class ExpectedCase
        {
            [DataMember(Name = "id")] public string Id { get; set; }
            [DataMember(Name = "version")] public string Version { get; set; }
            [DataMember(Name = "groups")] public List<ExpectedGroup> Groups { get; set; }
            [DataMember(Name = "metadataError")] public ExpectedError MetadataError { get; set; }
        }
        [DataContract]
        private sealed class ExpectedError
        {
            [DataMember(Name = "diagnostic")] public string Diagnostic { get; set; }
        }
        [DataContract]
        private sealed class ExpectedGroup
        {
            [DataMember(Name = "dataGroupIndex")] public int DataGroup { get; set; }
            [DataMember(Name = "channelGroupIndex")] public int ChannelGroup { get; set; }
            [DataMember(Name = "samples")] public int Samples { get; set; }
            [DataMember(Name = "dataBytes")] public int DataBytes { get; set; }
            [DataMember(Name = "channels")] public List<ExpectedChannel> Channels { get; set; }
        }
        [DataContract]
        private sealed class ExpectedChannel
        {
            [DataMember(Name = "key")] public string Key { get; set; }
            [DataMember(Name = "m5iReason")] public string M5iReason { get; set; }
            [DataMember(Name = "dependency")] public ExpectedDependency Dependency { get; set; }
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "index")] public int Index { get; set; }
            [DataMember(Name = "channelType")] public int ChannelType { get; set; }
            [DataMember(Name = "readable")] public bool Readable { get; set; }
            [DataMember(Name = "reason")] public string Reason { get; set; }
            [DataMember(Name = "kind")] public string Kind { get; set; }
            [DataMember(Name = "conversion")] public string Conversion { get; set; }
            [DataMember(Name = "bitCount")] public uint Bits { get; set; }
            [DataMember(Name = "dimensions")] public ulong[] Dimensions { get; set; }
            [DataMember(Name = "raw")] public string[] Raw { get; set; }
            [DataMember(Name = "physical")] public string[] Physical { get; set; }
            [DataMember(Name = "elementRaw")] public string[][] ElementRaw { get; set; }
            [DataMember(Name = "elementPhysical")] public string[][] ElementPhysical { get; set; }
            [DataMember(Name = "valid")] public bool[] Valid { get; set; }
        }
    }
}
