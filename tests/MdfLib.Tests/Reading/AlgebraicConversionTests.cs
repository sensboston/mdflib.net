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
    // literal algebraic CC blocks appended to frozen files (MDF 4) or literal edits of m6d-formula330 (MDF 3);
    // expected values are literal binary64 results.
    public sealed class AlgebraicConversionTests
    {
        private const string File410 = "m6b-text410"; // 8 rows: 1 state_u8, 2 code_i16 (rows 2/6 invalid), 3 level_f64, 6 mode_utf8
        private const int State = 1, Code = 2, Level = 3, Mode = 6;
        private static readonly ulong[] StateRaw = { 0, 1, 2, 3, 5, 255, 1, 0 };

        private static MdfReader Open(byte[] bytes) { return MdfReader.Open(new MemoryStream(bytes, false)); }

        private static MdfChannelGroup Group(MdfReader reader) { return reader.ReadMetadata()[0].ChannelGroups[0]; }

        /// <summary>Appends a cc_type 3 CC; formula null = NIL reference, "#CC" = a nested CC, "#HD" = the HD block.</summary>
        private static byte[] WithFormula(string formula, int channel = State, string id = File410, int references = 1, int values = 0)
        {
            byte[] bytes = FixtureCorpus.Read(id);
            ulong[] links = new ulong[4 + references];
            if (references > 0 && formula != null)
            {
                if (formula == "#CC") { links[4] = (ulong)M5Wire.Add(ref bytes, "CC", new ulong[4], new byte[24]); }
                else if (formula == "#HD") { links[4] = 64; }
                else { links[4] = (ulong)M5Wire.Add(ref bytes, "TX", new ulong[0], Encoding.UTF8.GetBytes(formula + "\0")); }
            }
            byte[] payload = new byte[24 + 8 * values];
            payload[0] = 3;
            M3Wire.Put(payload, 4, (ulong)references, 2);
            M3Wire.Put(payload, 6, (ulong)values, 2);
            int cc = M5Wire.Add(ref bytes, "CC", links, payload);
            M3Wire.Put(bytes, M3Wire.Link(M5DWire.Cn(bytes, channel), 4), (ulong)cc);
            return bytes;
        }

        private static List<MdfSampleValue> Column(MdfReader reader, int index, MdfValueRepresentation representation = MdfValueRepresentation.Physical)
        {
            return reader.ReadSamples(Group(reader), new[] { index }, representation: representation)
                .SelectMany(b => Enumerable.Range(0, b.SampleCount).Select(r => b.GetValue(r, 0))).ToList();
        }

        private static double[] Physical(byte[] bytes, int index = State)
        {
            using (MdfReader reader = Open(bytes))
            {
                List<MdfSampleValue> values = Column(reader, index);
                Assert.All(values, v => Assert.Equal(MdfValueKind.FloatingPoint, v.Kind));
                return values.Select(v => v.FloatingPointValue).ToArray();
            }
        }

        private static MdfChannel Channel(byte[] bytes, int index = State)
        {
            using (MdfReader reader = Open(bytes)) { return Group(reader).Channels[index]; }
        }

        [Theory]
        [InlineData("X", 5.0)]
        [InlineData("x1+1", 6.0)]
        [InlineData("X1 * x", 25.0)]
        [InlineData("2^3^2", 512.0)]
        [InlineData("-X^2", -25.0)]
        [InlineData("(-X)^2", 25.0)]
        [InlineData("2**-1", 0.5)]
        [InlineData("-2 ** 2", -4.0)]
        [InlineData("X/2*4", 10.0)]
        [InlineData("X-1-1", 3.0)]
        [InlineData("--+X", 5.0)]
        [InlineData("pow(X, 2)", 25.0)]
        [InlineData("sqrt(X*5)", 5.0)]
        [InlineData("abs(-X)", 5.0)]
        [InlineData("1e1+X", 15.0)]
        [InlineData(".5*X", 2.5)]
        [InlineData("1.5E+1 - X", 10.0)]
        [InlineData("4. * X", 20.0)]
        [InlineData("  X\t*\r\n2 ", 10.0)]
        [InlineData("exp(0)+ln(1)+log(1)+log10(100)", 3.0)]
        [InlineData("arctan(0)+atan(0)+asin(0)+arcsin(0)+acos(1)+arccos(1)+sinh(0)+tanh(0)+cosh(0)+cos(0)+sin(0)+tan(0)", 2.0)]
        [InlineData("2 * (X + (1 - (3 - 2)))", 10.0)]
        public void GrammarEvaluatesInBinary64(string formula, double atFive)
        {
            byte[] bytes = WithFormula(formula);
            MdfChannel channel = Channel(bytes);
            Assert.Equal(MdfConversionKind.Algebraic, channel.Conversion.Kind);
            Assert.Equal(formula, channel.Conversion.Formula);
            Assert.True(channel.CanReadPhysical);
            Assert.Equal(atFive, Physical(bytes)[4]);
        }

        [Fact]
        public void IeeeResultsForPolesDomainsAndOverflow()
        {
            Assert.Equal(new[] { double.PositiveInfinity, 1, 0.5, 1.0 / 3, 0.2, 1.0 / 255, 1, double.PositiveInfinity }, Physical(WithFormula("1/X")));
            Assert.Equal(double.NegativeInfinity, Physical(WithFormula("-1/X"))[0]);
            Assert.True(double.IsNaN(Physical(WithFormula("X/X"))[0]));
            Assert.True(double.IsNaN(Physical(WithFormula("sqrt(-X)"))[1]));
            Assert.Equal(double.NegativeInfinity, Physical(WithFormula("ln(X)"))[0]);
            Assert.Equal(double.PositiveInfinity, Physical(WithFormula("exp(X*3)"))[5]);
            Assert.Equal(double.PositiveInfinity, Physical(WithFormula("10^(X*2)"))[5]);
            Assert.True(double.IsNaN(Physical(WithFormula("asin(X+1)"))[1]));
        }

        [Fact]
        public void ArithmeticFollowsTheWrittenOperationOrder()
        {
            double[] expected = StateRaw.Select(x => ((double)x * 0.1 + 0.2) * 3 - (double)x / 7).ToArray();
            double[] actual = Physical(WithFormula("(X*0.1 + 0.2)*3 - X/7"));
            Assert.Equal(expected.Select(BitConverter.DoubleToInt64Bits), actual.Select(BitConverter.DoubleToInt64Bits));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("X +")]
        [InlineData("X X")]
        [InlineData("(X")]
        [InlineData("X)")]
        [InlineData("foo(X)")]
        [InlineData("X > 0 ? 1 : 0")]
        [InlineData("X % 2")]
        [InlineData("X & 1")]
        [InlineData("Y")]
        [InlineData("x2")]
        [InlineData("sin X")]
        [InlineData("pow(X)")]
        [InlineData("pow(X, 1, 2)")]
        [InlineData("X**")]
        [InlineData("X * * 2")]
        [InlineData("1..2")]
        [InlineData("1e")]
        [InlineData("Sin(X)")]
        [InlineData("PI * X")]
        [InlineData("X\u00b2")]
        public void FormulasOutsideTheGrammarStayUnsupportedButKeepTheirText(string formula)
        {
            byte[] bytes = WithFormula(formula);
            using (MdfReader reader = Open(bytes))
            {
                MdfChannelGroup group = Group(reader);
                MdfChannel channel = group.Channels[State];
                Assert.Equal(MdfConversionKind.Unsupported, channel.Conversion.Kind);
                Assert.Equal(formula, channel.Conversion.Formula);
                Assert.True(channel.CanReadRaw);
                Assert.False(channel.CanReadPhysical);
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { State }, representation: MdfValueRepresentation.Physical));
                Assert.Equal(StateRaw, Column(reader, State, MdfValueRepresentation.Raw).Select(v => v.UnsignedValue));
            }
        }

        [Fact]
        public void NestingNodeAndLengthBoundsAreUnsupported()
        {
            string nested32 = new string('(', 32) + "X" + new string(')', 32);
            string nested33 = new string('(', 33) + "X" + new string(')', 33);
            Assert.Equal(MdfConversionKind.Algebraic, Channel(WithFormula(nested32)).Conversion.Kind);
            Assert.Equal(MdfConversionKind.Unsupported, Channel(WithFormula(nested33)).Conversion.Kind);
            Assert.Equal(MdfConversionKind.Algebraic, Channel(WithFormula(new string('-', 32) + "X")).Conversion.Kind);
            Assert.Equal(MdfConversionKind.Unsupported, Channel(WithFormula(new string('-', 33) + "X")).Conversion.Kind);
            string sum = string.Join("+", Enumerable.Repeat("X", 512)); // 1023 nodes
            Assert.Equal(512.0 * 5, Physical(WithFormula(sum))[4]);
            Assert.Equal(MdfConversionKind.Unsupported, Channel(WithFormula(sum + "+X")).Conversion.Kind); // 1025 nodes
            Assert.Equal(MdfConversionKind.Unsupported, Channel(WithFormula("X" + new string(' ', 4096))).Conversion.Kind);
        }

        [Fact]
        public void NilAndNestedReferencesHaveNoFormula()
        {
            foreach (string reference in new[] { null, "#CC" })
            {
                MdfChannel channel = Channel(WithFormula(reference));
                Assert.Equal(MdfConversionKind.Unsupported, channel.Conversion.Kind);
                Assert.Null(channel.Conversion.Formula);
                Assert.False(channel.CanReadPhysical);
            }
            using (MdfReader reader = Open(WithFormula("#HD")))
            {
                Assert.Equal("MDF005", Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
            }
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(2, 0)]
        [InlineData(1, 1)]
        public void IncompatibleCountsAreFormatErrors(int references, int values)
        {
            using (MdfReader reader = Open(WithFormula("X", references: references, values: values)))
            {
                Assert.Equal("MDF013", Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
            }
        }

        [Fact]
        public void SignedAndFloatingRawValuesKeepValidity()
        {
            byte[] bytes = WithFormula("X*2 + 0.5", Code);
            using (MdfReader reader = Open(bytes))
            {
                List<MdfSampleValue> values = Column(reader, Code);
                Assert.Equal(new[] { -65535.5, -1.5, 0.5, 2.5, 4.5, 0.5, -1.5, 2.5 }, values.Select(v => v.FloatingPointValue));
                Assert.Equal(new[] { true, true, false, true, true, true, false, true }, values.Select(v => v.IsValid));
                Assert.Equal(new long[] { -32768, -1, 0, 1, 2, 0, -1, 1 }, Column(reader, Code, MdfValueRepresentation.Raw).Select(v => v.SignedValue));
            }
            double[] level = Physical(WithFormula("X - 1", Level), Level);
            Assert.Equal(-1.0, level[0]);
            Assert.True(double.IsNaN(level[2]));
            Assert.Equal(double.PositiveInfinity, level[3]);
        }

        [Fact]
        public void VirtualAndArrayChannelsConvert()
        {
            // m5o-virtual410 channel 2: record index 0..5; m5d-arrays410 channel 2: u8 array elements.
            Assert.Equal(new[] { 0.0, 1, 4, 9, 16, 25 }, Physical(WithFormula("X*X", 2, "m5o-virtual410"), 2));
            using (MdfReader reader = Open(WithFormula("X + 0.5", 2, "m5d-arrays410")))
            {
                MdfChannelGroup group = Group(reader);
                MdfSampleBatch raw = reader.ReadSamples(group, new[] { 2 }).First();
                MdfSampleBatch physical = reader.ReadSamples(group, new[] { 2 }, representation: MdfValueRepresentation.Physical).First();
                for (int row = 0; row < raw.SampleCount; row++)
                for (ulong element = 0; element < group.Channels[2].Array.ElementCount; element++)
                {
                    Assert.Equal(raw.GetValue(row, 0, element).UnsignedValue + 0.5, physical.GetValue(row, 0, element).FloatingPointValue);
                }
            }
        }

        [Fact]
        public void TextChannelsStayUnsupported()
        {
            MdfChannel channel = Channel(WithFormula("X", Mode), Mode);
            Assert.False(channel.CanReadRaw);
            Assert.Equal("Text and byte-array channels support only identity conversion.", channel.UnsupportedReason);
        }

        // MDF 3: literal edits of the frozen m6d-formula330 CC blocks (1 lin_u16, 2 quad_i16, 3 exp_f64).
        private const string File330 = "m6d-formula330";

        private static int Mdf3Cc(byte[] bytes, int index)
        {
            int dg = (int)BitConverter.ToUInt32(bytes, 68);
            int cn = (int)BitConverter.ToUInt32(bytes, (int)BitConverter.ToUInt32(bytes, dg + 8) + 8);
            for (int i = 0; i < index; i++) { cn = (int)BitConverter.ToUInt32(bytes, cn + 4); }
            return (int)BitConverter.ToUInt32(bytes, cn + 8);
        }

        private static byte[] Mdf3Formula(string formula, int channel = 1)
        {
            byte[] bytes = FixtureCorpus.Read(File330);
            int start = Mdf3Cc(bytes, channel) + 46;
            for (int i = 0; i < 256; i++) { bytes[start + i] = i < formula.Length ? (byte)formula[i] : (byte)0; }
            return bytes;
        }

        [Fact]
        public void Mdf3FormulaEditsChangeTheEvaluation()
        {
            // lin_u16 raw 0, 1, 2, 100, 65535, 7
            Assert.Equal(new[] { 0.0, 1, 4, 10000, 4294836225, 49 }, Physical(Mdf3Formula("pow(x,2)"), 1));
            Assert.Equal(new[] { double.PositiveInfinity, 1, 0.5, 1.0 / 100, 1.0 / 65535, 1.0 / 7 }, Physical(Mdf3Formula("1/X  "), 1));
            using (MdfReader reader = Open(FixtureCorpus.Read(File330)))
            {
                MdfChannelGroup group = Group(reader);
                Assert.Equal(new[] { "x*2+1", "X1^2 - 3*x1", "exp(x)" }, group.Channels.Skip(1).Select(c => c.Conversion.Formula));
                Assert.All(group.Channels.Skip(1), c => Assert.Equal(MdfConversionKind.Algebraic, c.Conversion.Kind));
                Assert.Null(group.Channels[0].Conversion.Formula);
            }
        }

        [Fact]
        public void Mdf3UnsupportedAndEmptyFormulas()
        {
            MdfChannel channel = Channel(Mdf3Formula("x \u00b5"), 1);
            Assert.Equal(MdfConversionKind.Unsupported, channel.Conversion.Kind);
            Assert.Equal("x \u00b5", channel.Conversion.Formula);
            Assert.True(channel.CanReadRaw);
            Assert.False(channel.CanReadPhysical);
            Assert.Equal(string.Empty, Channel(Mdf3Formula(""), 1).Conversion.Formula);
            byte[] bytes = FixtureCorpus.Read(File330);
            M3Wire.Put(bytes, Mdf3Cc(bytes, 1) + 2, 46, 2); // no formula field at all
            Assert.Equal(MdfConversionKind.Unsupported, Channel(bytes, 1).Conversion.Kind);
            bytes = FixtureCorpus.Read(File330);
            M3Wire.Put(bytes, Mdf3Cc(bytes, 1) + 44, 65535, 2); // the count field is not a length
            Assert.Equal(MdfConversionKind.Algebraic, Channel(bytes, 1).Conversion.Kind);
        }
        // MDF 3 exponential/logarithmic: literal edits of m6d-expo330 channel 1 (expo_a_u16, raw 0, 10, 110, 410, 65535, 1000).
        private static byte[] Expo(int type, params double[] p)
        {
            byte[] bytes = FixtureCorpus.Read("m6d-expo330");
            int cc = Mdf3Cc(bytes, 1);
            M3Wire.Put(bytes, cc + 42, (ulong)type, 2);
            for (int i = 0; i < p.Length; i++) { M3Wire.Put(bytes, cc + 46 + 8 * i, (ulong)BitConverter.DoubleToInt64Bits(p[i]), 8); }
            return bytes;
        }

        [Fact]
        public void ExponentialAndLogarithmicFollowBothParameterLayouts()
        {
            double[] raw = { 0, 10, 110, 410, 65535, 1000 };
            Assert.Equal(raw.Select(Math.Exp), Physical(Expo(7, 1, 1, 0, 0, 0, 1, 0), 1));
            Assert.Equal(raw.Select(x => Math.Log(x) / 2), Physical(Expo(8, 1, 2, 0, 0, 7, 1, 0), 1));
            Assert.Equal(raw.Select(x => Math.Log((4 / (x - 10) - 1) / 0.5) / 3), Physical(Expo(8, 0, 9, 4, 0.5, 3, 1, 10), 1));
            Assert.Equal(raw.Select(x => Math.Exp(((x - 10) * 0.5 - 1) / 100) / 2), Physical(Expo(7, 100, 2, 1, 0, 9, 0.5, 10), 1));
            MdfChannel channel = Channel(Expo(8, 0, 9, 4, 0.5, 3, 1, 10), 1);
            Assert.Equal(MdfConversionKind.Logarithmic, channel.Conversion.Kind);
            Assert.Equal(new[] { 0, 9, 4, 0.5, 3, 1, 10 }, channel.Conversion.Parameters);
            Assert.Null(channel.Conversion.Formula);
        }

        [Theory]
        [InlineData(0.0, 0.0, 1.0)]
        [InlineData(1.0, 1.0, 1.0)]
        [InlineData(1.0, 0.0, double.NaN)]
        [InlineData(1.0, 0.0, double.PositiveInfinity)]
        public void AmbiguousOrNonFiniteParametersStayUnsupported(double p1, double p4, double p2)
        {
            foreach (int type in new[] { 7, 8 })
            {
                MdfChannel channel = Channel(Expo(type, p1, p2, 0, p4, 1, 1, 0), 1);
                Assert.Equal(MdfConversionKind.Unsupported, channel.Conversion.Kind);
                Assert.Empty(channel.Conversion.Parameters);
                Assert.True(channel.CanReadRaw);
                Assert.False(channel.CanReadPhysical);
            }
        }

        [Theory]
        [InlineData(6, 102)]
        [InlineData(8, 102)]
        [InlineData(7, 94)]
        public void ExponentialCountsAndLengthsAreFormatErrors(int count, int length)
        {
            byte[] bytes = FixtureCorpus.Read("m6d-expo330");
            int cc = Mdf3Cc(bytes, 1);
            M3Wire.Put(bytes, cc + 44, (ulong)count, 2);
            M3Wire.Put(bytes, cc + 2, (ulong)length, 2);
            using (MdfReader reader = Open(bytes))
            {
                Assert.Equal("MDF013", Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
            }
        }
    }
}
