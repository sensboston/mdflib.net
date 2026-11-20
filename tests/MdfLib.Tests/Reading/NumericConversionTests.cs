// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MdfLib.Tests.TestDoubles;
using MdfLib.TestSupport;
using Xunit;

namespace MdfLib.Tests.Reading
{
    // literal CC blocks appended to the frozen stdlib files and linked to existing
    // channels; expected values are literal numbers, never computed by the library.
    public sealed class NumericConversionTests
    {
        private const string Mdf4 = "m6a-conv410"; // 9 rows; 5 tabi_f64 (-20,-10,-5,0,3.3,10,25,40,1e300); 11 n u16 (0..7, 65535)
        private const string Mdf3 = "m6a-conv330"; // 7 rows; 1 rat u16, 2 tabi i16, 3 tab u8, 4 poly f64, 5 linear i16
        private const int Floats = 5, Counter = 11;
        private static readonly double[] CounterRaw = { 0, 1, 2, 3, 4, 5, 6, 7, 65535 };

        private static MdfReader Open(byte[] bytes) { return MdfReader.Open(new MemoryStream(bytes, false)); }
        private static MdfChannelGroup Group(MdfReader reader) { return reader.ReadMetadata()[0].ChannelGroups[0]; }

        private static byte[] WithCc(int kind, double[] values, int channel = Counter, string id = Mdf4, int references = 0)
        {
            byte[] bytes = FixtureCorpus.Read(id);
            byte[] payload = new byte[24 + values.Length * 8];
            payload[0] = (byte)kind;
            M3Wire.Put(payload, 4, (ulong)references, 2);
            M3Wire.Put(payload, 6, (ulong)values.Length, 2);
            for (int i = 0; i < values.Length; i++) { M3Wire.Put(payload, 24 + 8 * i, (ulong)BitConverter.DoubleToInt64Bits(values[i])); }
            int cc = M5Wire.Add(ref bytes, "CC", new ulong[4 + references], payload);
            M3Wire.Put(bytes, M3Wire.Link(M5DWire.Cn(bytes, channel), 4), (ulong)cc);
            return bytes;
        }

        private static List<MdfSampleValue> Column(MdfReader reader, int index, MdfValueRepresentation representation)
        {
            return reader.ReadSamples(Group(reader), new[] { index }, representation: representation)
                .SelectMany(b => Enumerable.Range(0, b.SampleCount).Select(r => b.GetValue(r, 0))).ToList();
        }

        private static double[] Physical(byte[] bytes, int index = Counter)
        {
            using (MdfReader reader = Open(bytes))
            {
                List<MdfSampleValue> values = Column(reader, index, MdfValueRepresentation.Physical);
                Assert.All(values, v => Assert.Equal(MdfValueKind.FloatingPoint, v.Kind));
                return values.Select(v => v.FloatingPointValue).ToArray();
            }
        }

        private static void Bitwise(double[] expected, double[] actual)
        {
            Assert.Equal(expected.Select(BitConverter.DoubleToInt64Bits), actual.Select(BitConverter.DoubleToInt64Bits));
        }

        [Fact]
        public void RationalEvaluatesTheFullFormulaInBinary64()
        {
            Bitwise(new[] { 0, 1, 4, 9, 16, 25, 36, 49, 4294836225.0 }, Physical(WithCc(2, new[] { 1.0, 0, 0, 0, 0, 1 })));
            Bitwise(new[] { -0.75, 0.25, 1.25, 2.25, 3.25, 4.25, 5.25, 6.25, 65534.25 }, Physical(WithCc(2, new[] { 0, 2.0, -1.5, 0, 0, 2 })));
        }

        [Fact]
        public void RationalZeroDenominatorsAreIeeeAndKeepValidity()
        {
            // x / (x - 2): 0 -> -0, 2 -> +Infinity; (x - 2) / (x - 2) at 2 -> NaN.
            double[] pole = Physical(WithCc(2, new[] { 0, 1.0, 0, 0, 1, -2 }));
            Assert.Equal(BitConverter.DoubleToInt64Bits(-0.0), BitConverter.DoubleToInt64Bits(pole[0]));
            Assert.Equal(double.PositiveInfinity, pole[2]);
            Assert.Equal(-1.0, pole[1]);
            Assert.True(double.IsNaN(Physical(WithCc(2, new[] { 0, 1.0, -2, 0, 1, -2 }))[2]));
            using (MdfReader reader = Open(WithCc(2, new[] { 0, 1.0, 0, 0, 0, 0 }, 6)))
            {
                List<MdfSampleValue> values = Column(reader, 6, MdfValueRepresentation.Physical);
                Assert.Equal(new[] { true, false, true, true, false, true, true, false, true }, values.Select(v => v.IsValid));
                Assert.Equal(double.NegativeInfinity, values[0].FloatingPointValue); // -2147483648 / 0
            }
        }

