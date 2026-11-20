// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MdfLib.Tests.TestDoubles;
using MdfLib.TestSupport;
using Xunit;

namespace MdfLib.Tests.Reading
{
    // literal mutations of frozen files, never the library's writer.
    public sealed class ListDataRemainderTests
    {
        public static IEnumerable<object[]> SummaryCases()
        {
            foreach (string id in new[] { "m5c-ld-equal420", "m5c-ld-offset420", "m5c-ld-chain420", "m5c-ld-null420", "m5c-ld-noinval420" })
            foreach (uint summary in new[] { 2U, 4U, 8U, 6U, 10U, 12U, 14U })
            { yield return new object[] { id, summary }; }
        }

        [Theory]
        [MemberData(nameof(SummaryCases))]
        public void SummaryArraysAreInertAndNeverRead(string id, uint summary)
        {
            byte[] original = FixtureCorpus.Read(id);
            byte[] bytes = (byte[])original.Clone();
            int region;
            int list = WithSummaries(ref bytes, M5CWire.Root(bytes), summary, 0xFF, 0, out region);
            M5CWire.SetRoot(bytes, list);
            int length = (int)M3Wire.U64(bytes, list + 8) - (region - list);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(All(original), All(reader, group));
                Assert.DoesNotContain(source.ReadRanges, r => r.Item1 < region + length && r.Item1 + r.Item2 > region);
                Assert.Equal(2, group.Layout.ListDataMap.Values.MaximumDepth);
            }
        }

