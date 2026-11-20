// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;

namespace MdfLib
{
    /// <summary>An independently owned, immutable batch of selected scalar/array channels in requested order.</summary>
    internal sealed class MdfSampleBatch
    {
        private readonly MdfSampleValue[] values;
        private readonly int[] valueOffsets;
        private readonly int valuesPerRow;

        internal MdfSampleBatch(ulong firstSampleIndex, int sampleCount, IReadOnlyList<MdfChannel> channels,
            MdfSampleValue[] values, int valuesPerRow, int[] valueOffsets)
        {
            FirstSampleIndex = firstSampleIndex;
            SampleCount = sampleCount;
            Channels = channels;
            this.values = values;
            this.valuesPerRow = valuesPerRow;
            this.valueOffsets = valueOffsets;
        }

        /// <summary>Gets the first sample index within the channel group, not a timestamp or array-element ordinal.</summary>
        public ulong FirstSampleIndex { get; }
        /// <summary>Gets the number of record rows, not the number of array elements.</summary>
        public int SampleCount { get; }
        /// <summary>Gets selected logical channels in column order; an array occupies one column.</summary>
        public IReadOnlyList<MdfChannel> Channels { get; }

        /// <summary>Returns a scalar value by zero-based row and selected-column index.</summary>
        /// <exception cref="ArgumentOutOfRangeException">An index is outside this batch.</exception>
        /// <exception cref="InvalidOperationException">The column is an array; use the explicit element overload even for singleton arrays.</exception>
        public MdfSampleValue GetValue(int sampleIndex, int channelIndex)
        {
            CheckIndices(sampleIndex, channelIndex);
            if (Channels[channelIndex].Array != null)
            { throw new InvalidOperationException("An array column requires an explicit flattened element index."); }
            return values[sampleIndex * valuesPerRow + (valueOffsets == null ? channelIndex : valueOffsets[channelIndex])];
        }

        /// <summary>Returns one exact-kind element in row-major flattened order; a scalar accepts only element zero.</summary>
        /// <exception cref="ArgumentOutOfRangeException">A row, column or element index is outside this batch/shape.</exception>
        public MdfSampleValue GetValue(int sampleIndex, int channelIndex, ulong elementIndex)
        {
            CheckIndices(sampleIndex, channelIndex);
            MdfChannelArray array = Channels[channelIndex].Array;
            if (elementIndex >= (array == null ? 1 : array.ElementCount))
            { throw new ArgumentOutOfRangeException(nameof(elementIndex)); }
            return values[sampleIndex * valuesPerRow + (valueOffsets == null ? channelIndex : valueOffsets[channelIndex]) + (int)elementIndex];
        }

        private void CheckIndices(int sampleIndex, int channelIndex)
        {
            if (sampleIndex < 0 || sampleIndex >= SampleCount) { throw new ArgumentOutOfRangeException(nameof(sampleIndex)); }
            if (channelIndex < 0 || channelIndex >= Channels.Count) { throw new ArgumentOutOfRangeException(nameof(channelIndex)); }
        }
    }
}
