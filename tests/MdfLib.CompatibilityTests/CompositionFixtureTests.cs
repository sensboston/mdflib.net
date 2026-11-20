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
    // Every expectation below was frozen before the parser existed (tools/fixtures/m5e-expectations.json).
    public sealed class CompositionFixtureTests
    {
        private static readonly string[] Ids = { "m5e-structure410", "m5e-lists411", "m5e-paired420", "m5e-interleaved410",
            "m5e-interleaved411", "m5e-asammdf410", "m5e-asammdf411", "m5e-asammdf420" };

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
        public void CatalogKeepsTopLevelIndicesAppendsPreorderComponentsAndExposesTheFrozenTree(string id)
        {
            ExpectedCase expected = ReadCase(id);
            IReadOnlyList<MdfDataGroup> catalog;
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                Assert.Equal(expected.Version, reader.Header.Version);
                catalog = reader.ReadMetadata();
                Assert.Same(catalog, reader.ReadMetadata());
            }
            foreach (ExpectedGroup values in expected.Groups)
            {
                MdfChannelGroup group = catalog[values.DataGroup].ChannelGroups[values.ChannelGroup];
                Assert.True(group.CanReadSamples, group.UnsupportedReason);
                Assert.Equal(values.Channels.Count, group.Channels.Count);
                for (int index = 0; index < values.Channels.Count; index++)
                {
                    ExpectedChannel wanted = values.Channels[index];
                    MdfChannel channel = group.Channels[index];
                    Assert.Equal(wanted.Index, channel.Index);
                    Assert.Equal(index, channel.Index);
                    Assert.Equal(wanted.Name, channel.Name);
                    Assert.Equal(wanted.Bits, channel.BitCount);
                    // supersedes the frozen parent refusal (expectations unchanged): parents read their window.
                    bool parent = wanted.Components.Length != 0;
                    if (parent) { Assert.Equal("Unsupported", wanted.Kind); Assert.False(wanted.Readable); }
                    Assert.Equal(parent ? MdfValueKind.ByteArray : (MdfValueKind)Enum.Parse(typeof(MdfValueKind), wanted.Kind), channel.ValueKind);
                    if (wanted.Parent < 0) { Assert.Null(channel.Parent); }
                    else { Assert.Same(group.Channels[wanted.Parent], channel.Parent); }
                    Assert.Equal(wanted.Components, channel.Components.Select(c => c.Index));
                    Assert.All(channel.Components, c => Assert.Same(group.Channels[c.Index], c));
                    Assert.Equal(wanted.Readable || parent, channel.CanReadRaw);
                    Assert.Equal(wanted.Readable || parent, channel.CanReadPhysical);
                    Assert.Null(channel.UnsupportedReason);
                    Assert.Equal(wanted.Shape.Length != 0, channel.Array != null);
                    if (channel.Array != null) { Assert.Equal(wanted.Shape, channel.Array.Dimensions); }
                    Assert.Throws<NotSupportedException>(() => ((IList<MdfChannel>)channel.Components).Add(channel));
                }
            }
        }

        [Theory]
        [MemberData(nameof(FullReads))]
        public void EveryReadableLeafMatchesFrozenTypedValuesMasksAndShapes(string id, int batchSize, object representationValue)
        {
            MdfValueRepresentation representation = (MdfValueRepresentation)representationValue;
            ExpectedCase expected = ReadCase(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                IReadOnlyList<MdfDataGroup> catalog = reader.ReadMetadata();
                foreach (ExpectedGroup values in expected.Groups)
                {
                    MdfChannelGroup group = catalog[values.DataGroup].ChannelGroups[values.ChannelGroup];
                    Assert.Equal(values.Samples, (int)group.SampleCount);
                    Assert.Equal(values.DataBytes + values.InvalidationBytes, (int)group.RecordByteCount);
                    int[] selected = values.Channels.Where(c => c.Readable).Select(c => c.Index).ToArray();
                    Check(reader.ReadSamples(group, selected, batchSize: batchSize, representation: representation), values, selected, 0, values.Samples, representation);
                }
            }
        }

        [Theory]
        [MemberData(nameof(AllFiles))]
        public void ReorderedLeafAndTopLevelSelectionsMatchForEverySubrangeAndParentsAreNeverExpanded(string id)
        {
            ExpectedCase expected = ReadCase(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                IReadOnlyList<MdfDataGroup> catalog = reader.ReadMetadata();
                foreach (ExpectedGroup values in expected.Groups)
                {
                    MdfChannelGroup group = catalog[values.DataGroup].ChannelGroups[values.ChannelGroup];
                    int[] selected = values.Channels.Where(c => c.Readable).Select(c => c.Index).Reverse().ToArray();
                    foreach (MdfValueRepresentation representation in new[] { MdfValueRepresentation.Raw, MdfValueRepresentation.Physical })
                    foreach (int batch in new[] { 1, 3, 1024 })
                    for (int start = 0; start <= values.Samples; start++)
                    for (int count = 0; count <= values.Samples - start; count++)
                    {
                        Check(reader.ReadSamples(group, selected, (ulong)start, (ulong)count, batch, representation), values, selected, start, count, representation);
                    }
                    foreach (ExpectedChannel parent in values.Channels.Where(c => !c.Readable))
                    {
                        // a parent selection yields its own window and is never expanded into components.
                        Assert.NotEmpty(parent.Components);
                        List<MdfSampleBatch> batches = reader.ReadSamples(group, new[] { 0, parent.Index }, batchSize: 2).ToList();
                        Assert.Equal(values.Samples, batches.Sum(b => b.SampleCount));
                        foreach (MdfSampleBatch batch in batches)
                        {
                            Assert.Equal(new[] { 0, parent.Index }, batch.Channels.Select(c => c.Index));
                            for (int row = 0; row < batch.SampleCount; row++)
                            {
                                MdfSampleValue value = batch.GetValue(row, 1);
                                Assert.Equal(MdfValueKind.ByteArray, value.Kind);
                                Assert.Equal(parent.Bits / 8, (uint)value.GetByteArrayValue().Length);
                            }
                        }
                    }
                }
            }
        }

        [Fact]
        public void TheGraphAndMaterializedLeafBatchesRemainUsableAfterDispose()
        {
            MdfChannelGroup group;
            List<MdfSampleBatch> batches;
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read("m5e-structure410"), false)))
            {
                group = reader.ReadMetadata()[0].ChannelGroups[0];
                batches = reader.ReadSamples(group, new[] { 7, 5 }, batchSize: 2).ToList();
            }
            MdfChannel packet = group.Channels[2];
            Assert.Equal(new[] { 5, 6, 11, 12 }, packet.Components.Select(c => c.Index));
            Assert.Same(group.Channels[6], group.Channels[7].Parent);
            Assert.Same(packet, group.Channels[6].Parent);
            Assert.Equal(-32768L, batches[0].GetValue(0, 0).SignedValue);
            Assert.Equal(9007199254740993UL, batches[0].GetValue(1, 1).UnsignedValue);
            Assert.Equal(5, batches.Sum(b => b.SampleCount));
        }

        [Fact]
        public void TheRetainedRejectedProducerOutputFailsWithItsFrozenDiagnosticAndPublishesNoCatalog()
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read("m5e-rejected-subarray410"), false)))
            {
                Assert.Equal("MDF013", Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
                Assert.Equal("MDF013", Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
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
                    Assert.Equal(wanted.Name, channel.Name);
                    MdfValueKind kind = representation == MdfValueRepresentation.Physical && channel.Conversion.Kind == MdfConversionKind.Linear
                        ? MdfValueKind.FloatingPoint : (MdfValueKind)Enum.Parse(typeof(MdfValueKind), wanted.Kind);
                    for (int row = 0; row < batch.SampleCount; row++)
                    for (ulong element = 0; element < wanted.Elements; element++)
                    {
                        MdfSampleValue actual = channel.Array == null && element == 0 ? batch.GetValue(row, column) : batch.GetValue(row, column, element);
                        string text = (representation == MdfValueRepresentation.Raw ? wanted.Raw : wanted.Physical)[ordinal + row][(int)element];
                        Assert.Equal(kind, actual.Kind);
                        Assert.Equal(wanted.Valid[ordinal + row], actual.IsValid);
                        if (kind == MdfValueKind.UnsignedInteger) { Assert.Equal(ulong.Parse(text, CultureInfo.InvariantCulture), actual.UnsignedValue); }
                        else if (kind == MdfValueKind.SignedInteger) { Assert.Equal(long.Parse(text, CultureInfo.InvariantCulture), actual.SignedValue); }
                        else
                        {
                            double value = Real(text);
                            if (double.IsNaN(value)) { Assert.True(double.IsNaN(actual.FloatingPointValue)); }
                            else { Assert.Equal(BitConverter.DoubleToInt64Bits(value), BitConverter.DoubleToInt64Bits(actual.FloatingPointValue)); }
                        }
                    }
                }
                ordinal += batch.SampleCount;
            }
            Assert.Equal(start + count, ordinal);
        }

        private static double Real(string value)
        {
            if (value == "NaN") { return double.NaN; }
            if (value == "Infinity") { return double.PositiveInfinity; }
            if (value == "-Infinity") { return double.NegativeInfinity; }
            if (value == "-0") { return BitConverter.Int64BitsToDouble(long.MinValue); }
            return double.Parse(value, CultureInfo.InvariantCulture);
        }

        private static ExpectedCase ReadCase(string id)
        {
            using (FileStream stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "m5e-expectations.json")))
            { return ((ExpectedFile)new DataContractJsonSerializer(typeof(ExpectedFile)).ReadObject(stream)).Cases.Single(c => c.Id == id); }
        }
        [DataContract]
        private sealed class ExpectedFile
        {
            [DataMember(Name = "cases")] public List<ExpectedCase> Cases { get; set; }
        }
        [DataContract]
        private sealed class ExpectedCase
        {
            [DataMember(Name = "id")] public string Id { get; set; }
            [DataMember(Name = "version")] public string Version { get; set; }
            [DataMember(Name = "groups")] public List<ExpectedGroup> Groups { get; set; }
        }
        [DataContract]
        private sealed class ExpectedGroup
        {
            [DataMember(Name = "dataGroupIndex")] public int DataGroup { get; set; }
            [DataMember(Name = "channelGroupIndex")] public int ChannelGroup { get; set; }
            [DataMember(Name = "samples")] public int Samples { get; set; }
            [DataMember(Name = "dataBytes")] public int DataBytes { get; set; }
            [DataMember(Name = "invalidationBytes")] public int InvalidationBytes { get; set; }
            [DataMember(Name = "channels")] public List<ExpectedChannel> Channels { get; set; }
        }
        [DataContract]
        private sealed class ExpectedChannel
        {
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "index")] public int Index { get; set; }
            [DataMember(Name = "parent")] public int Parent { get; set; }
            [DataMember(Name = "components")] public int[] Components { get; set; }
            [DataMember(Name = "readable")] public bool Readable { get; set; }
            [DataMember(Name = "kind")] public string Kind { get; set; }
            [DataMember(Name = "shape")] public ulong[] Shape { get; set; }
            [DataMember(Name = "elementCount")] public ulong Elements { get; set; }
            [DataMember(Name = "bitCount")] public uint Bits { get; set; }
            [DataMember(Name = "raw")] public string[][] Raw { get; set; }
            [DataMember(Name = "physical")] public string[][] Physical { get; set; }
            [DataMember(Name = "valid")] public bool[] Valid { get; set; }
        }
    }
}
