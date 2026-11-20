// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Linq;
using System.Threading;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Reading
{
    public sealed class Mdf420MetadataTests
    {
        [Theory]
        [InlineData("m5c-dt420")]
        [InlineData("m5c-hl420")]
        [InlineData("m5c-ld-equal420")]
        [InlineData("m5c-ld-offset420")]
        [InlineData("m5c-ld-chain420")]
        [InlineData("m5c-ld-null420")]
        [InlineData("m5c-ld-noinval420")]
        [InlineData("m5c-ld-single420")]
        [InlineData("m5c-ld-zsingle420")]
        [InlineData("m5c-ld-tsingle420")]
        public void ExactFinalized420MetadataAndZeroRequestsNeverReadStorageOrInertHistory(string id)
        {
            byte[] bytes = M5CWire.Read(id);
            int dg = M5CWire.Dg(bytes);
            int storage = id.Contains("single") ? 1256 : 1200;
            using (TestReadStream source = new TestReadStream(bytes) { MaximumBytesPerRead = 3 })
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                Assert.Equal("4.20", reader.Header.Version);
                Assert.Equal(1234567890123456789UL, reader.Header.RawStartTimeNanoseconds);
                Assert.Equal(-300, reader.Header.UtcOffsetMinutes);
                MdfChannelGroup group = Assert.Single(Assert.Single(reader.ReadMetadata()).ChannelGroups);
                Assert.True(group.CanReadSamples);
                Assert.Equal(id.Contains("noinval") ? 22UL : 24UL, group.RecordByteCount);
                Assert.True(group.Channels[0].IsMaster);
                Assert.All(group.Channels.Skip(1), c => Assert.False(c.IsMaster));
                Assert.All(source.ReadRanges, r => Assert.True(r.Item1 + r.Item2 <= storage));
                Assert.DoesNotContain(source.ReadRanges, r => r.Item1 >= 168 && r.Item1 < dg); // No FH semantics introduced.
                int before = source.ReadCalls;
                source.ReadFailure = new IOException("No storage access for eager/zero requests");
                var pending = reader.ReadSamples(group, new[] { 1 }, 1, 1);
                Assert.Empty(reader.ReadSamples(group, new[] { 1 }, sampleCount: 0));
                Assert.Empty(reader.ReadSamples(group, new[] { 1 }, group.SampleCount));
                Assert.Equal(before, source.ReadCalls);
                Assert.Null(group.Layout.DataMap);
                Assert.Null(group.Layout.ListDataMap);
                source.ReadFailure = null;
                Assert.Equal(9007199254740993UL, pending.Single().GetValue(0, 0).UnsignedValue);
                Assert.Equal(0, source.WriteCalls);
                Assert.Equal(0, source.FlushCalls);
            }
        }

        [Theory]
        [InlineData("m5c-ld-remote420")]
        [InlineData("m5c-asammdf-ld420")]
        [InlineData("m5c-asammdf-zld420")]
        [InlineData("m5c-asammdf-transpose420")]
        public void RemoteReferencesNormalizeAfterTheCatalogAndNeverFetchMasterSamplesImplicitly(string id)
        {
            byte[] bytes = M5CWire.Read(id);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                var catalog = reader.ReadMetadata();
                MdfChannelGroup master = catalog[0].ChannelGroups[0];
                MdfChannelGroup signal = catalog[1].ChannelGroups[0];
                Assert.True(master.Channels[0].IsMaster);
                Assert.All(signal.Channels, channel => Assert.False(channel.IsMaster));
                Assert.All(catalog, dg => Assert.True(Assert.Single(dg.ChannelGroups).CanReadSamples));
                int before = source.ReadRanges.Count;
                Assert.Equal(9007199254740993UL, reader.ReadSamples(signal, new[] { 0 }, 1, 1).Single().GetValue(0, 0).UnsignedValue);
                Assert.Null(master.Layout.DataMap);
                Assert.Null(master.Layout.ListDataMap);
                int masterRoot = M5CWire.Root(bytes);
                Assert.DoesNotContain(source.ReadRanges.Skip(before), r => r.Item1 >= masterRoot && r.Item1 < masterRoot + (long)M3Wire.U64(bytes, masterRoot + 8));
                Assert.Equal(id == "m5c-ld-remote420" ? 0.0 : .125, reader.ReadSamples(master, new[] { 0 }, 1, 1).Single().GetValue(0, 0).FloatingPointValue);
                Assert.NotNull(master.Layout.ListDataMap);
                Assert.Equal(0, source.WriteCalls);
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(60)]
        [InlineData(61)]
        [InlineData(62)]
        [InlineData(63)]
        public void Unfinalized420IsRefusedBeforeHdOrMetadata(int flag)
        {
            byte[] bytes = M5CWire.Read();
            if (flag == 0) { Array.Copy(System.Text.Encoding.ASCII.GetBytes("UnFinMF "), bytes, 8); }
            else { bytes[flag] = 1; }
            using (TestReadStream source = new TestReadStream(bytes))
            {
                Assert.Throws<NotSupportedException>(() => MdfReader.Open(source, true));
                Assert.All(source.ReadRanges, r => Assert.True(r.Item1 + r.Item2 <= 64));
                Assert.Equal(0, source.DisposeCalls);
            }
        }

        [Theory]
        [InlineData("4.19", 419)]
        [InlineData("4.21", 421)]
        [InlineData("4.30", 430)]
        public void NeighborRevisionsDoNotBorrow420StorageSupport(string text, int number)
        {
            byte[] bytes = M5CWire.Read();
            for (int index = 0; index < 4; index++) { bytes[index + 8] = (byte)text[index]; }
            M3Wire.Put(bytes, 28, (ulong)number, 2);
            using (TestReadStream source = new TestReadStream(bytes))
            {
                Assert.Throws<NotSupportedException>(() => MdfReader.Open(source, true));
                Assert.Single(source.ReadRanges);
            }
        }

        [Theory]
        [InlineData(3)]
        [InlineData(5)]
        [InlineData(16)]
        [InlineData(255)]
        public void Unsorted420WithUndefinedRecordIdWidthsRemainsInspectableButExplicitlyUnsupported(int width)
        {
            byte[] bytes = M5CWire.Read();
            bytes[M3Wire.Payload(bytes, M5CWire.Dg(bytes))] = (byte)width;
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.False(group.CanReadSamples);
                Assert.Contains("prefix widths", group.UnsupportedReason);
                Assert.Null(group.Layout.Interleaved);
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 0 }));
            }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(8)]
        public void Unsorted420LdRootIsRefusedAtEnumerationWithoutReadingListContents(int width)
        {
            // record IDs make the DG interleaved; column-oriented LD storage is defined only for sorted DGs.
            byte[] bytes = M5CWire.Read();
            bytes[M3Wire.Payload(bytes, M5CWire.Dg(bytes))] = (byte)width;
            int leaf = M5CWire.Leaf(bytes);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.True(group.CanReadSamples);
                Assert.NotNull(group.Layout.Interleaved);
                Assert.Equal(width, group.Layout.Interleaved.PrefixBytes);
                source.ReadRanges.Clear();
                NotSupportedException error = Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 0 }).ToList());
                Assert.Contains("unsorted MDF 4.20", error.Message);
                Assert.DoesNotContain(source.ReadRanges, r => r.Item1 < leaf + 24 && r.Item1 + r.Item2 > leaf);
            }
        }

        [Theory]
        [InlineData(0, 7)]
        [InlineData(1, 6)]
        [InlineData(32, 6)]
        [InlineData(64, 6)]
        [InlineData(8, 6)]
        [InlineData(16, 6)]
        [InlineData(9, 7)]
        [InlineData(8, 8)]
        public void OnlyOrdinarySixLinksOrRemoteFlagEightWithSevenLinksIsAllowed(int flags, int links)
        {
            byte[] bytes = M5CWire.Read();
            M5CWire.CloneCg(ref bytes, 0, (ushort)flags, links);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.False(group.CanReadSamples);
                Assert.NotEmpty(group.UnsupportedReason);
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 0 }));
            }
        }

        [Theory]
        [InlineData("null")]
        [InlineData("self")]
        [InlineData("hd")]
        [InlineData("orphan-cg")]
        [InlineData("count")]
        public void MalformedRemoteReferenceFailsAtomicCatalogAndRetryWithoutClosingSource(string mutation)
        {
            byte[] bytes = M5CWire.Read("m5c-ld-remote420");
            int remote = M5CWire.Cg(bytes, 1);
            if (mutation == "count") { M3Wire.Put(bytes, M3Wire.Payload(bytes, remote) + 8, 6); }
            else
            {
                int target = mutation == "null" ? 0 : mutation == "self" ? remote : 64;
                if (mutation == "orphan-cg")
                {
                    int original = M5CWire.Cg(bytes);
                    target = M5CWire.CloneCg(ref bytes, 0, 0, 6);
                    M3Wire.Put(bytes, M3Wire.Link(M5CWire.Dg(bytes), 1), (ulong)original); // Syntactically CG, but not catalog-owned.
                }
                M3Wire.Put(bytes, M3Wire.Link(remote, 6), (ulong)target);
            }
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                for (int retry = 0; retry < 2; retry++)
                {
                    MdfFormatException error = Assert.Throws<MdfFormatException>(() => reader.ReadMetadata());
                    Assert.Equal("MDF013", error.DiagnosticCode);
                    Assert.Equal((ulong)remote, error.Offset);
                }
                Assert.Equal(0, source.DisposeCalls);
                Assert.Equal(0, source.WriteCalls);
            }
        }

        [Theory]
        [InlineData("no-master")]
        [InlineData("unsupported-master")]
        [InlineData("unsupported-group")]
        [InlineData("ids")]
        [InlineData("chain")]
        [InlineData("cycle")]
        public void UnsupportedTargetsChainsAndCyclesDoNotBecomeImplicitTimeJoins(string mutation)
        {
            byte[] bytes = M5CWire.Read("m5c-asammdf-ld420");
            int master = M5CWire.Cg(bytes), remote = M5CWire.Cg(bytes, 1);
            int cn = (int)M3Wire.U64(bytes, M3Wire.Link(master, 1));
            if (mutation == "no-master") { bytes[M3Wire.Payload(bytes, cn)] = 0; }
            else if (mutation == "unsupported-master") { bytes[M3Wire.Payload(bytes, cn)] = 3; }
            else if (mutation == "unsupported-group") { M3Wire.Put(bytes, M3Wire.Payload(bytes, master) + 16, 1, 2); }
            else if (mutation == "ids") { bytes[M3Wire.Payload(bytes, M5CWire.Dg(bytes))] = 1; }
            else if (mutation == "chain") { M3Wire.Put(bytes, M3Wire.Link(remote, 6), (ulong)M5CWire.Cg(bytes, 2)); }
            else
            {
                master = M5CWire.CloneCg(ref bytes, 0, 8, 7);
                M3Wire.Put(bytes, M3Wire.Link(master, 6), (ulong)remote);
                for (int index = 1; index < 4; index++) { M3Wire.Put(bytes, M3Wire.Link(M5CWire.Cg(bytes, index), 6), (ulong)master); }
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[1].ChannelGroups[0];
                Assert.False(group.CanReadSamples);
                Assert.Contains("master", group.UnsupportedReason);
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 0 }));
            }
        }

        [Fact]
        public void OlderVersionsDoNotAdopt420RemoteFlagsAndMultiple420CgsNeedFutureFramingContract()
        {
            byte[] bytes = M5CWire.Read("m5c-ld-remote420");
            bytes[10] = (byte)'1'; bytes[11] = (byte)'1'; M3Wire.Put(bytes, 28, 411, 2);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            { Assert.False(reader.ReadMetadata()[1].ChannelGroups[0].CanReadSamples); }
            bytes = M5CWire.Read();
            int original = M5CWire.Cg(bytes);
            int copy = M5CWire.CloneCg(ref bytes, 0, 0, 6);
            M3Wire.Put(bytes, M3Wire.Link(copy, 1), 0);
            M3Wire.Put(bytes, M3Wire.Link(M5CWire.Dg(bytes), 1), (ulong)original);
            M3Wire.Put(bytes, M3Wire.Link(original, 0), (ulong)copy);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                var groups = reader.ReadMetadata()[0].ChannelGroups;
                Assert.Equal(2, groups.Count);
                Assert.All(groups, group => { Assert.False(group.CanReadSamples); Assert.Null(group.Layout.Interleaved); });
            }
        }

        [Fact]
        public void RemoteGroupCannotReinterpretInlineDtAsSeparateLd()
        {
            byte[] bytes = M5CWire.Read("m5c-ld-remote420");
            int dt = M5Wire.Add(ref bytes, "DT", new ulong[0], new byte[7 * 16]);
            M3Wire.Put(bytes, M3Wire.Link(M5CWire.Dg(bytes, 1), 2), (ulong)dt);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[1].ChannelGroups[0];
                Assert.True(group.CanReadSamples);
                Assert.Empty(reader.ReadSamples(group, new[] { 0 }, sampleCount: 0));
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 0 }).ToList());
                Assert.Null(group.Layout.ListDataMap);
            }
        }
    }
}
