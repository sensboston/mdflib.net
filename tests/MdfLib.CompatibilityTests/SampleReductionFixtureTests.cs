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
    // expectations frozen before the C# change (tools/fixtures/m6f-expectations.json, build_m6f.py). No external reader
    // decodes SR data (asammdf 8.7.2 only stores cg_sr_first), so the independent oracle is the stdlib generator itself.
    public sealed class SampleReductionFixtureTests
    {
        private static readonly string[] Ids = { "m6f-reduction410", "m6f-reduction420", "m6f-reduction330" };
        private static readonly string[] Parts = { "mean", "minimum", "maximum" };

        public static IEnumerable<object[]> AllFiles() { return Ids.Select(id => new object[] { id }); }

        [Theory]
        [MemberData(nameof(AllFiles))]
        public void ReductionDescriptorsMatchTheFrozenCorpus(string id)
        {
            XElement expected = ReadCase(id);
            using (MdfReader reader = Open(id))
            {
                Assert.Equal(Text(expected, "version"), reader.Header.Version);
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(Items(expected, "channels").Select(x => x.Value), group.Channels.Select(c => c.Name));
                IReadOnlyList<MdfSampleReduction> reductions = reader.ReadSampleReductions(group);
                Assert.Same(reductions, reader.ReadSampleReductions(group));
                List<XElement> want = Items(expected, "reductions").ToList();
                Assert.Equal(want.Count, reductions.Count);
                for (int i = 0; i < want.Count; i++)
                {
                    MdfSampleReduction actual = reductions[i];
                    Assert.Same(group, actual.Group);
                    Assert.Equal(i, actual.Index);
                    Assert.True(actual.CanReadValues);
                    Assert.Null(actual.UnsupportedReason);
                    Assert.Equal(ulong.Parse(Text(want[i], "cycleCount"), CultureInfo.InvariantCulture), actual.CycleCount);
                    Assert.Equal(double.Parse(Text(want[i], "interval"), CultureInfo.InvariantCulture), actual.Interval);
                    Assert.Equal(Text(want[i], "syncType"), actual.SyncType.ToString());
                    Assert.Equal(int.Parse(Text(want[i], "flags"), CultureInfo.InvariantCulture), actual.Flags);
                    Assert.Equal(bool.Parse(Text(want[i], "hasInvalidationBytes")), actual.HasInvalidationBytes);
                    Assert.Equal(bool.Parse(Text(want[i], "invalidationDominant")), actual.IsInvalidationDominant);
                }
            }
        }

        [Theory]
        [MemberData(nameof(AllFiles))]
        public void RawReductionValuesMatchInOneAndInSingleCycleBatches(string id)
        {
            XElement expected = ReadCase(id);
            using (MdfReader reader = Open(id))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                IReadOnlyList<MdfSampleReduction> reductions = reader.ReadSampleReductions(group);
                List<XElement> want = Items(expected, "reductions").ToList();
                int[] all = Enumerable.Range(0, group.Channels.Count).ToArray();
                for (int r = 0; r < want.Count; r++)
                {
                    List<XElement> cycles = Items(want[r], "cycles").ToList();
                    foreach (int batchSize in new[] { 1024, 1 })
                    {
                        List<MdfSampleReductionBatch> batches = reader.ReadSampleReduction(reductions[r], all, batchSize: batchSize).ToList();
                        Assert.Equal(batchSize == 1 ? cycles.Count : 1, batches.Count);
                        ulong next = 0;
                        foreach (MdfSampleReductionBatch batch in batches)
                        {
                            Assert.Equal(next, batch.FirstCycleIndex);
                            for (int row = 0; row < batch.CycleCount; row++)
                            {
                                XElement cycle = cycles[(int)batch.FirstCycleIndex + row];
                                MdfSampleBatch[] parts = { batch.Mean, batch.Minimum, batch.Maximum };
                                for (int p = 0; p < 3; p++)
                                {
                                    Assert.Equal(batch.FirstCycleIndex, parts[p].FirstSampleIndex);
                                    List<XElement> values = Items(cycle, Parts[p]).ToList();
                                    for (int column = 0; column < all.Length; column++)
                                    {
                                        MdfSampleValue value = parts[p].GetValue(row, column);
                                        string where = id + " sr" + r + " cycle " + (batch.FirstCycleIndex + (ulong)row) + " " + Parts[p] + " " + group.Channels[column].Name;
                                        Assert.Equal(where + " valid=" + Text(values[column], "valid"), where + " valid=" + (value.IsValid ? "true" : "false"));
                                        Assert.Equal(where + " raw=" + Text(values[column], "raw"), where + " raw=" + Raw(value));
                                    }
                                }
                            }
                            next += (ulong)batch.CycleCount;
                        }
                        Assert.Equal((ulong)cycles.Count, next);
                    }
                }
            }
        }

        [Fact]
        public void PhysicalValuesApplyTheChannelConversionToEveryPart()
        {
            foreach (string id in Ids.Take(2))
            {
                XElement expected = ReadCase(id);
                using (MdfReader reader = Open(id))
                {
                    MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                    MdfSampleReduction reduction = reader.ReadSampleReductions(group)[0];
                    List<XElement> cycles = Items(Items(expected, "reductions").First(), "cycles").ToList();
                    MdfSampleReductionBatch batch = reader.ReadSampleReduction(reduction, new[] { 2, 1 }, 1, 2,
                        representation: MdfValueRepresentation.Physical).Single();
                    Assert.Equal(1UL, batch.FirstCycleIndex);
                    Assert.Equal(2, batch.CycleCount);
                    Assert.Equal("temp", batch.Mean.Channels[0].Name);
                    MdfSampleBatch[] parts = { batch.Mean, batch.Minimum, batch.Maximum };
                    for (int row = 0; row < 2; row++)
                    {
                        for (int p = 0; p < 3; p++)
                        {
                            XElement temp = Items(cycles[row + 1], Parts[p]).ElementAt(2);
                            MdfSampleValue value = parts[p].GetValue(row, 0);
                            Assert.Equal(MdfValueKind.FloatingPoint, value.Kind);
                            Assert.Equal(double.Parse(Text(temp, "physical"), CultureInfo.InvariantCulture), value.FloatingPointValue);
                            Assert.Equal(MdfValueKind.UnsignedInteger, parts[p].GetValue(row, 1).Kind); // Identity passes raw through.
                        }
                    }
                }
            }
        }

        private static string Raw(MdfSampleValue value)
        {
            switch (value.Kind)
            {
                case MdfValueKind.UnsignedInteger: return value.UnsignedValue.ToString(CultureInfo.InvariantCulture);
                case MdfValueKind.SignedInteger: return value.SignedValue.ToString(CultureInfo.InvariantCulture);
                default:
                    double number = value.FloatingPointValue;
                    return number == Math.Floor(number) && Math.Abs(number) < 1e15
                        ? number.ToString("0.0", CultureInfo.InvariantCulture) : number.ToString("R", CultureInfo.InvariantCulture);
            }
        }

        private static MdfReader Open(string id) { return MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)); }
        private static IEnumerable<XElement> Items(XElement parent, string name) { return parent.Element(name).Elements("item"); }
        private static string Text(XElement parent, string name) { return parent.Element(name).Value; }

        private static XElement ReadCase(string id)
        {
            using (FileStream stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "m6f-expectations.json")))
            using (XmlDictionaryReader json = JsonReaderWriterFactory.CreateJsonReader(stream, XmlDictionaryReaderQuotas.Max))
            {
                return XElement.Load(json).Element("cases").Elements("item").Single(c => Text(c, "id") == id);
            }
        }
    }
}
