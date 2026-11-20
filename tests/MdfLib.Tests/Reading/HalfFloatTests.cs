// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MdfLib.Formats.Mdf4;
using MdfLib.IO;
using MdfLib.Tests.TestDoubles;
using MdfLib.TestSupport;
using Xunit;

namespace MdfLib.Tests.Reading
{
    // a byte-aligned 16-bit IEEE field is binary16 widened exactly to double. Literal
    // mutations of frozen files (m5w corpus, m5q-sync410), never the writer; expected values from an independent formula.
    public sealed class HalfFloatTests
    {
        private const string Reason = "Only byte-aligned IEEE 16/32/64-bit floating-point storage is supported.";
        private const string File410 = "m5w-half410"; // 1 h (LE half @8), 6 n (u16 @31), 3 harr (LE half[4] @12, stride 2)

        // IEEE 754 binary16 by its definition, with Math.Pow scaling (not the library's bit assembly).
        private static double Definition(int bits)
        {
            double sign = (bits & 0x8000) != 0 ? -1.0 : 1.0;
            int exponent = bits >> 10 & 31, fraction = bits & 1023;
            if (exponent == 31) { return fraction == 0 ? sign * double.PositiveInfinity : double.NaN; }
            if (exponent == 0) { return sign * fraction * Math.Pow(2, -24); }
            return sign * (1024 + fraction) * Math.Pow(2, exponent - 25);
        }

        [Fact]
        public void EveryBinary16PatternWidensExactlyKeepingSignAndNaNPayload()
        {
            for (int bits = 0; bits <= ushort.MaxValue; bits++)
            {
                double actual = EndianBitConverter.HalfToDouble((ushort)bits);
                long actualBits = BitConverter.DoubleToInt64Bits(actual);
                Assert.Equal((bits & 0x8000) != 0, actualBits < 0);
                double expected = Definition(bits);
                if (double.IsNaN(expected))
                {
                    Assert.True(double.IsNaN(actual), bits.ToString("X4"));
                    Assert.Equal(bits & 1023, (int)(actualBits >> 42 & 1023));
                    continue;
                }
                Assert.Equal(BitConverter.DoubleToInt64Bits(expected), actualBits);
            }
        }

        [Theory]
        [InlineData(0x3C, 0x00, 1.0)]
        [InlineData(0xC0, 0x00, -2.0)]
        [InlineData(0x7B, 0xFF, 65504.0)]
        [InlineData(0x00, 0x01, 5.9604644775390625E-08)]
        [InlineData(0x35, 0x55, 0.333251953125)]
        public void ByteOrderSelectsTheHighByte(int high, int low, double expected)
        {
            byte[] buffer = { 0xEE, (byte)high, (byte)low, (byte)high, 0xEE };
            Assert.Equal(expected, EndianBitConverter.ReadHalf(buffer, 1, ByteOrder.BigEndian));
            Assert.Equal(expected, EndianBitConverter.ReadHalf(buffer, 2, ByteOrder.LittleEndian));
            Assert.Throws<ArgumentOutOfRangeException>(() => EndianBitConverter.ReadHalf(buffer, 4, ByteOrder.LittleEndian));
        }

        [Theory]
        [InlineData(4)]
        [InlineData(5)]
        public void HalfScalarIsTheBinary16ValueOfItsTwoBytes(int dataType)
        {
            // Read the same two bytes as u16 (data type 0/1, same order) and as half: the half is Definition(u16).
            byte[] bytes = FixtureCorpus.Read(File410);
            int cn = M3Wire.Payload(bytes, M5DWire.Cn(bytes, 1));
            bytes[cn + 2] = (byte)(dataType - 4);
            ulong[] patterns = Column(bytes, 1).Select(v => v.UnsignedValue).ToArray();
            bytes[cn + 2] = (byte)dataType;
            List<MdfSampleValue> halves = Column(bytes, 1);
            Assert.Equal(6, halves.Count);
            for (int row = 0; row < halves.Count; row++)
            {
                Assert.Equal(MdfValueKind.FloatingPoint, halves[row].Kind);
                double expected = Definition((int)patterns[row]);
                if (double.IsNaN(expected)) { Assert.True(double.IsNaN(halves[row].FloatingPointValue)); }
                else { Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(halves[row].FloatingPointValue)); }
            }
        }

