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
    // every expectation below was frozen before the MIME change existed (tools/fixtures/m5s-expectations.json,
    //): data types 11/12 are byte arrays in fixed, VLSD and MLSD storage; MIME type = unit.
    // supersedes the two frozen CANopen refusals (tools/fixtures/m5z-expectations.json `supersedes`,
    //): they read their frozen field bytes; m5s-expectations.json is unchanged.
    public sealed class MimeChannelFixtureTests
    {
        private static readonly string[] Ids = { "m5s-mime410", "m5s-mime-dl411", "m5s-mime-vlsdcg420", "m5s-mime-ld420" };

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
        public void CatalogPublishesMimeChannelsAsByteArraysWithTheirUnits(string id)
        {
            XElement expected = ReadCase(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                Assert.Equal(Text(expected, "version"), reader.Header.Version);
                MdfDataGroup dataGroup = Assert.Single(reader.ReadMetadata());
                List<XElement> groups = Items(expected, "groups").ToList();
                List<XElement> vlsd = Items(expected, "vlsdGroups").ToList();
                Assert.Equal(groups.Count + vlsd.Count, dataGroup.ChannelGroups.Count);
                foreach (XElement storage in vlsd)
                {
                    MdfChannelGroup group = dataGroup.ChannelGroups.Single(g => g.Name == Text(storage, "name"));
                    Assert.False(group.CanReadSamples);
                    Assert.Equal(Text(storage, "reason"), group.UnsupportedReason);
                }
                foreach (XElement wantedGroup in groups)
                {
                    MdfChannelGroup group = dataGroup.ChannelGroups.Single(g => g.Name == Text(wantedGroup, "name"));
                    Assert.True(group.CanReadSamples, group.UnsupportedReason);
                    Assert.Equal((ulong)Int(wantedGroup, "samples"), group.SampleCount);
                    List<XElement> channels = Items(wantedGroup, "channels").ToList();
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
                        if (Text(wanted, "unit").Length != 0) { Assert.Equal(Text(wanted, "unit"), channel.Unit); }
                        Assert.Equal(wanted.Element("conversion") != null ? MdfConversionKind.Linear : MdfConversionKind.Identity, channel.Conversion.Kind);
                        Assert.Null(channel.Array);
                        Assert.Empty(channel.Components);
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(FullReads))]
        public void EveryMimeValueAndValidityMatchesTheFrozenValues(string id, int batchSize, object representationValue)
        {
            MdfValueRepresentation representation = (MdfValueRepresentation)representationValue;
            XElement expected = ReadCase(id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                foreach (XElement wantedGroup in Items(expected, "groups"))
                {
                    MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups.Single(g => g.Name == Text(wantedGroup, "name"));
                    List<XElement> channels = Items(wantedGroup, "channels").Where(c => Bool(c, "readable")).ToList();
                    int[] selected = channels.Select(c => Int(c, "index")).ToArray();
                    int samples = Int(wantedGroup, "samples");
                    Check(reader.ReadSamples(group, selected, batchSize: batchSize, representation: representation), channels, selected, 0, samples);
                    foreach (XElement mime in channels.Where(IsMime))
                    {
                        int[] only = { Int(mime, "index") };
                        Check(reader.ReadSamples(group, only, batchSize: batchSize, representation: representation), channels, only, 0, samples);
                    }
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
                foreach (XElement wantedGroup in Items(expected, "groups"))
                {
                    MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups.Single(g => g.Name == Text(wantedGroup, "name"));
                    List<XElement> channels = Items(wantedGroup, "channels").Where(c => Bool(c, "readable")).ToList();
                    int samples = Int(wantedGroup, "samples");
                    int[] all = channels.Select(c => Int(c, "index")).ToArray();
                    List<int[]> selections = new List<int[]> { all.Reverse().ToArray(), new[] { all[all.Length - 1], all[0] } };
                    selections.AddRange(all.Select(i => new[] { i }));
                    foreach (int[] selected in selections)
                    foreach (int batch in new[] { 1, 3, 1024 })
                    for (int start = 0; start <= samples; start++)
                    for (int count = 0; count <= samples - start; count++)
                    {
                        Check(reader.ReadSamples(group, selected, (ulong)start, (ulong)count, batch), channels, selected, start, count);
                    }
                }
            }
        }

        [Fact]
        public void RefusedChannelsRefuseReadsWhileMimeNeighboursRead()
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read("m5s-mime410"), false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 5 }));
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 1, 5 }));
                Assert.Equal(5, reader.ReadSamples(group, new[] { 3, 1, 2 }).Sum(b => b.SampleCount));
                Assert.Equal(5, reader.ReadSamples(group, new[] { 6, 1 }).Sum(b => b.SampleCount)); // CANopen date reads.
            }
        }

        [Fact]
        public void FrozenCorpusCoversEveryByteArrayStorageRoleAndTheRefusals()
        {
            List<XElement> all = Ids.SelectMany(id => Items(ReadCase(id), "groups")).SelectMany(g => Items(g, "channels")).ToList();
            List<XElement> mime = all.Where(c => IsMime(c) && Bool(c, "readable")).ToList();
            Assert.Equal(8, mime.Count);
            Assert.Equal(new[] { "0", "1", "5" }, mime.Select(c => Text(c, "cnType")).Distinct().OrderBy(t => t));
            Assert.Equal(new[] { "CG", "DL", "LD", "SD" }, mime.Where(c => c.Element("signalData") != null)
                .Select(c => Text(c.Element("signalData"), "root")).OrderBy(t => t));
            Assert.Equal(new[] { "11", "12" }, mime.Select(c => Text(c, "dataType")).Distinct().OrderBy(t => t));
            Assert.Equal(new[] { "12" }, all.Where(c => !Bool(c, "readable")).Select(c => Text(c, "dataType")).OrderBy(t => t));
            Assert.Contains(mime, c => Items(c, "valid").Any(v => v.Value == "false") && Text(c, "cnType") == "0");
            Assert.Contains(mime, c => Items(c, "valid").Any(v => v.Value == "false") && Text(c, "cnType") == "1");
        }

        [Fact]
        public void M5AASupersedesExactlyTheFrozenComplexRefusal()
        {
            XElement entry = Assert.Single(ComplexSupersessions());
            Assert.Equal("m5s-mime-ld420/cplx", Text(entry, "id") + "/" + Text(entry, "key"));
            XElement frozen = Items(LoadCase("m5s-expectations.json", "m5s-mime-ld420"), "groups").SelectMany(g => Items(g, "channels"))
                .Single(c => Text(c, "name") == "cplx");
            Assert.False(Bool(frozen, "readable"));
            Assert.Equal("15", Text(frozen, "dataType"));
            Assert.Equal(Text(entry, "index"), Text(frozen, "index"));
            Assert.All(Items(frozen, "field"), v => Assert.Equal("0000000000000000", v.Value));
            Assert.All(Items(entry, "raw"), r => Assert.Equal(new[] { "0.0", "0.0" }, r.Elements("item").Select(v => v.Value)));
        }

        [Fact]
        public void M5ZSupersedesExactlyTheTwoFrozenCanOpenRefusals()
        {
            List<XElement> superseded = Supersessions().ToList();
            Assert.Equal(new[] { "m5s-mime410/date", "m5s-mime-ld420/clock" }, superseded.Select(s => Text(s, "id") + "/" + Text(s, "key")));
            foreach (XElement entry in superseded)
            {
                XElement frozen = Items(LoadCase("m5s-expectations.json", Text(entry, "id")), "groups").SelectMany(g => Items(g, "channels"))
                    .Single(c => Text(c, "name") == Text(entry, "key"));
                Assert.False(Bool(frozen, "readable"));
                Assert.Equal("Unsupported", Text(frozen, "kind"));
                Assert.Equal(Text(entry, "m5sReason"), Text(frozen, "reason"));
                Assert.Equal(Text(entry, "dataType"), Text(frozen, "dataType"));
                Assert.Equal(Text(entry, "index"), Text(frozen, "index"));
                Assert.Equal(Items(frozen, "field").Select(v => v.Value), Items(entry, "bytes").Select(v => v.Value));
                Assert.Equal(Items(frozen, "field").Select(v => v.Value), Items(frozen, "bytes").Select(v => v.Value));
            }
            List<XElement> frozenAll = Ids.SelectMany(id => Items(LoadCase("m5s-expectations.json", id), "groups")).SelectMany(g => Items(g, "channels")).ToList();
            Assert.Equal(new[] { "13", "14" }, frozenAll.Where(c => !Bool(c, "readable") && (Text(c, "dataType") == "13" || Text(c, "dataType") == "14"))
                .Select(c => Text(c, "dataType")).OrderBy(t => t));
        }

        private static bool IsMime(XElement channel) { int type = Int(channel, "dataType"); return type == 11 || type == 12; }

        private static void Check(IEnumerable<MdfSampleBatch> batches, List<XElement> channels, int[] selected, int start, int count)
        {
            int ordinal = start;
            foreach (MdfSampleBatch batch in batches)
            {
                Assert.Equal((ulong)ordinal, batch.FirstSampleIndex);
                Assert.Equal(selected, batch.Channels.Select(c => c.Index));
                Assert.InRange(batch.SampleCount, 1, count);
                for (int column = 0; column < selected.Length; column++)
                {
                    XElement wanted = channels.Single(c => Int(c, "index") == selected[column]);
                    MdfValueKind kind = Kind(wanted);
                    List<bool> valid = Items(wanted, "valid").Select(v => v.Value == "true").ToList();
                    for (int row = 0; row < batch.SampleCount; row++)
                    {
                        int sample = ordinal + row;
                        MdfSampleValue actual = batch.GetValue(row, column);
                        Assert.Equal(kind, actual.Kind);
                        Assert.Equal(valid[sample], actual.IsValid);
                        if (kind == MdfValueKind.ByteArray)
                        {
                            Assert.Throws<InvalidOperationException>(() => actual.TextValue);
                            Assert.Equal(Row(wanted, "bytes", sample), BitConverter.ToString(actual.GetByteArrayValue()).Replace("-", "").ToLowerInvariant());
                        }
                        else if (kind == MdfValueKind.Complex)
                        {
                            List<string> parts = Items(wanted, "parts").ElementAt(sample).Elements("item").Select(v => v.Value).ToList();
                            Assert.Equal(BitConverter.DoubleToInt64Bits(double.Parse(parts[0], CultureInfo.InvariantCulture)), BitConverter.DoubleToInt64Bits(actual.RealPart));
                            Assert.Equal(BitConverter.DoubleToInt64Bits(double.Parse(parts[1], CultureInfo.InvariantCulture)), BitConverter.DoubleToInt64Bits(actual.ImaginaryPart));
                        }
                        else if (kind == MdfValueKind.UnsignedInteger) { Assert.Equal(ulong.Parse(Row(wanted, "raw", sample), CultureInfo.InvariantCulture), actual.UnsignedValue); }
                        else if (kind == MdfValueKind.SignedInteger) { Assert.Equal(long.Parse(Row(wanted, "raw", sample), CultureInfo.InvariantCulture), actual.SignedValue); }
                        else
                        {
                            Assert.Equal(BitConverter.DoubleToInt64Bits(double.Parse(Row(wanted, "raw", sample), CultureInfo.InvariantCulture)),
                                BitConverter.DoubleToInt64Bits(actual.FloatingPointValue));
                        }
                    }
                }
                ordinal += batch.SampleCount;
            }
            Assert.Equal(start + count, ordinal);
        }

        private static string Row(XElement parent, string name, int row) { return Items(parent, name).ElementAt(row).Value; }
        private static MdfValueKind Kind(XElement parent) { return (MdfValueKind)Enum.Parse(typeof(MdfValueKind), Text(parent, "kind")); }
        private static IEnumerable<XElement> Items(XElement parent, string name) { return parent.Element(name).Elements("item"); }
        private static string Text(XElement parent, string name) { return parent.Element(name).Value; }
        private static int Int(XElement parent, string name) { return int.Parse(parent.Element(name).Value, CultureInfo.InvariantCulture); }
        private static bool Bool(XElement parent, string name) { return parent.Element(name).Value == "true"; }

        // The frozen case with the supersession applied: the listed CANopen channels become readable byte arrays.
        private static XElement ReadCase(string id)
        {
            XElement expected = LoadCase("m5s-expectations.json", id);
            foreach (XElement entry in Supersessions().Where(s => Text(s, "id") == id))
            {
                XElement channel = Items(expected, "groups").SelectMany(g => Items(g, "channels")).Single(c => Text(c, "name") == Text(entry, "key"));
                channel.Element("readable").Value = Text(entry, "readable");
                channel.Element("kind").Value = Text(entry, "kind");
                Assert.Equal(Items(entry, "bytes").Select(v => v.Value), Items(channel, "bytes").Select(v => v.Value));
            }
            // the frozen 64-bit complex refusal reads two binary32 parts.
            foreach (XElement entry in ComplexSupersessions().Where(s => Text(s, "id") == id))
            {
                XElement channel = Items(expected, "groups").SelectMany(g => Items(g, "channels")).Single(c => Text(c, "name") == Text(entry, "key"));
                Assert.Equal(Text(entry, "m5sReason"), Text(channel, "reason"));
                channel.Element("readable").Value = Text(entry, "readable");
                channel.Element("kind").Value = Text(entry, "kind");
                channel.Add(new XElement(entry.Element("raw")) { Name = "parts" });
            }
            return expected;
        }

        private static IEnumerable<XElement> Supersessions() { return Items(Load("m5z-expectations.json").Element("supersedes"), "channels"); }
        private static IEnumerable<XElement> ComplexSupersessions() { return Items(Load("m5aa-expectations.json").Element("supersedes"), "channels"); }

        private static XElement LoadCase(string file, string id) { return Load(file).Element("cases").Elements("item").Single(c => Text(c, "id") == id); }

        private static XElement Load(string file)
        {
            using (FileStream stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, file)))
            using (XmlDictionaryReader json = JsonReaderWriterFactory.CreateJsonReader(stream, XmlDictionaryReaderQuotas.Max))
            {
                return XElement.Load(json);
            }
        }
    }
}