        [Theory]
        [InlineData(2U, 0)]    // summary bit without its array
        [InlineData(4U, 0)]
        [InlineData(8U, 0)]
        [InlineData(0x80000002U, 0)]
        [InlineData(0U, 8)]    // trailing bytes without a summary bit
        [InlineData(6U, -8)]   // one array short (filled as 8 bytes per child minus one value)
        public void SummaryPayloadLengthMustBeExactBeforeAnyMapOrBody(uint summary, int extra)
        {
            byte[] bytes = M5CWire.Read();
            int region;
            int root = M5CWire.Root(bytes);
            int list;
            if (extra < 0)
            {
                list = WithSummaries(ref bytes, root, summary, 0, extra, out region);
            }
            else
            {
                list = WithSummaries(ref bytes, root, 0, 0, extra, out region);
                M3Wire.Put(bytes, M3Wire.Payload(bytes, list), (ulong)(BitConverter.ToUInt32(bytes, M3Wire.Payload(bytes, list)) | summary), 4);
            }
            M5CWire.SetRoot(bytes, list);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                MdfFormatException error = Assert.Throws<MdfFormatException>(() => reader.ReadSamples(group, new[] { 1 }).ToList());
                Assert.Equal("MDF006", error.DiagnosticCode);
                Assert.Equal((ulong)list, error.Offset);
                Assert.Null(group.Layout.ListDataMap);
            }
        }

        [Theory]
        [InlineData(0x10U)]
        [InlineData(0x40000000U)]
        [InlineData(0x1EU)]
        public void ZipInfoAndUnknownBitsStayUnsupportedEvenWithSummaries(uint flags)
        {
            byte[] bytes = M5CWire.Read();
            int region;
            int list = WithSummaries(ref bytes, M5CWire.Root(bytes), flags & 0xE, 0, 0, out region);
            M3Wire.Put(bytes, M3Wire.Payload(bytes, list), (ulong)(BitConverter.ToUInt32(bytes, M3Wire.Payload(bytes, list)) | flags), 4);
            M5CWire.SetRoot(bytes, list);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 1 }).ToList());
                Assert.Null(group.Layout.ListDataMap);
            }
        }

        [Theory]
        [InlineData("m5m-hl420")]
        [InlineData("m5m-hl-paired420")]
        public void HeaderListOverListDataAddsOneLevelAndReadsHeaderOnlyUntilSamples(string id)
        {
            byte[] bytes = FixtureCorpus.Read(id);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                int before = source.ReadCalls;
                Assert.Empty(reader.ReadSamples(group, new[] { 1 }, sampleCount: 0));
                Assert.Equal(before, source.ReadCalls);
                Assert.Equal(7, reader.ReadSamples(group, new[] { 1, 2 }).Sum(b => b.SampleCount));
                Assert.Equal(3, group.Layout.ListDataMap.Values.MaximumDepth);
                Assert.Equal(3, group.Layout.ListDataMap.Invalidations.MaximumDepth);
            }
        }

        [Theory]
        [InlineData(0, 2, "unsupported")]    // HL flag bit 1
        [InlineData(2, 2, "unsupported")]    // zip type 2
        [InlineData(0, 0, "MDF013")]         // equal flag 0 against an equal LD
        [InlineData(2, 0, "MDF013")]         // zlib against transposed DZ leaves
        public void HeaderListFlagsAndModeMustAgreeWithTheListData(int field, int value, string outcome)
        {
            byte[] bytes = FixtureCorpus.Read("m5m-hl420");
            int hl = M5CWire.Root(bytes);
            bytes[M3Wire.Payload(bytes, hl) + field] = (byte)value;
            Refused(bytes, outcome);
        }

        [Fact]
        public void RawLeavesUnderAHeaderListAreRejected()
        {
            byte[] bytes = FixtureCorpus.Read("m5m-summaries420");
            int list = M5CWire.Root(bytes);
            int hl = M5Wire.Add(ref bytes, "HL", new[] { (ulong)list }, new byte[8]);
            M5CWire.SetRoot(bytes, hl);
            Refused(bytes, "MDF013");
        }

        [Fact]
        public void HeaderListShapeAndNullFirstLinkAreFormatErrors()
        {
            byte[] bytes = FixtureCorpus.Read("m5m-hl420");
            int hl = M5CWire.Root(bytes);
            ulong list = M3Wire.U64(bytes, M3Wire.Link(hl, 0));
            byte[] payload = bytes.Skip(M3Wire.Payload(bytes, hl)).Take(8).ToArray();
            byte[] wide = (byte[])bytes.Clone();
            int wideRoot = M5Wire.Add(ref wide, "HL", new[] { list, 0UL }, payload); // Add replaces the array: link afterwards.
            M5CWire.SetRoot(wide, wideRoot);
            Refused(wide, "MDF006");
            byte[] longer = (byte[])bytes.Clone();
            int longerRoot = M5Wire.Add(ref longer, "HL", new[] { list }, payload.Concat(new byte[8]).ToArray());
            M5CWire.SetRoot(longer, longerRoot);
            Refused(longer, "MDF006");
            M3Wire.Put(bytes, M3Wire.Link(hl, 0), 0);
            Refused(bytes, "MDF013");
        }

        [Fact]
        public void HeaderListOverListDataInAnUnsortedGroupIsRefusedLikeListData()
        {
            foreach (string id in new[] { "m5m-hl420", "m5m-summaries420" })
            {
                byte[] bytes = FixtureCorpus.Read(id);
                bytes[M3Wire.Payload(bytes, M5CWire.Dg(bytes))] = 1; // record IDs of width 1 (record ID 0 for the only CG)
                using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
                {
                    MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                    if (!group.CanReadSamples) { continue; } // A metadata-level refusal is equally conservative.
                    Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 1 }).ToList());
                    Assert.Null(group.Layout.ListDataMap);
                }
            }
        }

        [Fact]
        public void HeaderListOverListDataIsNotListStorageBeforeMdf420()
        {
            byte[] bytes = FixtureCorpus.Read("m5m-hl420");
            bytes[8] = (byte)'4'; bytes[10] = (byte)'1'; bytes[11] = (byte)'0';
            M3Wire.Put(bytes, 28, 410, 2);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                Assert.Equal("4.10", reader.Header.Version);
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Exception error = Record.Exception(() => reader.ReadSamples(group, new[] { 1 }).ToList());
                Assert.True(error is MdfFormatException || error is NotSupportedException, error == null ? "no error" : error.ToString());
                Assert.Null(group.Layout.ListDataMap);
            }
        }

        [Fact]
        public void GarbageSummaryBytesInTheFrozenCorpusDoNotChangeAnyValue()
        {
            byte[] original = FixtureCorpus.Read("m5m-summaries420");
            byte[] bytes = (byte[])original.Clone();
            int list = M5CWire.Root(bytes);
            int fields = M3Wire.Payload(bytes, list);
            int end = list + (int)M3Wire.U64(bytes, list + 8);
            for (int at = fields + 8 + 2 * 8; at < end; at++) { bytes[at] = 0xFF; } // after flags/count and two offsets: summaries only
            Assert.Equal(All(original), All(bytes));
        }

        private static void Refused(byte[] bytes, string outcome)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                if (outcome == "unsupported")
                {
                    Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 1 }).ToList());
                }
                else
                {
                    Assert.Equal(outcome, Assert.Throws<MdfFormatException>(() => reader.ReadSamples(group, new[] { 1 }).ToList()).DiagnosticCode);
                }
                Assert.Null(group.Layout.ListDataMap);
            }
        }

        // Rebuilds an LD with the same links and fields plus `summary` bits and their arrays (+extra bytes); returns the new LD.
        private static int WithSummaries(ref byte[] bytes, int list, uint summary, byte fill, int extra, out int fields)
        {
            byte[] source = bytes; // A ref parameter cannot be captured by the lambda below.
            int links = (int)M3Wire.U64(source, list + 16);
            ulong[] targets = Enumerable.Range(0, links).Select(i => M3Wire.U64(source, M3Wire.Link(list, i))).ToArray();
            int start = M3Wire.Payload(bytes, list);
            int length = (int)M3Wire.U64(bytes, list + 8) - (start - list);
            byte[] old = bytes.Skip(start).Take(length).ToArray();
            int count = (int)BitConverter.ToUInt32(old, 4);
            int arrays = ((summary >> 1) & 1) == 1 ? 1 : 0;
            arrays += ((summary >> 2) & 1) == 1 ? 1 : 0;
            arrays += ((summary >> 3) & 1) == 1 ? 1 : 0;
            byte[] payload = old.Concat(Enumerable.Repeat(fill, count * 8 * arrays + extra)).ToArray();
            M3Wire.Put(payload, 0, BitConverter.ToUInt32(old, 0) | summary, 4);
            int added = M5Wire.Add(ref bytes, "LD", targets, payload);
            fields = M3Wire.Payload(bytes, added) + length;
            return added;
        }

        private static List<string> All(byte[] bytes)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                return All(reader, reader.ReadMetadata()[0].ChannelGroups[0]);
            }
        }

        private static List<string> All(MdfReader reader, MdfChannelGroup group)
        {
            List<string> rows = new List<string>();
            foreach (MdfValueRepresentation representation in new[] { MdfValueRepresentation.Raw, MdfValueRepresentation.Physical })
            {
            int[] channels = Enumerable.Range(0, group.Channels.Count).Where(i => group.Channels[i].Array == null
                && (representation == MdfValueRepresentation.Raw ? group.Channels[i].CanReadRaw : group.Channels[i].CanReadPhysical)).ToArray();
            foreach (MdfSampleBatch batch in reader.ReadSamples(group, channels, batchSize: 2, representation: representation))
            for (int row = 0; row < batch.SampleCount; row++)
            for (int column = 0; column < channels.Length; column++)
            {
                MdfSampleValue value = batch.GetValue(row, column);
                string text = value.Kind == MdfValueKind.UnsignedInteger ? value.UnsignedValue.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : value.Kind == MdfValueKind.SignedInteger ? value.SignedValue.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : value.Kind == MdfValueKind.FloatingPoint ? BitConverter.DoubleToInt64Bits(value.FloatingPointValue).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : value.Kind == MdfValueKind.Text ? value.TextValue
                    : value.Kind == MdfValueKind.ByteArray ? BitConverter.ToString(value.GetByteArrayValue()) : "unsupported";
                rows.Add(representation + ":" + batch.FirstSampleIndex + ":" + row + ":" + column + ":" + value.Kind + ":" + text + ":" + value.IsValid);
            }
            }
            return rows;
        }
    }
}
