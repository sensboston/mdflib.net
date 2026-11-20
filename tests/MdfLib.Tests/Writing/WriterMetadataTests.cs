// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Linq;
using System.Text;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Writing
{
    // Writer header metadata (HDcomment / MDF 3 HD fields) and the MDF 4 file-history entry, read back through MdfFileModel.
    public sealed class WriterMetadataTests
    {
        private const string Tricky = "R&D <test> \"q\" 'a'\r\nline2\tend";
        private const string Unicode = "M\u00fcller \u2713 \U0001D11E";

        private static byte[] Write(MdfWriterOptions options, int rows = 3, bool twoGroups = false)
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(output, true, options))
            {
                writer.AddChannel(WriterMultiGroupTests.Master());
                writer.AddChannel(WriterConfigurationTests.Channel("value", 16));
                if (twoGroups)
                {
                    int second = writer.AddChannelGroup("second");
                    writer.AddChannel(second, WriterConfigurationTests.Channel("other", 8));
                }
                writer.StartMeasurement();
                for (int i = 0; i < rows; i++)
                {
                    writer.SaveSample(new[] { MdfSampleValue.FromFloatingPoint(i), MdfSampleValue.FromUnsigned((ulong)i) });
                    if (twoGroups) { writer.SaveSample(1, new[] { MdfSampleValue.FromUnsigned((ulong)i) }); }
                }
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
            }
            return output.Snapshot();
        }

        private static MdfFileModel Model(byte[] file, out MdfFileHeader header)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file)))
            {
                header = reader.Header;
                MdfFileModel model = reader.ReadFileModel();
                Assert.True(MdfValidator.Validate(reader).IsValid);
                Assert.Equal(3UL, reader.ReadMetadata()[0].ChannelGroups[0].SampleCount);
                return model;
            }
        }

        private static MdfWriterOptions Full(string version)
        {
            return new MdfWriterOptions
            {
                Version = version, StartTimeNanoseconds = 1_700_000_000_123_456_789UL,
                Comment = Tricky, Author = "Ingemar " + Unicode, Organization = "IH Development", Project = "mdflib.net", Subject = "PXY",
                ToolIdentifier = "Unit Test", ToolVendor = "ACME & Co", ToolVersion = "2.3", UserName = "Ducky", HistoryComment = "Initial <stuff>"
            };
        }

        [Theory]
        [InlineData("4.10", false, false)]
        [InlineData("4.20", false, false)]
        [InlineData("4.10", true, false)]
        [InlineData("4.10", false, true)]
        public void Mdf4HeaderPropertiesAndFileHistoryRoundTrip(string version, bool compress, bool twoGroups)
        {
            MdfWriterOptions options = Full(version);
            options.CompressData = compress;
            MdfFileHeader header;
            MdfFileModel model = Model(Write(options, twoGroups: twoGroups), out header);
            Assert.Equal(Tricky, model.Description);
            Assert.Equal("Ingemar " + Unicode, model.Author);
            Assert.Equal("IH Development", model.Organization);
            Assert.Equal("mdflib.net", model.Project);
            Assert.Equal("PXY", model.Subject);
            Assert.StartsWith("<HDcomment><TX>R&amp;D &lt;test&gt;", model.Comment);
            MdfFileHistory history = Assert.Single(model.FileHistory);
            Assert.Equal("Unit Test", history.ToolIdentifier);
            Assert.Equal("ACME & Co", history.ToolVendor);
            Assert.Equal("2.3", history.ToolVersion);
            Assert.Equal("Ducky", history.UserName);
            Assert.Equal("Initial <stuff>", history.Text);
            Assert.Equal(1_700_000_000_123_456_789UL, history.RawTimeNanoseconds);
            Assert.Equal((byte)2, history.TimeFlags);
            Assert.Equal(0, history.UtcOffsetMinutes);

        }

        [Fact]
        public void HistoryAloneDefaultsToolIdentifierAndWritesNoHeaderComment()
        {
            MdfFileHeader header;
            MdfFileModel model = Model(Write(new MdfWriterOptions { ToolVendor = "V" }), out header);
            Assert.Equal("", model.Comment);
            Assert.Equal("", model.Description);
            Assert.Equal("", model.Author);
            MdfFileHistory history = Assert.Single(model.FileHistory);
            Assert.Equal("MdfLib", history.ToolIdentifier);
            Assert.Equal("V", history.ToolVendor);
            Assert.Equal("", history.ToolVersion);
            Assert.Equal("", history.Text);
        }

        [Fact]
        public void PropertiesWithoutDescriptionAndDescriptionWithoutProperties()
        {
            MdfFileHeader header;
            MdfFileModel model = Model(Write(new MdfWriterOptions { Subject = "only" }), out header);
            Assert.Equal("only", model.Subject);
            Assert.Equal("", model.Description);
            Assert.Empty(model.FileHistory);
            model = Model(Write(new MdfWriterOptions { Comment = "" }), out header);
            Assert.Equal("<HDcomment><TX></TX></HDcomment>", model.Comment);
            Assert.Equal("", model.Subject);
        }

        [Theory]
        [InlineData("4.10")]
        [InlineData("4.20")]
        [InlineData("3.30")]
        public void DefaultOptionsKeepTheOutputUnchanged(string version)
        {
            byte[] plain = Write(new MdfWriterOptions { Version = version });
            MdfWriterOptions history = new MdfWriterOptions { Version = version, ToolIdentifier = version == "3.30" ? "ignored" : null };
            Assert.Equal(plain, Write(history));
            MdfFileHeader header;
            MdfFileModel model = Model(plain, out header);
            Assert.Empty(model.FileHistory);
            Assert.Equal("", model.Description);
            Assert.Equal("", model.Author);
        }

        [Fact]
        public void Mdf3HeaderFieldsAndCommentRoundTrip()
        {
            MdfWriterOptions options = new MdfWriterOptions
            {
                Version = "3.30", Comment = "Line1\r\nLine2 \u00e9", Author = "Caller", Organization = "Home Alone", Project = "Mdf3WriteHD",
                Subject = new string('s', 31), ToolVendor = "not stored"
            };
            MdfFileHeader header;
            MdfFileModel model = Model(Write(options), out header);
            Assert.Equal("Caller", header.Author);
            Assert.Equal("Home Alone", header.Organization);
            Assert.Equal("Mdf3WriteHD", header.Project);
            Assert.Equal(new string('s', 31), header.Subject);
            Assert.Equal("Caller", model.Author);
            Assert.Equal(new string('s', 31), model.Subject);
            Assert.Equal("Line1\r\nLine2 \u00e9", model.Comment);
            Assert.Equal(model.Comment, model.Description);
            Assert.Empty(model.FileHistory);
        }

        [Theory]
        [InlineData("3.30", "Author", "0123456789012345678901234567890X")] // 32 bytes: no room for the terminator.
        [InlineData("3.30", "Project", "\u20ac")] // Not Latin-1.
        [InlineData("3.30", "Comment", "\u0394")]
        [InlineData("4.10", "Comment", "a\0b")]
        [InlineData("4.10", "Author", "bell\u0007")]
        [InlineData("4.10", "UserName", "\uFFFE")]
        [InlineData("4.20", "ToolVersion", "x#DC00")] // Lone surrogates: attribute strings cannot carry them, see Decode.
        [InlineData("4.20", "HistoryComment", "#D800")]
        [InlineData("4.10", "Subject", "#DFFF#D800")]
        [InlineData("3.30", "ToolIdentifier", "\u0001")] // Validated even though MDF 3 does not store it.
        public void InvalidMetadataIsRefusedBeforeAnyByteIsWritten(string version, string property, string value)
        {
            MdfWriterOptions options = new MdfWriterOptions { Version = version };
            typeof(MdfWriterOptions).GetProperty(property).SetValue(options, Decode(value));
            TestWriteStream output = new TestWriteStream();
            ArgumentException error = Assert.Throws<ArgumentException>(() => MdfWriter.Create(output, true, options));
            Assert.Equal(property, error.ParamName);
            Assert.Equal(0, output.Writes);
        }

        private static string Decode(string value)
        {
            StringBuilder result = new StringBuilder();
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] == '#') { result.Append((char)Convert.ToInt32(value.Substring(i + 1, 4), 16)); i += 4; }
                else { result.Append(value[i]); }
            }
            return result.ToString();
        }

        [Fact]
        public void PairedSurrogatesAreAccepted()
        {
            MdfFileHeader header;
            Assert.Equal("\U0001F600", Model(Write(new MdfWriterOptions { UserName = Decode("#D83D#DE00") }), out header).FileHistory[0].UserName);
        }

        [Fact]
        public void MetadataRespectsTheTextLimit()
        {
            MdfWriterOptions options = new MdfWriterOptions { MaximumTextBytes = 64, Comment = new string('c', 60) };
            Assert.Equal(nameof(MdfWriterOptions.MaximumTextBytes),
                Assert.Throws<MdfResourceLimitException>(() => MdfWriter.Create(new TestWriteStream(), true, options)).LimitName);
            options = new MdfWriterOptions { MaximumTextBytes = 128, HistoryComment = new string('h', 100) };
            Assert.Throws<MdfResourceLimitException>(() => MdfWriter.Create(new TestWriteStream(), true, options));
            options = new MdfWriterOptions { Version = "3.30", Comment = new string('m', 70000), MaximumTextBytes = 100000 };
            Assert.Throws<NotSupportedException>(() => MdfWriter.Create(new TestWriteStream(), true, options));
        }

        [Fact]
        public void AppendIgnoresHeaderMetadataAndKeepsTheOriginal()
        {
            byte[] original = Write(Full("4.10"));
            MemoryStream file = new MemoryStream();
            file.Write(original, 0, original.Length);
            MdfWriterOptions other = new MdfWriterOptions { Comment = "new", Author = "someone else", ToolIdentifier = "other" };
            using (MdfWriter writer = MdfWriter.Append(file, true, other))
            {
                writer.AddChannel(WriterMultiGroupTests.Master());
                writer.StartMeasurement();
                writer.SaveSample(new[] { MdfSampleValue.FromFloatingPoint(1) });
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file.ToArray())))
            {
                MdfFileModel model = reader.ReadFileModel();
                Assert.Equal(Tricky, model.Description);
                Assert.Equal("Unit Test", Assert.Single(model.FileHistory).ToolIdentifier);
                Assert.Equal(2, reader.ReadMetadata().Count);
            }
        }

        // Replaces the written HDcomment payload (same block, zero-padded) to exercise the reader on other producers' XML.
        private static MdfFileModel Patched(string xml)
        {
            byte[] file = Write(new MdfWriterOptions { Comment = new string('x', 1000) });
            byte[] marker = Encoding.ASCII.GetBytes("<HDcomment>");
            int at = Enumerable.Range(0, file.Length - marker.Length).First(i => file.Skip(i).Take(marker.Length).SequenceEqual(marker));
            int end = Array.IndexOf(file, (byte)0, at);
            byte[] replacement = Encoding.UTF8.GetBytes(xml);
            Assert.True(replacement.Length < end - at);
            Array.Clear(file, at, end - at);
            Array.Copy(replacement, 0, file, at, replacement.Length);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file))) { return reader.ReadFileModel(); }
        }

        [Fact]
        public void ReaderTakesFirstDirectPropertiesAndIgnoresNestedOrForeignMarkup()
        {
            MdfFileModel model = Patched("<HDcomment xmlns=\"http://www.asam.net/mdf/v4\"><TX>desc <![CDATA[a<b]]></TX><TX>second</TX>"
                + "<time_source>local</time_source><common_properties><e name=\"author\">first</e><e name=\"author\">dup</e>"
                + "<tree name=\"t\"><e name=\"project\">nested</e></tree><e name=\"subject\"/><e name=\"department\" type=\"string\">dep</e>"
                + "<e>nameless</e></common_properties><common_properties><e name=\"project\">late</e></common_properties></HDcomment>");
            Assert.Equal("desc a<b", model.Description);
            Assert.Equal("first", model.Author);
            Assert.Equal("dep", model.Organization);
            Assert.Equal("late", model.Project);
            Assert.Equal("", model.Subject);

            model = Patched("<FHcomment><TX>wrong root</TX></FHcomment>");
            Assert.Equal("", model.Description);
            Assert.Equal("", model.Author);
        }

        [Fact]
        public void ReaderRejectsMalformedHeaderXml()
        {
            Assert.Throws<MdfFormatException>(() => Patched("<HDcomment><TX>open</HDcomment>"));
        }
    }
}
