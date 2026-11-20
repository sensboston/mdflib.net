// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Writing
{
    // fixed (CN-template CA) array channels with per-element invalidation bits.
    public sealed class WriterArrayTests
    {
        private static readonly int[][] Shapes = { null, new[] { 2, 3 }, new[] { 4 }, new[] { 3 }, new[] { 2 }, new[] { 5 }, new[] { 2 }, null };

        private static MdfChannelDefinition[] Definitions()
        {
            return new[]
            {
                WriterMultiGroupTests.Master(),
                new MdfChannelDefinition { Name = "matrix", BitCount = 16, ArrayDimensions = Shapes[1] },
                new MdfChannelDefinition { Name = "gain", ValueKind = MdfValueKind.FloatingPoint, BitCount = 32, ArrayDimensions = Shapes[2],
                    HasLinearConversion = true, Factor = 2, Offset = 1 },
                new MdfChannelDefinition { Name = "labels", ValueKind = MdfValueKind.Text, BitCount = 64, ArrayDimensions = Shapes[3] },
                new MdfChannelDefinition { Name = "iq", ValueKind = MdfValueKind.Complex, BitCount = 128, ArrayDimensions = Shapes[4] },
                new MdfChannelDefinition { Name = "packed", ValueKind = MdfValueKind.SignedInteger, BitCount = 12, ArrayDimensions = Shapes[5] },
                new MdfChannelDefinition { Name = "motorola", BitCount = 32, IsBigEndian = true, ArrayDimensions = Shapes[6] },
                WriterConfigurationTests.Channel("tail", 8),
            };
        }

        private static int Count(int column) { return Shapes[column] == null ? 1 : Shapes[column].Aggregate(1, (a, b) => a * b); }

        private static MdfSampleValue Value(int row, int column, int element)
        {
            bool valid = column == 0 || (row + column + element) % 5 != 0;
            int seed = row * 31 + column * 7 + element;
            switch (column)
            {
                case 0: return MdfSampleValue.FromFloatingPoint(row * 0.01);
                case 1: return MdfSampleValue.FromUnsigned((ulong)(seed & 0xFFFF), valid);
                case 2: return MdfSampleValue.FromFloatingPoint(seed * 0.5, valid);
                case 3: return MdfSampleValue.FromText(element == 1 ? string.Empty : "L" + seed, valid);
                case 4: return MdfSampleValue.FromComplex(seed, -seed * 0.25, valid);
                case 5: return MdfSampleValue.FromSigned(seed % 2048 - 1024, valid);
                case 6: return MdfSampleValue.FromUnsigned((ulong)seed * 65537, valid);
                default: return MdfSampleValue.FromUnsigned((ulong)(row & 0xFF), row % 3 != 1);
            }
        }

        private static MdfSampleValue[] Row(int row)
        {
            List<MdfSampleValue> values = new List<MdfSampleValue>();
            for (int column = 0; column < Shapes.Length; column++)
            {
                for (int element = 0; element < Count(column); element++) { values.Add(Value(row, column, element)); }
            }
            return values.ToArray();
        }

        private static byte[] Write(string version, int rows, bool compress = false, int buffer = 0)
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(output, true, new MdfWriterOptions
            { Version = version, CompressData = compress, CompressionBlockBytes = 100, WriteBufferBytes = buffer }))
            {
                foreach (MdfChannelDefinition definition in Definitions()) { writer.AddChannel(definition); }
                writer.StartMeasurement();
                for (int r = 0; r < rows; r++) { writer.SaveSample(Row(r)); }
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
            }
            return output.Snapshot();
        }

        private static void AssertValue(MdfSampleValue expected, MdfSampleValue actual)
        {
            Assert.Equal(expected.Kind, actual.Kind);
            Assert.Equal(expected.IsValid, actual.IsValid);
            switch (expected.Kind)
            {
                case MdfValueKind.UnsignedInteger: Assert.Equal(expected.UnsignedValue, actual.UnsignedValue); break;
                case MdfValueKind.SignedInteger: Assert.Equal(expected.SignedValue, actual.SignedValue); break;
                case MdfValueKind.FloatingPoint: Assert.Equal(expected.FloatingPointValue, actual.FloatingPointValue); break;
                case MdfValueKind.Complex:
                    Assert.Equal(expected.RealPart, actual.RealPart);
                    Assert.Equal(expected.ImaginaryPart, actual.ImaginaryPart);
                    break;
                default: Assert.Equal(expected.TextValue, actual.TextValue); break;
            }
        }

        internal static void AssertFile(byte[] file, int rows)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file)))
            {
                Assert.True(MdfValidator.Validate(reader).IsValid);
                MdfChannelGroup group = reader.ReadMetadata().Single().ChannelGroups.Single();
                Assert.Equal((ulong)rows, group.SampleCount);
                Assert.Equal(Shapes.Length, group.Channels.Count);
                for (int column = 0; column < Shapes.Length; column++)
                {
                    MdfChannel channel = group.Channels[column];
                    Assert.True(channel.CanReadRaw, channel.Name + ": " + channel.UnsupportedReason);
                    if (Shapes[column] == null) { Assert.Null(channel.Array); }
                    else
                    {
                        Assert.Equal(Shapes[column].Select(d => (ulong)d), channel.Array.Dimensions);
                        Assert.Equal((ulong)Count(column), channel.Array.ElementCount);
                    }
                }
                int row = 0;
                foreach (MdfSampleBatch batch in reader.ReadSamples(group, Enumerable.Range(0, Shapes.Length).ToArray(), batchSize: 3))
                {
                    for (int i = 0; i < batch.SampleCount; i++, row++)
                    {
                        for (int column = 0; column < Shapes.Length; column++)
                        {
                            for (int element = 0; element < Count(column); element++)
                            { AssertValue(Value(row, column, element), batch.GetValue(i, column, (ulong)element)); }
                        }
                    }
                }
                Assert.Equal(rows, row);
                // The linear conversion applies to every element.
                foreach (MdfSampleBatch batch in reader.ReadSamples(group, new[] { 2 }, representation: MdfValueRepresentation.Physical))
                {
                    for (int i = 0; i < batch.SampleCount; i++)
                    {
                        for (int element = 0; element < 4; element++)
                        {
                            MdfSampleValue expected = Value((int)batch.FirstSampleIndex + i, 2, element);
                            MdfSampleValue actual = batch.GetValue(i, 0, (ulong)element);
                            Assert.Equal(expected.IsValid, actual.IsValid);
                            if (expected.IsValid) { Assert.Equal(expected.FloatingPointValue * 2 + 1, actual.FloatingPointValue); }
                        }
                    }
                }
            }
        }

        [Theory]
        [InlineData("4.10", 0, false, 0)]
        [InlineData("4.10", 17, false, 0)]
        [InlineData("4.20", 17, false, 50)]
        [InlineData("4.10", 17, true, 0)]
        [InlineData("4.20", 30, true, 0)]
        public void ArraysRoundTripWithPerElementValidity(string version, int rows, bool compress, int buffer)
        {
            AssertFile(Write(version, rows, compress, buffer), rows);
        }

        [Fact]
        public void WireLayoutIsACnTemplateArrayWithPerElementInvalidationBits()
        {
            byte[] file = Write("4.10", 2);
            int dg = (int)BitConverter.ToUInt64(file, 64 + 24);
            int cg = (int)BitConverter.ToUInt64(file, dg + 32);
            // Data: 8 + 12 + 16 + 24 + 32 + 10 + 8 + 1 = 111 bytes; bits: 1 + 6 + 4 + 3 + 2 + 5 + 2 + 1 = 24 -> 3 bytes.
            Assert.Equal(111U, BitConverter.ToUInt32(file, cg + 96));
            Assert.Equal(3U, BitConverter.ToUInt32(file, cg + 100));
            int cn = (int)BitConverter.ToUInt64(file, cg + 32);
            int[] offsets = { 0, 8, 20, 36, 60, 92, 102, 110 };
            int[] bits = { 0, 1, 7, 11, 14, 16, 21, 23 };
            for (int column = 0; column < Shapes.Length; column++)
            {
                Assert.Equal((uint)offsets[column], BitConverter.ToUInt32(file, cn + 92));
                Assert.Equal((uint)bits[column], BitConverter.ToUInt32(file, cn + 104));
                int ca = (int)BitConverter.ToUInt64(file, cn + 32);
                if (Shapes[column] == null) { Assert.Equal(0, ca); }
                else
                {
                    Assert.Equal("##CA", System.Text.Encoding.ASCII.GetString(file, ca, 4));
                    Assert.Equal(0, file[ca + 32]); // Array.
                    Assert.Equal(0, file[ca + 33]); // CN template.
                    Assert.Equal(Shapes[column].Length, BitConverter.ToUInt16(file, ca + 34));
                    Assert.Equal(0U, BitConverter.ToUInt32(file, ca + 36));
                    Assert.Equal(1U, BitConverter.ToUInt32(file, ca + 44));
                    for (int axis = 0; axis < Shapes[column].Length; axis++)
                    { Assert.Equal((ulong)Shapes[column][axis], BitConverter.ToUInt64(file, ca + 48 + 8 * axis)); }
                }
                cn = (int)BitConverter.ToUInt64(file, cn + 24);
            }
            Assert.Equal(0, cn);
        }

        [Fact]
        public void InvalidDefinitionsAndRowsAreRefused()
        {
            using (MdfWriter writer = MdfWriter.Create(new TestWriteStream(), true, new MdfWriterOptions { Version = "3.30" }))
            {
                Assert.Throws<NotSupportedException>(() => writer.AddChannel(new MdfChannelDefinition { Name = "a", BitCount = 8, ArrayDimensions = new[] { 2 } }));
            }
            using (MdfWriter writer = MdfWriter.Create(new TestWriteStream(), true, new MdfWriterOptions { MaximumRecordBytes = 64 }))
            {
                MdfChannelDefinition master = WriterMultiGroupTests.Master();
                master.ArrayDimensions = new[] { 2 };
                Assert.Throws<ArgumentException>(() => writer.AddChannel(master));
                Assert.Throws<NotSupportedException>(() => writer.AddChannel(new MdfChannelDefinition
                { Name = "v", ValueKind = MdfValueKind.Text, IsVariableLength = true, ArrayDimensions = new[] { 2 } }));
                Assert.Throws<ArgumentOutOfRangeException>(() => writer.AddChannel(new MdfChannelDefinition { Name = "e", ArrayDimensions = new int[0] }));
                Assert.Throws<ArgumentOutOfRangeException>(() => writer.AddChannel(new MdfChannelDefinition { Name = "z", ArrayDimensions = new[] { 3, 0 } }));
                Assert.Throws<ArgumentOutOfRangeException>(() => writer.AddChannel(new MdfChannelDefinition { Name = "h", ArrayDimensions = new[] { 65536, 65536 } }));
                Assert.Equal(nameof(MdfWriterOptions.MaximumRecordBytes), Assert.Throws<MdfResourceLimitException>(
                    () => writer.AddChannel(new MdfChannelDefinition { Name = "big", ArrayDimensions = new[] { 8 } })).LimitName);
                int[] shape = { 3 };
                Assert.Equal(0, writer.AddChannel(new MdfChannelDefinition { Name = "ok", BitCount = 8, ArrayDimensions = shape }));
                shape[0] = 100; // The definition was copied.
                writer.AddChannel(WriterConfigurationTests.Channel("s", 8));
                writer.StartMeasurement();
                MdfSampleValue one = MdfSampleValue.FromUnsigned(1);
                Assert.Throws<ArgumentException>(() => writer.SaveSample(new[] { one, one }));
                Assert.Throws<ArgumentException>(() => writer.SaveSample(new[] { one, one, one, one, one }));
                Assert.Throws<ArgumentOutOfRangeException>(() => writer.SaveSample(new[] { one, MdfSampleValue.FromUnsigned(256), one, one }));
                writer.SaveSample(new[] { one, one, one, one });
                Assert.Equal(1UL, writer.GetSampleCount(0));
            }
        }

        [Theory]
        [InlineData("4.10")]
        [InlineData("4.20")]
        public void CopierCopiesArraysExactly(string version)
        {
            byte[] source = Write("4.20", 9, true);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(source)))
            using (MemoryStream copy = new MemoryStream())
            {
                MdfCopyResult result = MdfFileCopier.Copy(reader, copy, new MdfCopyOptions { WriterOptions = new MdfWriterOptions { Version = version } });
                Assert.Empty(result.Skipped);
                AssertFile(copy.ToArray(), 9);
            }
        }

        [Fact]
        public void BackgroundWriterAndAppendAcceptFlattenedRows()
        {
            byte[] original = WriterMultiGroupTests.TwoGroups("4.10", false, 4, 2);
            MemoryStream file = new MemoryStream();
            file.Write(original, 0, original.Length);
            using (MdfWriter writer = MdfWriter.Append(file, true))
            {
                writer.AddChannel(WriterMultiGroupTests.Master());
                writer.AddChannel(new MdfChannelDefinition { Name = "vector", BitCount = 8, ArrayDimensions = new[] { 3 } });
                writer.StartMeasurement();
                using (MdfBackgroundWriter background = new MdfBackgroundWriter(writer))
                {
                    for (int r = 0; r < 5; r++)
                    {
                        background.SaveSample(new[] { MdfSampleValue.FromFloatingPoint(r), MdfSampleValue.FromUnsigned((ulong)r),
                            MdfSampleValue.FromUnsigned((ulong)r + 1, r != 2), MdfSampleValue.FromUnsigned((ulong)r + 2) });
                    }
                    background.Complete();
                }
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file.ToArray())))
            {
                MdfChannelGroup group = reader.ReadMetadata()[2].ChannelGroups.Single();
                Assert.Equal(5UL, group.SampleCount);
                MdfSampleBatch batch = reader.ReadSamples(group, new[] { 1 }).Single();
                for (int r = 0; r < 5; r++)
                {
                    for (int e = 0; e < 3; e++)
                    {
                        MdfSampleValue value = batch.GetValue(r, 0, (ulong)e);
                        Assert.Equal((ulong)(r + e), value.UnsignedValue);
                        Assert.Equal(!(r == 2 && e == 1), value.IsValid);
                    }
                }
            }
        }
    }
}
