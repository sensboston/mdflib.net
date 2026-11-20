// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// TX/MD comments and source information blocks written for a DG/CG/CN/SI, read back through MdfReader.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Writing
{
    public sealed class WriterBlockMetadataTests
    {
        private const string Comment = "R&D <speed> \"max\"\r\nsecond line";
        private const string Escaped = "R&amp;D &lt;speed&gt; &quot;max&quot;&#13;\nsecond line";

        // An MDF 4 comment is the MD block the writer stored, reported verbatim: the model properties of the engine say
        // so in their own summaries, and MdfLib.CompatibilityTests pins that reading of foreign files ("<ATcomment>…"
        // and "<DGcomment>…" stay documents rather than being guessed into prose). So these round trips check the text
        // inside the document, which is what the wrapper's property maps; MDF 3.30, that has no such block and keeps
        // the comment in a fixed field, is the revision whose model reports the text itself.

        private static MdfSourceDefinition Source(string name, string path, string comment, byte busType, bool sync)
        {
            return new MdfSourceDefinition { Name = name, Path = path, Comment = comment, BusType = busType, SourceType = 2, IsSyncBus = sync };
        }

        private static byte[] WriteChannelMetadata(MdfWriterOptions options, MdfChannelDefinition channel, string groupComment = null, MdfSourceDefinition groupSource = null)
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(output, true, options))
            {
                writer.AddChannel(WriterMultiGroupTests.Master());
                writer.AddChannel(channel);
                if (groupComment != null || groupSource != null)
                {
                    writer.SetChannelGroupMetadata(0, new MdfChannelGroupDefinition { Comment = groupComment, Source = groupSource });
                }
                writer.StartMeasurement();
                writer.SaveSample(new[] { MdfSampleValue.FromFloatingPoint(0.5), MdfSampleValue.FromUnsigned(7) });
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
            }
            return output.Snapshot();
        }

        private static List<long> Blocks(byte[] file, string type)
        {
            byte[] marker = Encoding.ASCII.GetBytes("##" + type);
            List<long> result = new List<long>();
            for (int i = 0; i + marker.Length <= file.Length; i++)
            {
                bool match = true;
                for (int k = 0; k < marker.Length && match; k++) { match = file[i + k] == marker[k]; }
                if (match) { result.Add(i); }
            }
            return result;
        }

        private static ulong U64(byte[] bytes, int at)
        {
            ulong result = 0;
            for (int i = 7; i >= 0; i--) { result = (result << 8) | bytes[at + i]; }
            return result;
        }

        private static MdfChannelGroup Read(byte[] file)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file)))
            {
                Assert.True(MdfValidator.Validate(reader).IsValid);
                return reader.ReadMetadata().SelectMany(g => g.ChannelGroups).Single();
            }
        }

        [Theory]
        [InlineData("4.10")]
        [InlineData("4.20")]
        public void ChannelCommentAndSourceAreWrittenAndReadBack(string version)
        {
            MdfChannelDefinition channel = WriterConfigurationTests.Channel("speed", 16);
            channel.Comment = Comment;
            channel.Source = Source("ECU12", "CCF:/a/b.mdf", "brake ECU", 1, true);
            channel.Source.LongIdentifier = "wheel speed, front left";
            byte[] file = WriteChannelMetadata(new MdfWriterOptions { Version = version }, channel);
            MdfChannelGroup group = Read(file);
            MdfChannel value = group.Channels.Single(c => c.Name == "speed");
            Assert.Equal("<CNcomment><TX>" + Escaped + "</TX></CNcomment>", value.Comment);
            Assert.Equal("ECU12", value.Source.Name);
            Assert.Equal("CCF:/a/b.mdf", value.Source.Path);
            Assert.Equal("<SIcomment><TX>brake ECU</TX></SIcomment>", value.Source.Comment);
            Assert.Equal((byte)2, value.Source.SourceType);
            Assert.Equal((byte)1, value.Source.BusType);
            Assert.Equal((byte)(1 | 2 | 4 | 8), value.Source.Flags);
            // The stored MD payload is the schema's XML element with the same escaping as a header comment, and the
            // reader hands the TX of that document back as the comment (asserted above) rather than the markup.
            Assert.Contains("<TX>" + Escaped + "</TX>", Encoding.UTF8.GetString(file, 0, file.Length), StringComparison.Ordinal);
            Assert.Single(Blocks(file, "SI"));
            // The channel comment and the SI comment are MD blocks; the long identifier is a field of the CN block
            // and the channel and group names are TX blocks, so they add no blocks of their own here.
            Assert.Equal(2, Blocks(file, "MD").Count);
            Assert.Equal(5, Blocks(file, "TX").Count); // The two channel names, the master's unit, and the SI name and path.
            // The group has two channels, so the block under test is the CN that links the source: the master's block
            // has nothing there, and only the described channel has a comment and a long identifier.
            long si = Blocks(file, "SI")[0];
            long cn = Blocks(file, "CN").Single(block => U64(file, (int)block + 48) == (ulong)si);
            long master = Blocks(file, "CN").Single(block => block != cn);
            Assert.Equal(0UL, U64(file, (int)master + 48)); // cn_source of the master is NIL.
            Assert.All(Blocks(file, "CN"), block => Assert.Equal(160UL, U64(file, (int)block + 8))); // Both hold eight links.
            Assert.Equal("wheel speed, front left", Encoding.ASCII.GetString(file, (int)cn + 134, 23).TrimEnd('\0')); // cn_long_identifier.
        }

        [Fact]
        public void GroupMetadataWritesACommentAndReplacesTheSourceOfTheGroup()
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(output, true, new MdfWriterOptions()))
            {
                writer.AddChannel(WriterMultiGroupTests.Master());
                int second = writer.AddChannelGroup(new MdfChannelGroupDefinition
                {
                    Name = "slow",
                    Comment = Comment,
                    Source = Source("device B", "\\\\share\\file.mf4", "bench", 5, false)
                });
                Assert.Equal(1, second);
                writer.AddChannel(second, WriterConfigurationTests.Channel("x", 8));
                writer.SetChannelGroupMetadata(0, new MdfChannelGroupDefinition { Comment = "group zero", Source = Source("device A", "C:/x.mf4", null, 1, false) });
                writer.StartMeasurement();
                writer.SaveSample(new[] { MdfSampleValue.FromFloatingPoint(1.0) });
                writer.SaveSample(second, new[] { MdfSampleValue.FromUnsigned(2) });
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
            }
            byte[] file = output.Snapshot();
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file)))
            {
                Assert.True(MdfValidator.Validate(reader).IsValid);
                IReadOnlyList<MdfChannelGroup> groups = reader.ReadMetadata().SelectMany(d => d.ChannelGroups).ToList();
                Assert.Equal(2, groups.Count);
                Assert.Equal("<CGcomment><TX>group zero</TX></CGcomment>", groups[0].Comment);
                Assert.Equal("device A", groups[0].Source.Name);
                Assert.Equal("C:/x.mf4", groups[0].Source.Path);
                Assert.Equal((byte)(1 | 2), groups[0].Source.Flags);
                Assert.Equal('/', groups[0].PathSeparator);
                Assert.Equal("slow", groups[1].Name);
                Assert.Equal("<CGcomment><TX>" + Escaped + "</TX></CGcomment>", groups[1].Comment);
                Assert.Equal("<SIcomment><TX>bench</TX></SIcomment>", groups[1].Source.Comment);
                Assert.Equal((byte)5, groups[1].Source.BusType);
                Assert.Equal('\\', groups[1].PathSeparator);
            }
            // Three group-level comments are stored as MD blocks: one per group plus the SI comment of group 1,
            // whose source had one; a null source comment stores nothing, and SI names and paths are TX blocks.
            Assert.Equal(2, Blocks(file, "SI").Count);
            Assert.Equal(3, Blocks(file, "MD").Count);
        }

        [Fact]
        public void AnEmptyCommentRemovesTheStoredBlockAndANameStillWorks()
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(output, true, new MdfWriterOptions()))
            {
                writer.AddChannel(WriterMultiGroupTests.Master());
                int group = writer.AddChannelGroup("fast");
                writer.AddChannel(group, WriterConfigurationTests.Channel("speed", 16)); // A group needs a channel to be written at all.
                writer.SetChannelGroupMetadata(group, new MdfChannelGroupDefinition { Comment = Comment, Source = Source("dev", null, null, 1, false) });
                writer.SetChannelGroupMetadata(group, new MdfChannelGroupDefinition { Comment = string.Empty, Name = "slower" });
                writer.StartMeasurement();
                writer.SaveSample(group, new[] { MdfSampleValue.FromUnsigned(7) }); // The one channel of the group is a 16-bit unsigned value.
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
            }
            byte[] file = output.Snapshot();
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file)))
            {
                // The master's channel sits in the group the file starts with, so the named group is the second one;
                // counting on a Single over all groups would only prove the master's group had been dropped.
                IReadOnlyList<MdfChannelGroup> groups = reader.ReadMetadata().SelectMany(d => d.ChannelGroups).ToList();
                Assert.Equal(2, groups.Count);
                MdfChannelGroup group = groups[1];
                Assert.Equal("slower", group.Name);
                Assert.Equal(string.Empty, group.Comment);
                Assert.Equal(string.Empty, groups[0].Comment); // The group that was never described stays without one.
                Assert.Equal("dev", group.Source.Name); // A null Source member keeps what the group already holds.
            }
            Assert.Empty(Blocks(file, "MD"));
        }

        [Fact]
        public void Mdf3StoresAChannelCommentAsAFixedFieldAndRefusesSourceAndGroupBlocks()
        {
            MdfChannelDefinition withSource = WriterConfigurationTests.Channel("speed", 16);
            withSource.Source = new MdfSourceDefinition { Name = "dev" }; // MDF 3.30 has no per-channel SI block.
            Assert.Throws<NotSupportedException>(() => WriteChannelMetadata(new MdfWriterOptions { Version = "3.30" }, withSource));
            // cn_description is CHAR[128], and a MDF 3 fixed field is stored without a NUL of its own, so a
            // comment of exactly 128 Latin-1 bytes is the longest one that fits the block.
            string widest = new string('c', 128);
            MdfChannelDefinition channel = WriterConfigurationTests.Channel("speed", 16);
            channel.Comment = widest;
            byte[] file = WriteChannelMetadata(new MdfWriterOptions { Version = "3.30" }, channel);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file)))
            {
                Assert.True(MdfValidator.Validate(reader).IsValid);
                MdfChannel value = reader.ReadMetadata().SelectMany(g => g.ChannelGroups).Single().Channels.Single(c => c.Name == "speed");
                Assert.Equal(widest, value.Comment);
            }
            // One byte more does not fit the field: it is refused instead of truncated, and no second text is
            // moved into the field either, because a long identifier has no storage of its own in this revision.
            MdfChannelDefinition overlong = WriterConfigurationTests.Channel("speed", 16);
            overlong.Comment = new string('c', 129);
            Assert.Throws<NotSupportedException>(() => WriteChannelMetadata(new MdfWriterOptions { Version = "3.30" }, overlong));
            MdfChannelDefinition identifier = WriterConfigurationTests.Channel("speed", 16);
            identifier.Source = new MdfSourceDefinition { Name = "dev", LongIdentifier = "wheel speed" };
            Assert.Throws<NotSupportedException>(() => WriteChannelMetadata(new MdfWriterOptions { Version = "3.30" }, identifier));
            Assert.Throws<NotSupportedException>(() =>
            {
                TestWriteStream output = new TestWriteStream();
                using (MdfWriter writer = MdfWriter.Create(output, true, new MdfWriterOptions { Version = "3.30" }))
                {
                    writer.SetChannelGroupMetadata(0, new MdfChannelGroupDefinition { Comment = "no CG block in 3.30" });
                }
            });
        }

        [Fact]
        public void OverlongIdentifiersAndUnknownBusTypesAreRefused()
        {
            // cn_long_identifier is the 26-byte tail field of the 160-byte CN block, so 25 bytes plus the NUL fit and
            // one byte more does not; the block never grows to make room, since no link points at that field.
            MdfChannelDefinition widest = WriterConfigurationTests.Channel("x", 8);
            widest.Source = new MdfSourceDefinition { Name = "dev", LongIdentifier = new string('i', 25) };
            byte[] stored = WriteChannelMetadata(new MdfWriterOptions(), widest);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(stored)))
            { Assert.True(MdfValidator.Validate(reader).IsValid); }
            Assert.All(Blocks(stored, "CN"), block => Assert.Equal(160UL, U64(stored, (int)block + 8)));
            MdfChannelDefinition longId = WriterConfigurationTests.Channel("x", 8);
            longId.Source = new MdfSourceDefinition { Name = "dev", LongIdentifier = new string('i', 26) };
            Assert.Throws<NotSupportedException>(() => WriteChannelMetadata(new MdfWriterOptions(), longId));
            MdfChannelDefinition badBus = WriterConfigurationTests.Channel("x", 8);
            badBus.Source = new MdfSourceDefinition { Name = "dev", BusType = 8 };
            Assert.Throws<ArgumentOutOfRangeException>(() => WriteChannelMetadata(new MdfWriterOptions(), badBus));
            MdfChannelDefinition badType = WriterConfigurationTests.Channel("x", 8);
            badType.Source = new MdfSourceDefinition { Name = "dev", SourceType = 0 };
            Assert.Throws<ArgumentOutOfRangeException>(() => WriteChannelMetadata(new MdfWriterOptions(), badType));
            // An over-long source name (not the name field) is bounded by MaximumTextBytes, like every metadata text.
            MdfChannelDefinition huge = WriterConfigurationTests.Channel("x", 8);
            huge.Source = new MdfSourceDefinition { Name = new string('n', 2000) };
            Assert.Throws<MdfResourceLimitException>(() => WriteChannelMetadata(new MdfWriterOptions { MaximumTextBytes = 1024 }, huge));
        }

        [Fact]
        public void CommentsSurviveCompressionAndAppendAndDoNotDisturbData()
        {
            MdfChannelDefinition channel = WriterConfigurationTests.Channel("speed", 16);
            channel.Comment = "compressed group note";
            channel.Source = Source("dev", "C:/a.mf4", null, 1, false);
            byte[] file = WriteChannelMetadata(new MdfWriterOptions { CompressData = true, CompressionBlockBytes = 32, StartTimeNanoseconds = 99 }, channel);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file)))
            {
                Assert.True(MdfValidator.Validate(reader).IsValid);
                IReadOnlyList<MdfChannelGroup> groups = reader.ReadMetadata().SelectMany(d => d.ChannelGroups).ToList();
                Assert.Equal("<CNcomment><TX>compressed group note</TX></CNcomment>", groups.Single().Channels.Single(c => c.Name == "speed").Comment);
                Assert.Equal(1UL, groups.Single().SampleCount);
            }
        }

        [Fact]
        public void ANameAndCommentAreAlsoAcceptedWhenTheGroupIsCreated()
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(output, true, new MdfWriterOptions()))
            {
                writer.AddChannel(WriterMultiGroupTests.Master());
                int group = writer.AddChannelGroup(new MdfChannelGroupDefinition { Name = "second", Comment = "created with the group", Source = Source("dev", null, null, 1, true) });
                writer.AddChannel(group, WriterConfigurationTests.Channel("x", 8));
                writer.StartMeasurement();
                writer.SaveSample(group, new[] { MdfSampleValue.FromUnsigned(1) });
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
            }
            byte[] file = output.Snapshot();
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file)))
            {
                IReadOnlyList<MdfChannelGroup> groups = reader.ReadMetadata().SelectMany(d => d.ChannelGroups).ToList();
                Assert.Equal(2, groups.Count);
                Assert.Equal("second", groups[1].Name);
                Assert.Equal("<CGcomment><TX>created with the group</TX></CGcomment>", groups[1].Comment);
                Assert.Equal((byte)(1 | 8), groups[1].Source.Flags);
            }
            // The retry stream cannot be called output here: the enclosing method declares that name for its
            // own writer, and a nested scope may not reuse it, so the lambda gets a name of its own (CS0136).
            Assert.Throws<NotSupportedException>(() =>
            {
                TestWriteStream retry = new TestWriteStream();
                using (MdfWriter writer = MdfWriter.Create(retry, true, new MdfWriterOptions { Version = "3.30" }))
                {
                    writer.AddChannelGroup(new MdfChannelGroupDefinition { Comment = "unsupported here" });
                }
            });
        }
    }
}
