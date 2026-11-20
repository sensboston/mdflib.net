// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
namespace MdfLib
{
    /// <summary>Explicit synchronous writer lifecycle; Dispose never finalizes a measurement.</summary>
    internal enum MdfWriterState
    {
        /// <summary>Channels can be configured; no bytes have been written.</summary>
        Configuring = 0,
        /// <summary>Configuration is frozen and raw records may be saved.</summary>
        Recording = 1,
        /// <summary>No more samples; explicit finalization is required.</summary>
        Stopped = 2,
        /// <summary>Counts, lengths and finalized identification have been written and flushed.</summary>
        Finalized = 3,
        /// <summary>An output operation failed; recording/finalization cannot be retried.</summary>
        Faulted = 4,
        /// <summary>Resources have been released; no further operation is permitted.</summary>
        Disposed = 5
    }
}
