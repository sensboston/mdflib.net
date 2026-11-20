// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Writing
{
    public sealed class WriterFaultTests
    {
        private static readonly MdfSampleValue[] Row = { MdfSampleValue.FromUnsigned(7) };
        public static IEnumerable<object[]> OutputFailures()
        {
            foreach (string stage in new[] { "prefix", "row", "padding", "count", "length", "publish", "flush1", "flush2" })
            foreach (int partial in new[] { -1, 0, 1 }) { yield return new object[] { stage, partial }; }
        }

        [Theory]
        [MemberData(nameof(OutputFailures))]
        public void EveryOutputPhasePreservesCauseFaultsAndCannotBeRetried(string stage, int partial)
        {
            TestWriteStream stream = new TestWriteStream { PartialBytes = partial };
            string[] writes = { "prefix", "row", "padding", "count", "length", "publish" };
            stream.FailWriteAt = Array.IndexOf(writes, stage) + 1;
            if (stage.StartsWith("flush", StringComparison.Ordinal)) { stream.FailWriteAt = -1; stream.FailFlushAt = stage == "flush1" ? 1 : 2; }
            MdfWriter writer = MdfWriter.Create(stream);
            try
            {
                writer.AddChannel(WriterConfigurationTests.Channel());
                Action operation;
                if (stage == "prefix") { operation = () => writer.StartMeasurement(); }
                else
                {
                    writer.StartMeasurement();
                    if (stage == "row") { operation = () => writer.SaveSample(Row); }
                    else { writer.SaveSample(Row); writer.StopMeasurement(); operation = () => writer.FinalizeMeasurement(); }
                }
                IOException error = Assert.Throws<IOException>(operation);
                Assert.Same(stream.Failure, error);
                Assert.Equal(MdfWriterState.Faulted, writer.State);
                Assert.Equal(stage == "prefix" || stage == "row" ? 0UL : 1UL, writer.SampleCount);
                int calls = stream.Writes;
                Assert.Throws<InvalidOperationException>(() => writer.StartMeasurement());
                Assert.Throws<InvalidOperationException>(() => writer.SaveSample(Row));
                Assert.Throws<InvalidOperationException>(() => writer.StopMeasurement());
                Assert.Throws<InvalidOperationException>(() => writer.FinalizeMeasurement());
                Assert.Equal(calls, stream.Writes); Assert.False(stream.Disposed);
            }
            finally { writer.Dispose(); writer.Dispose(); }
            Assert.Equal(1, stream.Disposals);
        }

        [Theory]
        [InlineData("return")]
        [InlineData("position")]
        [InlineData("seek-throw")]
        [InlineData("short-write")]
        [InlineData("length")]
        public void IncorrectSeekShortWriteAndExternalLengthChangesCannotLookSuccessful(string fault)
        {
            TestWriteStream stream = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(stream, true))
            {
                writer.AddChannel(WriterConfigurationTests.Channel());
                if (fault == "return") { stream.WrongSeekReturn = true; }
                if (fault == "position") { stream.WrongSeekPosition = true; }
                if (fault == "seek-throw") { stream.FailSeekAt = 1; }
                if (fault == "short-write") { stream.SilentShortWrite = true; }
                if (fault == "length") { stream.ChangeLength(1); }
                Assert.Throws<IOException>(() => writer.StartMeasurement());
                Assert.Equal(MdfWriterState.Faulted, writer.State);
                if (fault != "short-write") { Assert.Equal(0, stream.Writes); }
            }
        }

        [Theory]
        [InlineData("write-cancel")]
        [InlineData("write-dispose")]
        [InlineData("seek-cancel")]
        [InlineData("flush-cancel")]
        [InlineData("flush-dispose")]
        [InlineData("length-dispose")]
        public void CancellationOrDisposalInsideOutputCallbacksNeverPublishesSuccess(string stage)
        {
            TestWriteStream stream = new TestWriteStream();
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            using (MdfWriter writer = MdfWriter.Create(stream, true))
            {
                writer.AddChannel(WriterConfigurationTests.Channel());
                if (stage == "length-dispose") { stream.OnLength = s => writer.Dispose(); }
                if (stage == "seek-cancel") { stream.BeforeSeek = s => cancellation.Cancel(); }
                if (stage.StartsWith("write", StringComparison.Ordinal))
                { stream.AfterWrite = s => { if (stage == "write-cancel") { cancellation.Cancel(); } else { writer.Dispose(); } }; }
                if (stage.StartsWith("flush", StringComparison.Ordinal))
                {
                    writer.StartMeasurement(); writer.SaveSample(Row); writer.StopMeasurement();
                    stream.BeforeFlush = s => { if (stage == "flush-cancel") { cancellation.Cancel(); } else { writer.Dispose(); } };
                }
                Action operation = stage.StartsWith("flush", StringComparison.Ordinal)
                    ? (Action)(() => writer.FinalizeMeasurement(cancellation.Token)) : () => writer.StartMeasurement(cancellation.Token);
                if (stage.EndsWith("dispose", StringComparison.Ordinal))
                { Assert.Throws<ObjectDisposedException>(operation); Assert.Equal(MdfWriterState.Disposed, writer.State); }
                else { Assert.Throws<OperationCanceledException>(operation); Assert.Equal(MdfWriterState.Faulted, writer.State); }
                if (stage == "seek-cancel" || stage == "length-dispose") { Assert.Equal(0, stream.Writes); }
                Assert.False(stream.Disposed); // leaveOpen holds even for terminal callback disposal
            }
        }

        [Fact]
        public void CancellationAfterARecordWriteDoesNotIncrementItsSuccessfulCounter()
        {
            TestWriteStream stream = new TestWriteStream();
            using (MdfWriter writer = WriterConfigurationTests.Started(stream))
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                stream.AfterWrite = s => cancellation.Cancel();
                Assert.Throws<OperationCanceledException>(() => writer.SaveSample(Row, cancellation.Token));
                Assert.Equal(0UL, writer.SampleCount); Assert.Equal(MdfWriterState.Faulted, writer.State);
                Assert.Equal(2, stream.Writes); Assert.Throws<InvalidOperationException>(() => writer.FinalizeMeasurement());
            }
        }

        [Fact]
        public void ReentryIsRefusedDuringWriteAndFlushWithoutChangingOuterState()
        {
            TestWriteStream stream = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(stream, true))
            {
                writer.AddChannel(WriterConfigurationTests.Channel());
                int attempts = 0;
                stream.BeforeWrite = s => { attempts++; Assert.Throws<InvalidOperationException>(() => writer.StartMeasurement()); };
                stream.BeforeFlush = s => { attempts++; Assert.Throws<InvalidOperationException>(() => writer.FinalizeMeasurement()); };
                writer.StartMeasurement(); writer.SaveSample(Row); writer.StopMeasurement(); writer.FinalizeMeasurement();
                Assert.Equal(MdfWriterState.Finalized, writer.State); Assert.True(attempts >= 6);
            }
        }

        [Fact]
        public void ArbitraryOutputExceptionsAreNotReplacedWithFormatErrorsOrCleanupSuccess()
        {
            TestWriteStream stream = new TestWriteStream { FailWriteAt = 1, Failure = new InvalidOperationException("destination callback failed") };
            using (MdfWriter writer = MdfWriter.Create(stream, true))
            {
                writer.AddChannel(WriterConfigurationTests.Channel());
                Assert.Same(stream.Failure, Assert.Throws<InvalidOperationException>(() => writer.StartMeasurement()));
                Assert.Equal(MdfWriterState.Faulted, writer.State);
            }
        }
    }
}
