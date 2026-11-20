// Copyright 2021 Ingemar Hedvall
// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Common wire header layouts adapted from the pinned MIT upstream. Bounds/cache policy is new.
using System;
using System.Collections.Generic;
using System.Threading;
using MdfLib.Internal;
using MdfLib.IO;

namespace MdfLib.Formats
{
    internal sealed class BlockResolver
    {
        private readonly BinaryInput input;
        private readonly bool mdf4;
        private readonly ReadBudget budget;
        private readonly Dictionary<ulong, BlockHeader> cache = new Dictionary<ulong, BlockHeader>();
        private readonly Dictionary<ulong, ulong> rawRanges = new Dictionary<ulong, ulong>();
        private readonly BlockExtentIndex extents = new BlockExtentIndex();

        internal BlockResolver(BinaryInput input, bool mdf4, ReadBudget budget)
        {
            this.input = input ?? throw new ArgumentNullException(nameof(input));
            this.mdf4 = mdf4;
            this.budget = budget ?? throw new ArgumentNullException(nameof(budget));
        }

        internal BlockHeader Resolve(ulong offset, string expectedType = null, int minimumSize = 0, int mdf3LinkCount = 0,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            long sourceLength = input.Length; // Also reject operations on a disposed input, including cache hits.
            if (minimumSize < 0 || mdf3LinkCount < 0)
            {
                throw new ArgumentOutOfRangeException(minimumSize < 0 ? nameof(minimumSize) : nameof(mdf3LinkCount));
            }
            if (offset == 0)
            {
                return null;
            }
            ValidateAddress(sourceLength, offset, mdf4 ? 24UL : 4UL, offset, expectedType);
            BlockHeader cached;
            if (cache.TryGetValue(offset, out cached))
            {
                ValidateExpected(cached, expectedType, minimumSize);
                if (!mdf4 && cached.Links.Count != mdf3LinkCount)
                {
                    throw new InvalidOperationException("A cached MDF 3 block cannot be reinterpreted with another link layout.");
                }
                return cached;
            }

            int commonSize = mdf4 ? 24 : 4;
            byte[] common = new byte[commonSize];
            FormatInput.ReadAt(input, offset, common, commonSize, expectedType, cancellationToken);
            string type = ReadType(common, offset);
            ulong length = mdf4 ? EndianBitConverter.ReadUInt64(common, 8, ByteOrder.LittleEndian)
                : EndianBitConverter.ReadUInt16(common, 2, ByteOrder.LittleEndian);
            ulong linkCount = mdf4 ? EndianBitConverter.ReadUInt64(common, 16, ByteOrder.LittleEndian) : (ulong)mdf3LinkCount;
            ulong linkSize = mdf4 ? 8UL : 4UL;
            long position;
            if (length < (ulong)commonSize || !BinaryBounds.TryGetRange(sourceLength, offset, length, out position)
                || linkCount > (length - (ulong)commonSize) / linkSize || length < (ulong)minimumSize)
            {
                throw new MdfFormatException("Invalid block length, link section or required minimum size.", "MDF006", offset, type);
            }
            if (expectedType != null && type != expectedType)
            {
                throw new MdfFormatException("Unexpected block type; expected " + expectedType + ".", "MDF005", offset, type);
            }
            CheckOverlap(position, position + (long)length, type);
            budget.ReserveBlock(linkCount, offset); // Bounds count before int conversion, multiplication and allocation.
            ulong[] links = new ulong[(int)linkCount];
            for (int index = 0; index < links.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ulong fieldOffset = offset + (ulong)commonSize + (ulong)index * linkSize;
                try
                {
                    links[index] = mdf4 ? input.ReadUInt64(ByteOrder.LittleEndian, cancellationToken)
                        : input.ReadUInt32(ByteOrder.LittleEndian, cancellationToken);
                }
                catch (System.IO.EndOfStreamException error)
                {
                    throw new MdfFormatException("Truncated link section.", "MDF004", fieldOffset, type, error);
                }
                if (links[index] != 0)
                {
                    // MDF 3 DG's fourth field is a headerless data extent, not a block pointer.
                    // Its length is checked after CG counts are known; an empty extent may start at EOF.
                    // All other outgoing metadata/tail links retain their strict byte/header requirement.
                    ulong required = mdf4 ? 24UL : type == "DG" && links.Length == 4 && index == 3 ? 0UL : 1UL;
                    ValidateAddress(sourceLength, links[index], required, fieldOffset, type);
                }
            }
            ulong dataOffset = offset + (ulong)commonSize + linkCount * linkSize;
            BlockHeader result = new BlockHeader(this, offset, type, length, dataOffset, links);
            extents.Add(position, position + (long)length);
            cache.Add(offset, result);
            return result;
        }

