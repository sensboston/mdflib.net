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
    // literal bitfield CC blocks appended to the frozen m6b/m6c/m5 files; expected values are literal.
    public sealed class BitfieldConversionTests
    {
        private const string File410 = "m6b-text410"; // 8 rows: 1 state_u8, 2 code_i16, 3 level_f64, 6 mode_utf8
        private const int State = 1, Code = 2, Level = 3, Mode = 6;
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

        private static byte[] Payload(int kind, int references, int values)
        {
            byte[] payload = new byte[24 + 8 * values];
            payload[0] = (byte)kind;
            M3Wire.Put(payload, 4, (ulong)references, 2);
            M3Wire.Put(payload, 6, (ulong)values, 2);
            return payload;
        }

        /// <summary>Appends a value (7) or range (8) to text CC; texts null = NIL; name null = no cc_tx_name.</summary>
        private static ulong Part(ref byte[] bytes, int kind, string name, double[] values, string[] texts)
        {
            ulong[] links = new ulong[4 + texts.Length];
            if (name != null) { links[0] = Tx(ref bytes, name); }
            for (int i = 0; i < texts.Length; i++) { links[4 + i] = texts[i] == null ? 0 : Tx(ref bytes, texts[i]); }
            byte[] payload = Payload(kind, texts.Length, values.Length);
            for (int i = 0; i < values.Length; i++) { M3Wire.Put(payload, 24 + 8 * i, (ulong)BitConverter.DoubleToInt64Bits(values[i])); }
            return (ulong)M5Wire.Add(ref bytes, "CC", links, payload);
        }

        private static int Bitfield(ref byte[] bytes, ulong[] masks, ulong[] parts)
        {
            ulong[] links = new ulong[4 + parts.Length];
            System.Array.Copy(parts, 0, links, 4, parts.Length);
            byte[] payload = Payload(11, parts.Length, masks.Length);
            for (int i = 0; i < masks.Length; i++) { M3Wire.Put(payload, 24 + 8 * i, masks[i]); }
            return M5Wire.Add(ref bytes, "CC", links, payload);
        }

        // Takes the array by ref: the CC argument appends to it, so a by-value array argument would be evaluated stale first.
        private static void Attach(ref byte[] bytes, int channel, int cc)
        {
            M3Wire.Put(bytes, M3Wire.Link(M5DWire.Cn(bytes, channel), 4), (ulong)cc);
        }

        /// <summary>masks 0x01 (unnamed b0/NIL), 0x02 (named b1: 0 clr, 2 set) and 0xF0 (named hi: [16, 240] any, NIL default).</summary>
        private static byte[] Standard(int channel = State, string id = File410)
        {
            byte[] bytes = FixtureCorpus.Read(id);
            ulong b0 = Part(ref bytes, 7, null, new[] { 1.0 }, new[] { "b0", null });
            ulong b1 = Part(ref bytes, 7, "b1", new[] { 0.0, 2 }, new[] { "clr", "set", null });
            ulong hi = Part(ref bytes, 8, "hi", new[] { 16.0, 240 }, new[] { "any", null });
            Attach(ref bytes, channel, Bitfield(ref bytes, new ulong[] { 0x01, 0x02, 0xF0 }, new[] { b0, b1, hi }));
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
                Assert.All(values, v => Assert.Equal(Encoding.UTF8.GetBytes(v.TextValue), v.GetByteArrayValue()));
                return values.Select(v => v.TextValue).ToArray();
            }
        }

        private static MdfChannel Channel(byte[] bytes, int index = State)
        {
            using (MdfReader reader = Open(bytes)) { return Group(reader).Channels[index]; }
        }

        [Fact]
        public void PartsJoinNamedAndUnnamedTextsInMaskOrder()
        {
            // raw 0, 1, 2, 3, 5, 255, 1, 0
            Assert.Equal(new[] { "b1=clr|hi=", "b0|b1=clr|hi=", "b1=set|hi=", "b0|b1=set|hi=", "b0|b1=clr|hi=", "b0|b1=set|hi=any",
                "b0|b1=clr|hi=", "b1=clr|hi=" }, Texts(Standard()));
        }

        [Fact]
        public void CatalogPublishesMasksAndPartConversions()
        {
            MdfChannel channel = Channel(Standard());
            MdfChannelConversion conversion = channel.Conversion;
            Assert.Equal(MdfConversionKind.BitfieldToText, conversion.Kind);
            Assert.True(channel.CanReadPhysical);
            Assert.Equal(new ulong[] { 0x01, 0x02, 0xF0 }, conversion.BitMasks);
            Assert.Equal(new[] { MdfConversionKind.ValueToText, MdfConversionKind.ValueToText, MdfConversionKind.RangeToText },
                conversion.BitfieldConversions.Select(c => c.Kind));
            Assert.Equal(new[] { string.Empty, "b1", "hi" }, conversion.BitfieldConversions.Select(c => c.Name));
            Assert.Equal(new[] { "clr", "set", null }, conversion.BitfieldConversions[1].Texts);
            Assert.Empty(conversion.Parameters);
            Assert.Empty(conversion.Texts);
            Assert.Null(conversion.Factor);
            Assert.Empty(conversion.BitfieldConversions[0].BitMasks);
            Assert.Empty(conversion.BitfieldConversions[0].BitfieldConversions);
        }

        [Fact]
        public void RawStaysTheStoredValueAndValidityIsKept()
        {
            byte[] bytes = Standard(Code);
            using (MdfReader reader = Open(bytes))
            {
                Assert.Equal(new long[] { -32768, -1, 0, 1, 2, 0, -1, 1 }, Column(reader, Code, MdfValueRepresentation.Raw).Select(v => v.SignedValue));
                List<MdfSampleValue> physical = Column(reader, Code);
                Assert.Equal(new[] { true, true, false, true, true, true, false, true }, physical.Select(v => v.IsValid));
                // -32768 =...8000: b0 0, b1 0, hi 0; -1: every bit set.
                Assert.Equal("b1=clr|hi=", physical[0].TextValue);
                Assert.Equal("b0|b1=set|hi=any", physical[1].TextValue);
            }
        }

        [Fact]
        public void SignedRawValuesAreSignExtendedToSixtyFourBits()
        {
            byte[] bytes = FixtureCorpus.Read(File410);
            ulong sign = Part(ref bytes, 7, "neg", new[] { 9223372036854775808.0 }, new[] { "yes", "no" });
            Attach(ref bytes, Code, Bitfield(ref bytes, new[] { 0x8000000000000000UL }, new[] { sign }));
            Assert.Equal(new[] { "neg=yes", "neg=yes", "neg=no", "neg=no", "neg=no", "neg=no", "neg=yes", "neg=no" }, Texts(bytes, Code));
        }

        [Fact]
        public void EmptyResultIsAnEmptyText()
        {
            byte[] bytes = FixtureCorpus.Read(File410);
            ulong part = Part(ref bytes, 7, null, new[] { 4.0 }, new[] { "four", "" });
            Attach(ref bytes, State, Bitfield(ref bytes, new ulong[] { 0x04 }, new[] { part }));
            Assert.Equal(new[] { "", "", "", "", "four", "four", "", "" }, Texts(bytes));
        }

        [Fact]
        public void SharedPartsAndBitfieldsAreOneObject()
        {
            byte[] bytes = FixtureCorpus.Read("m6c-bits410");
            using (MdfReader reader = Open(bytes))
            {
                MdfChannelGroup group = Group(reader);
                Assert.Same(group.Channels[1].Conversion.BitfieldConversions[1], group.Channels[2].Conversion.BitfieldConversions[2]);
                Assert.Same(group.Channels[1].Conversion.BitfieldConversions[0], group.Channels[3].Conversion.BitfieldConversions[1]);
            }
            byte[] direct = Standard();
            int field = (int)M3Wire.U64(direct, M3Wire.Link(M5DWire.Cn(direct, State), 4));
            ulong b1 = M3Wire.U64(direct, M3Wire.Link(field, 5));
            M3Wire.Put(direct, M3Wire.Link(M5DWire.Cn(direct, Code), 4), b1); // code_i16 uses the b1 part directly
            using (MdfReader reader = Open(direct))
            {
                MdfChannelGroup group = Group(reader);
                Assert.Same(group.Channels[Code].Conversion, group.Channels[State].Conversion.BitfieldConversions[1]);
                Assert.Equal(MdfConversionKind.ValueToText, group.Channels[Code].Conversion.Kind);
            }
        }

        [Theory]
        [InlineData("tx")]
        [InlineData("nil")]
        [InlineData("linear")]
        [InlineData("bitfield")]
        [InlineData("nested")]
        [InlineData("unsorted")]
        public void UnevaluablePartsMakeTheBitfieldUnsupported(string kind)
        {
            byte[] bytes = FixtureCorpus.Read(File410);
            ulong part;
            if (kind == "tx") { part = Tx(ref bytes, "text"); }
            else if (kind == "nil") { part = 0; }
            else if (kind == "linear")
            {
                byte[] payload = Payload(1, 0, 2);
                M3Wire.Put(payload, 32, (ulong)BitConverter.DoubleToInt64Bits(1.0));
                part = (ulong)M5Wire.Add(ref bytes, "CC", new ulong[4], payload);
            }
            else if (kind == "bitfield")
            {
                ulong inner = Part(ref bytes, 7, null, new[] { 1.0 }, new[] { "a", "b" });
                part = (ulong)Bitfield(ref bytes, new ulong[] { 1 }, new[] { inner });
            }
            else if (kind == "nested")
            {
                ulong inner = (ulong)M5Wire.Add(ref bytes, "CC", new ulong[4], new byte[24]);
                ulong[] links = { 0, 0, 0, 0, Tx(ref bytes, "a"), inner };
                byte[] payload = Payload(7, 2, 1);
                M3Wire.Put(payload, 24, (ulong)BitConverter.DoubleToInt64Bits(1.0));
                part = (ulong)M5Wire.Add(ref bytes, "CC", links, payload);
            }
            else { part = Part(ref bytes, 7, null, new[] { 2.0, 1 }, new[] { "a", "b", "c" }); }
            ulong good = Part(ref bytes, 7, null, new[] { 1.0 }, new[] { "one", null });
            Attach(ref bytes, State, Bitfield(ref bytes, new ulong[] { 1, 2 }, new[] { good, part }));
            using (MdfReader reader = Open(bytes))
            {
                MdfChannelGroup group = Group(reader);
                MdfChannel channel = group.Channels[State];
                Assert.Equal(MdfConversionKind.Unsupported, channel.Conversion.Kind);
                Assert.Empty(channel.Conversion.BitMasks);
                Assert.Empty(channel.Conversion.BitfieldConversions);
                Assert.True(channel.CanReadRaw);
                Assert.False(channel.CanReadPhysical);
                Assert.Equal(StateRaw, Column(reader, State, MdfValueRepresentation.Raw).Select(v => v.UnsignedValue));
            }
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(2, 1)]
        [InlineData(1, 2)]
        public void IncompatibleCountsAreFormatErrors(int masks, int references)
        {
            byte[] bytes = FixtureCorpus.Read(File410);
            ulong part = Part(ref bytes, 7, null, new[] { 1.0 }, new[] { "a", "b" });
            ulong[] links = new ulong[4 + references];
            for (int i = 0; i < references; i++) { links[4 + i] = part; }
            byte[] payload = Payload(11, references, masks);
            for (int i = 0; i < masks; i++) { M3Wire.Put(payload, 24 + 8 * i, 1UL << i); }
            Attach(ref bytes, State, M5Wire.Add(ref bytes, "CC", links, payload));
            using (MdfReader reader = Open(bytes))
            {
                Assert.Equal("MDF013", Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
            }
        }

        [Fact]
        public void PartReferencesMustBeTextOrConversionBlocksAndAcyclic()
        {
            byte[] bytes = FixtureCorpus.Read(File410);
            Attach(ref bytes, State, Bitfield(ref bytes, new ulong[] { 1 }, new ulong[] { 64 })); // HD
            using (MdfReader reader = Open(bytes))
            {
                Assert.Equal("MDF005", Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
            }
            bytes = FixtureCorpus.Read(File410);
            int self = Bitfield(ref bytes, new ulong[] { 1 }, new ulong[] { 0 });
            M3Wire.Put(bytes, M3Wire.Link(self, 4), (ulong)self);
            Attach(ref bytes, State, self);
            using (MdfReader reader = Open(bytes))
            {
                Assert.Equal("MDF010", Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
            }
        }

        [Fact]
        public void FloatingPointChannelsRefusePhysicalOnly()
        {
            byte[] bytes = Standard(Level);
            using (MdfReader reader = Open(bytes))
            {
                MdfChannelGroup group = Group(reader);
                MdfChannel channel = group.Channels[Level];
                Assert.Equal(MdfConversionKind.BitfieldToText, channel.Conversion.Kind);
                Assert.True(channel.CanReadRaw);
                Assert.False(channel.CanReadPhysical);
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { Level }, representation: MdfValueRepresentation.Physical));
                Assert.Equal(0.5, Column(reader, Level, MdfValueRepresentation.Raw)[1].FloatingPointValue);
            }
        }

        [Fact]
        public void TextChannelsStayUnsupported()
        {
            MdfChannel channel = Channel(Standard(Mode), Mode);
            Assert.False(channel.CanReadRaw);
            Assert.Equal("Text and byte-array channels support only identity conversion.", channel.UnsupportedReason);
        }

        [Fact]
        public void VirtualAndArrayChannelsConvert()
        {
            // m5o-virtual410 channel 2: record index 0..5.
            Assert.Equal(new[] { "b1=clr|hi=", "b0|b1=clr|hi=", "b1=set|hi=", "b0|b1=set|hi=", "b1=clr|hi=", "b0|b1=clr|hi=" },
                Texts(Standard(2, "m5o-virtual410"), 2));
            byte[] bytes = Standard(2, "m5d-arrays410");
            using (MdfReader reader = Open(bytes))
            {
                MdfChannelGroup group = Group(reader);
                MdfSampleBatch raw = reader.ReadSamples(group, new[] { 2 }).First();
                MdfSampleBatch physical = reader.ReadSamples(group, new[] { 2 }, representation: MdfValueRepresentation.Physical).First();
                for (int row = 0; row < raw.SampleCount; row++)
                for (ulong element = 0; element < group.Channels[2].Array.ElementCount; element++)
                {
                    ulong x = raw.GetValue(row, 0, element).UnsignedValue;
                    string expected = ((x & 1) != 0 ? "b0|" : "") + ((x & 2) != 0 ? "b1=set" : "b1=clr") + ((x & 0xF0) >= 16 ? "|hi=any" : "|hi=");
                    Assert.Equal(expected, physical.GetValue(row, 0, element).TextValue);
                }
            }
        }

        [Fact]
        public void LongPartTextsAreChargedToTheBatchBudget()
        {
            string big = new string('x', 200000);
            byte[] bytes = FixtureCorpus.Read(File410);
            ulong a = Part(ref bytes, 7, "a", new[] { 1.0 }, new[] { big, big });
            ulong b = Part(ref bytes, 7, "b", new[] { 2.0 }, new[] { big, big });
            Attach(ref bytes, State, Bitfield(ref bytes, new ulong[] { 1, 2 }, new[] { a, b }));
            using (MdfReader reader = Open(bytes, new MdfReaderOptions { MaximumSampleBatchBytes = 1000000 }))
            {
                MdfChannelGroup group = Group(reader);
                Assert.Equal(8, reader.ReadSamples(group, new[] { State }).Sum(s => s.SampleCount));
                Assert.Throws<MdfResourceLimitException>(() => reader.ReadSamples(group, new[] { State }, representation: MdfValueRepresentation.Physical).ToList());
            }
            using (MdfReader reader = Open(bytes, new MdfReaderOptions { MaximumSampleBatchBytes = 1500000 }))
            {
                MdfSampleBatch[] batches = reader.ReadSamples(Group(reader), new[] { State }, representation: MdfValueRepresentation.Physical).ToArray();
                Assert.True(batches.Length > 1);
                Assert.Equal("a=" + big + "|b=" + big, batches[0].GetValue(0, 0).TextValue);
            }
        }
    }
}
