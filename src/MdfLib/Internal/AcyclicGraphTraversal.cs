// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Threading;

namespace MdfLib.Internal
{
    // Only trusted codecs select acyclic edges; an arbitrary MDF graph can have legal back references.
    internal static class AcyclicGraphTraversal
    {
        internal static void Visit(ulong root, Func<ulong, IReadOnlyList<ulong>> children, ReaderLimits limits,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (children == null) { throw new ArgumentNullException(nameof(children)); }
            if (limits == null) { throw new ArgumentNullException(nameof(limits)); }
            cancellationToken.ThrowIfCancellationRequested();
            if (root == 0) { return; }
            Dictionary<ulong, bool> completed = new Dictionary<ulong, bool>();
            Stack<Frame> stack = new Stack<Frame>();
            ulong edges = 0;
            Add(root, children, limits, completed, stack, cancellationToken);
            while (stack.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Frame current = stack.Peek();
                if (current.Index == current.Children.Count)
                {
                    completed[current.Address] = true;
                    stack.Pop();
                    continue;
                }
                ReadBudget.Check(nameof(MdfReaderOptions.MaximumTotalLinks), (ulong)limits.MaximumTotalLinks, edges, 1, current.Address);
                edges++;
                ulong child = current.Children[current.Index++];
                if (child == 0) { continue; }
                bool isComplete;
                if (completed.TryGetValue(child, out isComplete))
                {
                    if (!isComplete)
                    {
                        throw new MdfFormatException("A cycle occurs in an acyclic block relation.", "MDF010", child);
                    }
                    continue;
                }
                Add(child, children, limits, completed, stack, cancellationToken);
            }
        }

        private static void Add(ulong address, Func<ulong, IReadOnlyList<ulong>> children, ReaderLimits limits,
            Dictionary<ulong, bool> completed, Stack<Frame> stack, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ReadBudget.Check(nameof(MdfReaderOptions.MaximumTraversalDepth), (ulong)limits.MaximumTraversalDepth, (ulong)stack.Count, 1, address);
            ReadBudget.Check(nameof(MdfReaderOptions.MaximumBlockCount), (ulong)limits.MaximumBlockCount, (ulong)completed.Count, 1, address);
            IReadOnlyList<ulong> next = children(address);
            if (next == null) { throw new InvalidOperationException("A graph selector returned null instead of an empty link list."); }
            ReadBudget.Check(nameof(MdfReaderOptions.MaximumLinksPerBlock), (ulong)limits.MaximumLinksPerBlock, 0, (ulong)next.Count, address);
            completed.Add(address, false);
            stack.Push(new Frame(address, next));
        }

        private sealed class Frame
        {
            internal Frame(ulong address, IReadOnlyList<ulong> children) { Address = address; Children = children; }
            internal ulong Address { get; }
            internal IReadOnlyList<ulong> Children { get; }
            internal int Index { get; set; }
        }
    }
}
