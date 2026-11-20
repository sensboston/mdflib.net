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
    // every expectation below was frozen before the conversion code existed (tools/fixtures/m6a-expectations.json,
    //): rational, tables, value ranges and MDF 3 polynomials evaluated in binary64.
    public sealed class NumericConversionFixtureTests
    {
        private static readonly string[] Ids = { "m6a-conv410", "m6a-conv420", "m6a-conv330" };

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
        public void CatalogPublishesKindsAndWireParameters(string id)
        {
            XElement expected = ReadCase(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                Assert.Equal(Text(expected, "version"), reader.Header.Version);
                MdfChannelGroup group = Assert.Single(Assert.Single(reader.ReadMetadata()).ChannelGroups);
                Assert.True(group.CanReadSamples, group.UnsupportedReason);
                Assert.Equal((ulong)Int(expected, "samples"), group.SampleCount);
                List<XElement> channels = Items(expected, "channels").ToList();
                Assert.Equal(channels.Count, group.Channels.Count);
                foreach (XElement wanted in channels)
                {
                    MdfChannel channel = group.Channels[Int(wanted, "index")];
                    MdfChannelConversion conversion = channel.Conversion;
                    MdfConversionKind kind = (MdfConversionKind)Enum.Parse(typeof(MdfConversionKind), Text(wanted, "conversion"));
                    Assert.Equal(Text(wanted, "name"), channel.Name);
                    Assert.Equal(Bool(wanted, "master"), channel.IsMaster);
                    Assert.Equal(Kind(wanted, "kind"), channel.ValueKind);
                    Assert.Equal((uint)Int(wanted, "bitCount"), channel.BitCount);
                    Assert.True(channel.CanReadRaw, channel.UnsupportedReason);
                    Assert.True(channel.CanReadPhysical);
                    Assert.Equal(kind, conversion.Kind);
                    Assert.Equal(Items(wanted, "parameters").Select(p => Bits(Real(p.Value))), conversion.Parameters.Select(Bits));
                    if (kind == MdfConversionKind.Identity) { Assert.Equal(1.0, conversion.Factor); Assert.Equal(0.0, conversion.Offset); }
                    else if (kind == MdfConversionKind.Linear) { Assert.Equal(conversion.Parameters[1], conversion.Factor); Assert.Equal(conversion.Parameters[0], conversion.Offset); }
                    else { Assert.Null(conversion.Factor); Assert.Null(conversion.Offset); }
                    XElement share = wanted.Element("sharesConversionWith");
                    if (share != null) { Assert.Same(group.Channels.Single(c => c.Name == share.Value).Conversion, conversion); }
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
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                List<XElement> channels = Items(expected, "channels").ToList();
                int[] all = channels.Select(c => Int(c, "index")).ToArray();
                int samples = Int(expected, "samples");
                Check(reader.ReadSamples(group, all, batchSize: batchSize, representation: representation), channels, all, 0, samples, representation);
                foreach (int index in all)
                {
                    int[] only = { index };
                    Check(reader.ReadSamples(group, only, batchSize: batchSize, representation: representation), channels, only, 0, samples, representation);
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
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                List<XElement> channels = Items(expected, "channels").ToList();
                int[] all = channels.Select(c => Int(c, "index")).ToArray();
                int samples = Int(expected, "samples");
                foreach (int[] selected in new[] { Enumerable.Reverse(all).ToArray(), new[] { all[all.Length - 1], all[1] } })
                foreach (int batch in new[] { 1, 4, 1024 })
                for (int start = 0; start <= samples; start++)
                for (int count = 0; count <= samples - start; count++)
                {
                    Check(reader.ReadSamples(group, selected, (ulong)start, (ulong)count, batch, MdfValueRepresentation.Physical),
                        channels, selected, start, count, MdfValueRepresentation.Physical);
                }
            }
        }

        [Fact]
        public void FrozenCorpusCoversEveryKindAndEdge()
        {
            List<XElement> cases = Ids.Select(ReadCase).ToList();
            Assert.Equal(new[] { "4.10", "4.20", "3.30" }, cases.Select(c => Text(c, "version")));
            List<XElement> channels = cases.SelectMany(c => Items(c, "channels")).ToList();
            Assert.Equal(22, channels.Count);
            Assert.Equal(new[] { "Identity", "Linear", "Polynomial", "RangeTable", "Rational", "Table", "TableInterpolation" },
                channels.Select(c => Text(c, "conversion")).Distinct().OrderBy(x => x, StringComparer.Ordinal));
            List<string> physical = channels.Where(c => Text(c, "conversion") != "Identity").SelectMany(c => Items(c, "physical").Select(v => v.Value)).ToList();
            Assert.Contains("NaN", physical);
            Assert.Contains("Infinity", physical);
            Assert.Contains("-Infinity", physical);
            Assert.Contains("-0", physical);
            Assert.Contains(channels, c => Items(c, "valid").Any(v => v.Value == "false"));
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
                    MdfValueKind kind = Kind(wanted, raw ? "kind" : "physicalKind");
                    List<string> values = Items(wanted, raw ? "raw" : "physical").Select(v => v.Value).ToList();
                    List<bool> valid = Items(wanted, "valid").Select(v => v.Value == "true").ToList();
                    for (int row = 0; row < batch.SampleCount; row++)
                    {
                        Same(kind, values[ordinal + row], valid[ordinal + row], batch.GetValue(row, column));
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
            else if (text == "NaN") { Assert.True(double.IsNaN(actual.FloatingPointValue)); }
            else { Assert.Equal(Bits(Real(text)), Bits(actual.FloatingPointValue)); }
        }

        private static long Bits(double value) { return BitConverter.DoubleToInt64Bits(value); }

        private static double Real(string value)
        {
            if (value == "NaN") { return double.NaN; }
            if (value == "Infinity") { return double.PositiveInfinity; }
            if (value == "-Infinity") { return double.NegativeInfinity; }
            if (value == "-0") { return BitConverter.Int64BitsToDouble(long.MinValue); }
            return double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static MdfValueKind Kind(XElement parent, string name) { return (MdfValueKind)Enum.Parse(typeof(MdfValueKind), Text(parent, name)); }
        private static IEnumerable<XElement> Items(XElement parent, string name) { return parent.Element(name).Elements("item"); }
        private static string Text(XElement parent, string name) { return parent.Element(name).Value; }
        private static int Int(XElement parent, string name) { return int.Parse(parent.Element(name).Value, CultureInfo.InvariantCulture); }
        private static bool Bool(XElement parent, string name) { return parent.Element(name).Value == "true"; }

        private static XElement ReadCase(string id)
        {
            using (FileStream stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "m6a-expectations.json")))
            using (XmlDictionaryReader json = JsonReaderWriterFactory.CreateJsonReader(stream, XmlDictionaryReaderQuotas.Max))
            {
                return XElement.Load(json).Element("cases").Elements("item").Single(c => Text(c, "id") == id);
            }
        }
    }
}
