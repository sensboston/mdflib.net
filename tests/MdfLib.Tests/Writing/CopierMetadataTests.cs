// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Writing
{
    // MdfFileCopier keeps nonlinear conversions and the header description/properties.
    public sealed class CopierMetadataTests
    {
        private static MdfChannelDefinition Numeric(string name, MdfConversionDefinition conversion)
        {
            return new MdfChannelDefinition { Name = name, Unit = "u", ValueKind = MdfValueKind.UnsignedInteger, BitCount = 16, Conversion = conversion };
        }

        private static byte[] Source(MdfWriterOptions options, MdfChannelDefinition master, params MdfChannelDefinition[] channels)
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(output, true, options))
            {
                writer.AddChannel(master);
                foreach (MdfChannelDefinition channel in channels) { writer.AddChannel(channel); }
                writer.StartMeasurement();
                for (int r = 0; r < 5; r++)
                {
                    List<MdfSampleValue> row = new List<MdfSampleValue> { MdfSampleValue.FromFloatingPoint(r) };
                    foreach (MdfChannelDefinition channel in channels)
                    {
                        row.Add(channel.ValueKind == MdfValueKind.Text ? MdfSampleValue.FromText(new[] { "a", "b", "c", "a", "x" }[r]) : MdfSampleValue.FromUnsigned((ulong)r));
                    }
                    writer.SaveSample(row);
                }
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
            }
            return output.Snapshot();
        }

        private static byte[] Copy(byte[] source, MdfCopyOptions options, out MdfCopyResult result)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(source)))
            using (MemoryStream output = new MemoryStream())
            {
                result = MdfFileCopier.Copy(reader, output, options);
                return output.ToArray();
            }
        }

        private static List<string> Physical(byte[] file, out MdfChannelGroup group)
        {
            List<string> rows = new List<string>();
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file)))
            {
                Assert.True(MdfValidator.Validate(reader).IsValid);
                group = reader.ReadMetadata()[0].ChannelGroups[0];
                int[] all = Enumerable.Range(0, group.Channels.Count).ToArray();
                foreach (MdfSampleBatch batch in reader.ReadSamples(group, all, representation: MdfValueRepresentation.Physical))
                {
                    for (int r = 0; r < batch.SampleCount; r++)
                    {
                        rows.Add(string.Join(";", all.Select(c => MdfCsvExporter.Format(batch.GetValue(r, c)))));
                    }
                }
            }
            return rows;
        }

        private static MdfChannelDefinition[] Channels()
        {
            return new[]
            {
                Numeric("rational", MdfConversionDefinition.Rational(0, 1, 2, 0, 0, 1)),
                Numeric("formula", MdfConversionDefinition.Algebraic("X * 2 + 1")),
                Numeric("table", MdfConversionDefinition.Table(new double[] { 0, 4 }, new double[] { 0, 40 }, true)),
                Numeric("range", MdfConversionDefinition.RangeTable(new double[] { 0, 2 }, new double[] { 1, 3 }, new double[] { 10, 20 }, -1)),
                Numeric("v2t", MdfConversionDefinition.ValueToText(new double[] { 0, 1 }, new[] { "zero", "one" }, null)),
                Numeric("r2t", MdfConversionDefinition.RangeToText(new double[] { 0, 3 }, new double[] { 2, 3 }, new[] { "low", null }, "high")),
                new MdfChannelDefinition { Name = "t2v", ValueKind = MdfValueKind.Text, BitCount = 16, Conversion = MdfConversionDefinition.TextToValue(new[] { "a" }, new double[] { 1 }, 0) },
            };
        }

        [Theory]
        [InlineData("4.10")]
        [InlineData("4.20")]
        public void Mdf4CopiesKeepEveryConversionAndPhysicalValue(string version)
        {
            byte[] source = Source(new MdfWriterOptions(), WriterMultiGroupTests.Master(), Channels());
            MdfCopyResult result;
            byte[] copy = Copy(source, new MdfCopyOptions { WriterOptions = new MdfWriterOptions { Version = version } }, out result);
            Assert.Empty(result.Skipped);
            MdfChannelGroup from, to;
            List<string> expected = Physical(source, out from), actual = Physical(copy, out to);
            Assert.Equal(expected, actual);
            Assert.Equal(8, to.Channels.Count);
            for (int c = 0; c < from.Channels.Count; c++)
            {
                Assert.Equal(from.Channels[c].Conversion.Kind, to.Channels[c].Conversion.Kind);
                Assert.Equal(from.Channels[c].Conversion.Parameters, to.Channels[c].Conversion.Parameters);
                Assert.Equal(from.Channels[c].Conversion.Texts, to.Channels[c].Conversion.Texts);
            }
        }

        [Fact]
        public void Mdf3CopySkipsConversionsItCannotStore()
        {
            byte[] source = Source(new MdfWriterOptions(), WriterMultiGroupTests.Master(), Channels());
            MdfCopyResult result;
            byte[] copy = Copy(source, new MdfCopyOptions { WriterOptions = new MdfWriterOptions { Version = "3.30" } }, out result);
            Assert.Equal(2, result.Skipped.Count);
            Assert.Contains(result.Skipped, line => line.StartsWith("DG 0 / CG 0 / range: ", StringComparison.Ordinal));
            Assert.Contains(result.Skipped, line => line.StartsWith("DG 0 / CG 0 / t2v: ", StringComparison.Ordinal));
            MdfChannelGroup group;
            List<string> rows = Physical(copy, out group);
            Assert.Equal(new[] { "t", "rational", "formula", "table", "v2t", "r2t" }, group.Channels.Select(c => c.Name));
            Assert.Equal(MdfConversionKind.RangeToText, group.Channels[5].Conversion.Kind);
            Assert.Equal(new[] { "low", null, "high" }, group.Channels[5].Conversion.Texts);
            Assert.Equal(5, rows.Count);
            Assert.EndsWith(";5;7;30;;", rows[3]);
            Assert.EndsWith(";6;9;40;;high", rows[4]);
        }

        [Fact]
        public void TimeRangesNeedAnIdentityOrLinearMaster()
        {
            MdfChannelDefinition master = WriterMultiGroupTests.Master();
            master.Conversion = MdfConversionDefinition.Rational(0, 2, 0, 0, 0, 1);
            byte[] source = Source(new MdfWriterOptions(), master, Numeric("x", null));
            MdfCopyResult result;
            Copy(source, null, out result); // Without a range the master and its conversion are copied.
            Assert.Empty(result.Skipped);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(source)))
            {
                Assert.Throws<InvalidOperationException>(() => MdfFileCopier.Copy(reader, new MemoryStream(), new MdfCopyOptions { StartTime = 1 }));
            }
        }

        private static MdfWriterOptions Described(string version = "4.10")
        {
            return new MdfWriterOptions { Version = version, Comment = "drive <1>", Author = "Ann", Organization = "Lab", Project = "P", Subject = "Car \u00e9" };
        }

        private static MdfFileModel Model(byte[] file)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file))) { return reader.ReadFileModel(); }
        }

        [Theory]
        [InlineData("4.10", "4.20")]
        [InlineData("3.30", "4.10")]
        [InlineData("4.10", "3.30")]
        public void HeaderDescriptionAndPropertiesAreCopied(string from, string to)
        {
            byte[] source = Source(Described(from), WriterMultiGroupTests.Master(), Numeric("x", null));
            MdfCopyResult result;
            MdfFileModel model = Model(Copy(source, new MdfCopyOptions { WriterOptions = new MdfWriterOptions { Version = to } }, out result));
            Assert.Empty(result.Skipped);
            Assert.Equal("drive <1>", model.Description);
            Assert.Equal("Ann", model.Author);
            Assert.Equal("Lab", model.Organization);
            Assert.Equal("P", model.Project);
            Assert.Equal("Car \u00e9", model.Subject);
            Assert.Empty(model.FileHistory);
        }

        [Fact]
        public void CallerMetadataOrOptOutWins()
        {
            byte[] source = Source(Described(), WriterMultiGroupTests.Master(), Numeric("x", null));
            MdfCopyResult result;
            MdfFileModel model = Model(Copy(source, new MdfCopyOptions { WriterOptions = new MdfWriterOptions { Project = "mine", ToolIdentifier = "copier" } }, out result));
            Assert.Equal("mine", model.Project);
            Assert.Equal("", model.Author);
            Assert.Equal("", model.Description);
            Assert.Equal("copier", Assert.Single(model.FileHistory).ToolIdentifier);
            model = Model(Copy(source, new MdfCopyOptions { CopyHeaderMetadata = false }, out result));
            Assert.Equal("", model.Comment);
            Assert.Equal("", model.Author);
            Assert.Empty(result.Skipped);
        }

        [Fact]
        public void UnstorableHeaderMetadataIsDroppedAndListed()
        {
            MdfWriterOptions options = Described();
            options.Author = "\u4e2d\u6587"; // Not Latin-1.
            byte[] source = Source(options, WriterMultiGroupTests.Master(), Numeric("x", null));
            MdfCopyResult result;
            byte[] copy = Copy(source, new MdfCopyOptions { WriterOptions = new MdfWriterOptions { Version = "3.30" } }, out result);
            Assert.StartsWith("header metadata: ", Assert.Single(result.Skipped));
            MdfFileModel model = Model(copy);
            Assert.Equal("", model.Author);
            Assert.Equal("", model.Description);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(copy))) { Assert.Equal(5UL, reader.ReadMetadata()[0].ChannelGroups[0].SampleCount); }
        }
    }
}
