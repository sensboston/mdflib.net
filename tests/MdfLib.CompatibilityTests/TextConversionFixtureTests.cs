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
    // expectations frozen before the C# change (tools/fixtures/m6b-expectations.json, build_m6b.py): MDF 4 value/range
    // to text and text to value/text with TX and NIL references, fixed and VLSD text inputs.
    public sealed class TextConversionFixtureTests
    {
        private static readonly string[] Ids = { "m6b-text410", "m6b-text420", "m6b-text330" };

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
        public void CatalogPublishesKindsTextsAndParameters(string id)
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
                    Assert.Equal(Text(wanted, "name"), channel.Name);
                    Assert.Equal(Kind(wanted, "kind"), channel.ValueKind);
                    Assert.True(channel.CanReadRaw, channel.UnsupportedReason);
                    Assert.True(channel.CanReadPhysical);
                    Assert.Equal((MdfConversionKind)Enum.Parse(typeof(MdfConversionKind), Text(wanted, "conversion")), conversion.Kind);
                    Assert.Equal(Items(wanted, "parameters").Select(p => double.Parse(p.Value, NumberStyles.Float, CultureInfo.InvariantCulture)),
                        conversion.Parameters);
                    Assert.Equal(Items(wanted, "texts").Select(Nullable), conversion.Texts);
                    if (conversion.Kind != MdfConversionKind.Identity) { Assert.Null(conversion.Factor); }
                    XElement share = wanted.Element("sharesConversionWith");
                    if (share != null) { Assert.Same(group.Channels.Single(c => c.Name == share.Value).Conversion, conversion); }
                }
            }
        }

        [Theory]
        [MemberData(nameof(FullReads))]
        public void EveryValueBytesAndValidityMatchTheFrozenValues(string id, int batchSize, object representationValue)
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
                    Check(reader.ReadSamples(group, new[] { index }, batchSize: batchSize, representation: representation),
                        channels, new[] { index }, 0, samples, representation);
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
                foreach (int batch in new[] { 1, 4 })
                for (int start = 0; start <= samples; start++)
                for (int count = 0; count <= samples - start; count++)
                {
                    Check(reader.ReadSamples(group, selected, (ulong)start, (ulong)count, batch, MdfValueRepresentation.Physical),
                        channels, selected, start, count, MdfValueRepresentation.Physical);
                }
            }
        }

        [Fact]
        public void FrozenCorpusCoversEveryTextKindAndEdge()
        {
            List<XElement> channels = Ids.Select(ReadCase).SelectMany(c => Items(c, "channels")).ToList();
            Assert.Equal(19, channels.Count);
            Assert.Equal(new[] { "Identity", "RangeToText", "TextToText", "TextToValue", "ValueToText" },
                channels.Select(c => Text(c, "conversion")).Distinct().OrderBy(x => x, StringComparer.Ordinal));
            Assert.Contains(channels, c => Items(c, "texts").Any(t => Nullable(t) == null));
            Assert.Contains(channels, c => Items(c, "valid").Any(v => v.Value == "false"));
            Assert.Contains(channels, c => c.Element("physical") != null && Items(c, "physical").Any(v => v.Value.Contains("✓")));
        }

        private static void Check(IEnumerable<MdfSampleBatch> batches, List<XElement> channels, int[] selected, int start, int count,
            MdfValueRepresentation representation)
        {
            bool raw = representation == MdfValueRepresentation.Raw;
            int ordinal = start;
            foreach (MdfSampleBatch batch in batches)
            {
                Assert.Equal((ulong)ordinal, batch.FirstSampleIndex);
                Assert.InRange(batch.SampleCount, 1, count);
                for (int column = 0; column < selected.Length; column++)
                {
                    XElement wanted = channels.Single(c => Int(c, "index") == selected[column]);
                    bool converted = !raw && wanted.Element("physical") != null;
                    MdfValueKind kind = Kind(wanted, converted ? "physicalKind" : "kind");
                    List<string> values = Items(wanted, converted ? "physical" : "raw").Select(v => v.Value).ToList();
                    XElement bytes = wanted.Element(converted ? "physicalBytes" : "rawBytes");
                    List<bool> valid = Items(wanted, "valid").Select(v => v.Value == "true").ToList();
                    for (int row = 0; row < batch.SampleCount; row++)
                    {
                        MdfSampleValue actual = batch.GetValue(row, column);
                        string text = values[ordinal + row];
                        Assert.Equal(kind, actual.Kind);
                        Assert.Equal(valid[ordinal + row], actual.IsValid);
                        if (kind == MdfValueKind.Text)
                        {
                            Assert.Equal(text, actual.TextValue);
                            Assert.Equal(bytes.Elements("item").ElementAt(ordinal + row).Value, Hex(actual.GetByteArrayValue()));
                        }
                        else if (kind == MdfValueKind.UnsignedInteger) { Assert.Equal(ulong.Parse(text, CultureInfo.InvariantCulture), actual.UnsignedValue); }
                        else if (kind == MdfValueKind.SignedInteger) { Assert.Equal(long.Parse(text, CultureInfo.InvariantCulture), actual.SignedValue); }
                        else if (text == "NaN") { Assert.True(double.IsNaN(actual.FloatingPointValue)); }
                        else { Assert.Equal(BitConverter.DoubleToInt64Bits(Real(text)), BitConverter.DoubleToInt64Bits(actual.FloatingPointValue)); }
                    }
                }
                ordinal += batch.SampleCount;
            }
            Assert.Equal(start + count, ordinal);
        }

        private static string Hex(byte[] bytes) { return string.Concat(bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture))); }

        private static double Real(string value)
        {
            if (value == "NaN") { return double.NaN; }
            if (value == "Infinity") { return double.PositiveInfinity; }
            if (value == "-Infinity") { return double.NegativeInfinity; }
            if (value == "-0") { return BitConverter.Int64BitsToDouble(long.MinValue); }
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
            using (FileStream stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "m6b-expectations.json")))
            using (XmlDictionaryReader json = JsonReaderWriterFactory.CreateJsonReader(stream, XmlDictionaryReaderQuotas.Max))
            {
                return XElement.Load(json).Element("cases").Elements("item").Single(c => Text(c, "id") == id);
            }
        }
    }
}
