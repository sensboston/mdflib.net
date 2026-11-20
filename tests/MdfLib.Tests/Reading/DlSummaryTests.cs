// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MdfLib.Tests.TestDoubles;
using MdfLib.TestSupport;
using Xunit;

namespace MdfLib.Tests.Reading
{
    // literal mutations of frozen stdlib files, never the library's writer.
    public sealed class DlSummaryTests
    {
        // Exact MDF 4.20 record data DLs: sorted HL->DL, interleaved DL and HL->DL, VLSD-CG stream HL->DL, equal DL.
        private static readonly string[] RecordDataFiles =
            { "m5c-hl420", "m5h-id4-dl420", "m5h-id8-hl420", "m5j-id4-hl420", "m5k-interleaved420", "m5n-dl-equal420" };

        public static IEnumerable<object[]> SummaryCases()
        {
            foreach (string id in RecordDataFiles)
            foreach (int summary in new[] { 2, 4, 8, 6, 10, 12, 14 })
            { yield return new object[] { id, summary }; }
        }

        [Theory]
        [MemberData(nameof(SummaryCases))]
        public void SummaryArraysOfMdf420RecordDataListsAreInertAndNeverRead(string id, int summary)
        {
            byte[] original = FixtureCorpus.Read(id);
            byte[] bytes = (byte[])original.Clone();
            int dl = FirstList(bytes, M5CWire.Root(bytes));
            int start, end;
            int added = WithSummaries(ref bytes, dl, (byte)summary, 0xFF, 0, out start, out end);
            Assert.NotEqual(0, Relink(bytes, dl, added));
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                Assert.Equal(All(original), All(reader));
                Assert.DoesNotContain(source.ReadRanges, r => r.Item1 < end && r.Item1 + r.Item2 > start);
            }
        }

