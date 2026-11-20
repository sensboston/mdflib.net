// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Sample: writes a description for a channel group and an attachment reference in the header, reads both back and checks them.
// The attachment here is a reference to an external file (not embedded), so the file itself is not written.
using System;
using System.IO;

namespace Samples
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                Run();
                Console.WriteLine("HeaderAndAttachments: OK");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("HeaderAndAttachments: FAILED: " + error.Message);
                return 1;
            }
        }

        private static void Run()
        {
            string file = Path.Combine(Path.GetTempPath(), "mdflib-sample-header-" + Guid.NewGuid().ToString("N") + ".mf4");
            try
            {
                Write(file);
                Read(file);
            }
            finally
            {
                File.Delete(file);
            }
        }

        private static void Write(string file)
        {
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            try
            {
                if (!writer.Init(file)) { throw new InvalidOperationException("the writer did not open " + file); }

                MdfLibrary.MdfAttachment attachment = writer.Header.CreateAttachment();
                attachment.FileName = "notes.txt";
                attachment.FileType = "TXT";
                attachment.Embedded = false;
                attachment.Compressed = false;

                MdfLibrary.MdfChannelGroup group = writer.CreateDataGroup().ChannelGroups[0];
                group.Name = "Notes";
                group.Description = "a group with a description";
                MdfLibrary.MdfChannel master = group.CreateChannel();
                master.Name = "Time";
                master.Type = MdfLibrary.ChannelType.Master;
                master.Sync = MdfLibrary.ChannelSyncType.Time;
                master.DataType = MdfLibrary.ChannelDataType.FloatLe;
                master.BitCount = 64;

                if (!writer.InitMeasurement()) { throw new InvalidOperationException("the measurement did not start"); }
                writer.StartMeasurement(1000000000UL);
                writer.SaveSample(group, 1000000000UL);
                if (!writer.FinalizeMeasurement()) { throw new InvalidOperationException("the measurement was not finalized"); }
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void Read(string file)
        {
            MdfLibrary.MdfReader reader = new MdfLibrary.MdfReader(file);
            try
            {
                if (!reader.ReadEverythingButData()) { throw new InvalidOperationException("the header did not read"); }

                MdfLibrary.MdfAttachment[] attachments = reader.Header.Attachments;
                if (attachments.Length != 1) { throw new InvalidOperationException("found " + attachments.Length + " attachments, expected 1"); }
                if (attachments[0].FileName != "notes.txt" || attachments[0].FileType != "TXT" || attachments[0].Embedded)
                {
                    throw new InvalidOperationException("the attachment reads as " + attachments[0].FileName + " / " + attachments[0].FileType);
                }
                Console.WriteLine("attachment: " + attachments[0].FileName + " (" + attachments[0].FileType + ")");

                MdfLibrary.MdfDataGroup dataGroup = reader[0];
                if (!reader.ReadData(dataGroup)) { throw new InvalidOperationException("the samples did not read"); }
                string description = dataGroup.ChannelGroups[0].Description;
                if (description != "a group with a description") { throw new InvalidOperationException("the description reads as " + description); }
                Console.WriteLine("group description: " + description);
            }
            finally
            {
                reader.Dispose();
            }
        }
    }
}