        [Fact]
        public void TableInterpolationClampsAndInterpolates()
        {
            Bitwise(new[] { 10, 10, 20, 30, 30, 30, 30, 30, 30.0 }, Physical(WithCc(4, new[] { 1.0, 10, 3, 30 })));
            // tabi_f64 raw: -20, -10, -5, 0, 3.3, 10, 25, 40, 1e300.
            double[] floats = Physical(WithCc(4, new[] { -10.0, -1, 0, 0, 10, 2, 20, 4 }, Floats), Floats);
            Assert.Equal(0.66, floats[4], 12);
            floats[4] = 0;
            Bitwise(new[] { -1, -1, -0.5, 0, 0, 2, 4, 4, 4.0 }, floats);
            Assert.Equal(new[] { 7.0, 7, 7, 7, 7, 7, 7, 7, 7 }, Physical(WithCc(4, new[] { 3.0, 7 })));
        }

        [Fact]
        public void TableSelectsTheNearestKeyAndTheLowerOneAtMidpoints()
        {
            Assert.Equal(new[] { 100.0, 100, 200, 200, 200, 300, 300, 300, 300 }, Physical(WithCc(5, new[] { 0.0, 100, 2, 200, 6, 300 })));
        }

        [Fact]
        public void TablesPropagateNaNRaw()
        {
            byte[] bytes = WithCc(5, new[] { 0.0, 1, 1, 2 }, Floats);
            int record = M3Wire.Payload(bytes, M5DWire.Root(bytes));
            M3Wire.Put(bytes, record + 24, (ulong)BitConverter.DoubleToInt64Bits(double.NaN)); // row 0 tabi_f64 @24
            Assert.True(double.IsNaN(Physical(bytes, Floats)[0]));
            bytes = WithCc(4, new[] { 0.0, 1, 1, 2 }, Floats);
            M3Wire.Put(bytes, record + 24, (ulong)BitConverter.DoubleToInt64Bits(double.NaN));
            Assert.True(double.IsNaN(Physical(bytes, Floats)[0]));
        }

        [Fact]
        public void RangeTablesUseInclusiveIntegerAndHalfOpenFloatingPointRanges()
        {
            double[] ranges = { 1, 3, 7, 3, 5, 9, -1 };
            Assert.Equal(new[] { -1.0, 7, 7, 7, 9, 9, -1, -1, -1 }, Physical(WithCc(6, ranges)));
            Assert.Equal(new[] { 0.0, 1, 1, 2, 2, 0, 0, 0, 0 }, Physical(WithCc(6, new[] { -10.0, 0, 1, 0, 10, 2, 0 }, Floats), Floats));
            Assert.Equal(new[] { -3.0, -3, -3, -3, -3, -3, -3, -3, -3 }, Physical(WithCc(6, new[] { -3.0 })));
            // Zero-width ranges: integers match them (first in wire order), floating point never does.
            Assert.Equal(new[] { 5.0, 5, 5, 5, 6, 5, 5, 5, 5 }, Physical(WithCc(6, new[] { 4.0, 4, 6, 4, 4, 7, 5 })));
        }

        [Fact]
        public void ParametersAreWireOrderedAndImmutable()
        {
            using (MdfReader reader = Open(WithCc(6, new[] { 1.0, 3, 7, -1 })))
            {
                MdfChannelGroup group = Group(reader);
                MdfChannelConversion conversion = group.Channels[Counter].Conversion;
                Assert.Equal(MdfConversionKind.RangeTable, conversion.Kind);
                Assert.Equal(new[] { 1.0, 3, 7, -1 }, conversion.Parameters);
                Assert.Null(conversion.Factor);
                Assert.Null(conversion.Offset);
                Assert.True(group.Channels[Counter].CanReadPhysical);
                Assert.Throws<NotSupportedException>(() => ((IList<double>)conversion.Parameters).Add(1));
                Assert.Throws<NotSupportedException>(() => { ((IList<double>)conversion.Parameters)[0] = 2; });
                Assert.Empty(group.Channels[0].Conversion.Parameters); // identity
            }
            using (MdfReader reader = Open(FixtureCorpus.Read(Mdf3)))
            {
                MdfChannelConversion linear = Group(reader).Channels[5].Conversion;
                Assert.Equal(MdfConversionKind.Linear, linear.Kind);
                Assert.Equal(new[] { -10.0, 0.5 }, linear.Parameters);
                Assert.Equal(0.5, linear.Factor);
                Assert.Equal(-10.0, linear.Offset);
            }
        }

