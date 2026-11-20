// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Threading;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Reading
{
    public sealed class CsvExportTests
    {
        private static byte[] Sample(string version = "4.10")
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(output, true, new MdfWriterOptions { Version = version }))
            {
                writer.AddChannel(new MdfChannelDefinition { Name = "t", Unit = "s", BitCount = 64, ValueKind = MdfValueKind.FloatingPoint, IsMaster = true });
                writer.AddChannel(new MdfChannelDefinition { Name = "temp", Unit = "degC", BitCount = 8, ValueKind = MdfValueKind.SignedInteger, HasLinearConversion = true, Factor = 0.5, Offset = 40 });
                writer.AddChannel(new MdfChannelDefinition { Name = "label", BitCount = 64, ValueKind = MdfValueKind.Text });
                writer.AddChannel(new MdfChannelDefinition { Name = "raw", BitCount = 16, ValueKind = MdfValueKind.ByteArray });
                if (version != "3.30") { writer.AddChannel(new MdfChannelDefinition { Name = "z", BitCount = 128, ValueKind = MdfValueKind.Complex }); }
                writer.StartMeasurement();
                string[] labels = { "plain", "a,b", "q\"t", " pad" };
                for (int row = 0; row < 4; row++)
                {
                    MdfSampleValue[] values =
                    {
                        MdfSampleValue.FromFloatingPoint(row * 0.25),
                        MdfSampleValue.FromSigned(row * 3 - 4, version == "3.30" || row != 2),
                        MdfSampleValue.FromText(labels[row]),
                        MdfSampleValue.FromByteArray(new[] { (byte)(row * 16 + 10), (byte)row }),
                        MdfSampleValue.FromComplex(row - 1.5, row == 0 ? -0.0 : row * -1.0),
                    };
                    if (version == "3.30") { Array.Resize(ref values, 4); }
                    writer.SaveSample(values);
                }
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
            }
            return output.Snapshot();
        }

        private static string Export(byte[] file, MdfCsvOptions options = null, int[] channels = null)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file)))
            using (StringWriter text = new StringWriter())
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                ulong rows = MdfCsvExporter.Export(reader, group, text, channels, options);
                Assert.Equal(4UL, rows);
                return text.ToString();
            }
        }

        [Fact]
        public void PhysicalExportQuotesFieldsFormatsInvariantNumbersAndBlanksInvalidSamples()
        {
            string expected =
                "t [s],temp [degC],label,raw,z\r\n" +
                "0,38,plain,0A00,-1.5-0i\r\n" +
                "0.25,39.5,\"a,b\",1A01,-0.5-1i\r\n" +
                "0.5,,\"q\"\"t\",2A02,0.5-2i\r\n" +
                "0.75,42.5,\" pad\",3A03,1.5-3i\r\n";
            Assert.Equal(expected, Export(Sample()));
        }

        [Fact]
        public void RawSelectionDelimiterNewLineHeaderAndInvalidPolicyAreHonoured()
        {
            MdfCsvOptions options = new MdfCsvOptions
            {
                Representation = MdfValueRepresentation.Raw, Delimiter = ';', NewLine = "\n",
                IncludeUnits = false, EmptyForInvalid = false,
            };
            Assert.Equal("temp;t\n-4;0\n-1;0.25\n2;0.5\n5;0.75\n", Export(Sample("4.20"), options, new[] { 1, 0 }));
            options.IncludeHeader = false;
            options.Delimiter = ',';
            Assert.Equal("\"a,b\"\n", Export(Sample("4.20"), options, new[] { 2 }).Split('\n')[1] + "\n");
        }

        [Fact]
        public void Mdf3ExportUsesTheSameFormatting()
        {
            string expected =
                "t [s],temp [degC],label,raw\r\n" +
                "0,38,plain,0A00\r\n" +
                "0.25,39.5,\"a,b\",1A01\r\n" +
                "0.5,41,\"q\"\"t\",2A02\r\n" +
                "0.75,42.5,\" pad\",3A03\r\n";
            Assert.Equal(expected, Export(Sample("3.30")));
        }

        [Fact]
        public void FormatCoversEveryValueKindAndSpecialFloatingPointValues()
        {
            Assert.Equal("18446744073709551615", MdfCsvExporter.Format(MdfSampleValue.FromUnsigned(ulong.MaxValue)));
            Assert.Equal("-9223372036854775808", MdfCsvExporter.Format(MdfSampleValue.FromSigned(long.MinValue)));
            Assert.Equal("NaN", MdfCsvExporter.Format(MdfSampleValue.FromFloatingPoint(double.NaN)));
            Assert.Equal("Infinity", MdfCsvExporter.Format(MdfSampleValue.FromFloatingPoint(double.PositiveInfinity)));
            Assert.Equal("-Infinity", MdfCsvExporter.Format(MdfSampleValue.FromFloatingPoint(double.NegativeInfinity)));
            Assert.Equal("1E-300", MdfCsvExporter.Format(MdfSampleValue.FromFloatingPoint(1e-300)));
            Assert.Equal("2+0.5i", MdfCsvExporter.Format(MdfSampleValue.FromComplex(2, 0.5)));
            Assert.Equal("NaN+Infinityi", MdfCsvExporter.Format(MdfSampleValue.FromComplex(double.NaN, double.PositiveInfinity)));
            Assert.Equal("0-Infinityi", MdfCsvExporter.Format(MdfSampleValue.FromComplex(0, double.NegativeInfinity)));
            Assert.Equal("", MdfCsvExporter.Format(MdfSampleValue.FromByteArray(new byte[0])));
            Assert.Equal("FF00", MdfCsvExporter.Format(MdfSampleValue.FromByteArray(new byte[] { 255, 0 })));
            Assert.Equal("x\ny", MdfCsvExporter.Format(MdfSampleValue.FromText("x\ny")));
        }

        [Fact]
        public void ArgumentsAndOptionsAreValidatedBeforeAnyOutput()
        {
            byte[] file = Sample();
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file)))
            using (StringWriter text = new StringWriter())
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Throws<ArgumentNullException>(() => MdfCsvExporter.Export(null, group, text));
                Assert.Throws<ArgumentNullException>(() => MdfCsvExporter.Export(reader, null, text));
                Assert.Throws<ArgumentNullException>(() => MdfCsvExporter.Export(reader, group, null));
                Assert.Throws<ArgumentException>(() => MdfCsvExporter.Export(reader, group, text, null, new MdfCsvOptions { Delimiter = '"' }));
                Assert.Throws<ArgumentException>(() => MdfCsvExporter.Export(reader, group, text, null, new MdfCsvOptions { Delimiter = '\n' }));
                Assert.Throws<ArgumentException>(() => MdfCsvExporter.Export(reader, group, text, null, new MdfCsvOptions { NewLine = "\r" }));
                Assert.Throws<ArgumentException>(() => MdfCsvExporter.Export(reader, group, text, null, new MdfCsvOptions { NewLine = null }));
                Assert.Throws<ArgumentException>(() => MdfCsvExporter.Export(reader, group, text, null, new MdfCsvOptions { Representation = (MdfValueRepresentation)7 }));
                Assert.Throws<ArgumentOutOfRangeException>(() => MdfCsvExporter.Export(reader, group, text, null, new MdfCsvOptions { BatchSize = 0 }));
                Assert.Throws<ArgumentException>(() => MdfCsvExporter.Export(reader, group, text, new int[0]));
                Assert.ThrowsAny<ArgumentException>(() => MdfCsvExporter.Export(reader, group, text, new[] { 99 }));
                Assert.ThrowsAny<ArgumentException>(() => MdfCsvExporter.Export(reader, group, text, new[] { 0, 0 }));
                using (CancellationTokenSource cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel();
                    Assert.ThrowsAny<OperationCanceledException>(() => MdfCsvExporter.Export(reader, group, text, null, null, cancelled.Token));
                }
                Assert.Equal(string.Empty, text.ToString());
                Assert.Equal(4UL, MdfCsvExporter.Export(reader, group, text, new[] { 0 }, new MdfCsvOptions { BatchSize = 1 }));
                Assert.Equal("t [s]\r\n0\r\n0.25\r\n0.5\r\n0.75\r\n", text.ToString());
            }
        }
    }
}
