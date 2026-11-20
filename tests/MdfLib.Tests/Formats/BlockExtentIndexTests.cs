// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MdfLib.Internal;
using Xunit;

namespace MdfLib.Tests.Formats
{
    public sealed class BlockExtentIndexTests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void OrderedReverseAndShuffledInsertionsRemainHeightBalanced(int order)
        {
            const int count = 10_000;
            int[] entries = Order(count, order);
            BlockExtentIndex index = new BlockExtentIndex();
            for (int item = 0; item < count; item++)
            {
                long start = 64L + entries[item] * 16;
                index.Add(start, start + 8);
                Assert.Equal(item + 1, index.Count);
                Assert.InRange(index.Height, 1, 2 * CeilingLog2(index.Count + 1));
            }
            for (int item = 0; item < count; item++)
            {
                long start = 64L + item * 16;
                Assert.True(index.Overlaps(start, start + 1));
                Assert.True(index.Overlaps(start + 7, start + 8));
                Assert.False(index.Overlaps(start - 1, start));
                Assert.False(index.Overlaps(start + 8, start + 16));
            }
            // Inspect actual nodes independently, not just the height cached by the implementation.
            object root = typeof(BlockExtentIndex).GetField("root", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(index);
            int nodes = 0;
            long previousEnd = -1;
            Assert.Equal(index.Height, Inspect(root, 1, ref nodes, ref previousEnd));
            Assert.Equal(count, nodes);
        }

        [Fact]
        public void FixedSeedRangeQueriesMatchAnIndependentLinearOracle()
        {
            BlockExtentIndex index = new BlockExtentIndex();
            List<long> starts = new List<long>();
            foreach (int item in Order(200, 2))
            {
                long start = item * 16L;
                starts.Add(start);
                index.Add(start, start + 5);
            }
            uint state = 0xcdf04123U;
            for (int query = 0; query < 10_000; query++)
            {
                state = unchecked(state * 1664525U + 1013904223U);
                long start = state % 3_500U;
                state = unchecked(state * 1664525U + 1013904223U);
                long end = start + 1 + state % 300U;
                bool expected = starts.Any(stored => stored < end && start < stored + 5);
                Assert.Equal(expected, index.Overlaps(start, end));
            }
        }

        [Fact]
        public void HalfOpenAdjacencyAndExtremeEndsDoNotOverflow()
        {
            BlockExtentIndex index = new BlockExtentIndex();
            Assert.Equal(0, index.Count);
            Assert.Equal(0, index.Height);
            Assert.False(index.Overlaps(0, long.MaxValue));
            index.Add(0, 1);
            index.Add(1, 2);
            index.Add(long.MaxValue - 2, long.MaxValue);
            Assert.False(index.Overlaps(2, long.MaxValue - 2));
            Assert.True(index.Overlaps(long.MaxValue - 1, long.MaxValue));
            Assert.True(index.Overlaps(0, long.MaxValue));
            Assert.Equal(3, index.Count);
        }

        [Theory]
        [InlineData(-1L, 1L)]
        [InlineData(0L, 0L)]
        [InlineData(2L, 1L)]
        public void InvalidRangesNeverMutateTheIndex(long start, long end)
        {
            BlockExtentIndex index = new BlockExtentIndex();
            Assert.Throws<ArgumentOutOfRangeException>(() => index.Overlaps(start, end));
            Assert.Throws<ArgumentOutOfRangeException>(() => index.Add(start, end));
            Assert.Equal(0, index.Count);
            Assert.Equal(0, index.Height);
        }

        [Theory]
        [InlineData(64L, 72L)]
        [InlineData(66L, 70L)]
        [InlineData(60L, 66L)]
        [InlineData(70L, 80L)]
        [InlineData(60L, 80L)]
        public void IntersectingInsertionsFailWithoutLosingAnyExistingRanges(long start, long end)
        {
            BlockExtentIndex index = new BlockExtentIndex();
            index.Add(64, 72); index.Add(0, 8); index.Add(128, 136);
            int height = index.Height;
            Assert.Throws<ArgumentException>(() => index.Add(start, end));
            Assert.Equal(3, index.Count);
            Assert.Equal(height, index.Height);
            Assert.True(index.Overlaps(0, 8));
            Assert.True(index.Overlaps(64, 72));
            Assert.True(index.Overlaps(128, 136));
            Assert.False(index.Overlaps(8, 64));
            Assert.False(index.Overlaps(72, 128));
        }

        private static int[] Order(int count, int order)
        {
            int[] entries = Enumerable.Range(0, count).ToArray();
            if (order == 1) { Array.Reverse(entries); }
            if (order == 2)
            {
                uint state = 0xabcd1234U;
                for (int last = count - 1; last > 0; last--)
                {
                    state = unchecked(state * 1664525U + 1013904223U);
                    int other = (int)(state % (uint)(last + 1));
                    int value = entries[last]; entries[last] = entries[other]; entries[other] = value;
                }
            }
            return entries;
        }

        private static int CeilingLog2(int value)
        {
            int result = 0;
            for (int power = 1; power < value; power *= 2) { result++; }
            return result;
        }

        private static int Inspect(object node, int depth, ref int count, ref long previousEnd)
        {
            if (node == null) { return 0; }
            Assert.InRange(depth, 1, 64);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Type type = node.GetType();
            int left = Inspect(type.GetProperty("Left", flags).GetValue(node), depth + 1, ref count, ref previousEnd);
            long start = (long)type.GetProperty("Start", flags).GetValue(node);
            long end = (long)type.GetProperty("End", flags).GetValue(node);
            Assert.True(start >= previousEnd && end > start);
            previousEnd = end;
            count++;
            int right = Inspect(type.GetProperty("Right", flags).GetValue(node), depth + 1, ref count, ref previousEnd);
            Assert.InRange(left - right, -1, 1);
            int height = 1 + Math.Max(left, right);
            Assert.Equal(height, (int)type.GetProperty("Height", flags).GetValue(node));
            return height;
        }
    }
}
