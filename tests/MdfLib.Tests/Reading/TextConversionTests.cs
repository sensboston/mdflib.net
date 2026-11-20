// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MdfLib.Tests.TestDoubles;
using MdfLib.TestSupport;
using Xunit;

namespace MdfLib.Tests.Reading
{
    // literal CC/TX blocks appended to the frozen m6b/m5 files; expected values are literal.
    public sealed class TextConversionTests
    {
        private const string File410 = "m6b-text410"; // 8 rows: 1 state_u8, 4 band_u16, 6 mode_utf8 (dt7, 8 bytes), 7 tag_latin, 8 keep_latin
        private const int State = 1, Band = 4, Mode = 6, Tag = 7;
        private static readonly ulong[] StateRaw = { 0, 1, 2, 3, 5, 255, 1, 0 };

        private static MdfReader Open(byte[] bytes, MdfReaderOptions options = null)
        {
            return MdfReader.Open(new MemoryStream(bytes, false), options: options);
        }

        private static MdfChannelGroup Group(MdfReader reader) { return reader.ReadMetadata()[0].ChannelGroups[0]; }

        private static ulong Tx(ref byte[] bytes, string text)
        {
            return (ulong)M5Wire.Add(ref bytes, "TX", new ulong[0], Encoding.UTF8.GetBytes(text + "\0"));
        }

        /// <summary>Appends a CC (texts: null = NIL, "#CC" = a nested identity CC, "#HD" = a non-text block) and links it.</summary>
        private static byte[] WithCc(int kind, double[] values, string[] texts, int channel = State, string id = File410)
        {
            byte[] bytes = FixtureCorpus.Read(id);
            ulong[] links = new ulong[4 + texts.Length];
            for (int i = 0; i < texts.Length; i++)
            {
                if (texts[i] == null) { continue; }
                if (texts[i] == "#CC") { links[4 + i] = (ulong)M5Wire.Add(ref bytes, "CC", new ulong[4], new byte[24]); }
                else if (texts[i] == "#HD") { links[4 + i] = 64; }
                else { links[4 + i] = Tx(ref bytes, texts[i]); }
            }
            byte[] payload = new byte[24 + values.Length * 8];
            payload[0] = (byte)kind;
            M3Wire.Put(payload, 4, (ulong)texts.Length, 2);
            M3Wire.Put(payload, 6, (ulong)values.Length, 2);
            for (int i = 0; i < values.Length; i++) { M3Wire.Put(payload, 24 + 8 * i, (ulong)BitConverter.DoubleToInt64Bits(values[i])); }
            int cc = M5Wire.Add(ref bytes, "CC", links, payload);
            M3Wire.Put(bytes, M3Wire.Link(M5DWire.Cn(bytes, channel), 4), (ulong)cc);
            return bytes;
        }

        private static List<MdfSampleValue> Column(MdfReader reader, int index, MdfValueRepresentation representation = MdfValueRepresentation.Physical)
        {
            return reader.ReadSamples(Group(reader), new[] { index }, representation: representation)
                .SelectMany(b => Enumerable.Range(0, b.SampleCount).Select(r => b.GetValue(r, 0))).ToList();
        }

        private static string[] Texts(byte[] bytes, int index = State)
        {
            using (MdfReader reader = Open(bytes))
            {
                List<MdfSampleValue> values = Column(reader, index);
                Assert.All(values, v => Assert.Equal(MdfValueKind.Text, v.Kind));
                return values.Select(v => v.TextValue).ToArray();
            }
        }

        [Fact]
        public void ValueToTextUsesExactKeysDefaultAndEmptyNil()
        {
            Assert.Equal(new[] { "zero", "one", "", "dflt", "dflt", "max", "one", "zero" },
                Texts(WithCc(7, new[] { 0.0, 1, 2, 255 }, new[] { "zero", "one", null, "max", "dflt" })));
            Assert.Equal(new[] { "", "", "", "", "", "", "", "" }, Texts(WithCc(7, new double[0], new string[] { null })));
            Assert.Equal(Enumerable.Repeat("only", 8), Texts(WithCc(7, new double[0], new[] { "only" })));
        }

