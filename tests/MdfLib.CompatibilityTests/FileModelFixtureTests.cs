// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using MdfLib.TestSupport;
using Xunit;

namespace MdfLib.CompatibilityTests
{
    // expectations frozen before the C# change (tools/fixtures/m6e-expectations.json, build_m6e.py) and independently
    // cross-read by asammdf 8.7.2 (m6e-reference-report.json): HD comment, FH, AT (plain/zlib/external, MD5), EV, CH, CN AT refs.
    public sealed class FileModelFixtureTests
    {
        private static readonly string[] Ids = { "m6e-model410", "m6e-model420", "m6e-comment330" };

        public static IEnumerable<object[]> AllFiles() { return Ids.Select(id => new object[] { id }); }
        public static IEnumerable<object[]> Mdf4Files() { return Ids.Where(id => id != "m6e-comment330").Select(id => new object[] { id }); }

        [Theory]
        [MemberData(nameof(AllFiles))]
        public void HeaderCommentAndFileHistoryMatchTheFrozenModel(string id)
        {
            XElement expected = ReadCase(id);
            using (MdfReader reader = Open(id))
            {
                Assert.Equal(Text(expected, "version"), reader.Header.Version);
                MdfFileModel model = reader.ReadFileModel();
                Assert.Same(model, reader.ReadFileModel());
                Assert.Equal(Text(expected, "comment"), model.Comment);
                List<XElement> history = Items(expected, "history").ToList();
                Assert.Equal(history.Count, model.FileHistory.Count);
                for (int i = 0; i < history.Count; i++)
                {
                    MdfFileHistory actual = model.FileHistory[i];
                    XElement want = history[i];
                    Assert.Equal(i, actual.Index);
                    Assert.Equal(ulong.Parse(Text(want, "time"), CultureInfo.InvariantCulture), actual.RawTimeNanoseconds);
                    Assert.Equal(Int(want, "utcOffset"), actual.UtcOffsetMinutes);
                    Assert.Equal(Int(want, "dstOffset"), actual.DaylightSavingOffsetMinutes);
                    Assert.Equal(Int(want, "timeFlags"), (int)actual.TimeFlags);
                    Assert.Equal(Text(want, "comment"), actual.Comment);
                    Assert.Equal(Text(want, "text"), actual.Text);
                    Assert.Equal(Text(want, "toolId"), actual.ToolIdentifier);
                    Assert.Equal(Text(want, "toolVendor"), actual.ToolVendor);
                    Assert.Equal(Text(want, "toolVersion"), actual.ToolVersion);
                    Assert.Equal(Text(want, "userName"), actual.UserName);
                }
                if (id == "m6e-comment330")
                {
                    Assert.Empty(model.Attachments);
                    Assert.Empty(model.Events);
                    Assert.Empty(model.ChannelHierarchy);
                }
            }
        }

        [Theory]
        [MemberData(nameof(Mdf4Files))]
        public void AttachmentMetadataAndPayloadsMatch(string id)
        {
            XElement expected = ReadCase(id);
            using (MdfReader reader = Open(id))
            {
                MdfFileModel model = reader.ReadFileModel();
                List<XElement> attachments = Items(expected, "attachments").ToList();
                Assert.Equal(attachments.Count, model.Attachments.Count);
                foreach (XElement want in attachments)
                {
                    MdfAttachment actual = model.Attachments[Int(want, "index")];
                    Assert.Equal(Int(want, "index"), actual.Index);
                    Assert.Equal(Text(want, "fileName"), actual.FileName);
                    Assert.Equal(Text(want, "mimeType"), actual.MimeType);
                    Assert.Equal(Text(want, "comment"), actual.Comment);
                    Assert.Equal(Int(want, "flags"), (int)actual.Flags);
                    Assert.Equal(Int(want, "creatorIndex"), (int)actual.CreatorIndex);
                    Assert.Equal(Bool(want, "embedded"), actual.IsEmbedded);
                    Assert.Equal(Bool(want, "compressed"), actual.IsCompressed);
                    Assert.Equal(Bool(want, "hasChecksum"), actual.HasChecksum);
                    Assert.Equal(ulong.Parse(Text(want, "originalSize"), CultureInfo.InvariantCulture), actual.OriginalSize);
                    Assert.Equal(ulong.Parse(Text(want, "embeddedSize"), CultureInfo.InvariantCulture), actual.EmbeddedSize);
                    Assert.Equal(Text(want, "checksum"), Hex(actual.GetChecksum()));
                    Assert.NotSame(actual.GetChecksum(), actual.GetChecksum());
                    if (actual.IsEmbedded)
                    {
                        foreach (bool verify in new[] { true, false })
                        {
                            byte[] payload = reader.ReadAttachment(actual, verify);
                            Assert.Equal((long)actual.OriginalSize, payload.LongLength);
                            Assert.Equal(Text(want, "payloadHead"), Hex(payload.Take(16).ToArray()));
                            using (SHA256 hash = SHA256.Create()) { Assert.Equal(Text(want, "payloadSha256"), Hex(hash.ComputeHash(payload))); }
                            using (MD5 md5 = MD5.Create()) { Assert.Equal(Text(want, "checksum"), Hex(md5.ComputeHash(payload))); }
                        }
                    }
                    else
                    {
                        Assert.Throws<NotSupportedException>(() => reader.ReadAttachment(actual));
                    }
                }
                MdfChannel speed = Assert.Single(Assert.Single(reader.ReadMetadata()).ChannelGroups).Channels.Single(c => c.Name == "speed");
                XElement link = Items(expected, "channelAttachments").Single();
                Assert.Equal(Items(link, "attachments").Select(x => int.Parse(x.Value, CultureInfo.InvariantCulture)),
                    model.GetChannelAttachments(speed).Select(a => a.Index));
                Assert.All(model.GetChannelAttachments(speed), a => Assert.Same(model.Attachments[a.Index], a));
            }
        }

