// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Threading;

namespace MdfLib.IO
{
    internal sealed class BinaryInput : IDisposable
    {
        internal const int DefaultMaximumBufferBytes = 1_048_576;
        internal const int MaximumReadChunkBytes = 65_536;

        private readonly Stream stream;
        private readonly bool leaveOpen;
        private readonly long length;
        private readonly int maximumBufferBytes;
        private readonly byte[] scratch;
        private bool disposed;

        internal BinaryInput(Stream stream, bool leaveOpen = false, int maximumBufferBytes = DefaultMaximumBufferBytes)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }
            if (maximumBufferBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumBufferBytes));
            }
            if (!stream.CanRead || !stream.CanSeek)
            {
                throw new ArgumentException("A readable, seekable stream is required.", nameof(stream));
            }

            long initialLength = stream.Length;
            if (initialLength < 0)
            {
                throw new ArgumentException("The stream reported a negative length.", nameof(stream));
            }

            // Transfer ownership only after validation succeeds; the initial position is unchanged.
            this.stream = stream;
            this.leaveOpen = leaveOpen;
            this.length = initialLength;
            this.maximumBufferBytes = maximumBufferBytes;
            scratch = new byte[8];
        }

        internal long Length
        {
            get
            {
                ThrowIfDisposed();
                return length;
            }
        }

        // Raw headerless storage has no DT header to revalidate. Never extend the document's captured bound,
        // but detect a newly reported truncation before allocating a sample input/output buffer.
        internal long CurrentBoundedLength
        {
            get
            {
                ThrowIfDisposed();
                long current = stream.Length;
                if (current < 0) { throw new IOException("The stream reported a negative current length."); }
                return Math.Min(length, current);
            }
        }

        internal long Position
        {
            get
            {
                ThrowIfDisposed();
                return GetPosition();
            }
        }

        internal int MaximumBufferBytes
        {
            get
            {
                ThrowIfDisposed();
                return maximumBufferBytes;
            }
        }

        internal void Seek(long position)
        {
            ThrowIfDisposed();
            if (position < 0 || position > length)
            {
                throw new ArgumentOutOfRangeException(nameof(position));
            }
            if (stream.Seek(position, SeekOrigin.Begin) != position)
            {
                throw new IOException("The stream did not seek to the requested position.");
            }
        }

        internal void ReadExactly(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default(CancellationToken))
        {
            ThrowIfDisposed();
            BinaryBounds.ValidateBufferRange(buffer, offset, count);
            ValidateRead(count, cancellationToken);
            ReadCore(buffer, offset, count, cancellationToken);
        }

        internal byte[] ReadBytes(int count, CancellationToken cancellationToken = default(CancellationToken))
        {
            ThrowIfDisposed();
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            if (count > maximumBufferBytes)
            {
                throw new InvalidOperationException("The requested buffer exceeds the configured read buffer limit.");
            }
            ValidateRead(count, cancellationToken);
            if (count == 0)
            {
                return Array.Empty<byte>();
            }

            byte[] result = new byte[count];
            ReadCore(result, 0, count, cancellationToken);
            return result;
        }

        internal byte ReadByte(CancellationToken cancellationToken = default(CancellationToken))
        {
            ReadExactly(scratch, 0, 1, cancellationToken);
            return scratch[0];
        }

        internal ushort ReadUInt16(ByteOrder byteOrder, CancellationToken cancellationToken = default(CancellationToken))
        {
            ReadPrimitive(2, byteOrder, cancellationToken);
            return EndianBitConverter.ReadUInt16(scratch, 0, byteOrder);
        }

        internal uint ReadUInt32(ByteOrder byteOrder, CancellationToken cancellationToken = default(CancellationToken))
        {
            ReadPrimitive(4, byteOrder, cancellationToken);
            return EndianBitConverter.ReadUInt32(scratch, 0, byteOrder);
        }

        internal ulong ReadUInt64(ByteOrder byteOrder, CancellationToken cancellationToken = default(CancellationToken))
        {
            ReadPrimitive(8, byteOrder, cancellationToken);
            return EndianBitConverter.ReadUInt64(scratch, 0, byteOrder);
        }

        internal short ReadInt16(ByteOrder byteOrder, CancellationToken cancellationToken = default(CancellationToken))
        {
            ReadPrimitive(2, byteOrder, cancellationToken);
            return EndianBitConverter.ReadInt16(scratch, 0, byteOrder);
        }

        internal int ReadInt32(ByteOrder byteOrder, CancellationToken cancellationToken = default(CancellationToken))
        {
            ReadPrimitive(4, byteOrder, cancellationToken);
            return EndianBitConverter.ReadInt32(scratch, 0, byteOrder);
        }

        internal long ReadInt64(ByteOrder byteOrder, CancellationToken cancellationToken = default(CancellationToken))
        {
            ReadPrimitive(8, byteOrder, cancellationToken);
            return EndianBitConverter.ReadInt64(scratch, 0, byteOrder);
        }

        internal float ReadSingle(ByteOrder byteOrder, CancellationToken cancellationToken = default(CancellationToken))
        {
            ReadPrimitive(4, byteOrder, cancellationToken);
            return EndianBitConverter.ReadSingle(scratch, 0, byteOrder);
        }

        internal double ReadDouble(ByteOrder byteOrder, CancellationToken cancellationToken = default(CancellationToken))
        {
            ReadPrimitive(8, byteOrder, cancellationToken);
            return EndianBitConverter.ReadDouble(scratch, 0, byteOrder);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            if (!leaveOpen)
            {
                stream.Dispose();
            }
        }

        private void ReadPrimitive(int width, ByteOrder byteOrder, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            EndianBitConverter.ValidateByteOrder(byteOrder);
            ReadExactly(scratch, 0, width, cancellationToken);
        }

        private void ValidateRead(int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (count > length - GetPosition())
            {
                throw new EndOfStreamException("The requested range extends beyond the input length.");
            }
        }

        private void ReadCore(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int remaining = count;
            while (remaining != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int requested = Math.Min(remaining, MaximumReadChunkBytes);
                int received = stream.Read(buffer, offset, requested);
                if (received == 0)
                {
                    throw new EndOfStreamException("The stream ended before the requested bytes were read.");
                }
                if (received < 0 || received > requested)
                {
                    throw new IOException("The stream returned an invalid read count.");
                }
                offset += received;
                remaining -= received;
            }
        }

        private long GetPosition()
        {
            long position = stream.Position;
            if (position < 0 || position > length)
            {
                throw new IOException("The stream position is outside the captured input range.");
            }
            return position;
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(BinaryInput));
            }
        }
    }
}
