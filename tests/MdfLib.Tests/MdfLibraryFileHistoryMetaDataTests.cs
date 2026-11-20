// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// through the facade: MdfFileHistory.MetaData gives the writer's FH block an MD block; its tags are written with
// the FHcomment, next to the text and the tool fields.
using System;
using System.IO;
using System.Text;
using Xunit;

namespace MdfLib.Tests
{
    public sealed class MdfLibraryFileHistoryMetaDataTests : IDisposable
    {
        private readonly string directory_;

        public MdfLibraryFileHistoryMetaDataTests()
        {
            directory_ = Path.Combine(Path.GetTempPath(), "mdflib-facade-fh-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory_);
        }

        public void Dispose()
        {
            try { Directory.Delete(directory_, true); }
            catch (IOException) { }
        }

        [Fact]
        public void FileHistoryTagsAreWrittenInTheFhComment()
        {
            string file = Path.Combine(directory_, "fh.mf4");
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            Assert.True(writer.Init(file));
            MdfLibrary.MdfChannelGroup group = writer.CreateDataGroup().ChannelGroups[0];
            group.Name = "History";
            MdfLibrary.MdfChannel master = group.CreateChannel();
            master.Name = "Time";
            master.Unit = "s";
            master.Type = MdfLibrary.ChannelType.Master;
            master.Sync = MdfLibrary.ChannelSyncType.Time;
            master.DataType = MdfLibrary.ChannelDataType.FloatLe;
            master.BitCount = 64;

            MdfLibrary.MdfFileHistory history = writer.Header.CreateFileHistory();
            history.Description = "fh text";
            MdfLibrary.MdfMetaData metaData = history.MetaData;
            Assert.Same(metaData, history.MetaData);
            metaData.AddCommonProperty(new MdfLibrary.MdfETag { Name = "History.Tag", DataType = MdfLibrary.ETagDataType.StringType, ValueAsString = "kept" });

            Assert.True(writer.InitMeasurement());
            writer.StartMeasurement(1000000000UL);
            master.SetChannelValue(0.0);
            writer.SaveSample(group, 1000000000UL);
            Assert.True(writer.FinalizeMeasurement());
            writer.Dispose();

            string text = Encoding.ASCII.GetString(File.ReadAllBytes(file));
            Assert.Contains("<FHcomment>", text);
            Assert.Contains("<TX>fh text</TX>", text);
            Assert.Contains("History.Tag", text);
        }
    }
}
