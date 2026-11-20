// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Facade coverage): the attachments of the header and the identity members of the
// groups, channels and conversions (Index, Flags, Description, GetXChannel) are written by the managed writer and read back
// through the reader. Each value asserted here is one the writer stores; nothing is taken from another reader.
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace MdfLib.Tests
{
    public sealed class MdfLibraryHeaderTests : IDisposable
    {
        private readonly string directory_;

        public MdfLibraryHeaderTests()
        {
            directory_ = Path.Combine(Path.GetTempPath(), "mdflib-facade-header-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory_);
        }

        public void Dispose()
        {
            try { Directory.Delete(directory_, true); }
            catch (IOException) { }
        }

        private static MdfLibrary.MdfChannel Add(MdfLibrary.MdfChannelGroup group, string name, MdfLibrary.ChannelDataType type, uint bits)
        {
            MdfLibrary.MdfChannel channel = group.CreateChannel();
            channel.Name = name;
            channel.DataType = type;
            channel.BitCount = bits;
            return channel;
        }

        private static MdfLibrary.MdfChannel Named(MdfLibrary.MdfChannelGroup group, string name)
        {
            return Enumerable.First(group.Channels, channel => channel.Name == name);
        }

        [Fact]
        public void HeaderAttachmentsAreWrittenAndReadBack()
        {
            string file = Path.Combine(directory_, "attachments.mf4");
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            try
            {
                Assert.True(writer.Init(file));
                MdfLibrary.MdfAttachment attachment = writer.Header.CreateAttachment();
                attachment.FileName = "notes.txt";
                attachment.FileType = "TXT";
                attachment.Embedded = false;
                // Only embedded data can be compressed, so this reference is stored as it is.
                attachment.Compressed = false;

                MdfLibrary.MdfChannelGroup group = writer.CreateDataGroup().ChannelGroups[0];
                MdfLibrary.MdfChannel master = group.CreateChannel();
                master.Name = "Time";
                master.Type = MdfLibrary.ChannelType.Master;
                master.Sync = MdfLibrary.ChannelSyncType.Time;
                master.DataType = MdfLibrary.ChannelDataType.FloatLe;
                master.BitCount = 64;

                Assert.True(writer.InitMeasurement());
                writer.StartMeasurement(1000000000UL);
                writer.SaveSample(group, 1000000000UL);
                Assert.True(writer.FinalizeMeasurement());
            }
            finally
            {
                writer.Dispose();
            }

            MdfLibrary.MdfReader reader = new MdfLibrary.MdfReader(file);
            try
            {
                Assert.True(reader.ReadEverythingButData());
                MdfLibrary.MdfAttachment[] attachments = reader.Header.Attachments;
                Assert.Single(attachments);
                MdfLibrary.MdfAttachment read = attachments[0];
                Assert.Equal("notes.txt", read.FileName);
                Assert.Equal("TXT", read.FileType);
                Assert.False(read.Embedded);
                Assert.False(read.Compressed);
                Assert.True(read.Index >= 0);
                Assert.NotNull(read.Md5);
            }
            finally
            {
                reader.Dispose();
            }
        }

        [Fact]
        public void GroupAndChannelIdentityMembersReadBack()
        {
            string file = Path.Combine(directory_, "identity.mf4");
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            try
            {
                Assert.True(writer.Init(file));
                MdfLibrary.MdfChannelGroup group = writer.CreateDataGroup().ChannelGroups[0];
                group.Name = "Identity";
                group.Description = "group description";
                MdfLibrary.MdfChannel master = group.CreateChannel();
                master.Name = "Time";
                master.Type = MdfLibrary.ChannelType.Master;
                master.Sync = MdfLibrary.ChannelSyncType.Time;
                master.DataType = MdfLibrary.ChannelDataType.FloatLe;
                master.BitCount = 64;
                MdfLibrary.MdfChannel rational = Add(group, "Scaled", MdfLibrary.ChannelDataType.UnsignedIntegerLe, 16);
                MdfLibrary.MdfChannelConversion conversion = rational.CreateChannelConversion();
                conversion.Type = MdfLibrary.ConversionType.Rational;
                double[] six = { 0.0, 2.0, 1.0, 0.0, 0.0, 1.0 };
                for (ushort index = 0; index < six.Length; index++) { conversion.Parameter(index, six[index]); }

                Assert.True(writer.InitMeasurement());
                writer.StartMeasurement(1000000000UL);
                rational.SetChannelValue(3UL);
                writer.SaveSample(group, 1000000000UL);
                Assert.True(writer.FinalizeMeasurement());
            }
            finally
            {
                writer.Dispose();
            }

            MdfLibrary.MdfReader reader = new MdfLibrary.MdfReader(file);
            try
            {
                Assert.True(reader.ReadEverythingButData());
                MdfLibrary.MdfDataGroup dataGroup = reader[0];
                Assert.True(reader.ReadData(dataGroup));
                MdfLibrary.MdfChannelGroup group = dataGroup.ChannelGroups[0];

                Assert.Equal("Identity", group.Name);
                Assert.Equal("group description", group.Description);
                Assert.True(group.Index >= 0);
                Assert.NotNull(group.Channels);

                MdfLibrary.MdfChannel scaled = Named(group, "Scaled");
                Assert.Equal("Time", group.GetXChannel(scaled).Name);
                Assert.Null(group.GetXChannel(null));
                Assert.True(scaled.Index >= 0);
                Assert.False(scaled.PrecisionUsed);
                Assert.True(scaled.Precision >= 0);
                Assert.Null(scaled.MetaData);

                MdfLibrary.MdfChannelConversion conversion = scaled.ChannelConversion;
                Assert.True(conversion.Index >= 0);
                Assert.True(conversion.Flags >= 0);
                Assert.False(conversion.PrecisionUsed);
            }
            finally
            {
                reader.Dispose();
            }
        }
    }
}
