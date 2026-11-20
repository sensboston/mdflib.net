// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace MdfLib.Tests.Writing
{
    // writer attachments (AT) and events (EV), appended chains and copier support.
    public sealed class WriterAttachmentEventTests
    {
        private static readonly byte[] Small = Encoding.ASCII.GetBytes("BO_ 100 Engine: 8 ECU");

        private static byte[] Large()
        {
            byte[] data = new byte[5000];
            for (int i = 0; i < data.Length; i++) { data[i] = (byte)(i % 13); }
            return data;
        }

        private static void Ramp(MdfWriter writer, int rows)
        {
            writer.StartMeasurement();
            for (int i = 0; i < rows; i++)
            { writer.SaveSample(new[] { MdfSampleValue.FromFloatingPoint(i), MdfSampleValue.FromUnsigned((ulong)i) }); }
            writer.StopMeasurement();
            writer.FinalizeMeasurement();
        }

        private static byte[] Write(string version, bool compress = false, bool groupName = false)
        {
            MemoryStream output = new MemoryStream();
            using (MdfWriter writer = MdfWriter.Create(output, true, new MdfWriterOptions { Version = version, CompressData = compress }))
            {
                writer.AddChannel(WriterMultiGroupTests.Master());
                writer.AddChannel(WriterConfigurationTests.Channel("level", 16));
                int second = writer.AddChannelGroup();
                writer.SetChannelGroupName(second, "second");
                writer.AddChannel(second, WriterConfigurationTests.Channel("other", 8));
                Assert.Equal(0, writer.AddAttachment(new MdfAttachmentDefinition { FileName = "engine.dbc", MimeType = "text/plain", Comment = "database", Data = Small }));
                Assert.Equal(1, writer.AddAttachment(new MdfAttachmentDefinition { FileName = "big.bin", Data = Large(), Compress = true }));
                Assert.Equal(2, writer.AddAttachment(new MdfAttachmentDefinition { FileName = "external.a2l", Comment = "not embedded" }));
                Assert.Equal(0, writer.AddEvent(new MdfEventDefinition
                {
                    Name = "start", Comment = "range", Type = MdfEventType.Trigger, RangeType = MdfEventRangeType.RangeBegin, Cause = MdfEventCause.User,
                    SyncBaseValue = 15, SyncFactor = 0.1, ScopeGroupIndices = new[] { 0, second }, AttachmentIndices = new[] { 0, 2 },
                    GroupName = groupName ? "laps" : string.Empty
                }));
                Assert.Equal(1, writer.AddEvent(new MdfEventDefinition
                { Name = "stop", RangeType = MdfEventRangeType.RangeEnd, RangeIndex = 0, ParentIndex = 0, SyncBaseValue = 42 }));
                Assert.Equal(3, writer.AttachmentCount);
                Assert.Equal(2, writer.EventCount);
                writer.StartMeasurement();
                for (int i = 0; i < 20; i++)
                {
                    writer.SaveSample(new[] { MdfSampleValue.FromFloatingPoint(i), MdfSampleValue.FromUnsigned((ulong)i) });
                    if (i % 4 == 0) { writer.SaveSample(second, new[] { MdfSampleValue.FromUnsigned((ulong)i) }); }
                }
                Assert.Equal(2, writer.AddEvent(new MdfEventDefinition { Name = "mark", SyncBaseValue = 7, AttachmentIndices = new[] { 1 } }));
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
                Assert.Throws<InvalidOperationException>(() => writer.AddEvent(new MdfEventDefinition()));
            }
            return output.ToArray();
        }

        private static List<MdfChannelGroup> Groups(MdfReader reader)
        {
            return reader.ReadMetadata().SelectMany(group => group.ChannelGroups).Where(group => group.CanReadSamples).ToList();
        }

        private static void AssertModel(MdfReader reader, bool groupName)
        {
            MdfFileModel model = reader.ReadFileModel();
            List<MdfChannelGroup> groups = Groups(reader);
            Assert.Equal(3, model.Attachments.Count);
            MdfAttachment small = model.Attachments[0], large = model.Attachments[1], external = model.Attachments[2];
            Assert.Equal("engine.dbc", small.FileName);
            Assert.Equal("text/plain", small.MimeType);
            Assert.Equal("database", small.Comment);
            Assert.True(small.IsEmbedded && small.HasChecksum && !small.IsCompressed);
            Assert.Equal(Small, reader.ReadAttachment(small));
            Assert.True(large.IsEmbedded && large.IsCompressed && large.HasChecksum);
            Assert.Equal(5000UL, large.OriginalSize);
            Assert.True(large.EmbeddedSize < 5000UL);
            Assert.Equal(Large(), reader.ReadAttachment(large));
            Assert.False(external.IsEmbedded);
            Assert.Equal("external.a2l", external.FileName);
            Assert.Equal("not embedded", external.Comment);
            Assert.Throws<NotSupportedException>(() => reader.ReadAttachment(external));

            Assert.Equal(3, model.Events.Count);
            MdfEvent start = model.Events[0], stop = model.Events[1], mark = model.Events[2];
            Assert.Equal("start", start.Name);
            Assert.Equal("range", start.Comment);
            Assert.Equal(MdfEventType.Trigger, start.Type);
            Assert.Equal(MdfEventSyncType.Time, start.SyncType);
            Assert.Equal(MdfEventRangeType.RangeBegin, start.RangeType);
            Assert.Equal(MdfEventCause.User, start.Cause);
            Assert.Equal(15L, start.SyncBaseValue);
            Assert.Equal(0.1, start.SyncFactor);
            Assert.Equal(groupName ? "laps" : string.Empty, start.GroupName);
            Assert.Equal(new[] { groups[0], groups[1] }, start.ScopeChannelGroups);
            Assert.Empty(start.ScopeChannels);
            Assert.Equal(new[] { small, external }, start.Attachments);
            Assert.Null(start.Parent);
            Assert.Same(start, stop.Parent);
            Assert.Same(start, stop.Range);
            Assert.Equal(MdfEventRangeType.RangeEnd, stop.RangeType);
            Assert.Equal(MdfEventType.Marker, mark.Type);
            Assert.Equal(7.0, mark.SyncValue);
            Assert.Equal(new[] { large }, mark.Attachments);
            Assert.Empty(mark.ScopeChannelGroups);
            Assert.Equal(20UL, groups[0].SampleCount);
            Assert.Equal(5UL, groups[1].SampleCount);
        }

        [Theory]
        [InlineData("4.10", false, false)]
        [InlineData("4.20", false, true)]
        [InlineData("4.20", true, true)]
        public void AttachmentsAndEventsRoundTrip(string version, bool compress, bool groupName)
        {
            byte[] file = Write(version, compress, groupName);
            Assert.Equal("MDF     ", Encoding.ASCII.GetString(file, 0, 8));
            Assert.Equal(0, file[60] | file[61] | file[62] | file[63]);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file))) { AssertModel(reader, groupName); }
            Assert.True(MdfValidator.Validate(new MemoryStream(file)).IsValid);
        }

        [Fact]
        public void InvalidDefinitionsAreRejected()
        {
            using (MdfWriter writer = MdfWriter.Create(new MemoryStream(), false, new MdfWriterOptions { Version = "4.10" }))
            {
                Assert.Throws<ArgumentNullException>(() => writer.AddAttachment(null));
                Assert.Throws<ArgumentNullException>(() => writer.AddEvent(null));
                Assert.Throws<ArgumentException>(() => writer.AddAttachment(new MdfAttachmentDefinition()));
                Assert.Throws<ArgumentException>(() => writer.AddAttachment(new MdfAttachmentDefinition { FileName = "x", Compress = true }));
                Assert.Throws<ArgumentException>(() => writer.AddAttachment(new MdfAttachmentDefinition { FileName = "a\0b", Data = Small }));
                Assert.Throws<ArgumentOutOfRangeException>(() => writer.AddEvent(new MdfEventDefinition { ParentIndex = 0 }));
                Assert.Throws<ArgumentOutOfRangeException>(() => writer.AddEvent(new MdfEventDefinition { RangeIndex = -2 }));
                Assert.Throws<ArgumentOutOfRangeException>(() => writer.AddEvent(new MdfEventDefinition { ScopeGroupIndices = new[] { 1 } }));
                Assert.Throws<ArgumentOutOfRangeException>(() => writer.AddEvent(new MdfEventDefinition { AttachmentIndices = new[] { 0 } }));
                Assert.Throws<ArgumentOutOfRangeException>(() => writer.AddEvent(new MdfEventDefinition { Type = (MdfEventType)9 }));
                Assert.Throws<ArgumentException>(() => writer.AddEvent(new MdfEventDefinition { SyncFactor = double.NaN }));
                Assert.Throws<NotSupportedException>(() => writer.AddEvent(new MdfEventDefinition { GroupName = "g" }));
                Assert.Equal(0, writer.AttachmentCount);
                Assert.Equal(0, writer.EventCount);
            }
            using (MdfWriter writer = MdfWriter.Create(new MemoryStream(), false, new MdfWriterOptions { Version = "3.30" }))
            {
                Assert.Throws<NotSupportedException>(() => writer.AddAttachment(new MdfAttachmentDefinition { FileName = "x" }));
                Assert.Throws<NotSupportedException>(() => writer.AddEvent(new MdfEventDefinition()));
            }
        }

        [Fact]
        public void AttachmentsAreChargedAgainstTheMetadataLimit()
        {
            using (MdfWriter writer = MdfWriter.Create(new MemoryStream(), false, new MdfWriterOptions { Version = "4.10", MaximumMetadataBytes = 8192 }))
            {
                Assert.Throws<MdfResourceLimitException>(() => writer.AddAttachment(new MdfAttachmentDefinition { FileName = "big", Data = new byte[8192] }));
                Assert.Equal(0, writer.AttachmentCount);
                Assert.Equal(0, writer.AddAttachment(new MdfAttachmentDefinition { FileName = "small", Data = Small }));
            }
        }

        [Fact]
        public void AppendChainsAttachmentsAndEventsAfterTheExistingOnes()
        {
            MemoryStream stream = new MemoryStream();
            byte[] original = Write("4.20");
            stream.Write(original, 0, original.Length);
            using (MdfWriter writer = MdfWriter.Append(stream, true))
            {
                writer.SetChannelGroupName(0, "appended");
                writer.AddChannel(WriterMultiGroupTests.Master());
                writer.AddChannel(WriterConfigurationTests.Channel("level", 16));
                int added = writer.AddAttachment(new MdfAttachmentDefinition { FileName = "late.txt", Data = Small });
                writer.AddEvent(new MdfEventDefinition { Name = "appended", ScopeGroupIndices = new[] { 0 }, AttachmentIndices = new[] { added } });
                Ramp(writer, 6);
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(stream.ToArray())))
            {
                MdfFileModel model = reader.ReadFileModel();
                Assert.Equal(4, model.Attachments.Count);
                Assert.Equal(4, model.Events.Count);
                Assert.Equal("late.txt", model.Attachments[3].FileName);
                Assert.Equal(Small, reader.ReadAttachment(model.Attachments[3]));
                MdfEvent appended = model.Events[3];
                Assert.Equal("appended", appended.Name);
                MdfChannelGroup group = Assert.Single(appended.ScopeChannelGroups);
                Assert.Equal("appended", group.Name);
                Assert.Equal(6UL, group.SampleCount);
                Assert.Same(model.Attachments[3], Assert.Single(appended.Attachments));
                Assert.Same(model.Events[0], model.Events[1].Parent);
            }
        }

        [Fact]
        public void AppendWithoutExistingChainsLinksTheHeader()
        {
            MemoryStream stream = new MemoryStream();
            using (MdfWriter writer = MdfWriter.Create(stream, true, new MdfWriterOptions { Version = "4.10" }))
            {
                writer.AddChannel(WriterMultiGroupTests.Master());
                writer.AddChannel(WriterConfigurationTests.Channel("level", 16));
                Ramp(writer, 3);
            }
            using (MdfWriter writer = MdfWriter.Append(stream, true))
            {
                writer.AddChannel(WriterMultiGroupTests.Master());
                writer.AddChannel(WriterConfigurationTests.Channel("level", 16));
                writer.AddEvent(new MdfEventDefinition { Name = "only" });
                Ramp(writer, 2);
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(stream.ToArray())))
            {
                MdfFileModel model = reader.ReadFileModel();
                Assert.Empty(model.Attachments);
                Assert.Equal("only", Assert.Single(model.Events).Name);
            }
        }

        [Fact]
        public void CopierCopiesAttachmentsAndEvents()
        {
            byte[] source = Write("4.20", false, true);
            MdfCopyResult result;
            byte[] copy;
            using (MdfReader reader = MdfReader.Open(new MemoryStream(source)))
            using (MemoryStream output = new MemoryStream())
            {
                result = MdfFileCopier.Copy(reader, output, new MdfCopyOptions { WriterOptions = new MdfWriterOptions { Version = "4.20" } });
                copy = output.ToArray();
            }
            Assert.Empty(result.Skipped);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(copy))) { AssertModel(reader, true); }
        }

        [Fact]
        public void CopierReportsWhatItCannotKeep()
        {
            byte[] source = Write("4.20", false, true);
            MdfCopyResult result;
            using (MdfReader reader = MdfReader.Open(new MemoryStream(source)))
            using (MemoryStream output = new MemoryStream())
            {
                result = MdfFileCopier.Copy(reader, output, new MdfCopyOptions { WriterOptions = new MdfWriterOptions { Version = "4.10" }, CopyAttachments = false });
                using (MdfReader copy = MdfReader.Open(new MemoryStream(output.ToArray())))
                {
                    MdfFileModel model = copy.ReadFileModel();
                    Assert.Empty(model.Attachments);
                    Assert.Equal(3, model.Events.Count);
                    Assert.Empty(model.Events[0].Attachments);
                    Assert.Equal(string.Empty, model.Events[0].GroupName);
                    Assert.Same(model.Events[0], model.Events[1].Range);
                }
            }
            Assert.Contains("event 0: an attachment reference was not copied", result.Skipped);
            Assert.Contains("event 0: the group name needs MDF 4.20 output", result.Skipped);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(source)))
            using (MemoryStream output = new MemoryStream())
            {
                result = MdfFileCopier.Copy(reader, output, new MdfCopyOptions { WriterOptions = new MdfWriterOptions { Version = "3.30" } });
            }
            Assert.Contains("attachments: MDF 3.30 output has no attachments", result.Skipped);
            Assert.Contains("events: MDF 3.30 output has no events", result.Skipped);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(source)))
            using (MemoryStream output = new MemoryStream())
            {
                result = MdfFileCopier.Copy(reader, output, new MdfCopyOptions { CopyAttachments = false, CopyEvents = false });
                using (MdfReader copy = MdfReader.Open(new MemoryStream(output.ToArray())))
                {
                    Assert.Empty(copy.ReadFileModel().Attachments);
                    Assert.Empty(copy.ReadFileModel().Events);
                }
            }
        }
    }
}
