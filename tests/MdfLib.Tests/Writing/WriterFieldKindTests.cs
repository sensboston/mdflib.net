// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace MdfLib.Tests.Writing
{
    // fixed-length text/byte-array channels (MDF 3.30/4.x) and complex channels (MDF 4) beyond the numeric writer.
    public sealed class WriterFieldKindTests
    {
        private static MdfChannelDefinition Def(string name, MdfValueKind kind, int bits, bool big = false)
        { return new MdfChannelDefinition { Name = name, ValueKind = kind, BitCount = bits, IsBigEndian = big }; }

        private static byte[] Write(string version, IList<MdfChannelDefinition> channels, IEnumerable<MdfSampleValue[]> rows)
        {
            MemoryStream stream = new MemoryStream();
            using (MdfWriter writer = MdfWriter.Create(stream, true, new MdfWriterOptions { Version = version }))
            {
                foreach (MdfChannelDefinition channel in channels) { writer.AddChannel(channel); }
                writer.StartMeasurement();
                foreach (MdfSampleValue[] row in rows) { writer.SaveSample(row); }
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
            }
            return stream.ToArray();
        }

        private static MdfSampleBatch ReadAll(byte[] bytes, out MdfChannelGroup group)
        {
            MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false));
            group = Assert.Single(Assert.Single(reader.ReadMetadata()).ChannelGroups);
            return reader.ReadSamples(group, Enumerable.Range(0, group.Channels.Count).ToArray()).Single();
        }

        [Theory]
        [InlineData("3.30")]
        [InlineData("4.10")]
        [InlineData("4.20")]
        public void FixedTextAndByteArraysRoundTripExactly(string version)
        {
            bool mdf3 = version == "3.30";
            string[] texts = { "", "abc", mdf3 ? "caf\u00e9!!!" : "\u20ac\u00fc\u4e2d", mdf3 ? "\u00ff" : "x" };
            byte[][] blobs = { new byte[] { 0, 0, 0 }, new byte[] { 1, 2, 3 }, new byte[] { 255, 0, 128 }, new byte[] { 9, 9, 9 } };
            MdfChannelDefinition time = Def("t", MdfValueKind.FloatingPoint, 64);
            time.IsMaster = true;
            byte[] bytes = Write(version, new[] { time, Def("label", MdfValueKind.Text, 64), Def("blob", MdfValueKind.ByteArray, 24),
                Def("n", MdfValueKind.UnsignedInteger, 8) },
                Enumerable.Range(0, 4).Select(r => new[] { MdfSampleValue.FromFloatingPoint(r * 0.5), MdfSampleValue.FromText(texts[r]),
                    MdfSampleValue.FromByteArray(blobs[r]), MdfSampleValue.FromUnsigned((ulong)r) }));
            MdfChannelGroup group;
            MdfSampleBatch batch = ReadAll(bytes, out group);
            Assert.Equal(MdfValueKind.Text, group.Channels[1].ValueKind);
            Assert.Equal(MdfValueKind.ByteArray, group.Channels[2].ValueKind);
            Assert.Equal(64U, group.Channels[1].BitCount);
            Assert.Equal(mdf3 ? 20 : 21, (int)group.RecordByteCount); // 8 + 8 + 3 + 1 (+ 1 MDF 4 invalidation byte)
            for (int row = 0; row < 4; row++)
            {
                Assert.Equal(texts[row], batch.GetValue(row, 1).TextValue);
                byte[] stored = batch.GetValue(row, 1).GetByteArrayValue();
                Assert.Equal(8, stored.Length);
                Assert.All(stored.Skip(Array.IndexOf(stored, (byte)0) < 0 ? 8 : Array.IndexOf(stored, (byte)0)), b => Assert.Equal(0, b)); // NUL padding
                Assert.Equal(blobs[row], batch.GetValue(row, 2).GetByteArrayValue());
                Assert.Equal((ulong)row, batch.GetValue(row, 3).UnsignedValue);
            }
        }

        [Fact]
        public void TextFillingTheWholeFieldHasNoTerminator()
        {
            byte[] bytes = Write("4.20", new[] { Def("s", MdfValueKind.Text, 32) }, new[] { new[] { MdfSampleValue.FromText("\u00fc\u00fc") } });
            MdfChannelGroup group;
            MdfSampleValue value = ReadAll(bytes, out group).GetValue(0, 0);
            Assert.Equal("\u00fc\u00fc", value.TextValue);
            Assert.Equal(new byte[] { 0xC3, 0xBC, 0xC3, 0xBC }, value.GetByteArrayValue());
        }

        [Theory]
        [InlineData("4.10", false)]
        [InlineData("4.20", true)]
        public void ComplexChannelsRoundTripBothWidthsAndByteOrders(string version, bool big)
        {
            double[] parts = { 1.5, -0.0, double.PositiveInfinity, 3.25e-5, double.NaN, -2.0 };
            byte[] bytes = Write(version, new[] { Def("c64", MdfValueKind.Complex, 64, big), Def("c128", MdfValueKind.Complex, 128, big) },
                Enumerable.Range(0, 3).Select(r => new[]
                {
                    MdfSampleValue.FromComplex(parts[2 * r], parts[2 * r + 1], r != 1),
                    MdfSampleValue.FromComplex(parts[2 * r] * 1e300, parts[2 * r + 1], r != 1)
                }));
            MdfChannelGroup group;
            MdfSampleBatch batch = ReadAll(bytes, out group);
            Assert.All(group.Channels, c => Assert.Equal(MdfValueKind.Complex, c.ValueKind));
            Assert.Equal(new uint[] { 64, 128 }, group.Channels.Select(c => c.BitCount));
            for (int row = 0; row < 3; row++)
            {
                MdfSampleValue c64 = batch.GetValue(row, 0), c128 = batch.GetValue(row, 1);
                Assert.Equal(row != 1, c64.IsValid);
                Same((double)(float)parts[2 * row], c64.RealPart);
                Same((double)(float)parts[2 * row + 1], c64.ImaginaryPart);
                Same(parts[2 * row] * 1e300, c128.RealPart);
                Same(parts[2 * row + 1], c128.ImaginaryPart);
            }
        }

        private static void Same(double expected, double actual)
        {
            if (double.IsNaN(expected)) { Assert.True(double.IsNaN(actual)); return; }
            Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(actual));
        }

        [Fact]
        public void BigEndianComplexStoresEachPartBigEndian()
        {
            byte[] bytes = Write("4.20", new[] { Def("c", MdfValueKind.Complex, 64, true) }, new[] { new[] { MdfSampleValue.FromComplex(1.0, -2.0) } });
            byte[] record = { 0x3F, 0x80, 0, 0, 0xC0, 0, 0, 0, 0 }; // binary32 BE 1.0, -2.0, invalidation byte
            Assert.True(Contains(bytes, record));
        }

        private static bool Contains(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                if (haystack.Skip(i).Take(needle.Length).SequenceEqual(needle)) { return true; }
            }
            return false;
        }

        [Theory]
        [InlineData("4.20", MdfValueKind.Text, 12)]
        [InlineData("4.20", MdfValueKind.Text, 0)]
        [InlineData("4.20", MdfValueKind.ByteArray, 7)]
        [InlineData("4.20", MdfValueKind.Complex, 32)]
        [InlineData("4.20", MdfValueKind.Complex, 96)]
        [InlineData("3.30", MdfValueKind.Text, 65536)]
        public void InvalidFieldWidthsAreRefusedBeforeOutput(string version, object kindValue, int bits)
        {
            MdfValueKind kind = (MdfValueKind)kindValue;
            using (MdfWriter writer = MdfWriter.Create(new MemoryStream(), options: new MdfWriterOptions { Version = version, MaximumTextBytes = 1 << 20 }))
            {
                Exception error = Record.Exception(() => writer.AddChannel(Def("x", kind, bits)));
                Assert.True(error is ArgumentException || error is NotSupportedException, error?.ToString());
                Assert.Equal(0, writer.ChannelCount);
            }
        }

        [Fact]
        public void FieldRolesOrdersConversionsAndVersionsAreRefused()
        {
            using (MdfWriter writer = MdfWriter.Create(new MemoryStream(), options: new MdfWriterOptions { Version = "3.30" }))
            {
                Assert.Throws<NotSupportedException>(() => writer.AddChannel(Def("c", MdfValueKind.Complex, 64)));
            }
            using (MdfWriter writer = MdfWriter.Create(new MemoryStream()))
            {
                MdfChannelDefinition master = Def("m", MdfValueKind.Text, 64);
                master.IsMaster = true;
                Assert.Throws<ArgumentException>(() => writer.AddChannel(master));
                Assert.Throws<ArgumentException>(() => writer.AddChannel(Def("b", MdfValueKind.ByteArray, 64, true)));
                MdfChannelDefinition linear = Def("l", MdfValueKind.Complex, 64);
                linear.HasLinearConversion = true;
                Assert.Throws<NotSupportedException>(() => writer.AddChannel(linear));
                Assert.Equal(0, writer.ChannelCount);
            }
            using (MdfWriter writer = MdfWriter.Create(new MemoryStream(), options: new MdfWriterOptions { MaximumTextBytes = 4 }))
            {
                Assert.Throws<MdfResourceLimitException>(() => writer.AddChannel(Def("s", MdfValueKind.Text, 40)));
                writer.AddChannel(Def("s", MdfValueKind.Text, 32));
            }
        }

        [Fact]
        public void WrongSamplesWriteNoPartialRow()
        {
            MemoryStream stream = new MemoryStream();
            using (MdfWriter writer = MdfWriter.Create(stream, true, new MdfWriterOptions { Version = "3.30" }))
            {
                writer.AddChannel(Def("s", MdfValueKind.Text, 32));
                writer.AddChannel(Def("b", MdfValueKind.ByteArray, 16));
                writer.StartMeasurement();
                long length = stream.Length;
                MdfSampleValue bytes = MdfSampleValue.FromByteArray(new byte[] { 1, 2 });
                Assert.Throws<ArgumentOutOfRangeException>(() => writer.SaveSample(new[] { MdfSampleValue.FromText("abcde"), bytes }));
                Assert.Throws<ArgumentException>(() => writer.SaveSample(new[] { MdfSampleValue.FromText("\u20ac"), bytes })); // not Latin-1
                Assert.Throws<ArgumentException>(() => writer.SaveSample(new[] { MdfSampleValue.FromText("a\0b"), bytes }));
                Assert.Throws<ArgumentException>(() => writer.SaveSample(new[] { MdfSampleValue.FromText("ab"), MdfSampleValue.FromByteArray(new byte[] { 1 }) }));
                Assert.Throws<ArgumentException>(() => writer.SaveSample(new[] { MdfSampleValue.FromByteArray(new byte[4]), bytes })); // wrong kind
                Assert.Equal(length, stream.Length);
                Assert.Equal(0UL, writer.SampleCount);
                Assert.Equal(MdfWriterState.Recording, writer.State);
                writer.SaveSample(new[] { MdfSampleValue.FromText("abcd"), bytes });
                Assert.Equal(1UL, writer.SampleCount);
            }
        }

        [Fact]
        public void Binary32ComplexPartsOutsideTheRangeAreRefused()
        {
            using (MdfWriter writer = MdfWriter.Create(new MemoryStream()))
            {
                writer.AddChannel(Def("c", MdfValueKind.Complex, 64));
                writer.StartMeasurement();
                Assert.Throws<ArgumentOutOfRangeException>(() => writer.SaveSample(new[] { MdfSampleValue.FromComplex(0, 1e39) }));
                Assert.Equal(0UL, writer.SampleCount);
            }
        }

        [Fact]
        public void NewSampleFactoriesCopyAndValidate()
        {
            byte[] source = { 1, 2, 3 };
            MdfSampleValue value = MdfSampleValue.FromByteArray(source, false);
            source[0] = 9;
            Assert.Equal(new byte[] { 1, 2, 3 }, value.GetByteArrayValue());
            Assert.False(value.IsValid);
            Assert.Equal(MdfValueKind.ByteArray, value.Kind);
            MdfSampleValue text = MdfSampleValue.FromText("\u20ac");
            Assert.Equal(MdfValueKind.Text, text.Kind);
            Assert.Equal(new byte[] { 0xE2, 0x82, 0xAC }, text.GetByteArrayValue());
            Assert.Throws<ArgumentNullException>(() => MdfSampleValue.FromText(null));
            Assert.Throws<ArgumentNullException>(() => MdfSampleValue.FromByteArray(null));
            Assert.Throws<ArgumentException>(() => MdfSampleValue.FromText("\ud800"));
        }
    }
}
