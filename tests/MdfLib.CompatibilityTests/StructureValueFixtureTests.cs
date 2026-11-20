// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using MdfLib.TestSupport;
using Xunit;

namespace MdfLib.CompatibilityTests
{
    // every expectation below was frozen before the aggregate structure-value change existed
    // (tools/fixtures/m5l-expectations.json). Parent raw values are hex record
    // windows, leaf values are element lists, so the JSON is read through the BCL JSON-to-XML mapping.
    public sealed class StructureValueFixtureTests
    {
        private static readonly string[] Ids = { "m5l-structures410", "m5l-lists411", "m5l-paired420", "m5l-interleaved420" };

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
        public void CatalogHierarchyKindsAndReasonsAreExactlyTheFrozenOnes(string id)
        {
            XElement expected = ReadCase(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                Assert.Equal(Text(expected, "version"), reader.Header.Version);
                IReadOnlyList<MdfDataGroup> catalog = reader.ReadMetadata();
                foreach (XElement values in Items(expected, "groups"))
                {
                    MdfChannelGroup group = catalog[Int(values, "dataGroupIndex")].ChannelGroups[Int(values, "channelGroupIndex")];
                    Assert.True(group.CanReadSamples, group.UnsupportedReason);
                    Assert.Equal((ulong)Int(values, "samples"), group.SampleCount);
                    Assert.Equal((ulong)(Int(values, "dataBytes") + Int(values, "invalidationBytes")), group.RecordByteCount);
                    List<XElement> channels = Items(values, "channels").ToList();
                    Assert.Equal(channels.Count, group.Channels.Count);
                    for (int index = 0; index < channels.Count; index++)
                    {
                        XElement wanted = channels[index];
                        MdfChannel channel = group.Channels[index];
                        Assert.Equal(Int(wanted, "index"), channel.Index);
                        Assert.Equal(Text(wanted, "name"), channel.Name);
                        Assert.Equal((uint)Int(wanted, "bitCount"), channel.BitCount);
                        Assert.Equal((MdfValueKind)Enum.Parse(typeof(MdfValueKind), Text(wanted, "kind")), channel.ValueKind);
                        int parent = Int(wanted, "parent");
                        if (parent < 0) { Assert.Null(channel.Parent); } else { Assert.Same(group.Channels[parent], channel.Parent); }
                        Assert.Equal(Items(wanted, "components").Select(c => int.Parse(c.Value, CultureInfo.InvariantCulture)),
                            channel.Components.Select(c => c.Index));
                        bool readable = Bool(wanted, "readable");
                        Assert.Equal(readable, channel.CanReadRaw);
                        Assert.Equal(readable, channel.CanReadPhysical);
                        Assert.Equal(OptionalText(wanted, "reason"), channel.UnsupportedReason);
                        List<ulong> shape = Items(wanted, "shape").Select(d => ulong.Parse(d.Value, CultureInfo.InvariantCulture)).ToList();
                        if (shape.Count == 0) { Assert.Null(channel.Array); } else { Assert.Equal(shape, channel.Array.Dimensions); }
                        Assert.Equal(Text(wanted, "conversion") == "linear" ? MdfConversionKind.Linear : MdfConversionKind.Identity,
                            channel.Conversion.Kind);
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(FullReads))]
        public void EveryParentWindowLeafValueAndValidityMatchesTheFrozenValues(string id, int batchSize, object representationValue)
        {
            MdfValueRepresentation representation = (MdfValueRepresentation)representationValue;
            XElement expected = ReadCase(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                IReadOnlyList<MdfDataGroup> catalog = reader.ReadMetadata();
                foreach (XElement values in Items(expected, "groups"))
                {
                    MdfChannelGroup group = catalog[Int(values, "dataGroupIndex")].ChannelGroups[Int(values, "channelGroupIndex")];
                    List<XElement> channels = Items(values, "channels").ToList();
                    int[] selected = channels.Where(c => Bool(c, "readable")).Select(c => Int(c, "index")).ToArray();
                    int samples = Int(values, "samples");
                    Check(reader.ReadSamples(group, selected, batchSize: batchSize, representation: representation), channels, selected, 0, samples, representation);
                }
            }
        }

        [Theory]
        [MemberData(nameof(AllFiles))]
        public void ReorderedSingleAndMixedSelectionsMatchForEverySubrangeAndRefusalsStayRefused(string id)
        {
            XElement expected = ReadCase(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                IReadOnlyList<MdfDataGroup> catalog = reader.ReadMetadata();
                foreach (XElement values in Items(expected, "groups"))
                {
                    MdfChannelGroup group = catalog[Int(values, "dataGroupIndex")].ChannelGroups[Int(values, "channelGroupIndex")];
                    List<XElement> channels = Items(values, "channels").ToList();
                    int samples = Int(values, "samples");
                    int[] readable = channels.Where(c => Bool(c, "readable")).Select(c => Int(c, "index")).ToArray();
                    List<int[]> selections = new List<int[]> { readable.Reverse().ToArray() };
                    selections.AddRange(readable.Select(i => new[] { i }));
                    foreach (int[] selected in selections)
                    foreach (int batch in new[] { 1, 3, 1024 })
                    for (int start = 0; start <= samples; start++)
                    for (int count = 0; count <= samples - start; count++)
                    {
                        Check(reader.ReadSamples(group, selected, (ulong)start, (ulong)count, batch), channels, selected, start, count, MdfValueRepresentation.Raw);
                    }
                    foreach (XElement rejected in channels.Where(c => !Bool(c, "readable")))
                    {
                        int index = Int(rejected, "index");
                        Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { index }).ToList());
                        Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 0, index }).ToList());
                    }
                }
            }
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
                    XElement wanted = channels[selected[column]];
                    bool raw = representation == MdfValueRepresentation.Raw;
                    List<XElement> rows = Items(wanted, raw ? "raw" : "physical").ToList();
                    List<bool> valid = Items(wanted, "valid").Select(v => v.Value == "true").ToList();
                    MdfValueKind kind = (MdfValueKind)Enum.Parse(typeof(MdfValueKind), Text(wanted, "kind"));
                    for (int row = 0; row < batch.SampleCount; row++)
                    {
                        int sample = ordinal + row;
                        if (Bool(wanted, "structure"))
                        {
                            MdfSampleValue value = batch.GetValue(row, column);
                            Assert.Equal(MdfValueKind.ByteArray, value.Kind);
                            Assert.Equal(valid[sample], value.IsValid);
                            Assert.Equal(rows[sample].Value, Hex(value.GetByteArrayValue()));
                            Assert.Equal(rows[sample].Value, Hex(batch.GetValue(row, column, 0).GetByteArrayValue()));
                            continue;
                        }
                        List<string> elements = rows[sample].Elements("item").Select(e => e.Value).ToList();
                        if (elements.Count == 1 && Items(wanted, "shape").Count() == 0)
                        {
                            Same(kind, elements[0], valid[sample], batch.GetValue(row, column));
                            continue;
                        }
                        Assert.Throws<InvalidOperationException>(() => batch.GetValue(row, column));
                        for (int element = 0; element < elements.Count; element++)
                        { Same(kind, elements[element], valid[sample], batch.GetValue(row, column, (ulong)element)); }
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

        private static string Hex(byte[] bytes)
        {
            StringBuilder text = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes) { text.Append(value.ToString("x2", CultureInfo.InvariantCulture)); }
            return text.ToString();
        }

        private static IEnumerable<XElement> Items(XElement parent, string name) { return parent.Element(name).Elements("item"); }
        private static string Text(XElement parent, string name) { return parent.Element(name).Value; }
        private static int Int(XElement parent, string name) { return int.Parse(parent.Element(name).Value, CultureInfo.InvariantCulture); }
        private static bool Bool(XElement parent, string name) { return parent.Element(name).Value == "true"; }
        private static string OptionalText(XElement parent, string name)
        {
            XElement element = parent.Element(name);
            return (string)element.Attribute("type") == "null" ? null : element.Value;
        }

        private static XElement ReadCase(string id)
        {
            using (FileStream stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "m5l-expectations.json")))
            using (XmlDictionaryReader json = JsonReaderWriterFactory.CreateJsonReader(stream, XmlDictionaryReaderQuotas.Max))
            {
                return XElement.Load(json).Element("cases").Elements("item").Single(c => Text(c, "id") == id);
            }
        }
    }
}
