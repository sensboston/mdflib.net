// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// for the bus log through the facade: StartMeasurement(t) after InitMeasurement with a t other than the start time the
// header holds patches the header of the bus log in place, as it does for an ordinary MDF 4 writer. The bus log has no
// ordinary writer behind it, so the patch goes to the engine that owns the file.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MdfLib;
using Xunit;

namespace MdfLib.Tests
{
    public sealed class MdfLibraryBusLogStartTimeTests : IDisposable
    {
        private const ulong HeaderStart = 1000000000UL;
        private const ulong MeasurementStart = 5000000000UL;
        private readonly string directory_;

        public MdfLibraryBusLogStartTimeTests()
        {
            directory_ = Path.Combine(Path.GetTempPath(), "mdflib-facade-buslog-start-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory_);
        }

        public void Dispose()
        {
            try { Directory.Delete(directory_, true); }
            catch (IOException) { }
        }

        [Fact]
        public void StartTimeSetAfterInitMeasurementIsWrittenInTheBusLogHeader()
        {
            string file = Path.Combine(directory_, "bus-start.mf4");
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.MdfBusLogger);
            Assert.True(writer.Init(file));
            writer.BusType = MdfLibrary.MdfBusType.CAN;
            writer.Header.StartTime = HeaderStart;
            Assert.True(writer.CreateBusLogConfiguration());
            MdfLibrary.MdfChannelGroup frames = writer.Header.LastDataGroup.GetChannelGroup("CAN_DataFrame");
            Assert.NotNull(frames);
            Assert.True(writer.InitMeasurement());

            // The header is on disk now; the start time given here differs from the one it holds.
            writer.StartMeasurement(MeasurementStart);
            Assert.Equal(MeasurementStart, writer.StartTime);
            Assert.Equal(MeasurementStart, writer.Header.StartTime);

            MdfLibrary.CanMessage message = new MdfLibrary.CanMessage();
            for (uint index = 0U; index < 3U; index++)
            {
                message.Reset();
                message.MessageId = 0x200U + index;
                message.BusChannel = 1;
                message.DataBytes = new byte[] { 0x10, (byte)index, 0x30 };
                writer.SaveCanMessage(frames, MeasurementStart + (ulong)index * 1000000000UL, message);
            }
            writer.StopMeasurement(MeasurementStart + 3000000000UL);
            Assert.True(writer.FinalizeMeasurement());
            writer.Dispose();

            using (MdfReader reader = MdfReader.Open(File.OpenRead(file)))
            {
                Assert.Equal(MeasurementStart, reader.Header.RawStartTimeNanoseconds);
                List<MdfBusMessage> messages = MdfBusReader.ReadMessages(reader).ToList();
                Assert.Equal(3, messages.Count);
                // The frame times are relative to the start time the header now holds.
                Assert.Equal(0.0, messages[0].Timestamp, 6);
                Assert.Equal(1.0, messages[1].Timestamp, 6);
                Assert.Equal(2.0, messages[2].Timestamp, 6);
                Assert.Equal(0x202u, messages[2].Id);
            }
        }
    }
}