        [Theory]
        [InlineData(4, 0)]    // summary bit without its array (the frozen DL already has time)
        [InlineData(8, 0)]
        [InlineData(0, 8)]    // trailing bytes without a summary bit
        [InlineData(12, -8)]  // one value short
        public void SummaryPayloadLengthMustBeExactBeforeAnyBody(int summary, int extra)
        {
            byte[] bytes = FixtureCorpus.Read("m5n-dl-equal420");
            int dl = M5CWire.Root(bytes);
            int start, end;
            int added = extra < 0 ? WithSummaries(ref bytes, dl, (byte)summary, 0, extra, out start, out end)
                : WithSummaries(ref bytes, dl, 0, 0, extra, out start, out end);
            if (extra >= 0) { bytes[M3Wire.Payload(bytes, added)] |= (byte)summary; }
            M5CWire.SetRoot(bytes, added);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                MdfFormatException error = Assert.Throws<MdfFormatException>(() => reader.ReadSamples(group, new[] { 1 }).ToList());
                Assert.Equal("MDF013", error.DiagnosticCode);
                Assert.Equal((ulong)added, error.Offset);
                Assert.Null(group.Layout.DataMap);
                int firstLeaf = (int)M3Wire.U64(bytes, M3Wire.Link(added, 1));
                Assert.DoesNotContain(source.ReadRanges, r => r.Item1 > firstLeaf + 24 && r.Item1 < firstLeaf + (long)M3Wire.U64(bytes, firstLeaf + 8));
            }
        }

        [Theory]
        [InlineData("m5n-dl-equal420", 0x10)]
        [InlineData("m5n-dl-equal420", 0x80)]
        [InlineData("m5n-dl-equal420", 0x1E)]
        [InlineData("m5-offset411", 0x02)]   // 4.11: bits 1-3 are reserved
        [InlineData("m5-hl411", 0x08)]
        [InlineData("m5-chain410", 0x04)]   // 4.10 likewise
        public void UnknownBitsAndPre420SummaryBitsStayUnsupported(string id, int flags)
        {
            byte[] bytes = FixtureCorpus.Read(id);
            int dl = FirstList(bytes, M5CWire.Root(bytes));
            int start, end;
            int added = WithSummaries(ref bytes, dl, (byte)(flags & 0xE), 0, 0, out start, out end);
            bytes[M3Wire.Payload(bytes, added)] |= (byte)flags;
            Assert.NotEqual(0, Relink(bytes, dl, added));
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.True(group.CanReadSamples);
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 1 }).ToList());
                Assert.Null(group.Layout.DataMap);
            }
        }

        [Fact]
        public void SignalDataListSummaryBitsStayUnsupported()
        {
            byte[] bytes = FixtureCorpus.Read("m5f-paired420");
            int dl = Blocks(bytes).First(b => Type(bytes, b) == "DL");
            int start, end;
            int added = WithSummaries(ref bytes, dl, 2, 0, 0, out start, out end);
            Assert.NotEqual(0, Relink(bytes, dl, added));
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                int refused = 0;
                foreach (MdfChannelGroup group in reader.ReadMetadata().SelectMany(d => d.ChannelGroups).Where(g => g.CanReadSamples))
                {
                    int[] readable = group.Channels.Where(c => c.CanReadRaw && c.Array == null).Select(c => c.Index).ToArray();
                    if (readable.Length == 0) { continue; }
                    Exception error = Record.Exception(() => reader.ReadSamples(group, readable).ToList());
                    Assert.True(error == null || error is NotSupportedException, error == null ? "" : error.ToString());
                    refused += error == null ? 0 : 1;
                }
                Assert.True(refused >= 1); // The group whose VLSD signal data is the mutated SD-role DL.
            }
            Assert.NotEmpty(All(FixtureCorpus.Read("m5f-paired420"))); // The unmutated file reads completely.
        }

        [Theory]
        [InlineData(3)]   // equal + time
        [InlineData(9)]   // equal + distance (the DL's own bits)
        public void HeaderListSummaryBitsStayUnsupported(int flags)
        {
            byte[] bytes = FixtureCorpus.Read("m5n-hl-dl420");
            M3Wire.Put(bytes, M3Wire.Payload(bytes, M5CWire.Root(bytes)), (ulong)flags, 2);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 1 }).ToList());
                Assert.Null(group.Layout.DataMap);
            }
        }

        [Fact]
        public void HeaderListEqualAgreementIgnoresTheDlSummaryBitsButNotTheEqualBit()
        {
            byte[] bytes = FixtureCorpus.Read("m5n-hl-dl420");
            M3Wire.Put(bytes, M3Wire.Payload(bytes, M5CWire.Root(bytes)), 0, 2);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal("MDF013", Assert.Throws<MdfFormatException>(() => reader.ReadSamples(group, new[] { 1 }).ToList()).DiagnosticCode);
            }
            Assert.Equal(7, All(FixtureCorpus.Read("m5n-hl-dl420")).Count / 8); // 4 channels x raw/physical x 7 rows
        }

        [Theory]
        [InlineData("m5n-dl-summaries420")]
        [InlineData("m5n-split420")]
        [InlineData("m5n-hl-dl420")]
        public void GarbageSummaryBytesInTheFrozenCorpusDoNotChangeAnyValue(string id)
        {
            byte[] original = FixtureCorpus.Read(id);
            byte[] bytes = (byte[])original.Clone();
            for (int dl = FirstList(bytes, M5CWire.Root(bytes)); dl != 0; dl = (int)M3Wire.U64(bytes, M3Wire.Link(dl, 0)))
            {
                int payload = M3Wire.Payload(bytes, dl);
                int count = (int)BitConverter.ToUInt32(bytes, payload + 4);
                int prefix = 8 + ((bytes[payload] & 1) != 0 ? 8 : 8 * count);
                for (int at = payload + prefix; at < dl + (int)M3Wire.U64(bytes, dl + 8); at++) { bytes[at] = 0xFF; }
            }
            Assert.Equal(All(original), All(bytes));
        }

        [Fact]
        public void ZeroRangeReadsNoListHeaderAndDepthIsUnchanged()
        {
            byte[] bytes = FixtureCorpus.Read("m5n-hl-dl420");
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                int before = source.ReadCalls;
                Assert.Empty(reader.ReadSamples(group, new[] { 1 }, sampleCount: 0));
                Assert.Equal(before, source.ReadCalls);
                Assert.Equal(7, reader.ReadSamples(group, new[] { 1 }).Sum(b => b.SampleCount));
                Assert.Equal(3, group.Layout.DataMap.MaximumDepth);
            }
        }

        private static int FirstList(byte[] bytes, int root)
        {
            return Type(bytes, root) == "HL" ? (int)M3Wire.U64(bytes, M3Wire.Link(root, 0)) : root;
        }

        private static string Type(byte[] bytes, int block) { return new string(new[] { (char)bytes[block + 2], (char)bytes[block + 3] }); }

        // Literal sequential walk of 8-aligned blocks (stdlib corpus layout).
        private static IEnumerable<int> Blocks(byte[] bytes)
        {
            int address = 64;
            while (address + 24 <= bytes.Length && bytes[address] == (byte)'#' && bytes[address + 1] == (byte)'#')
            {
                yield return address;
                address = (address + (int)M3Wire.U64(bytes, address + 8) + 7) & ~7;
            }
        }

        // Repoints every link to `from` (DG data, HL first link, CN signal data, DL next) at `to`; returns the count.
        private static int Relink(byte[] bytes, int from, int to)
        {
            int count = 0;
            foreach (int block in Blocks(bytes).ToList())
            {
                if (block == to) { continue; }
                int links = (int)M3Wire.U64(bytes, block + 16);
                for (int index = 0; index < links; index++)
                {
                    if (M3Wire.U64(bytes, M3Wire.Link(block, index)) == (ulong)from) { M3Wire.Put(bytes, M3Wire.Link(block, index), (ulong)to); count++; }
                }
            }
            return count;
        }

        // Rebuilds a DL with the same links/fields plus `summary` bits and their arrays (+extra bytes); returns the new DL.
        private static int WithSummaries(ref byte[] bytes, int list, byte summary, byte fill, int extra, out int start, out int end)
        {
            byte[] source = bytes; // A ref parameter cannot be captured by the lambda below.
            int links = (int)M3Wire.U64(source, list + 16);
            ulong[] targets = Enumerable.Range(0, links).Select(i => M3Wire.U64(source, M3Wire.Link(list, i))).ToArray();
            int payloadAt = M3Wire.Payload(source, list);
            int length = (int)M3Wire.U64(source, list + 8) - (payloadAt - list);
            byte[] old = source.Skip(payloadAt).Take(length).ToArray();
            int count = (int)BitConverter.ToUInt32(old, 4);
            int fresh = summary & ~old[0] & 0x0E; // Bits the DL already has keep their existing arrays (wire order preserved).
            int arrays = ((fresh >> 1) & 1) + ((fresh >> 2) & 1) + ((fresh >> 3) & 1);
            byte[] payload = old.Concat(Enumerable.Repeat(fill, count * 8 * arrays + extra)).ToArray();
            payload[0] |= summary;
            int added = M5Wire.Add(ref bytes, "DL", targets, payload);
            start = M3Wire.Payload(bytes, added) + length;
            end = added + (int)M3Wire.U64(bytes, added + 8);
            return added;
        }

        private static List<string> All(byte[] bytes)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                return All(reader);
            }
        }

        private static List<string> All(MdfReader reader)
        {
            List<string> rows = new List<string>();
            foreach (MdfDataGroup dataGroup in reader.ReadMetadata())
            foreach (MdfChannelGroup group in dataGroup.ChannelGroups.Where(g => g.CanReadSamples))
            foreach (MdfValueRepresentation representation in new[] { MdfValueRepresentation.Raw, MdfValueRepresentation.Physical })
            {
                int[] channels = Enumerable.Range(0, group.Channels.Count).Where(i => group.Channels[i].Array == null
                    && (representation == MdfValueRepresentation.Raw ? group.Channels[i].CanReadRaw : group.Channels[i].CanReadPhysical)).ToArray();
                if (channels.Length == 0) { continue; }
                foreach (MdfSampleBatch batch in reader.ReadSamples(group, channels, batchSize: 2, representation: representation))
                for (int row = 0; row < batch.SampleCount; row++)
                for (int column = 0; column < channels.Length; column++)
                {
                    MdfSampleValue value = batch.GetValue(row, column);
                    string text = value.Kind == MdfValueKind.UnsignedInteger ? value.UnsignedValue.ToString(CultureInfo.InvariantCulture)
                        : value.Kind == MdfValueKind.SignedInteger ? value.SignedValue.ToString(CultureInfo.InvariantCulture)
                        : value.Kind == MdfValueKind.FloatingPoint ? BitConverter.DoubleToInt64Bits(value.FloatingPointValue).ToString(CultureInfo.InvariantCulture)
                        : value.Kind == MdfValueKind.Text ? value.TextValue
                        : value.Kind == MdfValueKind.ByteArray ? BitConverter.ToString(value.GetByteArrayValue()) : "unsupported";
                    rows.Add(group.Index + ":" + representation + ":" + batch.FirstSampleIndex + ":" + row + ":" + column + ":" + value.Kind + ":" + text + ":" + value.IsValid);
                }
            }
            return rows;
        }
    }
}
