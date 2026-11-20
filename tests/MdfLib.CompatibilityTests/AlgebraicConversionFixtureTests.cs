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
    // expectations frozen before the C# change (tools/fixtures/m6d-expectations.json, build_m6d.py, an independent
    // Python parser): MDF 4 cc_type 3, MDF 3 cc_type 10 formulas and MDF 3 cc_type 7/8 exponential/logarithmic. Arithmetic-only formulas are exact; formulas with
    // powers or functions allow a relative 1e-13 (platform libm differences).
    public sealed class AlgebraicConversionFixtureTests
    {
        private static readonly string[] Ids = { "m6d-alg410", "m6d-formula330", "m6d-expo330" };

        public static IEnumerable<object[]> AllFiles() { return Ids.Select(id => new object[] { id }); }

        public static IEnumerable<object[]> FullReads()
        {
            foreach (string id in Ids)
            foreach (int batch in new[] { 1, 3, 1024 })
            foreach (MdfValueRepresentation representation in new[] { MdfValueRepresentation.Raw, MdfValueRepresentation.Physical })
            { yield return new object[] { id, batch, representation }; }
        }

        [Theory]
        [MemberData(nameof(AllFiles))]
        public void CatalogPublishesKindsAndFormulas(string id)
        {
            XElement expected = ReadCase(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                Assert.Equal(Text(expected, "version"), reader.Header.Version);
                MdfChannelGroup group = Assert.Single(Assert.Single(reader.ReadMetadata()).ChannelGroups);
                Assert.Equal((ulong)Int(expected, "samples"), group.SampleCount);
                List<XElement> channels = Items(expected, "channels").ToList();
                Assert.Equal(channels.Count, group.Channels.Count);
                foreach (XElement wanted in channels)
                {
                    MdfChannel channel = group.Channels[Int(wanted, "index")];
                    MdfConversionKind kind = (MdfConversionKind)Enum.Parse(typeof(MdfConversionKind), Text(wanted, "conversion"));
                    Assert.Equal(Text(wanted, "name"), channel.Name);
                    Assert.True(channel.CanReadRaw, channel.UnsupportedReason);
                    Assert.Equal(kind, channel.Conversion.Kind);
                    Assert.Equal(kind != MdfConversionKind.Unsupported, channel.CanReadPhysical);
                    Assert.Equal(Nullable(wanted.Element("formula")), channel.Conversion.Formula);
                    Assert.Equal(Items(wanted, "parameters").Select(v => Real(v.Value)), channel.Conversion.Parameters);
                    if (kind != MdfConversionKind.Identity) { Assert.Null(channel.Conversion.Factor); }
                    XElement share = wanted.Element("sharesConversionWith");
                    if (share != null) { Assert.Same(group.Channels.Single(c => c.Name == share.Value).Conversion, channel.Conversion); }
                }
            }
        }

        [Theory]
        [MemberData(nameof(FullReads))]
        public void EveryValueAndValidityMatchTheFrozenValues(string id, int batchSize, object representationValue)
        {
            MdfValueRepresentation representation = (MdfValueRepresentation)representationValue;
            XElement expected = ReadCase(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                List<XElement> channels = Items(expected, "channels").ToList();
                int[] readable = channels.Where(c => representation == MdfValueRepresentation.Raw || Text(c, "conversion") != "Unsupported")
                    .Select(c => Int(c, "index")).ToArray();
                int samples = Int(expected, "samples");
                Check(reader.ReadSamples(group, readable, batchSize: batchSize, representation: representation), channels, readable, 0, samples, representation);
                foreach (int index in readable)
                {
                    Check(reader.ReadSamples(group, new[] { index }, batchSize: batchSize, representation: representation),
                        channels, new[] { index }, 0, samples, representation);
                }
            }
        }

        [Theory]
        [MemberData(nameof(AllFiles))]
        public void SubrangesMatchAndUnsupportedFormulasRefusePhysical(string id)
        {
            XElement expected = ReadCase(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                List<XElement> channels = Items(expected, "channels").ToList();
                int[] readable = channels.Where(c => Text(c, "conversion") != "Unsupported").Select(c => Int(c, "index")).Reverse().ToArray();
                int samples = Int(expected, "samples");
                for (int start = 0; start <= samples; start++)
                for (int count = 0; count <= samples - start; count++)
                {
                    Check(reader.ReadSamples(group, readable, (ulong)start, (ulong)count, 2, MdfValueRepresentation.Physical),
                        channels, readable, start, count, MdfValueRepresentation.Physical);
                }
                foreach (XElement bad in channels.Where(c => Text(c, "conversion") == "Unsupported"))
                {
                    Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { Int(bad, "index") }, representation: MdfValueRepresentation.Physical));
                }
            }
        }

        [Fact]
        public void FrozenCorpusCoversEveryFormulaEdge()
        {
            List<XElement> channels = Ids.Select(ReadCase).SelectMany(c => Items(c, "channels")).ToList();
            Assert.Equal(21, channels.Count);
            List<XElement> algebraic = channels.Where(c => Text(c, "conversion") == "Algebraic").ToList();
            Assert.Contains(algebraic, c => c.Element("exact").Value == "true");
            Assert.Contains(algebraic, c => c.Element("exact").Value == "false");
            Assert.Contains(algebraic, c => Items(c, "physical").Any(v => v.Value == "Infinity"));
            Assert.Contains(algebraic, c => Items(c, "physical").Any(v => v.Value == "NaN"));
            Assert.Equal(3, channels.Count(c => Text(c, "conversion") == "Unsupported"));
            foreach (string kind in new[] { "Exponential", "Logarithmic" })
            {
                List<XElement> mapped = channels.Where(c => Text(c, "conversion") == kind).ToList();
                Assert.Equal(2, mapped.Count);
                Assert.Contains(mapped, c => Real(Items(c, "parameters").ElementAt(3).Value) == 0);
                Assert.Contains(mapped, c => Real(Items(c, "parameters").ElementAt(0).Value) == 0);
            }
            Assert.Contains(channels, c => c.Element("sharesConversionWith") != null);
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
                for (int column = 0; column < selected.Length; column++)
                {
                    XElement wanted = channels.Single(c => Int(c, "index") == selected[column]);
                    bool converted = !raw && wanted.Element("physical") != null;
                    MdfValueKind kind = converted ? MdfValueKind.FloatingPoint : Kind(wanted, "kind");
                    bool exact = !converted || wanted.Element("exact").Value == "true";
                    List<string> values = Items(wanted, converted ? "physical" : "raw").Select(v => v.Value).ToList();
                    List<bool> valid = Items(wanted, "valid").Select(v => v.Value == "true").ToList();
                    for (int row = 0; row < batch.SampleCount; row++)
                    {
                        MdfSampleValue actual = batch.GetValue(row, column);
                        string text = values[ordinal + row];
                        Assert.Equal(kind, actual.Kind);
                        Assert.Equal(valid[ordinal + row], actual.IsValid);
                        if (kind == MdfValueKind.UnsignedInteger) { Assert.Equal(ulong.Parse(text, CultureInfo.InvariantCulture), actual.UnsignedValue); }
                        else if (kind == MdfValueKind.SignedInteger) { Assert.Equal(long.Parse(text, CultureInfo.InvariantCulture), actual.SignedValue); }
                        else { Same(Real(text), actual.FloatingPointValue, exact, wanted); }
                    }
                }
                ordinal += batch.SampleCount;
            }
            Assert.Equal(start + count, ordinal);
        }

        private static void Same(double expected, double actual, bool exact, XElement channel)
        {
            if (double.IsNaN(expected)) { Assert.True(double.IsNaN(actual), Text(channel, "name")); return; }
            if (exact || double.IsInfinity(expected))
            {
                Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(actual));
                return;
            }
            Assert.True(Math.Abs(expected - actual) <= 1e-13 * Math.Max(1.0, Math.Abs(expected)),
                Text(channel, "name") + ": " + expected.ToString("R", CultureInfo.InvariantCulture) + " vs " + actual.ToString("R", CultureInfo.InvariantCulture));
        }

        private static double Real(string value)
        {
            if (value == "NaN") { return double.NaN; }
            if (value == "Infinity") { return double.PositiveInfinity; }
            if (value == "-Infinity") { return double.NegativeInfinity; }
            if (value == "-0" || value == "-0.0") { return BitConverter.Int64BitsToDouble(long.MinValue); }
            return double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static string Nullable(XElement item)
        {
            XAttribute type = item.Attribute("type");
            return type != null && type.Value == "null" ? null : item.Value;
        }

        private static MdfValueKind Kind(XElement parent, string name) { return (MdfValueKind)Enum.Parse(typeof(MdfValueKind), Text(parent, name)); }
        private static IEnumerable<XElement> Items(XElement parent, string name) { return parent.Element(name).Elements("item"); }
        private static string Text(XElement parent, string name) { return parent.Element(name).Value; }
        private static int Int(XElement parent, string name) { return int.Parse(parent.Element(name).Value, CultureInfo.InvariantCulture); }

        private static XElement ReadCase(string id)
        {
            using (FileStream stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "m6d-expectations.json")))
            using (XmlDictionaryReader json = JsonReaderWriterFactory.CreateJsonReader(stream, XmlDictionaryReaderQuotas.Max))
            {
                return XElement.Load(json).Element("cases").Elements("item").Single(c => Text(c, "id") == id);
            }
        }
    }
}