        internal byte[] ReadPayload(BlockHeader block, int count, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            long sourceLength = input.Length;
            if (block == null)
            {
                throw new ArgumentNullException(nameof(block));
            }
            if (!ReferenceEquals(block.Owner, this))
            {
                throw new ArgumentException("The block belongs to another document.", nameof(block));
            }
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            if ((ulong)count > block.DataLength || block.DataOffset > (ulong)sourceLength
                || (ulong)count > (ulong)sourceLength - block.DataOffset)
            {
                throw new MdfFormatException("The requested payload exceeds the declared block.", "MDF006", block.Offset, block.Type);
            }
            budget.ReservePayload(count, block.Offset);
            if (count == 0)
            {
                return Array.Empty<byte>();
            }
            byte[] result = new byte[count];
            FormatInput.ReadAt(input, block.DataOffset, result, count, block.Type, cancellationToken);
            return result;
        }

        internal void ValidateAdditionalLink(ulong address, ulong fieldOffset, string type)
        {
            if (address != 0) { ValidateAddress(input.Length, address, mdf4 ? 24UL : 1UL, fieldOffset, type); }
        }

        internal void RegisterRawDataRange(ulong offset, ulong byteCount, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            long position;
            if (offset < 64 || !BinaryBounds.TryGetRange(input.CurrentBoundedLength, offset, byteCount, out position))
            {
                throw new MdfFormatException("The declared headerless record range is outside the source.", "MDF007", offset, "DT");
            }
            if (byteCount == 0) { return; }
            ulong cached;
            if (rawRanges.TryGetValue(offset, out cached) && cached == byteCount) { return; }
            // The Stream bound proves the end fits Int64 before either cast or addition.
            long end = position + (long)byteCount;
            CheckOverlap(position, end, "DT");
            budget.ReserveMetadata(128, offset);
            extents.Add(position, end);
            rawRanges.Add(offset, byteCount);
        }

        private void ValidateAddress(long length, ulong address, ulong requiredBytes, ulong errorOffset, string type)
        {
            long position;
            if (address < 64 || !BinaryBounds.TryGetRange(length, address, requiredBytes, out position))
            {
                throw new MdfFormatException("A nonzero link is outside the permitted source range.", "MDF007", errorOffset, type);
            }
            if (mdf4 && (address & 7) != 0)
            {
                throw new MdfFormatException("MDF 4 block address is not aligned to eight bytes.", "MDF008", errorOffset, type);
            }
        }

        private string ReadType(byte[] common, ulong offset)
        {
            int start = mdf4 ? 2 : 0;
            if ((mdf4 && (common[0] != (byte)'#' || common[1] != (byte)'#'))
                || common[start] < (byte)'A' || common[start] > (byte)'Z'
                || common[start + 1] < (byte)'A' || common[start + 1] > (byte)'Z')
            {
                throw new MdfFormatException("Invalid block identifier.", "MDF005", offset);
            }
            return mdf4 ? "##" + (char)common[2] + (char)common[3] : new string(new[] { (char)common[0], (char)common[1] });
        }

        private static void ValidateExpected(BlockHeader block, string type, int minimumSize)
        {
            if (type != null && block.Type != type)
            {
                throw new MdfFormatException("Unexpected cached block type; expected " + type + ".", "MDF005", block.Offset, block.Type);
            }
            if (block.Length < (ulong)minimumSize)
            {
                throw new MdfFormatException("Cached block is shorter than the required minimum.", "MDF006", block.Offset, block.Type);
            }
        }

        private void CheckOverlap(long start, long end, string type)
        {
            if (extents.Overlaps(start, end))
            {
                throw new MdfFormatException("Different block ranges overlap.", "MDF011", (ulong)start, type);
            }
        }
    }
}
