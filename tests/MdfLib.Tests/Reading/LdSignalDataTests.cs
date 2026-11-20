// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MdfLib.Formats.Mdf4;
using MdfLib.Reading;
using MdfLib.Tests.TestDoubles;
using MdfLib.TestSupport;
using Xunit;

namespace MdfLib.Tests.Reading
{
    // literal mutations of the frozen stdlib m5r files, never the library's writer.
    public sealed class LdSignalDataTests
    {
        private const string Sorted = "m5r-ld-sd420"; // 0 t, 1 name (UTF-8 VLSD -> LD offsets [SD, SD]),
                                                     // 2 blob (bytes VLSD -> LD0 equal+time [SD, DZ] -> LD1 [SD]), 3 plain
        private const string Header = "m5r-hl-ld-sd420"; // records LD/DV; 1 u16 (HL -> LD [DZ, DZ]), 2 lat (plain SD)
        private const int Name = 1, Blob = 2, Plain = 3;
        private static readonly string[] Names = { "alpha", "\u03b2-ray", "", "Gr\u00f6\u00dfe", "", "\u65e5\u672c" };
        private static readonly string[] Blobs = { "0001", "", "ffffffffffffff", "616263", "1020304050", "5a" };

        private static int DataLink(byte[] bytes, int index) { return (int)M3Wire.U64(bytes, M3Wire.Link(M5DWire.Cn(bytes, index), 5)); }
        private static int Child(byte[] bytes, int list, int link) { return (int)M3Wire.U64(bytes, M3Wire.Link(list, link)); }
        private static int Fields(byte[] bytes, int block) { return M3Wire.Payload(bytes, block); }
        private static MdfChannelGroup Group(MdfReader reader) { return reader.ReadMetadata()[0].ChannelGroups[0]; }

        private static List<MdfSampleValue> Column(MdfReader reader, int index, ulong start = 0, ulong count = ulong.MaxValue, int batch = 1024)
        {
            return reader.ReadSamples(Group(reader), new[] { index }, start, count, batch)
                .SelectMany(b => Enumerable.Range(0, b.SampleCount).Select(r => b.GetValue(r, 0))).ToList();
        }

        private static string Hex(MdfSampleValue value) { return BitConverter.ToString(value.GetByteArrayValue()).Replace("-", "").ToLowerInvariant(); }

        private static void Version(byte[] bytes, string version)
        {
            for (int i = 0; i < 4; i++) { bytes[8 + i] = (byte)version[i]; }
            M3Wire.Put(bytes, 28, ulong.Parse(version.Replace(".", "")), 2);
        }

