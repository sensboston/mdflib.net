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
    public sealed class FixedArrayFixtureTests
    {
        private static readonly string[] Ids = { "m5d-arrays410", "m5d-lists411", "m5d-arrays420", "m5d-paired420",
            "m5d-interleaved410", "m5d-interleaved411", "m5d-asammdf410", "m5d-asammdf411", "m5d-asammdf420" };

        public static IEnumerable<object[]> FullReads()
        {
            foreach (string id in Ids)
            foreach (int batch in new[] { 1, 2, 1024 })
            foreach (MdfValueRepresentation representation in new[] { MdfValueRepresentation.Raw, MdfValueRepresentation.Physical })
            { yield return new object[] { id, batch, representation }; }
        }
        public static IEnumerable<object[]> AllFiles() { return Ids.Select(id => new object[] { id }); }

        [Theory]
        [MemberData(nameof(FullReads))]
        public void ActualReaderMatchesEveryFrozenPreParserTypedElementShapeAndMask(string id, int batchSize, object representationValue)
        {
            MdfValueRepresentation representation = (MdfValueRepresentation)representationValue;
            ExpectedCase expected = ReadCase(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                Assert.Equal(expected.Version, reader.Header.Version);
                IReadOnlyList<MdfDataGroup> catalog = reader.ReadMetadata();
                foreach (ExpectedGroup values in expected.Groups)
                {
                    MdfChannelGroup group = catalog[values.DataGroup].ChannelGroups[values.ChannelGroup];
                    Assert.Equal(values.Samples, (int)group.SampleCount);
                    Assert.Equal(values.DataBytes + values.InvalidationBytes, (int)group.RecordByteCount);
                    Assert.Equal(values.Channels.Count, group.Channels.Count);
                    int[] selected = Enumerable.Range(0, group.Channels.Count).ToArray();
                    Check(reader.ReadSamples(group, selected, batchSize: batchSize, representation: representation), values, selected, 0, values.Samples, representation);
                }
            }
        }

        [Theory]
        [MemberData(nameof(AllFiles))]
        public void AllSubrangesMixedReorderedColumnsEmptyRangesAndBatchSizesMatchUnchangedExpectations(string id)
        {
            ExpectedCase expected = ReadCase(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                IReadOnlyList<MdfDataGroup> catalog = reader.ReadMetadata();
                foreach (ExpectedGroup values in expected.Groups)
                {
                    MdfChannelGroup group = catalog[values.DataGroup].ChannelGroups[values.ChannelGroup];
                    int[] selected = group.Channels.Count == 1 ? new[] { 0 } : new[] { 23, 1, 8, 2, 22, 0, 19 };
                    foreach (MdfValueRepresentation representation in new[] { MdfValueRepresentation.Raw, MdfValueRepresentation.Physical })
                    foreach (int batch in new[] { 1, 2, 1024 })
                    for (int start = 0; start <= values.Samples; start++)
                    for (int count = 0; count <= values.Samples - start; count++)
                    {
                        Check(reader.ReadSamples(group, selected, (ulong)start, (ulong)count, batch, representation), values, selected, start, count, representation);
                    }
                }
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
                    Assert.Equal(wanted.Bits, channel.BitCount);
                    Assert.Equal(wanted.Shape.Length != 0, channel.Array != null);
                    if (channel.Array != null)
                    {
                        Assert.Equal(wanted.Shape, channel.Array.Dimensions);
                        Assert.Equal(wanted.Elements, channel.Array.ElementCount);
                        Assert.Throws<InvalidOperationException>(() => batch.GetValue(0, column));
                    }
                    MdfValueKind kind = representation == MdfValueRepresentation.Physical && channel.Conversion.Kind == MdfConversionKind.Linear
                        ? MdfValueKind.FloatingPoint : (MdfValueKind)Enum.Parse(typeof(MdfValueKind), wanted.Kind);
                    for (int row = 0; row < batch.SampleCount; row++)
                    for (ulong element = 0; element < wanted.Elements; element++)
                    {
                        MdfSampleValue actual = batch.GetValue(row, column, element);
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
                        if (channel.Array == null)
                        { Assert.Equal(actual.Kind, batch.GetValue(row, column).Kind); }
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
            using (FileStream stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "m5d-expectations.json")))
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
