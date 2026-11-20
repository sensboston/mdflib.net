// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Threading;
using MdfLib.Compression;
using MdfLib.Formats;
using MdfLib.Internal;
using MdfLib.IO;

namespace MdfLib.Reading
{
    // One cursor/cache per enumeration; the immutable map belongs to its reader-owned record layout.
    internal sealed class Mdf4DataCursor
    {
        private readonly MdfReader reader;
        private readonly BinaryInput input;
        private readonly Mdf4DataMap map;
        private readonly CancellationToken token;
        private readonly Action checkpoint;
        private ulong cachedAddress;
        private byte[] cachedBytes;

        internal Mdf4DataCursor(MdfReader reader, BinaryInput input, Mdf4DataMap map, CancellationToken token)
        {
            this.reader = reader;
            this.input = input;
            this.map = map;
            this.token = token;
            checkpoint = () => { reader.CheckOpen(); token.ThrowIfCancellationRequested(); };
        }

        internal ulong Total => map.Total;

        internal void Read(ulong start, byte[] buffer, int count, int destinationOffset = 0)
        {
            checkpoint();
            int low = 0, high = map.Segments.Length - 1;
            while (low < high)
            {
                int middle = low + (high - low + 1) / 2;
                if (map.Segments[middle].Start <= start) { low = middle; } else { high = middle - 1; }
            }
            int written = 0;
            while (written < count)
            {
                checkpoint();
                Mdf4DataMap.Segment segment = map.Segments[low++];
                ulong offset = start - segment.Start;
                int portion = (int)Math.Min((ulong)(count - written), segment.Length - offset);
                if (segment.Block == null || segment.Block.Type != "##DZ")
                {
                    cachedBytes = null;
                    cachedAddress = 0;
                    if (segment.Block == null) { Array.Clear(buffer, destinationOffset + written, portion); }
                    else
                    {
                        // Separate DV/DI streams must not hide reported live truncation on a cached map.
                        // Existing DT semantics remain unchanged; no sample body is read during map construction.
                        if (segment.Block.Type != "##DT") { ValidateExtent(segment); }
                        FormatInput.ReadAt(input, segment.Block.DataOffset + offset, buffer, portion, segment.Block.Type, token, destinationOffset + written);
                    }
                }
                else
                {
                    if (cachedBytes == null || cachedAddress != segment.Block.Offset)
                    {
                        cachedBytes = null;
                        cachedAddress = 0;
                        byte[] decoded;
                        DecodedBlockCache shared = reader.BlockCache;
                        if (shared == null || !shared.TryGet(segment.Block.Offset, out decoded))
                        {
                            decoded = Decode(segment);
                            if (shared != null) { shared.Add(segment.Block.Offset, decoded); }
                        }
                        checkpoint();
                        cachedBytes = decoded;
                        cachedAddress = segment.Block.Offset;
                    }
                    Array.Copy(cachedBytes, (int)offset, buffer, destinationOffset + written, portion);
                }
                written += portion;
                start += (ulong)portion;
            }
            checkpoint();
        }

        private void ValidateExtent(Mdf4DataMap.Segment segment)
        {
            long position;
            if (!BinaryBounds.TryGetRange(input.CurrentBoundedLength, segment.Block.Offset, segment.Block.Length, out position))
            {
                throw new MdfFormatException("The selected data extent is outside the current bounded source.", "MDF007", segment.Block.Offset, segment.Block.Type);
            }
        }

        private byte[] Decode(Mdf4DataMap.Segment segment)
        {
            checkpoint();
            ulong maximum = (ulong)reader.Limits.MaximumReadBufferBytes;
            ulong needed = Math.Max(segment.Length, segment.CompressedLength);
            if (needed > maximum)
            {
                throw new MdfResourceLimitException(nameof(MdfReaderOptions.MaximumReadBufferBytes), maximum, needed, segment.Block.Offset);
            }
            ValidateExtent(segment);
            checkpoint();
            byte[] compressed = new byte[(int)segment.CompressedLength];
            FormatInput.ReadAt(input, segment.Block.DataOffset + 24, compressed, compressed.Length, segment.Block.Type, token);
            checkpoint();
            byte[] inflated;
            try { inflated = ZlibInflater.Inflate(compressed, (int)segment.Length, (int)maximum, checkpoint); }
            catch (InvalidDataException error)
            {
                throw new MdfFormatException("Invalid zlib data, declared output size or checksum.", "MDF015", segment.Block.Offset, segment.Block.Type, error);
            }
            if (segment.ZipType == 0 || segment.Parameter > (uint)inflated.Length) { return inflated; }
            int columns = (int)segment.Parameter; // Positive and <= bounded Int32 output length.
            int rows = inflated.Length / columns;
            if (rows <= 1) { return inflated; }
            checkpoint();
            byte[] output = new byte[inflated.Length];
            int matrix = rows * columns;
            for (int column = 0; column < columns; column++)
            {
                checkpoint();
                for (int row = 0; row < rows; row++)
                {
                    if ((row & 4095) == 0) { checkpoint(); }
                    output[row * columns + column] = inflated[column * rows + row];
                }
            }
            Array.Copy(inflated, matrix, output, matrix, inflated.Length - matrix);
            checkpoint();
            return output;
        }
    }
}
