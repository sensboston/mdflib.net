// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Linq;
using MdfLib.Formats.Mdf4;
using MdfLib.Tests.TestDoubles;
using MdfLib.TestSupport;
using Xunit;

namespace MdfLib.Tests.Reading
{
    // literal mutations of the frozen stdlib m5ab files, never the library's writer.
    public sealed class TextArrayChannelTests
    {
        private const string Arrays = "m5ab-fixed-arrays420"; // t0 dates1[3]@8/8 times2[2,2]@32/6 labels3[2]@56/5 blobs4[3]@66/4 pkt5@78 n6@94 | pdate7 pcount8 plabel9
        private const string Vlsd = "m5ab-canopen-vlsd420";   // t0 date1 (VLSD) time2 (MLSD, size tlen3) tlen3 n4
        private const int Dates = 1, Times = 2, Labels = 3, Blobs = 4, Pkt = 5, PDate = 7, PLabel = 9;

        private static int Field(byte[] bytes, int index) { return M3Wire.Payload(bytes, M5DWire.Cn(bytes, index)); }
        private static int CaField(byte[] bytes, int index) { return M3Wire.Payload(bytes, M5DWire.Ca(bytes, index)); }
        private static MdfReader Open(byte[] bytes) { return MdfReader.Open(new MemoryStream(bytes, false)); }
        private static MdfChannelGroup Group(MdfReader reader) { return reader.ReadMetadata()[0].ChannelGroups[0]; }
        private static string Reason(byte[] bytes, int index) { using (MdfReader reader = Open(bytes)) { return Group(reader).Channels[index].UnsupportedReason; } }
        private static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant(); }

        [Fact]
        public void StrideBelowTheElementWidthIsRefused()
        {
            byte[] bytes = FixtureCorpus.Read(Arrays);
            M3Wire.Put(bytes, CaField(bytes, Labels) + 8, 4, 4); // 5-byte UTF-8 elements
            Assert.Equal("Array byte stride must be at least the element's byte width.", Reason(bytes, Labels));
            Assert.Null(Reason(bytes, Blobs));
        }

