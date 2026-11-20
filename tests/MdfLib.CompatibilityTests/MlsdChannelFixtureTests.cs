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
    // every expectation below was frozen before the MLSD change existed
    // (tools/fixtures/m5p-expectations.json): cn_type 5 value = first N bytes, N = size channel.
    public sealed class MlsdChannelFixtureTests
    {
        private static readonly string[] Ids = { "m5p-mlsd410", "m5p-mlsd-hl420", "m5p-unsorted411", "m5p-ld420" };

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
        public void CatalogPublishesMlsdChannelsAndRefusesUnresolvableSizes(string id)
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
                    Assert.Equal((ulong)(Int(groups[g], "dataBytes") + Int(groups[g], "invalidationBytes")), group.RecordByteCount);
                    List<XElement> channels = Items(groups[g], "channels").ToList();
                    Assert.Equal(channels.Count, group.Channels.Count);
                    foreach (XElement wanted in channels)
                    {
                        MdfChannel channel = group.Channels[Int(wanted, "index")];
                        Assert.Equal(Text(wanted, "name"), channel.Name);
                        Assert.Equal(Bool(wanted, "master"), channel.IsMaster);
                        Assert.Equal((byte)Int(wanted, "syncType"), channel.SynchronizationType);
                        Assert.Equal(Kind(wanted, "kind"), channel.ValueKind);
                        Assert.Equal((uint)Int(wanted, "bitCount"), channel.BitCount);
                        Assert.Equal(Bool(wanted, "readable"), channel.CanReadRaw);
                        Assert.Equal(Bool(wanted, "readable"), channel.CanReadPhysical);
                        if (Bool(wanted, "readable")) { Assert.Null(channel.UnsupportedReason); }
                        else { Assert.Equal(Text(wanted, "reason"), channel.UnsupportedReason); }
                        Assert.Null(channel.Array);
                        Assert.Empty(channel.Components);
                    }
                    Assert.Single(group.Channels, c => c.IsMaster);
                }
            }
        }

        [Theory]
        [MemberData(nameof(FullReads))]
        public void EveryPrefixTextValueAndValidityMatchesTheFrozenValues(string id, int batchSize, object representationValue)
        {
            MdfValueRepresentation representation = (MdfValueRepresentation)representationValue;
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
                    Check(reader.ReadSamples(group, selected, batchSize: batchSize, representation: representation), channels, selected, 0, samples);
                    foreach (XElement mlsd in channels.Where(c => Int(c, "cnType") == 5))
                    {
                        // An MLSD-only selection reads its size from the same record without selecting it.
                        int[] only = { Int(mlsd, "index") };
                        Check(reader.ReadSamples(group, only, batchSize: batchSize, representation: representation), channels, only, 0, samples);
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
        public void UnresolvableMlsdChannelsRefuseReadsWithoutTouchingData()
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read("m5p-unsorted411"), false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[1];
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 1 }));
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 0, 3 }));
            }
        }

        [Fact]
        public void FrozenCorpusCoversEveryStorageKindAllTextEncodingsAndBothRefusals()
        {
            Assert.Equal(new[] { "DT", "HL", "DT", "LD" }, Ids.Select(id => Text(ReadCase(id).Element("storage"), "kind")));
            List<XElement> all = Ids.SelectMany(id => Items(ReadCase(id), "groups")).SelectMany(g => Items(g, "channels")).ToList();
            List<XElement> mlsd = all.Where(c => Int(c, "cnType") == 5).ToList();
            Assert.Equal(6, mlsd.Count(c => Bool(c, "readable")));
            Assert.Equal(2, mlsd.Count(c => !Bool(c, "readable")));
            Assert.Equal(new[] { "10", "6", "7", "8", "9" }, mlsd.Where(c => Bool(c, "readable")).Select(c => Text(c, "dataType")).Distinct().OrderBy(t => t));
            Assert.Contains(mlsd, c => Bool(c, "readable") && Int(c, "sizeChannel") > Int(c, "index")); // size after its MLSD
            Assert.Contains(mlsd, c => Bool(c, "readable") && Items(c, "valid").Zip(Items(c, "ownValid"), (v, o) => v.Value != o.Value).Any(d => d));
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
                    List<bool> valid = Items(wanted, "valid").Select(v => v.Value == "true").ToList();
                    for (int row = 0; row < batch.SampleCount; row++)
                    {
                        int sample = ordinal + row;
                        MdfSampleValue actual = batch.GetValue(row, column);
                        Assert.Equal(Kind(wanted, "kind"), actual.Kind);
                        Assert.Equal(valid[sample], actual.IsValid);
                        if (Int(wanted, "cnType") == 5) { Mlsd(wanted, sample, actual); }
                        else { Scalar(actual.Kind, Items(wanted, "raw").ElementAt(sample).Value, actual); }
                    }
                }
                ordinal += batch.SampleCount;
            }
            Assert.Equal(start + count, ordinal);
        }

        private static void Mlsd(XElement wanted, int sample, MdfSampleValue actual)
        {
            string hex = Items(wanted, "bytes").ElementAt(sample).Value;
            Assert.Equal(hex, BitConverter.ToString(actual.GetByteArrayValue()).Replace("-", "").ToLowerInvariant());
            if (actual.Kind == MdfValueKind.Text)
            {
                string text = new string(Items(wanted, "text").ElementAt(sample).Elements("item")
                    .Select(u => (char)int.Parse(u.Value, CultureInfo.InvariantCulture)).ToArray());
                Assert.Equal(text, actual.TextValue);
            }
        }

        private static void Scalar(MdfValueKind kind, string text, MdfSampleValue actual)
        {
            if (kind == MdfValueKind.UnsignedInteger) { Assert.Equal(ulong.Parse(text, CultureInfo.InvariantCulture), actual.UnsignedValue); }
            else if (kind == MdfValueKind.SignedInteger) { Assert.Equal(long.Parse(text, CultureInfo.InvariantCulture), actual.SignedValue); }
            else { Assert.Equal(BitConverter.DoubleToInt64Bits(double.Parse(text, CultureInfo.InvariantCulture)), BitConverter.DoubleToInt64Bits(actual.FloatingPointValue)); }
        }

        private static MdfValueKind Kind(XElement parent, string name) { return (MdfValueKind)Enum.Parse(typeof(MdfValueKind), Text(parent, name)); }
        private static IEnumerable<XElement> Items(XElement parent, string name) { return parent.Element(name).Elements("item"); }
        private static string Text(XElement parent, string name) { return parent.Element(name).Value; }
        private static int Int(XElement parent, string name) { return int.Parse(parent.Element(name).Value, CultureInfo.InvariantCulture); }
        private static bool Bool(XElement parent, string name) { return parent.Element(name).Value == "true"; }

        private static XElement ReadCase(string id)
        {
            using (FileStream stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "m5p-expectations.json")))
            using (XmlDictionaryReader json = JsonReaderWriterFactory.CreateJsonReader(stream, XmlDictionaryReaderQuotas.Max))
            {
                return XElement.Load(json).Element("cases").Elements("item").Single(c => Text(c, "id") == id);
            }
        }
    }
}
