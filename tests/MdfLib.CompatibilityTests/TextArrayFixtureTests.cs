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
    // expectations frozen before the C# change (tools/fixtures/m5ab-expectations.json, build_m5ab.py): CANopen VLSD/MLSD
    // values, CN-template arrays of fixed CANopen/UTF-8/byte-array elements and fixed text/CANopen structure leaves.
    public sealed class TextArrayFixtureTests
    {
        private static readonly string[] Ids = { "m5ab-canopen-vlsd420", "m5ab-fixed-arrays420" };

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
        public void CatalogPublishesKindsShapesCalendarsAndComponents(string id)
        {
            XElement expected = ReadCase(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
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
                    Assert.Equal((MdfCalendarKind)Enum.Parse(typeof(MdfCalendarKind), Text(wanted, "calendarKind")), channel.CalendarKind);
                    Assert.True(channel.CanReadRaw, channel.UnsupportedReason);
                    Assert.True(channel.CanReadPhysical);
                    Assert.Equal(MdfConversionKind.Identity, channel.Conversion.Kind);
                    Assert.Equal(Bool(wanted, "component"), channel.Parent != null);
                    Assert.Equal(Int(wanted, "componentCount"), channel.Components.Count);
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
        public void EveryElementValueAndValidityMatchesTheFrozenValues(string id, int batchSize, object representationValue)
        {
            MdfValueRepresentation representation = (MdfValueRepresentation)representationValue;
            XElement expected = ReadCase(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                List<XElement> channels = Items(expected, "channels").ToList();
                int[] all = channels.Select(c => Int(c, "index")).ToArray();
                int samples = Int(expected, "samples");
                Check(group, reader.ReadSamples(group, all, batchSize: batchSize, representation: representation), channels, all, 0, samples, representation);
                foreach (int index in all)
                {
                    Check(group, reader.ReadSamples(group, new[] { index }, batchSize: batchSize, representation: representation),
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
                foreach (int[] selected in new[] { all.Reverse().ToArray(), new[] { all[all.Length - 1], all[1], all[2] } })
                foreach (int batch in new[] { 1, 3 })
                for (int start = 0; start <= samples; start++)
                for (int count = 0; count <= samples - start; count++)
                {
                    Check(group, reader.ReadSamples(group, selected, (ulong)start, (ulong)count, batch), channels, selected, start, count, MdfValueRepresentation.Raw);
                }
            }
        }

        [Fact]
        public void FrozenCorpusCoversEveryStorageShapeAndCalendarOutcome()
        {
            List<XElement> channels = Ids.Select(ReadCase).SelectMany(c => Items(c, "channels")).ToList();
            List<XElement> calendar = channels.Where(c => Text(c, "calendarKind") != "None").ToList();
            Assert.Equal(new[] { "0", "1", "5" }, calendar.Select(c => Text(c, "cnType")).Distinct().OrderBy(t => t, StringComparer.Ordinal));
            Assert.Contains(calendar, c => Items(c, "shape").Count() == 2 && Bool(c, "inverse"));
            Assert.Contains(calendar, c => Bool(c, "component"));
            Assert.Contains(calendar, c => Items(c, "calendar").Any(r => r.Elements("item").Any(IsNull)));
            Assert.Contains(calendar, c => Items(c, "valid").Any(r => r.Elements("item").Any(v => v.Value == "false")));
            Assert.Contains(channels, c => Text(c, "kind") == "Text" && Items(c, "shape").Any());
            Assert.Contains(channels, c => Text(c, "kind") == "Text" && Bool(c, "component"));
            Assert.Contains(channels, c => Text(c, "dataType") == "10" && Items(c, "shape").Any() && Int(c, "byteStride") * 8 > Int(c, "bitCount"));
        }

        private static void Check(MdfChannelGroup group, IEnumerable<MdfSampleBatch> batches, List<XElement> channels, int[] selected,
            int start, int count, MdfValueRepresentation representation)
        {
            int ordinal = start;
            foreach (MdfSampleBatch batch in batches)
            {
                Assert.Equal((ulong)ordinal, batch.FirstSampleIndex);
                Assert.Equal(selected, batch.Channels.Select(c => c.Index));
                for (int column = 0; column < selected.Length; column++)
                {
                    XElement wanted = channels.Single(c => Int(c, "index") == selected[column]);
                    MdfChannel channel = group.Channels[selected[column]];
                    MdfValueKind kind = Kind(wanted);
                    bool array = Items(wanted, "shape").Any();
                    List<List<string>> values = Items(wanted, representation == MdfValueRepresentation.Raw ? "raw" : "physical")
                        .Select(r => r.Elements("item").Select(v => v.Value).ToList()).ToList();
                    List<List<bool>> valid = Items(wanted, "valid").Select(r => r.Elements("item").Select(v => v.Value == "true").ToList()).ToList();
                    List<List<XElement>> calendar = wanted.Element("calendar") == null ? null
                        : Items(wanted, "calendar").Select(r => r.Elements("item").ToList()).ToList();
                    for (int row = 0; row < batch.SampleCount; row++)
                    {
                        List<string> elements = values[ordinal + row];
                        for (int element = 0; element < elements.Count; element++)
                        {
                            MdfSampleValue actual = array ? batch.GetValue(row, column, (ulong)element) : batch.GetValue(row, column);
                            Assert.Equal(kind, actual.Kind);
                            Assert.Equal(valid[ordinal + row][element], actual.IsValid);
                            string text = elements[element];
                            if (kind == MdfValueKind.ByteArray) { Assert.Equal(text, Hex(actual.GetByteArrayValue())); }
                            else if (kind == MdfValueKind.Text) { Assert.Equal(text, actual.TextValue); }
                            else if (kind == MdfValueKind.UnsignedInteger) { Assert.Equal(ulong.Parse(text, CultureInfo.InvariantCulture), actual.UnsignedValue); }
                            else { Assert.Equal(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture), actual.FloatingPointValue); }
                            if (calendar != null)
                            {
                                XElement decoded = calendar[ordinal + row][element];
                                DateTime value;
                                bool ok = MdfCalendar.TryDecode(channel, actual, out value);
                                Assert.Equal(!IsNull(decoded), ok);
                                if (ok) { Assert.Equal(decoded.Value, value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture)); }
                            }
                        }
                    }
                }
                ordinal += batch.SampleCount;
            }
            Assert.Equal(start + count, ordinal);
        }

        private static bool IsNull(XElement element) { return (string)element.Attribute("type") == "null"; }
        private static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant(); }
        private static MdfValueKind Kind(XElement parent) { return (MdfValueKind)Enum.Parse(typeof(MdfValueKind), Text(parent, "kind")); }
        private static IEnumerable<XElement> Items(XElement parent, string name) { return parent.Element(name).Elements("item"); }
        private static string Text(XElement parent, string name) { return parent.Element(name).Value; }
        private static int Int(XElement parent, string name) { return int.Parse(parent.Element(name).Value, CultureInfo.InvariantCulture); }
        private static bool Bool(XElement parent, string name) { return parent.Element(name).Value == "true"; }

        private static XElement Load()
        {
            using (FileStream stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "m5ab-expectations.json")))
            using (XmlDictionaryReader json = JsonReaderWriterFactory.CreateJsonReader(stream, XmlDictionaryReaderQuotas.Max))
            {
                return XElement.Load(json);
            }
        }

        private static XElement ReadCase(string id) { return Load().Element("cases").Elements("item").Single(c => Text(c, "id") == id); }
    }
}
