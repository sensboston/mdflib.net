// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Writing
{
    public sealed class WriterCallbackTests
    {
        private static readonly MdfSampleValue[] Row = { MdfSampleValue.FromUnsigned(7) };

        public static IEnumerable<object[]> CallerCallbacks()
        {
            foreach (string stage in new[] { "count1", "value", "count2" })
            foreach (string action in new[] { "dispose", "cancel", "error", "reentry" })
            { yield return new object[] { stage, action }; }
        }

        [Theory]
        [MemberData(nameof(CallerCallbacks))]
        public void EveryCallerAccessorObservesTerminalActionsBeforeBufferUseOrOutput(string stage, string action)
        {
            TestWriteStream stream = new TestWriteStream();
            IOException failure = new IOException("caller row accessor failed");
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            using (MdfWriter writer = WriterConfigurationTests.Started(stream))
            {
                int callbacks = 0;
                CallbackRow row = new CallbackRow(stage, () =>
                {
                    callbacks++;
                    if (action == "dispose") { writer.Dispose(); }
                    else if (action == "cancel") { cancellation.Cancel(); }
                    else if (action == "error") { throw failure; }
                    else { Assert.Throws<InvalidOperationException>(() => writer.SaveSample(Row)); }
                });
                int before = stream.Writes;
                Action save = () => writer.SaveSample(row, cancellation.Token);
                if (action == "dispose")
                {
                    // Especially count1: disposal clears the private record, which must not be touched next.
                    Assert.Throws<ObjectDisposedException>(save);
                    Assert.Equal(MdfWriterState.Disposed, writer.State);
                    Assert.Equal(before, stream.Writes);
                    Assert.Equal(0UL, writer.SampleCount);
                }
                else
                {
                    if (action == "cancel") { Assert.Throws<OperationCanceledException>(save); }
                    else if (action == "error") { Assert.Same(failure, Assert.Throws<IOException>(save)); }
                    else { save(); }
                    Assert.Equal(MdfWriterState.Recording, writer.State);
                    Assert.Equal(before + (action == "reentry" ? 1 : 0), stream.Writes);
                    Assert.Equal(action == "reentry" ? 1UL : 0UL, writer.SampleCount);
                    // Caller validation/cancellation before output cannot poison the next row.
                    if (action != "reentry") { writer.SaveSample(Row); }
                    writer.StopMeasurement(); writer.FinalizeMeasurement();
                    Assert.Equal(MdfWriterState.Finalized, writer.State);
                }
                Assert.Equal(1, callbacks);
                Assert.False(stream.Disposed);
            }
            if (action != "dispose")
            {
                using (MdfReader reader = MdfReader.Open(new MemoryStream(stream.Snapshot())))
                {
                    MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                    Assert.Equal(1UL, group.SampleCount);
                    Assert.Equal(7UL, reader.ReadSamples(group, new[] { 0 }).Single().GetValue(0, 0).UnsignedValue);
                }
            }
        }

        [Theory]
        [InlineData("start", 1)]
        [InlineData("row", -1)]
        [InlineData("row", 1)]
        [InlineData("finalize", -1)]
        [InlineData("finalize", 1)]
        public void LengthMutationDuringAnOtherwiseExactSeekFailsBeforeTheNextWrite(string stage, int delta)
        {
            TestWriteStream stream = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(stream, true))
            {
                Prepare(writer, stage);
                int before = stream.Writes;
                ulong count = writer.SampleCount;
                long length = stream.Length;
                stream.BeforeSeek = destination => destination.ChangeLength(length + delta);
                IOException failure = Assert.Throws<IOException>(() => Operation(writer, stage, CancellationToken.None));
                Assert.Contains("length changed during seek", failure.Message);
                Assert.Equal(before, stream.Writes);
                Assert.Equal(count, writer.SampleCount);
                Assert.Equal(MdfWriterState.Faulted, writer.State);
                Assert.Equal(length + delta, stream.Length);
            }
        }

        public static IEnumerable<object[]> PostSeekCallbacks()
        {
            foreach (string stage in new[] { "start", "row", "finalize" })
            foreach (string action in new[] { "dispose", "cancel", "error" })
            { yield return new object[] { stage, action }; }
        }

        [Theory]
        [MemberData(nameof(PostSeekCallbacks))]
        public void PostSeekLengthGetterCannotDisposeCancelOrThrowAndStillReachWrite(string stage, string action)
        {
            TestWriteStream stream = new TestWriteStream();
            IOException failure = new IOException("post-seek length getter failed");
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            using (MdfWriter writer = MdfWriter.Create(stream, true))
            {
                Prepare(writer, stage);
                int before = stream.Writes;
                ulong count = writer.SampleCount;
                int checks = 0;
                stream.OnLength = destination =>
                {
                    if (++checks != 2) { return; }
                    if (action == "dispose") { writer.Dispose(); }
                    else if (action == "cancel") { cancellation.Cancel(); }
                    else { throw failure; }
                };
                Action operation = () => Operation(writer, stage, cancellation.Token);
                if (action == "dispose") { Assert.Throws<ObjectDisposedException>(operation); }
                else if (action == "cancel") { Assert.Throws<OperationCanceledException>(operation); }
                else { Assert.Same(failure, Assert.Throws<IOException>(operation)); }
                Assert.Equal(2, checks);
                stream.OnLength = null;
                Assert.Equal(before, stream.Writes);
                Assert.Equal(count, writer.SampleCount);
                Assert.Equal(action == "dispose" ? MdfWriterState.Disposed : MdfWriterState.Faulted, writer.State);
                Assert.False(stream.Disposed);
            }
        }

        private static void Prepare(MdfWriter writer, string stage)
        {
            writer.AddChannel(WriterConfigurationTests.Channel());
            if (stage != "start") { writer.StartMeasurement(); }
            if (stage == "finalize") { writer.SaveSample(Row); writer.StopMeasurement(); }
        }

        private static void Operation(MdfWriter writer, string stage, CancellationToken token)
        {
            if (stage == "start") { writer.StartMeasurement(token); }
            else if (stage == "row") { writer.SaveSample(Row, token); }
            else { writer.FinalizeMeasurement(token); }
        }

        private sealed class CallbackRow : IReadOnlyList<MdfSampleValue>
        {
            private readonly string stage;
            private readonly Action callback;
            private int counts;
            internal CallbackRow(string stage, Action callback) { this.stage = stage; this.callback = callback; }
            public int Count
            {
                get
                {
                    counts++;
                    if ((stage == "count1" && counts == 1) || (stage == "count2" && counts == 2)) { callback(); }
                    return 1;
                }
            }
            public MdfSampleValue this[int index] { get { if (stage == "value") { callback(); } return Row[0]; } }
            public IEnumerator<MdfSampleValue> GetEnumerator() { throw new NotSupportedException(); }
            IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
        }
    }
}
