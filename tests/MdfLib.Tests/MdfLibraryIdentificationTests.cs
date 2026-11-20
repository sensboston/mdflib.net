// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// and si_flags the wrapper surface sections 10.16 and 10.19): the facade writes the MDF Library
// v2.3.0 identification and the source's si_flags as they are, and reads si_flags back from the file.
using System;
using System.IO;
using System.Text;
using Xunit;

namespace MdfLib.Tests
{
    public sealed class MdfLibraryIdentificationTests : IDisposable
    {
        private readonly string directory_;

        public MdfLibraryIdentificationTests()
        {
            directory_ = Path.Combine(Path.GetTempPath(), "mdflib-facade-e11-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory_);
        }

        public void Dispose()
        {
            try { Directory.Delete(directory_, true); }
            catch (IOException) { }
        }

        // One master with a source named on the group; flags is set on the source only when given.
        private string Write(string name, MdfLibrary.MdfWriterType type, byte? flags, string program = null)
        {
            string file = Path.Combine(directory_, name);
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(type);
            Assert.True(writer.Init(file));
            if (program != null) { writer.File.ProgramId = program; }
            MdfLibrary.MdfChannelGroup group = writer.CreateDataGroup().ChannelGroups[0];
            // MDF 3.30 has no acquisition name and no source block per group (refused by the facade).
            if (type != MdfLibrary.MdfWriterType.Mdf3Basic)
            {
                group.Name = "Engine";
                MdfLibrary.MdfSourceInformation source = group.CreateSourceInformation();
                source.Name = "TestBench";
                if (flags.HasValue) { source.Flags = flags.Value; }
            }
            MdfLibrary.MdfChannel master = group.CreateChannel();
            master.Name = "Time";
            master.Unit = "s";
            master.Type = MdfLibrary.ChannelType.Master;
            master.Sync = MdfLibrary.ChannelSyncType.Time;
            master.DataType = MdfLibrary.ChannelDataType.FloatLe;
            master.BitCount = 64;
            Assert.True(writer.InitMeasurement());
            writer.StartMeasurement(1000000000UL);
            master.SetChannelValue(0.0);
            writer.SaveSample(group, 1000000000UL);
            Assert.True(writer.FinalizeMeasurement());
            writer.Dispose();
            return file;
        }

        private static byte[] Slice(byte[] bytes, int offset, int count)
        {
            byte[] result = new byte[count];
            Array.Copy(bytes, offset, result, 0, count);
            return result;
        }

        [Fact]
        public void Mdf4ProgramIdentifierIsEmptyAsInV230()
        {
            byte[] bytes = File.ReadAllBytes(Write("mdf4.mf4", MdfLibrary.MdfWriterType.Mdf4Basic, null));
            Assert.Equal(new byte[8], Slice(bytes, 16, 8));
        }

        [Fact]
        public void Mdf3ProgramIdentifierIsMdfWriteAndTimerIdentificationIsEmptyAsInV230()
        {
            byte[] bytes = File.ReadAllBytes(Write("mdf3.mdf", MdfLibrary.MdfWriterType.Mdf3Basic, null));
            Assert.Equal("MdfWrite", Encoding.ASCII.GetString(bytes, 16, 8));
            Assert.Equal(new byte[32], Slice(bytes, 240, 32));
        }

        [Fact]
        public void ProgramIdSetBeforeInitIsWrittenPaddedWithZerosAndReadBack()
        {
            string file = Write("program.mf4", MdfLibrary.MdfWriterType.Mdf4Basic, null, "TestRig");
            Assert.Equal("TestRig\0", Encoding.ASCII.GetString(File.ReadAllBytes(file), 16, 8));
            MdfLibrary.MdfReader reader = new MdfLibrary.MdfReader(file);
            try
            {
                Assert.True(reader.ReadEverythingButData());
                Assert.Equal("TestRig", reader.File.ProgramId);
            }
            finally { reader.Dispose(); }
        }

        [Fact]
        public void SourceFlagsAreZeroUnlessSetAndReadBackFromTheFile()
        {
            MdfLibrary.MdfReader reader = new MdfLibrary.MdfReader(Write("unset.mf4", MdfLibrary.MdfWriterType.Mdf4Basic, null));
            try
            {
                Assert.True(reader.ReadEverythingButData());
                Assert.Equal((byte)0, reader.get_DataGroup(0).ChannelGroups[0].SourceInformation.Flags);
            }
            finally { reader.Dispose(); }
        }

        [Fact]
        public void SourceFlagsWrittenAsSetAreReadBackFromTheFile()
        {
            MdfLibrary.MdfReader reader = new MdfLibrary.MdfReader(Write("set.mf4", MdfLibrary.MdfWriterType.Mdf4Basic, 5));
            try
            {
                Assert.True(reader.ReadEverythingButData());
                Assert.Equal((byte)5, reader.get_DataGroup(0).ChannelGroups[0].SourceInformation.Flags);
            }
            finally { reader.Dispose(); }
        }
    }
}