        [Theory]
        [InlineData(16, 0, null)]
        [InlineData(16, 1, Reason)]
        [InlineData(16, 7, Reason)]
        [InlineData(8, 0, Reason)]
        [InlineData(24, 0, Reason)]
        [InlineData(48, 0, Reason)]
        [InlineData(32, 0, null)]
        [InlineData(64, 0, null)]
        public void OnlyByteAligned16And32And64BitIeeeFieldsAreReadable(int bits, int bitOffset, string reason)
        {
            // Channel 6 (u16 @31) is followed by 8 data bytes; the record has 39, so a 64-bit field at 31 still fits.
            byte[] bytes = FixtureCorpus.Read(File410);
            int cn = M3Wire.Payload(bytes, M5DWire.Cn(bytes, 6));
            bytes[cn + 2] = 4;
            bytes[cn + 3] = (byte)bitOffset;
            M3Wire.Put(bytes, cn + 8, (ulong)bits, 4);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                MdfChannel channel = group.Channels[6];
                Assert.Equal(MdfValueKind.FloatingPoint, channel.ValueKind);
                Assert.Equal(reason, channel.UnsupportedReason);
                Assert.Equal(reason == null, channel.CanReadRaw);
                if (reason != null) { Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 6 })); }
                else { Assert.Equal(6, reader.ReadSamples(group, new[] { 6 }).Sum(b => b.SampleCount)); }
                Assert.Equal(6, reader.ReadSamples(group, new[] { 1 }).Sum(b => b.SampleCount));
            }
        }

        [Fact]
        public void TheReasonConstantIsTheContractText()
        {
            Assert.Equal(Reason, MetadataReader.FloatReason);
        }

        [Theory]
        [InlineData(37U, true)]   // 37 + 2 = 39 data bytes: ends exactly at the data area end
        [InlineData(38U, false)]
        public void HalfFieldMustFitTheDataArea(uint byteOffset, bool fits)
        {
            byte[] bytes = FixtureCorpus.Read(File410);
            M3Wire.Put(bytes, M3Wire.Payload(bytes, M5DWire.Cn(bytes, 1)) + 4, byteOffset, 4);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                if (fits)
                {
                    MdfChannel channel = reader.ReadMetadata()[0].ChannelGroups[0].Channels[1];
                    Assert.True(channel.CanReadRaw, channel.UnsupportedReason);
                    return;
                }
                MdfFormatException error = Assert.Throws<MdfFormatException>(() => reader.ReadMetadata());
                Assert.Equal("MDF013", error.DiagnosticCode);
            }
        }

        [Theory]
        [InlineData(1U, "Array byte stride must be at least the element's byte width.")]
        [InlineData(2U, null)]
        [InlineData(3U, null)]
        public void HalfArrayStrideMustCoverTheTwoByteField(uint stride, string reason)
        {
            byte[] bytes = FixtureCorpus.Read(File410);
            M3Wire.Put(bytes, M3Wire.Payload(bytes, M5DWire.Ca(bytes, 3)) + 8, stride, 4);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannel channel = reader.ReadMetadata()[0].ChannelGroups[0].Channels[3];
                Assert.Equal(reason, channel.UnsupportedReason);
                Assert.Equal(4UL, channel.Array.ElementCount);
            }
        }

        [Fact]
        public void PaddedHalfArrayElementIsTheFieldAtItsStride()
        {
            // harr stride 3: element s is the half at 12 + 3s; compare with the u16 read of the same scalar positions.
            byte[] bytes = FixtureCorpus.Read(File410);
            M3Wire.Put(bytes, M3Wire.Payload(bytes, M5DWire.Ca(bytes, 3)) + 8, 3, 4);
            int scalar = M3Wire.Payload(bytes, M5DWire.Cn(bytes, 6));
            for (int element = 0; element < 4; element++)
            {
                byte[] probe = (byte[])bytes.Clone();
                M3Wire.Put(probe, scalar + 4, (ulong)(12 + 3 * element), 4);
                ulong[] patterns = Column(probe, 6).Select(v => v.UnsignedValue).ToArray();
                MdfSampleValue[][] halves = Elements(bytes, 3);
                for (int row = 0; row < 6; row++)
                {
                    double expected = Definition((int)patterns[row]);
                    double actual = halves[row][element].FloatingPointValue;
                    if (double.IsNaN(expected)) { Assert.True(double.IsNaN(actual)); }
                    else { Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(actual)); }
                }
            }
        }

        [Fact]
        public void Binary16MasterTimestampsAreItsExactValues()
        {
            byte[] bytes = FixtureCorpus.Read("m5w-half420");
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.True(group.Channels[0].IsMaster);
                Assert.Equal(16U, group.Channels[0].BitCount);
                Assert.Equal(new[] { 0.0, 0.125, 0.25, 0.375, 0.5, 0.625 },
                    Column(bytes, 0).Select(v => v.FloatingPointValue));
            }
        }

        [Fact]
        public void Binary16SynchronizationChannelReadsWithItsConversion()
        {
            // m5q-sync410 channel 2: BE i16 angle sync (linear 0.5x - 10). As BE half the same bytes are Definition(u16).
            byte[] bytes = FixtureCorpus.Read("m5q-sync410");
            int cn = M3Wire.Payload(bytes, M5DWire.Cn(bytes, 2));
            ushort[] patterns = Column(bytes, 2).Select(v => unchecked((ushort)v.SignedValue)).ToArray();
            bytes[cn + 2] = 5;
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannel channel = reader.ReadMetadata()[0].ChannelGroups[0].Channels[2];
                Assert.True(channel.CanReadRaw, channel.UnsupportedReason);
                Assert.Equal(MdfValueKind.FloatingPoint, channel.ValueKind);
            }
            List<MdfSampleValue> raw = Column(bytes, 2);
            List<MdfSampleValue> physical = Column(bytes, 2, MdfValueRepresentation.Physical);
            Assert.Equal(patterns.Length, raw.Count);
            for (int row = 0; row < raw.Count; row++)
            {
                double expected = Definition(patterns[row]);
                Assert.Equal(expected, raw[row].FloatingPointValue);
                Assert.Equal(0.5 * expected - 10, physical[row].FloatingPointValue);
            }
        }

        private static List<MdfSampleValue> Column(byte[] bytes, int index, MdfValueRepresentation representation = MdfValueRepresentation.Raw)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.True(group.Channels[index].CanReadRaw, group.Channels[index].UnsupportedReason);
                return reader.ReadSamples(group, new[] { index }, representation: representation)
                    .SelectMany(b => Enumerable.Range(0, b.SampleCount).Select(r => b.GetValue(r, 0))).ToList();
            }
        }

        private static MdfSampleValue[][] Elements(byte[] bytes, int index)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.True(group.Channels[index].CanReadRaw, group.Channels[index].UnsupportedReason);
                ulong count = group.Channels[index].Array.ElementCount;
                List<MdfSampleValue[]> rows = new List<MdfSampleValue[]>();
                foreach (MdfSampleBatch batch in reader.ReadSamples(group, new[] { index }, batchSize: 4))
                for (int row = 0; row < batch.SampleCount; row++)
                {
                    MdfSampleValue[] values = new MdfSampleValue[count];
                    for (ulong element = 0; element < count; element++) { values[element] = batch.GetValue(row, 0, element); }
                    rows.Add(values);
                }
                return rows.ToArray();
            }
        }
    }
}
