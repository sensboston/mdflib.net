// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// managed equivalent of the upstream pre-trigger sample queue; synchronous and bounded, no worker thread.
using System;
using System.Collections.Generic;
using System.Threading;

namespace MdfLib
{
    /// <summary>Buffers timestamped samples before a trigger and forwards those inside the pre-trigger window to a writer.</summary>
    /// <remarks>Before <see cref="Trigger"/> samples are kept in arrival order; samples older than the newest time minus
    /// the pre-trigger window are discarded, and at most <see cref="Capacity"/> samples are retained (oldest dropped first).
    /// After the trigger every sample is written immediately. Not thread-safe, like <see cref="MdfWriter"/>.</remarks>
    internal sealed class MdfPreTriggerQueue
    {
        private struct Entry
        {
            internal int Group;
            internal double Time;
            internal MdfSampleValue[] Values;
        }

        private readonly MdfWriter writer;
        private readonly Queue<Entry> queue = new Queue<Entry>();
        private double newest = double.NegativeInfinity;

        /// <summary>Creates a queue for a writer that is still configuring or already recording.</summary>
        /// <param name="writer">The destination writer; it is not owned.</param>
        /// <param name="preTriggerSeconds">Non-negative finite window kept before the trigger time.</param>
        /// <param name="capacity">Maximum number of buffered samples (1 or more).</param>
        public MdfPreTriggerQueue(MdfWriter writer, double preTriggerSeconds, int capacity = 65536)
        {
            if (writer == null) { throw new ArgumentNullException(nameof(writer)); }
            if (double.IsNaN(preTriggerSeconds) || double.IsInfinity(preTriggerSeconds) || preTriggerSeconds < 0)
            { throw new ArgumentOutOfRangeException(nameof(preTriggerSeconds)); }
            if (capacity <= 0) { throw new ArgumentOutOfRangeException(nameof(capacity)); }
            this.writer = writer;
            PreTriggerSeconds = preTriggerSeconds;
            Capacity = capacity;
        }

        /// <summary>Gets the pre-trigger window in seconds.</summary>
        public double PreTriggerSeconds { get; }
        /// <summary>Gets the maximum number of buffered samples.</summary>
        public int Capacity { get; }
        /// <summary>Gets the number of currently buffered samples.</summary>
        public int Count { get { return queue.Count; } }
        /// <summary>Gets the number of buffered samples discarded by the window or the capacity bound.</summary>
        public long DroppedCount { get; private set; }
        /// <summary>Gets whether <see cref="Trigger"/> has run; later samples go straight to the writer.</summary>
        public bool IsTriggered { get; private set; }

        /// <summary>Buffers (before the trigger) or writes (after it) one complete row of a channel group.</summary>
        /// <param name="groupIndex">Channel group of the row.</param>
        /// <param name="time">Finite sample time in seconds used for the window; not written by itself.</param>
        /// <param name="values">The complete row, copied when buffered.</param>
        /// <param name="cancellationToken">Cancels a direct write.</param>
        public void Add(int groupIndex, double time, IReadOnlyList<MdfSampleValue> values, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (double.IsNaN(time) || double.IsInfinity(time)) { throw new ArgumentOutOfRangeException(nameof(time)); }
            if (values == null) { throw new ArgumentNullException(nameof(values)); }
            if (IsTriggered) { writer.SaveSample(groupIndex, values, cancellationToken); return; }
            if (groupIndex < 0 || groupIndex >= writer.ChannelGroupCount) { throw new ArgumentOutOfRangeException(nameof(groupIndex)); }
            if (time < newest - PreTriggerSeconds) { DroppedCount++; return; } // Already outside the window.
            MdfSampleValue[] copy = new MdfSampleValue[values.Count];
            for (int i = 0; i < copy.Length; i++) { copy[i] = values[i]; }
            if (time > newest) { newest = time; }
            queue.Enqueue(new Entry { Group = groupIndex, Time = time, Values = copy });
            while (queue.Count > Capacity || (queue.Count > 0 && queue.Peek().Time < newest - PreTriggerSeconds))
            {
                queue.Dequeue();
                DroppedCount++;
            }
        }

        /// <summary>Writes buffered rows with time at or after <paramref name="triggerTime"/> minus the window, in arrival order.</summary>
        /// <remarks>The writer must be recording (call <see cref="MdfWriter.StartMeasurement"/> first). Returns the number of rows written.
        /// On a writer failure the queue keeps the unwritten rows and stays untriggered.</remarks>
        public int Trigger(double triggerTime, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (double.IsNaN(triggerTime) || double.IsInfinity(triggerTime)) { throw new ArgumentOutOfRangeException(nameof(triggerTime)); }
            if (IsTriggered) { throw new InvalidOperationException("The queue has already been triggered."); }
            if (writer.State != MdfWriterState.Recording) { throw new InvalidOperationException("The writer must be recording before the trigger."); }
            double from = triggerTime - PreTriggerSeconds;
            int written = 0;
            while (queue.Count > 0)
            {
                Entry entry = queue.Peek();
                if (entry.Time >= from)
                {
                    writer.SaveSample(entry.Group, entry.Values, cancellationToken);
                    written++;
                }
                else { DroppedCount++; }
                queue.Dequeue();
            }
            IsTriggered = true;
            return written;
        }
    }
}
