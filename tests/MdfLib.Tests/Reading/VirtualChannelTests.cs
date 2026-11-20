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
    // literal mutations of frozen stdlib files, never the library's writer.
    public sealed class VirtualChannelTests
    {
        private const string Sorted = "m5o-virtual410"; // 0 virtual master, 1 u16 (inval bit), 2 virtual data, 3 i32
        private const int VirtualData = 2;

        private static void Field(byte[] bytes, int cn, int offset, ulong value, int width)
        {
            M3Wire.Put(bytes, M3Wire.Payload(bytes, cn) + offset, value, width);
        }

        [Theory]
        [InlineData(2, 2UL, 1, "unsigned")]          // signed data type
        [InlineData(2, 3UL, 1, "unsigned")]
        [InlineData(2, 4UL, 1, "unsigned")]          // IEEE
        [InlineData(2, 5UL, 1, "unsigned")]
        [InlineData(2, 6UL, 1, "unsigned")]          // text kinds
        [InlineData(2, 10UL, 1, "unsigned")]
        [InlineData(12, 1UL, 4, "Invalidation")]     // all-invalid flag
        [InlineData(12, 2UL, 4, "Invalidation")]     // invalidation-bit flag (even outside the area: no MDF013)
        [InlineData(12, 0x2000UL, 4, "flags")]       // unknown layout flag
        [InlineData(8, 65UL, 4, "greater than 64")]
        public void UnsupportedVirtualFieldsAreInspectableAndOtherChannelsStillRead(int offset, ulong value, int width, string reason)
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            Field(bytes, M5DWire.Cn(bytes, VirtualData), offset, value, width);
            if (offset == 12 && value == 2) { Field(bytes, M5DWire.Cn(bytes, VirtualData), 16, 1000, 4); }
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                MdfChannel channel = group.Channels[VirtualData];
                Assert.False(channel.CanReadRaw);
                Assert.Contains(reason, channel.UnsupportedReason);
                int before = source.ReadCalls;
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { VirtualData }));
                Assert.Equal(before, source.ReadCalls);
                Assert.Equal(new ulong[] { 0, 1, 2, 3, 4, 5 }, Raw(reader, group, 0));
            }
        }

        [Fact]
        public void ChannelDataLinkIsUnsupported()
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            int cn = M5DWire.Cn(bytes, VirtualData);
            M3Wire.Put(bytes, M3Wire.Link(cn, 5), (ulong)M5DWire.Cn(bytes, 3));
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannel channel = reader.ReadMetadata()[0].ChannelGroups[0].Channels[VirtualData];
                Assert.False(channel.CanReadRaw);
                Assert.Contains("Channel-specific data", channel.UnsupportedReason);
            }
        }

        [Theory]
        [InlineData(7)] // VLSC (sync 4 is, MLSD 5 is)
        [InlineData(9)] // unknown
        public void OtherChannelTypesKeepTheRoleRefusal(int role)
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            Field(bytes, M5DWire.Cn(bytes, VirtualData), 0, (ulong)role, 1);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannel channel = reader.ReadMetadata()[0].ChannelGroups[0].Channels[VirtualData];
                Assert.Equal("Only stored value and stored master channel roles are supported.", channel.UnsupportedReason);
                Assert.False(channel.IsMaster);
            }
        }

        [Theory]
        [InlineData(4, 100000UL, 4)] // byte offset far outside the 7-byte record
        [InlineData(3, 5UL, 1)]      // bit offset
        [InlineData(8, 7UL, 4)]      // odd bit count: never truncates the index
        [InlineData(8, 0UL, 4)]
        [InlineData(2, 1UL, 1)]      // big-endian unsigned: byte order is not used
        public void FieldPlacementIsNotARecordFieldForVirtualChannels(int offset, ulong value, int width)
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            Field(bytes, M5DWire.Cn(bytes, VirtualData), offset, value, width);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.True(group.Channels[VirtualData].CanReadRaw, group.Channels[VirtualData].UnsupportedReason);
                Assert.Equal(new ulong[] { 0, 1, 2, 3, 4, 5 }, Raw(reader, group, VirtualData));
            }
        }

        [Fact]
        public void VirtualArraysAreUnsupportedButStayInspectable()
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            M5DWire.Array(ref bytes, new ulong[] { 2 });
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                MdfChannel channel = group.Channels[VirtualData];
                Assert.Equal("Virtual array channels are not supported.", channel.UnsupportedReason);
                Assert.NotNull(channel.Array);
                Assert.Equal(new ulong[] { 0, 1, 2, 3, 4, 5 }, Raw(reader, group, 0));
            }
        }

        [Fact]
        public void VirtualStructureParentsAndComponentsAreUnsupportedWithoutFieldChecks()
        {
            byte[] bytes = M5EWire.Read();
            int packet = M5EWire.Packet(bytes);
            int child = M5EWire.Child(bytes, packet, 0);
            Field(bytes, child, 0, 6, 1);
            Field(bytes, child, 2, 0, 1);
            Field(bytes, child, 12, 0, 4);
            M5EWire.ByteOffset(bytes, child, 100000); // Would be MDF013 for a stored component.
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                MdfChannel component = group.Channels.Single(c => c.Parent != null && c.Parent.Index == 2 && c == c.Parent.Components[0]);
                Assert.Equal("Virtual structure components are not supported.", component.UnsupportedReason);
                Assert.True(group.Channels[0].CanReadRaw);
            }

            bytes = M5EWire.Read();
            packet = M5EWire.Packet(bytes);
            Field(bytes, packet, 0, 6, 1);
            Field(bytes, packet, 2, 0, 1);
            Field(bytes, packet, 8, 64, 4);
            Field(bytes, packet, 12, 0, 4);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannel parent = reader.ReadMetadata()[0].ChannelGroups[0].Channels[2];
                Assert.Equal("Virtual structure channels are not supported.", parent.UnsupportedReason);
                Assert.Empty(parent.Components);
            }
        }

        [Fact]
        public void AVirtualMasterIsAMasterButNotAStoredRemoteMasterTarget()
        {
            byte[] bytes = M5CWire.Read("m5c-ld-remote420");
            int master = M5DWire.Cn(bytes, 0);
            Field(bytes, master, 0, 3, 1);
            Field(bytes, master, 2, 0, 1);
            Field(bytes, master, 12, 0, 4);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                IReadOnlyList<MdfDataGroup> catalog = reader.ReadMetadata();
                MdfChannelGroup masterGroup = catalog[0].ChannelGroups[0];
                MdfChannel channel = Assert.Single(masterGroup.Channels);
                Assert.True(channel.IsMaster);
                Assert.True(channel.CanReadRaw, channel.UnsupportedReason);
                Assert.False(masterGroup.Layout.HasStoredMasterChannel);
                Assert.Equal(new ulong[] { 0, 1 }, Raw(reader, masterGroup, 0).Take(2));
                MdfChannelGroup remote = catalog.Skip(1).Select(d => d.ChannelGroups[0]).First(g => g.Layout.HasRemoteMaster);
                Assert.False(remote.CanReadSamples);
                Assert.Contains("stored master", remote.UnsupportedReason);
            }
        }

        [Theory]
        [InlineData(0UL, 6UL, 1)]
        [InlineData(1UL, 4UL, 1)]
        [InlineData(3UL, 3UL, 2)]
        [InlineData(5UL, 1UL, 1024)]
        public void RawIsTheAbsoluteRecordIndexForEveryRangeAndBatch(ulong start, ulong count, int batch)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(Sorted), false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                List<MdfSampleBatch> batches = reader.ReadSamples(group, new[] { 2, 0, 1 }, start, count, batch).ToList();
                ulong expected = start;
                foreach (MdfSampleBatch b in batches)
                for (int row = 0; row < b.SampleCount; row++, expected++)
                {
                    Assert.Equal(expected, b.GetValue(row, 0).UnsignedValue);
                    Assert.True(b.GetValue(row, 0).IsValid);
                    Assert.Equal(expected, b.GetValue(row, 1).UnsignedValue);
                }
                Assert.Equal(start + count, expected);
                List<MdfSampleValue> physical = reader.ReadSamples(group, new[] { 0 }, start, count, batch, MdfValueRepresentation.Physical)
                    .SelectMany(b => Enumerable.Range(0, b.SampleCount).Select(r => b.GetValue(r, 0))).ToList();
                Assert.Equal(Enumerable.Range((int)start, (int)count).Select(i => 0.01 * (ulong)i + 1.5), physical.Select(v => v.FloatingPointValue));
                Assert.All(physical, v => Assert.Equal(MdfValueKind.FloatingPoint, v.Kind));
            }
        }

        [Fact]
        public void UnsupportedConversionOnlyBlocksPhysical()
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            int cc = (int)M3Wire.U64(bytes, M3Wire.Link(M5DWire.Cn(bytes, 0), 4));
            bytes[M3Wire.Payload(bytes, cc)] = 200; // unknown/unsupported kind
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.True(group.Channels[0].CanReadRaw);
                Assert.False(group.Channels[0].CanReadPhysical);
                Assert.Equal(new ulong[] { 0, 1, 2, 3, 4, 5 }, Raw(reader, group, 0));
            }
        }

        [Fact]
        public void MetadataReadsNoStorageAndVirtualOnlySelectionsStillReadAndValidateStorage()
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            int root = M5DWire.Root(bytes);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.DoesNotContain(source.ReadRanges, r => r.Item1 >= root + 24); // No DT body before sample reading.
                Assert.Equal(new ulong[] { 0, 1, 2, 3, 4, 5 }, Raw(reader, group, 0));
                Assert.Contains(source.ReadRanges, r => r.Item1 >= root + 24 && r.Item1 < root + 24 + 42);
            }
            M3Wire.Put(bytes, root + 8, 24 + 41); // DT one byte short of six 7-byte records
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Throws<MdfFormatException>(() => reader.ReadSamples(group, new[] { 0, 2 }).ToList());
            }
        }

        [Fact]
        public void UnsortedGroupsCountTheirOwnRecords()
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read("m5o-unsorted411"), false)))
            {
                IReadOnlyList<MdfChannelGroup> groups = reader.ReadMetadata()[0].ChannelGroups;
                Assert.Equal(new ulong[] { 0, 1, 2, 3 }, Raw(reader, groups[0], 0));
                Assert.Equal(new ulong[] { 0, 1, 2 }, Raw(reader, groups[1], 0));
                Assert.Equal(2UL, reader.ReadSamples(groups[1], new[] { 2 }, 2, 1).Single().GetValue(0, 0).UnsignedValue);
                Assert.Equal(7.0, reader.ReadSamples(groups[1], new[] { 2 }, 2, 1, representation: MdfValueRepresentation.Physical).Single().GetValue(0, 0).FloatingPointValue);
            }
        }

        private static ulong[] Raw(MdfReader reader, MdfChannelGroup group, int channel)
        {
            return reader.ReadSamples(group, new[] { channel }, batchSize: 2)
                .SelectMany(b => Enumerable.Range(0, b.SampleCount).Select(r => b.GetValue(r, 0).UnsignedValue)).ToArray();
        }
    }
}
