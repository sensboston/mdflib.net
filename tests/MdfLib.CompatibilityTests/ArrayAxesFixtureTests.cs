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
    // every expectation below was frozen before the axis/quantity change existed (tools/fixtures/m5t-expectations.json,
    //): CN-template array roles 0/1/2 with axis, fixed-axis, quantity, inverse, left-open and
    // standard-axis metadata read exactly as arrays; the metadata itself is inert.
    public sealed class ArrayAxesFixtureTests
    {
        private static readonly string[] Ids = { "m5t-axes410", "m5t-axes411", "m5t-axes420" };

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
        public void CatalogPublishesShapesReadabilityAndExactRefusalReasons(string id)
        {
            XElement expected = ReadCase(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                Assert.Equal(Text(expected, "version"), reader.Header.Version);
                MdfChannelGroup group = Assert.Single(Assert.Single(reader.ReadMetadata()).ChannelGroups);
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
                    Assert.Equal(Kind(wanted), channel.ValueKind);
                    Assert.Equal((uint)Int(wanted, "bitCount"), channel.BitCount);
                    Assert.Equal(Bool(wanted, "readable"), channel.CanReadRaw);
                    Assert.Equal(Bool(wanted, "readable"), channel.CanReadPhysical);
                    if (Bool(wanted, "readable")) { Assert.Null(channel.UnsupportedReason); }
                    else { Assert.Equal(Text(wanted, "reason"), channel.UnsupportedReason); }
                    Assert.Equal(wanted.Element("conversion") != null ? MdfConversionKind.Linear : MdfConversionKind.Identity, channel.Conversion.Kind);
                    List<ulong> shape = Items(wanted, "shape").Select(s => ulong.Parse(s.Value, CultureInfo.InvariantCulture)).ToList();
                    if (shape.Count == 0) { Assert.Null(channel.Array); }
                    else
                    {
                        Assert.Equal(shape, channel.Array.Dimensions);
                        Assert.Equal((ulong)Int(wanted, "elementCount"), channel.Array.ElementCount);
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(FullReads))]
        public void EveryElementAndValidityMatchesTheFrozenValues(string id, int batchSize, object representationValue)
        {
            MdfValueRepresentation representation = (MdfValueRepresentation)representationValue;
            XElement expected = ReadCase(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                List<XElement> channels = Items(expected, "channels").Where(c => Bool(c, "readable")).ToList();
                int[] selected = channels.Select(c => Int(c, "index")).ToArray();
                int samples = Int(expected, "samples");
                Check(reader.ReadSamples(group, selected, batchSize: batchSize, representation: representation), channels, selected, 0, samples, representation);
                foreach (XElement array in channels.Where(c => Items(c, "shape").Any()))
                {
                    int[] only = { Int(array, "index") };
                    Check(reader.ReadSamples(group, only, batchSize: batchSize, representation: representation), channels, only, 0, samples, representation);
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
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                List<XElement> channels = Items(expected, "channels").Where(c => Bool(c, "readable")).ToList();
                int samples = Int(expected, "samples");
                int[] all = channels.Select(c => Int(c, "index")).ToArray();
                List<int[]> selections = new List<int[]> { all.Reverse().ToArray(), new[] { all[all.Length - 1], all[0] } };
                selections.AddRange(all.Select(i => new[] { i }));
                foreach (int[] selected in selections)
                foreach (int batch in new[] { 1, 3, 1024 })
                for (int start = 0; start <= samples; start++)
                for (int count = 0; count <= samples - start; count++)
                {
                    Check(reader.ReadSamples(group, selected, (ulong)start, (ulong)count, batch), channels, selected, start, count, MdfValueRepresentation.Raw);
                }
            }
        }

        [Theory]
        [MemberData(nameof(AllFiles))]
        public void RefusedArraysRefuseReadsWhileNeighboursRead(string id)
        {
            XElement expected = ReadCase(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                List<XElement> channels = Items(expected, "channels").ToList();
                int[] readable = channels.Where(c => Bool(c, "readable")).Select(c => Int(c, "index")).ToArray();
                foreach (XElement refused in channels.Where(c => !Bool(c, "readable")))
                {
                    int index = Int(refused, "index");
                    Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { index }));
                    Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { readable[1], index }));
                }
                Assert.Equal(5, reader.ReadSamples(group, readable.Reverse().ToArray()).Sum(b => b.SampleCount));
            }
        }

        [Fact]
        public void FrozenCorpusCoversEveryAcceptedRoleFlagAndRefusal()
        {
            List<XElement> all = Ids.SelectMany(id => Items(ReadCase(id), "channels")).ToList();
            List<XElement> arrays = all.Where(c => Items(c, "shape").Any() && Bool(c, "readable")).ToList();
            Assert.Equal(new[] { 0, 1, 2 }, arrays.Select(c => Int(c, "caType")).Distinct().OrderBy(t => t));
            Assert.Equal(0x1FE, arrays.Aggregate(0, (flags, c) => flags | Int(c, "caFlags")));
            Assert.Equal(new[]
            {
                "A fixed-axis array requires the axis flag.",
                "A scaling-axis array must have exactly one dimension.",
                "Dynamic-size arrays are not supported.",
                "Only array, scaling-axis and look-up CN-template arrays are supported.",
                "Unknown array flags, or the standard-axis flag before MDF 4.20, are not supported.",
                "Unknown array flags, or the standard-axis flag before MDF 4.20, are not supported.",
            }, all.Where(c => !Bool(c, "readable")).Select(c => Text(c, "reason")).OrderBy(r => r, StringComparer.Ordinal));
            Assert.Contains(arrays, c => Items(c, "valid").Any(v => v.Value == "false"));
            Assert.Contains(arrays, c => c.Element("conversion") != null);
            Assert.Contains(arrays, c => Items(c, "linkTargets").Any(t => t.Value.Length == 0) && Int(c, "caFlags") == 0x10);
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
                    MdfValueKind kind = !raw && wanted.Element("conversion") != null ? MdfValueKind.FloatingPoint : Kind(wanted);
                    List<bool> valid = Items(wanted, "valid").Select(v => v.Value == "true").ToList();
                    int elements = Int(wanted, "elementCount");
                    bool array = Items(wanted, "shape").Any();
                    for (int row = 0; row < batch.SampleCount; row++)
                    {
                        int sample = ordinal + row;
                        List<string> values = Items(wanted, raw ? "raw" : "physical").ElementAt(sample).Elements("item").Select(e => e.Value).ToList();
                        Assert.Equal(elements, values.Count);
                        if (!array)
                        {
                            Same(kind, values[0], valid[sample], batch.GetValue(row, column));
                            continue;
                        }
                        Assert.Throws<InvalidOperationException>(() => batch.GetValue(row, column));
                        for (int element = 0; element < elements; element++)
                        {
                            Same(kind, values[element], valid[sample], batch.GetValue(row, column, (ulong)element));
                        }
                        Assert.Throws<ArgumentOutOfRangeException>(() => batch.GetValue(row, column, (ulong)elements));
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

        private static MdfValueKind Kind(XElement parent) { return (MdfValueKind)Enum.Parse(typeof(MdfValueKind), Text(parent, "kind")); }
        private static IEnumerable<XElement> Items(XElement parent, string name) { return parent.Element(name).Elements("item"); }
        private static string Text(XElement parent, string name) { return parent.Element(name).Value; }
        private static int Int(XElement parent, string name) { return int.Parse(parent.Element(name).Value, CultureInfo.InvariantCulture); }
        private static bool Bool(XElement parent, string name) { return parent.Element(name).Value == "true"; }

        private static XElement ReadCase(string id)
        {
            using (FileStream stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "m5t-expectations.json")))
            using (XmlDictionaryReader json = JsonReaderWriterFactory.CreateJsonReader(stream, XmlDictionaryReaderQuotas.Max))
            {
                return XElement.Load(json).Element("cases").Elements("item").Single(c => Text(c, "id") == id);
            }
        }
    }
}
