// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Writing
{
    public sealed class WriterFlushCallbackTests
    {
        public static IEnumerable<object[]> PreFlushPositionCallbacks()
        {
            foreach (string version in new[] { "3.30", "4.10", "4.20" })
            foreach (int flush in new[] { 1, 2 })
            foreach (string action in new[] { "dispose-owned", "dispose-open", "cancel", "error", "reentry" })
            { yield return new object[] { version, flush, action }; }
        }

        [Theory]
        [MemberData(nameof(PreFlushPositionCallbacks))]
        public void PreFlushPositionCallbackIsObservedBeforeCallingDestinationFlush(string version, int flush, string action)
        {
            TestWriteStream stream = new TestWriteStream();
            IOException failure = new IOException("pre-flush position getter failed");
            bool dispose = action.StartsWith("dispose", StringComparison.Ordinal);
            bool owned = action == "dispose-owned";
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            using (MdfWriter writer = MdfWriter.Create(stream, !owned, new MdfWriterOptions { Version = version }))
            {
                writer.AddChannel(WriterConfigurationTests.Channel(bits: 64));
                Assert.Equal(version == "3.30" ? 8 : 9, writer.RecordByteCount);
                writer.StartMeasurement();
                writer.SaveSample(new[] { MdfSampleValue.FromUnsigned(7) });
                writer.StopMeasurement();

                // Literal IO sequence for one UInt64 row: MDF3 count patch has two Position
                // checks before Flush1; MDF4 padding/count/DT-length patches have six.
                // Then post-Flush1 check, publish seek/write and final seek precede Flush2.
                int target = (version == "3.30" ? 3 : 7) + (flush - 1) * 5;
                int positions = 0, callbacks = 0;
                stream.OnPosition = destination =>
                {
                    if (++positions != target) { return; }
                    callbacks++;
                    Assert.Equal(flush - 1, destination.Flushes);
                    Assert.Equal(MdfWriterState.Stopped, writer.State);
                    if (dispose) { writer.Dispose(); }
                    else if (action == "cancel") { cancellation.Cancel(); }
                    else if (action == "error") { throw failure; }
                    else { Assert.Throws<InvalidOperationException>(() => writer.FinalizeMeasurement()); }
                };

                Action finalize = () => writer.FinalizeMeasurement(cancellation.Token);
                if (dispose)
                {
                    ObjectDisposedException error = Assert.Throws<ObjectDisposedException>(finalize);
                    // The writer checkpoint must win even if its owned stream was closed.
                    Assert.Equal(nameof(MdfWriter), error.ObjectName);
                }
                else if (action == "cancel")
                {
                    OperationCanceledException error = Assert.Throws<OperationCanceledException>(finalize);
                    Assert.Equal(cancellation.Token, error.CancellationToken);
                }
                else if (action == "error") { Assert.Same(failure, Assert.Throws<IOException>(finalize)); }
                else { finalize(); }

                stream.OnPosition = null;
                Assert.Equal(1, callbacks);
                Assert.Equal(1UL, writer.SampleCount);
                Assert.Equal(owned, stream.Disposed);
                Assert.Equal(owned ? 1 : 0, stream.Disposals);
                bool successful = action == "reentry";
                Assert.Equal(successful ? 2 : flush - 1, stream.Flushes);
                Assert.Equal(successful ? MdfWriterState.Finalized : dispose ? MdfWriterState.Disposed : MdfWriterState.Faulted, writer.State);
                Assert.Equal(successful || flush == 2 ? "MDF     " : "UnFinMF ", Encoding.ASCII.GetString(stream.Snapshot(), 0, 8));
                if (!successful) { Assert.Equal(target, positions); }

                int writes = stream.Writes, seeks = stream.Seeks, flushes = stream.Flushes;
                if (successful) { writer.FinalizeMeasurement(); } // Idempotence still performs no output.
                else if (dispose) { Assert.Throws<ObjectDisposedException>(() => writer.FinalizeMeasurement()); }
                else { Assert.Throws<InvalidOperationException>(() => writer.FinalizeMeasurement()); }
                writer.Dispose(); writer.Dispose(); // No implicit flush/repair or second owned-stream close.
                Assert.Equal(writes, stream.Writes);
                Assert.Equal(seeks, stream.Seeks);
                Assert.Equal(flushes, stream.Flushes);
                Assert.Equal(owned ? 1 : 0, stream.Disposals);
            }
        }
    }
}
