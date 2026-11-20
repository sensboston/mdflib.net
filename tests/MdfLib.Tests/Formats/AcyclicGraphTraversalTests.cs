// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Threading;
using MdfLib.Internal;
using Xunit;

namespace MdfLib.Tests.Formats
{
    public sealed class AcyclicGraphTraversalTests
    {
        [Fact]
        public void SharedDiamondNodesAndDuplicateReferencesAreNotCycles()
        {
            Dictionary<ulong, ulong[]> graph = new Dictionary<ulong, ulong[]>
            {
                { 64, new ulong[] { 72, 80, 72, 0 } }, { 72, new ulong[] { 88 } },
                { 80, new ulong[] { 88 } }, { 88, Array.Empty<ulong>() }
            };
            HashSet<ulong> visits = new HashSet<ulong>();
            AcyclicGraphTraversal.Visit(64, address => { Assert.True(visits.Add(address)); return graph[address]; }, new ReaderLimits(null));
            Assert.Equal(4, visits.Count);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SelfAndMultiNodeCyclesAreDistinguishedFromSharedNodes(bool self)
        {
            MdfFormatException error = Assert.Throws<MdfFormatException>(() => AcyclicGraphTraversal.Visit(64,
                address => self || address == 80 ? new ulong[] { 64 } : new[] { address + 8 }, new ReaderLimits(null)));
            Assert.Equal("MDF010", error.DiagnosticCode);
            Assert.Equal(64UL, error.Offset);
        }

        [Theory]
        [InlineData("MaximumTraversalDepth")]
        [InlineData("MaximumBlockCount")]
        [InlineData("MaximumTotalLinks")]
        [InlineData("MaximumLinksPerBlock")]
        public void TraversalBudgetsAreFiniteAndReportWhichOptionWasExceeded(string name)
        {
            MdfReaderOptions options = new MdfReaderOptions();
            if (name == "MaximumTraversalDepth") { options.MaximumTraversalDepth = 2; }
            if (name == "MaximumBlockCount") { options.MaximumBlockCount = 2; }
            if (name == "MaximumTotalLinks") { options.MaximumTotalLinks = 1; }
            if (name == "MaximumLinksPerBlock") { options.MaximumLinksPerBlock = 1; }
            MdfResourceLimitException error = Assert.Throws<MdfResourceLimitException>(() => AcyclicGraphTraversal.Visit(64,
                address => name == "MaximumLinksPerBlock" ? new ulong[] { 72, 80 } : new[] { address + 8 }, new ReaderLimits(options)));
            Assert.Equal(name, error.LimitName);
        }

        [Fact]
        public void LongChainsUseAnExplicitStackRatherThanClrRecursion()
        {
            const ulong count = 20_000;
            int visited = 0;
            ReaderLimits limits = new ReaderLimits(new MdfReaderOptions { MaximumTraversalDepth = (int)count });
            AcyclicGraphTraversal.Visit(1, address => { visited++; return address < count ? new[] { address + 1 } : Array.Empty<ulong>(); }, limits);
            Assert.Equal((int)count, visited);
        }

        [Fact]
        public void NullRootIsNotResolvedButDoesNotSuppressCancellation()
        {
            AcyclicGraphTraversal.Visit(0, address => throw new InvalidOperationException(), new ReaderLimits(null));
            using (CancellationTokenSource token = new CancellationTokenSource())
            {
                token.Cancel();
                Assert.Throws<OperationCanceledException>(() => AcyclicGraphTraversal.Visit(0, address => Array.Empty<ulong>(), new ReaderLimits(null), token.Token));
            }
        }

        [Fact]
        public void CancellationAndCallbackFailuresPropagateUnchanged()
        {
            using (CancellationTokenSource token = new CancellationTokenSource())
            {
                int visits = 0;
                Assert.Throws<OperationCanceledException>(() => AcyclicGraphTraversal.Visit(1, address => { visits++; token.Cancel(); return new ulong[] { 2 }; }, new ReaderLimits(null), token.Token));
                Assert.Equal(1, visits);
            }
            InvalidOperationException failure = new InvalidOperationException("Injected selector failure.");
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => AcyclicGraphTraversal.Visit(1, address => throw failure, new ReaderLimits(null))));
            Assert.Throws<ArgumentNullException>(() => AcyclicGraphTraversal.Visit(1, null, new ReaderLimits(null)));
            Assert.Throws<InvalidOperationException>(() => AcyclicGraphTraversal.Visit(1, address => null, new ReaderLimits(null)));
        }
    }
}
