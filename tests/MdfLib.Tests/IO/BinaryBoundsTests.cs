// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using MdfLib.IO;
using Xunit;

namespace MdfLib.Tests.IO
{
    public sealed class BinaryBoundsTests
    {
        [Fact]
        public void RangeIncludesTheLastByteAndAnEmptyRangeAtEof()
        {
            long position;
            Assert.True(BinaryBounds.TryGetRange(8, 7, 1, out position));
            Assert.Equal(7L, position);
            Assert.True(BinaryBounds.TryGetRange(8, 8, 0, out position));
            Assert.Equal(8L, position);
            Assert.True(BinaryBounds.TryGetRange(0, 0, 0, out position));
            Assert.Equal(0L, position);
        }

        [Fact]
        public void RangeRejectsEndOverflowAndResetsTheOutput()
        {
            long position = 123;
            Assert.False(BinaryBounds.TryGetRange(8, 8, 1, out position));
            Assert.Equal(0L, position);
            Assert.False(BinaryBounds.TryGetRange(8, 9, 0, out position));
            Assert.Equal(0L, position);
            Assert.False(BinaryBounds.TryGetRange(8, 1, ulong.MaxValue, out position));
            Assert.Equal(0L, position);
        }

        [Fact]
        public void WireAddressesAreNeverTruncatedToSignedPositions()
        {
            long position;
            Assert.True(BinaryBounds.TryGetRange(long.MaxValue, (ulong)long.MaxValue, 0, out position));
            Assert.Equal(long.MaxValue, position);
            Assert.True(BinaryBounds.TryGetRange(long.MaxValue, (ulong)long.MaxValue - 1, 1, out position));
            Assert.Equal(long.MaxValue - 1, position);
            Assert.False(BinaryBounds.TryGetRange(long.MaxValue, (ulong)long.MaxValue, 1, out position));
            Assert.False(BinaryBounds.TryGetRange(long.MaxValue, (ulong)long.MaxValue + 1, 0, out position));
            Assert.False(BinaryBounds.TryGetRange(long.MaxValue, ulong.MaxValue, ulong.MaxValue, out position));
            Assert.Equal(0L, position);
        }

        [Fact]
        public void NegativeStreamLengthIsAConfigurationError()
        {
            long position;
            Assert.Throws<ArgumentOutOfRangeException>(() => BinaryBounds.TryGetRange(-1, 0, 0, out position));
        }

        [Fact]
        public void BufferSizeAllowsTheExactBudgetAndZeroProducts()
        {
            int size;
            Assert.True(BinaryBounds.TryGetBufferSize(4, 8, 32, out size));
            Assert.Equal(32, size);
            Assert.True(BinaryBounds.TryGetBufferSize(0, ulong.MaxValue, 0, out size));
            Assert.Equal(0, size);
            Assert.True(BinaryBounds.TryGetBufferSize(ulong.MaxValue, 0, 0, out size));
            Assert.Equal(0, size);
            Assert.True(BinaryBounds.TryGetBufferSize(int.MaxValue, 1, int.MaxValue, out size));
            Assert.Equal(int.MaxValue, size);
        }

        [Fact]
        public void BufferSizeRejectsOverflowBeforeMultiplication()
        {
            int size = 123;
            Assert.False(BinaryBounds.TryGetBufferSize(4, 8, 31, out size));
            Assert.Equal(0, size);
            Assert.False(BinaryBounds.TryGetBufferSize(ulong.MaxValue, ulong.MaxValue, int.MaxValue, out size));
            Assert.Equal(0, size);
            Assert.False(BinaryBounds.TryGetBufferSize(1UL << 63, 2, int.MaxValue, out size));
            Assert.Equal(0, size);
            Assert.False(BinaryBounds.TryGetBufferSize(1, (ulong)int.MaxValue + 1, int.MaxValue, out size));
            Assert.Equal(0, size);
            Assert.False(BinaryBounds.TryGetBufferSize(1, 1, 0, out size));
        }

        [Fact]
        public void BufferSizeRejectsANegativeBudget()
        {
            int size;
            Assert.Throws<ArgumentOutOfRangeException>(() => BinaryBounds.TryGetBufferSize(0, 0, -1, out size));
        }

        [Fact]
        public void CallerBufferRangesAreCheckedWithoutAddingOffsets()
        {
            byte[] buffer = new byte[8];
            BinaryBounds.ValidateBufferRange(buffer, 8, 0);
            BinaryBounds.ValidateBufferRange(buffer, 0, 8);
            Assert.Throws<ArgumentNullException>(() => BinaryBounds.ValidateBufferRange(null, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => BinaryBounds.ValidateBufferRange(buffer, -1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => BinaryBounds.ValidateBufferRange(buffer, 9, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => BinaryBounds.ValidateBufferRange(buffer, 0, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => BinaryBounds.ValidateBufferRange(buffer, int.MaxValue, int.MaxValue));
            Assert.Throws<ArgumentOutOfRangeException>(() => BinaryBounds.ValidateBufferRange(buffer, 1, int.MaxValue));
        }
    }
}
