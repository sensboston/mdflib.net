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
    // MDF 3.30 N-dimensional CD arrays over literal mutations of the frozen pre-parser m5i files.
    public sealed class Mdf3ArrayTests
    {
        // Block addresses recorded in tools/fixtures/m5i-expectations.json (m5i-arrays-le330).
        private const int CnMat = 558;
        private const int CdMat = 786;
        private const int CnMat00 = 870;
        private const int CnMat01 = 1098;
        private const int CnVec = 2238;
        private const int CdVec = 2466;
        private const int CnCounter = 9438;
        private const int CcLinear = 9666;
        private const int Dg = 272;
        private const int Cg = 300;
        private const int Mat = 1;
        private const int Vec = 8;
        private const int Cube = 37;
        private const int Counter = 38;
        private const string Type = "Only N-dimensional (256+N) MDF 3 channel dependencies are supported.";
        private const string Scope = "Channel dependency elements must be distinct other channels of the same channel group.";
        private const string Element = "Channel dependency elements must be supported numeric scalars of the parent's encoding, bit count, bit offset and conversion.";
        private const string Layout = "Channel dependency elements must be evenly spaced in reference order at a positive byte stride.";
        private const string Master = "Array master channels are not supported; a stored master must be scalar.";

        private static byte[] Fixture(string id = "m5i-arrays-le330") { return FixtureCorpus.Read(id); }

        private static MdfChannelGroup Group(MdfReader reader) { return reader.ReadMetadata()[0].ChannelGroups[0]; }

        private static int Reference(int index, int field) { return CdMat + 8 + 12 * index + 4 * field; }

        [Fact]
        public void ShapesAreReversedWireDimensionsAndElementsStayScalarChannels()
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(Fixture(), false)))
            {
                MdfChannelGroup group = Group(reader);
                Assert.Equal(new ulong[] { 2, 3 }, group.Channels[Mat].Array.Dimensions);
                Assert.Equal(6UL, group.Channels[Mat].Array.ElementCount);
                Assert.Equal(new ulong[] { 4 }, group.Channels[Vec].Array.Dimensions);
                Assert.Equal(new ulong[] { 2, 3, 4 }, group.Channels[Cube].Array.Dimensions);
                Assert.Equal(24UL, group.Channels[Cube].Array.ElementCount);
                foreach (MdfChannel channel in group.Channels.Where(c => c.Index != Mat && c.Index != Vec && c.Index != Cube))
                {
                    Assert.Null(channel.Array);
                    Assert.True(channel.CanReadRaw, channel.Name);
                }
                Assert.All(new[] { Mat, Vec, Cube }, index => Assert.True(group.Channels[index].CanReadRaw));
                Assert.Equal(MdfValueKind.FloatingPoint, group.Channels[Mat].ValueKind);
                Assert.Equal(MdfValueKind.SignedInteger, group.Channels[Vec].ValueKind);
                Assert.Equal(MdfConversionKind.Linear, group.Channels[Vec].Conversion.Kind);
                Assert.Equal(MdfValueKind.UnsignedInteger, group.Channels[Cube].ValueKind);
                Assert.Equal(39, group.Channels.Count);
            }
        }

        [Theory]
        [InlineData(MdfValueRepresentation.Raw)]
        [InlineData(MdfValueRepresentation.Physical)]
        public void ArrayElementsEqualTheReferencedScalarChannelsInReferenceOrder(object representationValue)
        {
            MdfValueRepresentation representation = (MdfValueRepresentation)representationValue;
            using (MdfReader reader = MdfReader.Open(new MemoryStream(Fixture(), false)))
            {
                MdfChannelGroup group = Group(reader);
                int[] cube = Enumerable.Range(13, 24).ToArray(); // Element CNs precede their parent in the chain.
                Dictionary<int, int[]> elements = new Dictionary<int, int[]>
                {
                    { Mat, Enumerable.Range(2, 6).ToArray() }, { Vec, Enumerable.Range(9, 4).ToArray() }, { Cube, cube }
                };
                int[] selected = new[] { Cube, Vec, Mat }.Concat(elements.Values.SelectMany(e => e)).ToArray();
                MdfSampleBatch batch = reader.ReadSamples(group, selected, representation: representation).Single();
                for (int row = 0; row < 5; row++)
                for (int column = 0; column < 3; column++)
                {
                    int[] parts = elements[selected[column]];
                    for (int element = 0; element < parts.Length; element++)
                    {
                        MdfSampleValue value = batch.GetValue(row, column, (ulong)element);
                        MdfSampleValue scalar = batch.GetValue(row, Array.IndexOf(selected, parts[element]));
                        Same(scalar, value);
                        Assert.True(value.IsValid);
                    }
                    Assert.Throws<InvalidOperationException>(() => batch.GetValue(row, column));
                    Assert.Throws<ArgumentOutOfRangeException>(() => batch.GetValue(row, column, (ulong)parts.Length));
                }
                // Typed spot checks from the generator inputs: mat[1][2] = (5 - 2.5) * (r + 1) + 0.125 * r; vec physical linear.
                Assert.Equal(2.5 * 4 + 0.375, batch.GetValue(3, 2, 5).FloatingPointValue);
                if (representation == MdfValueRepresentation.Raw) { Assert.Equal(-(2 * 4096 + 7), batch.GetValue(2, 1, 1).SignedValue); }
                else { Assert.Equal(-1.0 + 0.5 * -(2 * 4096 + 7), batch.GetValue(2, 1, 1).FloatingPointValue); }
                Assert.Equal((ulong)((4 * 24 + 23 * 7) % 256), batch.GetValue(4, 0, 23).UnsignedValue);
            }
        }

        private static void Same(MdfSampleValue expected, MdfSampleValue actual)
        {
            Assert.Equal(expected.Kind, actual.Kind);
            if (expected.Kind == MdfValueKind.UnsignedInteger) { Assert.Equal(expected.UnsignedValue, actual.UnsignedValue); }
            else if (expected.Kind == MdfValueKind.SignedInteger) { Assert.Equal(expected.SignedValue, actual.SignedValue); }
            else { Assert.Equal(BitConverter.DoubleToInt64Bits(expected.FloatingPointValue), BitConverter.DoubleToInt64Bits(actual.FloatingPointValue)); }
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(2, 2)]
        [InlineData(3, 1024)]
        public void SubrangesAndBatchesKeepTheElementOffsets(int start, int batchSize)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(Fixture(), false)))
            {
                MdfChannelGroup group = Group(reader);
                int ordinal = start;
                foreach (MdfSampleBatch batch in reader.ReadSamples(group, new[] { Counter, Mat, 7 }, (ulong)start, (ulong)(5 - start), batchSize))
                {
                    for (int row = 0; row < batch.SampleCount; row++)
                    {
                        Assert.Equal(batch.GetValue(row, 2).FloatingPointValue, batch.GetValue(row, 1, 5).FloatingPointValue);
                        Assert.Equal((2.5 * (ordinal + row + 1)) + 0.125 * (ordinal + row), batch.GetValue(row, 1, 5).FloatingPointValue);
                    }
                    ordinal += batch.SampleCount;
                }
                Assert.Equal(5, ordinal);
            }
        }

        [Theory]
        [InlineData("vector", CdVec + 4, 1UL, 2, Vec, Type, false)]
        [InlineData("matrix", CdVec + 4, 2UL, 2, Vec, Type, false)]
        [InlineData("type256", CdVec + 4, 256UL, 2, Vec, Type, false)]
        [InlineData("foreign-dg", CdMat + 8 + 24, 0UL, 4, Mat, Scope, true)]
        [InlineData("foreign-cg", CdMat + 8 + 28, 0UL, 4, Mat, Scope, true)]
        [InlineData("duplicate", CdMat + 8 + 20, (ulong)CnMat00, 4, Mat, Scope, true)]
        [InlineData("self", CdMat + 8 + 8, (ulong)CnMat, 4, Mat, Scope, true)]
        [InlineData("master", CnMat + 24, 1UL, 2, Mat, Master, true)]
        [InlineData("element-master", CnMat01 + 24, 1UL, 2, Mat, Element, true)]
        [InlineData("element-encoding", CnMat01 + 190, 1UL, 2, Mat, Element, true)]
        [InlineData("element-float32", CnMat01 + 190, 2UL, 2, Mat, Element, true)]
        [InlineData("element-conversion", CnMat01 + 8, (ulong)CcLinear, 4, Mat, Element, true)]
        [InlineData("parent-conversion", CnVec + 8, 0UL, 4, Vec, Element, true)]
        [InlineData("parent-width", CnMat + 188, 32UL, 2, Mat, Element, true)]
        [InlineData("element-shift", CnMat01 + 186, 129UL, 2, Mat, Element, true)]
        [InlineData("gap", CnMat01 + 186, 192UL, 2, Mat, Layout, true)]
        public void IneligibleDependenciesStayInspectableButUnsupported(string name, int field, ulong value, int width, int parent, string reason, bool shaped)
        {
            byte[] bytes = Fixture();
            M3Wire.Put(bytes, field, value, width);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = Group(reader);
                MdfChannel channel = group.Channels[parent];
                Assert.Equal(reason, channel.UnsupportedReason);
                Assert.False(channel.CanReadRaw, name);
                Assert.Equal(shaped, channel.Array != null);
                int before = source.ReadCalls;
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { parent }).ToList());
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { Counter, parent }).ToList());
                Assert.Equal(before, source.ReadCalls);
                int other = parent == Mat ? Vec : Mat;
                Assert.True(group.Channels[other].CanReadRaw);
                Assert.Equal(5, reader.ReadSamples(group, new[] { Counter, other, Cube }).Sum(b => b.SampleCount));
            }
        }

        [Fact]
        public void SwappedReferencesAreNotContiguousInReferenceOrder()
        {
            byte[] bytes = Fixture();
            M3Wire.Put(bytes, Reference(0, 2), CnMat01, 4);
            M3Wire.Put(bytes, Reference(1, 2), CnMat00, 4);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                Assert.Equal(Layout, Group(reader).Channels[Mat].UnsupportedReason);
            }
        }

        [Fact]
        public void ADependencyParentsOwnStartIsNotInterpreted()
        {
            byte[] bytes = Fixture();
            M3Wire.Put(bytes, CnMat + 186, 65535, 2); // Far beyond the record: only the elements decide.
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = Group(reader);
                Assert.True(group.Channels[Mat].CanReadRaw);
                MdfSampleBatch batch = reader.ReadSamples(group, new[] { Mat, 2 }).Single();
                Assert.Equal(batch.GetValue(4, 1).FloatingPointValue, batch.GetValue(4, 0, 0).FloatingPointValue);
            }
        }

        [Fact]
        public void ATextParentWithADependencyIsRefusedWithoutReadingIt()
        {
            byte[] bytes = Fixture();
            M3Wire.Put(bytes, CnMat + 190, 7, 2);
            M3Wire.Put(bytes, CdMat, 0x5858, 2); // Not even a CD block: never resolved for a text parent.
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannel channel = Group(reader).Channels[Mat];
                Assert.Equal("Text and byte-array arrays are not supported.", channel.UnsupportedReason);
                Assert.Null(channel.Array);
            }
        }

        [Theory]
        [InlineData("short-references", CdMat + 2, 4 + 4 + 12 * 5, "MDF006")]
        [InlineData("missing-dimension", CdMat + 2, 8 + 12 * 6 + 2, "MDF006")]
        [InlineData("rank", CdMat + 4, 259, "MDF006")]
        [InlineData("zero-dimension", CdMat + 8 + 72, 0, "MDF013")]
        [InlineData("product", CdMat + 8 + 72, 4, "MDF013")]
        [InlineData("count", CdMat + 6, 5, "MDF006")]
        public void MalformedDependencyBlocksFailTheMetadataRead(string name, int field, int value, string code)
        {
            byte[] bytes = Fixture();
            M3Wire.Put(bytes, field, (ulong)value, 2);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                Assert.True(code == Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode, name);
            }
        }

        [Fact]
        public void ASameGroupReferenceToANonChannelIsMdf013()
        {
            byte[] bytes = Fixture();
            M3Wire.Put(bytes, Reference(3, 2), CcLinear, 4);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfFormatException error = Assert.Throws<MdfFormatException>(() => reader.ReadMetadata());
                Assert.Equal("MDF013", error.DiagnosticCode);
            }
        }

        [Fact]
        public void ANumericDependencyLinkToAnotherBlockTypeIsTheResolverError()
        {
            byte[] bytes = Fixture();
            M3Wire.Put(bytes, CnCounter + 16, CcLinear, 4);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                Assert.Equal("MDF005", Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
            }
        }

        private static long MinimumMetadataBudget(byte[] bytes)
        {
            long low = 1, high = 1L << 24; // high succeeds for these small files.
            while (low < high)
            {
                long middle = low + (high - low) / 2;
                try
                {
                    using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false), options: new MdfReaderOptions { MaximumMetadataBytes = middle }))
                    { reader.ReadMetadata(); }
                    high = middle;
                }
                catch (MdfResourceLimitException error)
                {
                    Assert.Equal(nameof(MdfReaderOptions.MaximumMetadataBytes), error.LimitName);
                    low = middle + 1;
                }
            }
            return low;
        }

        [Fact]
        public void TheDependencyAllowanceIsReservedFromTheMetadataBudget()
        {
            byte[] plain = Fixture();
            M3Wire.Put(plain, CnMat + 16, 0, 4);
            M3Wire.Put(plain, CnVec + 16, 0, 4);
            M3Wire.Put(plain, 8908 + 16, 0, 4); // cube parent CN.
            long withDependencies = MinimumMetadataBudget(Fixture());
            long without = MinimumMetadataBudget(plain);
            // Three CDs (6, 4 and 24 references; rank 2, 1, 3) plus the one per-group address index.
            long allowance = (256 + 16 * 6 + 8 * 2) + (256 + 16 * 4 + 8) + (256 + 16 * 24 + 8 * 3) + (64 + 48 * 39);
            Assert.True(withDependencies - without >= allowance, withDependencies + " - " + without);
        }

        [Fact]
        public void BigEndianInterleavedArraysUseTheirOwnElementEncodings()
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(Fixture("m5i-interleaved-be330"), false)))
            {
                IReadOnlyList<MdfDataGroup> catalog = reader.ReadMetadata();
                MdfChannelGroup group = catalog[0].ChannelGroups[0];
                Assert.Equal(new ulong[] { 3 }, group.Channels[1].Array.Dimensions);
                Assert.Equal(new ulong[] { 2, 2 }, group.Channels[5].Array.Dimensions);
                MdfSampleBatch batch = reader.ReadSamples(group, new[] { 5, 1 }).Single();
                Assert.Equal(0xFFFFFFFFUL - 4000 - 1, batch.GetValue(4, 1, 1).UnsignedValue);
                Assert.Equal((ulong)((3 * 257 + 3 * 3) & 0xFFFF), batch.GetValue(3, 0, 3).UnsignedValue);
                Assert.Equal(3, reader.ReadSamples(catalog[0].ChannelGroups[1], new[] { 1 }).Sum(b => b.SampleCount));
            }
        }

        [Fact]
        public void TheRejectedProducerProbeFailsOnItsChannelCounts()
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(Fixture("m5i-asammdf-probe330"), false)))
            {
                MdfFormatException error = Assert.Throws<MdfFormatException>(() => reader.ReadMetadata());
                Assert.Equal("MDF013", error.DiagnosticCode);
                Assert.Equal("CG", error.BlockType);
            }
        }
    }
}
