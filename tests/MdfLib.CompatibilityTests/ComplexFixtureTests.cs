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
    // expectations frozen before the C# change (tools/fixtures/m5aa-expectations.json, build_m5aa.py): MDF 4 complex
    // channels (data types 15/16) of two IEEE binary16/32/64 parts, scalars and CN-template arrays; refusals by reason.
    public sealed class ComplexFixtureTests
    {
        private static readonly string[] Ids = { "m5aa-complex420", "m5aa-complex-dl420", "m5aa-complex-half420" };

        public static IEnumerable<object[]> AllFiles() { return Ids.Select(id => new object[] { id }); }

        public static IEnumerable<object[]> FullReads()
        {
            foreach (string id in Ids)
            foreach (int batch in new[] { 1, 4, 1024 })
            foreach (MdfValueRepresentation representation in new[] { MdfValueRepresentation.Raw, MdfValueRepresentation.Physical })
            { yield return new object[] { id, batch, representation }; }
        }

        [Theory]
        [MemberData(nameof(AllFiles))]
        public void CatalogPublishesComplexKindsAndRefusals(string id)
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
                    Assert.Equal(MdfCalendarKind.None, channel.CalendarKind);
                    Assert.Equal(Bool(wanted, "readable"), channel.CanReadRaw);
                    Assert.Equal(Bool(wanted, "physicalReadable"), channel.CanReadPhysical);
                    Assert.Equal(Text(wanted, "conversion"), channel.Conversion.Kind.ToString());
                    if (Bool(wanted, "readable")) { Assert.Null(channel.UnsupportedReason); }
                    else { Assert.Equal(Text(wanted, "reason"), channel.UnsupportedReason); }
                    if (!Items(wanted, "shape").Any()) { Assert.Null(channel.Array); } // a scalar's elementCount is 1
                    else { Assert.Equal((ulong)Int(wanted, "elementCount"), channel.Array.ElementCount); }
                }
            }
        }

        [Theory]
        [MemberData(nameof(FullReads))]
        public void EveryPartAndValidityMatchesTheFrozenValues(string id, int batchSize, object representationValue)
        {
            MdfValueRepresentation representation = (MdfValueRepresentation)representationValue;
            XElement expected = ReadCase(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                List<XElement> channels = Items(expected, "channels").ToList();
                string gate = representation == MdfValueRepresentation.Raw ? "readable" : "physicalReadable";
                int[] readable = channels.Where(c => Bool(c, gate)).Select(c => Int(c, "index")).ToArray();
                int samples = Int(expected, "samples");
                Check(reader.ReadSamples(group, readable, batchSize: batchSize, representation: representation), channels, readable, 0, samples, representation);
                foreach (int index in readable)
                {
                    Check(reader.ReadSamples(group, new[] { index }, batchSize: batchSize, representation: representation),
                        channels, new[] { index }, 0, samples, representation);
                }
                foreach (XElement refused in channels.Where(c => !Bool(c, gate)))
                {
                    Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { Int(refused, "index") }, representation: representation).ToList());
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
                int[] all = channels.Where(c => Bool(c, "readable")).Select(c => Int(c, "index")).ToArray();
                int samples = Int(expected, "samples");
                foreach (int[] selected in new[] { all.Reverse().ToArray(), new[] { all[all.Length - 1], all[1] } })
                foreach (int batch in new[] { 1, 3 })
                for (int start = 0; start <= samples; start++)
                for (int count = 0; count <= samples - start; count++)
                {
                    Check(reader.ReadSamples(group, selected, (ulong)start, (ulong)count, batch), channels, selected, start, count, MdfValueRepresentation.Raw);
                }
            }
        }

        [Fact]
        public void FrozenCorpusCoversEveryPartWidthOrderAndRefusal()
        {
            List<XElement> complex = Ids.Select(ReadCase).SelectMany(c => Items(c, "channels")).Where(c => Text(c, "kind") == "Complex").ToList();
            List<XElement> readable = complex.Where(c => Bool(c, "physicalReadable")).ToList();
            Assert.Equal(new[] { "128", "32", "64" }, readable.Select(c => Text(c, "bitCount")).Distinct().OrderBy(t => t, StringComparer.Ordinal));
            Assert.Equal(new[] { "15", "16" }, readable.Select(c => Text(c, "dataType")).Distinct().OrderBy(t => t, StringComparer.Ordinal));
            Assert.Contains(readable, c => Items(c, "shape").Any() && Bool(c, "inverse"));
            Assert.Contains(readable, c => Items(c, "shape").Any() && Int(c, "byteStride") * 8 > Int(c, "bitCount"));
            Assert.Contains(readable, c => Items(c, "valid").Any(r => r.Elements("item").Any(v => v.Value == "false")));
            Assert.Contains(readable, c => Items(c, "raw").Any(r => r.Elements("item").Any(p => p.Elements("item").Any(v => v.Value == "NaN"))));
            Assert.Contains(readable, c => Items(c, "raw").Any(r => r.Elements("item").Any(p => p.Elements("item").Any(v => v.Value == "-0"))));
            Assert.Equal(new[] { "Complex channels support only identity conversion.",
                "Only byte-aligned complex storage of two IEEE 16/32/64-bit parts is supported.",
                "Only byte-aligned complex storage of two IEEE 16/32/64-bit parts is supported." },
                complex.Where(c => !Bool(c, "physicalReadable")).Select(c => Text(c, "reason")).OrderBy(t => t, StringComparer.Ordinal));
        }

        private static void Check(IEnumerable<MdfSampleBatch> batches, List<XElement> channels, int[] selected, int start, int count,
            MdfValueRepresentation representation)
        {
            int ordinal = start;
            foreach (MdfSampleBatch batch in batches)
            {
                Assert.Equal((ulong)ordinal, batch.FirstSampleIndex);
                Assert.Equal(selected, batch.Channels.Select(c => c.Index));
                for (int column = 0; column < selected.Length; column++)
                {
                    XElement wanted = channels.Single(c => Int(c, "index") == selected[column]);
                    MdfValueKind kind = Kind(wanted);
                    List<List<List<string>>> values = Items(wanted, representation == MdfValueRepresentation.Raw ? "raw" : "physical")
                        .Select(r => r.Elements("item").Select(e => e.Elements("item").Select(v => v.Value).ToList()).ToList()).ToList();
                    List<List<bool>> valid = Items(wanted, "valid").Select(r => r.Elements("item").Select(v => v.Value == "true").ToList()).ToList();
                    for (int row = 0; row < batch.SampleCount; row++)
                    {
                        List<List<string>> elements = values[ordinal + row];
                        for (int element = 0; element < elements.Count; element++)
                        {
                            MdfSampleValue actual = !Items(wanted, "shape").Any() ? batch.GetValue(row, column) : batch.GetValue(row, column, (ulong)element);
                            Assert.Equal(kind, actual.Kind);
                            Assert.Equal(valid[ordinal + row][element], actual.IsValid);
                            List<string> parts = elements[element];
                            if (kind == MdfValueKind.Complex)
                            {
                                Same(parts[0], actual.RealPart);
                                Same(parts[1], actual.ImaginaryPart);
                                Assert.Throws<InvalidOperationException>(() => actual.FloatingPointValue);
                            }
                            else if (kind == MdfValueKind.UnsignedInteger) { Assert.Equal(ulong.Parse(parts[0], CultureInfo.InvariantCulture), actual.UnsignedValue); }
                            else if (kind == MdfValueKind.SignedInteger) { Assert.Equal(long.Parse(parts[0], CultureInfo.InvariantCulture), actual.SignedValue); }
                            else { Same(parts[0], actual.FloatingPointValue); }
                        }
                    }
                }
                ordinal += batch.SampleCount;
            }
            Assert.Equal(start + count, ordinal);
        }

        private static void Same(string text, double actual)
        {
            if (text == "NaN") { Assert.True(double.IsNaN(actual)); return; }
            double expected = text == "Infinity" ? double.PositiveInfinity : text == "-Infinity" ? double.NegativeInfinity
                : text == "-0" ? BitConverter.Int64BitsToDouble(long.MinValue) : double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(actual));
        }

        private static MdfValueKind Kind(XElement parent) { return (MdfValueKind)Enum.Parse(typeof(MdfValueKind), Text(parent, "kind")); }
        private static IEnumerable<XElement> Items(XElement parent, string name) { return parent.Element(name).Elements("item"); }
        private static string Text(XElement parent, string name) { return parent.Element(name).Value; }
        private static int Int(XElement parent, string name) { return int.Parse(parent.Element(name).Value, CultureInfo.InvariantCulture); }
        private static bool Bool(XElement parent, string name) { return parent.Element(name).Value == "true"; }

        internal static XElement Load()
        {
            using (FileStream stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "m5aa-expectations.json")))
            using (XmlDictionaryReader json = JsonReaderWriterFactory.CreateJsonReader(stream, XmlDictionaryReaderQuotas.Max))
            {
                return XElement.Load(json);
            }
        }

        private static XElement ReadCase(string id) { return Load().Element("cases").Elements("item").Single(c => Text(c, "id") == id); }
    }
}
