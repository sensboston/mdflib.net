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
    // every expectation below was frozen before the LD remainder change existed
    // (tools/fixtures/m5m-expectations.json): LD summary arrays and HL over LD.
    public sealed class ListDataRemainderFixtureTests
    {
        private static readonly string[] Ids = { "m5m-summaries420", "m5m-equal-summaries420", "m5m-hl420", "m5m-hl-paired420" };

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
        public void CatalogIsTheFrozenSingleSortedGroupWithItsMasterAndConversions(string id)
        {
            XElement expected = ReadCase(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                Assert.Equal(Text(expected, "version"), reader.Header.Version);
                IReadOnlyList<MdfDataGroup> catalog = reader.ReadMetadata();
                Assert.Single(catalog);
                MdfChannelGroup group = Assert.Single(catalog[0].ChannelGroups);
                Assert.True(group.CanReadSamples, group.UnsupportedReason);
                Assert.Equal((ulong)Int(expected, "samples"), group.SampleCount);
                Assert.Equal((ulong)(Int(expected, "dataBytes") + Int(expected, "invalidationBytes")), group.RecordByteCount);
                List<XElement> channels = Items(expected, "channels").ToList();
                Assert.Equal(channels.Count, group.Channels.Count);
                foreach (XElement wanted in channels)
                {
                    MdfChannel channel = group.Channels[Int(wanted, "index")];
                    Assert.Equal(Text(wanted, "name"), channel.Name);
                    Assert.Equal(Bool(wanted, "master"), channel.IsMaster);
                    Assert.Equal(Kind(wanted, "kind"), channel.ValueKind);
                    Assert.True(channel.CanReadRaw, channel.UnsupportedReason);
                    Assert.True(channel.CanReadPhysical);
                    Assert.Equal(wanted.Element("physicalKind") != null ? MdfConversionKind.Linear : MdfConversionKind.Identity, channel.Conversion.Kind);
                }
            }
        }

        [Theory]
        [MemberData(nameof(FullReads))]
        public void EveryValueValidityAndMasterTimestampMatchesTheFrozenValues(string id, int batchSize, object representationValue)
        {
            MdfValueRepresentation representation = (MdfValueRepresentation)representationValue;
            XElement expected = ReadCase(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                List<XElement> channels = Items(expected, "channels").ToList();
                int[] selected = channels.Select(c => Int(c, "index")).ToArray();
                int samples = Int(expected, "samples");
                Check(reader.ReadSamples(group, selected, batchSize: batchSize, representation: representation), channels, selected, 0, samples, representation);
                int master = Int(channels.Single(c => Bool(c, "master")), "index");
                Check(reader.ReadSamples(group, new[] { master }, batchSize: batchSize, representation: representation), channels, new[] { master }, 0, samples, representation);
            }
        }

        [Theory]
        [MemberData(nameof(AllFiles))]
        public void ReorderedAndSingleSelectionsMatchForEverySubrangeAndBothRepresentations(string id)
        {
            XElement expected = ReadCase(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                List<XElement> channels = Items(expected, "channels").ToList();
                int samples = Int(expected, "samples");
                int[] all = channels.Select(c => Int(c, "index")).ToArray();
                List<int[]> selections = new List<int[]> { all.Reverse().ToArray(), new[] { 2, 0 } };
                selections.AddRange(all.Select(i => new[] { i }));
                foreach (MdfValueRepresentation representation in new[] { MdfValueRepresentation.Raw, MdfValueRepresentation.Physical })
                foreach (int[] selected in selections)
                foreach (int batch in new[] { 1, 3, 1024 })
                for (int start = 0; start <= samples; start++)
                for (int count = 0; count <= samples - start; count++)
                {
                    Check(reader.ReadSamples(group, selected, (ulong)start, (ulong)count, batch, representation), channels, selected, start, count, representation);
                }
            }
        }

        [Fact]
        public void HeaderListAndPairedInvalidationFilesExerciseEveryFrozenValidityPattern()
        {
            XElement paired = ReadCase("m5m-hl-paired420");
            Assert.Equal(1, Int(paired, "invalidationBytes"));
            Assert.Equal(new[] { "2147483654", "2147483648" }, Items(paired, "lists").Select(l => Text(l, "flags")));
            Assert.Contains(Items(paired, "lists").SelectMany(l => Items(l, "children")), c => c.Element("invalidation").Value == "null");
            Assert.Contains(Items(paired, "channels").SelectMany(c => Items(c, "valid")), v => v.Value == "false");
            XElement plain = ReadCase("m5m-hl420");
            Assert.Equal("1", Text(plain.Element("headerList"), "zip"));
        }

        private static void Check(IEnumerable<MdfSampleBatch> batches, List<XElement> channels, int[] selected, int start, int count,
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
                    XElement wanted = channels.Single(c => Int(c, "index") == selected[column]);
                    bool raw = representation == MdfValueRepresentation.Raw;
                    List<string> rows = Items(wanted, raw ? "raw" : "physical").Select(v => v.Value).ToList();
                    List<bool> valid = Items(wanted, "valid").Select(v => v.Value == "true").ToList();
                    MdfValueKind kind = !raw && wanted.Element("physicalKind") != null ? Kind(wanted, "physicalKind") : Kind(wanted, "kind");
                    for (int row = 0; row < batch.SampleCount; row++)
                    {
                        int sample = ordinal + row;
                        Same(kind, rows[sample], valid[sample], batch.GetValue(row, column));
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
            if (value == "-0" || value == "-0.0") { return BitConverter.Int64BitsToDouble(long.MinValue); }
            return double.Parse(value, CultureInfo.InvariantCulture);
        }

        private static MdfValueKind Kind(XElement parent, string name) { return (MdfValueKind)Enum.Parse(typeof(MdfValueKind), Text(parent, name)); }
        private static IEnumerable<XElement> Items(XElement parent, string name) { return parent.Element(name).Elements("item"); }
        private static string Text(XElement parent, string name) { return parent.Element(name).Value; }
        private static int Int(XElement parent, string name) { return int.Parse(parent.Element(name).Value, CultureInfo.InvariantCulture); }
        private static bool Bool(XElement parent, string name) { return parent.Element(name).Value == "true"; }

        private static XElement ReadCase(string id)
        {
            using (FileStream stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "m5m-expectations.json")))
            using (XmlDictionaryReader json = JsonReaderWriterFactory.CreateJsonReader(stream, XmlDictionaryReaderQuotas.Max))
            {
                return XElement.Load(json).Element("cases").Elements("item").Single(c => Text(c, "id") == id);
            }
        }
    }
}
