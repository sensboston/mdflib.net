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
    // every expectation below was frozen before the binary16 change existed (tools/fixtures/m5w-expectations.json,
    //): byte-aligned IEEE binary16 scalars, master, arrays (padded, inverse, per-element
    // invalidation) and linear CC, with subnormals, signed zeros, infinities and NaN payloads.
    public sealed class HalfFloatFixtureTests
    {
        private const string Reason = "Only byte-aligned IEEE 16/32/64-bit floating-point storage is supported.";
        private static readonly string[] Ids = { "m5w-half410", "m5w-half411", "m5w-half420" };

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
        public void CatalogPublishesKindsShapesReadabilityAndExactRefusalReasons(string id)
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
        public void EveryValueAndValidityMatchesTheFrozenValues(string id, int batchSize, object representationValue)
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
                foreach (XElement channel in channels)
                {
                    int[] only = { Int(channel, "index") };
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
                foreach (int batch in new[] { 1, 4, 1024 })
                for (int start = 0; start <= samples; start++)
                for (int count = 0; count <= samples - start; count++)
                {
                    Check(reader.ReadSamples(group, selected, (ulong)start, (ulong)count, batch), channels, selected, start, count, MdfValueRepresentation.Raw);
                }
            }
        }

        [Theory]
        [MemberData(nameof(AllFiles))]
        public void FrozenBitPatternsDecodeToTheFrozenValuesByTheBinary16Definition(string id)
        {
            // Independent of the library: each frozen 16-bit pattern, decoded by the IEEE definition, is the frozen raw value.
            foreach (XElement channel in Items(ReadCase(id), "channels").Where(c => c.Element("patterns") != null))
            {
                List<List<string>> patterns = Rows(channel, "patterns");
                List<List<string>> raw = Rows(channel, "raw");
                for (int row = 0; row < patterns.Count; row++)
                for (int element = 0; element < patterns[row].Count; element++)
                {
                    int bits = int.Parse(patterns[row][element].Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    double definition = Definition(bits);
                    double frozen = Real(raw[row][element]);
                    if (double.IsNaN(frozen)) { Assert.True(double.IsNaN(definition)); }
                    else { Assert.Equal(BitConverter.DoubleToInt64Bits(frozen), BitConverter.DoubleToInt64Bits(definition)); }
                }
            }
        }

        [Theory]
        [MemberData(nameof(AllFiles))]
        public void RefusedFieldsRefuseReadsWhileNeighboursRead(string id)
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
                Assert.Equal(Int(expected, "samples"), reader.ReadSamples(group, readable.Reverse().ToArray()).Sum(b => b.SampleCount));
            }
        }

        [Fact]
        public void FrozenCorpusCoversSpecialValuesOrdersLayoutsAndRefusals()
        {
            List<XElement> all = Ids.SelectMany(id => Items(ReadCase(id), "channels")).ToList();
            List<XElement> halves = all.Where(c => c.Element("patterns") != null).ToList();
            Assert.Equal(10, halves.Count);
            // The 20 corpus patterns cycle through the value channels; the binary16 master adds its own timestamps.
            HashSet<string> patterns = new HashSet<string>(halves.Where(c => !Bool(c, "master")).SelectMany(c => Rows(c, "patterns").SelectMany(r => r)));
            Assert.Equal(20, patterns.Count);
            HashSet<string> values = new HashSet<string>(halves.SelectMany(c => Rows(c, "raw").SelectMany(r => r)));
            Assert.Subset(values, new HashSet<string> { "-0", "Infinity", "-Infinity", "NaN", "65504.0", "-65504.0", "5.960464477539063e-08" });
            Assert.Contains(halves, c => Int(c, "dataType") == 5 && Bool(c, "inverse"));
            Assert.Contains(halves, c => Bool(c, "master"));
            Assert.Contains(halves, c => Int(c, "invalidationBase") != 0 && Int(c, "byteStride") > 2);
            Assert.Contains(halves, c => Int(c, "dataType") == 5 && Items(c, "shape").Any() && Int(c, "byteStride") > 2);
            Assert.Equal(2, halves.Count(c => c.Element("conversion") != null));
            Assert.Equal(new[] { Reason, Reason }, all.Where(c => !Bool(c, "readable")).Select(c => Text(c, "reason")));
        }

        private static double Definition(int bits)
        {
            double sign = (bits & 0x8000) != 0 ? -1.0 : 1.0;
            int exponent = bits >> 10 & 31, fraction = bits & 1023;
            if (exponent == 31) { return fraction == 0 ? sign * double.PositiveInfinity : double.NaN; }
            if (exponent == 0) { return sign * fraction * Math.Pow(2, -24); }
            return sign * (1024 + fraction) * Math.Pow(2, exponent - 25);
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
                    List<List<string>> valid = Rows(wanted, "valid");
                    List<List<string>> values = Rows(wanted, raw ? "raw" : "physical");
                    int elements = Int(wanted, "elementCount");
                    bool array = Items(wanted, "shape").Any();
                    for (int row = 0; row < batch.SampleCount; row++)
                    {
                        int sample = ordinal + row;
                        if (!array)
                        {
                            Same(kind, values[sample][0], valid[sample][0] == "true", batch.GetValue(row, column));
                            continue;
                        }
                        Assert.Throws<InvalidOperationException>(() => batch.GetValue(row, column));
                        for (int element = 0; element < elements; element++)
                        {
                            Same(kind, values[sample][element], valid[sample][element] == "true", batch.GetValue(row, column, (ulong)element));
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
            else if (text == "NaN") { Assert.True(double.IsNaN(actual.FloatingPointValue)); } // NaN is a value, not invalidity.
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

        private static List<List<string>> Rows(XElement parent, string name)
        {
            return Items(parent, name).Select(row => row.Elements("item").Select(v => v.Value).ToList()).ToList();
        }

        private static MdfValueKind Kind(XElement parent) { return (MdfValueKind)Enum.Parse(typeof(MdfValueKind), Text(parent, "kind")); }
        private static IEnumerable<XElement> Items(XElement parent, string name) { return parent.Element(name).Elements("item"); }
        private static string Text(XElement parent, string name) { return parent.Element(name).Value; }
        private static int Int(XElement parent, string name) { return int.Parse(parent.Element(name).Value, CultureInfo.InvariantCulture); }
        private static bool Bool(XElement parent, string name) { return parent.Element(name).Value == "true"; }

        private static XElement ReadCase(string id)
        {
            using (FileStream stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "m5w-expectations.json")))
            using (XmlDictionaryReader json = JsonReaderWriterFactory.CreateJsonReader(stream, XmlDictionaryReaderQuotas.Max))
            {
                return XElement.Load(json).Element("cases").Elements("item").Single(c => Text(c, "id") == id);
            }
        }
    }
}
