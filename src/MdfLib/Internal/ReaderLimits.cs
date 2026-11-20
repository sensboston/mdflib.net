// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;

namespace MdfLib.Internal
{
    internal sealed class ReaderLimits
    {
        internal ReaderLimits(MdfReaderOptions options)
        {
            if (options == null)
            {
                options = new MdfReaderOptions();
            }
            MaximumBlockCount = Positive(options.MaximumBlockCount, nameof(options.MaximumBlockCount));
            MaximumLinksPerBlock = Positive(options.MaximumLinksPerBlock, nameof(options.MaximumLinksPerBlock));
            MaximumTotalLinks = Positive(options.MaximumTotalLinks, nameof(options.MaximumTotalLinks));
            MaximumTraversalDepth = Positive(options.MaximumTraversalDepth, nameof(options.MaximumTraversalDepth));
            MaximumReadBufferBytes = Positive(options.MaximumReadBufferBytes, nameof(options.MaximumReadBufferBytes));
            MaximumTextBytes = Positive(options.MaximumTextBytes, nameof(options.MaximumTextBytes));
            MaximumXmlCharacters = Positive(options.MaximumXmlCharacters, nameof(options.MaximumXmlCharacters));
            MaximumXmlDepth = Positive(options.MaximumXmlDepth, nameof(options.MaximumXmlDepth));
            MaximumSampleBatchBytes = Positive(options.MaximumSampleBatchBytes, nameof(options.MaximumSampleBatchBytes));
            MaximumMetadataBytes = options.MaximumMetadataBytes;
            if (MaximumMetadataBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options.MaximumMetadataBytes));
            }
            DecodedBlockCacheBytes = options.DecodedBlockCacheBytes;
            if (DecodedBlockCacheBytes < 0) { throw new ArgumentOutOfRangeException(nameof(options.DecodedBlockCacheBytes)); }
        }

        internal int MaximumBlockCount { get; }
        internal int MaximumLinksPerBlock { get; }
        internal int MaximumTotalLinks { get; }
        internal int MaximumTraversalDepth { get; }
        internal int MaximumReadBufferBytes { get; }
        internal long MaximumMetadataBytes { get; }
        internal int MaximumTextBytes { get; }
        internal int MaximumXmlCharacters { get; }
        internal int MaximumXmlDepth { get; }
        internal int MaximumSampleBatchBytes { get; }
        internal long DecodedBlockCacheBytes { get; }

        private static int Positive(int value, string name)
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(name);
            }
            return value;
        }
    }
}