        [Fact]
        public void RangeToTextUsesInclusiveIntegerRangesInWireOrder()
        {
            // Zero-width [3,3] and adjacent [3,5]: the first range in wire order wins for 3.
            Assert.Equal(new[] { "lo", "lo", "lo", "three", "mid", "out", "lo", "lo" },
                Texts(WithCc(8, new[] { 0.0, 2, 3, 3, 3, 5 }, new[] { "lo", "three", "mid", "out" })));
        }

        [Fact]
        public void TextOutputsShareUtf8BytesAndKeepValidity()
        {
            using (MdfReader reader = Open(WithCc(7, new[] { 1.0 }, new[] { "Grüße ✓", "x" })))
            {
                List<MdfSampleValue> values = Column(reader, State);
                Assert.Equal(Encoding.UTF8.GetBytes("Grüße ✓"), values[1].GetByteArrayValue());
                Assert.Equal(new byte[] { (byte)'x' }, values[0].GetByteArrayValue());
                byte[] copy = values[1].GetByteArrayValue();
                copy[0] = 0;
                Assert.Equal(Encoding.UTF8.GetBytes("Grüße ✓"), values[6].GetByteArrayValue()); // copies, never the shared array
                Assert.All(values, v => Assert.True(v.IsValid));
            }
            using (MdfReader reader = Open(WithCc(7, new[] { 0.0 }, new[] { "z", "d" }, 2)))
            {
                Assert.Equal(new[] { true, true, false, true, true, true, false, true }, Column(reader, 2).Select(v => v.IsValid));
            }
        }

        [Fact]
        public void TextToValueComparesDecodedTextOrdinally()
        {
            byte[] bytes = WithCc(9, new[] { 10.0, 20, 30, -1 }, new[] { "run", "idle", "run" }, Mode);
            using (MdfReader reader = Open(bytes))
            {
                // mode_utf8: idle, run(\0garb), "", stop, RUN (invalid), Grüße, "idle ", stop
                List<MdfSampleValue> values = Column(reader, Mode);
                Assert.Equal(new[] { 20.0, 10, -1, -1, -1, -1, -1, -1 }, values.Select(v => v.FloatingPointValue));
                Assert.False(values[4].IsValid);
                Assert.Equal(new[] { "run", "idle", "run" }, Group(reader).Channels[Mode].Conversion.Texts);
            }
        }

        [Fact]
        public void TextToTextKeepsTheInputForNilOutputs()
        {
            // tag_latin: ab, zz, Café, xy, "", ab(\0zz), AB, zz(\0\xff)
            using (MdfReader reader = Open(WithCc(10, new double[0], new[] { "zz", null, "Café", "c", null }, Tag)))
            {
                List<MdfSampleValue> values = Column(reader, Tag);
                Assert.Equal(new[] { "ab", "zz", "c", "xy", "", "ab", "AB", "zz" }, values.Select(v => v.TextValue));
                Assert.Equal(new byte[] { (byte)'z', (byte)'z', 0, 0xff, 0, 0 }, values[7].GetByteArrayValue()); // input bytes kept
                Assert.Equal(new byte[] { (byte)'c' }, values[2].GetByteArrayValue());
            }
        }

        [Fact]
        public void RawStaysTheStoredValue()
        {
            using (MdfReader reader = Open(WithCc(7, new[] { 0.0 }, new[] { "z", "d" })))
            {
                Assert.Equal(StateRaw, Column(reader, State, MdfValueRepresentation.Raw).Select(v => v.UnsignedValue));
            }
            using (MdfReader reader = Open(WithCc(9, new[] { 1.0, 2 }, new[] { "idle" }, Mode)))
            {
                Assert.Equal("idle", Column(reader, Mode, MdfValueRepresentation.Raw)[0].TextValue);
            }
        }

