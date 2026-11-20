// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// through the facade: MdfAttachment.CreateMetaData and MdfEvent.MetaData give the AT and EV comment an MD block.
// Its tags are written with the comment (ATcomment / EVcomment XML); the text stays in the TX element of that XML.
using System;
using System.IO;
using System.Text;
using Xunit;

namespace MdfLib.Tests
{
    public sealed class MdfLibraryAttachmentEventMetaDataTests : IDisposable
    {
        private readonly string directory_;

        public MdfLibraryAttachmentEventMetaDataTests()
        {
            directory_ = Path.Combine(Path.GetTempPath(), "mdflib-facade-at-ev-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory_);
        }

        public void Dispose()
        {
            try { Directory.Delete(directory_, true); }
            catch (IOException) { }
        }

        [Fact]
        public void AttachmentAndEventCommentsCarryTheirTags()
        {
            string file = Path.Combine(directory_, "at-ev.mf4");
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            Assert.True(writer.Init(file));
            MdfLibrary.MdfChannelGroup group = writer.CreateDataGroup().ChannelGroups[0];
            group.Name = "Annotated";
            MdfLibrary.MdfChannel master = group.CreateChannel();
            master.Name = "Time";
            master.Unit = "s";
            master.Type = MdfLibrary.ChannelType.Master;
            master.Sync = MdfLibrary.ChannelSyncType.Time;
            master.DataType = MdfLibrary.ChannelDataType.FloatLe;
            master.BitCount = 64;

            MdfLibrary.MdfAttachment attachment = writer.Header.CreateAttachment();
            attachment.FileName = "notes.txt";
            MdfLibrary.MdfMetaData attachmentMetaData = attachment.CreateMetaData();
            Assert.Same(attachmentMetaData, attachment.CreateMetaData());
            Assert.Same(attachmentMetaData, attachment.MetaData);
            attachmentMetaData.AddCommonProperty(new MdfLibrary.MdfETag { Name = "Attachment.Tag", DataType = MdfLibrary.ETagDataType.StringType, ValueAsString = "one" });

            MdfLibrary.MdfEvent evt = writer.Header.CreateEvent();
            evt.Name = "Trigger";
            evt.Description = "marker text";
            evt.AddAttachment(attachment);
            MdfLibrary.MdfMetaData eventMetaData = evt.MetaData;
            Assert.Same(eventMetaData, evt.MetaData);
            eventMetaData.AddCommonProperty(new MdfLibrary.MdfETag { Name = "Event.Tag", DataType = MdfLibrary.ETagDataType.StringType, ValueAsString = "two" });

            Assert.True(writer.InitMeasurement());
            writer.StartMeasurement(1000000000UL);
            master.SetChannelValue(0.0);
            writer.SaveSample(group, 1000000000UL);
            Assert.True(writer.FinalizeMeasurement());
            writer.Dispose();

            string text = Encoding.ASCII.GetString(File.ReadAllBytes(file));
            Assert.Contains("<ATcomment>", text);
            Assert.Contains("Attachment.Tag", text);
            Assert.Contains("<EVcomment>", text);
            Assert.Contains("Event.Tag", text);
            Assert.Contains("<TX>marker text</TX>", text);
        }

        [Fact]
        public void CreatorIndicesOfAttachmentAndEventAreWrittenAndReadBack()
        {
            string file = Path.Combine(directory_, "creator.mf4");
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            Assert.True(writer.Init(file));
            MdfLibrary.MdfChannelGroup group = writer.CreateDataGroup().ChannelGroups[0];
            group.Name = "Creators";
            MdfLibrary.MdfChannel master = group.CreateChannel();
            master.Name = "Time";
            master.Unit = "s";
            master.Type = MdfLibrary.ChannelType.Master;
            master.Sync = MdfLibrary.ChannelSyncType.Time;
            master.DataType = MdfLibrary.ChannelDataType.FloatLe;
            master.BitCount = 64;

            MdfLibrary.MdfAttachment attachment = writer.Header.CreateAttachment();
            attachment.FileName = "notes.txt";
            attachment.CreatorIndex = 2;
            MdfLibrary.MdfEvent evt = writer.Header.CreateEvent();
            evt.Name = "Trigger";
            evt.CreatorIndex = 3UL;
            evt.AddAttachment(attachment);

            Assert.True(writer.InitMeasurement());
            writer.StartMeasurement(1000000000UL);
            master.SetChannelValue(0.0);
            writer.SaveSample(group, 1000000000UL);
            Assert.True(writer.FinalizeMeasurement());
            writer.Dispose();

            MdfLibrary.MdfReader reader = new MdfLibrary.MdfReader(file);
            try
            {
                Assert.True(reader.ReadEverythingButData());
                Assert.Equal((ushort)2, reader.Header.Attachments[0].CreatorIndex);
                Assert.Equal(3UL, reader.Header.Events[0].CreatorIndex);
            }
            finally { reader.Dispose(); }
        }
    }
}