        [Fact]
        public void LdListedSignalDataReadsConcatenatedLeavesWithValidity()
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(Sorted), false)))
            {
                MdfChannelGroup group = Group(reader);
                Assert.True(group.Channels[Name].CanReadRaw, group.Channels[Name].UnsupportedReason);
                Assert.True(group.Channels[Blob].CanReadRaw, group.Channels[Blob].UnsupportedReason);
                List<MdfSampleValue> names = Column(reader, Name);
                Assert.Equal(Names, names.Select(v => v.TextValue));
                Assert.Equal(new[] { true, true, false, true, true, true }, names.Select(v => v.IsValid));
                Assert.Equal(Blobs, Column(reader, Blob).Select(Hex));
                foreach (int batch in new[] { 1, 2, 5 })
                {
                    Assert.Equal(Names.Skip(1).Take(4), Column(reader, Name, 1, 4, batch).Select(v => v.TextValue));
                    Assert.Equal(Blobs.Skip(2).Take(3), Column(reader, Blob, 2, 3, batch).Select(Hex));
                }
            }
        }

        [Fact]
        public void HeaderListOverLdReadsDzLeaves()
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(Header), false)))
            {
                Assert.Equal(new[] { "A\u00e9", "\U0001f600", "xyz", "", "end" }, Column(reader, 1).Select(v => v.TextValue));
                Assert.Equal(new[] { "caf\u00e9", "na\u00efve", "a", "bb", "ccc" }, Column(reader, 2).Select(v => v.TextValue));
            }
        }

        [Theory]
        [InlineData("summary-values")]   // inert time summary array contents
        [InlineData("equal-count")]      // the equal sample count is not interpreted
        [InlineData("equal-offsets")]    // non-decreasing (equal) sample offsets are allowed
        public void OpaqueListCountsAndSummariesDoNotChangeValues(string mutation)
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            int blob = DataLink(bytes, Blob);
            if (mutation == "summary-values") { M3Wire.Put(bytes, Fields(bytes, blob) + 16, 0x7FFFFFFFFFFFFFFFUL); }
            else if (mutation == "equal-count") { M3Wire.Put(bytes, Fields(bytes, blob) + 8, 999); }
            else { M3Wire.Put(bytes, Fields(bytes, DataLink(bytes, Name)) + 16, 0); }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                Assert.Equal(Names, Column(reader, Name).Select(v => v.TextValue));
                Assert.Equal(Blobs, Column(reader, Blob).Select(Hex));
            }
        }

        [Theory]
        [InlineData("invalidation-flag", Name, null)]
        [InlineData("unknown-flag", Name, null)]
        [InlineData("count", Name, "MDF013")]
        [InlineData("first-offset", Name, "MDF013")]
        [InlineData("null-link", Name, "MDF013")]
        [InlineData("text-link", Name, null)]
        [InlineData("empty-leaf", Name, "MDF013")]
        [InlineData("summary-without-array", Blob, "MDF006")]
        [InlineData("equal-zero", Blob, "MDF013")]
        [InlineData("dz-original-type", Blob, null)]
        [InlineData("dz-transposed", Blob, null)]
        [InlineData("cycle", Blob, "MDF010")]
        [InlineData("next-not-ld", Blob, "MDF005")]
        public void MalformedOrUnsupportedListsFailOnlyWhenTheirChannelIsRead(string mutation, int index, string code)
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            int name = DataLink(bytes, Name);
            int blob = DataLink(bytes, Blob);
            int dz = Child(bytes, blob, 2);
            switch (mutation)
            {
                case "invalidation-flag": M3Wire.Put(bytes, Fields(bytes, name), 0x80000000UL, 4); break;
                case "unknown-flag": M3Wire.Put(bytes, Fields(bytes, name), 0x10, 4); break;
                case "count": M3Wire.Put(bytes, Fields(bytes, name) + 4, 3, 4); break;
                case "first-offset": M3Wire.Put(bytes, Fields(bytes, name) + 8, 1); break;
                case "null-link": M3Wire.Put(bytes, M3Wire.Link(name, 2), 0); break;
                case "text-link": M3Wire.Put(bytes, M3Wire.Link(name, 2), M3Wire.U64(bytes, M3Wire.Link(M5DWire.Cn(bytes, Plain), 2))); break;
                case "empty-leaf": M3Wire.Put(bytes, Child(bytes, name, 1) + 8, 24); break;
                case "summary-without-array": M3Wire.Put(bytes, Fields(bytes, blob), 0x07, 4); break;
                case "equal-zero": M3Wire.Put(bytes, Fields(bytes, blob) + 8, 0); break;
                case "dz-original-type": bytes[Fields(bytes, dz)] = (byte)'D'; bytes[Fields(bytes, dz) + 1] = (byte)'T'; break;
                case "dz-transposed": bytes[Fields(bytes, dz) + 2] = 1; M3Wire.Put(bytes, Fields(bytes, dz) + 4, 1, 4); break;
                case "cycle": M3Wire.Put(bytes, M3Wire.Link(Child(bytes, blob, 0), 0), (ulong)blob); break;
                default: M3Wire.Put(bytes, M3Wire.Link(blob, 0), (ulong)Child(bytes, blob, 1)); break; // next -> an SD leaf
            }
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = Group(reader);
                Assert.True(group.Channels[index].CanReadRaw, group.Channels[index].UnsupportedReason);
                Exception error = Record.Exception(() => Column(reader, index));
                if (code == null) { Assert.IsType<NotSupportedException>(error); }
                else { Assert.Equal(code, Assert.IsType<MdfFormatException>(error).DiagnosticCode); }
                if (mutation.EndsWith("flag", StringComparison.Ordinal)) { Assert.Equal(Mdf4ListDataMap.SignalFlagsReason, error.Message); }
                Assert.Equal(new ulong[] { 1, 2, 3, 4, 5, 6 }, Column(reader, Plain).Select(v => v.UnsignedValue));
                Assert.Equal(6, Column(reader, Name + Blob - index).Count); // the other list is independent
            }
        }

        [Theory]
        [InlineData("zip", null)]
        [InlineData("flags", "MDF013")]
        [InlineData("plain-leaf", "MDF013")]
        public void HeaderListRulesApplyToLdSignalData(string mutation, string code)
        {
            byte[] bytes = FixtureCorpus.Read(Header);
            int hl = DataLink(bytes, 1);
            int ld = Child(bytes, hl, 0);
            if (mutation == "zip") { bytes[Fields(bytes, hl) + 2] = 1; }
            else if (mutation == "flags") { bytes[Fields(bytes, hl)] = 1; }
            else { M3Wire.Put(bytes, M3Wire.Link(ld, 2), (ulong)DataLink(bytes, 2)); } // the control channel's plain SD
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                Exception error = Record.Exception(() => Column(reader, 1));
                if (code == null) { Assert.IsType<NotSupportedException>(error); }
                else { Assert.Equal(code, Assert.IsType<MdfFormatException>(error).DiagnosticCode); }
                Assert.Equal(5, Column(reader, 2).Count);
            }
        }

        [Fact]
        public void LdRootsBeforeMdf420StayUnsupported()
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            Version(bytes, "4.11");
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = Group(reader);
                Assert.Equal(MetadataReader.VlsdRootReason, group.Channels[Name].UnsupportedReason);
                Assert.Equal(MetadataReader.VlsdRootReason, group.Channels[Blob].UnsupportedReason);
                Assert.Equal(6, Column(reader, Plain).Count);
            }
        }

        [Fact]
        public void HeaderListOverLdBeforeMdf420KeepsTheDlRule()
        {
            byte[] bytes = FixtureCorpus.Read("m5r-ld-sd411"); // 1 old (UTF-8 VLSD -> LD, refused), 2 ok (SD)
            int ld = DataLink(bytes, 1);
            byte[] payload = new byte[8];
            int hl = M5Wire.Add(ref bytes, "HL", new[] { (ulong)ld }, payload);
            M3Wire.Put(bytes, M3Wire.Link(M5DWire.Cn(bytes, 1), 5), (ulong)hl);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = Group(reader);
                Assert.True(group.Channels[1].CanReadRaw); // an HL root is accepted by metadata
                Assert.Equal("MDF005", Assert.Throws<MdfFormatException>(() => Column(reader, 1)).DiagnosticCode);
                Assert.Equal(new[] { "one", "two", "three", "four" }, Column(reader, 2).Select(v => v.TextValue));
            }
        }

        [Fact]
        public void MetadataReadsNoListContentAndValuesNeverReadSummaryArrays()
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            int name = DataLink(bytes, Name);
            int blob = DataLink(bytes, Blob);
            int summaries = Fields(bytes, blob) + 16; // two time values after flags/count/equal
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                Group(reader);
                Assert.DoesNotContain(source.ReadRanges, r => r.Item1 >= Fields(bytes, name) && r.Item1 < name + 72);
                Assert.DoesNotContain(source.ReadRanges, r => r.Item1 == Child(bytes, name, 1) || r.Item1 == Child(bytes, blob, 1));
                Assert.Contains(source.ReadRanges, r => r.Item1 == name); // the root header itself is resolved
                Assert.Equal(Blobs, Column(reader, Blob).Select(Hex));
                Assert.DoesNotContain(source.ReadRanges, r => r.Item1 < summaries + 16 && r.Item1 + r.Item2 > summaries);
                Assert.Contains(source.ReadRanges, r => r.Item1 == Fields(bytes, blob));
            }
        }
    }
}
