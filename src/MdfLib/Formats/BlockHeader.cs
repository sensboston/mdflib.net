// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.ObjectModel;

namespace MdfLib.Formats
{
    internal sealed class BlockHeader
    {
        internal BlockHeader(object owner, ulong offset, string type, ulong length, ulong dataOffset, ulong[] links)
        {
            Owner = owner;
            Offset = offset;
            Type = type;
            Length = length;
            DataOffset = dataOffset;
            Links = Array.AsReadOnly(links);
        }

        internal object Owner { get; }
        internal ulong Offset { get; }
        internal string Type { get; }
        internal ulong Length { get; }
        internal ulong DataOffset { get; }
        internal ReadOnlyCollection<ulong> Links { get; }
        internal ulong DataLength => Length - (DataOffset - Offset);
    }
}
