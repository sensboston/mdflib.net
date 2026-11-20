// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// reader-wide LRU of decoded (inflated and untransposed) DZ payloads; the idea follows upstream readcache, the
// byte-bounded policy is original. Only one reader operation runs at a time, so no locking is needed.
using System.Collections.Generic;

namespace MdfLib.Internal
{
    internal sealed class DecodedBlockCache
    {
        private readonly long capacity;
        private readonly Dictionary<ulong, LinkedListNode<KeyValuePair<ulong, byte[]>>> index = new Dictionary<ulong, LinkedListNode<KeyValuePair<ulong, byte[]>>>();
        private readonly LinkedList<KeyValuePair<ulong, byte[]>> order = new LinkedList<KeyValuePair<ulong, byte[]>>();

        internal DecodedBlockCache(long capacity) { this.capacity = capacity; }

        internal long Bytes { get; private set; }
        internal long Hits { get; private set; }
        internal long Misses { get; private set; }
        internal int Count => index.Count;

        internal bool TryGet(ulong address, out byte[] bytes)
        {
            LinkedListNode<KeyValuePair<ulong, byte[]>> node;
            if (index.TryGetValue(address, out node))
            {
                order.Remove(node);
                order.AddFirst(node);
                bytes = node.Value.Value;
                Hits++;
                return true;
            }
            Misses++;
            bytes = null;
            return false;
        }

        // Blocks larger than the whole capacity are not retained.
        internal void Add(ulong address, byte[] bytes)
        {
            if (bytes.LongLength > capacity || index.ContainsKey(address)) { return; }
            while (Bytes + bytes.LongLength > capacity && order.Last != null)
            {
                LinkedListNode<KeyValuePair<ulong, byte[]>> last = order.Last;
                order.RemoveLast();
                index.Remove(last.Value.Key);
                Bytes -= last.Value.Value.LongLength;
            }
            index.Add(address, order.AddFirst(new KeyValuePair<ulong, byte[]>(address, bytes)));
            Bytes += bytes.LongLength;
        }
    }
}