        [Theory]
        [MemberData(nameof(Mdf4Files))]
        public void EventsResolveParentsRangesScopesAndAttachments(string id)
        {
            XElement expected = ReadCase(id);
            using (MdfReader reader = Open(id))
            {
                MdfFileModel model = reader.ReadFileModel();
                MdfChannelGroup group = Assert.Single(Assert.Single(reader.ReadMetadata()).ChannelGroups);
                List<XElement> events = Items(expected, "events").ToList();
                Assert.Equal(events.Count, model.Events.Count);
                foreach (XElement want in events)
                {
                    MdfEvent actual = model.Events[Int(want, "index")];
                    Assert.Equal(Int(want, "index"), actual.Index);
                    Assert.Equal(Text(want, "name"), actual.Name);
                    Assert.Equal(Text(want, "comment"), actual.Comment);
                    Assert.Equal(Text(want, "groupName"), actual.GroupName);
                    Assert.Equal(Text(want, "type"), actual.Type.ToString());
                    Assert.Equal(Text(want, "syncType"), actual.SyncType.ToString());
                    Assert.Equal(Text(want, "rangeType"), actual.RangeType.ToString());
                    Assert.Equal(Text(want, "cause"), actual.Cause.ToString());
                    Assert.Equal(Int(want, "flags"), (int)actual.Flags);
                    Assert.Equal(Int(want, "creatorIndex"), (int)actual.CreatorIndex);
                    Assert.Equal(long.Parse(Text(want, "syncBase"), CultureInfo.InvariantCulture), actual.SyncBaseValue);
                    Assert.Equal(double.Parse(Text(want, "syncFactor"), CultureInfo.InvariantCulture), actual.SyncFactor);
                    Assert.Equal(double.Parse(Text(want, "syncValue"), CultureInfo.InvariantCulture), actual.SyncValue);
                    Assert.Same(Reference(model, want, "parent"), actual.Parent);
                    Assert.Same(Reference(model, want, "range"), actual.Range);
                    Assert.Equal(Items(want, "scopeGroups").Count(), actual.ScopeChannelGroups.Count);
                    Assert.All(actual.ScopeChannelGroups, g => Assert.Same(group, g));
                    Assert.Equal(Items(want, "scopeChannels").Select(x => x.Value), actual.ScopeChannels.Select(c => c.Name));
                    Assert.All(actual.ScopeChannels, c => Assert.Same(group.Channels[c.Index], c));
                    Assert.Equal(Items(want, "attachments").Select(x => int.Parse(x.Value, CultureInfo.InvariantCulture)), actual.Attachments.Select(a => a.Index));
                    Assert.All(actual.Attachments, a => Assert.Same(model.Attachments[a.Index], a));
                }
            }
        }

