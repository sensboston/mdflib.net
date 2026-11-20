// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;

namespace MdfLib.Tests.TestDoubles
{
    // A sparse read/write/seekable stream: unwritten bytes read as zero, so multi-GiB offsets cost only touched pages.
    internal sealed class PagedStream : Stream
    {
        private const int PageBytes = 1 << 16;
        private readonly Dictionary<long, byte[]> pages = new Dictionary<long, byte[]>();
        private long length, position;

        internal PagedStream(byte[] initial, long length)
        {
            Write(initial, 0, initial.Length);
            this.length = length;
            position = 0;
        }

        internal int Pages => pages.Count;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => length;
        public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int total = (int)Math.Max(0, Math.Min(count, length - position));
            for (int done = 0; done < total;)
            {
                long page = (position + done) / PageBytes;
                int within = (int)((position + done) % PageBytes);
                int step = Math.Min(total - done, PageBytes - within);
                byte[] bytes;
                if (pages.TryGetValue(page, out bytes)) { Array.Copy(bytes, within, buffer, offset + done, step); }
                else { Array.Clear(buffer, offset + done, step); }
                done += step;
            }
            position += total;
            return total;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            for (int done = 0; done < count;)
            {
                long page = (position + done) / PageBytes;
                int within = (int)((position + done) % PageBytes);
                int step = Math.Min(count - done, PageBytes - within);
                byte[] bytes;
                if (!pages.TryGetValue(page, out bytes)) { bytes = new byte[PageBytes]; pages.Add(page, bytes); }
                Array.Copy(buffer, offset + done, bytes, within, step);
                done += step;
            }
            position += count;
            length = Math.Max(length, position);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? position + offset : length + offset;
            if (target < 0) { throw new IOException("Negative position."); }
            position = target;
            return position;
        }

        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Flush() { }
    }
}
