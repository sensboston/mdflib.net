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
    // every expectation below was frozen before MDF 4.00 was accepted (tools/fixtures/m5y-expectations.json,
    //): MDF 4.10-layout blocks labelled 4.00 with nonzero reserved ID bytes 60..63.
    public sealed class Mdf400FixtureTests
    {
        private static readonly string[] Ids = { "m5y-sorted400", "m5y-list400", "m5y-interleaved400" };

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
        public void HeaderIsReadFromThe400IdentificationAndHdBlocks(string id)
        {
            XElement header = ReadCase(id).Element("header");
            byte[] bytes = FixtureCorpus.Read(id);
            Assert.NotEqual(0, bytes[60] | bytes[61] | bytes[62] | bytes[63]); // Would be unfinalized flags from 4.10.
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfFileHeader actual = reader.Header;
                Assert.Equal("4.00", actual.Version);
                Assert.Equal(Text(header, "programIdentifier"), actual.ProgramIdentifier);
                Assert.Equal(ulong.Parse(Text(header, "rawStartTimeNanoseconds"), CultureInfo.InvariantCulture), actual.RawStartTimeNanoseconds);
                Assert.Equal(Int(header, "utcOffsetMinutes"), actual.UtcOffsetMinutes);
                Assert.Equal((byte)Int(header, "timeFlags"), actual.TimeFlags);
                Assert.Equal(0, actual.DaylightSavingOffsetMinutes);
                Assert.Null(actual.CodePage);
                Assert.False(actual.DefaultDataIsBigEndian);
            }
        }

        [Theory]
        [MemberData(nameof(AllFiles))]
        public void CatalogPublishesGroupsNamesKindsShapesAndConversions(string id)
        {
            XElement expected = ReadCase(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
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
                        List<ulong> shape = Items(wanted, "shape").Select(s => ulong.Parse(s.Value, CultureInfo.InvariantCulture)).ToList();
                        if (shape.Count == 0) { Assert.Null(channel.Array); }
                        else { Assert.Equal(shape, channel.Array.Dimensions); }
                        XElement conversion = wanted.Element("conversion");
                        if (conversion == null) { Assert.Equal(MdfConversionKind.Identity, channel.Conversion.Kind); }
                        else
                        {
                            Assert.Equal(MdfConversionKind.Linear, channel.Conversion.Kind);
                            Assert.Equal(Real(Text(conversion, "factor")), channel.Conversion.Factor);
                            Assert.Equal(Real(Text(conversion, "offset")), channel.Conversion.Offset);
                        }
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
        public void FrozenCorpusCoversStorageLayoutsAndReservedBytes()
        {
            List<XElement> cases = Ids.Select(ReadCase).ToList();
            Assert.All(cases, c => Assert.Equal("4.00", Text(c, "version")));
            Assert.Equal(new[] { "DT", "DL", "DT" }, cases.Select(c => Text(c, "storage")));
            Assert.Equal(3, cases.Select(c => Text(c.Element("header"), "reservedTail")).Distinct().Count());
            Assert.DoesNotContain(cases, c => Text(c.Element("header"), "reservedTail") == "00000000");
            Assert.Equal(1, Int(cases[2], "recordIdBytes"));
            List<XElement> channels = cases.SelectMany(c => Items(c, "groups").SelectMany(g => Items(g, "channels"))).ToList();
            Assert.Equal(16, channels.Count);
            Assert.Contains(channels, c => Text(c, "kind") == "Text");
            Assert.Contains(channels, c => Items(c, "shape").Any());
            Assert.Contains(channels, c => Items(c, "valid").Any(v => v.Value == "false"));
            Assert.Equal(2, channels.Count(c => c.Element("conversion") != null));
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
                    List<List<string>> values = Items(wanted, raw ? "raw" : "physical").Select(r => r.Elements("item").Select(v => v.Value).ToList()).ToList();
                    List<bool> valid = Items(wanted, "valid").Select(v => v.Value == "true").ToList();
                    bool array = Items(wanted, "shape").Any();
                    for (int row = 0; row < batch.SampleCount; row++)
                    {
                        int sample = ordinal + row;
                        if (!array)
                        {
                            Same(kind, values[sample][0], valid[sample], batch.GetValue(row, column));
                            continue;
                        }
                        Assert.Throws<InvalidOperationException>(() => batch.GetValue(row, column));
                        for (int element = 0; element < values[sample].Count; element++)
                        {
                            Same(kind, values[sample][element], valid[sample], batch.GetValue(row, column, (ulong)element));
                        }
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
            else if (kind == MdfValueKind.Text) { Assert.Equal(text, actual.TextValue); }
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
            using (FileStream stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "m5y-expectations.json")))
            using (XmlDictionaryReader json = JsonReaderWriterFactory.CreateJsonReader(stream, XmlDictionaryReaderQuotas.Max))
            {
                return XElement.Load(json).Element("cases").Elements("item").Single(c => Text(c, "id") == id);
            }
        }
    }
}
