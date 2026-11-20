// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
namespace MdfLib
{
    /// <summary>Configures bounded header, metadata and streaming sample reading. Open validates and snapshots all values.</summary>
    internal sealed class MdfReaderOptions
    {
        /// <summary>Creates options with finite, documented defaults.</summary>
        public MdfReaderOptions()
        {
        }

        /// <summary>Gets or sets the positive block-count budget; default 100,000.</summary>
        public int MaximumBlockCount { get; set; } = 100_000;

        /// <summary>Gets or sets the positive per-block link budget; default 4,096.</summary>
        public int MaximumLinksPerBlock { get; set; } = 4_096;

        /// <summary>Gets or sets the positive total-link budget; default 1,000,000.</summary>
        public int MaximumTotalLinks { get; set; } = 1_000_000;

        /// <summary>Gets or sets the positive traversal depth budget (root is one); default 256.</summary>
        public int MaximumTraversalDepth { get; set; } = 256;

        /// <summary>Gets or sets the positive single materialization limit in bytes; default 1 MiB.</summary>
        /// <remarks>Each needed DZ compressed/original array and the interleaved counter array (8 bytes per CG)
        /// must fit separately. A small sample range does not bypass a DZ needed for ID validation.</remarks>
        public int MaximumReadBufferBytes { get; set; } = 1_048_576;

        /// <summary>Gets or sets the positive whole TX/MD payload limit; default 1 MiB.</summary>
        public int MaximumTextBytes { get; set; } = 1_048_576;

        /// <summary>Gets or sets the positive XML character limit (UTF-16 units); default 1 Mi.</summary>
        public int MaximumXmlCharacters { get; set; } = 1_048_576;

        /// <summary>Gets or sets the positive XML element-depth limit (root is one); default 64.</summary>
        public int MaximumXmlDepth { get; set; } = 64;

        /// <summary>Gets or sets the positive per-batch output-accounting limit; default 8 MiB.</summary>
        /// <remarks>Rows can be reduced below the requested batch size. Not a cumulative sample budget.</remarks>
        public int MaximumSampleBatchBytes { get; set; } = 8_388_608;

        /// <summary>Gets or sets the positive metadata accounting budget in bytes; default 64 MiB.</summary>
        /// <remarks>This is not an exact measurement of GC heap overhead.</remarks>
        public long MaximumMetadataBytes { get; set; } = 67_108_864;

        /// <summary>Gets or sets the reader-wide cache of decoded DZ blocks in bytes; default 0 (only the current block of
        /// each enumeration is kept).</summary>
        /// <remarks>A positive value keeps recently decoded blocks
        /// across ReadSamples enumerations (least recently used first out), so reading channels one by one inflates each block once. Must be 0 or positive.</remarks>
        public long DecodedBlockCacheBytes { get; set; }
    }
}
