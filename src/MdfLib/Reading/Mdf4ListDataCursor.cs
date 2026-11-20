// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System.Threading;
using MdfLib.IO;

namespace MdfLib.Reading
{
    // One bounded decoded-DZ cache PER stream. Scatter directly into the existing normalized batch buffer;
    // no temporary column arrays, record index, master fetch, resampling, or reader-global decoded data.
    internal sealed class Mdf4ListDataCursor
    {
        private readonly Mdf4DataCursor values;
        private readonly Mdf4DataCursor invalidations;
        private readonly uint dataBytes;
        private readonly uint invalidationBytes;

        internal Mdf4ListDataCursor(MdfReader reader, BinaryInput input, RecordLayout layout, CancellationToken token)
            : this(reader, input, layout.ListDataMap, layout.DataBytes, layout.InvalidationBytes, token) { }

        // RV/RI reduction streams use the same pairing with 3 x data bytes per cycle.
        internal Mdf4ListDataCursor(MdfReader reader, BinaryInput input, Mdf4ListDataMap map, uint dataBytes, uint invalidationBytes, CancellationToken token)
        {
            values = new Mdf4DataCursor(reader, input, map.Values, token);
            invalidations = new Mdf4DataCursor(reader, input, map.Invalidations, token);
            this.dataBytes = dataBytes;
            this.invalidationBytes = invalidationBytes;
        }

        internal void ReadRows(ulong first, int rows, byte[] buffer, int recordSize, int destinationRow = 0)
        {
            for (int row = 0; row < rows; row++)
            {
                ulong index = first + (ulong)row;
                int target = (destinationRow + row) * recordSize;
                values.Read(index * dataBytes, buffer, (int)dataBytes, target);
                if (invalidationBytes != 0)
                { invalidations.Read(index * invalidationBytes, buffer, (int)invalidationBytes, target + (int)dataBytes); }
            }
        }
    }
}
