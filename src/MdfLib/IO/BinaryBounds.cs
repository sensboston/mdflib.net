// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;

namespace MdfLib.IO
{
    internal static class BinaryBounds
    {
        internal static bool TryGetRange(long streamLength, ulong offset, ulong byteCount, out long position)
        {
            if (streamLength < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(streamLength));
            }

            position = 0;
            ulong length = (ulong)streamLength;
            if (offset > length || byteCount > length - offset)
            {
                return false;
            }

            // The comparison above proves that the address fits in a Stream position.
            position = (long)offset;
            return true;
        }

        internal static bool TryGetBufferSize(ulong elementCount, ulong elementSize, int maximumBytes, out int byteCount)
        {
            if (maximumBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            }

            byteCount = 0;
            if (elementSize != 0 && elementCount > (ulong)maximumBytes / elementSize)
            {
                return false;
            }

            // Nonzero products have already been bounded by an int, before multiplication.
            byteCount = (int)(elementCount * elementSize);
            return true;
        }

        internal static void ValidateBufferRange(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }
            if (offset < 0 || offset > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }
            if (count < 0 || count > buffer.Length - offset)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
        }
    }
}
