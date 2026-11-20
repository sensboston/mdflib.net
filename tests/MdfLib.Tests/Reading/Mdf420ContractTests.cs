// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MdfLib.Internal;
using MdfLib.Reading;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Reading
{
    public sealed class Mdf420ContractTests
    {
        [Theory]
        [InlineData("m5c-ld-equal420")]
        [InlineData("m5c-ld-offset420")]
        [InlineData("m5c-ld-chain420")]
        [InlineData("m5c-ld-null420")]
        [InlineData("m5c-ld-noinval420")]
        [InlineData("m5c-ld-remote420")]
        [InlineData("m5c-ld-plain-offset420")]
        [InlineData("m5c-ld-plain-chain420")]
        [InlineData("m5c-ld-single420")]
        [InlineData("m5c-ld-zsingle420")]
        [InlineData("m5c-ld-tsingle420")]
        [InlineData("m5c-asammdf-row420")]
        [InlineData("m5c-asammdf-ld420")]
        [InlineData("m5c-asammdf-zld420")]
        [InlineData("m5c-asammdf-transpose420")]
        public void EveryFrozenLdCatalogBuildsBothCompleteMapsWithoutReadingAnyRawOrCompressedSampleBody(string id)
        {
            byte[] bytes = M5CWire.Read(id);
            List<Tuple<long, long>> bodies = DataBodyRanges(bytes);
            Assert.NotEmpty(bodies);
            using (TestReadStream source = new TestReadStream(bytes) { MaximumBytesPerRead = 3 })
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                foreach (MdfDataGroup dg in reader.ReadMetadata())
                {
                    MdfChannelGroup group = Assert.Single(dg.ChannelGroups);
                    Mdf4ListDataMap map = Mdf4ListDataMap.Build(reader.Resolver, reader.Limits, new ReadBudget(reader.Limits),
                        group.Layout.DataAddress, group.SampleCount, group.Layout.DataBytes, group.Layout.InvalidationBytes, default(CancellationToken));
                    Assert.Equal(group.SampleCount * group.Layout.DataBytes, map.Values.Segments.Aggregate(0UL, (total, segment) => total + segment.Length));
                    Assert.Equal(group.SampleCount * group.Layout.InvalidationBytes, map.Invalidations.Segments.Aggregate(0UL, (total, segment) => total + segment.Length));
                    Assert.Equal(2, map.Values.MaximumDepth);
                    Assert.Equal(2, map.Invalidations.MaximumDepth);
                    Assert.Null(group.Layout.ListDataMap); // A local internal build does not itself publish either map on the reader.
                    Assert.Null(group.Layout.DataMap);
                }
                // The independent literal wire reader below identifies the physical leaf bodies, not the tested map.
                foreach (Tuple<long, long> body in bodies)
                { Assert.DoesNotContain(source.ReadRanges, range => range.Item1 < body.Item2 && range.Item1 + range.Item2 > body.Item1); }
                Assert.Equal(0, source.WriteCalls);
                Assert.Equal(0, source.FlushCalls);
            }
        }

        [Theory]
        [InlineData("m5c-ld-remote420")]
        [InlineData("m5c-asammdf-ld420")]
        public void SupportedForwardMasterReferencesResolveOnlyAfterTheEntireReorderedCatalogWithoutImplicitStorage(string id)
        {
            byte[] bytes = M5CWire.Read(id);
            List<int> groups = new List<int>();
            for (int address = M5CWire.Dg(bytes); address != 0; address = (int)M3Wire.U64(bytes, M3Wire.Link(address, 0)))
            { groups.Add(address); }
            int masterRoot = M5CWire.Root(bytes);
            long masterRootEnd = masterRoot + (long)M3Wire.U64(bytes, masterRoot + 8);
            M3Wire.Put(bytes, M3Wire.Link(64, 0), (ulong)groups[1]);
            for (int index = 1; index < groups.Count; index++)
            { M3Wire.Put(bytes, M3Wire.Link(groups[index], 0), (ulong)groups[index + 1 < groups.Count ? index + 1 : 0]); }
            M3Wire.Put(bytes, M3Wire.Link(groups[0], 0), 0); // Stored master is now last, not already available when remotes are parsed.
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                IReadOnlyList<MdfDataGroup> catalog = reader.ReadMetadata();
                Assert.Equal(groups.Count, catalog.Count);
                Assert.All(catalog, dg => Assert.True(Assert.Single(dg.ChannelGroups).CanReadSamples));
                MdfChannelGroup signal = catalog[0].ChannelGroups[0];
                MdfChannelGroup master = catalog[catalog.Count - 1].ChannelGroups[0];
                Assert.All(signal.Channels, channel => Assert.False(channel.IsMaster));
                Assert.True(Assert.Single(master.Channels).IsMaster);
                int before = source.ReadRanges.Count;
                Assert.Equal(9007199254740993UL, reader.ReadSamples(signal, new[] { 0 }, 1, 1).Single().GetValue(0, 0).UnsignedValue);
                Assert.Null(master.Layout.ListDataMap);
                Assert.Null(master.Layout.DataMap);
                Assert.DoesNotContain(source.ReadRanges.Skip(before), range => range.Item1 < masterRootEnd && range.Item1 + range.Item2 > masterRoot);
                Assert.Equal(id == "m5c-ld-remote420" ? 0.0 : .125, reader.ReadSamples(master, new[] { 0 }, 1, 1).Single().GetValue(0, 0).FloatingPointValue);
                Assert.NotNull(master.Layout.ListDataMap);
            }
        }

        [Theory]
        [InlineData(2U, false)]          // narrowed: a summary bit without its array is a shape error, not a refusal.
        [InlineData(4U, false)]
        [InlineData(8U, false)]
        [InlineData(16U, true)]
        [InlineData(0x40000000U, true)]
        [InlineData(0x80000002U, false)]
        [InlineData(uint.MaxValue, true)]
        public void EveryUnknownLowOrHighFlagIsUnsupportedBeforeEitherStreamMapOrAnySampleBody(uint unknownFlags, bool unsupported)
        {
            byte[] bytes = M5CWire.Read();
            List<Tuple<long, long>> bodies = DataBodyRanges(bytes);
            int fields = M3Wire.Payload(bytes, M5CWire.Root(bytes));
            M3Wire.Put(bytes, fields, unknownFlags | 0x80000001U, 4); // Preserve DI role/equal bits and the independent UInt32 count at+4.
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                int before = source.ReadCalls;
                Assert.Empty(reader.ReadSamples(group, new[] { 1 }, sampleCount: 0));
                Assert.Equal(before, source.ReadCalls);
                if (unsupported) { Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 1 }, sampleCount: 1).ToList()); }
                else
                {
                    Assert.Equal("MDF006", Assert.Throws<MdfFormatException>(
                        () => reader.ReadSamples(group, new[] { 1 }, sampleCount: 1).ToList()).DiagnosticCode);
                }
                Assert.Null(group.Layout.ListDataMap);
                Assert.Null(group.Layout.DataMap);
                foreach (Tuple<long, long> body in bodies)
                { Assert.DoesNotContain(source.ReadRanges, range => range.Item1 < body.Item2 && range.Item1 + range.Item2 > body.Item1); }
            }
        }

        [Fact]
        public void FiveThousandForwardRemoteGroupsUseCatalogIdentityWithoutRecursiveTraversalOrMasterMapPublication()
        {
            byte[] bytes = M5CWire.Read("m5c-ld-remote420");
            int masterDg = M5CWire.Dg(bytes), masterCg = M5CWire.Cg(bytes), masterRoot = M5CWire.Root(bytes);
            int masterCn = (int)M3Wire.U64(bytes, M3Wire.Link(masterCg, 1));
            M3Wire.Put(bytes, M3Wire.Link(masterCn, 2), 0); // No nonzero depth4 name; all metadata fits hierarchy depth3.
            const int count = 5000, size = 336;
            int first = (bytes.Length + 7) & ~7;
            Array.Resize(ref bytes, first + count * size);
            for (int index = 0; index < count; index++)
            {
                int dg = first + index * size, cg = dg + 64, cn = cg + 112;
                Array.Copy(M5Wire.Header("DG", 40), 0, bytes, dg, 24);
                M3Wire.Put(bytes, dg + 16, 4);
                M3Wire.Put(bytes, M3Wire.Link(dg, 0), (ulong)(index + 1 == count ? masterDg : dg + size));
                M3Wire.Put(bytes, M3Wire.Link(dg, 1), (ulong)cg);
                M3Wire.Put(bytes, M3Wire.Link(dg, 2), (ulong)masterRoot); // Legal shared data leaf extents, not structural ownership sharing.
                Array.Copy(M5Wire.Header("CG", 88), 0, bytes, cg, 24);
                M3Wire.Put(bytes, cg + 16, 7);
                M3Wire.Put(bytes, M3Wire.Link(cg, 1), (ulong)cn);
                M3Wire.Put(bytes, M3Wire.Link(cg, 6), (ulong)masterCg);
                M3Wire.Put(bytes, cg + 88, 7);
                M3Wire.Put(bytes, cg + 96, 8, 2);
                M3Wire.Put(bytes, cg + 104, 8, 4);
                Array.Copy(M5Wire.Header("CN", 136), 0, bytes, cn, 24);
                M3Wire.Put(bytes, cn + 16, 8);
                M3Wire.Put(bytes, cn + 96, 8, 4); // Own stored unsigned8 value, not a virtual/stored master CN.
            }
            M3Wire.Put(bytes, M3Wire.Link(64, 0), (ulong)first);
            M3Wire.Put(bytes, M3Wire.Link(masterDg, 0), 0);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true, new MdfReaderOptions { MaximumTraversalDepth = 3 }))
            {
                IReadOnlyList<MdfDataGroup> catalog = reader.ReadMetadata();
                Assert.Equal(count + 1, catalog.Count);
                Assert.All(catalog.Take(count), dg =>
                {
                    MdfChannelGroup group = Assert.Single(dg.ChannelGroups);
                    Assert.True(group.CanReadSamples);
                    Assert.False(Assert.Single(group.Channels).IsMaster);
                    Assert.True(group.Layout.HasRemoteMaster);
                    Assert.Null(group.Layout.ListDataMap);
                });
                MdfChannelGroup master = catalog[count].ChannelGroups[0];
                Assert.True(Assert.Single(master.Channels).IsMaster);
                Assert.False(master.Layout.HasRemoteMaster);
                Assert.Equal(0UL, reader.ReadSamples(catalog[count - 1].ChannelGroups[0], new[] { 0 }, 1, 1).Single().GetValue(0, 0).UnsignedValue);
                Assert.Null(master.Layout.ListDataMap);
                Assert.InRange(source.LargestReadRequest, 1, 72);
                Assert.Equal(0, source.WriteCalls);
            }
        }

        private static List<Tuple<long, long>> DataBodyRanges(byte[] bytes)
        {
            // Only the frozen positive corpus: inspect literal LE wire headers/links, not MdfLib codecs/maps.
            List<Tuple<long, long>> bodies = new List<Tuple<long, long>>();
            for (int dg = M5CWire.Dg(bytes); dg != 0; dg = (int)M3Wire.U64(bytes, M3Wire.Link(dg, 0)))
            {
                int list = (int)M3Wire.U64(bytes, M3Wire.Link(dg, 2));
                while (list != 0)
                {
                    Assert.Equal((byte)'L', bytes[list + 2]); Assert.Equal((byte)'D', bytes[list + 3]);
                    int links = (int)M3Wire.U64(bytes, list + 16);
                    for (int index = 1; index < links; index++)
                    {
                        int leaf = (int)M3Wire.U64(bytes, M3Wire.Link(list, index));
                        if (leaf == 0) { continue; }
                        Assert.Equal((byte)'D', bytes[leaf + 2]);
                        Assert.True(bytes[leaf + 3] == (byte)'V' || bytes[leaf + 3] == (byte)'I' || bytes[leaf + 3] == (byte)'Z');
                        long start = leaf + 24 + (bytes[leaf + 3] == (byte)'Z' ? 24 : 0);
                        bodies.Add(Tuple.Create(start, leaf + (long)M3Wire.U64(bytes, leaf + 8)));
                    }
                    list = (int)M3Wire.U64(bytes, M3Wire.Link(list, 0));
                }
            }
            return bodies;
        }
    }
}
