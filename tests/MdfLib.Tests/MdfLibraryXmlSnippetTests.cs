// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// through the facade: MdfMetaData.XmlSnippet is read as the common properties the tags make, and set by parsing
// its e elements into tags (the snapshot is the base of the MD XML, not copied as it is). A TX text in the snippet is
// refused: the comment of the owning block is set through its Description.
using System;
using System.IO;
using System.Text;
using Xunit;

namespace MdfLib.Tests
{
    public sealed class MdfLibraryXmlSnippetTests : IDisposable
    {
        private readonly string directory_;

        public MdfLibraryXmlSnippetTests()
        {
            directory_ = Path.Combine(Path.GetTempPath(), "mdflib-facade-snippet-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory_);
        }

        public void Dispose()
        {
            try { Directory.Delete(directory_, true); }
            catch (IOException) { }
        }

        [Fact]
        public void SnippetTagsAreWrittenAndReadBackAsTheSnippet()
        {
            string file = Path.Combine(directory_, "snippet.mf4");
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            Assert.True(writer.Init(file));
            MdfLibrary.MdfChannelGroup group = writer.CreateDataGroup().ChannelGroups[0];
            group.Name = "Snippet";
            MdfLibrary.MdfChannel master = group.CreateChannel();
            master.Name = "Time";
            master.Unit = "s";
            master.Type = MdfLibrary.ChannelType.Master;
            master.Sync = MdfLibrary.ChannelSyncType.Time;
            master.DataType = MdfLibrary.ChannelDataType.FloatLe;
            master.BitCount = 64;

            MdfLibrary.MdfMetaData metaData = group.CreateMetaData();
            metaData.XmlSnippet = @"<CGcomment><common_properties><e name=""Lab.Count"" type=""integer"">4</e><e name=""Lab.Name"">bench &amp; 2</e></common_properties></CGcomment>";
            Assert.Equal(@"<common_properties><e name=""Lab.Count"" type=""integer"">4</e><e name=""Lab.Name"" type=""string"">bench &amp; 2</e></common_properties>", metaData.XmlSnippet);

            Assert.True(writer.InitMeasurement());
            writer.StartMeasurement(1000000000UL);
            master.SetChannelValue(0.0);
            writer.SaveSample(group, 1000000000UL);
            Assert.True(writer.FinalizeMeasurement());
            writer.Dispose();

            string text = Encoding.ASCII.GetString(File.ReadAllBytes(file));
            Assert.Contains("Lab.Count", text);
            Assert.Contains("bench &amp; 2", text);
        }

        [Fact]
        public void SnippetWithTextOrUnknownElementsIsRefused()
        {
            string file = Path.Combine(directory_, "refused.mf4");
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            Assert.True(writer.Init(file));
            MdfLibrary.MdfMetaData metaData = writer.CreateDataGroup().ChannelGroups[0].CreateMetaData();
            Assert.Throws<NotSupportedException>(() => metaData.XmlSnippet = "<CGcomment><TX>text</TX></CGcomment>");
            Assert.Throws<NotSupportedException>(() => metaData.XmlSnippet = "<CGcomment><other/></CGcomment>");
            Assert.Throws<ArgumentException>(() => metaData.XmlSnippet = "<CGcomment><common_properties><e type=\"string\">no name</e></common_properties></CGcomment>");
            Assert.Throws<ArgumentException>(() => metaData.XmlSnippet = "<unclosed>");
            writer.Dispose();
        }
    }
}
