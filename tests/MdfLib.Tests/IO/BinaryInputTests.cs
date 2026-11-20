// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Threading;
using MdfLib.IO;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.IO
{
    public sealed class BinaryInputTests
    {
        [Fact]
        public void ConstructorPreservesTheInitialPositionAndCapturesLength()
        {
            using (TestReadStream source = new TestReadStream(8L))
            {
                source.Position = 3;
                int seeks = source.SeekCalls;
                using (BinaryInput input = new BinaryInput(source, true))
                {
                    Assert.Equal(3L, input.Position);
                    Assert.Equal(8L, input.Length);
                    Assert.Equal(BinaryInput.DefaultMaximumBufferBytes, input.MaximumBufferBytes);
                    Assert.Equal(seeks, source.SeekCalls);
                    Assert.Equal(0, source.ReadCalls);
                    source.ReportedLength = 16;
                    source.AvailableLength = 16;
                    Assert.Equal(8L, input.Length);
                    Assert.Throws<ArgumentOutOfRangeException>(() => input.Seek(9));
                    input.Seek(8);
                    Assert.Throws<EndOfStreamException>(() => input.ReadByte());
                    Assert.Equal(0, source.ReadCalls);
                }
            }
        }

        [Fact]
        public void CurrentRawRangeBoundDetectsReportedShrinkageButNeverExtendsTheCapturedDocument()
        {
            using (TestReadStream source = new TestReadStream(1024L))
            {
                BinaryInput input = new BinaryInput(source, true);
                Assert.Equal(1024L, input.CurrentBoundedLength);
                source.ReportedLength = 2048;
                Assert.Equal(1024L, input.CurrentBoundedLength);
                source.ReportedLength = 512;
                Assert.Equal(512L, input.CurrentBoundedLength);
                Assert.Equal(1024L, input.Length); // snapshot semantics did not change.
                IOException cause = new IOException("current raw-range length failure");
                source.LengthFailure = cause;
                Assert.Same(cause, Assert.Throws<IOException>(() => input.CurrentBoundedLength));
                source.LengthFailure = null;
                source.ReportedLength = -1;
                Assert.Throws<IOException>(() => input.CurrentBoundedLength);
                input.Dispose();
                Assert.Throws<ObjectDisposedException>(() => input.CurrentBoundedLength);
                Assert.Equal(0, source.ReadCalls);
                Assert.Equal(0, source.SeekCalls);
                Assert.Equal(0, source.DisposeCalls);
            }
        }

        [Fact]
        public void ConstructorRejectsNullAndInvalidLimits()
        {
            Assert.Throws<ArgumentNullException>(() => new BinaryInput(null));
            using (TestReadStream source = new TestReadStream(8L))
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => new BinaryInput(source, maximumBufferBytes: 0));
                Assert.Throws<ArgumentOutOfRangeException>(() => new BinaryInput(source, maximumBufferBytes: -1));
                Assert.Equal(0, source.DisposeCalls);
                Assert.True(source.CanRead);
            }
        }

        [Fact]
        public void FailedCapabilityValidationDoesNotTakeOwnership()
        {
            using (TestReadStream source = new TestReadStream(8L))
            {
                source.Readable = false;
                Assert.Throws<ArgumentException>(() => new BinaryInput(source));
                source.Readable = true;
                source.Seekable = false;
                Assert.Throws<ArgumentException>(() => new BinaryInput(source));
                Assert.Equal(0, source.ReadCalls);
                Assert.Equal(0, source.SeekCalls);
                Assert.Equal(0, source.DisposeCalls);
            }
        }

        [Fact]
        public void InvalidOrUnavailableLengthDoesNotTakeOwnership()
        {
            using (TestReadStream source = new TestReadStream(-1L))
            {
                Assert.Throws<ArgumentException>(() => new BinaryInput(source));
                IOException failure = new IOException("Length failure.");
                source.LengthFailure = failure;
                Assert.Same(failure, Assert.Throws<IOException>(() => new BinaryInput(source)));
                Assert.Equal(0, source.DisposeCalls);
            }
        }

        [Fact]
        public void ShortReadsFillOnlyTheRequestedSlice()
        {
            byte[] original = { 1, 2, 3, 4, 5 };
            using (TestReadStream source = new TestReadStream(original))
            using (BinaryInput input = new BinaryInput(source, true))
            {
                source.MaximumBytesPerRead = 1;
                byte[] destination = { 0xee, 0xee, 0xee, 0xee, 0xee, 0xee, 0xee };
                input.ReadExactly(destination, 1, 5);
                Assert.Equal(new byte[] { 0xee, 1, 2, 3, 4, 5, 0xee }, destination);
                Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, original);
                Assert.Equal(5, source.ReadCalls);
                Assert.Equal(5L, input.Position);
                Assert.Equal(0, source.WriteCalls);
                Assert.Equal(0, source.FlushCalls);
            }
        }

        [Fact]
        public void KnownOutOfRangeReadsFailBeforeTouchingTheStream()
        {
            using (TestReadStream source = new TestReadStream(3L))
            using (BinaryInput input = new BinaryInput(source, true))
            {
                byte[] destination = { 0xee, 0xee, 0xee, 0xee };
                Assert.Throws<EndOfStreamException>(() => input.ReadExactly(destination, 0, 4));
                Assert.Throws<EndOfStreamException>(() => input.ReadBytes(4));
                Assert.Equal(new byte[] { 0xee, 0xee, 0xee, 0xee }, destination);
                Assert.Equal(0, source.ReadCalls);
                Assert.Equal(0L, input.Position);
            }
        }

        [Fact]
        public void UnexpectedSourceTruncationFailsRatherThanReturningPartialSuccess()
        {
            using (TestReadStream source = new TestReadStream(new byte[] { 1, 2, 3, 4 }))
            using (BinaryInput input = new BinaryInput(source, true))
            {
                source.AvailableLength = 2;
                source.ReportedLength = 2;
                byte[] destination = { 0xee, 0xee, 0xee, 0xee };
                Assert.Throws<EndOfStreamException>(() => input.ReadExactly(destination, 0, 4));
                Assert.Equal(new byte[] { 1, 2, 0xee, 0xee }, destination);
                Assert.Equal(2, source.ReadCalls);
                Assert.Equal(2L, input.Position);
                Assert.Equal(4L, input.Length);
            }
        }

        [Fact]
        public void EmptyReadsAtEofDoNotCallReadOrAllocateNewArrays()
        {
            using (TestReadStream source = new TestReadStream(0L))
            using (BinaryInput input = new BinaryInput(source, true))
            {
                input.ReadExactly(Array.Empty<byte>(), 0, 0);
                Assert.Same(Array.Empty<byte>(), input.ReadBytes(0));
                Assert.Same(input.ReadBytes(0), input.ReadBytes(0));
                Assert.Equal(0, source.ReadCalls);
                Assert.Equal(0L, input.Position);
            }
        }

        [Fact]
        public void ReadArgumentsAreValidatedEvenForZeroCount()
        {
            using (TestReadStream source = new TestReadStream(8L))
            using (BinaryInput input = new BinaryInput(source, true))
            {
                byte[] buffer = new byte[4];
                Assert.Throws<ArgumentNullException>(() => input.ReadExactly(null, 0, 0));
                Assert.Throws<ArgumentOutOfRangeException>(() => input.ReadExactly(buffer, -1, 0));
                Assert.Throws<ArgumentOutOfRangeException>(() => input.ReadExactly(buffer, 5, 0));
                Assert.Throws<ArgumentOutOfRangeException>(() => input.ReadExactly(buffer, 0, -1));
                Assert.Throws<ArgumentOutOfRangeException>(() => input.ReadExactly(buffer, 1, int.MaxValue));
                Assert.Throws<ArgumentOutOfRangeException>(() => input.ReadBytes(-1));
                Assert.Equal(0, source.ReadCalls);
                Assert.Equal(0L, input.Position);
            }
        }

        [Fact]
        public void BufferBudgetIsCheckedBeforeReadOrAllocation()
        {
            using (TestReadStream source = new TestReadStream(64L))
            using (BinaryInput input = new BinaryInput(source, true, 4))
            {
                Assert.Equal(4, input.MaximumBufferBytes);
                Assert.Throws<InvalidOperationException>(() => input.ReadBytes(5));
                Assert.Throws<InvalidOperationException>(() => input.ReadBytes(int.MaxValue));
                Assert.Equal(0, source.ReadCalls);
                Assert.Equal(0L, input.Position);
                Assert.Equal(new byte[] { 0, 1, 2, 3 }, input.ReadBytes(4));
            }
        }

        [Fact]
        public void ExplicitCallerBufferCanExceedTheOwnedBufferBudget()
        {
            using (TestReadStream source = new TestReadStream(8L))
            using (BinaryInput input = new BinaryInput(source, true, 1))
            {
                byte[] destination = new byte[8];
                input.ReadExactly(destination, 0, destination.Length);
                Assert.Equal(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 }, destination);
            }
        }

        [Fact]
        public void ReadBytesReturnsIndependentNonemptyBuffers()
        {
            using (TestReadStream source = new TestReadStream(4L))
            using (BinaryInput input = new BinaryInput(source, true))
            {
                byte[] first = input.ReadBytes(2);
                byte[] second = input.ReadBytes(2);
                Assert.NotSame(first, second);
                Assert.Equal(new byte[] { 0, 1 }, first);
                Assert.Equal(new byte[] { 2, 3 }, second);
                second[0] = 0xff;
                Assert.Equal((byte)0, first[0]);
            }
        }

        [Fact]
        public void IndividualReadRequestsAreCappedAtSixtyFourKiB()
        {
            const int count = 2 * BinaryInput.MaximumReadChunkBytes + 3;
            using (TestReadStream source = new TestReadStream((long)count))
            using (BinaryInput input = new BinaryInput(source, true, 1))
            {
                byte[] destination = new byte[count];
                input.ReadExactly(destination, 0, count);
                Assert.Equal(3, source.ReadCalls);
                Assert.Equal(BinaryInput.MaximumReadChunkBytes, source.LargestReadRequest);
                Assert.Equal((long)count, input.Position);
                Assert.Equal((byte)0, destination[0]);
                Assert.Equal((byte)2, destination[count - 1]);
            }
        }

        [Fact]
        public void PreCancelledReadsLeavePositionAndDestinationUntouched()
        {
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            using (TestReadStream source = new TestReadStream(4L))
            using (BinaryInput input = new BinaryInput(source, true))
            {
                cancellation.Cancel();
                byte[] destination = { 0xee, 0xee };
                OperationCanceledException error = Assert.Throws<OperationCanceledException>(() => input.ReadExactly(destination, 0, 2, cancellation.Token));
                Assert.Equal(cancellation.Token, error.CancellationToken);
                Assert.Throws<OperationCanceledException>(() => input.ReadBytes(2, cancellation.Token));
                Assert.Throws<OperationCanceledException>(() => input.ReadByte(cancellation.Token));
                Assert.Equal(new byte[] { 0xee, 0xee }, destination);
                Assert.Equal(0, source.ReadCalls);
                Assert.Equal(0L, input.Position);
            }
        }

        [Fact]
        public void CancellationDoesNotDisappearForZeroCount()
        {
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            using (TestReadStream source = new TestReadStream(0L))
            using (BinaryInput input = new BinaryInput(source, true))
            {
                cancellation.Cancel();
                Assert.Throws<OperationCanceledException>(() => input.ReadExactly(Array.Empty<byte>(), 0, 0, cancellation.Token));
                Assert.Throws<OperationCanceledException>(() => input.ReadBytes(0, cancellation.Token));
                Assert.Equal(0, source.ReadCalls);
            }
        }

        [Fact]
        public void CancellationBetweenShortReadsPreservesThePartialReadState()
        {
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            using (TestReadStream source = new TestReadStream(new byte[] { 1, 2, 3, 4 }))
            using (BinaryInput input = new BinaryInput(source, true))
            {
                source.MaximumBytesPerRead = 2;
                source.AfterRead = cancellation.Cancel;
                byte[] destination = { 0xee, 0xee, 0xee, 0xee };
                Assert.Throws<OperationCanceledException>(() => input.ReadExactly(destination, 0, 4, cancellation.Token));
                Assert.Equal(new byte[] { 1, 2, 0xee, 0xee }, destination);
                Assert.Equal(1, source.ReadCalls);
                Assert.Equal(2L, input.Position);
            }
        }

        [Fact]
        public void UnderlyingIoExceptionsAreNotHiddenOrReplaced()
        {
            using (TestReadStream source = new TestReadStream(8L))
            using (BinaryInput input = new BinaryInput(source, true))
            {
                IOException readFailure = new IOException("Underlying read failed.");
                source.ReadFailure = readFailure;
                Assert.Same(readFailure, Assert.Throws<IOException>(() => input.ReadByte()));
                IOException seekFailure = new IOException("Underlying seek failed.");
                source.SeekFailure = seekFailure;
                Assert.Same(seekFailure, Assert.Throws<IOException>(() => input.Seek(0)));
            }
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(2)]
        public void InvalidStreamReadCountsAreRejected(int returnedCount)
        {
            using (TestReadStream source = new TestReadStream(1L))
            using (BinaryInput input = new BinaryInput(source, true))
            {
                source.InvalidReadCount = returnedCount;
                Assert.Throws<IOException>(() => input.ReadByte());
                Assert.Equal(1, source.ReadCalls);
            }
        }

        [Theory]
        [InlineData(-1L)]
        [InlineData(5L)]
        public void PositionOutsideTheCapturedRangeIsAnIoErrorEvenForZeroCount(long position)
        {
            using (TestReadStream source = new TestReadStream(4L))
            using (BinaryInput input = new BinaryInput(source, true))
            {
                source.PositionOverride = position;
                Assert.Throws<IOException>(() => input.Position);
                Assert.Throws<IOException>(() => input.ReadBytes(0));
                Assert.Equal(0, source.ReadCalls);
            }
        }

        [Fact]
        public void SeekValidatesBeforeTouchingTheSourceAndChecksTheResult()
        {
            using (TestReadStream source = new TestReadStream(8L))
            using (BinaryInput input = new BinaryInput(source, true))
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => input.Seek(-1));
                Assert.Throws<ArgumentOutOfRangeException>(() => input.Seek(9));
                Assert.Throws<ArgumentOutOfRangeException>(() => input.Seek(long.MaxValue));
                Assert.Equal(0, source.SeekCalls);
                input.Seek(8);
                Assert.Equal(8L, input.Position);
                input.Seek(0);
                source.SeekResultOverride = 1;
                Assert.Throws<IOException>(() => input.Seek(2));
            }
        }

        [Fact]
        public void SparseSourceSupportsOffsetsBeyondTwoGiBAndNearLongMaxValue()
        {
            using (TestReadStream source = new TestReadStream(long.MaxValue))
            using (BinaryInput input = new BinaryInput(source, true))
            {
                long offset = (long)int.MaxValue + 1;
                input.Seek(offset);
                Assert.Equal(offset, input.Position);
                Assert.Equal(new byte[] { 0, 1 }, input.ReadBytes(2));
                input.Seek(long.MaxValue - 2);
                Assert.Equal(new byte[] { 0xfd, 0xfe }, input.ReadBytes(2));
                Assert.Equal(long.MaxValue, input.Position);
                Assert.Throws<EndOfStreamException>(() => input.ReadByte());
                Assert.Equal(2, source.ReadCalls);
                Assert.Equal(2, source.LargestReadRequest);
            }
        }

        [Fact]
        public void DisposeClosesOwnedStreamExactlyOnceWithoutFlushOrWrite()
        {
            TestReadStream source = new TestReadStream(8L);
            BinaryInput input = new BinaryInput(source);
            input.Dispose();
            input.Dispose();
            Assert.Equal(1, source.DisposeCalls);
            Assert.False(source.CanRead);
            Assert.Equal(0, source.WriteCalls);
            Assert.Equal(0, source.FlushCalls);
            Assert.Throws<ObjectDisposedException>(() => input.Length);
            Assert.Throws<ObjectDisposedException>(() => input.Position);
            Assert.Throws<ObjectDisposedException>(() => input.MaximumBufferBytes);
            Assert.Throws<ObjectDisposedException>(() => input.Seek(0));
            Assert.Throws<ObjectDisposedException>(() => input.ReadExactly(Array.Empty<byte>(), 0, 0));
            Assert.Throws<ObjectDisposedException>(() => input.ReadBytes(0));
            Assert.Throws<ObjectDisposedException>(() => input.ReadByte());
            Assert.Throws<ObjectDisposedException>(() => input.ReadUInt64(ByteOrder.BigEndian));
        }

        [Fact]
        public void LeaveOpenPreservesTheExternalStreamButNotTheInput()
        {
            using (TestReadStream source = new TestReadStream(8L))
            {
                BinaryInput input = new BinaryInput(source, true);
                input.Dispose();
                input.Dispose();
                Assert.Equal(0, source.DisposeCalls);
                Assert.True(source.CanRead);
                Assert.Throws<ObjectDisposedException>(() => input.ReadBytes(0));
                byte[] result = new byte[1];
                Assert.Equal(1, source.Read(result, 0, 1));
            }
        }

        [Fact]
        public void DisposeFailurePropagatesAndTheWrapperStillRemainsDisposed()
        {
            TestReadStream source = new TestReadStream(8L);
            BinaryInput input = new BinaryInput(source);
            IOException failure = new IOException("Underlying dispose failed.");
            source.DisposeFailure = failure;
            Assert.Same(failure, Assert.Throws<IOException>(() => input.Dispose()));
            input.Dispose();
            Assert.Equal(1, source.DisposeCalls);
            Assert.Throws<ObjectDisposedException>(() => input.Length);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TypedIntegerReadsHandleShortReadsAndTheSelectedOrder(bool bigEndian)
        {
            ByteOrder order = bigEndian ? ByteOrder.BigEndian : ByteOrder.LittleEndian;
            byte[] data = { 0x01, 0x23, 0x45, 0x67, 0x89, 0xab, 0xcd, 0xef };
            Assert.Equal((byte)0x01, Read(data, input => input.ReadByte()));
            Assert.Equal((ushort)(bigEndian ? 0x0123 : 0x2301), Read(data, input => input.ReadUInt16(order)));
            Assert.Equal(bigEndian ? 0x01234567U : 0x67452301U, Read(data, input => input.ReadUInt32(order)));
            Assert.Equal(bigEndian ? 0x0123456789abcdefUL : 0xefcdab8967452301UL, Read(data, input => input.ReadUInt64(order)));
            byte[] ones = { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff };
            Assert.Equal((short)-1, Read(ones, input => input.ReadInt16(order)));
            Assert.Equal(-1, Read(ones, input => input.ReadInt32(order)));
            Assert.Equal(-1L, Read(ones, input => input.ReadInt64(order)));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TypedFloatingReadsHandleShortReadsAndTheSelectedOrder(bool bigEndian)
        {
            ByteOrder order = bigEndian ? ByteOrder.BigEndian : ByteOrder.LittleEndian;
            byte[] single = { 0x3f, 0x80, 0, 0 };
            byte[] precise = { 0x3f, 0xf0, 0, 0, 0, 0, 0, 0 };
            if (!bigEndian)
            {
                Array.Reverse(single);
                Array.Reverse(precise);
            }
            Assert.Equal(1.0f, Read(single, input => input.ReadSingle(order)));
            Assert.Equal(1.0, Read(precise, input => input.ReadDouble(order)));
        }

        [Fact]
        public void InvalidByteOrderIsRejectedBeforePrimitiveRead()
        {
            using (TestReadStream source = new TestReadStream(8L))
            using (BinaryInput input = new BinaryInput(source, true))
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => input.ReadUInt16((ByteOrder)17));
                Assert.Throws<ArgumentOutOfRangeException>(() => input.ReadUInt32((ByteOrder)17));
                Assert.Throws<ArgumentOutOfRangeException>(() => input.ReadUInt64((ByteOrder)17));
                Assert.Throws<ArgumentOutOfRangeException>(() => input.ReadInt16((ByteOrder)17));
                Assert.Throws<ArgumentOutOfRangeException>(() => input.ReadInt32((ByteOrder)17));
                Assert.Throws<ArgumentOutOfRangeException>(() => input.ReadInt64((ByteOrder)17));
                Assert.Throws<ArgumentOutOfRangeException>(() => input.ReadSingle((ByteOrder)17));
                Assert.Throws<ArgumentOutOfRangeException>(() => input.ReadDouble((ByteOrder)17));
                Assert.Equal(0, source.ReadCalls);
                Assert.Equal(0L, input.Position);
            }
        }

        private static T Read<T>(byte[] data, Func<BinaryInput, T> read)
        {
            TestReadStream source = new TestReadStream(data) { MaximumBytesPerRead = 1 };
            using (BinaryInput input = new BinaryInput(source))
            {
                return read(input);
            }
        }
    }
}
