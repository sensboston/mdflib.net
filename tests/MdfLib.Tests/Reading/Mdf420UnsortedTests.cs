// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MdfLib.TestSupport;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Reading
{
    // exact MDF 4.20 data groups with record IDs reuse the accepted interleaved engine.
    public sealed class Mdf420UnsortedTests
    {
        [Theory]
        [InlineData("m5h-id1-dt420", "m5b-id1-dt410", 1)]
        [InlineData("m5h-id2-dz420", "m5b-id2-dz411", 2)]
        [InlineData("m5h-id4-dl420", "m5b-id4-dl410", 4)]
        [InlineData("m5h-id8-hl420", "m5b-id8-hl411", 8)]
        [InlineData("m5h-wide-id8-hl420", "m5b-wide-id8-hl411", 8)]
        [InlineData("m5h-text420", "m5f-interleaved410", 2)]
        public void Unsorted420ReadsExactlyLikeTheFrozenSourceRevision(string id, string source, int width)
        {
            List<string> unsorted = Snapshot(FixtureCorpus.Read(id), "4.20", width);
            List<string> frozen = Snapshot(FixtureCorpus.Read(source), null, width);
            Assert.NotEmpty(unsorted);
            Assert.Equal(frozen, unsorted);
        }

        [Fact]
        public void RemoteMasterGroupInsideUnsorted420DataGroupIsRefusedWithoutAffectingItsStoredMaster()
        {
            byte[] bytes = M5CWire.Read("m5c-ld-remote420");
            bytes[M3Wire.Payload(bytes, M5CWire.Dg(bytes, 1))] = 1;
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                IReadOnlyList<MdfDataGroup> catalog = reader.ReadMetadata();
                MdfChannelGroup remote = catalog[1].ChannelGroups[0];
                Assert.False(remote.CanReadSamples);
                Assert.Contains("Remote-master channel groups are not supported in unsorted MDF 4.20", remote.UnsupportedReason);
                Assert.Null(remote.Layout.Interleaved);
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(remote, new[] { 0 }));
                MdfChannelGroup master = catalog[0].ChannelGroups[0];
                Assert.True(master.CanReadSamples, master.UnsupportedReason);
                Assert.Equal((int)master.SampleCount, reader.ReadSamples(master, new[] { 0 }).Sum(batch => batch.SampleCount));
            }
        }

        [Fact]
        public void UnknownRecordIdInUnsorted420StreamFailsBeforeAnyBatch()
        {
            byte[] bytes = FixtureCorpus.Read("m5h-id1-dt420");
            int root = M5CWire.Root(bytes);
            Assert.Equal((byte)'D', bytes[root + 2]);
            bytes[root + 24] = 7; // First frame prefix; declared IDs are 0, 255 and 129.
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.True(group.CanReadSamples);
                int published = 0;
                MdfFormatException error = Assert.Throws<MdfFormatException>(() =>
                {
                    foreach (MdfSampleBatch batch in reader.ReadSamples(group, new[] { 0 }, batchSize: 1)) { published++; }
                });
                Assert.Equal("MDF013", error.DiagnosticCode);
                Assert.Contains("Unknown record ID", error.Message);
                Assert.Equal(0, published);
            }
        }

        [Fact]
        public void DuplicateRecordIdsInUnsorted420DataGroupAreAFormatError()
        {
            byte[] bytes = FixtureCorpus.Read("m5h-id4-dl420");
            int first = M5CWire.Cg(bytes);
            int second = (int)M3Wire.U64(bytes, M3Wire.Link(first, 0));
            M3Wire.Put(bytes, M3Wire.Payload(bytes, second), M3Wire.U64(bytes, M3Wire.Payload(bytes, first)));
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                Assert.Equal("MDF013", Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
            }
        }

        [Fact]
        public void SortedSingleGroup420WithoutRecordIdsKeepsTheM5CPath()
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read("m5c-dt420"), false)))
            {
                MdfChannelGroup group = Assert.Single(Assert.Single(reader.ReadMetadata()).ChannelGroups);
                Assert.True(group.CanReadSamples);
                Assert.Null(group.Layout.Interleaved);
            }
        }

        private static List<string> Snapshot(byte[] bytes, string version, int width)
        {
            List<string> values = new List<string>();
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                if (version != null) { Assert.Equal(version, reader.Header.Version); }
                IReadOnlyList<MdfDataGroup> catalog = reader.ReadMetadata();
                foreach (MdfDataGroup dataGroup in catalog)
                {
                    foreach (MdfChannelGroup group in dataGroup.ChannelGroups)
                    {
                        Assert.True(group.CanReadSamples, group.UnsupportedReason);
                        Assert.NotNull(group.Layout.Interleaved);
                        Assert.Equal(width, group.Layout.Interleaved.PrefixBytes);
                        int[] selected = group.Channels.Where(channel => channel.CanReadRaw).Select(channel => channel.Index).ToArray();
                        values.Add(dataGroup.Index + "/" + group.Index + ":" + group.SampleCount + ":" + string.Join(",", selected));
                        foreach (MdfValueRepresentation representation in new[] { MdfValueRepresentation.Raw, MdfValueRepresentation.Physical })
                        {
                            foreach (MdfSampleBatch batch in reader.ReadSamples(group, selected, batchSize: 2, representation: representation))
                            {
                                for (int row = 0; row < batch.SampleCount; row++)
                                {
                                    for (int column = 0; column < selected.Length; column++)
                                    { values.Add(Format(batch.GetValue(row, column))); }
                                }
                            }
                        }
                    }
                }
            }
            return values;
        }

        private static string Format(MdfSampleValue value)
        {
            string text;
            switch (value.Kind)
            {
                case MdfValueKind.UnsignedInteger: text = value.UnsignedValue.ToString(CultureInfo.InvariantCulture); break;
                case MdfValueKind.SignedInteger: text = value.SignedValue.ToString(CultureInfo.InvariantCulture); break;
                case MdfValueKind.FloatingPoint: text = BitConverter.DoubleToInt64Bits(value.FloatingPointValue).ToString(CultureInfo.InvariantCulture); break;
                case MdfValueKind.Text: text = BitConverter.ToString(value.GetByteArrayValue()) + "|" + value.TextValue; break;
                case MdfValueKind.ByteArray: text = BitConverter.ToString(value.GetByteArrayValue()); break;
                default: throw new InvalidOperationException("Unexpected value kind " + value.Kind + ".");
            }
            return value.Kind + ":" + value.IsValid + ":" + text;
        }
    }
}
