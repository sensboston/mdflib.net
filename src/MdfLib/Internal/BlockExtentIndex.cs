// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;

namespace MdfLib.Internal
{
    // Disjoint half-open intervals. Do not replace with SortedSet views: Framework recounts their ranges.
    // AVL insertion has logarithmically bounded recursion, independent of the on-disk graph's depth.
    internal sealed class BlockExtentIndex
    {
        private Node root;

        internal int Count { get; private set; }
        internal int Height => HeightOf(root);

        internal bool Overlaps(long start, long end)
        {
            if (start < 0) { throw new ArgumentOutOfRangeException(nameof(start)); }
            if (end <= start) { throw new ArgumentOutOfRangeException(nameof(end)); }
            Node current = root;
            while (current != null)
            {
                if (end <= current.Start) { current = current.Left; }
                else if (start >= current.End) { current = current.Right; }
                else { return true; }
            }
            return false;
        }

        internal void Add(long start, long end)
        {
            if (Overlaps(start, end)) { throw new ArgumentException("Block extents must not overlap.", nameof(start)); }
            if (Count == int.MaxValue) { throw new InvalidOperationException("The extent index cannot exceed the int block budget."); }
            root = Insert(root, new Node(start, end));
            Count++;
        }

        private static Node Insert(Node current, Node item)
        {
            if (current == null) { return item; }
            if (item.Start < current.Start) { current.Left = Insert(current.Left, item); }
            else { current.Right = Insert(current.Right, item); }
            UpdateHeight(current);
            int balance = HeightOf(current.Left) - HeightOf(current.Right);
            if (balance > 1)
            {
                if (item.Start > current.Left.Start) { current.Left = RotateLeft(current.Left); }
                return RotateRight(current);
            }
            if (balance < -1)
            {
                if (item.Start < current.Right.Start) { current.Right = RotateRight(current.Right); }
                return RotateLeft(current);
            }
            return current;
        }

        private static Node RotateLeft(Node node)
        {
            Node next = node.Right;
            node.Right = next.Left;
            next.Left = node;
            UpdateHeight(node);
            UpdateHeight(next);
            return next;
        }

        private static Node RotateRight(Node node)
        {
            Node next = node.Left;
            node.Left = next.Right;
            next.Right = node;
            UpdateHeight(node);
            UpdateHeight(next);
            return next;
        }

        private static int HeightOf(Node node) { return node == null ? 0 : node.Height; }
        private static void UpdateHeight(Node node) { node.Height = 1 + Math.Max(HeightOf(node.Left), HeightOf(node.Right)); }

        private sealed class Node
        {
            internal Node(long start, long end) { Start = start; End = end; Height = 1; }
            internal long Start { get; }
            internal long End { get; }
            internal int Height { get; set; }
            internal Node Left { get; set; }
            internal Node Right { get; set; }
        }
    }
}
