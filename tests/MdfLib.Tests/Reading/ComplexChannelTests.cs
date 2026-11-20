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
    // literal mutations of the frozen stdlib m5aa-complex420 file, never the writer.
    public sealed class ComplexChannelTests
    {
        private const string File = "m5aa-complex420"; // 0 t, 1 c64 @8, 2 c128 @16, 3 c128be @32, 4 carr @48 [3], 5 cinv @72,
        // 6 cconv @80 (linear), 7 n @88, 8 codd @90 (bit offset 4); 99 data + 1 invalidation byte, 6 rows.
        private const int C64 = 1, C128 = 2, C128Be = 3, Carr = 4, Cinv = 5, Cconv = 6, Counter = 7, Codd = 8, RecordBytes = 100;
        private const string ComplexReason = "Only byte-aligned complex storage of two IEEE 16/32/64-bit parts is supported.";
        private const string RoleReason = "Complex values are supported only for stored value channels.";

        private static int Field(byte[] bytes, int index) { return M3Wire.Payload(bytes, M5DWire.Cn(bytes, index)); }
        private static int Record(byte[] bytes, int row) { return M3Wire.Payload(bytes, M5DWire.Root(bytes)) + row * RecordBytes; }
        private static MdfReader Open(byte[] bytes) { return MdfReader.Open(new MemoryStream(bytes, false)); }
        private static MdfChannel Channel(byte[] bytes, int index) { using (MdfReader reader = Open(bytes)) { return reader.ReadMetadata()[0].ChannelGroups[0].Channels[index]; } }

        private static List<MdfSampleValue> Column(byte[] bytes, int index, MdfValueRepresentation representation = MdfValueRepresentation.Raw)
        {
            using (MdfReader reader = Open(bytes))
            {
                return reader.ReadSamples(reader.ReadMetadata()[0].ChannelGroups[0], new[] { index }, representation: representation)
                    .SelectMany(b => Enumerable.Range(0, b.SampleCount).Select(r => b.GetValue(r, 0))).ToList();
            }
        }

        private static void PutSingle(byte[] bytes, int offset, float value, bool big)
        {
            byte[] raw = BitConverter.GetBytes(value);
            if (big == BitConverter.IsLittleEndian) { Array.Reverse(raw); }
            Buffer.BlockCopy(raw, 0, bytes, offset, 4);
        }

        private static void PutDouble(byte[] bytes, int offset, double value, bool big)
        {
            byte[] raw = BitConverter.GetBytes(value);
            if (big == BitConverter.IsLittleEndian) { Array.Reverse(raw); }
            Buffer.BlockCopy(raw, 0, bytes, offset, 8);
        }

        [Fact]
        public void PartsAreRealThenImaginaryInTheChannelByteOrder()
        {
            byte[] bytes = FixtureCorpus.Read(File);
            int record = Record(bytes, 0);
            PutSingle(bytes, record + 8, 1.25f, false);
            PutSingle(bytes, record + 12, -3.5f, false);
            PutDouble(bytes, record + 16, Math.PI, false);
            PutDouble(bytes, record + 24, -1e-300, false);
            PutDouble(bytes, record + 32, 6.5, true);
            PutDouble(bytes, record + 40, double.NegativeInfinity, true);
            foreach (MdfValueRepresentation representation in new[] { MdfValueRepresentation.Raw, MdfValueRepresentation.Physical })
            {
                MdfSampleValue c64 = Column(bytes, C64, representation)[0];
                MdfSampleValue c128 = Column(bytes, C128, representation)[0];
                MdfSampleValue be = Column(bytes, C128Be, representation)[0];
                Assert.Equal(MdfValueKind.Complex, c64.Kind);
                Assert.Equal(1.25, c64.RealPart);
                Assert.Equal(-3.5, c64.ImaginaryPart);
                Assert.Equal(Math.PI, c128.RealPart);
                Assert.Equal(-1e-300, c128.ImaginaryPart);
                Assert.Equal(6.5, be.RealPart);
                Assert.Equal(double.NegativeInfinity, be.ImaginaryPart);
            }
        }

        [Fact]
        public void HalfPartsDecodeExactlyFromA32BitField()
        {
            byte[] bytes = FixtureCorpus.Read(File);
            M3Wire.Put(bytes, Field(bytes, C64) + 8, 32, 4);
            int record = Record(bytes, 1);
            M3Wire.Put(bytes, record + 8, 0x3C00, 2);  // 1.0
            M3Wire.Put(bytes, record + 10, 0x8001, 2); // -2^-24 (subnormal)
            MdfChannel channel = Channel(bytes, C64);
            Assert.Null(channel.UnsupportedReason);
            Assert.Equal(32U, channel.BitCount);
            MdfSampleValue value = Column(bytes, C64)[1];
            Assert.Equal(1.0, value.RealPart);
            Assert.Equal(-Math.Pow(2, -24), value.ImaginaryPart);
        }

        [Theory]
        [InlineData(0U, 0)]
        [InlineData(48U, 0)]
        [InlineData(96U, 0)]
        [InlineData(127U, 0)]
        [InlineData(64U, 1)]
        public void OtherWidthsAndUnalignedFieldsAreRefused(uint bits, int bitOffset)
        {
            byte[] bytes = FixtureCorpus.Read(File);
            M3Wire.Put(bytes, Field(bytes, C128) + 8, bits, 4);
            bytes[Field(bytes, C128) + 3] = (byte)bitOffset;
            if (bits == 0)
            {
                using (MdfReader reader = Open(bytes))
                {
                    Assert.Equal("MDF013", Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
                }
                return;
            }
            MdfChannel channel = Channel(bytes, C128);
            Assert.Equal(MdfValueKind.Complex, channel.ValueKind);
            Assert.Equal(ComplexReason, channel.UnsupportedReason);
            Assert.False(channel.CanReadRaw);
            Assert.Equal(ComplexReason, Channel(FixtureCorpus.Read(File), Codd).UnsupportedReason);
            Assert.Equal(6, Column(bytes, Counter).Count); // Neighbours stay readable.
        }

        [Theory]
        [InlineData(4, RoleReason)] // synchronization
        [InlineData(5, "MLSD")]
        [InlineData(6, "Virtual channels require an unsigned integer data type.")]
        public void NonStoredRolesAreRefused(byte role, string reason)
        {
            byte[] bytes = FixtureCorpus.Read(File);
            bytes[Field(bytes, C64)] = role;
            bytes[Field(bytes, C64) + 1] = 1;
            MdfChannel channel = Channel(bytes, C64);
            Assert.Equal(MdfValueKind.Complex, channel.ValueKind);
            Assert.NotNull(channel.UnsupportedReason);
            if (reason != "MLSD") { Assert.Equal(reason, channel.UnsupportedReason); }
        }

        [Fact]
        public void ConvertedComplexChannelsReadRawButRefusePhysical()
        {
            byte[] bytes = FixtureCorpus.Read(File);
            MdfChannel channel = Channel(bytes, Cconv);
            Assert.True(channel.CanReadRaw);
            Assert.False(channel.CanReadPhysical);
            Assert.Equal(MdfConversionKind.Linear, channel.Conversion.Kind);
            Assert.All(Column(bytes, Cconv), v => Assert.Equal(MdfValueKind.Complex, v.Kind));
            using (MdfReader reader = Open(bytes))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { Cconv, C64 }, representation: MdfValueRepresentation.Physical).ToList());
            }
        }

        [Fact]
        public void InvalidationAndArrayElementsUseTheWholeComplexField()
        {
            byte[] bytes = FixtureCorpus.Read(File);
            Assert.Equal(new[] { true, true, false, true, true, true }, Column(bytes, Cinv).Select(v => v.IsValid));
            int record = Record(bytes, 3);
            for (int element = 0; element < 3; element++)
            {
                PutSingle(bytes, record + 48 + 8 * element, element + 0.5f, false);
                PutSingle(bytes, record + 52 + 8 * element, -element, false);
            }
            using (MdfReader reader = Open(bytes))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(3UL, group.Channels[Carr].Array.ElementCount);
                MdfSampleBatch batch = reader.ReadSamples(group, new[] { Carr }, 3, 1).Single();
                for (int element = 0; element < 3; element++)
                {
                    MdfSampleValue value = batch.GetValue(0, 0, (ulong)element);
                    Assert.Equal(element + 0.5, value.RealPart);
                    Assert.Equal(-element, value.ImaginaryPart);
                }
            }
        }

        [Fact]
        public void RetypingToBigEndianReadsTheSameBytesAsBigEndianParts()
        {
            // Retyping c128 to 16 reads the same bytes as two big-endian doubles.
            byte[] bytes = FixtureCorpus.Read(File);
            int record = Record(bytes, 0);
            PutDouble(bytes, record + 16, 2.5, true);
            PutDouble(bytes, record + 24, -0.0, true);
            bytes[Field(bytes, C128) + 2] = 16;
            MdfSampleValue value = Column(bytes, C128)[0];
            Assert.Equal(2.5, value.RealPart);
            Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(value.ImaginaryPart));
        }

        [Fact]
        public void ComplexValuesHaveTheirOwnGettersAndFactory()
        {
            MdfSampleValue value = MdfSampleValue.FromComplex(1.5, double.NaN, false);
            Assert.Equal(MdfValueKind.Complex, value.Kind);
            Assert.False(value.IsValid);
            Assert.Equal(1.5, value.RealPart);
            Assert.True(double.IsNaN(value.ImaginaryPart));
            Assert.Throws<InvalidOperationException>(() => value.FloatingPointValue);
            Assert.Throws<InvalidOperationException>(() => value.UnsignedValue);
            Assert.Throws<InvalidOperationException>(() => value.TextValue);
            Assert.Throws<InvalidOperationException>(() => value.GetByteArrayValue());
            Assert.Throws<InvalidOperationException>(() => MdfSampleValue.FromFloatingPoint(1).RealPart);
            Assert.Throws<InvalidOperationException>(() => MdfSampleValue.FromSigned(1).ImaginaryPart);
            Assert.Throws<InvalidOperationException>(() => default(MdfSampleValue).RealPart);
            Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(MdfSampleValue.FromComplex(-0.0, -0.0).ImaginaryPart));
            Assert.True(MdfSampleValue.FromComplex(0, 0).IsValid);
        }
    }
}
