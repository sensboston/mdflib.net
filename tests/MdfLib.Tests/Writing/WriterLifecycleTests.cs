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
    public sealed class WriterLifecycleTests
    {
        private static readonly MdfSampleValue[] Row = { MdfSampleValue.FromUnsigned(7) };

        [Theory]
        [InlineData("3.30", false)]
        [InlineData("4.10", false)]
        [InlineData("4.20", false)]
        [InlineData("3.30", true)]
        [InlineData("4.10", true)]
        [InlineData("4.20", true)]
        public void DisposeNeverFinalizesAndOwnershipIsExplicit(string version, bool leaveOpen)
        {
            TestWriteStream stream = new TestWriteStream();
            MdfWriter writer = MdfWriter.Create(stream, leaveOpen, new MdfWriterOptions { Version = version });
            stream.ChangePosition(77); // Empty destinations are always written from logical zero.
            Assert.Equal(MdfWriterState.Configuring, writer.State);
            Assert.Throws<InvalidOperationException>(() => writer.StartMeasurement());
            Assert.Equal(0, stream.Writes);
            writer.AddChannel(WriterConfigurationTests.Channel());
            writer.StartMeasurement(); writer.SaveSample(Row);
            int writes = stream.Writes;
            writer.Dispose(); writer.Dispose();
            Assert.Equal(writes, stream.Writes); Assert.Equal(0, stream.Flushes); Assert.Equal(0, stream.SetLengths); Assert.Equal(0, stream.Reads);
            Assert.Equal(leaveOpen ? 0 : 1, stream.Disposals);
            Assert.Equal(MdfWriterState.Disposed, writer.State); Assert.Equal(1UL, writer.SampleCount); Assert.Equal(1, writer.ChannelCount);
            Assert.Throws<ObjectDisposedException>(() => writer.FinalizeMeasurement());
            using (MemoryStream incomplete = new MemoryStream(stream.Snapshot()))
            { Assert.Throws<NotSupportedException>(() => MdfReader.Open(incomplete)); }
        }

        [Theory]
        [InlineData("3.30")]
        [InlineData("4.10")]
        [InlineData("4.20")]
        public void ExplicitTransitionsAndFinalizationAreExactAndIdempotent(string version)
        {
            TestWriteStream stream = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(stream, true, new MdfWriterOptions { Version = version }))
            {
                Assert.Throws<InvalidOperationException>(() => writer.StopMeasurement());
                Assert.Throws<InvalidOperationException>(() => writer.FinalizeMeasurement());
                Assert.Throws<InvalidOperationException>(() => writer.SaveSample(Row));
                writer.AddChannel(WriterConfigurationTests.Channel()); writer.StartMeasurement();
                Assert.Equal(MdfWriterState.Recording, writer.State);
                Assert.Throws<InvalidOperationException>(() => writer.AddChannel(WriterConfigurationTests.Channel()));
                Assert.Throws<InvalidOperationException>(() => writer.StartMeasurement());
                Assert.Throws<InvalidOperationException>(() => writer.FinalizeMeasurement());
                writer.SaveSample(Row); writer.StopMeasurement();
                Assert.Equal(MdfWriterState.Stopped, writer.State);
                Assert.Throws<InvalidOperationException>(() => writer.SaveSample(Row));
                Assert.Throws<InvalidOperationException>(() => writer.StopMeasurement());
                Assert.Equal(0, stream.Flushes);
                writer.FinalizeMeasurement();
                Assert.Equal(MdfWriterState.Finalized, writer.State); Assert.Equal(2, stream.Flushes);
                byte[] before = stream.Snapshot(); int writes = stream.Writes;
                writer.FinalizeMeasurement();
                Assert.Equal(before, stream.Snapshot()); Assert.Equal(writes, stream.Writes); Assert.Equal(2, stream.Flushes);
                Assert.Throws<InvalidOperationException>(() => writer.SaveSample(Row));
                Assert.Throws<InvalidOperationException>(() => writer.StartMeasurement());
                Assert.Throws<OperationCanceledException>(() => writer.FinalizeMeasurement(new CancellationToken(true)));
                Assert.Equal(MdfWriterState.Finalized, writer.State);
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(stream.Snapshot())))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(1UL, group.SampleCount);
                Assert.Equal(7UL, reader.ReadSamples(group, new[] { 0 }).Single().GetValue(0, 0).UnsignedValue);
            }
        }

        [Theory]
        [InlineData("start")]
        [InlineData("save")]
        [InlineData("stop")]
        [InlineData("finalize")]
        public void EntryCancellationDoesNotFaultOrWriteAndCanBeRetried(string stage)
        {
            TestWriteStream stream = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(stream, true))
            {
                writer.AddChannel(WriterConfigurationTests.Channel());
                if (stage != "start") { writer.StartMeasurement(); }
                if (stage == "finalize") { writer.StopMeasurement(); }
                int writes = stream.Writes; MdfWriterState before = writer.State;
                CancellationToken token = new CancellationToken(true);
                Assert.Throws<OperationCanceledException>(() =>
                {
                    if (stage == "start") { writer.StartMeasurement(token); }
                    if (stage == "save") { writer.SaveSample(Row, token); }
                    if (stage == "stop") { writer.StopMeasurement(token); }
                    if (stage == "finalize") { writer.FinalizeMeasurement(token); }
                });
                Assert.Equal(before, writer.State); Assert.Equal(writes, stream.Writes);
                if (stage == "start") { writer.StartMeasurement(); }
                if (stage != "finalize") { writer.SaveSample(Row); writer.StopMeasurement(); }
                writer.FinalizeMeasurement(); Assert.Equal(MdfWriterState.Finalized, writer.State);
            }
        }

        [Theory]
        [InlineData("null")]
        [InlineData("short")]
        [InlineData("long")]
        [InlineData("kind")]
        [InlineData("unsigned")]
        [InlineData("signed")]
        [InlineData("float")]
        [InlineData("invalid3")]
        public void BadWholeRowsNeverWritePartialRecordsAndRecordingCanContinue(string reason)
        {
            TestWriteStream stream = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(stream, true, new MdfWriterOptions { Version = reason == "invalid3" ? "3.30" : "4.10" }))
            {
                writer.AddChannel(WriterConfigurationTests.Channel());
                MdfValueKind kind = reason == "signed" ? MdfValueKind.SignedInteger : reason == "float" ? MdfValueKind.FloatingPoint : MdfValueKind.UnsignedInteger;
                writer.AddChannel(WriterConfigurationTests.Channel("second", reason == "float" ? 32 : 8, kind));
                writer.StartMeasurement();
                MdfSampleValue good = kind == MdfValueKind.SignedInteger ? MdfSampleValue.FromSigned(1) : kind == MdfValueKind.FloatingPoint ? MdfSampleValue.FromFloatingPoint(1) : MdfSampleValue.FromUnsigned(1);
                MdfSampleValue bad = reason == "kind" ? MdfSampleValue.FromSigned(1) : reason == "unsigned" ? MdfSampleValue.FromUnsigned(256)
                    : reason == "signed" ? MdfSampleValue.FromSigned(128) : reason == "float" ? MdfSampleValue.FromFloatingPoint(double.MaxValue)
                    : MdfSampleValue.FromUnsigned(1, reason != "invalid3");
                MdfSampleValue[] values = reason == "null" ? null : reason == "short" ? new[] { bad }
                    : reason == "long" ? new[] { Row[0], good, good } : new[] { Row[0], bad };
                int writes = stream.Writes; long length = stream.Length;
                Exception error = Record.Exception(() => writer.SaveSample(values));
                Assert.NotNull(error); Assert.True(error is ArgumentException || error is NotSupportedException, error.ToString());
                Assert.Equal(writes, stream.Writes); Assert.Equal(length, stream.Length); Assert.Equal(0UL, writer.SampleCount);
                Assert.Equal(MdfWriterState.Recording, writer.State);
                writer.SaveSample(new[] { Row[0], good }); writer.StopMeasurement(); writer.FinalizeMeasurement();
                Assert.Equal(1UL, writer.SampleCount);
            }
        }

        [Theory]
        [InlineData("3.30")]
        [InlineData("4.20")]
        public void LongNamesArePreservedWithoutChangingTheReaderModel(string version)
        {
            TestWriteStream stream = new TestWriteStream();
            string name = new string('n', 100) + (version == "4.20" ? " 温度" : " café");
            using (MdfWriter writer = MdfWriter.Create(stream, true, new MdfWriterOptions { Version = version, StartTimeNanoseconds = ulong.MaxValue }))
            {
                writer.AddChannel(WriterConfigurationTests.Channel(name)); writer.StartMeasurement(); writer.SaveSample(Row); writer.StopMeasurement(); writer.FinalizeMeasurement();
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(stream.Snapshot())))
            { Assert.Equal(name, reader.ReadMetadata()[0].ChannelGroups[0].Channels[0].Name); Assert.Equal(ulong.MaxValue, reader.Header.RawStartTimeNanoseconds); }
        }

        [Fact]
        public void CallerListCallbacksCannotReenterOrChangeTheCopiedRowSize()
        {
            TestWriteStream stream = new TestWriteStream();
            using (MdfWriter writer = WriterConfigurationTests.Started(stream))
            {
                CallbackRow row = new CallbackRow();
                row.Callback = () => Assert.Throws<InvalidOperationException>(() => writer.SaveSample(Row));
                writer.SaveSample(row);
                Assert.Equal(1UL, writer.SampleCount);
                int writes = stream.Writes;
                row.Callback = () => row.Size = 2;
                Assert.Throws<ArgumentException>(() => writer.SaveSample(row));
                Assert.Equal(writes, stream.Writes); Assert.Equal(1UL, writer.SampleCount); Assert.Equal(MdfWriterState.Recording, writer.State);
                row.Size = 1; row.Callback = writer.Dispose;
                Assert.Throws<ObjectDisposedException>(() => writer.SaveSample(row));
                Assert.Equal(MdfWriterState.Disposed, writer.State); Assert.Equal(writes, stream.Writes);
            }
        }

        private sealed class CallbackRow : IReadOnlyList<MdfSampleValue>
        {
            internal int Size = 1;
            internal Action Callback;
            public int Count { get { return Size; } }
            public MdfSampleValue this[int index] { get { Callback?.Invoke(); return Row[0]; } }
            public IEnumerator<MdfSampleValue> GetEnumerator() { throw new NotSupportedException(); }
            IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
        }
    }
}
