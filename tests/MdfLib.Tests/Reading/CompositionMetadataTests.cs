// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Reading
{
    public sealed class CompositionMetadataTests
    {
        [Fact]
        public void ScalarsShareTheImmutableEmptyComponentListAndHaveNoParent()
        {
            MdfChannelGroup group = Group(M5EWire.Read());
            Assert.Equal(18, group.Channels.Count);
            Assert.Null(group.Channels[0].Parent);
            Assert.Empty(group.Channels[0].Components);
            Assert.Same(group.Channels[0].Components, group.Channels[1].Components);
            Assert.Same(group.Channels[0].Components, group.Channels[7].Components);
            Assert.Throws<NotSupportedException>(() => ((IList<MdfChannel>)group.Channels[2].Components).Clear());
            // a recognized parent reads its record window as a byte array.
            Assert.True(group.Channels[2].CanReadRaw, group.Channels[2].UnsupportedReason);
            Assert.True(group.Channels[2].CanReadPhysical);
            Assert.Equal(MdfValueKind.ByteArray, group.Channels[2].ValueKind);
            Assert.Null(group.Channels[2].Array);
        }

        [Fact]
        public void LegacyCorporaKeepTheirExactFlatCatalogs()
        {
            foreach (string id in new[] { "m5d-arrays410", "m5d-paired420", "m3-packed410" })
            {
                using (MdfReader reader = MdfReader.Open(new MemoryStream(Fixture(id), false)))
                {
                    foreach (MdfChannelGroup group in reader.ReadMetadata().SelectMany(d => d.ChannelGroups))
                    {
                        Assert.All(group.Channels, c => Assert.Null(c.Parent));
                        Assert.All(group.Channels, c => Assert.Empty(c.Components));
                        Assert.Equal(Enumerable.Range(0, group.Channels.Count), group.Channels.Select(c => c.Index));
                    }
                }
            }
        }

        [Theory]
        [InlineData(3, 1UL, 1)]     // bit offset
        [InlineData(8, 321UL, 4)]   // not a whole number of bytes
        [InlineData(8, 0UL, 4)]     // empty
        [InlineData(0, 1UL, 1)]     // nonzero (VLSD) role
        [InlineData(0, 3UL, 1)]     // virtual-master role
        [InlineData(2, 9UL, 1)]     // non-byte-array type
        public void IneligibleParentShapesStayUnsupportedAndTheirChildChainIsNeverFollowed(int field, ulong value, int width)
        {
            byte[] bytes = M5EWire.Read();
            int packet = M5EWire.Packet(bytes);
            M5EWire.Set(bytes, packet, field, value, width);
            MdfChannelGroup group = Group(bytes);
            Assert.Equal(10, group.Channels.Count);
            Assert.Empty(group.Channels[2].Components);
            Assert.False(group.Channels[2].CanReadRaw);
            Assert.Equal(new[] { 5, 6, 7, 8, 9 }, group.Channels[4].Components.Select(c => c.Index));
            Assert.All(group.Channels.Skip(5), c => Assert.Same(group.Channels[4], c.Parent));
        }

        [Fact]
        public void NumericChannelWithACnCompositionTargetIsNotTreatedAsAStructure()
        {
            byte[] bytes = M5EWire.Read();
            M5EWire.Link(bytes, M5EWire.Top(bytes, 3), 1, (ulong)M5EWire.Top(bytes, 3));
            MdfChannelGroup group = Group(bytes);
            Assert.Equal(18, group.Channels.Count);
            Assert.Empty(group.Channels[3].Components);
            Assert.Contains("composition", group.Channels[3].UnsupportedReason);
        }

        [Theory]
        [InlineData(74UL * 8 - 16 * 8 + 8)]
        [InlineData(0xfffffff8UL)]
        public void ParentBeyondTheRecordDataAreaIsRejectedBeforeAnyComponentRead(ulong bits)
        {
            byte[] bytes = M5EWire.Read();
            int packet = M5EWire.Packet(bytes);
            M5EWire.BitCount(bytes, packet, (uint)bits);
            Failure(bytes, "MDF013", packet);
        }

        [Fact]
        public void ParentOffsetBeyondTheRecordIsRejected()
        {
            byte[] bytes = M5EWire.Read();
            int packet = M5EWire.Packet(bytes);
            M5EWire.ByteOffset(bytes, packet, uint.MaxValue);
            Failure(bytes, "MDF013", packet);
        }

        [Theory]
        [InlineData(0, 15U)]  // counter starts before the parent window
        [InlineData(3, 54U)]  // float32 ends at 58, parent ends at 56
        [InlineData(2, 46U)]  // complete 2x3 UInt16 array ends at 58
        [InlineData(1, 42U)]  // nested structure (16 bytes) ends at 58
        public void ComponentsMustLieInsideTheirImmediateParent(int child, uint byteOffset)
        {
            byte[] bytes = M5EWire.Read();
            int cn = M5EWire.Child(bytes, M5EWire.Packet(bytes), child);
            M5EWire.ByteOffset(bytes, cn, byteOffset);
            Failure(bytes, "MDF013", cn);
        }

        [Fact]
        public void NestedLeafMustLieInsideItsNestedParentNotOnlyTheOuterParent()
        {
            byte[] bytes = M5EWire.Read();
            int packed = M5EWire.Child(bytes, M5EWire.Nested(bytes), 3);
            M5EWire.ByteOffset(bytes, packed, 40); // Inside packet [16,56) but outside nested [24,40).
            Failure(bytes, "MDF013", packed);
        }

        [Fact]
        public void SiblingCycleInsideAComponentChainIsMdf010()
        {
            byte[] bytes = M5EWire.Read();
            int packet = M5EWire.Packet(bytes);
            int last = M5EWire.Child(bytes, packet, 3);
            M5EWire.Link(bytes, last, 0, (ulong)M5EWire.Child(bytes, packet, 0));
            Failure(bytes, "MDF010", M5EWire.Child(bytes, packet, 0));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AncestorCyclesAreMdf010WithoutRecursion(bool root)
        {
            byte[] bytes = M5EWire.Read();
            int nested = M5EWire.Nested(bytes);
            ulong target = root ? (ulong)M5EWire.Packet(bytes) : (ulong)nested;
            M5EWire.Link(bytes, M5EWire.Child(bytes, nested, 3), 0, target);
            Failure(bytes, "MDF010", (int)target);
        }

        [Fact]
        public void AStructureCannotOwnItself()
        {
            byte[] bytes = M5EWire.Read();
            int nested = M5EWire.Nested(bytes);
            M5EWire.Link(bytes, nested, 1, (ulong)nested);
            Failure(bytes, "MDF010", nested);
        }

        [Fact]
        public void TwoParentsCannotShareAComponent()
        {
            byte[] bytes = M5EWire.Read();
            int counter = M5EWire.Child(bytes, M5EWire.Packet(bytes), 0);
            int other = M5EWire.Top(bytes, 4);
            M5EWire.Link(bytes, other, 1, (ulong)counter);
            M5EWire.ByteOffset(bytes, other, 16);
            M5EWire.BitCount(bytes, other, 58 * 8 - 16 * 8);
            Failure(bytes, "MDF014", counter);
        }

        [Fact]
        public void AComponentCannotAlsoBeATopLevelChannel()
        {
            byte[] bytes = M5EWire.Read();
            int last = M5EWire.Child(bytes, M5EWire.Packet(bytes), 3);
            M5EWire.Link(bytes, last, 0, (ulong)M5EWire.Top(bytes, 1));
            Failure(bytes, "MDF014", M5EWire.Top(bytes, 1));
        }

        [Fact]
        public void AComponentCannotBelongToAnotherChannelGroup()
        {
            byte[] bytes = Fixture("m5e-interleaved410");
            int cg = M5DWire.Cg(bytes);
            int neighborGroup = (int)M3Wire.U64(bytes, M3Wire.Link(cg, 0));
            int neighbor = (int)M3Wire.U64(bytes, M3Wire.Link(neighborGroup, 1));
            int last = M5EWire.Child(bytes, M5EWire.Packet(bytes), 3);
            M5EWire.Link(bytes, last, 0, (ulong)neighbor);
            M5EWire.ByteOffset(bytes, neighbor, 16);
            Failure(bytes, "MDF014", neighbor);
        }

        [Theory]
        [InlineData(1U, 0)]
        [InlineData(2U, 0)]
        [InlineData(0x200U, 0)]
        [InlineData(0U, 5)]
        public void ParentInvalidityFlagsOrChannelDataMakeDescendantsUnsupportedButStillInspectable(uint flags, int link)
        {
            byte[] bytes = M5EWire.Read();
            int packet = M5EWire.Packet(bytes);
            M5EWire.Flags(bytes, packet, flags);
            if (link != 0) { M5EWire.Link(bytes, packet, link, (ulong)M5EWire.Top(bytes, 0)); }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(18, group.Channels.Count);
                Assert.Equal(new[] { 5, 6, 11, 12 }, group.Channels[2].Components.Select(c => c.Index));
                for (int index = 5; index <= 12; index++)
                {
                    if (index == 6) { continue; }
                    Assert.False(group.Channels[index].CanReadRaw);
                    Assert.Contains("ancestor", group.Channels[index].UnsupportedReason);
                    Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { index }));
                }
                Assert.All(group.Channels.Skip(13), c => Assert.True(c.CanReadRaw, c.UnsupportedReason));
                Assert.Equal(5, reader.ReadSamples(group, new[] { 13, 0, 3 }).Sum(b => b.SampleCount));
            }
        }

        [Fact]
        public void NestedParentBlockingAffectsOnlyItsOwnDescendants()
        {
            byte[] bytes = M5EWire.Read();
            M5EWire.Flags(bytes, M5EWire.Nested(bytes), 1);
            MdfChannelGroup group = Group(bytes);
            Assert.All(new[] { 7, 8, 9, 10 }, i => Assert.Contains("ancestor", group.Channels[i].UnsupportedReason));
            Assert.All(new[] { 5, 11, 12 }, i => Assert.True(group.Channels[i].CanReadRaw));
        }

        [Fact]
        public void StoredMasterInsideAStructureIsUnsupportedAndDoesNotBecomeTheGroupMaster()
        {
            byte[] bytes = M5EWire.Read();
            int counter = M5EWire.Child(bytes, M5EWire.Packet(bytes), 0);
            M5EWire.Set(bytes, counter, 0, 2, 1);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.True(group.Channels[5].IsMaster);
                Assert.Contains("master", group.Channels[5].UnsupportedReason);
                Assert.Equal(0.25, reader.ReadSamples(group, new[] { 0 }, 1, 1).Single().GetValue(0, 0).FloatingPointValue);
            }
        }

        [Fact]
        public void ComponentDepthIncludesCachedSourceTextOfTheDeepestLeaves()
        {
            byte[] bytes = M5EWire.Read();
            int signed = M5EWire.Child(bytes, M5EWire.Nested(bytes), 0);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false), options: new MdfReaderOptions { MaximumTraversalDepth = 6 }))
            {
                MdfResourceLimitException error = Assert.Throws<MdfResourceLimitException>(() => reader.ReadMetadata());
                Assert.Equal("MaximumTraversalDepth", error.LimitName);
                Assert.Equal(M3Wire.U64(bytes, M3Wire.Link(signed, 3)), error.Offset);
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false), options: new MdfReaderOptions { MaximumTraversalDepth = 7 }))
            { Assert.Equal(18, reader.ReadMetadata()[0].ChannelGroups[0].Channels.Count); }
        }

        [Theory]
        [InlineData(200, true)]
        [InlineData(260, false)]
        public void DeepNestingIsIterativeAndBoundedByTraversalDepth(int levels, bool succeeds)
        {
            byte[] bytes = M5EWire.Read();
            int nested = M5EWire.Nested(bytes);
            ulong next = (ulong)M5EWire.AddCn(ref bytes, 0, 0, 0, 24, 8);
            for (int level = 0; level < levels; level++) { next = (ulong)M5EWire.AddCn(ref bytes, 0, next, 10, 24, 16); }
            M5EWire.Link(bytes, nested, 1, next);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                if (!succeeds)
                {
                    Assert.Equal("MaximumTraversalDepth", Assert.Throws<MdfResourceLimitException>(() => reader.ReadMetadata()).LimitName);
                    return;
                }
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(14 + levels + 1, group.Channels.Count);
                MdfChannel leaf = group.Channels.Single(c => c.ValueKind == MdfValueKind.UnsignedInteger && c.BitCount == 8 && c.Parent != null && c.Parent.Parent != null && c.Parent.Parent.Parent != null);
                int depth = 0;
                for (MdfChannel parent = leaf.Parent; parent != null; parent = parent.Parent) { depth++; }
                Assert.Equal(levels + 2, depth);
                Assert.Equal(new ulong[] { 0, 255, 0, 1, 255 }, reader.ReadSamples(group, new[] { leaf.Index }).SelectMany(b => Enumerable.Range(0, b.SampleCount).Select(r => b.GetValue(r, 0).UnsignedValue)));
            }
        }

        [Fact]
        public void ThousandsOfSiblingComponentsKeepWireOrderAndSelectableIndices()
        {
            byte[] bytes = M5EWire.Read();
            int nested = M5EWire.Nested(bytes);
            ulong next = M3Wire.U64(bytes, M3Wire.Link(nested, 1));
            for (int i = 0; i < 3000; i++) { next = (ulong)M5EWire.AddCn(ref bytes, next, 0, 0, 24, 8); }
            M5EWire.Link(bytes, nested, 1, next);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(3018, group.Channels.Count);
                MdfChannel parent = group.Channels[6];
                Assert.Equal(3004, parent.Components.Count);
                Assert.Equal(Enumerable.Range(7, 3004), parent.Components.Select(c => c.Index));
                Assert.Equal("signed", group.Channels[3007].Name);
                Assert.Equal("matrix", group.Channels[3011].Name);
                MdfSampleBatch batch = reader.ReadSamples(group, new[] { 3006, 7, 3007 }).Single();
                Assert.Equal(255UL, batch.GetValue(1, 0).UnsignedValue);
                Assert.Equal(255UL, batch.GetValue(1, 1).UnsignedValue);
                Assert.Equal(-1L, batch.GetValue(1, 2).SignedValue);
            }
        }

        [Fact]
        public void EachComponentCostsExactlyItsDocumentedRelationshipAllowanceMoreThanATopLevelChannel()
        {
            // AddCn replaces the array: append first, then link inside the resized copy.
            byte[] top = M5EWire.Read();
            int other = M5EWire.Top(top, 4);
            int extraTop = M5EWire.AddCn(ref top, 0, 0, 0, 0, 8);
            M5EWire.Link(top, other, 0, (ulong)extraTop);
            byte[] child = M5EWire.Read();
            int packed = M5EWire.Child(child, M5EWire.Nested(child), 3);
            int extraChild = M5EWire.AddCn(ref child, 0, 0, 0, 24, 8);
            M5EWire.Link(child, packed, 0, (ulong)extraChild);
            Assert.Equal(top.Length, child.Length);
            Assert.Equal(19, Group(top).Channels.Count);
            Assert.Equal(19, Group(child).Channels.Count);
            Assert.Equal(new[] { 7, 8, 9, 10, 11 }, Group(child).Channels[6].Components.Select(c => c.Index));
            Assert.Equal(MinimumMetadataBudget(top) + 64, MinimumMetadataBudget(child));
        }

        [Fact]
        public void MetadataBudgetRefusesBeforeTraversingTheRestOfAHugeComponentChain()
        {
            byte[] bytes = M5EWire.Read();
            int nested = M5EWire.Nested(bytes);
            ulong next = M3Wire.U64(bytes, M3Wire.Link(nested, 1));
            int first = 0;
            for (int i = 0; i < 2000; i++) { next = (ulong)M5EWire.AddCn(ref bytes, next, 0, 0, 24, 8); if (i == 0) { first = (int)next; } }
            M5EWire.Link(bytes, nested, 1, next);
            long budget = MinimumMetadataBudget(M5EWire.Read()) + 100_000;
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true, new MdfReaderOptions { MaximumMetadataBytes = budget }))
            {
                Assert.Equal("MaximumMetadataBytes", Assert.Throws<MdfResourceLimitException>(() => reader.ReadMetadata()).LimitName);
                Assert.DoesNotContain(source.ReadRanges, r => r.Item1 <= first && first < r.Item1 + r.Item2);
            }
        }

        [Fact]
        public void MetadataNeverReadsRecordBytes()
        {
            byte[] bytes = M5EWire.Read();
            int root = M5DWire.Root(bytes);
            long end = root + (long)M3Wire.U64(bytes, root + 8);
            using (TestReadStream source = new TestReadStream(bytes) { MaximumBytesPerRead = 5 })
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                Assert.Equal(18, reader.ReadMetadata()[0].ChannelGroups[0].Channels.Count);
                Assert.DoesNotContain(source.ReadRanges, r => r.Item1 < end && r.Item1 + r.Item2 > root + 24);
                Assert.Equal(0, source.WriteCalls);
            }
        }

        [Fact]
        public void CancellationDuringComponentTraversalPublishesNothingAndARetrySucceeds()
        {
            byte[] bytes = M5EWire.Read();
            int nested = M5EWire.Nested(bytes);
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                source.AfterRead = () => { if (source.ReadRanges.Any(r => r.Item1 <= nested && nested < r.Item1 + r.Item2)) { cancellation.Cancel(); } };
                Assert.ThrowsAny<OperationCanceledException>(() => reader.ReadMetadata(cancellation.Token));
                source.AfterRead = null;
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(new[] { 7, 8, 9, 10 }, group.Channels[6].Components.Select(c => c.Index));
            }
        }

        [Fact]
        public void ReadmeStructureExampleSelectsOnlyReadableImmediateComponents()
        {
            // Mirrors README "MDF4: fixed CN structure components" with assertions instead of console output.
            using (MdfReader reader = MdfReader.Open(new MemoryStream(M5EWire.Read(), false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                List<int[]> selections = new List<int[]>();
                foreach (MdfChannel channel in group.Channels)
                {
                    if (channel.Parent != null || channel.Components.Count == 0) { continue; }
                    int[] leaves = channel.Components.Where(c => c.CanReadRaw).Select(c => c.Index).ToArray();
                    if (leaves.Length == 0) { continue; }
                    selections.Add(leaves);
                    MdfSampleBatch batch = reader.ReadSamples(group, leaves, sampleCount: Math.Min(group.SampleCount, 10UL)).Single();
                    Assert.Equal(5, batch.SampleCount);
                    Assert.Equal(leaves, batch.Channels.Select(c => c.Index));
                }
                Assert.Equal(new[] { 5, 6, 11, 12 }, selections[0]); // the nested parent is readable too
                Assert.Equal(new[] { 13, 14, 15, 16, 17 }, selections[1]);
                Assert.Equal(2, selections.Count);
            }
        }

        private static long MinimumMetadataBudget(byte[] bytes)
        {
            long low = 1, high = 67_108_864;
            while (low < high)
            {
                long middle = low + (high - low) / 2;
                bool ok;
                try
                {
                    using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false), options: new MdfReaderOptions { MaximumMetadataBytes = middle }))
                    { reader.ReadMetadata(); ok = true; }
                }
                catch (MdfResourceLimitException error) when (error.LimitName == "MaximumMetadataBytes") { ok = false; }
                if (ok) { high = middle; } else { low = middle + 1; }
            }
            return low;
        }

        private static byte[] Fixture(string id) { return MdfLib.TestSupport.FixtureCorpus.Read(id); }

        private static MdfChannelGroup Group(byte[] bytes)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            { return reader.ReadMetadata()[0].ChannelGroups[0]; }
        }

        private static void Failure(byte[] bytes, string code, int offset)
        {
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfFormatException first = Assert.Throws<MdfFormatException>(() => reader.ReadMetadata());
                Assert.Equal(code, first.DiagnosticCode);
                Assert.Equal((ulong)offset, first.Offset);
                Assert.Equal(code, Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
            }
        }
    }
}
