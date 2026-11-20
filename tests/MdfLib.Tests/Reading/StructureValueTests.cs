// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Reading
{
    // literal mutations of the frozen m5l-structures410 file, never the library's writer.
    // Catalog: time0 pkt1 vpkt2 ipkt3 cpkt4 opkt5 after6 | counter7 inner8 sig9 real10 arr11 v_u16 12 i_u8 13 c_u16 14 onest15 o_u8 16 o_tail17.
    public sealed class StructureValueTests
    {
        private const string Id = "m5l-structures410";
        private const int DataBytes = 57;
        private const int Record = 58;
        private const string Ancestor = "An ancestor structure declares invalidity, layout flags or channel-specific data whose inheritance is not supported.";
        private const string Identity = "Text and byte-array channels support only identity conversion.";

        [Theory]
        [InlineData(1, 8, 24)]   // pkt, including uncovered bytes 18-19 and 24-31
        [InlineData(8, 12, 8)]   // nested inner, including uncovered bytes 18-19
        [InlineData(2, 32, 8)]   // vpkt
        [InlineData(5, 48, 8)]   // opkt
        public void ParentValueIsTheExactRecordWindow(int channel, int offset, int length)
        {
            byte[] bytes = M5DWire.Read(Id);
            MdfSampleValue[] values = Column(bytes, channel);
            Assert.Equal(5, values.Length);
            for (int row = 0; row < 5; row++)
            {
                Assert.Equal(MdfValueKind.ByteArray, values[row].Kind);
                Assert.Equal(bytes.Skip(RecordStart(bytes, row) + offset).Take(length), values[row].GetByteArrayValue());
            }
        }

        [Fact]
        public void UncoveredWindowBytesChangeOnlyTheParentValue()
        {
            byte[] bytes = M5DWire.Read(Id);
            int[] all = { 1, 7, 8, 9, 10, 11, 14 };
            List<MdfSampleValue[]> before = all.Select(c => Column(bytes, c)).ToList();
            bytes[RecordStart(bytes, 2) + 25] ^= 0xFF;
            bytes[RecordStart(bytes, 2) + 18] ^= 0xFF;
            for (int i = 0; i < all.Length; i++)
            {
                MdfSampleValue[] after = Column(bytes, all[i]);
                for (int row = 0; row < 5; row++)
                {
                    bool parent = all[i] == 1 || all[i] == 8;
                    bool changed = !Same(before[i][row], after[row]);
                    Assert.Equal(parent && row == 2, changed);
                }
            }
        }

        [Fact]
        public void OwnInvalidationBitDrivesOnlyTheParentWhileDescendantsStayRefused()
        {
            byte[] bytes = M5DWire.Read(Id);
            MdfSampleValue[] before = Column(bytes, 2);
            Assert.Equal(new[] { true, false, true, false, true }, before.Select(v => v.IsValid));
            bytes[RecordStart(bytes, 0) + DataBytes] ^= 1; // vpkt bit 0
            Assert.Equal(new[] { false, false, true, false, true }, Column(bytes, 2).Select(v => v.IsValid));
            Assert.Equal(before.Select(v => v.GetByteArrayValue()), Column(bytes, 2).Select(v => v.GetByteArrayValue()));
            Assert.Equal(Ancestor, Catalog(bytes).Channels[12].UnsupportedReason);
            Assert.All(Column(bytes, 3), v => Assert.False(v.IsValid)); // all-invalid ipkt
        }

        [Fact]
        public void BlockedNestedParentAndNonIdentityParentKeepTheirReasonsWithoutIoAtRefusal()
        {
            byte[] bytes = M5DWire.Read(Id);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(Identity, group.Channels[4].UnsupportedReason);
                Assert.Equal(MdfValueKind.ByteArray, group.Channels[4].ValueKind);
                Assert.True(group.Channels[14].CanReadRaw, group.Channels[14].UnsupportedReason); // CC is not inherited
                Assert.Equal(Ancestor, group.Channels[15].UnsupportedReason);
                Assert.Equal(new[] { 16 }, group.Channels[15].Components.Select(c => c.Index));
                int before = source.ReadCalls;
                foreach (int refused in new[] { 4, 15, 16, 17 })
                {
                    Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { refused }).ToList());
                    Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 1, refused }).ToList());
                }
                Assert.Equal(before, source.ReadCalls);
            }
        }

        [Fact]
        public void RemovingTheParentConversionMakesItReadable()
        {
            byte[] bytes = M5DWire.Read(Id);
            M5EWire.Link(bytes, M5DWire.Cn(bytes, 4), 4, 0);
            MdfSampleValue[] values = Column(bytes, 4);
            for (int row = 0; row < 5; row++)
            { Assert.Equal(bytes.Skip(RecordStart(bytes, row) + 44).Take(4), values[row].GetByteArrayValue()); }
        }

        [Fact]
        public void FixedTextLeavesInsideAStructureReadTheirWindow()
        {
            byte[] bytes = M5DWire.Read(Id);
            int counter = M5EWire.Child(bytes, M5DWire.Cn(bytes, 1), 0);
            M5EWire.Set(bytes, counter, 2, 7, 1); // UTF-8 fixed text leaf (readable like a top-level field)
            MdfChannelGroup group = Catalog(bytes);
            Assert.True(group.Channels[7].CanReadRaw, group.Channels[7].UnsupportedReason);
            Assert.Equal(MdfValueKind.Text, group.Channels[7].ValueKind);
            Assert.All(Column(bytes, 7), v => Assert.Equal(MdfValueKind.Text, v.Kind));

            Assert.True(group.Channels[1].CanReadRaw, group.Channels[1].UnsupportedReason);
            Assert.True(group.Channels[8].CanReadRaw, group.Channels[8].UnsupportedReason);
        }

        [Theory]
        [InlineData(392U, true)]    // 8 + 49 = 57: exact end of the data area
        [InlineData(400U, false)]
        public void ParentWindowIsBoundedByTheDataArea(uint bits, bool fits)
        {
            byte[] bytes = M5DWire.Read(Id);
            int pkt = M5DWire.Cn(bytes, 1);
            M5EWire.BitCount(bytes, pkt, bits);
            if (fits)
            {
                Assert.All(Column(bytes, 1), v => Assert.Equal(49, v.GetByteArrayValue().Length));
                return;
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfFormatException error = Assert.Throws<MdfFormatException>(() => reader.ReadMetadata());
                Assert.Equal("MDF013", error.DiagnosticCode);
                Assert.Equal((ulong)pkt, error.Offset);
            }
        }

        [Fact]
        public void ParentSelectionIsNeverExpandedAndEachValueIsAnIndependentCopy()
        {
            byte[] bytes = M5DWire.Read(Id);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                MdfSampleBatch batch = reader.ReadSamples(group, new[] { 1, 8, 9 }).Single();
                Assert.Equal(new[] { 1, 8, 9 }, batch.Channels.Select(c => c.Index));
                byte[] first = batch.GetValue(0, 0).GetByteArrayValue();
                first[0] ^= 0xFF;
                Assert.NotEqual(first, batch.GetValue(0, 0).GetByteArrayValue());
                byte[] window = batch.GetValue(3, 0).GetByteArrayValue();
                byte[] inner = batch.GetValue(3, 1).GetByteArrayValue();
                Assert.Equal(window.Skip(4).Take(8), inner); // nested window lies at its absolute offset 12 = 8 + 4
                Assert.Equal((long)BitConverter.ToInt16(inner, 0), batch.GetValue(3, 2).SignedValue);
            }
        }

        [Fact]
        public void WiderWindowsCostMoreBatchBudgetWithoutLosingRows()
        {
            byte[] bytes = M5DWire.Read(Id);
            Func<int, List<MdfSampleBatch>> read = channel =>
            {
                using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false), false, new MdfReaderOptions { MaximumSampleBatchBytes = 600 }))
                {
                    return reader.ReadSamples(reader.ReadMetadata()[0].ChannelGroups[0], new[] { channel }).ToList();
                }
            };
            List<MdfSampleBatch> wide = read(1);
            List<MdfSampleBatch> narrow = read(8);
            Assert.True(wide.Count > narrow.Count, wide.Count + " vs " + narrow.Count);
            Assert.Equal(5, wide.Sum(b => b.SampleCount));
            Assert.Equal(5, narrow.Sum(b => b.SampleCount));
            Assert.Equal(Column(bytes, 1).Select(v => v.GetByteArrayValue()),
                wide.SelectMany(b => Enumerable.Range(0, b.SampleCount).Select(r => b.GetValue(r, 0).GetByteArrayValue())));
        }

        private static bool Same(MdfSampleValue a, MdfSampleValue b)
        {
            if (a.Kind != b.Kind || a.IsValid != b.IsValid) { return false; }
            if (a.Kind == MdfValueKind.ByteArray) { return a.GetByteArrayValue().SequenceEqual(b.GetByteArrayValue()); }
            if (a.Kind == MdfValueKind.UnsignedInteger) { return a.UnsignedValue == b.UnsignedValue; }
            if (a.Kind == MdfValueKind.SignedInteger) { return a.SignedValue == b.SignedValue; }
            return BitConverter.DoubleToInt64Bits(a.FloatingPointValue) == BitConverter.DoubleToInt64Bits(b.FloatingPointValue);
        }

        private static int RecordStart(byte[] bytes, int row) { return M5DWire.Root(bytes) + 24 + row * Record; }

        private static MdfChannelGroup Catalog(byte[] bytes)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false))) { return reader.ReadMetadata()[0].ChannelGroups[0]; }
        }

        private static MdfSampleValue[] Column(byte[] bytes, int channel)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.True(group.Channels[channel].CanReadRaw, group.Channels[channel].UnsupportedReason);
                // Arrays compare their first element only here; full element checks live in the fixture tests.
                return reader.ReadSamples(group, new[] { channel }, batchSize: 2)
                    .SelectMany(b => Enumerable.Range(0, b.SampleCount).Select(r => b.GetValue(r, 0, 0))).ToArray();
            }
        }
    }
}
