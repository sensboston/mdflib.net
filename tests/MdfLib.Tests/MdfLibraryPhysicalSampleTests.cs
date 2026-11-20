// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// localisation through the facade: a 16-bit raw channel with a conversion, written with MdfLibrary.MdfWriter and
// read back by the engine. The raw values are checked first. The double overload follows the wrapper's
// static_cast<uint64_t>(double) for an integer channel, so 3.0 is the raw value 3 (not its IEEE bit pattern).
using System;
using System.IO;
using System.Linq;
using MdfLib;
using Xunit;

namespace MdfLib.Tests
{
    public sealed class MdfLibraryPhysicalSampleTests : IDisposable
    {
        private static readonly ulong[] Raw = { 0, 3, 7 };
        private readonly string directory_;

        public MdfLibraryPhysicalSampleTests()
        {
            directory_ = Path.Combine(Path.GetTempPath(), "mdflib-facade-physical-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory_);
        }

        public void Dispose()
        {
            try { Directory.Delete(directory_, true); }
            catch (IOException) { }
        }

        // The raw value is set with an overload: "ulong" (the raw value) or "double" (the wrapper's double overload).
        private string Write(string name, Action<MdfLibrary.MdfChannel> configure, bool asDouble)
        {
            string file = Path.Combine(directory_, name + ".mf4");
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            Assert.True(writer.Init(file));
            MdfLibrary.MdfChannelGroup group = writer.CreateDataGroup().ChannelGroups[0];
            group.Name = "Physical";
            MdfLibrary.MdfChannel master = group.CreateChannel();
            master.Name = "Time";
            master.Unit = "s";
            master.Type = MdfLibrary.ChannelType.Master;
            master.Sync = MdfLibrary.ChannelSyncType.Time;
            master.DataType = MdfLibrary.ChannelDataType.FloatLe;
            master.BitCount = 64;
            MdfLibrary.MdfChannel raw = group.CreateChannel();
            raw.Name = "raw";
            raw.DataType = MdfLibrary.ChannelDataType.UnsignedIntegerLe;
            raw.BitCount = 16;
            configure(raw);
            Assert.True(writer.InitMeasurement());
            writer.StartMeasurement(1000000000UL);
            for (int row = 0; row < Raw.Length; row++)
            {
                if (asDouble) { raw.SetChannelValue((double)Raw[row]); }
                else { raw.SetChannelValue(Raw[row]); }
                writer.SaveSample(group, 1000000000UL + (ulong)row * 1000000000UL);
            }
            Assert.True(writer.FinalizeMeasurement());
            writer.Dispose();
            return file;
        }

        private static MdfSampleValue[] Read(string file, MdfValueRepresentation representation)
        {
            using (MdfReader reader = MdfReader.Open(File.OpenRead(file)))
            {
                MdfChannelGroup readGroup = reader.ReadMetadata()[0].ChannelGroups[0];
                int index = -1;
                for (int c = 0; c < readGroup.Channels.Count; c++)
                {
                    if (readGroup.Channels[c].Name == "raw") { index = c; }
                }
                Assert.True(index >= 0);
                MdfSampleValue[] samples = new MdfSampleValue[Raw.Length];
                int filled = 0;
                foreach (MdfSampleBatch batch in reader.ReadSamples(readGroup, new[] { index }, 0UL, (ulong)Raw.Length, 1024, representation))
                {
                    for (int r = 0; r < batch.SampleCount; r++) { samples[filled++] = batch.GetValue(r, 0); }
                }
                Assert.Equal(Raw.Length, filled);
                return samples;
            }
        }

        private static void Linear(MdfLibrary.MdfChannel raw)
        {
            MdfLibrary.MdfChannelConversion conversion = raw.CreateChannelConversion();
            conversion.Type = MdfLibrary.ConversionType.Linear;
            conversion.Parameter(0, 1.0);
            conversion.Parameter(1, 2.0);
        }

        private static void Rational(MdfLibrary.MdfChannel raw)
        {
            MdfLibrary.MdfChannelConversion conversion = raw.CreateChannelConversion();
            conversion.Type = MdfLibrary.ConversionType.Rational;
            double[] p = { 0.5, 2.0, 1.0, 0.0, 0.0, 1.0 };
            for (ushort i = 0; i < p.Length; i++) { conversion.Parameter(i, p[i]); }
        }

        // lifted: the wrapper's StartMeasurement(time) after InitMeasurement patches the header start time in place.
        [Fact]
        public void StartTimeSetAfterInitMeasurementIsWrittenInTheHeader()
        {
            string file = Path.Combine(directory_, "start.mf4");
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            Assert.True(writer.Init(file));
            MdfLibrary.MdfChannelGroup group = writer.CreateDataGroup().ChannelGroups[0];
            MdfLibrary.MdfChannel master = group.CreateChannel();
            master.Name = "Time";
            master.Type = MdfLibrary.ChannelType.Master;
            master.Sync = MdfLibrary.ChannelSyncType.Time;
            master.DataType = MdfLibrary.ChannelDataType.FloatLe;
            master.BitCount = 64;
            Assert.True(writer.InitMeasurement());
            writer.StartMeasurement(5000000000UL);
            Assert.Equal(5000000000UL, writer.StartTime);
            writer.SaveSample(group, 5000000000UL);
            Assert.True(writer.FinalizeMeasurement());
            writer.Dispose();
            using (MdfReader reader = MdfReader.Open(File.OpenRead(file)))
            {
                Assert.Equal(5000000000UL, reader.Header.RawStartTimeNanoseconds);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void LinearChannelReadsRawThenPhysical(bool asDouble)
        {
            string file = Write("linear" + asDouble, Linear, asDouble);
            Assert.Equal(Raw, Read(file, MdfValueRepresentation.Raw).Select(v => v.UnsignedValue).ToArray());
            Assert.Equal(new double[] { 1.0, 7.0, 15.0 }, Read(file, MdfValueRepresentation.Physical).Select(v => v.FloatingPointValue).ToArray());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RationalChannelReadsRawThenPhysical(bool asDouble)
        {
            // (0.5 x^2 + 2 x + 1) for the raw values 0, 3, 7: 1, 11.5, 39.5.
            string file = Write("rational" + asDouble, Rational, asDouble);
            Assert.Equal(Raw, Read(file, MdfValueRepresentation.Raw).Select(v => v.UnsignedValue).ToArray());
            Assert.Equal(new double[] { 1.0, 11.5, 39.5 }, Read(file, MdfValueRepresentation.Physical).Select(v => v.FloatingPointValue).ToArray());
        }
    }
}