        [Theory]
        [InlineData(2, new[] { 1.0, 0, 0, 0, 0, double.NaN })]
        [InlineData(2, new[] { 1.0, 0, double.PositiveInfinity, 0, 0, 1 })]
        [InlineData(4, new[] { 0.0, 1, 0, 2 })]             // equal keys
        [InlineData(5, new[] { 3.0, 1, 1, 2 })]             // decreasing keys
        [InlineData(5, new[] { 0.0, double.NaN, 1, 2 })]    // non-finite value
        [InlineData(6, new[] { 3.0, 1, 7, -1 })]            // lower > upper
        [InlineData(6, new[] { 0.0, 5, 1, 4, 9, 2, -1 })]   // overlapping ranges
        [InlineData(6, new[] { 0.0, 5, 1, double.NegativeInfinity })]
        public void InvalidParametersAreUnsupportedButRawStaysReadable(int kind, double[] values)
        {
            using (MdfReader reader = Open(WithCc(kind, values)))
            {
                MdfChannelGroup group = Group(reader);
                MdfChannel channel = group.Channels[Counter];
                Assert.Equal(MdfConversionKind.Unsupported, channel.Conversion.Kind);
                Assert.Empty(channel.Conversion.Parameters);
                Assert.True(channel.CanReadRaw);
                Assert.False(channel.CanReadPhysical);
                Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { Counter }, representation: MdfValueRepresentation.Physical));
                Assert.Equal(CounterRaw, Column(reader, Counter, MdfValueRepresentation.Raw).Select(v => (double)v.UnsignedValue));
            }
        }

        [Theory]
        [InlineData(2, 5, 0)]
        [InlineData(2, 7, 0)]
        [InlineData(4, 0, 0)]
        [InlineData(4, 3, 0)]
        [InlineData(5, 1, 0)]
        [InlineData(6, 0, 0)]
        [InlineData(6, 3, 0)]
        [InlineData(6, 5, 0)]
        [InlineData(5, 4, 1)]
        [InlineData(2, 6, 1)]
        public void IncompatibleCountsOrReferencesAreFormatErrors(int kind, int count, int references)
        {
            byte[] bytes = WithCc(kind, Enumerable.Range(0, count).Select(i => (double)i).ToArray(), references: references);
            using (MdfReader reader = Open(bytes))
            {
                Assert.Equal("MDF013", Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
            }
        }

        [Theory]
        [InlineData(200)]
        [InlineData(12)]
        public void OtherConversionTypesStayUnsupported(int kind)
        {
            using (MdfReader reader = Open(WithCc(kind, new[] { 1.0, 2, 3 })))
            {
                MdfChannel channel = Group(reader).Channels[Counter];
                Assert.Equal(MdfConversionKind.Unsupported, channel.Conversion.Kind);
                Assert.False(channel.CanReadPhysical);
            }
        }

        [Fact]
        public void VirtualChannelsConvertTheirRecordIndexAsAnInteger()
        {
            Assert.Equal(new[] { -5.0, 7, 7, -5, 9, -5 }, Physical(WithCc(6, new[] { 1.0, 2, 7, 4, 4, 9, -5 }, 2, "m5o-virtual410"), 2));
            Assert.Equal(new[] { 0.0, 1, 4, 9, 16, 25 }, Physical(WithCc(2, new[] { 1.0, 0, 0, 0, 0, 1 }, 2, "m5o-virtual410"), 2));
        }

        [Fact]
        public void ArrayElementsAreConvertedIndividually()
        {
            byte[] bytes = WithCc(2, new[] { 1.0, 0, 0, 0, 0, 1 }, 2, "m5d-arrays410");
            using (MdfReader reader = Open(bytes))
            {
                MdfChannelGroup group = Group(reader);
                ulong elements = group.Channels[2].Array.ElementCount;
                Assert.True(elements > 1);
                MdfSampleBatch raw = reader.ReadSamples(group, new[] { 2 }).First();
                MdfSampleBatch physical = reader.ReadSamples(group, new[] { 2 }, representation: MdfValueRepresentation.Physical).First();
                for (int row = 0; row < raw.SampleCount; row++)
                for (ulong element = 0; element < elements; element++)
                {
                    MdfSampleValue value = raw.GetValue(row, 0, element);
                    double x = value.Kind == MdfValueKind.UnsignedInteger ? value.UnsignedValue
                        : value.Kind == MdfValueKind.SignedInteger ? value.SignedValue : value.FloatingPointValue;
                    Assert.Equal(x * x, physical.GetValue(row, 0, element).FloatingPointValue);
                    Assert.Equal(value.IsValid, physical.GetValue(row, 0, element).IsValid);
                }
            }
        }

        [Fact]
        public void SharedConversionBlocksAreOneInstance()
        {
            using (MdfReader reader = Open(FixtureCorpus.Read("m6a-conv420")))
            {
                MdfChannelGroup group = Group(reader);
                Assert.Same(group.Channels[1].Conversion, group.Channels[2].Conversion);
                Assert.Equal(MdfConversionKind.TableInterpolation, group.Channels[1].Conversion.Kind);
            }
        }

        [Fact]
        public void LargeTablesAreChargedToTheMetadataBudget()
        {
            double[] values = Enumerable.Range(0, 20000).Select(i => (double)i).ToArray();
            byte[] bytes = WithCc(4, values);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false), options: new MdfReaderOptions { MaximumMetadataBytes = 300000 }))
            {
                Assert.Equal("MaximumMetadataBytes", Assert.Throws<MdfResourceLimitException>(() => reader.ReadMetadata()).LimitName);
            }
            Assert.Equal(new[] { 1.0, 2, 3, 4, 5, 6, 7, 8, 19999 }, Physical(bytes));
        }

        // MDF 3: literal edits of the frozen 3.30 CC blocks.
        private static int Mdf3Cc(byte[] bytes, int index)
        {
            int dg = (int)BitConverter.ToUInt32(bytes, 68);
            int cn = (int)BitConverter.ToUInt32(bytes, (int)BitConverter.ToUInt32(bytes, dg + 8) + 8);
            for (int i = 0; i < index; i++) { cn = (int)BitConverter.ToUInt32(bytes, cn + 4); }
            return (int)BitConverter.ToUInt32(bytes, cn + 8);
        }

        [Fact]
        public void Mdf3KindsAndPolynomialPole()
        {
            using (MdfReader reader = Open(FixtureCorpus.Read(Mdf3)))
            {
                MdfChannelGroup group = Group(reader);
                Assert.Equal(new[] { MdfConversionKind.Identity, MdfConversionKind.Rational, MdfConversionKind.TableInterpolation,
                    MdfConversionKind.Table, MdfConversionKind.Polynomial, MdfConversionKind.Linear }, group.Channels.Select(c => c.Conversion.Kind));
                Assert.Equal(new[] { 2.0, 10, 0.5, 3, 1, 1 }, group.Channels[4].Conversion.Parameters);
                List<MdfSampleValue> poly = Column(reader, 4, MdfValueRepresentation.Physical);
                Assert.Equal(-5.0, poly[1].FloatingPointValue);        // x = 2: (10 - 0) / (0 - 2)
                Assert.Equal(double.NegativeInfinity, poly[6].FloatingPointValue); // x = 6: -2 / 0
            }
        }

        [Fact]
        public void Mdf3TableTypeSwitchChangesTheEvaluation()
        {
            byte[] bytes = FixtureCorpus.Read(Mdf3);
            M3Wire.Put(bytes, Mdf3Cc(bytes, 3) + 42, 1, 2); // TAB -> TABI on u8 (0, 5, 6, 15, 35, 50, 255)
            Assert.Equal(new[] { 1.0, 1.5, 1.6, 2.5, 3.5, 4, 4 }, Physical(bytes, 3).Select(v => Math.Round(v, 12)));
        }

        [Theory]
        [InlineData(1, 44, 5)]   // rational count 5
        [InlineData(4, 44, 7)]   // polynomial count 7
        [InlineData(2, 44, 0)]   // empty table
        [InlineData(2, 44, 5)]   // pairs beyond the block
        [InlineData(3, 2, 200)]  // block length not 46 + 16n
        public void Mdf3IncompatibleShapesAreFormatErrors(int channel, int field, int value)
        {
            byte[] bytes = FixtureCorpus.Read(Mdf3);
            M3Wire.Put(bytes, Mdf3Cc(bytes, channel) + field, (ulong)value, 2);
            using (MdfReader reader = Open(bytes))
            {
                Assert.Throws<MdfFormatException>(() => reader.ReadMetadata());
            }
        }

        [Fact]
        public void Mdf3UnsortedOrNonFiniteParametersAreUnsupported()
        {
            byte[] bytes = FixtureCorpus.Read(Mdf3);
            M3Wire.Put(bytes, Mdf3Cc(bytes, 2) + 46 + 16, (ulong)BitConverter.DoubleToInt64Bits(-50.0)); // second key < first
            M3Wire.Put(bytes, Mdf3Cc(bytes, 1) + 46, (ulong)BitConverter.DoubleToInt64Bits(double.NaN));
            using (MdfReader reader = Open(bytes))
            {
                MdfChannelGroup group = Group(reader);
                Assert.Equal(MdfConversionKind.Unsupported, group.Channels[1].Conversion.Kind);
                Assert.Equal(MdfConversionKind.Unsupported, group.Channels[2].Conversion.Kind);
                Assert.False(group.Channels[2].CanReadPhysical);
                Assert.Equal(new long[] { -32768, -10, -7, 0, 5, 40, 32767 }, Column(reader, 2, MdfValueRepresentation.Raw).Select(v => v.SignedValue));
                Assert.True(group.Channels[3].CanReadPhysical);
            }
        }
    }
}
