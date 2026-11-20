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
    // literal mutations of the frozen stdlib m5s files, never the library's writer.
    public sealed class MimeChannelTests
    {
        private const string Sorted = "m5s-mime410"; // 0 t, 1 png (fixed 11), 2 clip (VLSD SD 12), 3 note (MLSD 11), 4 n, 5 conv, 6 date
        private const int Png = 1, Clip = 2, Note = 3, Size = 4, Conv = 5, Date = 6;
        private static readonly string[] PngBytes = { "89504e470d0a", "000000000000", "616263646566", "fffefdfcfbfa", "010203040506" };
        private static readonly string[] ClipBytes = { "52494646", "", "000102030405060708", "57", "415645666d7420" };
        private static readonly string[] NoteBytes = { "6869", "", "3132333435363738", "00", "74657874" };

        private static int Cn(byte[] bytes, int index) { return M5DWire.Cn(bytes, index); }
        private static int Field(byte[] bytes, int index) { return M3Wire.Payload(bytes, Cn(bytes, index)); }
        private static MdfChannelGroup Group(MdfReader reader) { return reader.ReadMetadata()[0].ChannelGroups[0]; }
        private static string Hex(MdfSampleValue value) { return BitConverter.ToString(value.GetByteArrayValue()).Replace("-", "").ToLowerInvariant(); }

        private static List<MdfSampleValue> Column(MdfReader reader, int index, MdfValueRepresentation representation = MdfValueRepresentation.Raw)
        {
            return reader.ReadSamples(Group(reader), new[] { index }, representation: representation)
                .SelectMany(b => Enumerable.Range(0, b.SampleCount).Select(r => b.GetValue(r, 0))).ToList();
        }

        private static MdfReader Open(byte[] bytes) { return MdfReader.Open(new MemoryStream(bytes, false)); }

        [Fact]
        public void MimeChannelsAreByteArraysInFixedVlsdAndMlsdStorageWithTheirUnits()
        {
            using (MdfReader reader = Open(FixtureCorpus.Read(Sorted)))
            {
                MdfChannelGroup group = Group(reader);
                foreach (int index in new[] { Png, Clip, Note })
                {
                    Assert.Equal(MdfValueKind.ByteArray, group.Channels[index].ValueKind);
                    Assert.True(group.Channels[index].CanReadRaw, group.Channels[index].UnsupportedReason);
                    Assert.True(group.Channels[index].CanReadPhysical);
                }
                Assert.Equal(new[] { "image/png", "audio/wav", "text/plain" }, new[] { Png, Clip, Note }.Select(i => group.Channels[i].Unit));
                foreach (MdfValueRepresentation representation in new[] { MdfValueRepresentation.Raw, MdfValueRepresentation.Physical })
                {
                    List<MdfSampleValue> png = Column(reader, Png, representation);
                    Assert.Equal(PngBytes, png.Select(Hex)); // the invalid row keeps its stored field bytes
                    Assert.Equal(new[] { true, true, true, false, true }, png.Select(v => v.IsValid));
                    Assert.All(png, v => { Assert.Equal(MdfValueKind.ByteArray, v.Kind); Assert.Throws<InvalidOperationException>(() => v.TextValue); });
                    Assert.Equal(ClipBytes, Column(reader, Clip, representation).Select(Hex));
                    Assert.Equal(NoteBytes, Column(reader, Note, representation).Select(Hex));
                }
            }
        }

        [Theory]
        [InlineData(Png)]
        [InlineData(Clip)]
        [InlineData(Note)]
        public void MimeSampleMimeStreamAndByteArrayTypesReadIdentically(int index)
        {
            List<string>[] results = new List<string>[3];
            for (int type = 10; type <= 12; type++)
            {
                byte[] bytes = FixtureCorpus.Read(Sorted);
                bytes[Field(bytes, index) + 2] = (byte)type;
                using (MdfReader reader = Open(bytes))
                {
                    Assert.Equal(MdfValueKind.ByteArray, Group(reader).Channels[index].ValueKind);
                    results[type - 10] = Column(reader, index).Select(v => Hex(v) + (v.IsValid ? "+" : "-")).ToList();
                }
            }
            Assert.Equal(results[0], results[1]);
            Assert.Equal(results[0], results[2]);
        }

        [Theory]
        [InlineData(15)]
        [InlineData(16)]
        public void ComplexRetypesOfA48BitFieldAreRefusedByWidth(int type)
        {
            // complex (15/16) is two IEEE parts of 16/32/64 bits; a 48-bit field has no such split.
            byte[] bytes = FixtureCorpus.Read(Sorted);
            bytes[Field(bytes, Png) + 2] = (byte)type;
            using (MdfReader reader = Open(bytes))
            {
                MdfChannel channel = Group(reader).Channels[Png];
                Assert.Equal(MdfValueKind.Complex, channel.ValueKind);
                Assert.Equal("Only byte-aligned complex storage of two IEEE 16/32/64-bit parts is supported.", channel.UnsupportedReason);
                Assert.Throws<NotSupportedException>(() => Column(reader, Png));
                Assert.Equal(5, Column(reader, Clip).Count);
            }
        }

        // supersedes the frozen CANopen refusals: a 6-byte fixed field retyped as
        // CANopen time reads its bytes; as CANopen date (needs 7 bytes) it is refused with the reason. The frozen
        // 7-byte `date` channel reads its frozen bytes.
        [Theory]
        [InlineData(13, false)]
        [InlineData(14, true)]
        public void CanOpenTypesAreExactWidthFixedByteArrays(int type, bool readable)
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            bytes[Field(bytes, Png) + 2] = (byte)type;
            using (MdfReader reader = Open(bytes))
            {
                MdfChannel channel = Group(reader).Channels[Png];
                Assert.Equal(MdfValueKind.ByteArray, channel.ValueKind);
                if (readable)
                {
                    Assert.Null(channel.UnsupportedReason);
                    Assert.Equal(PngBytes, Column(reader, Png).Select(Hex));
                    Assert.Equal(PngBytes, Column(reader, Png, MdfValueRepresentation.Physical).Select(Hex));
                }
                else
                {
                    Assert.Equal(MetadataReader.CanOpenReason, channel.UnsupportedReason);
                    Assert.Throws<NotSupportedException>(() => Column(reader, Png));
                }
                MdfChannel date = Group(reader).Channels[Date];
                Assert.Equal(MdfValueKind.ByteArray, date.ValueKind);
                Assert.Null(date.UnsupportedReason);
                Assert.Equal(Enumerable.Repeat("01020304050607", 5), Column(reader, Date).Select(Hex));
                Assert.Equal(5, Column(reader, Clip).Count);
            }
        }

        [Fact]
        public void NonIdentityConversionOnAMimeStreamIsRefusedAndNeighboursRead()
        {
            using (MdfReader reader = Open(FixtureCorpus.Read(Sorted)))
            {
                MdfChannel conv = Group(reader).Channels[Conv];
                Assert.Equal(MdfValueKind.ByteArray, conv.ValueKind);
                Assert.Equal("Text and byte-array channels support only identity conversion.", conv.UnsupportedReason);
                Assert.Equal(MdfConversionKind.Linear, conv.Conversion.Kind);
                Assert.Throws<NotSupportedException>(() => Column(reader, Conv));
                Assert.Equal(new ulong[] { 2, 0, 8, 1, 4 }, Column(reader, Size).Select(v => v.UnsignedValue));
            }
        }

        [Theory]
        [InlineData(2, 11, "Only fixed-length and VLSD text/byte channels")]             // stored master role
        [InlineData(4, 11, "Synchronization channels require a numeric data type.")]      // sync role
        [InlineData(6, 12, "Virtual channels require an unsigned integer data type.")]    // virtual data role
        public void OtherRolesKeepTheirByteArrayRefusals(int role, int type, string reason)
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            int field = Field(bytes, Png);
            bytes[field] = (byte)role;
            bytes[field + 2] = (byte)type;
            if (role == 4) { bytes[field + 1] = 1; }
            using (MdfReader reader = Open(bytes))
            {
                Assert.StartsWith(reason, Group(reader).Channels[Png].UnsupportedReason);
                Assert.Equal(5, Column(reader, Clip).Count);
            }
        }

        [Theory]
        [InlineData(11)]
        [InlineData(12)]
        public void MimeCompositionIsNotAnAggregateStructureParent(int type)
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            bytes[Field(bytes, Png) + 2] = (byte)type;
            int child = M5Wire.Add(ref bytes, "CN", new ulong[8], new byte[72]);
            M3Wire.Put(bytes, M3Wire.Link(Cn(bytes, Png), 1), (ulong)child);
            using (MdfReader reader = Open(bytes))
            {
                MdfChannel png = Group(reader).Channels[Png];
                Assert.Equal("Structure/unknown channel composition is not supported.", png.UnsupportedReason);
                Assert.Empty(png.Components);
                Assert.Equal(7, Group(reader).Channels.Count);
            }
        }

        [Theory]
        [InlineData(11)]
        [InlineData(12)]
        public void MimeCnTemplateArraysReadEveryFixedElement(int type)
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            bytes[Field(bytes, Png) + 2] = (byte)type;
            byte[] payload = new byte[24];
            M3Wire.Put(payload, 2, 1, 2);  // one dimension
            M3Wire.Put(payload, 8, 6, 4);  // byte stride = the 6-byte field
            M3Wire.Put(payload, 16, 2);    // two elements
            int ca = M5Wire.Add(ref bytes, "CA", new ulong[1], payload);
            M3Wire.Put(bytes, M3Wire.Link(Cn(bytes, Png), 1), (ulong)ca);
            using (MdfReader reader = Open(bytes))
            {
                MdfChannel png = Group(reader).Channels[Png];
                Assert.Equal(MdfValueKind.ByteArray, png.ValueKind);
                Assert.Null(png.UnsupportedReason); // fixed-length byte-array arrays.
                Assert.Equal(2UL, png.Array.ElementCount);
                MdfSampleBatch batch = reader.ReadSamples(Group(reader), new[] { Png }).Single();
                Assert.Equal(PngBytes, Enumerable.Range(0, 5).Select(r => Hex(batch.GetValue(r, 0, 0))));
                Assert.Equal(6, batch.GetValue(0, 0, 1).GetByteArrayValue().Length);
                Assert.Equal(ClipBytes, Column(reader, Clip).Select(Hex));
                Assert.Equal(NoteBytes, Column(reader, Note).Select(Hex));
            }
        }

        [Fact]
        public void MimeStructureComponentsKeepTheAncestorInvalidationRefusal()
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            // A fixed byte-array parent (type 10, the aggregate) whose single component is a MIME sample field.
            byte[] payload = new byte[72];
            payload[2] = 11;
            M3Wire.Put(payload, 4, 8, 4);  // byte offset 8 inside the png window
            M3Wire.Put(payload, 8, 16, 4); // 2 bytes
            int child = M5Wire.Add(ref bytes, "CN", new ulong[8], payload);
            bytes[Field(bytes, Png) + 2] = 10;
            M3Wire.Put(bytes, M3Wire.Link(Cn(bytes, Png), 1), (ulong)child);
            using (MdfReader reader = Open(bytes))
            {
                MdfChannelGroup group = Group(reader);
                MdfChannel component = Assert.Single(group.Channels[Png].Components);
                Assert.Equal(MdfValueKind.ByteArray, component.ValueKind);
                // fixed byte-array leaves are readable, but this parent declares invalidation the leaf cannot inherit.
                Assert.Equal("An ancestor structure declares invalidity, layout flags or channel-specific data whose inheritance is not supported.",
                    component.UnsupportedReason);
                Assert.Equal(PngBytes, Column(reader, Png).Select(Hex));
            }
        }

        [Fact]
        public void EveryVlsdSignalDataRootCarriesMimeValues()
        {
            using (MdfReader reader = Open(FixtureCorpus.Read("m5s-mime-dl411"))) // DL of SD + DZ(SD), invalid garbage row
            {
                List<MdfSampleValue> doc = Column(reader, 1);
                Assert.Equal(new[] { "255044462d312e37", "00ff00ff00", "", "2525454f46" }, doc.Select(Hex));
                Assert.Equal(new[] { true, true, false, true }, doc.Select(v => v.IsValid));
                Assert.Equal(new[] { "102030", "000000", "abcdef", "78797a" }, Column(reader, 2).Select(Hex));
                Assert.Equal("application/pdf", Group(reader).Channels[1].Unit);
            }
            using (MdfReader reader = Open(FixtureCorpus.Read("m5s-mime-vlsdcg420"))) // VLSD channel group
            {
                Assert.Equal(new[] { "ffd8ff", "00", "", "4a4649460001" }, Column(reader, 1).Select(Hex));
                Assert.Equal(new[] { "6f6b", "6e6f", "0000", "5a5a" }, Column(reader, 2).Select(Hex));
            }
            using (MdfReader reader = Open(FixtureCorpus.Read("m5s-mime-ld420"))) // 4.20 LD
            {
                Assert.Equal(new[] { "010203", "040506070809", "0a" }, Column(reader, 1).Select(Hex));
                Assert.Null(Group(reader).Channels[2].UnsupportedReason); // 64-bit complex `cplx` reads two binary32 zeros.
                Assert.All(Column(reader, 2), v => Assert.Equal(0.0, v.RealPart + v.ImaginaryPart));
                Assert.Null(Group(reader).Channels[3].UnsupportedReason); // CANopen time `clock` reads its 6 bytes.
                Assert.Equal(Enumerable.Repeat("000000000000", 3), Column(reader, 3).Select(Hex));
            }
        }

        [Fact]
        public void MlsdMimeSizeAboveTheMaximumIsMalformedAtRead()
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            bytes[M3Wire.Payload(bytes, M5DWire.Root(bytes)) + 30] = 9; // row 0 size 9 > 8-byte maximum
            using (MdfReader reader = Open(bytes))
            {
                Assert.Equal("MDF013", Assert.Throws<MdfFormatException>(() => Column(reader, Note)).DiagnosticCode);
                Assert.Equal(PngBytes, Column(reader, Png).Select(Hex));
            }
        }
    }
}
