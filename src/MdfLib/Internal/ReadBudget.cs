// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
namespace MdfLib.Internal
{
    internal sealed class ReadBudget
    {
        private readonly ReaderLimits limits;
        private ulong blocks;
        private ulong links;
        private ulong metadataBytes;

        internal ReadBudget(ReaderLimits limits)
        {
            this.limits = limits;
        }

        internal void ReserveBlock(ulong linkCount, ulong offset)
        {
            Check(nameof(MdfReaderOptions.MaximumBlockCount), (ulong)limits.MaximumBlockCount, blocks, 1, offset);
            Check(nameof(MdfReaderOptions.MaximumLinksPerBlock), (ulong)limits.MaximumLinksPerBlock, 0, linkCount, offset);
            Check(nameof(MdfReaderOptions.MaximumTotalLinks), (ulong)limits.MaximumTotalLinks, links, linkCount, offset);
            // linkCount is bounded by a positive int before multiplication.
            Check(nameof(MdfReaderOptions.MaximumReadBufferBytes), (ulong)limits.MaximumReadBufferBytes, 0, linkCount * 8, offset);
            ulong bytes = 128 + linkCount * 8;
            Check(nameof(MdfReaderOptions.MaximumMetadataBytes), (ulong)limits.MaximumMetadataBytes, metadataBytes, bytes, offset);
            blocks++;
            links += linkCount;
            metadataBytes += bytes;
        }

        internal void ReserveAdditionalLinks(int baseCount, int additionalCount, ulong offset)
        {
            // MDF 3 CN/CG tail links are not adjacent to the common-header link section.
            ulong added = (ulong)additionalCount;
            Check(nameof(MdfReaderOptions.MaximumLinksPerBlock), (ulong)limits.MaximumLinksPerBlock, (ulong)baseCount, added, offset);
            Check(nameof(MdfReaderOptions.MaximumTotalLinks), (ulong)limits.MaximumTotalLinks, links, added, offset);
            Check(nameof(MdfReaderOptions.MaximumMetadataBytes), (ulong)limits.MaximumMetadataBytes, metadataBytes, added * 8, offset);
            links += added;
            metadataBytes += added * 8;
        }

        internal void ReservePayload(int count, ulong offset)
        {
            Check(nameof(MdfReaderOptions.MaximumReadBufferBytes), (ulong)limits.MaximumReadBufferBytes, 0, (ulong)count, offset);
            Check(nameof(MdfReaderOptions.MaximumMetadataBytes), (ulong)limits.MaximumMetadataBytes, metadataBytes, (ulong)count, offset);
            metadataBytes += (ulong)count;
        }

        internal void ReserveMetadata(ulong count, ulong offset)
        {
            Check(nameof(MdfReaderOptions.MaximumMetadataBytes), (ulong)limits.MaximumMetadataBytes, metadataBytes, count, offset);
            metadataBytes += count;
        }

        internal void CheckDepth(int depth, ulong offset)
        {
            Check(nameof(MdfReaderOptions.MaximumTraversalDepth), (ulong)limits.MaximumTraversalDepth, 0, (ulong)depth, offset);
        }

        internal static void Check(string name, ulong maximum, ulong used, ulong amount, ulong offset)
        {
            if (used > maximum || amount > maximum - used)
            {
                // Saturation is only for diagnostic reporting, never for accepting a size.
                ulong requested = amount > ulong.MaxValue - used ? ulong.MaxValue : used + amount;
                throw new MdfResourceLimitException(name, maximum, requested, offset);
            }
        }
    }
}
