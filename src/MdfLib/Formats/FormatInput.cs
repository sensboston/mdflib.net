// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System.IO;
using System.Threading;
using MdfLib.IO;

namespace MdfLib.Formats
{
    internal static class FormatInput
    {
        internal static void ReadAt(BinaryInput input, ulong offset, byte[] buffer, int count, string blockType, CancellationToken token, int destinationOffset = 0)
        {
            long position;
            if (!BinaryBounds.TryGetRange(input.Length, offset, (ulong)count, out position))
            {
                throw new MdfFormatException("Required input bytes are truncated or outside the source.", "MDF004", offset, blockType,
                    new EndOfStreamException("The required range is not available."));
            }
            input.Seek(position);
            try
            {
                input.ReadExactly(buffer, destinationOffset, count, token);
            }
            catch (EndOfStreamException error)
            {
                throw new MdfFormatException("Required input bytes are truncated.", "MDF004", offset, blockType, error);
            }
        }
    }
}
