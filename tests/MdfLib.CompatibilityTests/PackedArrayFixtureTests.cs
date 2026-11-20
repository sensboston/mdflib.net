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
    // every expectation below was frozen before the packed-element change existed (tools/fixtures/m5u-expectations.json,
    //): integer array elements of 1..64 bits at the CN bit offset inside byte-strided
    // containers (LE/BE, padded, inverse, per-element invalidation, linear CC). supersedes one frozen refusal text
    // (tools/fixtures/m5w-expectations.json `supersedes`).
    public sealed class PackedArrayFixtureTests
    {
        private static readonly string[] Ids = { "m5u-packed410", "m5u-packed411", "m5u-packed420" };

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
        public void FrozenCorpusCoversBitOffsetsWidthsOrdersAndRefusals()
        {
            List<XElement> all = Ids.SelectMany(id => Items(ReadCase(id), "channels")).ToList();
            List<XElement> arrays = all.Where(c => Items(c, "shape").Any() && Bool(c, "readable")).ToList();
            Assert.Equal(13, arrays.Count);
            Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 7 }, arrays.Select(c => Int(c, "bitOffset")).Distinct().OrderBy(o => o));
            Assert.Contains(arrays, c => Int(c, "containerBytes") == 9);
            Assert.Contains(arrays, c => Int(c, "dataType") % 2 == 1 && Bool(c, "inverse"));
            Assert.Contains(arrays, c => Int(c, "invalidationBase") != 0 && Int(c, "dataType") % 2 == 1);
            Assert.Contains(arrays, c => Int(c, "byteStride") > Int(c, "containerBytes"));
            Assert.Contains(arrays, c => c.Element("conversion") != null);
            Assert.Equal(new[]
            {
                "Array byte stride must be at least the element's byte width.",
                "Only byte-aligned IEEE 16/32/64-bit floating-point storage is supported.",
            }, all.Where(c => !Bool(c, "readable")).Select(c => Text(c, "reason")).OrderBy(r => r, StringComparer.Ordinal));
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
                    List<List<bool>> valid = Items(wanted, "valid").Select(row => row.Elements("item").Select(v => v.Value == "true").ToList()).ToList();
                    int elements = Int(wanted, "elementCount");
                    bool array = Items(wanted, "shape").Any();
                    for (int row = 0; row < batch.SampleCount; row++)
                    {
                        int sample = ordinal + row;
                        List<string> values = Items(wanted, raw ? "raw" : "physical").ElementAt(sample).Elements("item").Select(e => e.Value).ToList();
                        Assert.Equal(elements, values.Count);
                        if (!array)
                        {
                            Same(kind, values[0], valid[sample][0], batch.GetValue(row, column));
                            continue;
                        }
                        Assert.Throws<InvalidOperationException>(() => batch.GetValue(row, column));
                        for (int element = 0; element < elements; element++)
                        {
                            Same(kind, values[element], valid[sample][element], batch.GetValue(row, column, (ulong)element));
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

        [Fact]
        public void M5WSupersedesExactlyOneFrozenM5UReasonText()
        {
            XElement set = Assert.Single(Load("m5w-expectations.json").Element("supersedes").Elements("item"));
            Assert.Equal("m5u-packed410", Text(set, "id"));
            // The frozen-file sha256 binding is checked byte-exactly by tools/fixtures/check_m5w_reference.py (checkouts may
            // normalize line endings); here only its presence.
            Assert.Equal(64, Text(set, "m5uExpectationsSha256").Length);
            XElement entry = Assert.Single(Items(set, "channels"));
            XElement frozen = Items(Load("m5u-expectations.json").Element("cases").Elements("item").Single(c => Text(c, "id") == "m5u-packed410"), "channels")
                .Single(c => Int(c, "index") == Int(entry, "index"));
            Assert.Equal("fodd", Text(entry, "key"));
            Assert.Equal(Text(frozen, "name"), Text(entry, "key"));
            Assert.Equal(Text(frozen, "reason"), Text(entry, "m5uReason"));
            Assert.False(Bool(frozen, "readable"));
            Assert.False(Bool(entry, "readable"));
            Assert.Equal("Only byte-aligned IEEE 16/32/64-bit floating-point storage is supported.", Text(entry, "reason"));
            Assert.Equal(Text(entry, "reason"), Text(Items(ReadCase("m5u-packed410"), "channels").Single(c => Int(c, "index") == Int(entry, "index")), "reason"));
        }

        private static XElement Load(string file)
        {
            using (FileStream stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, file)))
            using (XmlDictionaryReader json = JsonReaderWriterFactory.CreateJsonReader(stream, XmlDictionaryReaderQuotas.Max))
            {
                return XElement.Load(json);
            }
        }

        private static XElement ReadCase(string id)
        {
            XElement expected = Load("m5u-expectations.json").Element("cases").Elements("item").Single(c => Text(c, "id") == id);
            // replaces the refusal text of one frozen outcome; it stays a refusal.
            foreach (XElement set in Load("m5w-expectations.json").Element("supersedes").Elements("item").Where(s => Text(s, "id") == id))
            foreach (XElement entry in Items(set, "channels"))
            {
                XElement channel = Items(expected, "channels").Single(c => Int(c, "index") == Int(entry, "index"));
                Assert.Equal(Text(entry, "key"), Text(channel, "name"));
                Assert.Equal(Text(entry, "m5uReason"), Text(channel, "reason"));
                channel.Element("readable").Value = entry.Element("readable").Value;
                channel.Element("reason").Value = Text(entry, "reason");
            }
            return expected;
        }
    }
}
