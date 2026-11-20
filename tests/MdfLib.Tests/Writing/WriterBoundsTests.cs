// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MdfLib.Tests.TestDoubles;
using MdfLib.Writing;
using Xunit;

namespace MdfLib.Tests.Writing
{
    public sealed class WriterBoundsTests
    {
        public static IEnumerable<object[]> NarrowIntegers()
        {
            for (int bits = 1; bits < 64; bits++)
            foreach (bool signed in new[] { false, true })
            foreach (bool big in new[] { false, true }) { yield return new object[] { bits, signed, big }; }
        }

        [Theory]
        [MemberData(nameof(NarrowIntegers))]
        public void EveryNarrowWidthRejectsOutOfRangeBeforeWritingAndPreservesTheNextExactValue(int bits, bool signed, bool big)
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(output, true))
            {
                MdfChannelDefinition definition = WriterConfigurationTests.Channel("narrow", bits, signed ? MdfValueKind.SignedInteger : MdfValueKind.UnsignedInteger);
                definition.IsBigEndian = big;
                writer.AddChannel(definition); writer.StartMeasurement();
                int writes = output.Writes;
                if (signed)
                {
                    long limit = 1L << (bits - 1);
                    Assert.Throws<ArgumentOutOfRangeException>(() => writer.SaveSample(new[] { MdfSampleValue.FromSigned(limit) }));
                    Assert.Throws<ArgumentOutOfRangeException>(() => writer.SaveSample(new[] { MdfSampleValue.FromSigned(-limit - 1) }));
                    Assert.Equal(writes, output.Writes);
                    writer.SaveSample(new[] { MdfSampleValue.FromSigned(-limit) });
                }
                else
                {
                    Assert.Throws<ArgumentOutOfRangeException>(() => writer.SaveSample(new[] { MdfSampleValue.FromUnsigned(1UL << bits) }));
                    Assert.Equal(writes, output.Writes);
                    writer.SaveSample(new[] { MdfSampleValue.FromUnsigned((1UL << bits) - 1) });
                }
                writer.StopMeasurement(); writer.FinalizeMeasurement();
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(output.Snapshot())))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                MdfSampleValue value = reader.ReadSamples(group, new[] { 0 }).Single().GetValue(0, 0);
                if (signed) { Assert.Equal(-(1L << (bits - 1)), value.SignedValue); }
                else { Assert.Equal((1UL << bits) - 1, value.UnsignedValue); }
            }
        }

        [Theory]
        [InlineData(32, false)]
        [InlineData(32, true)]
        [InlineData(64, false)]
        [InlineData(64, true)]
        public void IeeeValuesRoundPredictablyAndNaNHasDocumentedCanonicalBits(int bits, bool big)
        {
            TestWriteStream output = new TestWriteStream();
            double negativeZero = BitConverter.Int64BitsToDouble(long.MinValue);
            double[] values = { double.NaN, double.PositiveInfinity, double.NegativeInfinity, negativeZero, 1.0 + Math.Pow(2, -24), -1e-300 };
            int dataStart;
            using (MdfWriter writer = MdfWriter.Create(output, true))
            {
                MdfChannelDefinition channel = WriterConfigurationTests.Channel("ieee", bits, MdfValueKind.FloatingPoint);
                channel.IsBigEndian = big; writer.AddChannel(channel); writer.StartMeasurement();
                dataStart = checked((int)output.Length);
                foreach (double value in values) { writer.SaveSample(new[] { MdfSampleValue.FromFloatingPoint(value) }); }
                writer.StopMeasurement(); writer.FinalizeMeasurement();
            }
            byte[] bytes = output.Snapshot();
            ulong canonical = bits == 32 ? 0x7fc00000UL : 0x7ff8000000000000UL;
            for (int i = 0; i < bits / 8; i++)
            { Assert.Equal((byte)(canonical >> (8 * i)), bytes[dataStart + (big ? bits / 8 - 1 - i : i)]); }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                List<double> actual = reader.ReadSamples(group, new[] { 0 }).SelectMany(b => Enumerable.Range(0, b.SampleCount).Select(r => b.GetValue(r, 0).FloatingPointValue)).ToList();
                Assert.Equal(values.Length, actual.Count); Assert.True(double.IsNaN(actual[0]));
                for (int i = 1; i < values.Length; i++)
                {
                    double expected = bits == 32 ? (double)(float)values[i] : values[i];
                    Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(actual[i]));
                }
            }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Float32DomainIsExplicitEvenForTheNextDoubleThatCouldRoundBackToFinite(bool big, bool negative)
        {
            double boundary = (double)float.MaxValue;
            double outside = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(boundary) + 1);
            if (negative) { boundary = -boundary; outside = -outside; }
            Assert.False(double.IsInfinity(outside));
            Assert.True(Math.Abs(outside) > (double)float.MaxValue);
            TestWriteStream stream = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(stream, true))
            {
                MdfChannelDefinition channel = WriterConfigurationTests.Channel("float32", 32, MdfValueKind.FloatingPoint);
                channel.IsBigEndian = big; writer.AddChannel(channel); writer.StartMeasurement();
                int before = stream.Writes;
                Assert.Throws<ArgumentOutOfRangeException>(() => writer.SaveSample(new[] { MdfSampleValue.FromFloatingPoint(outside) }));
                Assert.Equal(before, stream.Writes); Assert.Equal(0UL, writer.SampleCount);
                Assert.Equal(MdfWriterState.Recording, writer.State);
                writer.SaveSample(new[] { MdfSampleValue.FromFloatingPoint(boundary) });
                writer.StopMeasurement(); writer.FinalizeMeasurement();
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(stream.Snapshot())))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(BitConverter.DoubleToInt64Bits(boundary), BitConverter.DoubleToInt64Bits(
                    reader.ReadSamples(group, new[] { 0 }).Single().GetValue(0, 0).FloatingPointValue));
            }
        }

        [Fact]
        public void InvalidationBufferIsClearedBetweenRowsAndCrossesTheByteBoundary()
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(output, true))
            {
                for (int i = 0; i < 9; i++) { writer.AddChannel(WriterConfigurationTests.Channel("c" + i)); }
                Assert.Equal(11, writer.RecordByteCount);
                writer.StartMeasurement();
                writer.SaveSample(Enumerable.Range(0, 9).Select(i => MdfSampleValue.FromUnsigned((ulong)i, false)).ToArray());
                writer.SaveSample(Enumerable.Range(0, 9).Select(i => MdfSampleValue.FromUnsigned((ulong)i)).ToArray());
                writer.StopMeasurement(); writer.FinalizeMeasurement();
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(output.Snapshot())))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                MdfSampleBatch batch = reader.ReadSamples(group, Enumerable.Range(0, 9).ToArray()).Single();
                for (int c = 0; c < 9; c++)
                { Assert.False(batch.GetValue(0, c).IsValid); Assert.True(batch.GetValue(1, c).IsValid); Assert.Equal((ulong)c, batch.GetValue(0, c).UnsignedValue); }
            }
        }

        [Fact]
        public void NinthInvalidationBitIsChargedBeforeAddingTheNinthChannel()
        {
            using (MdfWriter writer = MdfWriter.Create(new TestWriteStream(), options: new MdfWriterOptions { MaximumRecordBytes = 10 }))
            {
                for (int i = 0; i < 8; i++) { writer.AddChannel(WriterConfigurationTests.Channel()); }
                Assert.Equal(9, writer.RecordByteCount);
                Assert.Throws<MdfResourceLimitException>(() => writer.AddChannel(WriterConfigurationTests.Channel()));
                Assert.Equal(8, writer.ChannelCount); Assert.Equal(9, writer.RecordByteCount);
            }
        }

        [Fact]
        public void Mdf3AdditionalByteOffsetDoesNotWrapTheUInt16StartBit()
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(output, true, new MdfWriterOptions { Version = "3.30" }))
            {
                for (int i = 0; i < 1025; i++) { writer.AddChannel(WriterConfigurationTests.Channel("c" + i, 64)); }
                writer.StartMeasurement();
                writer.SaveSample(Enumerable.Range(0, 1025).Select(i => MdfSampleValue.FromUnsigned(i == 1024 ? ulong.MaxValue : (ulong)i)).ToArray());
                writer.StopMeasurement(); writer.FinalizeMeasurement();
            }
            byte[] bytes = output.Snapshot();
            // Separate literal wire traversal, not the tested reader layout.
            int dg = checked((int)U32(bytes, 68));
            int cg = checked((int)U32(bytes, dg + 8));
            int cn = checked((int)U32(bytes, cg + 8));
            for (int i = 0; i < 1024; i++) { cn = checked((int)U32(bytes, cn + 4)); }
            Assert.Equal(0, bytes[cn + 186]); Assert.Equal(0, bytes[cn + 187]);
            Assert.Equal(0, bytes[cn + 226]); Assert.Equal(32, bytes[cn + 227]); //8192 LE
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(8200UL, group.RecordByteCount);
                Assert.Equal(ulong.MaxValue, reader.ReadSamples(group, new[] { 1024 }).Single().GetValue(0, 0).UnsignedValue);
            }
        }

        [Fact]
        public void Mdf3RecordWireLimitCannotBeBypassedByIncreasingTheBufferLimit()
        {
            TestWriteStream output = new TestWriteStream { RetainBytes = false };
            using (MdfWriter writer = MdfWriter.Create(output, true, new MdfWriterOptions { Version = "3.30", MaximumChannelCount = 9000 }))
            {
                MdfChannelDefinition channel = WriterConfigurationTests.Channel("x", 64);
                for (int i = 0; i < 8191; i++) { writer.AddChannel(channel); }
                Assert.Throws<NotSupportedException>(() => writer.AddChannel(channel));
                Assert.Equal(8191, writer.ChannelCount); Assert.Equal(65528, writer.RecordByteCount); Assert.Equal(0, output.Writes);
            }
        }

        [Fact]
        public void HundredThousandSamplesReuseOneRecordWithoutSampleProportionalBuffersOrQueues()
        {
            TestWriteStream output = new TestWriteStream { RetainBytes = false };
            using (MdfWriter writer = WriterConfigurationTests.Started(output))
            {
                MdfSampleValue[] row = { MdfSampleValue.FromUnsigned(7) };
                long initial = output.Length;
                for (int i = 0; i < 100000; i++) { writer.SaveSample(row); }
                Assert.Equal(initial + 200000, output.Length); Assert.Equal(100000UL, writer.SampleCount);
                Assert.Equal(2, output.Buffers.Count); // Released prefix + ONE reused private record.
                writer.StopMeasurement(); writer.FinalizeMeasurement();
                Assert.InRange(output.Buffers.Count, 4, 5); // Plus bounded patch/ID/(optional padding).
                Assert.InRange(output.MaximumWrite, 1, 1024); Assert.Equal(0, output.Reads); Assert.Equal(0, output.SetLengths);
                Assert.Equal(output.Length + 64 + 16, output.WrittenBytes); // Header and rows once; two patches and final ID overwrite.
            }
        }

        [Fact]
        public void HugeOffsetsCountersAndFinalAlignmentAreCheckedWithoutGiantOutputs()
        {
            Assert.Equal((long)uint.MaxValue, WriterPlan.CheckAppend(true, 0, (long)uint.MaxValue - 1, 1));
            Assert.Equal(1L, WriterPlan.CheckAppend(true, uint.MaxValue - 1UL, 0, 1));
            Assert.Throws<NotSupportedException>(() => WriterPlan.CheckAppend(true, uint.MaxValue, 0, 1));
            Assert.Throws<NotSupportedException>(() => WriterPlan.CheckAppend(true, 0, uint.MaxValue, 1));
            Assert.Equal((1L << 42) + 8, WriterPlan.CheckAppend(false, 1UL << 40, 1L << 42, 8));
            Assert.Equal(long.MaxValue - 7, WriterPlan.CheckAppend(false, 0, long.MaxValue - 8, 1));
            Assert.Throws<NotSupportedException>(() => WriterPlan.CheckAppend(false, 0, long.MaxValue - 7, 1));
            Assert.Throws<NotSupportedException>(() => WriterPlan.CheckAppend(false, 0, long.MaxValue, int.MaxValue));
            Assert.Throws<NotSupportedException>(() => WriterPlan.CheckAppend(false, ulong.MaxValue, 0, 1));
        }

        private static uint U32(byte[] bytes, int at)
        { return (uint)(bytes[at] | bytes[at + 1] << 8 | bytes[at + 2] << 16 | bytes[at + 3] << 24); }
    }
}