        [Theory]
        [InlineData(9)]
        [InlineData(10)]
        public void TextInputConversionsOnNumericChannelsRefusePhysicalOnly(int kind)
        {
            byte[] bytes = kind == 9 ? WithCc(9, new[] { 1.0, 2 }, new[] { "a" }) : WithCc(10, new double[0], new[] { "a", "b", "c" });
            using (MdfReader reader = Open(bytes))
            {
                MdfChannelGroup group = Group(reader);
                MdfChannel channel = group.Channels[State];
                Assert.True(channel.CanReadRaw);
                Assert.False(channel.CanReadPhysical);
                Assert.NotEqual(MdfConversionKind.Unsupported, channel.Conversion.Kind);
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { State }, representation: MdfValueRepresentation.Physical));
                Assert.Equal(StateRaw, Column(reader, State, MdfValueRepresentation.Raw).Select(v => v.UnsignedValue));
            }
        }

        [Theory]
        [InlineData(7)]
        [InlineData(8)]
        public void NumericInputConversionsOnTextChannelsStayUnsupported(int kind)
        {
            byte[] bytes = kind == 7 ? WithCc(7, new[] { 1.0 }, new[] { "a", "b" }, Mode) : WithCc(8, new[] { 0.0, 1 }, new[] { "a", "b" }, Mode);
            using (MdfReader reader = Open(bytes))
            {
                MdfChannel channel = Group(reader).Channels[Mode];
                Assert.False(channel.CanReadRaw);
                Assert.Equal("Text and byte-array channels support only identity conversion.", channel.UnsupportedReason);
            }
        }

        [Theory]
        [InlineData(7, new[] { 2.0, 1 }, 3)]                         // decreasing keys
        [InlineData(7, new[] { 1.0, 1 }, 3)]                         // equal keys
        [InlineData(7, new[] { double.NaN }, 2)]
        [InlineData(8, new[] { 3.0, 1 }, 2)]                         // lower > upper
        [InlineData(8, new[] { 0.0, 5, 4, 9 }, 3)]                   // overlapping
        [InlineData(8, new[] { 0.0, double.PositiveInfinity }, 2)]
        public void InvalidKeysAreUnsupportedButRawStaysReadable(int kind, double[] values, int references)
        {
            using (MdfReader reader = Open(WithCc(kind, values, Enumerable.Range(0, references).Select(i => "t" + i).ToArray())))
            {
                MdfChannel channel = Group(reader).Channels[State];
                Assert.Equal(MdfConversionKind.Unsupported, channel.Conversion.Kind);
                Assert.Empty(channel.Conversion.Texts);
                Assert.Empty(channel.Conversion.Parameters);
                Assert.True(channel.CanReadRaw);
                Assert.False(channel.CanReadPhysical);
            }
        }

        [Fact]
        public void NestedConversionsAndNilKeysAreUnsupported()
        {
            Assert.Equal(MdfConversionKind.Unsupported, KindOf(WithCc(7, new[] { 1.0 }, new[] { "a", "#CC" })));
            Assert.Equal(MdfConversionKind.Unsupported, KindOf(WithCc(9, new[] { 1.0, 2 }, new string[] { null }, Mode), Mode));
            Assert.Equal(MdfConversionKind.Unsupported, KindOf(WithCc(10, new double[0], new[] { "a", "b", "#CC" }, Mode), Mode));
            Assert.Equal(MdfConversionKind.Unsupported, KindOf(WithCc(10, new double[0], new[] { null, "b", "c" }, Mode), Mode));
            Assert.Equal(MdfConversionKind.TextToText, KindOf(WithCc(10, new double[0], new[] { "a", null, null }, Mode), Mode));
            Assert.Equal(MdfConversionKind.TextToValue, KindOf(WithCc(9, new[] { 1.0, 2, 3 }, new[] { "a", "a" }, Mode), Mode)); // duplicate keys: first wins
        }

        private static MdfConversionKind KindOf(byte[] bytes, int index = State)
        {
            using (MdfReader reader = Open(bytes)) { return Group(reader).Channels[index].Conversion.Kind; }
        }

        [Fact]
        public void ReferencesMustBeTextOrConversionBlocks()
        {
            using (MdfReader reader = Open(WithCc(7, new[] { 1.0 }, new[] { "a", "#HD" })))
            {
                Assert.Equal("MDF005", Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
            }
        }

        [Theory]
        [InlineData(7, 1, 1)]
        [InlineData(7, 1, 3)]
        [InlineData(8, 1, 2)]
        [InlineData(8, 2, 1)]
        [InlineData(8, 2, 3)]
        [InlineData(9, 1, 1)]
        [InlineData(9, 3, 1)]
        [InlineData(10, 1, 3)]
        [InlineData(10, 0, 2)]
        [InlineData(10, 0, 0)]
        public void IncompatibleCountsAreFormatErrors(int kind, int values, int references)
        {
            byte[] bytes = WithCc(kind, Enumerable.Range(0, values).Select(i => (double)i).ToArray(),
                Enumerable.Range(0, references).Select(i => "t" + i).ToArray());
            using (MdfReader reader = Open(bytes))
            {
                Assert.Equal("MDF013", Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
            }
        }

        [Fact]
        public void VirtualAndArrayChannelsConvertToText()
        {
            // m5o-virtual410 channel 2: record index 0..5.
            Assert.Equal(new[] { "a", "b", "-", "-", "-", "-" }, Texts(WithCc(7, new[] { 0.0, 1 }, new[] { "a", "b", "-" }, 2, "m5o-virtual410"), 2));
            byte[] bytes = WithCc(8, new[] { 0.0, 127, 128, 255 }, new[] { "low", "high", "none" }, 2, "m5d-arrays410");
            using (MdfReader reader = Open(bytes))
            {
                MdfChannelGroup group = Group(reader);
                MdfSampleBatch raw = reader.ReadSamples(group, new[] { 2 }).First();
                MdfSampleBatch physical = reader.ReadSamples(group, new[] { 2 }, representation: MdfValueRepresentation.Physical).First();
                for (int row = 0; row < raw.SampleCount; row++)
                for (ulong element = 0; element < group.Channels[2].Array.ElementCount; element++)
                {
                    Assert.Equal(raw.GetValue(row, 0, element).UnsignedValue < 128 ? "low" : "high", physical.GetValue(row, 0, element).TextValue);
                }
            }
        }

        [Fact]
        public void LongTextOutputsAreChargedToTheBatchBudget()
        {
            string big = new string('x', 100000);
            byte[] bytes = WithCc(7, new[] { 0.0 }, new[] { big, "d" });
            using (MdfReader reader = Open(bytes, new MdfReaderOptions { MaximumSampleBatchBytes = 250000 }))
            {
                MdfChannelGroup group = Group(reader);
                Assert.Equal(8, reader.ReadSamples(group, new[] { State }).Sum(b => b.SampleCount));
                Assert.Throws<MdfResourceLimitException>(() => reader.ReadSamples(group, new[] { State }, representation: MdfValueRepresentation.Physical).ToList());
            }
            using (MdfReader reader = Open(bytes, new MdfReaderOptions { MaximumSampleBatchBytes = 700000 }))
            {
                MdfSampleBatch[] batches = reader.ReadSamples(Group(reader), new[] { State }, representation: MdfValueRepresentation.Physical).ToArray();
                Assert.True(batches.Length > 1);
                Assert.Equal(big, batches[0].GetValue(0, 0).TextValue);
            }
        }

        [Fact]
        public void MetadataCountsReferencesAgainstTheBudget()
        {
            string[] texts = Enumerable.Range(0, 3001).Select(i => "t" + i).ToArray();
            byte[] bytes = WithCc(7, Enumerable.Range(0, 3000).Select(i => (double)i).ToArray(), texts);
            using (MdfReader reader = Open(bytes, new MdfReaderOptions { MaximumMetadataBytes = 400000 }))
            {
                Assert.Equal("MaximumMetadataBytes", Assert.Throws<MdfResourceLimitException>(() => reader.ReadMetadata()).LimitName);
            }
            Assert.Equal(new[] { "t0", "t1", "t2", "t3", "t5", "t255", "t1", "t0" }, Texts(bytes));
        }

        // MDF 3 TABX/RTABX: literal edits of the frozen m6b-text330 CC blocks (1 tabx_u8, 2 tabx_f64, 3 rtabx_u16, 4 rtabx_f64).
        private const string File330 = "m6b-text330";

        private static int Mdf3Cc(byte[] bytes, int index)
        {
            int dg = (int)BitConverter.ToUInt32(bytes, 68);
            int cn = (int)BitConverter.ToUInt32(bytes, (int)BitConverter.ToUInt32(bytes, dg + 8) + 8);
            for (int i = 0; i < index; i++) { cn = (int)BitConverter.ToUInt32(bytes, cn + 4); }
            return (int)BitConverter.ToUInt32(bytes, cn + 8);
        }

        [Fact]
        public void Mdf3TabxAndRtabxExposeKindsTextsAndRanges()
        {
            using (MdfReader reader = Open(FixtureCorpus.Read(File330)))
            {
                MdfChannelGroup group = Group(reader);
                Assert.Equal(new[] { MdfConversionKind.Identity, MdfConversionKind.ValueToText, MdfConversionKind.ValueToText,
                    MdfConversionKind.RangeToText, MdfConversionKind.RangeToText }, group.Channels.Select(c => c.Conversion.Kind));
                Assert.Equal(new[] { "OFF", "ON", "Caf\u00e9", "", "" }, group.Channels[1].Conversion.Texts);
                Assert.Equal(new[] { 0.0, 1, 2, 5 }, group.Channels[1].Conversion.Parameters);
                Assert.Equal(new[] { "low", "mid", "twenty", null, "out" }, group.Channels[3].Conversion.Texts);
                Assert.Equal(new[] { 0.0, 9, 10, 19, 20, 20, 21, 30 }, group.Channels[3].Conversion.Parameters);
                Assert.All(group.Channels, c => Assert.True(c.CanReadPhysical));
                Assert.Equal(new[] { "low", "low", "mid", "mid", "twenty", "", "out" }, Column(reader, 3).Select(v => v.TextValue));
                Assert.Equal(new[] { "low", "out", "mid", "mid", "out", "", "out" }, Column(reader, 4).Select(v => v.TextValue));
                Assert.Equal(new ulong[] { 0, 9, 10, 19, 20, 25, 65535 }, Column(reader, 3, MdfValueRepresentation.Raw).Select(v => v.UnsignedValue));
            }
        }

        [Fact]
        public void Mdf3TabxKeyEditChangesTheLookup()
        {
            byte[] bytes = FixtureCorpus.Read(File330);
            M3Wire.Put(bytes, Mdf3Cc(bytes, 1) + 46 + 3 * 40, (ulong)BitConverter.DoubleToInt64Bits(255.0)); // key 5 -> 255, text ""
            M3Wire.Put(bytes, Mdf3Cc(bytes, 1) + 46 + 3 * 40 + 8, 0x4B4F, 2);                               // text "OK"
            Assert.Equal(new[] { "OFF", "ON", "Caf\u00e9", "", "", "OK", "ON" }, Texts(bytes, 1));
        }

        [Fact]
        public void Mdf3NilDefaultAndUnsortedKeys()
        {
            byte[] bytes = FixtureCorpus.Read(File330);
            M3Wire.Put(bytes, Mdf3Cc(bytes, 3) + 46 + 16, 0, 4);                                                 // default link -> NIL
            M3Wire.Put(bytes, Mdf3Cc(bytes, 2) + 46 + 40, (ulong)BitConverter.DoubleToInt64Bits(-1.0));     // keys -0.5, -1, 0.5
            Assert.Equal(new[] { "low", "low", "mid", "mid", "twenty", "", "" }, Texts(bytes, 3));
            using (MdfReader reader = Open(bytes))
            {
                MdfChannelGroup group = Group(reader);
                Assert.Equal(new[] { "low", "mid", "twenty", null, null }, group.Channels[3].Conversion.Texts);
                Assert.Equal(MdfConversionKind.Unsupported, group.Channels[2].Conversion.Kind);
                Assert.False(group.Channels[2].CanReadPhysical);
                Assert.Equal(0.5, Column(reader, 2, MdfValueRepresentation.Raw)[1].FloatingPointValue);
            }
        }

        [Theory]
        [InlineData(1, 44, 6)]   // TABX entries beyond the block
        [InlineData(3, 44, 0)]   // RTABX without the default entry
        [InlineData(3, 44, 3)]   // RTABX count shorter than the block
        [InlineData(1, 2, 300)]  // block length not 46 + 40n
        public void Mdf3IncompatibleTextShapesAreFormatErrors(int channel, int field, int value)
        {
            byte[] bytes = FixtureCorpus.Read(File330);
            M3Wire.Put(bytes, Mdf3Cc(bytes, channel) + field, (ulong)value, 2);
            using (MdfReader reader = Open(bytes))
            {
                Assert.Throws<MdfFormatException>(() => reader.ReadMetadata());
            }
        }

        [Fact]
        public void Mdf3RtabxLinksMustReferenceTextBlocks()
        {
            byte[] bytes = FixtureCorpus.Read(File330);
            M3Wire.Put(bytes, Mdf3Cc(bytes, 3) + 46 + 20 + 16, 64, 4); // entry 1 -> HD
            using (MdfReader reader = Open(bytes))
            {
                Assert.Throws<MdfFormatException>(() => reader.ReadMetadata());
            }
        }
    }
}
