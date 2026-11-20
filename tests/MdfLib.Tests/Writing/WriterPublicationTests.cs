// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Writing
{
    public sealed class WriterPublicationTests
    {
        public static IEnumerable<object[]> FinalizedShapes()
        {
            foreach (string version in new[] { "3.30", "4.10", "4.20" })
            foreach (int samples in new[] { 0, 1, 4 }) { yield return new object[] { version, samples }; }
        }

        [Theory]
        [MemberData(nameof(FinalizedShapes))]
        public void FirstFlushSeesPatchedCountsButOnlySecondFlushSeesPublishedIdentification(string version, int samples)
        {
            TestWriteStream stream = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(stream, true, new MdfWriterOptions { Version = version }))
            {
                writer.AddChannel(WriterConfigurationTests.Channel()); writer.StartMeasurement();
                bool mdf3 = version == "3.30";
                byte[] prefix = stream.Snapshot();
                int pointerBytes = mdf3 ? 4 : 8;
                int dg = checked((int)UInt(prefix, mdf3 ? 68 : 88, pointerBytes));
                int cg = checked((int)UInt(prefix, dg + (mdf3 ? 8 : 32), pointerBytes));
                int data = checked((int)UInt(prefix, dg + (mdf3 ? 16 : 40), pointerBytes));
                int countOffset = cg + (mdf3 ? 22 : 80);
                int dataEnd = checked(prefix.Length + samples * writer.RecordByteCount);
                int fileEnd = mdf3 ? dataEnd : (dataEnd + 7) & ~7;
                for (int i = 0; i < samples; i++) { writer.SaveSample(new[] { MdfSampleValue.FromUnsigned(7) }); }
                writer.StopMeasurement();
                Assert.Equal(0, stream.Flushes);
                Assert.Equal("UnFinMF ", Encoding.ASCII.GetString(stream.Snapshot(), 0, 8));
                stream.BeforeFlush = destination =>
                {
                    byte[] bytes = destination.Snapshot();
                    Assert.Equal(MdfWriterState.Stopped, writer.State);
                    Assert.Equal((ulong)samples, writer.SampleCount);
                    Assert.Equal(fileEnd, bytes.Length);
                    Assert.Equal((ulong)samples, UInt(bytes, countOffset, mdf3 ? 4 : 8));
                    Assert.Equal(destination.Flushes == 1 ? "UnFinMF " : "MDF     ", Encoding.ASCII.GetString(bytes, 0, 8));
                    if (!mdf3)
                    {
                        Assert.Equal(24UL + (ulong)(samples * writer.RecordByteCount), UInt(bytes, data + 8, 8));
                        for (int i = dataEnd; i < bytes.Length; i++) { Assert.Equal(0, bytes[i]); }
                    }
                };
                writer.FinalizeMeasurement();
                Assert.Equal(MdfWriterState.Finalized, writer.State);
                Assert.Equal(2, stream.Flushes);
                Assert.Equal(fileEnd, stream.Position);
                Assert.Equal(0, stream.Reads); Assert.Equal(0, stream.SetLengths);
            }
        }

        [Theory]
        [InlineData("flush1", false)]
        [InlineData("flush2", true)]
        [InlineData("publish-full-write-then-throw", true)]
        public void AFinalLookingMarkerOnFailedOutputIsNotASuccessOrAnAtomicityGuarantee(string failure, bool finalMarker)
        {
            TestWriteStream stream = new TestWriteStream();
            MdfWriter writer = WriterConfigurationTests.Started(stream);
            try
            {
                writer.SaveSample(new[] { MdfSampleValue.FromUnsigned(7) }); writer.StopMeasurement();
                if (failure == "publish-full-write-then-throw")
                {
                    stream.FailWriteAt = 6; // Prefix, row, padding, count, length, then final 64-byte ID.
                    stream.PartialBytes = 64; // ALL marker bytes accepted, but destination still reports failure.
                }
                else { stream.FailFlushAt = failure == "flush1" ? 1 : 2; }
                Assert.Same(stream.Failure, Assert.Throws<IOException>(() => writer.FinalizeMeasurement()));
                Assert.Equal(MdfWriterState.Faulted, writer.State);
                Assert.Equal(finalMarker ? "MDF     " : "UnFinMF ", Encoding.ASCII.GetString(stream.Snapshot(), 0, 8));
                int before = stream.Writes;
                Assert.Throws<InvalidOperationException>(() => writer.FinalizeMeasurement());
                Assert.Equal(before, stream.Writes);
                writer.Dispose(); writer.Dispose();
                Assert.Equal(before, stream.Writes);
                Assert.False(stream.Disposed);
            }
            finally { writer.Dispose(); }
        }

        // Literal wire reads, not writer planner/codec or production reader.
        private static ulong UInt(byte[] bytes, int offset, int width)
        {
            ulong result = 0;
            for (int i = 0; i < width; i++) { result |= (ulong)bytes[offset + i] << (8 * i); }
            return result;
        }
    }
}
