// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MdfLib.Reading;
using MdfLib.Tests.TestDoubles;
using MdfLib.TestSupport;
using Xunit;

namespace MdfLib.Tests.Reading
{
    // fixed and VLSD/SD text/byte values over literal mutations of the frozen pre-parser m5f-text410 file.
    public sealed class TextValueTests
    {
        private const int Records = 3424 + 24; // DT payload; five 97-byte records (96 data + 1 invalidation byte).
        private const int RecordBytes = 97;
        private static readonly Dictionary<string, int> Cn = new Dictionary<string, int>
        {
            { "latin", 528 }, { "utf8", 728 }, { "u16le", 928 }, { "u16be", 1128 }, { "bytes", 1328 },
            { "vutf8", 1528 }, { "vlatin", 1792 }, { "vu16le", 2088 }, { "vu16be", 2352 }, { "vbytes", 2616 }
        };

        [Theory]
        [InlineData(new byte[] { 0x41, 0xE2, 0x82, 0xAC, 0xF0, 0x9F, 0x98, 0x80 }, "A\u20AC\uD83D\uDE00")]
        [InlineData(new byte[] { 0xC0, 0xAF }, "\uFFFD\uFFFD")]                   // overlong lead bytes
        [InlineData(new byte[] { 0xE0, 0x80, 0x80 }, "\uFFFD\uFFFD\uFFFD")]       // overlong 3-byte: E0 requires A0..BF
        [InlineData(new byte[] { 0xED, 0xA0, 0x80 }, "\uFFFD\uFFFD\uFFFD")]       // encoded surrogate
        [InlineData(new byte[] { 0xF4, 0x90, 0x80, 0x80 }, "\uFFFD\uFFFD\uFFFD\uFFFD")] // above U+10FFFF
        [InlineData(new byte[] { 0xF5, 0x41 }, "\uFFFDA")]
        [InlineData(new byte[] { 0xE2, 0x82, 0x41 }, "\uFFFDA")]                 // maximal subpart -> one U+FFFD
        [InlineData(new byte[] { 0xF0, 0x9F, 0x98 }, "\uFFFD")]                  // truncated at end
        [InlineData(new byte[] { 0xE2, 0x82, 0x00, 0x41 }, "\uFFFD")]            // truncated by the terminator
        [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0x42 }, "\uFEFFB")]           // BOM is kept
        [InlineData(new byte[] { 0x80, 0xBF }, "\uFFFD\uFFFD")]
        [InlineData(new byte[] { 0xF4, 0x8F, 0xBF, 0xBF }, "\uDBFF\uDFFF")]
        [InlineData(new byte[0], "")]
        public void Utf8UsesTheOwnMaximalSubpartDecoderOnEveryFramework(byte[] bytes, string expected)
        {
            Assert.Equal(expected, TextDecoder.Decode(7, bytes));
        }

        [Fact]
        public void Latin1Utf16AndTerminatorsFollowTheContract()
        {
            Assert.Equal("Caf\u00E9\u0080\u00FF", TextDecoder.Decode(6, new byte[] { 0x43, 0x61, 0x66, 0xE9, 0x80, 0xFF }));
            Assert.Equal("ab", TextDecoder.Decode(6, new byte[] { 0x61, 0x62, 0x00, 0x63 }));
            Assert.Equal("A\u4200", TextDecoder.Decode(8, new byte[] { 0x41, 0x00, 0x00, 0x42 })); // terminator only on an even unit
            Assert.Equal("A", TextDecoder.Decode(8, new byte[] { 0x41, 0x00, 0x00, 0x00, 0x43, 0x00 }));
            Assert.Equal("\uD800z", TextDecoder.Decode(8, new byte[] { 0x00, 0xD8, 0x7A, 0x00, 0x99 })); // lone surrogate + odd tail
            Assert.Equal("\uDC00A", TextDecoder.Decode(9, new byte[] { 0xDC, 0x00, 0x00, 0x41 }));
            MdfSampleValue value = TextDecoder.Create(true, 8, new byte[] { 0x41, 0x00, 0x99 });
            Assert.Equal("A", value.TextValue);
            Assert.Equal(new byte[] { 0x41, 0x00, 0x99 }, value.GetByteArrayValue());
        }

        [Fact]
        public void NumericAndDefaultValuesKeepTheirKindsAndRejectTextGetters()
        {
            MdfSampleValue numeric = MdfSampleValue.FromUnsigned(5);
            Assert.Throws<InvalidOperationException>(() => numeric.TextValue);
            Assert.Throws<InvalidOperationException>(() => numeric.GetByteArrayValue());
            Assert.Equal(MdfValueKind.UnsignedInteger, default(MdfSampleValue).Kind);
            Assert.Throws<InvalidOperationException>(() => default(MdfSampleValue).GetByteArrayValue());
            Assert.Equal(4, (int)MdfValueKind.Text);
            Assert.Equal(5, (int)MdfValueKind.ByteArray);
        }

        [Fact]
        public void MetadataClassifiesWithoutReadingSignalDataPayloads()
        {
            byte[] bytes = Fixture();
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(new[] { MdfValueKind.FloatingPoint, MdfValueKind.Text, MdfValueKind.Text, MdfValueKind.Text, MdfValueKind.Text,
                    MdfValueKind.ByteArray, MdfValueKind.Text, MdfValueKind.Text, MdfValueKind.Text, MdfValueKind.Text, MdfValueKind.ByteArray,
                    MdfValueKind.Text }, group.Channels.Select(c => c.ValueKind));
                Assert.All(group.Channels.Take(11), c => Assert.True(c.CanReadRaw && c.CanReadPhysical, c.UnsupportedReason));
                Assert.Equal("Text and byte-array channels support only identity conversion.", group.Channels[11].UnsupportedReason);
                Assert.Equal(64U, group.Channels[6].BitCount);
                Assert.Null(group.Channels[6].Layout.SignalData); // Lazy: no SD map at metadata time.
            }
        }

        [Theory]
        [InlineData("latin", 3, 1UL, 1, "byte-aligned")]   // bit offset
        [InlineData("latin", 8, 60UL, 4, "byte-aligned")]  // not whole bytes
        [InlineData("u16le", 8, 72UL, 4, "UTF-16")]        // odd UTF-16 byte count
        [InlineData("latin", 0, 2UL, 1, "fixed-length and VLSD")] // text master
        [InlineData("latin", 0, 3UL, 1, "Virtual channels require an unsigned integer")] // virtual master with text data type
        [InlineData("vutf8", 8, 32UL, 4, "64-bit")]        // VLSD offset width
        [InlineData("vutf8", 3, 4UL, 1, "64-bit")]
        [InlineData("latin", 12, 0x2000UL, 4, "flags")]    // outside the 0x19ff mask
        public void IneligibleShapesStayInspectableButUnsupported(string key, int field, ulong value, int width, string reason)
        {
            byte[] bytes = Fixture();
            M3Wire.Put(bytes, M3Wire.Payload(bytes, Cn[key]) + field, value, width);
            MdfChannel channel = Group(bytes).Channels.Single(c => c.Index == Index(key));
            Assert.False(channel.CanReadRaw);
            Assert.Contains(reason, channel.UnsupportedReason);
            Assert.Equal(key.Contains("bytes") ? MdfValueKind.ByteArray : MdfValueKind.Text, channel.ValueKind);
        }

        [Fact]
        public void UnknownSignalDataRootAndNonzeroFixedDataLinkAreUnsupported()
        {
            byte[] bytes = Fixture();
            M3Wire.Put(bytes, M3Wire.Link(Cn["vutf8"], 5), 1688); // TX block
            M3Wire.Put(bytes, M3Wire.Link(Cn["latin"], 5), 1728); // SD block on a fixed field
            MdfChannelGroup group = Group(bytes);
            Assert.Contains("SD, DZ, DL or HL", group.Channels[6].UnsupportedReason);
            Assert.Contains("Channel-specific", group.Channels[1].UnsupportedReason);
        }

        [Fact]
        public void TextComponentsArraysAndOutOfRecordFieldsAreRejectedDeterministically()
        {
            byte[] beyond = Fixture();
            M3Wire.Put(beyond, M3Wire.Payload(beyond, Cn["latin"]) + 4, 90, 4); // 8 bytes at 90 > 96 data bytes
            using (MdfReader reader = MdfReader.Open(new MemoryStream(beyond, false)))
            { Assert.Equal("MDF013", Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode); }
            byte[] vlsd = Fixture();
            M3Wire.Put(vlsd, M3Wire.Payload(vlsd, Cn["vutf8"]) + 4, 92, 4); // 8-byte offset at 92 > 96
            using (MdfReader reader = MdfReader.Open(new MemoryStream(vlsd, false)))
            { Assert.Equal("MDF013", Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode); }
        }

        [Fact]
        public void InvalidVlsdRowsAreNeverDereferencedEvenWithGarbageOffsets()
        {
            byte[] bytes = Fixture();
            M3Wire.Put(bytes, Records + 4 * RecordBytes + 52, ulong.MaxValue); // vutf8 row 4 is invalid (inv bit1)
            MdfSampleValue[] values = Column(bytes, 6);
            Assert.False(values[4].IsValid);
            Assert.Equal("", values[4].TextValue);
            Assert.Empty(values[4].GetByteArrayValue());
            Assert.Equal("hello \u20AC", values[0].TextValue);
        }

        [Theory]
        [InlineData(ulong.MaxValue)]
        [InlineData(ulong.MaxValue - 3)]
        [InlineData(61UL)]   // offset+4 beyond the 64-byte SD stream
        [InlineData(64UL)]   // offset == total
        public void ValidRowOffsetsOutsideTheStreamFailWithMdf013WhenThatRowIsRead(ulong offset)
        {
            byte[] bytes = Fixture();
            M3Wire.Put(bytes, Records + 1 * RecordBytes + 52, offset);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Single(reader.ReadSamples(group, new[] { 6 }, 0, 1).ToList());
                Assert.Equal("MDF013", Assert.Throws<MdfFormatException>(() => reader.ReadSamples(group, new[] { 6 }, 1, 1).ToList()).DiagnosticCode);
                Assert.Equal(5, reader.ReadSamples(group, new[] { 5 }).Sum(b => b.SampleCount)); // other channels unaffected
            }
        }

        [Fact]
        public void EntryLengthBeyondTheStreamAndMissingSignalDataFailWithMdf013()
        {
            byte[] length = Fixture();
            int entry = 1728 + 24 + (int)M3Wire.U64(length, Records + 52); // row 0's referenced entry
            M3Wire.Put(length, entry, 0x7FFFFFFF, 4);
            Assert.Equal("MDF013", Assert.Throws<MdfFormatException>(() => Column(length, 6)).DiagnosticCode);
            byte[] missing = Fixture();
            M3Wire.Put(missing, M3Wire.Link(Cn["vutf8"], 5), 0);
            Assert.True(Group(missing).Channels[6].CanReadRaw);
            Assert.Equal("MDF013", Assert.Throws<MdfFormatException>(() => Column(missing, 6)).DiagnosticCode);
        }

        [Fact]
        public void EntryLongerThanTheReadBufferCapIsAResourceLimitBeforeAllocation()
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(Fixture(), false), false, new MdfReaderOptions { MaximumReadBufferBytes = 255 }))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(2, reader.ReadSamples(group, new[] { 10 }, 0, 2).Sum(b => b.SampleCount));
                MdfResourceLimitException error = Assert.Throws<MdfResourceLimitException>(() => reader.ReadSamples(group, new[] { 10 }, 2, 1).ToList());
                Assert.Equal(nameof(MdfReaderOptions.MaximumReadBufferBytes), error.LimitName);
            }
        }

        [Theory]
        [InlineData("m5f-text410")]
        [InlineData("m5f-lists411")]
        [InlineData("m5f-paired420")]
        [InlineData("m5f-interleaved410")]
        public void VariablePayloadsSplitBatchesEarlyWithoutLosingRowsOnEveryRecordSource(string id)
        {
            byte[] bytes = FixtureCorpus.Read(id);
            MdfSampleValue[] all = Column(bytes, 10);
            // Header 160 + rows of 104 + 3*n: [3,0,256,7,1] stored bytes -> batches of 2, 1 and 2 rows under 1100 bytes.
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false), false, new MdfReaderOptions { MaximumSampleBatchBytes = 1100 }))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                List<MdfSampleBatch> batches = reader.ReadSamples(group, new[] { 10 }).ToList();
                Assert.Equal(new[] { 2, 1, 2 }, batches.Select(b => b.SampleCount));
                Assert.Equal(new[] { 0UL, 2UL, 3UL }, batches.Select(b => b.FirstSampleIndex));
                MdfSampleValue[] split = batches.SelectMany(b => Enumerable.Range(0, b.SampleCount).Select(r => b.GetValue(r, 0))).ToArray();
                Assert.Equal(all.Select(v => v.GetByteArrayValue()), split.Select(v => v.GetByteArrayValue()));
            }
            // Mixed with a numeric column (header 192, rows 144 + 3*n) under 1200 bytes: rows 1 | 2 | 3-4, in order.
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false), false, new MdfReaderOptions { MaximumSampleBatchBytes = 1200 }))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                List<MdfSampleBatch> mixed = reader.ReadSamples(group, new[] { 10, 0 }, 1, 4).ToList();
                Assert.Equal(new[] { 1, 1, 2 }, mixed.Select(b => b.SampleCount));
                Assert.Equal(new[] { 0.25, 0.5, 0.75, 1.0 }, mixed.SelectMany(b => Enumerable.Range(0, b.SampleCount).Select(r => b.GetValue(r, 1).FloatingPointValue)));
                Assert.Equal(all.Skip(1).Select(v => v.GetByteArrayValue()),
                    mixed.SelectMany(b => Enumerable.Range(0, b.SampleCount).Select(r => b.GetValue(r, 0).GetByteArrayValue())));
            }
        }

        [Fact]
        public void ASingleRowOverTheBatchCapIsRefusedAfterEarlierRowsArePublished()
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(Fixture(), false), false, new MdfReaderOptions { MaximumSampleBatchBytes = 1000 }))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                using (IEnumerator<MdfSampleBatch> batches = reader.ReadSamples(group, new[] { 10 }).GetEnumerator())
                {
                    Assert.True(batches.MoveNext());
                    Assert.Equal(2, batches.Current.SampleCount);
                    MdfResourceLimitException error = Assert.Throws<MdfResourceLimitException>(() => batches.MoveNext());
                    Assert.Equal(nameof(MdfReaderOptions.MaximumSampleBatchBytes), error.LimitName);
                }
                Assert.Equal(2, reader.ReadSamples(group, new[] { 10 }, 3, 2).Sum(b => b.SampleCount)); // gate released
            }
        }

        [Fact]
        public void FixedTextWidthIsPlannedUpFrontAndCancellationIsObserved()
        {
            // Header 160 + one row 40 + 64 + 3*8 = 128: 160 + 2*128 = 416 allows exactly two rows per batch.
            using (MdfReader reader = MdfReader.Open(new MemoryStream(Fixture(), false), false, new MdfReaderOptions { MaximumSampleBatchBytes = 416 }))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(new[] { 2, 2, 1 }, reader.ReadSamples(group, new[] { 1 }).Select(b => b.SampleCount));
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    cancel.Cancel();
                    Assert.ThrowsAny<OperationCanceledException>(() => reader.ReadSamples(group, new[] { 6 }, cancellationToken: cancel.Token).ToList());
                }
            }
        }

        [Fact]
        public void SignalDataMapIsCachedAndBatchesOutliveDispose()
        {
            List<MdfSampleBatch> batches;
            MdfChannelGroup group;
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read("m5f-lists411"), false)))
            {
                group = reader.ReadMetadata()[0].ChannelGroups[0];
                reader.ReadSamples(group, new[] { 9 }).ToList();
                Mdf4DataMap map = group.Channels[9].Layout.SignalData;
                Assert.NotNull(map);
                batches = reader.ReadSamples(group, new[] { 9, 8, 6 }, batchSize: 2).ToList();
                Assert.Same(map, group.Channels[9].Layout.SignalData);
                Assert.Throws<ObjectDisposedException>(() => { reader.Dispose(); reader.ReadSamples(group, new[] { 9 }).ToList(); });
            }
            Assert.Equal(5, batches.Sum(b => b.SampleCount));
            Assert.Equal("hello \u20AC", batches[0].GetValue(0, 2).TextValue);
        }

        [Fact]
        public void WriterAcceptsFixedTextAndByteArraysButNotUnsupportedKinds()
        {
            using (MdfWriter writer = MdfWriter.Create(new MemoryStream(), false))
            {
                foreach (MdfValueKind kind in new[] { MdfValueKind.Text, MdfValueKind.ByteArray })
                {
                    writer.AddChannel(new MdfChannelDefinition { Name = "x", ValueKind = kind, BitCount = 64 });
                }
                MdfChannelDefinition unsupported = new MdfChannelDefinition { Name = "x", ValueKind = MdfValueKind.Unsupported, BitCount = 64 };
                Assert.Throws<NotSupportedException>(() => writer.AddChannel(unsupported));
                Assert.Equal(2, writer.ChannelCount);
                Assert.Equal(17, writer.RecordByteCount);
            }
        }

        [Fact]
        public void ReadmeTextExampleSelectsReadableTextAndByteChannels()
        {
            // Text and byte arrays (fixed and VLSD) in MDF 4 and MDF 3, checked by assertions.
            using (MdfReader reader = MdfReader.Open(new MemoryStream(Fixture(), false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                int[] selected = group.Channels
                    .Where(c => c.CanReadRaw && (c.ValueKind == MdfValueKind.Text || c.ValueKind == MdfValueKind.ByteArray))
                    .Select(c => c.Index).ToArray();
                Assert.Equal(Enumerable.Range(1, 10), selected);
                List<string> lines = new List<string>();
                foreach (MdfSampleBatch batch in reader.ReadSamples(group, selected, sampleCount: Math.Min(group.SampleCount, 10UL)))
                {
                    for (int row = 0; row < batch.SampleCount; row++)
                    {
                        MdfSampleValue value = batch.GetValue(row, 0);
                        string shown = value.Kind == MdfValueKind.Text ? value.TextValue : BitConverter.ToString(value.GetByteArrayValue());
                        lines.Add(string.Format("{0}: {1}{2}", batch.Channels[0].Name, shown, value.IsValid ? "" : " (invalid)"));
                    }
                }
                Assert.Equal(new[] { "latin1 fixed: Caf\u00E9", "latin1 fixed: ABCDEFGH", "latin1 fixed: ", "latin1 fixed: \u00A0\u00FFxy", "latin1 fixed: a" }, lines);
            }
        }

        private static int Index(string key) { return Cn.Keys.ToList().IndexOf(key) + 1; }
        private static byte[] Fixture() { return FixtureCorpus.Read("m5f-text410"); }

        private static MdfChannelGroup Group(byte[] bytes)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            { return reader.ReadMetadata()[0].ChannelGroups[0]; }
        }

        private static MdfSampleValue[] Column(byte[] bytes, int channel)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                return reader.ReadSamples(group, new[] { channel }).SelectMany(b => Enumerable.Range(0, b.SampleCount).Select(r => b.GetValue(r, 0))).ToArray();
            }
        }
    }
}