        [Fact]
        public void ElementsBeyondTheDataAreaAreMalformed()
        {
            byte[] bytes = FixtureCorpus.Read(Arrays);
            M3Wire.Put(bytes, CaField(bytes, Blobs) + 8, 20, 4); // 66 + 2 * 20 + 3 > 95
            using (MdfReader reader = Open(bytes))
            {
                Assert.Equal("MDF013", Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
            }
        }

        [Fact]
        public void PerElementInvalidationNeedsTheChannelFlag()
        {
            byte[] bytes = FixtureCorpus.Read(Arrays);
            M3Wire.Put(bytes, Field(bytes, Dates) + 12, 0, 4);
            Assert.Equal("A per-element invalidation stride requires the channel's invalidation-bit flag.", Reason(bytes, Dates));
        }

        [Fact]
        public void CanOpenArraysOfAnotherWidthKeepTheCanOpenReason()
        {
            byte[] bytes = FixtureCorpus.Read(Arrays);
            M3Wire.Put(bytes, Field(bytes, Dates) + 8, 64, 4);
            Assert.Equal(MetadataReader.CanOpenReason, Reason(bytes, Dates));
            bytes = FixtureCorpus.Read(Arrays);
            bytes[Field(bytes, Times)] = 6; // virtual
            Assert.NotNull(Reason(bytes, Times));
        }

        [Fact]
        public void InverseLayoutReadsColumnMajorElements()
        {
            byte[] bytes = FixtureCorpus.Read(Arrays);
            int record = M3Wire.Payload(bytes, M5DWire.Root(bytes));
            using (MdfReader reader = Open(bytes))
            {
                MdfSampleBatch batch = reader.ReadSamples(Group(reader), new[] { Times }).Single();
                // Row-major [0,1] is stored element 2 (column-major) at 32 + 2 * 6.
                Assert.Equal(Hex(bytes.Skip(record + 44).Take(6).ToArray()), Hex(batch.GetValue(0, 0, 1).GetByteArrayValue()));
                Assert.Equal(Hex(bytes.Skip(record + 38).Take(6).ToArray()), Hex(batch.GetValue(0, 0, 2).GetByteArrayValue()));
            }
            M3Wire.Put(bytes, CaField(bytes, Times) + 4, 0, 4); // drop the inverse flag: row-major storage
            using (MdfReader reader = Open(bytes))
            {
                MdfSampleBatch batch = reader.ReadSamples(Group(reader), new[] { Times }).Single();
                Assert.Equal(Hex(bytes.Skip(record + 38).Take(6).ToArray()), Hex(batch.GetValue(0, 0, 1).GetByteArrayValue()));
            }
        }

        [Fact]
        public void TextComponentsOutsideTheirParentAreMalformed()
        {
            byte[] bytes = FixtureCorpus.Read(Arrays);
            int label = M5EWire.Child(bytes, M5DWire.Cn(bytes, Pkt), 2);
            M3Wire.Put(bytes, M3Wire.Payload(bytes, label) + 4, 90, 4); // 90 + 6 > parent end 94
            using (MdfReader reader = Open(bytes))
            {
                Assert.Equal("MDF013", Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
            }
        }

        [Fact]
        public void ComponentValuesAreTheAbsoluteWindowOfTheParent()
        {
            byte[] bytes = FixtureCorpus.Read(Arrays);
            using (MdfReader reader = Open(bytes))
            {
                MdfChannelGroup group = Group(reader);
                Assert.Equal(new[] { "pdate", "pcount", "plabel" }, group.Channels[Pkt].Components.Select(c => c.Name));
                MdfSampleBatch batch = reader.ReadSamples(group, new[] { Pkt, PDate, PLabel }).Single();
                for (int row = 0; row < batch.SampleCount; row++)
                {
                    string window = Hex(batch.GetValue(row, 0).GetByteArrayValue());
                    Assert.Equal(window.Substring(0, 14), Hex(batch.GetValue(row, 1).GetByteArrayValue()));
                    DateTime ignored;
                    Assert.Equal(MdfCalendarKind.CanOpenDate, group.Channels[PDate].CalendarKind);
                    MdfCalendar.TryDecode(group.Channels[PDate], batch.GetValue(row, 1), out ignored);
                    Assert.Equal(MdfValueKind.Text, batch.GetValue(row, 2).Kind);
                }
            }
        }

        [Fact]
        public void VlsdAndMlsdCanOpenArraysAreRefused()
        {
            byte[] bytes = FixtureCorpus.Read(Vlsd);
            M5DWire.Array(ref bytes, new ulong[] { 2 }, 8); // on channel 2 (time, MLSD)
            Assert.Equal(MetadataReader.TextArrayReason, Reason(bytes, 2));
            bytes = FixtureCorpus.Read(Vlsd);
            int ca = M5DWire.Array(ref bytes, new ulong[] { 2 }, 8);
            M3Wire.Put(bytes, M3Wire.Link(M5DWire.Cn(bytes, 2), 1), 0);
            M3Wire.Put(bytes, M3Wire.Link(M5DWire.Cn(bytes, 1), 1), (ulong)ca); // move it to the VLSD date
            Assert.Equal(MetadataReader.TextArrayReason, Reason(bytes, 1));
        }

        [Fact]
        public void CanOpenVlsdAndMlsdValuesAreTheirStoredBytes()
        {
            using (MdfReader reader = Open(FixtureCorpus.Read(Vlsd)))
            {
                MdfChannelGroup group = Group(reader);
                Assert.Equal(MdfCalendarKind.CanOpenDate, group.Channels[1].CalendarKind);
                Assert.Equal(MdfCalendarKind.CanOpenTime, group.Channels[2].CalendarKind);
                MdfSampleBatch batch = reader.ReadSamples(group, new[] { 1, 2 }).Single();
                Assert.Equal(new[] { 7, 0, 6, 0, 7, 7 }, Enumerable.Range(0, 6).Select(r => batch.GetValue(r, 0).GetByteArrayValue().Length));
                Assert.Equal(new[] { 6, 0, 3, 6, 6, 6 }, Enumerable.Range(0, 6).Select(r => batch.GetValue(r, 1).GetByteArrayValue().Length));
                Assert.False(batch.GetValue(3, 0).IsValid);
                DateTime value;
                Assert.True(MdfCalendar.TryDecode(group.Channels[1], batch.GetValue(0, 0), out value));
                Assert.Equal(new DateTime(2026, 11, 16, 14, 5, 30, 250), value);
                Assert.False(MdfCalendar.TryDecode(group.Channels[1], batch.GetValue(2, 0), out value)); // 6 bytes for a date
                Assert.False(MdfCalendar.TryDecode(group.Channels[2], batch.GetValue(2, 1), out value)); // 3 bytes for a time
            }
        }

        [Fact]
        public void CanOpenVlsdWithoutSignalDataNeverDereferences()
        {
            byte[] bytes = FixtureCorpus.Read(Vlsd);
            int date = M5DWire.Cn(bytes, 1);
            M3Wire.Put(bytes, M3Wire.Link(date, 5), 0);
            using (MdfReader reader = Open(bytes))
            {
                MdfChannelGroup group = Group(reader);
                Assert.True(group.Channels[1].CanReadRaw, group.Channels[1].UnsupportedReason);
                Assert.Throws<MdfFormatException>(() => reader.ReadSamples(group, new[] { 1 }).ToList());
                Assert.Equal(6, reader.ReadSamples(group, new[] { 4 }).Sum(b => b.SampleCount));
            }
        }
    }
}
