// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Linq;
using System.Threading;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Writing
{
    public sealed class WriterConfigurationTests
    {
        internal static MdfChannelDefinition Channel(string name = "x", int bits = 8, MdfValueKind kind = MdfValueKind.UnsignedInteger)
        { return new MdfChannelDefinition { Name = name, BitCount = bits, ValueKind = kind }; }
        internal static MdfWriter Started(TestWriteStream stream, string version = "4.10")
        {
            MdfWriter writer = MdfWriter.Create(stream, true, new MdfWriterOptions { Version = version });
            writer.AddChannel(Channel()); writer.StartMeasurement(); return writer;
        }

        [Fact]
        public void DefaultsAreFiniteAndExactKindsHavePublicFactories()
        {
            MdfWriterOptions options = new MdfWriterOptions();
            Assert.Equal("4.10", options.Version);
            Assert.Equal(0UL, options.StartTimeNanoseconds);
            Assert.Equal(16777216, options.MaximumMetadataBytes);
            Assert.Equal(1048576, options.MaximumRecordBytes);
            Assert.Equal(4096, options.MaximumChannelCount);
            Assert.Equal(1048576, options.MaximumTextBytes);
            MdfChannelDefinition channel = new MdfChannelDefinition();
            Assert.Equal(string.Empty, channel.Name); Assert.Equal(string.Empty, channel.Unit);
            Assert.Equal(64, channel.BitCount); Assert.Equal(MdfValueKind.UnsignedInteger, channel.ValueKind);
            Assert.False(channel.IsBigEndian); Assert.False(channel.IsMaster); Assert.False(channel.HasLinearConversion);
            Assert.Equal(1, channel.Factor); Assert.Equal(0, channel.Offset);
            Assert.Equal(ulong.MaxValue, MdfSampleValue.FromUnsigned(ulong.MaxValue).UnsignedValue);
            Assert.Equal(long.MinValue, MdfSampleValue.FromSigned(long.MinValue, false).SignedValue);
            Assert.False(MdfSampleValue.FromSigned(long.MinValue, false).IsValid);
            Assert.True(double.IsNaN(MdfSampleValue.FromFloatingPoint(double.NaN).FloatingPointValue));
            Assert.False(default(MdfSampleValue).IsValid);
        }

        [Theory]
        [InlineData("3.20")]
        [InlineData("4.11")]
        [InlineData("4.21")]
        [InlineData(null)]
        public void UnsupportedRevisionsNeverTakeOwnership(string version)
        {
            TestWriteStream stream = new TestWriteStream();
            Assert.Throws<NotSupportedException>(() => MdfWriter.Create(stream, options: new MdfWriterOptions { Version = version }));
            Assert.False(stream.Disposed); Assert.Equal(0, stream.Writes);
        }

        [Theory]
        [InlineData("metadata")]
        [InlineData("metadata-big")]
        [InlineData("record")]
        [InlineData("channels")]
        [InlineData("text")]
        public void InvalidOptionsAreRejectedBeforeOwnership(string setting)
        {
            MdfWriterOptions options = new MdfWriterOptions();
            if (setting == "metadata") { options.MaximumMetadataBytes = 0; }
            if (setting == "metadata-big") { options.MaximumMetadataBytes = (long)int.MaxValue + 1; }
            if (setting == "record") { options.MaximumRecordBytes = -1; }
            if (setting == "channels") { options.MaximumChannelCount = 0; }
            if (setting == "text") { options.MaximumTextBytes = 0; }
            TestWriteStream stream = new TestWriteStream();
            Assert.Throws<ArgumentOutOfRangeException>(() => MdfWriter.Create(stream, options: options));
            Assert.False(stream.Disposed); Assert.Equal(0, stream.Seeks);
        }

        [Theory]
        [InlineData("null")]
        [InlineData("write")]
        [InlineData("seek")]
        [InlineData("nonempty")]
        public void CapabilitiesAndEmptySourceAreRequiredWithoutTruncation(string reason)
        {
            TestWriteStream stream = new TestWriteStream();
            if (reason == "write") { stream.Writable = false; }
            if (reason == "seek") { stream.Seekable = false; }
            if (reason == "nonempty") { stream.ChangeLength(3); }
            Assert.ThrowsAny<ArgumentException>(() => MdfWriter.Create(reason == "null" ? null : (Stream)stream));
            Assert.False(stream.Disposed); Assert.Equal(0, stream.Writes); Assert.Equal(0, stream.SetLengths);
        }

        [Fact]
        public void OptionsAndDefinitionsAreSnapshotsAndDuplicateNamesAreLegal()
        {
            TestWriteStream stream = new TestWriteStream();
            MdfWriterOptions options = new MdfWriterOptions { Version = "3.30", StartTimeNanoseconds = 9007199254740993 };
            using (MdfWriter writer = MdfWriter.Create(stream, true, options))
            {
                options.Version = "4.20"; options.MaximumChannelCount = 1; options.StartTimeNanoseconds = 0;
                MdfChannelDefinition channel = Channel("duplicate", 64);
                writer.AddChannel(channel); channel.BitCount = 1; channel.Name = "changed";
                Assert.Equal(1, writer.AddChannel(Channel("duplicate", 64)));
                Assert.Equal(16, writer.RecordByteCount);
                writer.StartMeasurement();
                writer.SaveSample(new[] { MdfSampleValue.FromUnsigned(ulong.MaxValue), MdfSampleValue.FromUnsigned(9007199254740993) });
                writer.StopMeasurement(); writer.FinalizeMeasurement();
                Assert.Equal("3.30", writer.Version);
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(stream.Snapshot())))
            {
                Assert.Equal(9007199254740993UL, reader.Header.RawStartTimeNanoseconds);
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(new[] { "duplicate", "duplicate" }, group.Channels.Select(c => c.Name));
                Assert.All(group.Channels, c => Assert.Equal(64U, c.BitCount));
                Assert.Equal(ulong.MaxValue, reader.ReadSamples(group, new[] { 0 }).Single().GetValue(0, 0).UnsignedValue);
            }
        }

        [Theory]
        [InlineData("null")]
        [InlineData("empty-name")]
        [InlineData("null-name")]
        [InlineData("null-unit")]
        [InlineData("nul")]
        [InlineData("surrogate")]
        [InlineData("kind")]
        [InlineData("zero-bits")]
        [InlineData("wide-bits")]
        [InlineData("float-bits")]
        [InlineData("factor")]
        [InlineData("offset")]
        [InlineData("implicit-linear")]
        public void InvalidDefinitionsAreAtomicAndDoNotWrite(string reason)
        {
            TestWriteStream stream = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(stream, true))
            {
                MdfChannelDefinition channel = Channel();
                if (reason == "empty-name") { channel.Name = string.Empty; }
                if (reason == "null-name") { channel.Name = null; }
                if (reason == "null-unit") { channel.Unit = null; }
                if (reason == "nul") { channel.Name = "x\0y"; }
                if (reason == "surrogate") { channel.Name = "\ud800"; }
                if (reason == "kind") { channel.ValueKind = MdfValueKind.Unsupported; }
                if (reason == "zero-bits") { channel.BitCount = 0; }
                if (reason == "wide-bits") { channel.BitCount = 65; }
                if (reason == "float-bits") { channel.ValueKind = MdfValueKind.FloatingPoint; channel.BitCount = 13; }
                if (reason == "factor") { channel.Factor = double.NaN; }
                if (reason == "offset") { channel.Offset = double.PositiveInfinity; }
                if (reason == "implicit-linear") { channel.Factor = 2; }
                Exception error = Record.Exception(() => writer.AddChannel(reason == "null" ? null : channel));
                Assert.NotNull(error); Assert.True(error is ArgumentException || error is NotSupportedException, error.ToString());
                Assert.Equal(0, writer.ChannelCount); Assert.Equal(0, writer.RecordByteCount); Assert.Equal(0, stream.Writes);
                writer.AddChannel(Channel()); Assert.Equal(1, writer.ChannelCount);
            }
        }

        [Theory]
        [InlineData("unicode")]
        [InlineData("unit")]
        [InlineData("trailing-name")]
        [InlineData("trailing-unit")]
        [InlineData("long-text")]
        public void Mdf3NeverSubstitutesOrSilentlyTruncatesText(string reason)
        {
            using (MdfWriter writer = MdfWriter.Create(new TestWriteStream(), options: new MdfWriterOptions { Version = "3.30" }))
            {
                MdfChannelDefinition channel = Channel();
                if (reason == "unicode") { channel.Name = "温度"; }
                if (reason == "unit") { channel.Unit = new string('u', 20); }
                if (reason == "trailing-name") { channel.Name = "x "; }
                if (reason == "trailing-unit") { channel.Unit = "u "; }
                if (reason == "long-text") { channel.Name = new string('x', 65531); }
                if (reason == "unicode") { Assert.Throws<ArgumentException>(() => writer.AddChannel(channel)); }
                else { Assert.Throws<NotSupportedException>(() => writer.AddChannel(channel)); }
                Assert.Equal(0, writer.ChannelCount);
            }
        }

        [Fact]
        public void MasterAndChannelRecordTextMetadataBudgetsApplyBeforeOutput()
        {
            TestWriteStream stream = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(stream, true, new MdfWriterOptions { MaximumChannelCount = 2, MaximumTextBytes = 3 }))
            {
                MdfChannelDefinition master = Channel(); master.IsMaster = true;
                writer.AddChannel(master);
                Assert.Throws<ArgumentException>(() => writer.AddChannel(master));
                MdfResourceLimitException text = Assert.Throws<MdfResourceLimitException>(() => writer.AddChannel(Channel("long")));
                Assert.Equal("MaximumTextBytes", text.LimitName);
                writer.AddChannel(Channel("yy"));
                Assert.Equal("MaximumChannelCount", Assert.Throws<MdfResourceLimitException>(() => writer.AddChannel(Channel())).LimitName);
                Assert.Equal(0, stream.Writes);
            }
            using (MdfWriter writer = MdfWriter.Create(new TestWriteStream(), options: new MdfWriterOptions { MaximumRecordBytes = 1 }))
            { Assert.Equal("MaximumRecordBytes", Assert.Throws<MdfResourceLimitException>(() => writer.AddChannel(Channel())).LimitName); Assert.Equal(0, writer.ChannelCount); }
        }

        [Theory]
        [InlineData(2912, false)]
        [InlineData(2913, true)]
        public void DescriptorPlusPrefixAccountingHasAnExactDocumentedBoundary(int limit, bool fits)
        {
            TestWriteStream stream = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(stream, true, new MdfWriterOptions { MaximumMetadataBytes = limit }))
            {
                writer.AddChannel(Channel());
                if (fits) { writer.StartMeasurement(); Assert.Equal(1, stream.Writes); }
                else
                {
                    MdfResourceLimitException error = Assert.Throws<MdfResourceLimitException>(() => writer.StartMeasurement());
                    Assert.Equal("MaximumMetadataBytes", error.LimitName); Assert.Equal(0, stream.Writes); Assert.Equal(MdfWriterState.Configuring, writer.State);
                }
            }
        }

        [Fact]
        public void TooSmallInitialAllowanceDoesNotTakeOwnershipAndCancellationPrecedesFileCreation()
        {
            TestWriteStream stream = new TestWriteStream();
            Assert.Throws<MdfResourceLimitException>(() => MdfWriter.Create(stream, options: new MdfWriterOptions { MaximumMetadataBytes = 1023 }));
            Assert.False(stream.Disposed);
            CancellationToken token = new CancellationToken(true);
            Assert.Throws<OperationCanceledException>(() => MdfWriter.Create(stream, cancellationToken: token));
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mf4");
            Assert.Throws<OperationCanceledException>(() => MdfWriter.Create(path, cancellationToken: token));
            Assert.False(File.Exists(path)); Assert.False(stream.Disposed);
        }

        [Fact]
        public void FileFactoryCreatesNewAndCannotOverwriteExistingMeasurement()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mf4");
            try
            {
                using (MdfWriter writer = MdfWriter.Create(path))
                { writer.AddChannel(Channel()); writer.StartMeasurement(); writer.StopMeasurement(); writer.FinalizeMeasurement(); }
                byte[] before = File.ReadAllBytes(path);
                Assert.Throws<IOException>(() => MdfWriter.Create(path));
                Assert.Equal(before, File.ReadAllBytes(path));
                using (MdfReader reader = MdfReader.Open(File.OpenRead(path))) { Assert.Equal(0UL, reader.ReadMetadata()[0].ChannelGroups[0].SampleCount); }
            }
            finally { if (File.Exists(path)) { File.Delete(path); } }
        }
    }
}