        [Theory]
        [MemberData(nameof(Mdf4Files))]
        public void ChannelHierarchyTreeResolvesCatalogChannels(string id)
        {
            XElement expected = ReadCase(id);
            using (MdfReader reader = Open(id))
            {
                MdfFileModel model = reader.ReadFileModel();
                MdfChannelGroup group = Assert.Single(Assert.Single(reader.ReadMetadata()).ChannelGroups);
                CheckNodes(Items(expected, "hierarchy").ToList(), model.ChannelHierarchy, group);
            }
        }

        [Theory]
        [MemberData(nameof(Mdf4Files))]
        public void ChannelsWithAttachmentReferencesStayReadable(string id)
        {
            XElement expected = ReadCase(id);
            using (MdfReader reader = Open(id))
            {
                MdfChannelGroup group = Assert.Single(Assert.Single(reader.ReadMetadata()).ChannelGroups);
                MdfChannel speed = group.Channels.Single(c => c.Name == "speed");
                Assert.True(speed.CanReadRaw, speed.UnsupportedReason);
                List<string> values = new List<string>();
                foreach (MdfSampleBatch batch in reader.ReadSamples(group, new[] { speed.Index }))
                {
                    for (int row = 0; row < batch.SampleCount; row++) { values.Add(batch.GetValue(row, 0).UnsignedValue.ToString(CultureInfo.InvariantCulture)); }
                }
                Assert.Equal(Items(expected, "speed").Select(x => x.Value), values);
            }
        }

        [Fact]
        public void TheModelSurvivesReaderDisposalButAttachmentReadsDoNot()
        {
            MdfFileModel model;
            MdfReader reader = Open("m6e-model410");
            using (reader) { model = reader.ReadFileModel(); }
            Assert.Equal(3, model.Attachments.Count);
            Assert.Equal("tester", model.FileHistory[0].UserName);
            Assert.Throws<ObjectDisposedException>(() => reader.ReadAttachment(model.Attachments[0]));
            Assert.Throws<ObjectDisposedException>(() => reader.ReadFileModel());
        }

        [Fact]
        public void AttachmentsOfAnotherReaderAreRejected()
        {
            using (MdfReader first = Open("m6e-model410"))
            using (MdfReader second = Open("m6e-model410"))
            {
                MdfAttachment foreign = first.ReadFileModel().Attachments[0];
                Assert.Throws<ArgumentException>(() => second.ReadAttachment(foreign));
                Assert.Throws<ArgumentNullException>(() => second.ReadAttachment(null));
                Assert.Throws<ArgumentNullException>(() => second.ReadFileModel().GetChannelAttachments(null));
            }
        }

        private static void CheckNodes(List<XElement> expected, IReadOnlyList<MdfChannelHierarchy> actual, MdfChannelGroup group)
        {
            Assert.Equal(expected.Count, actual.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(Text(expected[i], "name"), actual[i].Name);
                Assert.Equal(Text(expected[i], "comment"), actual[i].Comment);
                Assert.Equal(Text(expected[i], "type"), actual[i].Type.ToString());
                Assert.Equal(Items(expected[i], "channels").Select(x => x.Value), actual[i].Channels.Select(c => c.Name));
                Assert.All(actual[i].Channels, c => Assert.Same(group.Channels[c.Index], c));
                CheckNodes(Items(expected[i], "children").ToList(), actual[i].Children, group);
            }
        }

        private static MdfEvent Reference(MdfFileModel model, XElement parent, string name)
        {
            XElement item = parent.Element(name);
            XAttribute type = item.Attribute("type");
            return type != null && type.Value == "null" ? null : model.Events[int.Parse(item.Value, CultureInfo.InvariantCulture)];
        }

        private static MdfReader Open(string id) { return MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)); }
        private static string Hex(byte[] bytes) { return string.Concat(bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture))); }
        private static IEnumerable<XElement> Items(XElement parent, string name) { return parent.Element(name).Elements("item"); }
        private static string Text(XElement parent, string name) { return parent.Element(name).Value; }
        private static int Int(XElement parent, string name) { return int.Parse(parent.Element(name).Value, CultureInfo.InvariantCulture); }
        private static bool Bool(XElement parent, string name) { return bool.Parse(parent.Element(name).Value); }

        private static XElement ReadCase(string id)
        {
            using (FileStream stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "m6e-expectations.json")))
            using (XmlDictionaryReader json = JsonReaderWriterFactory.CreateJsonReader(stream, XmlDictionaryReaderQuotas.Max))
            {
                return XElement.Load(json).Element("cases").Elements("item").Single(c => Text(c, "id") == id);
            }
        }
    }
}
