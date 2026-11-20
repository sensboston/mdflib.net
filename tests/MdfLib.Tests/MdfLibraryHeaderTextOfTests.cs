// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// through the facade: a header common property of the four HD fields (author, department, project, subject) that is
// set with a tag of another kind is written as its text, as v2.3.0 reads it (the e element's text, whatever its type).
using System;
using System.IO;
using System.Text;
using Xunit;

namespace MdfLib.Tests
{
    public sealed class MdfLibraryHeaderTextOfTests : IDisposable
    {
        private readonly string directory_;

        public MdfLibraryHeaderTextOfTests()
        {
            directory_ = Path.Combine(Path.GetTempPath(), "mdflib-facade-hd-text-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory_);
        }

        public void Dispose()
        {
            try { Directory.Delete(directory_, true); }
            catch (IOException) { }
        }

        [Fact]
        public void FloatHeaderAuthorIsWrittenAsItsText()
        {
            string file = Path.Combine(directory_, "hd-text.mf4");
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            Assert.True(writer.Init(file));
            MdfLibrary.MdfChannelGroup group = writer.CreateDataGroup().ChannelGroups[0];
            group.Name = "Header";
            MdfLibrary.MdfChannel master = group.CreateChannel();
            master.Name = "Time";
            master.Unit = "s";
            master.Type = MdfLibrary.ChannelType.Master;
            master.Sync = MdfLibrary.ChannelSyncType.Time;
            master.DataType = MdfLibrary.ChannelDataType.FloatLe;
            master.BitCount = 64;
            writer.Header.MetaData.AddCommonProperty(new MdfLibrary.MdfETag { Name = "author", DataType = MdfLibrary.ETagDataType.FloatType, ValueAsFloat = 1.5 });

            Assert.True(writer.InitMeasurement());
            writer.StartMeasurement(1000000000UL);
            master.SetChannelValue(0.0);
            writer.SaveSample(group, 1000000000UL);
            Assert.True(writer.FinalizeMeasurement());
            writer.Dispose();

            string text = Encoding.ASCII.GetString(File.ReadAllBytes(file));
            Assert.Contains("name=\"author\"", text);
            Assert.Contains(">1.5</e>", text);
        }
    }
}
