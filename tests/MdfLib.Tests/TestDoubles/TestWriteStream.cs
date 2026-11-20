// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;

namespace MdfLib.Tests.TestDoubles
{
    internal sealed class TestWriteStream : Stream
    {
        private readonly MemoryStream saved = new MemoryStream();
        private long position, length;
        internal bool RetainBytes = true, Writable = true, Seekable = true, Disposed;
        internal bool WrongSeekReturn, WrongSeekPosition, SilentShortWrite;
        internal int FailWriteAt = -1, FailSeekAt = -1, FailFlushAt = -1, PartialBytes = -1;
        internal int Writes, Seeks, Flushes, Disposals, Reads, SetLengths, MaximumWrite;
        internal long WrittenBytes;
        internal Action<TestWriteStream> BeforeWrite, AfterWrite, BeforeSeek, BeforeFlush, OnLength, OnPosition;
        internal Exception Failure = new IOException("injected destination failure");
        internal readonly HashSet<byte[]> Buffers = new HashSet<byte[]>();
        internal byte[] Snapshot() { return saved.ToArray(); }
        internal void ChangeLength(long value) { length = value; }
        internal void ChangePosition(long value) { position = value; }
        public override bool CanRead { get { return false; } }
        public override bool CanSeek { get { return Seekable && !Disposed; } }
        public override bool CanWrite { get { return Writable && !Disposed; } }
        public override long Length { get { Check(); OnLength?.Invoke(this); return length; } }
        public override long Position { get { Check(); OnPosition?.Invoke(this); return position; } set { Check(); position = value; } }
        public override long Seek(long offset, SeekOrigin origin)
        {
            Check(); Seeks++; BeforeSeek?.Invoke(this); Check();
            if (Seeks == FailSeekAt) { throw Failure; }
            if (origin != SeekOrigin.Begin || offset < 0) { throw new ArgumentException("Only absolute nonnegative seeks expected."); }
            position = offset + (WrongSeekPosition ? 1 : 0);
            return offset + (WrongSeekReturn ? 1 : 0);
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(); Writes++; MaximumWrite = Math.Max(MaximumWrite, count); Buffers.Add(buffer);
            BeforeWrite?.Invoke(this); Check();
            if (Writes == FailWriteAt)
            {
                if (PartialBytes >= 0) { WriteCore(buffer, offset, Math.Min(PartialBytes, count)); }
                throw Failure;
            }
            WriteCore(buffer, offset, SilentShortWrite ? count - 1 : count);
            AfterWrite?.Invoke(this);
        }
        private void WriteCore(byte[] buffer, int offset, int count)
        {
            if (RetainBytes)
            {
                saved.Position = position;
                saved.Write(buffer, offset, count);
            }
            position += count;
            length = Math.Max(length, position);
            WrittenBytes += count;
        }
        public override void Flush()
        {
            Check(); Flushes++; BeforeFlush?.Invoke(this); Check();
            if (Flushes == FailFlushAt) { throw Failure; }
        }
        public override int Read(byte[] buffer, int offset, int count) { Reads++; throw new NotSupportedException(); }
        public override void SetLength(long value) { SetLengths++; throw new NotSupportedException("No hidden truncation is permitted."); }
        protected override void Dispose(bool disposing)
        {
            Disposals++; Disposed = true;
            base.Dispose(disposing);
        }
        private void Check() { if (Disposed) { throw new ObjectDisposedException(nameof(TestWriteStream)); } }
    }
}
