// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;

namespace MdfLib.Tests.TestDoubles
{
    // In-memory or virtual source; large offsets require no large files or arrays.
    internal sealed class TestReadStream : Stream
    {
        private readonly byte[] data;
        private long position;
        private bool disposed;

        internal TestReadStream(byte[] data)
        {
            this.data = data;
            ReportedLength = data.LongLength;
            AvailableLength = data.LongLength;
        }

        internal TestReadStream(long virtualLength)
        {
            ReportedLength = virtualLength;
            AvailableLength = virtualLength;
        }

        internal bool Readable { get; set; } = true;
        internal bool Seekable { get; set; } = true;
        internal long ReportedLength { get; set; }
        internal long AvailableLength { get; set; }
        internal long? PositionOverride { get; set; }
        internal long? SeekResultOverride { get; set; }
        internal int MaximumBytesPerRead { get; set; } = int.MaxValue;
        internal int? InvalidReadCount { get; set; }
        internal Exception ReadFailure { get; set; }
        internal Exception SeekFailure { get; set; }
        internal Exception LengthFailure { get; set; }
        internal Exception DisposeFailure { get; set; }
        internal Action AfterRead { get; set; }
        internal List<Tuple<long, int>> ReadRanges { get; } = new List<Tuple<long, int>>();
        internal int ReadCalls { get; private set; }
        internal int SeekCalls { get; private set; }
        internal int WriteCalls { get; private set; }
        internal int FlushCalls { get; private set; }
        internal int DisposeCalls { get; private set; }
        internal int LargestReadRequest { get; private set; }

        public override bool CanRead => Readable && !disposed;
        public override bool CanSeek => Seekable && !disposed;
        public override bool CanWrite => false;

        public override long Length
        {
            get
            {
                ThrowIfDisposed();
                if (LengthFailure != null)
                {
                    throw LengthFailure;
                }
                return ReportedLength;
            }
        }

        public override long Position
        {
            get
            {
                ThrowIfDisposed();
                return PositionOverride ?? position;
            }
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ThrowIfDisposed();
            if (!Readable)
            {
                throw new NotSupportedException();
            }
            if (buffer == null || offset < 0 || count < 0 || count > buffer.Length - offset)
            {
                throw new ArgumentException("Invalid test buffer range.");
            }
            ReadCalls++;
            ReadRanges.Add(Tuple.Create(position, count));
            LargestReadRequest = Math.Max(LargestReadRequest, count);
            if (ReadFailure != null)
            {
                throw ReadFailure;
            }
            if (InvalidReadCount.HasValue)
            {
                return InvalidReadCount.Value;
            }

            long available = Math.Max(0, AvailableLength - position);
            int received = (int)Math.Min(Math.Min((long)count, MaximumBytesPerRead), available);
            for (int index = 0; index < received; index++)
            {
                buffer[offset + index] = data == null ? unchecked((byte)(position + index)) : data[(int)(position + index)];
            }
            position += received;
            AfterRead?.Invoke();
            return received;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ThrowIfDisposed();
            SeekCalls++;
            if (!Seekable)
            {
                throw new NotSupportedException();
            }
            if (SeekFailure != null)
            {
                throw SeekFailure;
            }
            switch (origin)
            {
                case SeekOrigin.Begin:
                    position = offset;
                    break;
                case SeekOrigin.Current:
                    position = checked(position + offset);
                    break;
                case SeekOrigin.End:
                    position = checked(ReportedLength + offset);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(origin));
            }
            return SeekResultOverride ?? position;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteCalls++;
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            WriteCalls++;
            throw new NotSupportedException();
        }

        public override void Flush()
        {
            FlushCalls++;
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCalls++;
                disposed = true;
                if (DisposeFailure != null)
                {
                    throw DisposeFailure;
                }
            }
            base.Dispose(disposing);
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(TestReadStream));
            }
        }
    }
}
