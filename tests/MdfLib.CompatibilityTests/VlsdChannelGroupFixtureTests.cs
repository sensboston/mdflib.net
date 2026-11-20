// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using MdfLib.TestSupport;
using Xunit;

namespace MdfLib.CompatibilityTests
{
    // Every expectation below was frozen before the parser existed (tools/fixtures/m5j-expectations.json).
    public sealed class VlsdChannelGroupFixtureTests
    {
        private static readonly string[] Ids = { "m5j-id1-dt410", "m5j-id2-dl411", "m5j-id4-hl420", "m5j-shared-id8-dz410" };

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
        public void CatalogListsVlsdGroupsAndParentsExactlyAsFrozen(string id)
        {
            ExpectedCase expected = ReadCase(id);
            IReadOnlyList<MdfDataGroup> catalog;
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                Assert.Equal(expected.Version, reader.Header.Version);
                catalog = reader.ReadMetadata();
            }
            Assert.Single(catalog);
            Assert.Equal(expected.Groups.Count, catalog[0].ChannelGroups.Count);
            foreach (ExpectedGroup values in expected.Groups)
            {
                MdfChannelGroup group = catalog[values.DataGroup].ChannelGroups[values.ChannelGroup];
                Assert.Equal(values.ChannelGroup, group.Index);
                Assert.Equal((ulong)values.Samples, group.SampleCount);
                Assert.Equal(values.Channels.Count, group.Channels.Count);
                if (values.Vlsd)
                {
                    Assert.False(group.CanReadSamples);
                    Assert.Equal(values.Reason, group.UnsupportedReason);
                    Assert.Equal(0UL, group.RecordByteCount);
                    continue;
                }
                Assert.True(group.CanReadSamples, group.UnsupportedReason);
                Assert.Equal((ulong)(values.DataBytes + values.InvalidationBytes), group.RecordByteCount);
                for (int index = 0; index < values.Channels.Count; index++)
                {
                    ExpectedChannel wanted = values.Channels[index];
                    MdfChannel channel = group.Channels[index];
                    Assert.Equal(wanted.Index, channel.Index);
                    Assert.Equal(wanted.Name, channel.Name);
                    Assert.Equal(wanted.Bits, channel.BitCount);
                    Assert.Equal(wanted.ChannelType == 2, channel.IsMaster);
                    Assert.Equal((MdfValueKind)Enum.Parse(typeof(MdfValueKind), wanted.Kind), channel.ValueKind);
                    Assert.True(channel.CanReadRaw, channel.UnsupportedReason);
                }
            }
        }

