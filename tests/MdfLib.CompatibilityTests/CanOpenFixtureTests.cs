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
    // every expectation below was frozen before the CANopen change existed (tools/fixtures/m5z-expectations.json,
    //): CANopen date (13, 7 bytes) / time (14, 6 bytes) are fixed byte arrays of their bytes.
    public sealed class CanOpenFixtureTests
    {
        private static readonly string[] Ids = { "m5z-canopen410", "m5z-canopen-dl411", "m5z-canopen420" };

        public static IEnumerable<object[]> AllFiles() { return Ids.Select(id => new object[] { id }); }

        public static IEnumerable<object[]> FullReads()
        {
            foreach (string id in Ids)
            foreach (int batch in new[] { 1, 2, 1024 })
            foreach (MdfValueRepresentation representation in new[] { MdfValueRepresentation.Raw, MdfValueRepresentation.Physical })
            { yield return new object[] { id, batch, representation }; }
        }

        [Theory]
        [MemberData(nameof(AllFiles))]
        public void CatalogPublishesCanOpenChannelsAsReadableByteArrays(string id)
        {
            XElement expected = ReadCase(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                Assert.Equal(Text(expected, "version"), reader.Header.Version);
                MdfDataGroup dataGroup = Assert.Single(reader.ReadMetadata());
                List<XElement> groups = Items(expected, "groups").ToList();
                Assert.Equal(groups.Count, dataGroup.ChannelGroups.Count);
                foreach (XElement wantedGroup in groups)
                {
                    MdfChannelGroup group = dataGroup.ChannelGroups[Int(wantedGroup, "index")];
                    Assert.True(group.CanReadSamples, group.UnsupportedReason);
                    Assert.Equal((ulong)Int(wantedGroup, "samples"), group.SampleCount);
                    Assert.Equal((ulong)(Int(wantedGroup, "dataBytes") + Int(wantedGroup, "invalidationBytes")), group.RecordByteCount);
                    List<XElement> channels = Items(wantedGroup, "channels").ToList();
                    Assert.Equal(channels.Count, group.Channels.Count);
                    foreach (XElement wanted in channels)
                    {
                        MdfChannel channel = group.Channels[Int(wanted, "index")];
                        Assert.Equal(Text(wanted, "name"), channel.Name);
                        Assert.Equal(Bool(wanted, "master"), channel.IsMaster);
                        Assert.Equal(Kind(wanted), channel.ValueKind);
                        Assert.Equal((uint)Int(wanted, "bitCount"), channel.BitCount);
                        Assert.True(channel.CanReadRaw, channel.UnsupportedReason);
                        Assert.True(channel.CanReadPhysical);
                        Assert.Null(channel.UnsupportedReason);
                        Assert.Null(channel.Array);
                        Assert.Empty(channel.Components);
                        Assert.Equal(MdfConversionKind.Identity, channel.Conversion.Kind);
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(FullReads))]
        public void EveryValueAndValidityMatchesTheFrozenValues(string id, int batchSize, object representationValue)
        {
            MdfValueRepresentation representation = (MdfValueRepresentation)representationValue;
            XElement expected = ReadCase(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                IReadOnlyList<MdfChannelGroup> catalog = reader.ReadMetadata()[0].ChannelGroups;
                foreach (XElement wantedGroup in Items(expected, "groups"))
                {
                    MdfChannelGroup group = catalog[Int(wantedGroup, "index")];
                    List<XElement> channels = Items(wantedGroup, "channels").ToList();
                    int[] all = channels.Select(c => Int(c, "index")).ToArray();
                    int samples = Int(wantedGroup, "samples");
                    Check(reader.ReadSamples(group, all, batchSize: batchSize, representation: representation), channels, all, 0, samples, representation);
                    foreach (int index in all)
                    {
                        int[] only = { index };
                        Check(reader.ReadSamples(group, only, batchSize: batchSize, representation: representation), channels, only, 0, samples, representation);
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(AllFiles))]
        public void ReorderedSelectionsMatchForEverySubrange(string id)
        {
            XElement expected = ReadCase(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                IReadOnlyList<MdfChannelGroup> catalog = reader.ReadMetadata()[0].ChannelGroups;
                foreach (XElement wantedGroup in Items(expected, "groups"))
                {
                    MdfChannelGroup group = catalog[Int(wantedGroup, "index")];
                    List<XElement> channels = Items(wantedGroup, "channels").ToList();
                    int[] all = channels.Select(c => Int(c, "index")).ToArray();
                    int samples = Int(wantedGroup, "samples");
                    List<int[]> selections = new List<int[]> { all.Reverse().ToArray(), new[] { all[all.Length - 1], all[0] } };
                    foreach (int[] selected in selections)
                    foreach (int batch in new[] { 1, 3, 1024 })
                    for (int start = 0; start <= samples; start++)
                    for (int count = 0; count <= samples - start; count++)
                    {
                        Check(reader.ReadSamples(group, selected, (ulong)start, (ulong)count, batch, representation: MdfValueRepresentation.Physical),
                            channels, selected, start, count, MdfValueRepresentation.Physical);
                    }
                }
            }
        }

        [Fact]
        public void FrozenCorpusCoversVersionsLayoutsAndBothTypes()
        {
            List<XElement> cases = Ids.Select(ReadCase).ToList();
            Assert.Equal(new[] { "4.10", "4.11", "4.20" }, cases.Select(c => Text(c, "version")));
            Assert.Equal(new[] { "DT", "DL", "DT" }, cases.Select(c => Text(c, "storage")));
            Assert.Equal(1, Int(cases[2], "recordIdBytes"));
            List<XElement> channels = cases.SelectMany(c => Items(c, "groups").SelectMany(g => Items(g, "channels"))).ToList();
            Assert.Equal(14, channels.Count);
            Assert.Equal(4, channels.Count(c => Text(c, "dataType") == "13" && Text(c, "bitCount") == "56"));
            Assert.Equal(3, channels.Count(c => Text(c, "dataType") == "14" && Text(c, "bitCount") == "48"));
            Assert.Contains(channels, c => Text(c, "dataType") == "13" && Items(c, "valid").Any(v => v.Value == "false"));
            Assert.All(channels.Where(c => Text(c, "kind") == "ByteArray"), c => Assert.Equal(
                Items(c, "raw").Select(r => r.Value), Items(c, "physical").Select(r => r.Value)));
        }

        private static void Check(IEnumerable<MdfSampleBatch> batches, List<XElement> channels, int[] selected, int start, int count,
            MdfValueRepresentation representation)
        {
            bool raw = representation == MdfValueRepresentation.Raw;
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
                    List<List<string>> values = Items(wanted, raw ? "raw" : "physical").Select(r => r.Elements("item").Select(v => v.Value).ToList()).ToList();
                    List<bool> valid = Items(wanted, "valid").Select(v => v.Value == "true").ToList();
                    for (int row = 0; row < batch.SampleCount; row++)
                    {
                        int sample = ordinal + row;
                        Assert.Single(values[sample]);
                        Same(kind, values[sample][0], valid[sample], batch.GetValue(row, column));
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
            else if (kind == MdfValueKind.ByteArray)
            {
                Assert.Throws<InvalidOperationException>(() => actual.TextValue);
                Assert.Equal(text, BitConverter.ToString(actual.GetByteArrayValue()).Replace("-", "").ToLowerInvariant());
            }
            else if (text == "NaN") { Assert.True(double.IsNaN(actual.FloatingPointValue)); }
            else { Assert.Equal(BitConverter.DoubleToInt64Bits(Real(text)), BitConverter.DoubleToInt64Bits(actual.FloatingPointValue)); }
        }

        private static double Real(string value)
        {
            if (value == "NaN") { return double.NaN; }
            if (value == "Infinity") { return double.PositiveInfinity; }
            if (value == "-Infinity") { return double.NegativeInfinity; }
            if (value == "-0") { return BitConverter.Int64BitsToDouble(long.MinValue); }
            return double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static MdfValueKind Kind(XElement parent) { return (MdfValueKind)Enum.Parse(typeof(MdfValueKind), Text(parent, "kind")); }
        private static IEnumerable<XElement> Items(XElement parent, string name) { return parent.Element(name).Elements("item"); }
        private static string Text(XElement parent, string name) { return parent.Element(name).Value; }
        private static int Int(XElement parent, string name) { return int.Parse(parent.Element(name).Value, CultureInfo.InvariantCulture); }
        private static bool Bool(XElement parent, string name) { return parent.Element(name).Value == "true"; }

        private static XElement ReadCase(string id)
        {
            using (FileStream stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "m5z-expectations.json")))
            using (XmlDictionaryReader json = JsonReaderWriterFactory.CreateJsonReader(stream, XmlDictionaryReaderQuotas.Max))
            {
                return XElement.Load(json).Element("cases").Elements("item").Single(c => Text(c, "id") == id);
            }
        }
    }
}
