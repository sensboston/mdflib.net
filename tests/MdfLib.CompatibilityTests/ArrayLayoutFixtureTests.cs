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
    // every expectation below was frozen before the padded/inverse/per-element array decoder existed
    // (tools/fixtures/m5k-expectations.json).
    public sealed class ArrayLayoutFixtureTests
    {
        private static readonly string[] Ids = { "m5k-layouts410", "m5k-lists411", "m5k-paired420", "m5k-interleaved420" };

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
        public void CatalogShapesAreExactlyTheFrozenOnesAndEveryLayoutIsReadable(string id)
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
                    Assert.Equal((ulong)(values.DataBytes + values.InvalidationBytes), group.RecordByteCount);
                    Assert.Equal(values.Channels.Count, group.Channels.Count);
                    for (int index = 0; index < values.Channels.Count; index++)
                    {
                        ExpectedChannel wanted = values.Channels[index];
                        MdfChannel channel = group.Channels[index];
                        Assert.Equal(wanted.Index, channel.Index);
                        Assert.Equal(wanted.Name, channel.Name);
                        Assert.Equal((uint)wanted.Bits, channel.BitCount);
                        Assert.Equal((MdfValueKind)Enum.Parse(typeof(MdfValueKind), wanted.Kind), channel.ValueKind);
                        Assert.Equal(wanted.Name == "inv_pad_u8" ? MdfConversionKind.Linear : MdfConversionKind.Identity, channel.Conversion.Kind);
                        Assert.True(channel.CanReadRaw, channel.UnsupportedReason);
                        Assert.True(channel.CanReadPhysical, channel.UnsupportedReason);
                        Assert.Null(channel.UnsupportedReason);
                        if (wanted.Shape.Count == 0) { Assert.Null(channel.Array); }
                        else
                        {
                            Assert.Equal(wanted.Shape, channel.Array.Dimensions);
                            Assert.Equal((ulong)wanted.ElementCount, channel.Array.ElementCount);
                        }
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(FullReads))]
        public void EveryElementValueAndPerElementValidityMatchesTheFrozenValues(string id, int batchSize, object representationValue)
        {
            MdfValueRepresentation representation = (MdfValueRepresentation)representationValue;
            ExpectedCase expected = ReadCase(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                IReadOnlyList<MdfDataGroup> catalog = reader.ReadMetadata();
                foreach (ExpectedGroup values in expected.Groups)
                {
                    MdfChannelGroup group = catalog[values.DataGroup].ChannelGroups[values.ChannelGroup];
                    int[] selected = Enumerable.Range(0, values.Channels.Count).ToArray();
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
                    List<int[]> selections = new List<int[]> { Enumerable.Range(0, values.Channels.Count).Reverse().ToArray() };
                    selections.AddRange(values.Channels.Where(c => c.Shape.Count != 0).Select(c => new[] { c.Index }));
                    foreach (int[] selected in selections)
                    foreach (int batch in new[] { 1, 3, 1024 })
                    for (int start = 0; start <= values.Samples; start++)
                    for (int count = 0; count <= values.Samples - start; count++)
                    {
                        Check(reader.ReadSamples(group, selected, (ulong)start, (ulong)count, batch), values, selected, start, count, MdfValueRepresentation.Raw);
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(AllFiles))]
        public void StrayInvalidationBitsAreNeverAddressedAndAllInvalidStaysInvalid(string id)
        {
            ExpectedCase expected = ReadCase(id);
            Assert.Equal(new[] { 7, 9, 11 }, expected.StrayBits.ToArray());
            ExpectedGroup values = expected.Groups[0];
            ExpectedChannel all = values.Channels.Single(c => c.Name == "allinv_u8");
            Assert.Equal(1, all.CnFlags);
            Assert.True(all.Valid.SelectMany(row => row).All(valid => !valid));
            foreach (ExpectedChannel channel in values.Channels.Where(c => c.InvalidationBase != 0 && c.CnFlags == 2))
            for (int element = 0; element < channel.ElementCount; element++)
            {
                Assert.DoesNotContain(channel.InvalidationBit + channel.InvalidationBase * element, expected.StrayBits);
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
                    bool raw = representation == MdfValueRepresentation.Raw;
                    MdfValueKind kind = !raw && wanted.Name == "inv_pad_u8"
                        ? MdfValueKind.FloatingPoint : (MdfValueKind)Enum.Parse(typeof(MdfValueKind), wanted.Kind);
                    for (int row = 0; row < batch.SampleCount; row++)
                    {
                        int sample = ordinal + row;
                        List<string> elements = (raw ? wanted.Raw : wanted.Physical)[sample];
                        List<bool> valid = wanted.Valid[sample];
                        Assert.Equal(wanted.ElementCount, elements.Count);
                        if (wanted.Shape.Count == 0)
                        {
                            Assert.Null(channel.Array);
                            Same(kind, elements[0], valid[0], batch.GetValue(row, column));
                            Same(kind, elements[0], valid[0], batch.GetValue(row, column, 0));
                            continue;
                        }
                        Assert.Throws<InvalidOperationException>(() => batch.GetValue(row, column));
                        for (int element = 0; element < elements.Count; element++)
                        {
                            Same(kind, elements[element], valid[element], batch.GetValue(row, column, (ulong)element));
                        }
                        Assert.Throws<ArgumentOutOfRangeException>(() => batch.GetValue(row, column, (ulong)elements.Count));
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
            else { Assert.Equal(BitConverter.DoubleToInt64Bits(Real(text)), BitConverter.DoubleToInt64Bits(actual.FloatingPointValue)); }
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
            using (FileStream stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "m5k-expectations.json")))
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
            [DataMember(Name = "strayInvalidationBits")] public List<int> StrayBits { get; set; }
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
            [DataMember(Name = "kind")] public string Kind { get; set; }
            [DataMember(Name = "bitCount")] public int Bits { get; set; }
            [DataMember(Name = "shape")] public List<ulong> Shape { get; set; }
            [DataMember(Name = "elementCount")] public int ElementCount { get; set; }
            [DataMember(Name = "cnFlags")] public int CnFlags { get; set; }
            [DataMember(Name = "invalidationBit")] public int InvalidationBit { get; set; }
            [DataMember(Name = "invalidationBase")] public int InvalidationBase { get; set; }
            [DataMember(Name = "raw")] public List<List<string>> Raw { get; set; }
            [DataMember(Name = "physical")] public List<List<string>> Physical { get; set; }
            [DataMember(Name = "valid")] public List<List<bool>> Valid { get; set; }
        }
    }
}
