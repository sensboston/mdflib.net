// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MdfLib.Formats.Mdf4;
using MdfLib.Tests.TestDoubles;
using MdfLib.TestSupport;
using Xunit;

namespace MdfLib.Tests.Reading
{
    // literal mutations of the frozen stdlib m5q-sync410 file, never the library's writer.
    public sealed class SyncChannelTests
    {
        private const string Sorted = "m5q-sync410"; // 0 t (f64 master), 1 frame (u32 index sync -> AT0, inval bit 0),
                                                    // 2 angle (BE i16 angle sync -> AT1, linear 0.5/-10), 3 plain (u8)
        private const int Frame = 1, Angle = 2, Plain = 3;
        private const int RecordBytes = 16; // 15 data + 1 invalidation byte, 6 rows.

        private static void Field(byte[] bytes, int index, int offset, ulong value, int width)
        {
            M3Wire.Put(bytes, M3Wire.Payload(bytes, M5DWire.Cn(bytes, index)) + offset, value, width);
        }

        private static void DataLink(byte[] bytes, int index, ulong target)
        {
            M3Wire.Put(bytes, M3Wire.Link(M5DWire.Cn(bytes, index), 5), target);
        }

        private static int FirstAttachment(byte[] bytes) { return (int)M3Wire.U64(bytes, M3Wire.Link(64, 3)); }

        private static MdfChannelGroup Group(MdfReader reader) { return reader.ReadMetadata()[0].ChannelGroups[0]; }

        private static List<MdfSampleValue> Column(MdfReader reader, int index, MdfValueRepresentation representation = MdfValueRepresentation.Raw)
        {
            return reader.ReadSamples(Group(reader), new[] { index }, representation: representation)
                .SelectMany(b => Enumerable.Range(0, b.SampleCount).Select(r => b.GetValue(r, 0))).ToList();
        }

