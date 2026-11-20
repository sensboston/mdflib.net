// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// an opt-in bounded producer/consumer queue in front of a synchronous writer, in the spirit of upstream
// writer4samplequeue; the bounded blocking queue, snapshot and fault policy are original. The synchronous writers stay primary.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace MdfLib
{
    /// <summary>Queues samples or bus frames for one <see cref="MdfWriter"/> or <see cref="MdfBusLogger"/> and writes them on a
    /// dedicated worker thread, so producers do not wait for compression or storage.</summary>
    /// <remarks>The queue is bounded: <see cref="SaveSample(int, IReadOnlyList{MdfSampleValue}, CancellationToken)"/> and
    /// <see cref="Write"/> block while it is full, the Try methods return false instead. Values and frames are copied when queued.
    /// Producers may call from several threads (order between threads is unspecified). Until <see cref="Complete"/> returns,
    /// only the worker may use the target, which must already be recording. Validation happens on the worker: the first
    /// failure faults this queue (later items are discarded) and is rethrown, as the inner exception of an
    /// <see cref="InvalidOperationException"/>, by every later call and by <see cref="Complete"/>.
    /// <see cref="Complete"/> drains the queue but neither stops nor finalizes the target.</remarks>
    internal sealed class MdfBackgroundWriter : IDisposable
    {
        private readonly MdfWriter writer;
        private readonly MdfBusLogger logger;
        private readonly BlockingCollection<Item> queue;
        private readonly Thread worker;
        private Exception fault;
        private long accepted, written;
        private int completed;

        private struct Item
        {
            internal int Group;
            internal MdfSampleValue[] Values;
            internal MdfBusMessage Message;
        }

        /// <summary>Starts a worker that saves samples to a recording <paramref name="writer"/>.</summary>
        /// <param name="writer">The target; not disposed by this queue.</param>
        /// <param name="capacity">The maximum number of queued items; default 4096.</param>
        public MdfBackgroundWriter(MdfWriter writer, int capacity = 4096) : this(capacity)
        {
            if (writer == null) { throw new ArgumentNullException(nameof(writer)); }
            this.writer = writer;
            worker.Start();
        }

        /// <summary>Starts a worker that writes frames to a recording bus <paramref name="logger"/>.</summary>
        /// <param name="logger">The target; not disposed by this queue.</param>
        /// <param name="capacity">The maximum number of queued frames; default 4096.</param>
        public MdfBackgroundWriter(MdfBusLogger logger, int capacity = 4096) : this(capacity)
        {
            if (logger == null) { throw new ArgumentNullException(nameof(logger)); }
            this.logger = logger;
            worker.Start();
        }

        private MdfBackgroundWriter(int capacity)
        {
            if (capacity <= 0) { throw new ArgumentOutOfRangeException(nameof(capacity)); }
            Capacity = capacity;
            queue = new BlockingCollection<Item>(new ConcurrentQueue<Item>(), capacity);
            worker = new Thread(Run) { IsBackground = true, Name = "MdfLib background writer" };
        }

        /// <summary>Gets the maximum number of queued items.</summary>
        public int Capacity { get; }
        /// <summary>Gets the number of items waiting for the worker.</summary>
        public int PendingCount { get { return queue.Count; } }
        /// <summary>Gets the number of queued items so far.</summary>
        public long AcceptedCount { get { return Interlocked.Read(ref accepted); } }
        /// <summary>Gets the number of items the target has accepted so far.</summary>
        public long WrittenCount { get { return Interlocked.Read(ref written); } }
        /// <summary>Gets the first worker failure, or null.</summary>
        public Exception Fault { get { return Volatile.Read(ref fault); } }

        /// <summary>Queues one sample of channel group 0, waiting while the queue is full.</summary>
        public void SaveSample(IReadOnlyList<MdfSampleValue> values, CancellationToken cancellationToken = default(CancellationToken))
        { SaveSample(0, values, cancellationToken); }

        /// <summary>Queues one sample of a channel group, waiting while the queue is full.</summary>
        public void SaveSample(int groupIndex, IReadOnlyList<MdfSampleValue> values, CancellationToken cancellationToken = default(CancellationToken))
        {
            Add(Sample(groupIndex, values), -1, cancellationToken);
        }

        /// <summary>Queues one sample of a channel group unless the queue is full.</summary>
        /// <returns>False when the queue is full; nothing is queued.</returns>
        public bool TrySaveSample(int groupIndex, IReadOnlyList<MdfSampleValue> values)
        {
            return Add(Sample(groupIndex, values), 0, CancellationToken.None);
        }

        /// <summary>Queues one bus frame, waiting while the queue is full.</summary>
        public void Write(MdfBusMessage message, CancellationToken cancellationToken = default(CancellationToken))
        {
            Add(Frame(message), -1, cancellationToken);
        }

        /// <summary>Queues one bus frame unless the queue is full.</summary>
        /// <returns>False when the queue is full; nothing is queued.</returns>
        public bool TryWrite(MdfBusMessage message)
        {
            return Add(Frame(message), 0, CancellationToken.None);
        }

        /// <summary>Stops accepting items, waits until the worker has written (or, after a fault, discarded) every queued item,
        /// and rethrows a worker failure. The target is then free for StopMeasurement/FinalizeMeasurement.</summary>
        /// <exception cref="InvalidOperationException">The worker failed; the inner exception is the failure.</exception>
        /// <exception cref="OperationCanceledException">Waiting was cancelled; the worker keeps draining.</exception>
        public void Complete(CancellationToken cancellationToken = default(CancellationToken))
        {
            Interlocked.Exchange(ref completed, 1);
            try { queue.CompleteAdding(); } catch (ObjectDisposedException) { }
            while (!worker.Join(20)) { cancellationToken.ThrowIfCancellationRequested(); }
            ThrowIfFaulted();
        }

        /// <summary>Completes without throwing (a failure stays in <see cref="Fault"/>) and releases the queue.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref completed, 2) == 2) { return; }
            try { queue.CompleteAdding(); } catch (ObjectDisposedException) { }
            worker.Join();
            queue.Dispose();
        }

        private Item Sample(int groupIndex, IReadOnlyList<MdfSampleValue> values)
        {
            if (writer == null) { throw new InvalidOperationException("This queue writes bus frames; use Write."); }
            if (values == null) { throw new ArgumentNullException(nameof(values)); }
            MdfSampleValue[] copy = new MdfSampleValue[values.Count];
            for (int i = 0; i < copy.Length; i++) { copy[i] = values[i]; }
            return new Item { Group = groupIndex, Values = copy };
        }

        private Item Frame(MdfBusMessage message)
        {
            if (logger == null) { throw new InvalidOperationException("This queue writes samples; use SaveSample."); }
            if (message == null) { throw new ArgumentNullException(nameof(message)); }
            return new Item { Message = message.Snapshot() };
        }

        private bool Add(Item item, int timeout, CancellationToken token)
        {
            ThrowIfFaulted();
            if (Volatile.Read(ref completed) != 0) { throw new InvalidOperationException("The background writer is completed."); }
            bool added;
            try { added = queue.TryAdd(item, timeout, token); }
            catch (InvalidOperationException error) { throw new InvalidOperationException("The background writer is completed.", error); }
            if (added) { Interlocked.Increment(ref accepted); }
            return added;
        }

        private void ThrowIfFaulted()
        {
            Exception error = Volatile.Read(ref fault);
            if (error != null) { throw new InvalidOperationException("The background writer faulted: " + error.Message, error); }
        }

        private void Run()
        {
            foreach (Item item in queue.GetConsumingEnumerable())
            {
                if (Volatile.Read(ref fault) != null) { continue; } // Discard after a failure; producers see the fault.
                try
                {
                    if (item.Message != null) { logger.Write(item.Message); }
                    else { writer.SaveSample(item.Group, item.Values); }
                    Interlocked.Increment(ref written);
                }
                catch (Exception error) { Interlocked.CompareExchange(ref fault, error, null); }
            }
        }
    }
}
