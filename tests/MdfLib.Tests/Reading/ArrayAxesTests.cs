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
    // array roles, axes and quantities are inert metadata. Literal CA blocks appended to the
    // frozen m5d-arrays410 file (u8[2,3] channel 2), never the library's writer.
    public sealed class ArrayAxesTests
    {
        private const string Storage = "Only CN-template array storage is supported.";
        private const string Dynamic = "Dynamic-size arrays are not supported.";
        private const string Flags = "Unknown array flags, or the standard-axis flag before MDF 4.20, are not supported.";
        private const string Type = "Only array, scaling-axis and look-up CN-template arrays are supported.";
        private const string Scale = "A scaling-axis array must have exactly one dimension.";
        private const string Fixed = "A fixed-axis array requires the axis flag.";
        // In-range address of the ##HD block: resolving it as any CN/CC/DG/CG target would fail with MDF005, so a successful
        // read proves the quantity/axis links are never dereferenced (out-of-range links are rejected by the generic resolver).
        private const ulong Hd = 64;

        [Theory]
        [InlineData(2, (byte)0, 0x00U, 1, 0)]
        [InlineData(2, (byte)2, 0x00U, 1, 0)]
        [InlineData(1, (byte)1, 0x00U, 1, 0)]
        [InlineData(2, (byte)2, 0x10U, 9, 0)]     // 1 + D conversions + 3D axis channel links
        [InlineData(2, (byte)2, 0x30U, 3, 5)]     // fixed: D conversions, Σdims REAL values
        [InlineData(2, (byte)0, 0x30U, 3, 5)]
        [InlineData(1, (byte)1, 0x30U, 2, 6)]
        [InlineData(2, (byte)0, 0x02U, 7, 0)]     // input quantity: 3D
        [InlineData(2, (byte)0, 0x04U, 4, 0)]     // output quantity: 3
        [InlineData(2, (byte)0, 0x08U, 4, 0)]     // comparison quantity: 3
        [InlineData(2, (byte)2, 0x8EU, 13, 0)]
        [InlineData(2, (byte)2, 0xBEU, 15, 5)]
        [InlineData(2, (byte)2, 0x9EU, 21, 0)]
        [InlineData(1, (byte)2, 0x9EU, 14, 0)]   // 1 + 3 + 3 + 3 + 1 + 3
        public void AcceptedRolesFlagsAndLinksNeverChangeElementValues(int rank, byte type, uint flags, int links, int axisValues)
        {
            ulong[] shape = rank == 1 ? new ulong[] { 6 } : new ulong[] { 2, 3 };
            byte[] plain = M5DWire.Read();
            M5DWire.Array(ref plain, shape);
            byte[] bytes = M5DWire.Read();
            M5DWire.Array(ref bytes, shape, flags: flags, links: links, type: type, axisValues: axisValues);
            Assert.Equal(Values(plain), Values(bytes));
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannel channel = reader.ReadMetadata()[0].ChannelGroups[0].Channels[2];
                Assert.Equal(shape, channel.Array.Dimensions);
                Assert.Null(channel.UnsupportedReason);
                Assert.True(channel.CanReadPhysical);
            }
        }

        [Fact]
        public void ElementsMatchTheFrozenArrayRegardlessOfAxisMetadata()
        {
            byte[] bytes = M5DWire.Read();
            M5DWire.Array(ref bytes, new ulong[] { 2, 3 }, flags: 0x10, links: 9, type: 2);
            Assert.Equal(new long[] { 0, 1, 255, 128, 254, 37 }, Values(bytes)[0].Select(v => v.Item1));
        }

        [Theory]
        [InlineData(0x10U, 0, 8)]
        [InlineData(0x10U, 0, 10)]
        [InlineData(0x30U, 5, 2)]
        [InlineData(0x30U, 5, 4)]
        [InlineData(0x30U, 5, 9)]     // axis channel links are absent with fixed axes
        [InlineData(0x02U, 0, 4)]     // input quantity is one triple per dimension
        [InlineData(0x0EU, 0, 10)]
        [InlineData(0x0EU, 0, 14)]
        [InlineData(0x80U, 0, 2)]
        public void LinkCountMustBeExactlyWhatFlagsAndDimensionsDeclare(uint flags, int axisValues, int links)
        {
            byte[] bytes = M5DWire.Read();
            int ca = M5DWire.Array(ref bytes, new ulong[] { 2, 3 }, flags: flags, links: links, type: 2, axisValues: axisValues);
            Failure(bytes, ca);
        }

        [Theory]
        [InlineData(0x30U, 3, 4)]
        [InlineData(0x30U, 3, 6)]
        [InlineData(0x30U, 3, 0)]
        [InlineData(0x10U, 9, 1)]
        [InlineData(0x80U, 1, 1)]
        public void PayloadLengthMustBeExactlyTheDimensionAndFixedAxisSections(uint flags, int links, int axisValues)
        {
            byte[] bytes = M5DWire.Read();
            int ca = M5DWire.Array(ref bytes, new ulong[] { 2, 3 }, flags: flags, links: links, type: 2, axisValues: axisValues);
            Failure(bytes, ca);
        }

        [Fact]
        public void FixedAxisValueCountCannotOverflowTheExpectedPayloadLength()
        {
            byte[] bytes = M5DWire.Read();
            int ca = M5DWire.Array(ref bytes, new ulong[] { 1UL << 62, 1 }, flags: 0x30, links: 3, type: 2);
            Failure(bytes, ca);
        }

        [Theory]
        [InlineData((byte)3, (byte)1, 0x221U, Storage)]
        [InlineData((byte)3, (byte)0, 0x221U, Dynamic)]
        [InlineData((byte)3, (byte)0, 0x220U, Flags)]
        [InlineData((byte)3, (byte)0, 0x120U, Flags)]   // standard axis before 4.20
        [InlineData((byte)3, (byte)0, 0x20U, Type)]
        [InlineData((byte)4, (byte)0, 0x00U, Type)]
        [InlineData((byte)255, (byte)0, 0x00U, Type)]
        [InlineData((byte)1, (byte)0, 0x20U, Scale)]
        [InlineData((byte)2, (byte)0, 0x20U, Fixed)]
        [InlineData((byte)0, (byte)2, 0x00U, Storage)]
        public void RefusalsFollowTheContractOrderBeforeAnyLinkOrPayloadCheck(byte type, byte storage, uint flags, string reason)
        {
            byte[] bytes = M5DWire.Read();
            // Deliberately inconsistent links and payload: refused arrays never validate their axis structure.
            int ca = M5DWire.Array(ref bytes, new ulong[] { 2, 3 }, flags: flags, links: 6, type: type, axisValues: 1, fill: Hd);
            bytes[M3Wire.Payload(bytes, ca) + 1] = storage;
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                MdfChannel channel = group.Channels[2];
                Assert.Equal(reason, channel.UnsupportedReason);
                Assert.Equal(new ulong[] { 2, 3 }, channel.Array.Dimensions);
                Assert.False(channel.CanReadRaw);
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 2 }).ToList());
                Assert.Equal(5, reader.ReadSamples(group, new[] { 1 }).Sum(b => b.SampleCount));
            }
        }

        [Fact]
        public void ChainedCompositionStaysRefusedWhenAxisLinksAreValid()
        {
            byte[] bytes = M5DWire.Read();
            int inner = M5DWire.Array(ref bytes, new ulong[] { 2, 3 });
            M5DWire.Array(ref bytes, new ulong[] { 2, 3 }, next: (ulong)inner, flags: 0x10, links: 9, type: 2);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                Assert.Equal("Chained/nested array composition is not supported.", reader.ReadMetadata()[0].ChannelGroups[0].Channels[2].UnsupportedReason);
            }
        }

        [Theory]
        [InlineData(0x9EU, 21, 0)]
        [InlineData(0xBEU, 15, 5)]
        public void QuantityAndAxisLinksAndFixedAxisValuesAreNeverResolvedOrRead(uint flags, int links, int axisValues)
        {
            byte[] plain = M5DWire.Read();
            M5DWire.Array(ref plain, new ulong[] { 2, 3 });
            byte[] bytes = M5DWire.Read();
            int ca = M5DWire.Array(ref bytes, new ulong[] { 2, 3 }, flags: flags, links: links, type: 2, axisValues: axisValues, fill: Hd);
            // Fill the fixed axis section with NaN payload that would be noticed if interpreted.
            int axes = M3Wire.Payload(bytes, ca) + 32;
            for (int i = 0; i < axisValues; i++) { M3Wire.Put(bytes, axes + i * 8, ulong.MaxValue); }
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.True(group.Channels[2].CanReadRaw, group.Channels[2].UnsupportedReason);
                if (axisValues != 0) { Assert.DoesNotContain(source.ReadRanges, r => r.Item1 < axes + axisValues * 8 && r.Item1 + r.Item2 > axes); }
            }
            Assert.Equal(Values(plain), Values(bytes));
        }

        [Fact]
        public void StandardAxisIsAcceptedOnlyInMdf420AndIsInert()
        {
            byte[] bytes = M5DWire.Read("m5t-axes420");
            int payload = M3Wire.Payload(bytes, M5DWire.Ca(bytes, 1));
            Assert.Equal(0x130UL, M3Wire.U64(bytes, payload + 4) & uint.MaxValue);
            Tuple<long, bool>[][] standard = Values(bytes, 1);
            M3Wire.Put(bytes, payload + 4, 0x30, 4);
            Assert.Equal(standard, Values(bytes, 1));
            byte[] older = M5DWire.Read("m5t-axes411");
            using (MdfReader reader = MdfReader.Open(new MemoryStream(older, false)))
            {
                Assert.Equal(Flags, reader.ReadMetadata()[0].ChannelGroups[0].Channels.Single(c => c.Name == "std").UnsupportedReason);
            }
        }

        [Fact]
        public void InverseLayoutCombinesWithAxisMetadata()
        {
            byte[] bytes = M5DWire.Read("m5t-axes411");
            int payload = M3Wire.Payload(bytes, M5DWire.Ca(bytes, 4));
            Assert.Equal(0x50UL, M3Wire.U64(bytes, payload + 4) & uint.MaxValue);
            Tuple<long, bool>[][] inverse = Values(bytes, 4);
            M3Wire.Put(bytes, payload + 4, 0x10, 4);
            Tuple<long, bool>[][] forward = Values(bytes, 4);
            Assert.NotEqual(inverse, forward);
            // Logical [2,3] element (i, j) is the stored element j*2 + i under inverse layout.
            for (int row = 0; row < inverse.Length; row++)
            for (int i = 0; i < 2; i++)
            for (int j = 0; j < 3; j++)
            { Assert.Equal(forward[row][j * 2 + i], inverse[row][i * 3 + j]); }
        }

        private static Tuple<long, bool>[][] Values(byte[] bytes, int channel = 2)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.True(group.Channels[channel].CanReadRaw, group.Channels[channel].UnsupportedReason);
                ulong count = group.Channels[channel].Array.ElementCount;
                List<Tuple<long, bool>[]> rows = new List<Tuple<long, bool>[]>();
                foreach (MdfSampleBatch batch in reader.ReadSamples(group, new[] { channel }, batchSize: 2))
                for (int row = 0; row < batch.SampleCount; row++)
                {
                    Tuple<long, bool>[] values = new Tuple<long, bool>[count];
                    for (ulong element = 0; element < count; element++)
                    {
                        MdfSampleValue value = batch.GetValue(row, 0, element);
                        long bits = value.Kind == MdfValueKind.UnsignedInteger ? unchecked((long)value.UnsignedValue)
                            : value.Kind == MdfValueKind.SignedInteger ? value.SignedValue : BitConverter.DoubleToInt64Bits(value.FloatingPointValue);
                        values[element] = Tuple.Create(bits, value.IsValid);
                    }
                    rows.Add(values);
                }
                return rows.ToArray();
            }
        }

        private static void Failure(byte[] bytes, int ca)
        {
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfFormatException error = Assert.Throws<MdfFormatException>(() => reader.ReadMetadata());
                Assert.Equal("MDF006", error.DiagnosticCode);
                Assert.Equal((ulong)ca, error.Offset);
                Assert.Equal(0, source.WriteCalls);
            }
        }
    }
}
