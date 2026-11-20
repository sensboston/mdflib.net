// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// through the facade: MdfChannelConversion.CreateMetaData writes its tags into the conversion's comment block (MD
// XML under CCcomment), and the engine reads the comment back. A linear conversion is channel fields, so tags on it
// are refused when the file is built.
using System;
using System.IO;
using System.Text;
using MdfLib;
using Xunit;

namespace MdfLib.Tests
{
    public sealed class MdfLibraryConversionMetaDataTests : IDisposable
    {
        private readonly string directory_;

        public MdfLibraryConversionMetaDataTests()
        {
            directory_ = Path.Combine(Path.GetTempPath(), "mdflib-facade-cc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory_);
        }

        public void Dispose()
        {
            try { Directory.Delete(directory_, true); }
            catch (IOException) { }
        }

        // A 16-bit raw channel of an MDF 4 writer; the conversion is set by the caller before the file is built.
        private static MdfLibrary.MdfWriter NewWriter(string file, out MdfLibrary.MdfChannelGroup group, out MdfLibrary.MdfChannel raw)
        {
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            Assert.True(writer.Init(file));
            group = writer.CreateDataGroup().ChannelGroups[0];
            group.Name = "Converted";
            MdfLibrary.MdfChannel master = group.CreateChannel();
            master.Name = "Time";
            master.Unit = "s";
            master.Type = MdfLibrary.ChannelType.Master;
            master.Sync = MdfLibrary.ChannelSyncType.Time;
            master.DataType = MdfLibrary.ChannelDataType.FloatLe;
            master.BitCount = 64;
            raw = group.CreateChannel();
            raw.Name = "raw";
            raw.DataType = MdfLibrary.ChannelDataType.UnsignedIntegerLe;
            raw.BitCount = 16;
            return writer;
        }

        private static void Fill(MdfLibrary.MdfWriter writer, MdfLibrary.MdfChannelGroup group, MdfLibrary.MdfChannel raw)
        {
            Assert.True(writer.InitMeasurement());
            writer.StartMeasurement(1000000000UL);
            for (ulong row = 0; row < 3UL; row++)
            {
                raw.SetChannelValue(row * 3UL);
                writer.SaveSample(group, 1000000000UL + row * 1000000000UL);
            }
            Assert.True(writer.FinalizeMeasurement());
            writer.Dispose();
        }

        [Fact]
        public void ConversionTagsAreWrittenInTheCommentBlockAndTheCommentReadsBack()
        {
            string file = Path.Combine(directory_, "cc-metadata.mf4");
            MdfLibrary.MdfChannelGroup group;
            MdfLibrary.MdfChannel raw;
            MdfLibrary.MdfWriter writer = NewWriter(file, out group, out raw);
            MdfLibrary.MdfChannelConversion conversion = raw.CreateChannelConversion();
            conversion.Type = MdfLibrary.ConversionType.Rational;
            conversion.Parameter(0, 0.5);
            conversion.Parameter(1, 2.0);
            conversion.Parameter(2, 1.0);
            conversion.Parameter(3, 0.0);
            conversion.Parameter(4, 0.0);
            conversion.Parameter(5, 1.0);
            conversion.Description = "bench gain";
            MdfLibrary.MdfMetaData metaData = conversion.CreateMetaData();
            Assert.Same(metaData, conversion.CreateMetaData());
            Assert.Same(metaData, conversion.MetaData);
            metaData.AddCommonProperty(new MdfLibrary.MdfETag { Name = "Calibration.Tag", DataType = MdfLibrary.ETagDataType.StringType, ValueAsString = "delta" });
            Fill(writer, group, raw);

            string text = Encoding.ASCII.GetString(File.ReadAllBytes(file));
            Assert.Contains("<CCcomment>", text);
            Assert.Contains("Calibration.Tag", text);
            Assert.Contains("bench gain", text);

            // The engine reads the file back: the comment block of the conversion is the MD XML, whose TX holds the text.
            using (MdfReader reader = MdfReader.Open(File.OpenRead(file)))
            {
                MdfChannelGroup readGroup = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal("raw", readGroup.Channels[1].Name);
                Assert.Contains("<TX>bench gain</TX>", readGroup.Channels[1].Conversion.Comment);
            }
        }

        [Fact]
        public void TagsOnALinearConversionAreRefusedWhenTheFileIsBuilt()
        {
            string file = Path.Combine(directory_, "cc-linear.mf4");
            MdfLibrary.MdfChannelGroup group;
            MdfLibrary.MdfChannel raw;
            MdfLibrary.MdfWriter writer = NewWriter(file, out group, out raw);
            MdfLibrary.MdfChannelConversion conversion = raw.CreateChannelConversion();
            conversion.Type = MdfLibrary.ConversionType.Linear;
            conversion.Parameter(0, 1.0);
            conversion.Parameter(1, 2.0);
            conversion.CreateMetaData().AddCommonProperty(new MdfLibrary.MdfETag { Name = "Calibration.Tag", DataType = MdfLibrary.ETagDataType.StringType, ValueAsString = "delta" });
            Assert.Throws<NotSupportedException>(() => writer.InitMeasurement());
            writer.Dispose();
        }
    }
}
