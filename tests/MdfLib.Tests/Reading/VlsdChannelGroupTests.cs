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
    // VLSD channel groups over literal mutations of the frozen pre-parser m5j corpus.
    public sealed class VlsdChannelGroupTests
    {
        private const string VlsdReason = "A VLSD channel group holds variable-length signal data of another channel and has no sample records of its own.";
        private const string TargetReason = "A VLSD channel's channel-group link must address a VLSD channel group of the same unsorted data group.";
        private const string SharedReason = "A VLSD channel group must be referenced by exactly one VLSD channel.";
        private const int ParentBytes = 34; // 33 data bytes + 1 invalidation byte.

        // m5j-id1-dt410: CG chain parent(ID 1), text(2), bytes(3), u16(4); parent CN chain time, vtext, vbytes, vu16, counter.
        private static byte[] Fixture() { return FixtureCorpus.Read("m5j-id1-dt410"); }
        private static int Dg(byte[] bytes) { return (int)M3Wire.U64(bytes, M3Wire.Link(64, 0)); }
        private static int U32(byte[] bytes, int offset) { return bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24); }
        private static int Cg(byte[] bytes, int index)
        {
            int cg = (int)M3Wire.U64(bytes, M3Wire.Link(Dg(bytes), 1));
            for (int step = 0; step < index; step++) { cg = (int)M3Wire.U64(bytes, M3Wire.Link(cg, 0)); }
            return cg;
        }
        private static int Cn(byte[] bytes, int index)
        {
            int cn = (int)M3Wire.U64(bytes, M3Wire.Link(Cg(bytes, 0), 1));
            for (int step = 0; step < index; step++) { cn = (int)M3Wire.U64(bytes, M3Wire.Link(cn, 0)); }
            return cn;
        }
        private static int Data(byte[] bytes) { return (int)M3Wire.U64(bytes, M3Wire.Link(Dg(bytes), 2)) + 24; }

        // Absolute offsets of each parent record body in the DT payload (literal 1-byte-ID frame walk).
        private static List<int> ParentRecords(byte[] bytes)
        {
            int data = Data(bytes);
            int end = data + (int)M3Wire.U64(bytes, data - 16) - 24;
            List<int> parents = new List<int>();
            int position = data;
            while (position < end)
            {
                byte id = bytes[position++];
                if (id == 1) { parents.Add(position); position += ParentBytes; }
                else { position += 4 + U32(bytes, position); }
            }
            Assert.Equal(end, position);
            return parents;
        }

        private static IReadOnlyList<MdfChannelGroup> Groups(byte[] bytes)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false))) { return reader.ReadMetadata()[0].ChannelGroups; }
        }

        private static MdfSampleValue[] Column(byte[] bytes, int channel, MdfReaderOptions options = null)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false), false, options))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                return reader.ReadSamples(group, new[] { channel }, batchSize: 2).SelectMany(b => Enumerable.Range(0, b.SampleCount).Select(r => b.GetValue(r, 0))).ToArray();
            }
        }

        [Fact]
        public void VlsdGroupsStayInTheCatalogAsInspectableGroupsWithoutRecords()
        {
            IReadOnlyList<MdfChannelGroup> groups = Groups(Fixture());
            Assert.Equal(4, groups.Count);
            Assert.True(groups[0].CanReadSamples, groups[0].UnsupportedReason);
            Assert.Equal(34UL, groups[0].RecordByteCount);
            Assert.All(groups[0].Channels, c => Assert.True(c.CanReadRaw, c.UnsupportedReason));
            ulong[] cycles = { 4, 5, 5 };
            for (int index = 1; index < 4; index++)
            {
                Assert.False(groups[index].CanReadSamples);
                Assert.Equal(VlsdReason, groups[index].UnsupportedReason);
                Assert.Equal(cycles[index - 1], groups[index].SampleCount);
                Assert.Equal(0UL, groups[index].RecordByteCount);
                Assert.Empty(groups[index].Channels);
            }
        }

        [Fact]
        public void ValuesComeFromTheReferencedVlsdGroupRecordsAndInvalidOffsetsAreNeverFollowed()
        {
            byte[] bytes = Fixture();
            MdfSampleValue[] text = Column(bytes, 1);
            Assert.Equal(new[] { "alpha", "", "\u20AC\uD83D\uDE00" }, text.Take(3).Select(v => v.TextValue));
            Assert.False(text[3].IsValid);                    // garbage offset 0xFFFFFFFFFFFFFF00
            Assert.Empty(text[3].GetByteArrayValue());
            Assert.Equal(new string('z', 40), text[4].TextValue);
            Assert.Equal(45, text[4].GetByteArrayValue().Length);
            MdfSampleValue[] blobs = Column(bytes, 2);
            Assert.Equal(new[] { 3, 0, 256, 7, 1 }, blobs.Select(v => v.GetByteArrayValue().Length));
            Assert.Equal(Enumerable.Range(0, 256).Select(x => (byte)x), blobs[2].GetByteArrayValue());
            Assert.Equal(new[] { "Hi", "\uD83D\uDE00x", "", "\u1E9E", "end" }, Column(bytes, 3).Select(v => v.TextValue));
            Assert.Equal(new ulong[] { 7, 0, 255, 42, 1 }, Column(bytes, 4).Select(v => v.UnsignedValue));
        }

        [Fact]
        public void DeclaredVlsdTotalIsInformationalOnly()
        {
            byte[] bytes = Fixture();
            int payload = M3Wire.Payload(bytes, Cg(bytes, 1));
            M3Wire.Put(bytes, payload + 24, 0xDEADBEEF, 4);
            M3Wire.Put(bytes, payload + 28, 7, 4);
            Assert.Equal("alpha", Column(bytes, 1)[0].TextValue);
        }

        [Fact]
        public void SharedVlsdGroupRefusesEveryReferencingChannelWithoutIo()
        {
            byte[] bytes = Fixture();
            M3Wire.Put(bytes, M3Wire.Link(Cn(bytes, 3), 5), (ulong)Cg(bytes, 1)); // u16 now also points at the text VLSD CG
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(SharedReason, group.Channels[1].UnsupportedReason);
                Assert.Equal(SharedReason, group.Channels[3].UnsupportedReason);
                Assert.True(group.Channels[2].CanReadRaw);
                int before = source.ReadCalls;
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 1 }));
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 2, 3 }));
                Assert.Equal(before, source.ReadCalls);
                // The u16 VLSD CG is now an orphan; its records are still framed and skipped.
                Assert.Equal(new[] { 3, 0, 256, 7, 1 }, reader.ReadSamples(group, new[] { 2 }, batchSize: 1).Select(b => b.GetValue(0, 0).GetByteArrayValue().Length));
            }
        }

        [Fact]
        public void LinksToNonVlsdOrForeignGroupsAreUnsupported()
        {
            byte[] parent = Fixture();
            M3Wire.Put(parent, M3Wire.Link(Cn(parent, 1), 5), (ulong)Cg(parent, 0));
            Assert.Equal(TargetReason, Groups(parent)[0].Channels[1].UnsupportedReason);

            byte[] foreign = Fixture();
            byte[] payload = new byte[32];
            payload[16] = 1; // VLSD flag, but not part of this DG's CG chain
            int orphan = M5Wire.Add(ref foreign, "CG", new ulong[6], payload);
            M3Wire.Put(foreign, M3Wire.Link(Cn(foreign, 2), 5), (ulong)orphan);
            IReadOnlyList<MdfChannelGroup> groups = Groups(foreign);
            Assert.Equal(TargetReason, groups[0].Channels[2].UnsupportedReason);
            Assert.True(groups[0].CanReadSamples);
            Assert.True(groups[0].Channels[1].CanReadRaw);
        }

        [Theory]
        [InlineData(3)]
        [InlineData(17)]
        [InlineData(0x8001)]
        public void OtherFlagsOnAVlsdGroupKeepTheWholeDgUnsupported(int flags)
        {
            byte[] bytes = Fixture();
            M3Wire.Put(bytes, M3Wire.Payload(bytes, Cg(bytes, 2)) + 16, (ulong)flags, 2);
            IReadOnlyList<MdfChannelGroup> groups = Groups(bytes);
            Assert.False(groups[0].CanReadSamples);
            Assert.Contains("framing", groups[0].UnsupportedReason);
            Assert.Equal(VlsdReason, groups[1].UnsupportedReason);
        }

        [Fact]
        public void VlsdGroupWithChannelsIsNotAVlsdGroup()
        {
            byte[] bytes = Fixture();
            byte[] payload = new byte[72];
            payload[0] = 0; payload[2] = 0; M3Wire.Put(payload, 8, 8, 4);
            int cn = M5Wire.Add(ref bytes, "CN", new ulong[8], payload);
            M3Wire.Put(bytes, M3Wire.Link(Cg(bytes, 1), 1), (ulong)cn);
            IReadOnlyList<MdfChannelGroup> groups = Groups(bytes);
            Assert.Contains("Extended or variable-length", groups[1].UnsupportedReason);
            Assert.Contains("framing", groups[0].UnsupportedReason);
            Assert.Equal(TargetReason, groups[0].Channels[1].UnsupportedReason);
        }

        [Theory]
        [InlineData(1UL)]      // inside the first entry
        [InlineData(2UL)]
        [InlineData(10000UL)]  // beyond the virtual stream
        public void OffsetsMustAddressAnEntryStart(ulong offset)
        {
            byte[] bytes = Fixture();
            M3Wire.Put(bytes, ParentRecords(bytes)[0] + 16, offset); // vbytes row 0
            Assert.Equal(5, Column(bytes, 1).Length);
            MdfFormatException error = Assert.Throws<MdfFormatException>(() => Column(bytes, 2));
            Assert.Equal("MDF013", error.DiagnosticCode);
        }

        [Fact]
        public void DecreasingAndRepeatedOffsetsRewindThePrivateScan()
        {
            byte[] bytes = Fixture();
            List<int> parents = ParentRecords(bytes);
            ulong[] offsets = parents.Select(p => M3Wire.U64(bytes, p + 16)).ToArray();
            ulong[] reordered = { offsets[4], offsets[2], offsets[2], offsets[0], offsets[4] };
            for (int row = 0; row < 5; row++) { M3Wire.Put(bytes, parents[row] + 16, reordered[row]); }
            Assert.Equal(new[] { 1, 256, 256, 3, 1 }, Column(bytes, 2).Select(v => v.GetByteArrayValue().Length));
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(new[] { 256, 3 }, reader.ReadSamples(group, new[] { 2 }, 2, 2, 1).Select(b => b.GetValue(0, 0).GetByteArrayValue().Length));
            }
        }

        [Theory]
        [InlineData(1, 3UL)]
        [InlineData(1, 5UL)]
        [InlineData(3, 0UL)]
        public void VlsdGroupCycleMismatchIsAFramingErrorBeforeTheFirstRow(int group, ulong cycles)
        {
            byte[] bytes = Fixture();
            M3Wire.Put(bytes, M3Wire.Payload(bytes, Cg(bytes, group)) + 8, cycles);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup parent = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(cycles, reader.ReadMetadata()[0].ChannelGroups[group].SampleCount);
                Assert.Equal("MDF013", Assert.Throws<MdfFormatException>(() => reader.ReadSamples(parent, new[] { 4 }).ToList()).DiagnosticCode);
            }
        }

        [Fact]
        public void VlsdRecordLengthBeyondTheStreamOrTruncatedIsAFramingError()
        {
            byte[] bytes = Fixture();
            int data = Data(bytes);
            Assert.Equal(2, bytes[data]);               // the first frame is the text VLSD CG
            M3Wire.Put(bytes, data + 1, 100000, 4);
            Assert.Equal("MDF013", Assert.Throws<MdfFormatException>(() => Column(bytes, 4)).DiagnosticCode);

            byte[] truncated = Fixture();
            int root = Data(truncated) - 24;
            ulong length = M3Wire.U64(truncated, root + 8);
            // Cut the DT so that the last frame (a parent record) loses its body; the stream then ends inside framing.
            M3Wire.Put(truncated, root + 8, length - ParentBytes + 2);
            Assert.Equal("MDF013", Assert.Throws<MdfFormatException>(() => Column(truncated, 4)).DiagnosticCode);
        }

        [Fact]
        public void EntryLongerThanTheReadBufferCapIsAResourceLimitBeforeAllocation()
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(Fixture(), false), false, new MdfReaderOptions { MaximumReadBufferBytes = 255 }))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(2, reader.ReadSamples(group, new[] { 2 }, 0, 2).Sum(b => b.SampleCount));
                MdfResourceLimitException error = Assert.Throws<MdfResourceLimitException>(() => reader.ReadSamples(group, new[] { 2 }, 2, 1).ToList());
                Assert.Equal(nameof(MdfReaderOptions.MaximumReadBufferBytes), error.LimitName);
            }
        }

        [Fact]
        public void VlsdGroupItselfCannotBeEnumerated()
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(Fixture(), false)))
            {
                MdfChannelGroup vlsd = reader.ReadMetadata()[0].ChannelGroups[1];
                Assert.Empty(vlsd.Channels);
                Assert.Throws<ArgumentException>(() => reader.ReadSamples(vlsd, new[] { 0 }));
            }
        }
    }
}