        [Fact]
        public void SyncChannelsReadTheirStoredValuesValidityAndConversion()
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(Sorted), false)))
            {
                MdfChannelGroup group = Group(reader);
                MdfChannel frame = group.Channels[Frame];
                MdfChannel angle = group.Channels[Angle];
                Assert.True(frame.CanReadRaw && frame.CanReadPhysical, frame.UnsupportedReason);
                Assert.True(angle.CanReadRaw && angle.CanReadPhysical, angle.UnsupportedReason);
                Assert.False(frame.IsMaster);
                Assert.False(angle.IsMaster);
                Assert.Equal((byte)4, frame.SynchronizationType);
                Assert.Equal((byte)2, angle.SynchronizationType);
                Assert.Equal(MdfValueKind.UnsignedInteger, frame.ValueKind);
                Assert.Equal(MdfValueKind.SignedInteger, angle.ValueKind);
                Assert.Equal(MdfConversionKind.Linear, angle.Conversion.Kind);
                Assert.Single(group.Channels, c => c.IsMaster);
                List<MdfSampleValue> frames = Column(reader, Frame);
                Assert.Equal(new ulong[] { 0, 1, 2, 4294967295, 4, 5 }, frames.Select(v => v.UnsignedValue));
                Assert.Equal(new[] { true, true, true, false, true, true }, frames.Select(v => v.IsValid));
                Assert.Equal(new long[] { -720, -360, 0, 360, 32767, -32768 }, Column(reader, Angle).Select(v => v.SignedValue));
                Assert.Equal(new[] { -370.0, -190.0, -10.0, 170.0, 16373.5, -16394.0 },
                    Column(reader, Angle, MdfValueRepresentation.Physical).Select(v => v.FloatingPointValue));
            }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void EverySynchronizationTypeIsAcceptedAndPublished(int syncType)
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            Field(bytes, Frame, 1, (ulong)syncType, 1);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannel frame = Group(reader).Channels[Frame];
                Assert.True(frame.CanReadRaw, frame.UnsupportedReason);
                Assert.Equal((byte)syncType, frame.SynchronizationType);
                Assert.Equal(6, Column(reader, Frame).Count);
            }
        }

        [Theory]
        [InlineData(Frame, 2, 6UL, 1, MetadataReader.SyncNumericReason)]      // Latin-1 text
        [InlineData(Frame, 2, 10UL, 1, MetadataReader.SyncNumericReason)]     // byte array
        [InlineData(Frame, 2, 11UL, 1, MetadataReader.SyncNumericReason)]     // unknown encoding
        [InlineData(Frame, 1, 0UL, 1, MetadataReader.SyncTypeReason)]
        [InlineData(Frame, 1, 5UL, 1, MetadataReader.SyncTypeReason)]
        [InlineData(Frame, 12, 0x2002UL, 4, "flags")]                         // ordinary scalar flag mask
        [InlineData(Angle, 2, 0x0104UL, 2, "IEEE")]                           // unaligned 16-bit IEEE (bit offset 1)
        [InlineData(Angle, 2, 4UL, 1, null)]                                  // byte-aligned 16-bit IEEE is binary16
        [InlineData(Angle, 2, 1UL, 1, null)]                                  // control: BE unsigned stays readable
        public void SyncRulesRefuseIneligibleChannelsAndOtherChannelsStillRead(int index, int offset, ulong value, int width, string reason)
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            Field(bytes, index, offset, value, width);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = Group(reader);
                MdfChannel channel = group.Channels[index];
                if (reason == null)
                {
                    Assert.True(channel.CanReadRaw, channel.UnsupportedReason);
                    return;
                }
                Assert.False(channel.CanReadRaw);
                Assert.Contains(reason, channel.UnsupportedReason);
                int before = source.ReadCalls;
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { index }));
                Assert.Equal(before, source.ReadCalls);
                Assert.Equal(new ulong[] { 7, 6, 5, 4, 3, 2 }, Column(reader, Plain).Select(v => v.UnsignedValue));
            }
        }

        [Fact]
        public void MissingAttachmentLinkIsUnsupported()
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            DataLink(bytes, Frame, 0);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = Group(reader);
                Assert.Equal(MetadataReader.SyncNoAttachmentReason, group.Channels[Frame].UnsupportedReason);
                Assert.True(group.Channels[Angle].CanReadRaw);
            }
        }

        [Theory]
        [InlineData("text")]
        [InlineData("channel")]
        [InlineData("conversion")]
        [InlineData("data")]
        public void DataLinksToOtherBlockTypesAreUnsupported(string target)
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            ulong address = target == "text" ? M3Wire.U64(bytes, M3Wire.Link(M5DWire.Cn(bytes, Plain), 2))
                : target == "channel" ? (ulong)M5DWire.Cn(bytes, Plain)
                : target == "conversion" ? M3Wire.U64(bytes, M3Wire.Link(M5DWire.Cn(bytes, Angle), 4))
                : (ulong)M5DWire.Root(bytes);
            DataLink(bytes, Frame, address);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = Group(reader);
                Assert.Equal(MetadataReader.SyncAttachmentReason, group.Channels[Frame].UnsupportedReason);
                Assert.Equal(MdfValueKind.UnsignedInteger, group.Channels[Frame].ValueKind);
                Assert.Equal(6, Column(reader, Plain).Count); // a DT root resolved untyped still serves its own reads
                Assert.Equal(new long[] { -720, -360, 0, 360, 32767, -32768 }, Column(reader, Angle).Select(v => v.SignedValue));
            }
        }

        [Theory]
        [InlineData(3UL)]
        [InlineData(1UL << 40)]
        public void DataLinksBreakingTheCommonLinkRuleStayMalformed(ulong address)
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            DataLink(bytes, Frame, address);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                Assert.Equal("MDF007", Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
            }
        }

        [Fact]
        public void DataLinkIntoTheMiddleOfABlockIsMalformed()
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            DataLink(bytes, Frame, (ulong)M5DWire.Cn(bytes, Plain) + 24); // aligned, in range, not a block header
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                Assert.Throws<MdfFormatException>(() => reader.ReadMetadata());
            }
        }

        [Theory]
        [InlineData(0UL)]   // numeric
        [InlineData(6UL)]   // text: the invalidation rule still applies before the type refusal
        public void InvalidationBitOutsideTheRecordIsMalformedMetadata(ulong dataType)
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            Field(bytes, Frame, 2, dataType, 1);
            Field(bytes, Frame, 16, 8, 4); // bit 8 of a 1-byte invalidation area
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                Assert.Equal("MDF013", Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
            }
        }

        [Fact]
        public void FieldOutsideTheRecordIsMalformedMetadata()
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            Field(bytes, Frame, 4, 13, 4); // 4 bytes at 13 > 15 data bytes
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                Assert.Equal("MDF013", Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
            }
        }

        [Fact]
        public void SyncArraysAndStructureComponentsAreRefused()
        {
            byte[] array = FixtureCorpus.Read(Sorted);
            M5DWire.Array(ref array, new ulong[] { 1 }); // attaches to channel index 2 = angle
            using (MdfReader reader = MdfReader.Open(new MemoryStream(array, false)))
            {
                Assert.Equal("Synchronization array channels are not supported.", Group(reader).Channels[Angle].UnsupportedReason);
            }
            byte[] component = FixtureCorpus.Read(Sorted);
            Field(component, Plain, 2, 10, 1); // the u8 becomes a 1-byte fixed byte-array structure parent
            int child = M5EWire.AddCn(ref component, 0, 0, 0, 14, 8, 4);
            M3Wire.Put(component, M3Wire.Payload(component, child) + 1, 1, 1); // time synchronization
            M3Wire.Put(component, M3Wire.Link(child, 5), (ulong)FirstAttachment(component));
            M3Wire.Put(component, M3Wire.Link(M5DWire.Cn(component, Plain), 1), (ulong)child);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(component, false)))
            {
                MdfChannelGroup group = Group(reader);
                Assert.Equal(5, group.Channels.Count);
                Assert.Equal("Synchronization channels inside structures are not supported.", group.Channels[4].UnsupportedReason);
                Assert.True(group.Channels[Frame].CanReadRaw, group.Channels[Frame].UnsupportedReason);
            }
        }

        [Fact]
        public void UnsupportedConversionOnlyBlocksPhysical()
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            int cc = (int)M3Wire.U64(bytes, M3Wire.Link(M5DWire.Cn(bytes, Angle), 4));
            bytes[M3Wire.Payload(bytes, cc)] = 200; // unknown/unsupported kind
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannel angle = Group(reader).Channels[Angle];
                Assert.True(angle.CanReadRaw);
                Assert.False(angle.CanReadPhysical);
                Assert.Equal(new long[] { -720, -360, 0, 360, 32767, -32768 }, Column(reader, Angle).Select(v => v.SignedValue));
            }
        }

        [Fact]
        public void SyncChannelIsNeverAnMlsdSizeChannel()
        {
            byte[] bytes = FixtureCorpus.Read("m5p-mlsd410"); // 1 len (u8 size) -> 2 frame (MLSD bytes)
            byte[] payload = new byte[40];
            M3Wire.Put(payload, 0, 1, 2); // embedded, empty
            int at = M5Wire.Add(ref bytes, "AT", new ulong[4], payload);
            Field(bytes, 1, 0, 4, 1);
            Field(bytes, 1, 1, 4, 1);
            DataLink(bytes, 1, (ulong)at);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = Group(reader);
                Assert.True(group.Channels[1].CanReadRaw, group.Channels[1].UnsupportedReason); // a readable index sync channel
                Assert.Equal(MetadataReader.MaxLengthKindReason, group.Channels[2].UnsupportedReason);
            }
        }

        [Fact]
        public void MetadataResolvesOnlyTheAttachmentHeaderAndReadsNoRecords()
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            int root = M5DWire.Root(bytes);
            int at = FirstAttachment(bytes);
            int payload = M3Wire.Payload(bytes, at); // header + 4 links precede the AT data section
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                Group(reader);
                Assert.DoesNotContain(source.ReadRanges, r => r.Item1 >= root + 24);
                Assert.DoesNotContain(source.ReadRanges, r => r.Item1 >= payload && r.Item1 < payload + 40);
                Assert.Contains(source.ReadRanges, r => r.Item1 == at); // the header itself is resolved
                Assert.Equal(6, Column(reader, Frame).Count);
                Assert.Contains(source.ReadRanges, r => r.Item1 >= root + 24 && r.Item1 < root + 24 + 6 * RecordBytes);
            }
        }
    }
}
