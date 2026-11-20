// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;

namespace MdfLib.Tests.TestDoubles
{
    internal sealed class SparseReadStream : Stream
    {
        private readonly long length;
        private readonly Dictionary<long, byte[]> segments = new Dictionary<long, byte[]>();
        private long position;

        internal SparseReadStream(long length) { this.length = length; }
        internal void Add(long offset, byte[] bytes) { segments.Add(offset, bytes); }
        internal int ReadCalls { get; private set; }
        internal long BytesRead { get; private set; }
        internal int LargestReadRequest { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalls++;
            LargestReadRequest = Math.Max(LargestReadRequest, count);
            int received = (int)Math.Min(count, length - position);
            Array.Clear(buffer, offset, received);
            foreach (KeyValuePair<long, byte[]> item in segments)
            {
                long first = Math.Max(position, item.Key);
                long last = Math.Min(position + received, item.Key + item.Value.Length);
                if (last > first)
                {
                    Array.Copy(item.Value, (int)(first - item.Key), buffer, offset + (int)(first - position), (int)(last - first));
                }
            }
            position += received;
            BytesRead += received;
            return received;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            if (origin != SeekOrigin.Begin || offset < 0 || offset > length) { throw new ArgumentOutOfRangeException(nameof(offset)); }
            position = offset;
            return position;
        }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Flush() { throw new NotSupportedException(); }
    }
}
