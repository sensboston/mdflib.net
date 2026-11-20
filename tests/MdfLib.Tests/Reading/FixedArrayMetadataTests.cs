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
    public sealed class FixedArrayMetadataTests
    {
        [Theory]
        [InlineData("m5d-arrays410")]
        [InlineData("m5d-lists411")]
        [InlineData("m5d-arrays420")]
        [InlineData("m5d-paired420")]
        [InlineData("m5d-interleaved410")]
        [InlineData("m5d-interleaved411")]
        [InlineData("m5d-asammdf410")]
        [InlineData("m5d-asammdf411")]
        [InlineData("m5d-asammdf420")]
        public void EveryFrozenCatalogIsHeaderOnlyAndKeepsLogicalChannelsAndExactShapes(string id)
        {
            byte[] bytes = M5DWire.Read(id);
            List<Tuple<long, long>> bodies = DataBlocks(bytes);
            Assert.NotEmpty(bodies);
            MdfChannelGroup snapshot;
            using (TestReadStream source = new TestReadStream(bytes) { MaximumBytesPerRead = 3 })
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                snapshot = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(24, snapshot.Channels.Count);
                Assert.Null(snapshot.Channels[0].Array);
                Assert.Null(snapshot.Channels[1].Array);
                Assert.Equal(new ulong[] { 2, 3 }, snapshot.Channels[2].Array.Dimensions);
                Assert.Equal(new ulong[] { 1, 2, 2 }, snapshot.Channels[6].Array.Dimensions);
                Assert.Equal(new ulong[] { 2, 2, 2 }, snapshot.Channels[8].Array.Dimensions);
                Assert.Equal(new ulong[] { 1, 1, 1 }, snapshot.Channels[23].Array.Dimensions);
                Assert.All(snapshot.Channels, c => Assert.True(c.CanReadRaw, c.UnsupportedReason));
                foreach (Tuple<long, long> body in bodies)
                { Assert.DoesNotContain(source.ReadRanges, r => r.Item1 < body.Item2 && r.Item1 + r.Item2 > body.Item1); }
                Assert.Equal(0, source.WriteCalls);
                Assert.Equal(0, source.FlushCalls);
                int before = source.ReadCalls;
                Assert.Same(snapshot, reader.ReadMetadata()[0].ChannelGroups[0]);
                Assert.Equal(before, source.ReadCalls);
            }
            Assert.Equal(8UL, snapshot.Channels[8].Array.ElementCount);
            Assert.Throws<NotSupportedException>(() => ((IList<ulong>)snapshot.Channels[2].Array.Dimensions).Clear());
        }

        [Theory]
        [InlineData(2, 0UL, 2, "MDF013")]
        [InlineData(2, 65535UL, 2, "MDF006")]
        [InlineData(16, 0UL, 8, "MDF013")]
        [InlineData(16, ulong.MaxValue, 8, "MDF013")]
        public void InvalidDimensionsCannotOverflowOrAllocateByProduct(int field, ulong value, int width, string code)
        {
            byte[] bytes = M5DWire.Read();
            M3Wire.Put(bytes, M3Wire.Payload(bytes, M5DWire.Ca(bytes)) + field, value, width);
            Failure(bytes, code);
        }

        [Theory]
        [InlineData(47UL)]
        [InlineData(48UL)]
        [InlineData(55UL)]
        [InlineData(63UL)]
        public void PrefixAndDimensionsMustFitTheDeclaredCaPayload(ulong length)
        {
            byte[] bytes = M5DWire.Read();
            M3Wire.Put(bytes, M5DWire.Ca(bytes) + 8, length);
            Failure(bytes, "MDF006");
        }

        [Theory]
        [InlineData(0)]
        [InlineData(2)]
        public void CanonicalCaDoesNotGuessMissingOrExtraLinks(int links)
        {
            byte[] bytes = M5DWire.Read();
            M5DWire.Array(ref bytes, new ulong[] { 2, 3 }, links: links);
            Failure(bytes, "MDF006");
        }

        [Fact]
        public void ExtraCanonicalPayloadIsNotSilentlyAcceptedAsPadding()
        {
            byte[] bytes = M5DWire.Read();
            int ca = M5DWire.Array(ref bytes, new ulong[] { 2, 3 });
            Array.Resize(ref bytes, bytes.Length + 8);
            M3Wire.Put(bytes, ca + 8, M3Wire.U64(bytes, ca + 8) + 8);
            Failure(bytes, "MDF006");
        }

        [Theory]
        [InlineData(0, 1UL, 1)]
        [InlineData(1, 1UL, 1)]
        [InlineData(4, 1UL, 4)]
        [InlineData(4, 0x41UL, 4)]    // inverse layout combined with dynamic size; accepts 0x40 alone
        [InlineData(4, 0x80000000UL, 4)]
        [InlineData(8, 0UL, 4)]
        [InlineData(8, 0xffffffffUL, 4)]
        // padded strides (8, 2), inverse layout (4, 0x40) and per-element invalidation (12, 1) became supported.
        // an axis flag (4, 16) without its declared links is now MDF006 (AxisFlagWithoutItsLinksIsMalformed).
        public void ExtendedArraysRemainInspectableButNeverFallBackToFirstScalar(int field, ulong value, int width)
        {
            byte[] bytes = M5DWire.Read();
            M3Wire.Put(bytes, M3Wire.Payload(bytes, M5DWire.Ca(bytes)) + field, value, width);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.NotNull(group.Channels[2].Array);
                Assert.Equal(6UL, group.Channels[2].Array.ElementCount);
                Assert.False(group.Channels[2].CanReadRaw);
                Assert.False(group.Channels[2].CanReadPhysical);
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 2 }));
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 2 }, sampleCount: 0));
                Assert.Equal(5, reader.ReadSamples(group, new[] { 1 }).Sum(b => b.SampleCount));
            }
        }

        [Fact]
        public void AxisFlagWithoutItsLinksIsMalformed()
        {
            byte[] bytes = M5DWire.Read();
            M3Wire.Put(bytes, M3Wire.Payload(bytes, M5DWire.Ca(bytes)) + 4, 16UL, 4);
            Failure(bytes, "MDF006");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void NonzeroCompositionLinkIsUnsupportedWithoutRecursionEvenWhenItPointsToItself(bool self)
        {
            byte[] bytes = M5DWire.Read();
            int ca = M5DWire.Ca(bytes);
            M3Wire.Put(bytes, M3Wire.Link(ca, 0), (ulong)(self ? ca : M5DWire.Cn(bytes, 2)));
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannel channel = reader.ReadMetadata()[0].ChannelGroups[0].Channels[2];
                Assert.NotNull(channel.Array);
                Assert.False(channel.CanReadRaw);
                Assert.Contains("composition", channel.UnsupportedReason);
            }
        }

        [Theory]
        [InlineData(0, 2UL, 1)]
        [InlineData(2, 17UL, 1)]  // a fixed Latin-1 array (6) is readable; an unknown encoding is not
        [InlineData(3, 1UL, 1)]
        [InlineData(8, 12UL, 4)]   // a 7-bit element in a 1-byte container is now readable; 12 bits need 2 > stride 1
        [InlineData(8, 24UL, 4)]
        public void ArrayMastersNonnumericAndPackedElementsAreExplicitlyUnsupported(int field, ulong value, int width)
        {
            byte[] bytes = M5DWire.Read();
            M3Wire.Put(bytes, M3Wire.Payload(bytes, M5DWire.Cn(bytes)) + field, value, width);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.NotNull(group.Channels[2].Array);
                Assert.False(group.Channels[2].CanReadRaw);
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 2 }));
            }
        }

        [Fact]
        public void FixedLatin1ElementsOfAnArrayAreReadable()
        {
            byte[] bytes = M5DWire.Read();
            M3Wire.Put(bytes, M3Wire.Payload(bytes, M5DWire.Cn(bytes)) + 2, 6, 1);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                MdfChannel channel = group.Channels[2];
                if (!channel.CanReadRaw) { Assert.NotNull(channel.UnsupportedReason); return; } // a non-whole-byte width stays refused
                Assert.Equal(MdfValueKind.Text, channel.ValueKind);
                MdfSampleBatch batch = reader.ReadSamples(group, new[] { 2 }).First();
                for (ulong element = 0; element < channel.Array.ElementCount; element++)
                { Assert.Equal(MdfValueKind.Text, batch.GetValue(0, 0, element).Kind); }
            }
        }

        [Theory]
        [InlineData(561UL)]
        [InlineData(562UL)]
        [InlineData(577UL)]
        [InlineData(uint.MaxValue)]
        public void WholeArrayMustFitDataAreaNotMerelyItsFirstElement(ulong offset)
        {
            byte[] bytes = M5DWire.Read();
            // u64 has eight elements/64bytes: these offsets leave less than64 data bytes.
            M3Wire.Put(bytes, M3Wire.Payload(bytes, M5DWire.Cn(bytes, 8)) + 4, offset, 4);
            Failure(bytes, "MDF013");
        }

        [Fact]
        public void SharedCaProducesBoundedIndependentImmutableSnapshotsNotFalseOwnershipCycles()
        {
            byte[] bytes = M5DWire.Read();
            M3Wire.Put(bytes, M3Wire.Link(M5DWire.Cn(bytes, 3), 1), (ulong)M5DWire.Ca(bytes));
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(group.Channels[2].Array.Dimensions, group.Channels[3].Array.Dimensions);
                Assert.NotSame(group.Channels[2].Array, group.Channels[3].Array);
                Assert.True(group.Channels[3].CanReadRaw);
                Assert.Equal(5, reader.ReadSamples(group, new[] { 2, 3 }).Sum(b => b.SampleCount));
            }
        }

        [Theory]
        [InlineData(5000)]
        [InlineData(65535)]
        public void ManySingletonDimensionsUseBoundedShapeStorageRatherThanElementExpansion(int dimensions)
        {
            byte[] bytes = M5DWire.Read();
            M5DWire.Array(ref bytes, Enumerable.Repeat(1UL, dimensions).ToArray());
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(dimensions, group.Channels[2].Array.Dimensions.Count);
                Assert.Equal(1UL, group.Channels[2].Array.ElementCount);
                Assert.Equal(0UL, reader.ReadSamples(group, new[] { 2 }, sampleCount: 1).Single().GetValue(0, 0, 0).UnsignedValue);
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ShapeReadAndMetadataBudgetsRefuseBeforeOversizedShapePayloadRead(bool readCap)
        {
            byte[] bytes = M5DWire.Read();
            int ca = M5DWire.Array(ref bytes, Enumerable.Repeat(1UL, 5000).ToArray());
            MdfReaderOptions options = new MdfReaderOptions();
            if (readCap) { options.MaximumReadBufferBytes = 1024; }
            else { options.MaximumMetadataBytes = 50000; }
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true, options))
            {
                MdfResourceLimitException error = Assert.Throws<MdfResourceLimitException>(() => reader.ReadMetadata());
                Assert.Equal(readCap ? "MaximumReadBufferBytes" : "MaximumMetadataBytes", error.LimitName);
                Assert.Equal((ulong)ca, error.Offset);
                Assert.DoesNotContain(source.ReadRanges, r => r.Item1 == ca + 32 && r.Item2 == 40016);
            }
        }

        [Fact]
        public void CaAddsOneHierarchyLevelNotOneLevelPerDimension()
        {
            byte[] bytes = M5DWire.Read();
            for (int ci = 0; ci < 24; ci++)
            {
                int cn = M5DWire.Cn(bytes, ci);
                foreach (int link in new[] { 2, 3, 4, 6, 7 }) { M3Wire.Put(bytes, M3Wire.Link(cn, link), 0); }
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false), options: new MdfReaderOptions { MaximumTraversalDepth = 3 }))
            {
                MdfResourceLimitException error = Assert.Throws<MdfResourceLimitException>(() => reader.ReadMetadata());
                Assert.Equal("MaximumTraversalDepth", error.LimitName);
                Assert.Equal((ulong)M5DWire.Ca(bytes), error.Offset);
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false), options: new MdfReaderOptions { MaximumTraversalDepth = 4 }))
            { Assert.Equal(24, reader.ReadMetadata()[0].ChannelGroups[0].Channels.Count); }
        }

        private static void Failure(byte[] bytes, string code)
        {
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfFormatException first = Assert.Throws<MdfFormatException>(() => reader.ReadMetadata());
                Assert.Equal(code, first.DiagnosticCode);
                Assert.Equal(code, Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
                Assert.Equal(0, source.WriteCalls);
            }
        }

        private static List<Tuple<long, long>> DataBlocks(byte[] bytes)
        {
            // Literal storage-link traversal, not a linear scan: independent
            // producers may leave unallocated gaps between valid blocks.
            List<Tuple<long, long>> result = new List<Tuple<long, long>>();
            Stack<ulong> pending = new Stack<ulong>();
            HashSet<ulong> seen = new HashSet<ulong>();
            for (int dg = M5DWire.Dg(bytes); dg != 0; dg = (int)M3Wire.U64(bytes, M3Wire.Link(dg, 0)))
            { pending.Push(M3Wire.U64(bytes, M3Wire.Link(dg, 2))); }
            while (pending.Count != 0)
            {
                ulong p = pending.Pop();
                if (p == 0 || !seen.Add(p)) { continue; }
                Assert.Equal((byte)'#', bytes[(int)p]);
                ulong length = M3Wire.U64(bytes, (int)p + 8);
                Assert.InRange(length, 24UL, (ulong)bytes.LongLength - p);
                string type = System.Text.Encoding.ASCII.GetString(bytes, (int)p + 2, 2);
                if (type == "DT" || type == "DV" || type == "DI" || type == "DZ")
                { result.Add(Tuple.Create((long)p, (long)(p + length))); }
                else
                {
                    Assert.Contains(type, new[] { "DL", "HL", "LD" });
                    int count = (int)M3Wire.U64(bytes, (int)p + 16);
                    for (int link = 0; link < count; link++)
                    { pending.Push(M3Wire.U64(bytes, M3Wire.Link((int)p, link))); }
                }
            }
            return result;
        }
    }
}
