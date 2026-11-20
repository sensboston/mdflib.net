// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// the facade's pre-trigger window (PreTrigTime, as the upstream sample queue keeps it) and the HD fields the
// file keeps: the start angle and distance, and the common properties of the HDcomment. The files are read back
// with our own reader, as the other facade writer tests do.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace MdfLib.Tests
{
    public sealed class MdfLibraryPreTriggerHeaderTests : IDisposable
    {
        private const ulong Start = 10000000000UL; // 10 s after the epoch, so the samples before it stay positive
        private readonly string directory_;

        public MdfLibraryPreTriggerHeaderTests()
        {
            directory_ = Path.Combine(Path.GetTempPath(), "mdflib-facade-pretrigger-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory_);
        }

        public void Dispose()
        {
            try { Directory.Delete(directory_, true); }
            catch (IOException) { }
        }

        private static MdfLibrary.MdfChannelGroup CreateTimedGroup(MdfLibrary.MdfWriter writer, out MdfLibrary.MdfChannel value)
        {
            MdfLibrary.MdfChannelGroup group = writer.CreateDataGroup().ChannelGroups[0];
            group.Name = "Trigger";
            MdfLibrary.MdfChannel master = group.CreateChannel();
            master.Name = "Time";
            master.Unit = "s";
            master.Type = MdfLibrary.ChannelType.Master;
            master.Sync = MdfLibrary.ChannelSyncType.Time;
            master.DataType = MdfLibrary.ChannelDataType.FloatLe;
            master.BitCount = 64;
            value = group.CreateChannel();
            value.Name = "Value";
            value.DataType = MdfLibrary.ChannelDataType.FloatLe;
            value.BitCount = 64;
            return group;
        }

        private static MdfSampleValue Value(MdfReader reader, MdfChannelGroup group, int channel, int sample)
        {
            foreach (MdfSampleBatch batch in reader.ReadSamples(group, new int[] { channel }, (ulong)sample, 1UL))
            {
                return batch.GetValue(0, 0);
            }
            throw new InvalidOperationException("The reader returned no batch for channel " + channel + ".");
        }

        // Samples saved before StartMeasurement are kept for the 2.5 s window, with the one sample before the window
        // (-3.0 s); the start is at 0 s; the stop at 2.75 s drops the 3.0 s sample and the 5.0 s sample.
        [Fact]
        public void PreTriggerKeepsTheWindowBeforeTheStartAndStopsAtTheStopTime()
        {
            string file = Path.Combine(directory_, "pretrigger.mf4");
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            Assert.True(writer.Init(file));
            writer.Header.StartTime = Start;
            writer.PreTrigTime = 2.5;
            MdfLibrary.MdfChannel value;
            MdfLibrary.MdfChannelGroup group = CreateTimedGroup(writer, out value);
            Assert.True(writer.InitMeasurement());

            for (int index = 0; index <= 6; index++)
            {
                value.SetChannelValue((double)index);
                writer.SaveSample(group, Start - 3000000000UL + (ulong)index * 500000000UL);
            }
            writer.StartMeasurement(Start);
            for (int index = 1; index <= 6; index++)
            {
                value.SetChannelValue(10.0 + index);
                writer.SaveSample(group, Start + (ulong)index * 500000000UL);
            }
            value.SetChannelValue(99.0);
            writer.SaveSample(group, Start + 5000000000UL);
            writer.StopMeasurement(Start + 2750000000UL);
            Assert.True(writer.FinalizeMeasurement());
            writer.Dispose();

            double[] expectedTimes = { -3.0, -2.5, -2.0, -1.5, -1.0, -0.5, 0.0, 0.5, 1.0, 1.5, 2.0, 2.5 };
            double[] expectedValues = { 0.0, 1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 11.0, 12.0, 13.0, 14.0, 15.0 };
            using (MdfReader reader = MdfReader.Open(File.OpenRead(file)))
            {
                IReadOnlyList<MdfDataGroup> groups = reader.ReadMetadata();
                MdfChannelGroup read = groups[0].ChannelGroups[0];
                Assert.Equal((ulong)expectedTimes.Length, read.SampleCount);
                for (int i = 0; i < expectedTimes.Length; i++)
                {
                    Assert.Equal(expectedTimes[i], Value(reader, read, 0, i).FloatingPointValue, 9);
                    Assert.Equal(expectedValues[i], Value(reader, read, 1, i).FloatingPointValue, 9);
                }
            }
        }

        [Fact]
        public void StartAngleDistanceAndCommonPropertiesAreWrittenToTheHeader()
        {
            string file = Path.Combine(directory_, "header.mf4");
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            Assert.True(writer.Init(file));
            writer.Header.StartTime = Start;
            writer.Header.StartAngle = 0.5;
            writer.Header.StartDistance = 120.25;
            writer.Header.MeasurementId = "3c8f1e0a-2b7d-4f6e-9a51-7d2c4b8e6f10";
            writer.Header.RecorderId = "recorder-07";
            writer.Header.RecorderIndex = 7;
            Assert.True(writer.Header.IsStartAngleUsed);
            Assert.True(writer.Header.IsStartDistanceUsed);
            MdfLibrary.MdfChannel value;
            MdfLibrary.MdfChannelGroup group = CreateTimedGroup(writer, out value);
            Assert.True(writer.InitMeasurement());
            writer.StartMeasurement(Start);
            value.SetChannelValue(1.0);
            writer.SaveSample(group, Start);
            writer.StopMeasurement(Start + 1000000000UL);
            Assert.True(writer.FinalizeMeasurement());
            writer.Dispose();

            using (MdfReader reader = MdfReader.Open(File.OpenRead(file)))
            {
                Assert.Equal<double?>(0.5, reader.Header.StartAngle);
                Assert.Equal<double?>(120.25, reader.Header.StartDistance);
            }
            string text = Encoding.UTF8.GetString(File.ReadAllBytes(file));
            Assert.Contains("Measurement.UUID", text);
            Assert.Contains("3c8f1e0a-2b7d-4f6e-9a51-7d2c4b8e6f10", text);
            Assert.Contains("Recorder.UUID", text);
            Assert.Contains("Recorder.FileIndex", text);
        }

        // MDF 3.30 has none of these fields: the wrapper ignores the values, and so does the facade.
        [Fact]
        public void Mdf3WriterIgnoresTheHeaderFieldsTheRevisionDoesNotHave()
        {
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf3Basic);
            writer.Header.StartAngle = 1.0;
            writer.Header.StartDistance = 2.0;
            writer.Header.MeasurementId = "id";
            writer.Header.RecorderId = "id";
            writer.Header.RecorderIndex = 3;
            Assert.Equal(0.0, writer.Header.StartAngle);
            Assert.Equal(0.0, writer.Header.StartDistance);
            Assert.Equal(string.Empty, writer.Header.MeasurementId);
            Assert.Equal(string.Empty, writer.Header.RecorderId);
            Assert.Equal(0L, writer.Header.RecorderIndex);
            Assert.False(writer.Header.IsStartAngleUsed);
            writer.Dispose();
        }
    }
}
