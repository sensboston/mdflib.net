// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Xml;
using System.Xml.Linq;
using MdfLib.TestSupport;
using Xunit;

namespace MdfLib.CompatibilityTests
{
    // every expectation below was frozen before the LD signal-data change existed
    // (tools/fixtures/m5r-expectations.json): VLSD signal data through 4.20 LD (and HL -> LD) lists.
    public sealed class LdSignalDataFixtureTests
    {
        private static readonly string[] Ids = { "m5r-ld-sd420", "m5r-hl-ld-sd420", "m5r-unsorted420", "m5r-ld-sd411" };

        public static IEnumerable<object[]> FullReads()
        {
            foreach (string id in Ids)
            foreach (int batch in new[] { 1, 2, 1024 })
            { yield return new object[] { id, batch }; }
        }
        public static IEnumerable<object[]> AllFiles() { return Ids.Select(id => new object[] { id }); }

        [Theory]
        [MemberData(nameof(AllFiles))]
        public void CatalogPublishesLdListedVlsdChannelsAndRefusesPre420Roots(string id)
        {
            XElement expected = ReadCase(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                Assert.Equal(Text(expected, "version"), reader.Header.Version);
                MdfDataGroup dataGroup = Assert.Single(reader.ReadMetadata());
                List<XElement> groups = Items(expected, "groups").ToList();
                Assert.Equal(groups.Count, dataGroup.ChannelGroups.Count);
                for (int g = 0; g < groups.Count; g++)
                {
                    MdfChannelGroup group = dataGroup.ChannelGroups[g];
                    Assert.True(group.CanReadSamples, group.UnsupportedReason);
                    Assert.Equal(Text(groups[g], "name"), group.Name);
                    Assert.Equal((ulong)Int(groups[g], "samples"), group.SampleCount);
                    List<XElement> channels = Items(groups[g], "channels").ToList();
                    Assert.Equal(channels.Count, group.Channels.Count);
                    foreach (XElement wanted in channels)
                    {
                        MdfChannel channel = group.Channels[Int(wanted, "index")];
                        Assert.Equal(Text(wanted, "name"), channel.Name);
                        Assert.Equal(Bool(wanted, "master"), channel.IsMaster);
                        Assert.Equal(Kind(wanted), channel.ValueKind);
                        Assert.Equal(Bool(wanted, "readable"), channel.CanReadRaw);
                        if (Bool(wanted, "readable")) { Assert.Null(channel.UnsupportedReason); }
                        else { Assert.Equal(Text(wanted, "reason"), channel.UnsupportedReason); }
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(FullReads))]
        public void EveryTextByteArrayAndValidityMatchesTheFrozenValues(string id, int batchSize)
        {
            XElement expected = ReadCase(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                List<XElement> groups = Items(expected, "groups").ToList();
                for (int g = 0; g < groups.Count; g++)
                {
                    MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[g];
                    List<XElement> channels = Items(groups[g], "channels").Where(c => Bool(c, "readable")).ToList();
                    int[] selected = channels.Select(c => Int(c, "index")).ToArray();
                    int samples = Int(groups[g], "samples");
                    Check(reader.ReadSamples(group, selected, batchSize: batchSize), channels, selected, 0, samples);
                    foreach (XElement listed in channels.Where(c => c.Element("signalData") != null))
                    {
                        int[] only = { Int(listed, "index") };
                        Check(reader.ReadSamples(group, only, batchSize: batchSize), channels, only, 0, samples);
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(AllFiles))]
        public void ReorderedMixedAndSingleSelectionsMatchForEverySubrange(string id)
        {
            XElement expected = ReadCase(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                List<XElement> groups = Items(expected, "groups").ToList();
                for (int g = 0; g < groups.Count; g++)
                {
                    MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[g];
                    List<XElement> channels = Items(groups[g], "channels").Where(c => Bool(c, "readable")).ToList();
                    int samples = Int(groups[g], "samples");
                    int[] all = channels.Select(c => Int(c, "index")).ToArray();
                    List<int[]> selections = new List<int[]> { all.Reverse().ToArray(), new[] { all[all.Length - 1], all[0] } };
                    selections.AddRange(all.Select(i => new[] { i }));
                    foreach (int[] selected in selections)
                    foreach (int batch in new[] { 1, 3, 1024 })
                    for (int start = 0; start <= samples; start++)
                    for (int count = 0; count <= samples - start; count++)
                    {
                        Check(reader.ReadSamples(group, selected, (ulong)start, (ulong)count, batch), channels, selected, start, count);
                    }
                }
            }
        }

        [Fact]
        public void Pre420LdRootRefusesReadsWhileOtherChannelsRead()
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read("m5r-ld-sd411"), false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 1 }));
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 0, 1 }));
                Assert.Equal(4, reader.ReadSamples(group, new[] { 2, 0, 3 }).Sum(b => b.SampleCount));
            }
        }

        [Fact]
        public void FrozenCorpusCoversListShapesSpanningEntriesAndTheRefusal()
        {
            Assert.Equal(new[] { "DT", "LD", "DT", "DT" }, Ids.Select(id => Text(ReadCase(id).Element("storage"), "kind")));
            Assert.Equal(new[] { "0", "0", "1", "0" }, Ids.Select(id => Text(ReadCase(id), "recordIdSize")));
            List<XElement> listed = Ids.SelectMany(id => Items(ReadCase(id), "groups")).SelectMany(g => Items(g, "channels"))
                .Where(c => c.Element("signalData") != null && Text(c.Element("signalData"), "root") != "SD").ToList();
            Assert.Equal(5, listed.Count(c => Bool(c, "readable")));
            Assert.Single(listed, c => !Bool(c, "readable"));
            Assert.Single(listed, c => Text(c.Element("signalData"), "root") == "HL");
            List<XElement> lists = listed.SelectMany(c => Items(c.Element("signalData"), "lists")).ToList();
            Assert.Equal(7, lists.Count);
            Assert.Equal(3, lists.Count(l => l.Element("equal").Value != ""));
            Assert.Equal(4, lists.SelectMany(l => Items(l, "summaries")).Count());
            Assert.Equal(4, lists.SelectMany(l => Items(l, "children")).Count(c => Text(c, "value") == "DZ"));
            Assert.Contains(listed, c => Items(c, "valid").Any(v => v.Value == "false"));
        }

        private static void Check(IEnumerable<MdfSampleBatch> batches, List<XElement> channels, int[] selected, int start, int count)
        {
            int ordinal = start;
            foreach (MdfSampleBatch batch in batches)
            {
                Assert.Equal((ulong)ordinal, batch.FirstSampleIndex);
                Assert.Equal(selected, batch.Channels.Select(c => c.Index));
                Assert.InRange(batch.SampleCount, 1, count);
                for (int column = 0; column < selected.Length; column++)
                {
                    XElement wanted = channels.Single(c => Int(c, "index") == selected[column]);
                    MdfValueKind kind = Kind(wanted);
                    List<bool> valid = Items(wanted, "valid").Select(v => v.Value == "true").ToList();
                    for (int row = 0; row < batch.SampleCount; row++)
                    {
                        int sample = ordinal + row;
                        MdfSampleValue actual = batch.GetValue(row, column);
                        Assert.Equal(kind, actual.Kind);
                        Assert.Equal(valid[sample], actual.IsValid);
                        if (kind == MdfValueKind.Text) { Assert.Equal(Row(wanted, "text", sample), actual.TextValue); }
                        else if (kind == MdfValueKind.ByteArray)
                        { Assert.Equal(Row(wanted, "bytes", sample), BitConverter.ToString(actual.GetByteArrayValue()).Replace("-", "").ToLowerInvariant()); }
                        else if (kind == MdfValueKind.UnsignedInteger) { Assert.Equal(ulong.Parse(Row(wanted, "raw", sample), CultureInfo.InvariantCulture), actual.UnsignedValue); }
                        else if (kind == MdfValueKind.SignedInteger) { Assert.Equal(long.Parse(Row(wanted, "raw", sample), CultureInfo.InvariantCulture), actual.SignedValue); }
                        else
                        {
                            Assert.Equal(BitConverter.DoubleToInt64Bits(double.Parse(Row(wanted, "raw", sample), CultureInfo.InvariantCulture)),
                                BitConverter.DoubleToInt64Bits(actual.FloatingPointValue));
                        }
                    }
                }
                ordinal += batch.SampleCount;
            }
            Assert.Equal(start + count, ordinal);
        }

        private static string Row(XElement parent, string name, int row) { return Items(parent, name).ElementAt(row).Value; }
        private static MdfValueKind Kind(XElement parent) { return (MdfValueKind)Enum.Parse(typeof(MdfValueKind), Text(parent, "kind")); }
        private static IEnumerable<XElement> Items(XElement parent, string name) { return parent.Element(name).Elements("item"); }
        private static string Text(XElement parent, string name) { return parent.Element(name).Value; }
        private static int Int(XElement parent, string name) { return int.Parse(parent.Element(name).Value, CultureInfo.InvariantCulture); }
        private static bool Bool(XElement parent, string name) { return parent.Element(name).Value == "true"; }

        private static XElement ReadCase(string id)
        {
            using (FileStream stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "m5r-expectations.json")))
            using (XmlDictionaryReader json = JsonReaderWriterFactory.CreateJsonReader(stream, XmlDictionaryReaderQuotas.Max))
            {
                return XElement.Load(json).Element("cases").Elements("item").Single(c => Text(c, "id") == id);
            }
        }
    }
}