        [Theory]
        [MemberData(nameof(FullReads))]
        public void EveryReadableChannelMatchesFrozenBytesTextUnitsAndValidity(string id, int batchSize, object representationValue)
        {
            MdfValueRepresentation representation = (MdfValueRepresentation)representationValue;
            ExpectedCase expected = ReadCase(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                IReadOnlyList<MdfDataGroup> catalog = reader.ReadMetadata();
                foreach (ExpectedGroup values in expected.Groups.Where(g => !g.Vlsd))
                {
                    MdfChannelGroup group = catalog[values.DataGroup].ChannelGroups[values.ChannelGroup];
                    int[] selected = values.Channels.Select(c => c.Index).ToArray();
                    Check(reader.ReadSamples(group, selected, batchSize: batchSize, representation: representation), values, selected, 0, values.Samples);
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
                foreach (ExpectedGroup values in expected.Groups.Where(g => !g.Vlsd))
                {
                    MdfChannelGroup group = catalog[values.DataGroup].ChannelGroups[values.ChannelGroup];
                    int[] all = values.Channels.Select(c => c.Index).ToArray();
                    List<int[]> selections = new List<int[]> { all.Reverse().ToArray() };
                    selections.AddRange(all.Select(index => new[] { index }));
                    foreach (int[] selected in selections)
                    foreach (int batch in new[] { 1, 3, 1024 })
                    for (int start = 0; start <= values.Samples; start++)
                    for (int count = 0; count <= values.Samples - start; count++)
                    {
                        Check(reader.ReadSamples(group, selected, (ulong)start, (ulong)count, batch), values, selected, start, count);
                    }
                }
                foreach (ExpectedGroup values in expected.Groups.Where(g => g.Vlsd))
                {
                    MdfChannelGroup vlsd = catalog[values.DataGroup].ChannelGroups[values.ChannelGroup];
                    Assert.Throws<ArgumentException>(() => reader.ReadSamples(vlsd, new[] { 0 }));
                }
            }
        }

        [Theory]
        [MemberData(nameof(AllFiles))]
        public void TinyBatchCapsSplitVariableRowsWithoutLosingOrReorderingAny(string id)
        {
            ExpectedCase expected = ReadCase(id);
            ExpectedGroup values = expected.Groups.Single(g => !g.Vlsd && g.Channels.Count == 5);
            int[] selected = values.Channels.Select(c => c.Index).ToArray();
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false), false,
                new MdfReaderOptions { MaximumSampleBatchBytes = 2048 }))
            {
                MdfChannelGroup group = reader.ReadMetadata()[values.DataGroup].ChannelGroups[values.ChannelGroup];
                List<MdfSampleBatch> batches = reader.ReadSamples(group, selected, batchSize: 1024).ToList();
                Assert.True(batches.Count > 1);
                Check(batches, values, selected, 0, values.Samples);
            }
        }

        private static void Check(IEnumerable<MdfSampleBatch> batches, ExpectedGroup expected, int[] selected, int start, int count)
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
                    MdfValueKind kind = (MdfValueKind)Enum.Parse(typeof(MdfValueKind), wanted.Kind);
                    for (int row = 0; row < batch.SampleCount; row++)
                    {
                        MdfSampleValue actual = batch.GetValue(row, column);
                        int sample = ordinal + row;
                        Assert.Equal(kind, actual.Kind);
                        Assert.Equal(wanted.Valid[sample], actual.IsValid);
                        if (kind == MdfValueKind.Text || kind == MdfValueKind.ByteArray)
                        {
                            Assert.Equal(wanted.Bytes[sample], Hex(actual.GetByteArrayValue()));
                            if (kind == MdfValueKind.Text) { Assert.Equal(wanted.Text[sample], actual.TextValue.Select(c => (int)c).ToArray()); }
                            else { Assert.Throws<InvalidOperationException>(() => actual.TextValue); }
                        }
                        else if (kind == MdfValueKind.UnsignedInteger)
                        { Assert.Equal(ulong.Parse(wanted.Raw[sample], CultureInfo.InvariantCulture), actual.UnsignedValue); }
                        else
                        {
                            Assert.Equal(BitConverter.DoubleToInt64Bits(double.Parse(wanted.Raw[sample], CultureInfo.InvariantCulture)),
                                BitConverter.DoubleToInt64Bits(actual.FloatingPointValue));
                        }
                    }
                }
                ordinal += batch.SampleCount;
            }
            Assert.Equal(start + count, ordinal);
        }

        private static string Hex(byte[] bytes)
        {
            StringBuilder builder = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes) { builder.Append(value.ToString("x2", CultureInfo.InvariantCulture)); }
            return builder.ToString();
        }

        private static ExpectedCase ReadCase(string id)
        {
            using (FileStream stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "m5j-expectations.json")))
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
            [DataMember(Name = "vlsd")] public bool Vlsd { get; set; }
            [DataMember(Name = "reason")] public string Reason { get; set; }
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
            [DataMember(Name = "channelType")] public int ChannelType { get; set; }
            [DataMember(Name = "kind")] public string Kind { get; set; }
            [DataMember(Name = "bitCount")] public uint Bits { get; set; }
            [DataMember(Name = "raw")] public string[] Raw { get; set; }
            [DataMember(Name = "bytes")] public string[] Bytes { get; set; }
            [DataMember(Name = "text")] public int[][] Text { get; set; }
            [DataMember(Name = "valid")] public bool[] Valid { get; set; }
        }
    }
}
