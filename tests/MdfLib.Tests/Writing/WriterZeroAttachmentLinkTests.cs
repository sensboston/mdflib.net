// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Text;
using Xunit;

namespace MdfLib.Tests.Writing
{
    // / CI b52fa1a: v2.3.0 writes zero links in an event's attachment list (an unused reference slot). The
    // reader skips such a link instead of refusing the file; a link to another address is still an error.
    public sealed class WriterZeroAttachmentLinkTests
    {
        private static byte[] WriteEventWithAttachment()
        {
            MemoryStream output = new MemoryStream();
            using (MdfWriter writer = MdfWriter.Create(output, true, new MdfWriterOptions { Version = "4.10" }))
            {
                writer.AddChannel(WriterMultiGroupTests.Master());
                writer.AddChannel(WriterConfigurationTests.Channel("level", 16));
                Assert.Equal(0, writer.AddAttachment(new MdfAttachmentDefinition { FileName = "external.a2l", Comment = "not embedded" }));
                Assert.Equal(0, writer.AddEvent(new MdfEventDefinition { Name = "mark", SyncBaseValue = 7, AttachmentIndices = new[] { 0 } }));
                writer.StartMeasurement();
                writer.SaveSample(new[] { MdfSampleValue.FromFloatingPoint(0), MdfSampleValue.FromUnsigned(1UL) });
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
            }
            return output.ToArray();
        }

        // The EV block's links are next, parent, range, name, comment, then the attachments (no scope groups, no group name).
        private static int EventBlock(byte[] bytes)
        {
            int at = Encoding.ASCII.GetString(bytes).IndexOf("##EV", StringComparison.Ordinal);
            Assert.True(at >= 0, "the file has no EV block");
            return at;
        }

        [Fact]
        public void ZeroAttachmentLinkIsSkipped()
        {
            byte[] bytes = WriteEventWithAttachment();
            int attachmentLink = EventBlock(bytes) + 24 + 8 * 5;
            Array.Clear(bytes, attachmentLink, 8);

            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfFileModel model = reader.ReadFileModel();
                Assert.Single(model.Attachments);
                Assert.Single(model.Events);
                Assert.Empty(model.Events[0].Attachments);
            }
        }

        [Fact]
        public void NonzeroLinkOutsideTheAttachmentChainStillFails()
        {
            byte[] bytes = WriteEventWithAttachment();
            int attachmentLink = EventBlock(bytes) + 24 + 8 * 5;
            // An address that is not an AT block of the chain: the reader keeps refusing it.
            Array.Clear(bytes, attachmentLink, 8);
            bytes[attachmentLink] = 0x7F;
            bytes[attachmentLink + 1] = 0x7F;

            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                Assert.Throws<MdfFormatException>(() => reader.ReadFileModel());
            }
        }
    }
}
