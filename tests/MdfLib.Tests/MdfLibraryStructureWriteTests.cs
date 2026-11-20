// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// structures): CreateChannelComposition writes the components of a
// structure channel into the structure's window. The engine reads the components back and the window holds their bytes.
using System;
using System.IO;
using MdfLib;
using Xunit;

namespace MdfLib.Tests
{
    public sealed class MdfLibraryStructureWriteTests : IDisposable
    {
        private readonly string directory_;

        public MdfLibraryStructureWriteTests()
        {
            directory_ = Path.Combine(Path.GetTempPath(), "mdflib-facade-structure-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory_);
        }

        public void Dispose()
        {
            try { Directory.Delete(directory_, true); }
            catch (IOException) { }
        }

        private static int IndexOf(MdfChannelGroup group, string name)
        {
            for (int c = 0; c < group.Channels.Count; c++)
            {
                if (group.Channels[c].Name == name) { return c; }
            }
            throw new InvalidOperationException("No channel named " + name);
        }

        [Fact]
        public void StructureIsWrittenWithItsComponentsInsideItsWindow()
        {
            string file = Path.Combine(directory_, "structure.mf4");
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            Assert.True(writer.Init(file));
            MdfLibrary.MdfChannelGroup group = writer.CreateDataGroup().ChannelGroups[0];
            group.Name = "Structured";
            MdfLibrary.MdfChannel master = group.CreateChannel();
            master.Name = "Time";
            master.Unit = "s";
            master.Type = MdfLibrary.ChannelType.Master;
            master.Sync = MdfLibrary.ChannelSyncType.Time;
            master.DataType = MdfLibrary.ChannelDataType.FloatLe;
            master.BitCount = 64;
            MdfLibrary.MdfChannel packet = group.CreateChannel();
            packet.Name = "Packet";
            MdfLibrary.MdfChannel id = packet.CreateChannelComposition();
            id.Name = "Id";
            id.DataType = MdfLibrary.ChannelDataType.UnsignedIntegerLe;
            id.BitCount = 16;
            MdfLibrary.MdfChannel value = packet.CreateChannelComposition();
            value.Name = "Value";
            value.DataType = MdfLibrary.ChannelDataType.FloatLe;
            value.BitCount = 64;
            Assert.Equal(2, packet.ChannelCompositions.Length);
            Assert.Equal("Value", packet.ChannelCompositions[1].Name);
            Assert.True(writer.InitMeasurement());
            writer.StartMeasurement(1000000000UL);
            for (int row = 0; row < 3; row++)
            {
                id.SetChannelValue((ulong)(100 + row));
                value.SetChannelValue(0.5 * row);
                writer.SaveSample(group, 1000000000UL + (ulong)row * 1000000000UL);
            }
            Assert.True(writer.FinalizeMeasurement());
            writer.Dispose();

            using (MdfReader reader = MdfReader.Open(File.OpenRead(file)))
            {
                MdfChannelGroup read = reader.ReadMetadata()[0].ChannelGroups[0];
                int index = IndexOf(read, "Packet");
                MdfChannel readPacket = read.Channels[index];
                Assert.Equal(2, readPacket.Components.Count);
                Assert.Equal("Id", readPacket.Components[0].Name);
                Assert.Equal("Value", readPacket.Components[1].Name);
                Assert.Equal(10, readPacket.StoredDataType); // the window is a byte array

                int filled = 0;
                foreach (MdfSampleBatch batch in reader.ReadSamples(read, new[] { index }, 0UL, 3UL, 1024, MdfValueRepresentation.Raw))
                {
                    for (int r = 0; r < batch.SampleCount; r++, filled++)
                    {
                        byte[] bytes = batch.GetValue(r, 0).GetByteArrayValue();
                        Assert.Equal(10, bytes.Length);
                        Assert.Equal((ulong)(100 + filled), (ulong)(bytes[0] | (bytes[1] << 8)));
                        Assert.Equal(0.5 * filled, BitConverter.ToDouble(bytes, 2));
                    }
                }
                Assert.Equal(3, filled);
            }
        }

        [Fact]
        public void StructureRefusesWhatTheManagedWriterCannotWrite()
        {
            // MDF 3.30 has no composition.
            MdfLibrary.MdfWriter mdf3 = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf3Basic);
            Assert.True(mdf3.Init(Path.Combine(directory_, "mdf3.mdf")));
            MdfLibrary.MdfChannel plain = mdf3.CreateDataGroup().ChannelGroups[0].CreateChannel();
            Assert.Throws<NotSupportedException>(() => plain.CreateChannelComposition());
            mdf3.Dispose();

            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            Assert.True(writer.Init(Path.Combine(directory_, "refused.mf4")));
            MdfLibrary.MdfChannelGroup group = writer.CreateDataGroup().ChannelGroups[0];
            MdfLibrary.MdfChannel master = group.CreateChannel();
            master.Name = "Time";
            master.Type = MdfLibrary.ChannelType.Master;
            master.Sync = MdfLibrary.ChannelSyncType.Time;
            master.DataType = MdfLibrary.ChannelDataType.FloatLe;
            master.BitCount = 64;
            // A master cannot be a structure.
            Assert.Throws<NotSupportedException>(() => master.CreateChannelComposition());

            MdfLibrary.MdfChannel packet = group.CreateChannel();
            packet.Name = "Packet";
            MdfLibrary.MdfChannel component = packet.CreateChannelComposition();
            component.Name = "Odd";
            component.DataType = MdfLibrary.ChannelDataType.UnsignedIntegerLe;
            component.BitCount = 12; // not whole bytes
            // Nested structures are written in 1.1.
            Assert.Throws<NotSupportedException>(() => component.CreateChannelComposition());
            // The structure is refused when the measurement starts, because its window cannot be packed.
            Assert.Throws<NotSupportedException>(() => writer.InitMeasurement());
            writer.Dispose();
        }

        [Fact]
        public void CompositionCannotBeAddedAfterTheMeasurementIsInitialized()
        {
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            Assert.True(writer.Init(Path.Combine(directory_, "late.mf4")));
            MdfLibrary.MdfChannelGroup group = writer.CreateDataGroup().ChannelGroups[0];
            MdfLibrary.MdfChannel master = group.CreateChannel();
            master.Name = "Time";
            master.Type = MdfLibrary.ChannelType.Master;
            master.Sync = MdfLibrary.ChannelSyncType.Time;
            master.DataType = MdfLibrary.ChannelDataType.FloatLe;
            master.BitCount = 64;
            MdfLibrary.MdfChannel packet = group.CreateChannel();
            packet.Name = "Packet";
            MdfLibrary.MdfChannel component = packet.CreateChannelComposition();
            component.Name = "Id";
            component.DataType = MdfLibrary.ChannelDataType.UnsignedIntegerLe;
            component.BitCount = 8;
            Assert.True(writer.InitMeasurement());
            Assert.Throws<InvalidOperationException>(() => packet.CreateChannelComposition());
            writer.Dispose();
        }
    }
}
