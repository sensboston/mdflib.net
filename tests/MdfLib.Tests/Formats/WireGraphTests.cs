// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using MdfLib.Formats;
using MdfLib.Internal;
using MdfLib.IO;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Formats
{
    // Synthetic common headers, not a claim that these are complete MDF files or semantic codecs.
    public sealed class WireGraphTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SelectedWireEdgesShareCachedBlocksAndIgnoreLegalBackReferences(bool mdf4)
        {
            // The last edge is deliberately a back reference; the trusted selector excludes it.
            using (TestReadStream source = Source(mdf4, new[] { 1, 2, -1 }, new[] { 3, -1, 0 },
                new[] { 3, -1, 0 }, new[] { -1, -1, 0 }))
            using (BinaryInput input = new BinaryInput(source, true))
            {
                BlockResolver resolver = BlockResolverTests.Resolver(input, mdf4: mdf4);
                Dictionary<ulong, BlockHeader> visited = new Dictionary<ulong, BlockHeader>();
                AcyclicGraphTraversal.Visit(64, address =>
                {
                    BlockHeader block = Resolve(resolver, address, mdf4);
                    visited.Add(address, block);
                    return new[] { block.Links[0], block.Links[1] };
                }, new ReaderLimits(null));
                Assert.Equal(4, visited.Count);
                Assert.Equal(16, source.ReadCalls); // Each header and its three links exactly once.
                int reads = source.ReadCalls;
                Assert.Same(visited[Address(mdf4, 3)], Resolve(resolver, Address(mdf4, 3), mdf4));
                Assert.Same(visited[64], Resolve(resolver, 64, mdf4));
                Assert.Equal(reads, source.ReadCalls);
                Assert.Equal(0, source.WriteCalls);
            }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void SelectedWireCyclesFailWithoutConfusingCacheReuseWithCompletion(bool mdf4, bool self)
        {
            using (TestReadStream source = Source(mdf4, new[] { self ? 0 : 1, -1, -1 }, new[] { 0, -1, -1 }))
            using (BinaryInput input = new BinaryInput(source, true))
            {
                BlockResolver resolver = BlockResolverTests.Resolver(input, mdf4: mdf4);
                MdfFormatException error = Assert.Throws<MdfFormatException>(() => AcyclicGraphTraversal.Visit(64,
                    address => Resolve(resolver, address, mdf4).Links, new ReaderLimits(null)));
                Assert.Equal("MDF010", error.DiagnosticCode);
                Assert.Equal(64UL, error.Offset);
                Assert.Equal(self ? 4 : 8, source.ReadCalls);
            }
        }

        [Theory]
        [InlineData(false, "MaximumTraversalDepth")]
        [InlineData(true, "MaximumTraversalDepth")]
        [InlineData(false, "MaximumBlockCount")]
        [InlineData(true, "MaximumBlockCount")]
        public void GraphBudgetsRejectTheNextWireNodeBeforeItsIo(bool mdf4, string limit)
        {
            MdfReaderOptions options = new MdfReaderOptions();
            if (limit == "MaximumTraversalDepth") { options.MaximumTraversalDepth = 2; }
            else { options.MaximumBlockCount = 2; }
            using (TestReadStream source = Source(mdf4, new[] { 1, -1, -1 }, new[] { 2, -1, -1 }, new[] { -1, -1, -1 }))
            using (BinaryInput input = new BinaryInput(source, true))
            {
                BlockResolver resolver = BlockResolverTests.Resolver(input, options, mdf4);
                MdfResourceLimitException error = Assert.Throws<MdfResourceLimitException>(() => AcyclicGraphTraversal.Visit(64,
                    address => Resolve(resolver, address, mdf4).Links, new ReaderLimits(options)));
                Assert.Equal(limit, error.LimitName);
                Assert.Equal(2UL, error.Limit);
                Assert.Equal(3UL, error.Requested);
                Assert.Equal(Address(mdf4, 2), error.Offset);
                Assert.Equal(8, source.ReadCalls);
            }
        }

        private static BlockHeader Resolve(BlockResolver resolver, ulong address, bool mdf4)
        {
            return resolver.Resolve(address, mdf4 ? "##ZZ" : "ZZ", mdf4 ? 48 : 16, mdf4 ? 0 : 3);
        }

        private static ulong Address(bool mdf4, int index) { return (ulong)(64 + index * (mdf4 ? 48 : 16)); }

        private static TestReadStream Source(bool mdf4, params int[][] edges)
        {
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream)) // BCL writes LE; no library writer/codec involved.
            {
                writer.Write(new byte[64]);
                for (int index = 0; index < edges.Length; index++)
                {
                    Assert.Equal(3, edges[index].Length);
                    if (mdf4)
                    {
                        writer.Write(new byte[] { 35, 35, 90, 90 });
                        writer.Write(0U);
                        writer.Write(48UL);
                        writer.Write(3UL);
                    }
                    else
                    {
                        writer.Write(new byte[] { 90, 90 });
                        writer.Write((ushort)16);
                    }
                    foreach (int target in edges[index])
                    {
                        ulong address = target < 0 ? 0 : Address(mdf4, target);
                        if (mdf4) { writer.Write(address); }
                        else { writer.Write((uint)address); }
                    }
                }
                return new TestReadStream(stream.ToArray());
            }
        }
    }
}
