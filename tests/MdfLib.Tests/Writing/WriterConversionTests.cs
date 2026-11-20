// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Writing
{
    // nonlinear writer conversions, read back through the reader's catalog and physical evaluation.
    public sealed class WriterConversionTests
    {
        private const int Rows = 5;
        private static readonly string[] Words = { "a", "b", "c", "a", "x" };

        private static MdfChannelDefinition Numeric(string name, MdfConversionDefinition conversion, string unit = "")
        {
            return new MdfChannelDefinition { Name = name, Unit = unit, ValueKind = MdfValueKind.UnsignedInteger, BitCount = 16, Conversion = conversion };
        }

        private static MdfChannelDefinition Text(string name, MdfConversionDefinition conversion)
        {
            return new MdfChannelDefinition { Name = name, ValueKind = MdfValueKind.Text, BitCount = 64, Conversion = conversion };
        }

        private static byte[] Write(string version, IReadOnlyList<MdfChannelDefinition> channels)
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(output, true, new MdfWriterOptions { Version = version }))
            {
                writer.AddChannel(WriterMultiGroupTests.Master());
                foreach (MdfChannelDefinition channel in channels) { writer.AddChannel(channel); }
                writer.StartMeasurement();
                for (int r = 0; r < Rows; r++)
                {
                    List<MdfSampleValue> row = new List<MdfSampleValue> { MdfSampleValue.FromFloatingPoint(r) };
                    foreach (MdfChannelDefinition channel in channels)
                    {
                        row.Add(channel.ValueKind == MdfValueKind.Text ? MdfSampleValue.FromText(Words[r]) : MdfSampleValue.FromUnsigned((ulong)r));
                    }
                    writer.SaveSample(row);
                }
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
            }
            return output.Snapshot();
        }

        private static List<MdfSampleValue[]> Physical(byte[] file, out MdfChannelGroup group)
        {
            List<MdfSampleValue[]> rows = new List<MdfSampleValue[]>();
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file)))
            {
                Assert.True(MdfValidator.Validate(reader).IsValid);
                group = reader.ReadMetadata()[0].ChannelGroups[0];
                int[] indices = Enumerable.Range(0, group.Channels.Count).ToArray();
                Assert.All(group.Channels, c => Assert.True(c.CanReadPhysical, c.Name));
                foreach (MdfSampleBatch batch in reader.ReadSamples(group, indices, representation: MdfValueRepresentation.Physical))
                {
                    for (int i = 0; i < batch.SampleCount; i++)
                    {
                        rows.Add(indices.Select(c => batch.GetValue(i, c)).ToArray());
                    }
                }
            }
            Assert.Equal(Rows, rows.Count);
            return rows;
        }

        private static double[] Column(List<MdfSampleValue[]> rows, int channel) { return rows.Select(r => r[channel].FloatingPointValue).ToArray(); }
        private static string[] Strings(List<MdfSampleValue[]> rows, int channel) { return rows.Select(r => r[channel].TextValue).ToArray(); }

        [Theory]
        [InlineData("4.10")]
        [InlineData("4.20")]
        [InlineData("3.30")]
        public void NumericConversionsRoundTrip(string version)
        {
            MdfConversionDefinition[] conversions =
            {
                MdfConversionDefinition.Rational(0, 1, 2, 0, 0, 1),
                MdfConversionDefinition.Algebraic("X * 2 + 1"),
                MdfConversionDefinition.Table(new double[] { 0, 4 }, new double[] { 0, 40 }, true),
                MdfConversionDefinition.Table(new double[] { 0, 2, 4 }, new double[] { 100, 200, 300 }, false),
            };
            MdfChannelGroup group;
            List<MdfSampleValue[]> rows = Physical(Write(version, conversions.Select((c, i) => Numeric("n" + i, c, "km")).ToArray()), out group);
            Assert.Equal(new double[] { 2, 3, 4, 5, 6 }, Column(rows, 1));
            Assert.Equal(new double[] { 1, 3, 5, 7, 9 }, Column(rows, 2));
            Assert.Equal(new double[] { 0, 10, 20, 30, 40 }, Column(rows, 3));
            Assert.Equal(new double[] { 100, 100, 200, 200, 300 }, Column(rows, 4));
            for (int i = 0; i < conversions.Length; i++)
            {
                MdfChannelConversion read = group.Channels[i + 1].Conversion;
                Assert.Equal(conversions[i].Kind, read.Kind);
                Assert.Equal(conversions[i].Parameters, read.Parameters);
                Assert.Equal("km", group.Channels[i + 1].Unit);
            }
            Assert.Equal("X * 2 + 1", group.Channels[2].Conversion.Formula);
        }

        [Theory]
        [InlineData("4.10")]
        [InlineData("4.20")]
        public void Mdf4RangeAndTextConversionsRoundTrip(string version)
        {
            MdfChannelDefinition[] channels =
            {
                Numeric("range", MdfConversionDefinition.RangeTable(new double[] { 0, 2 }, new double[] { 1, 3 }, new double[] { 10, 20 }, -1)),
                Numeric("v2t", MdfConversionDefinition.ValueToText(new double[] { 0, 1, 2 }, new[] { "zero", null, "zwei \u00fc" }, "other")),
                Numeric("r2t", MdfConversionDefinition.RangeToText(new double[] { 0, 3 }, new double[] { 2, 3 }, new[] { "low", "three" }, "high")),
                Text("t2v", MdfConversionDefinition.TextToValue(new[] { "a", "b" }, new double[] { 1, 2 }, 0)),
                Text("t2t", MdfConversionDefinition.TextToText(new[] { "a", "b" }, new[] { "A", null }, null)),
            };
            MdfChannelGroup group;
            List<MdfSampleValue[]> rows = Physical(Write(version, channels), out group);
            Assert.Equal(new double[] { 10, 10, 20, 20, -1 }, Column(rows, 1));
            Assert.Equal(new[] { "zero", "", "zwei \u00fc", "other", "other" }, Strings(rows, 2));
            Assert.Equal(new[] { "low", "low", "low", "three", "high" }, Strings(rows, 3));
            Assert.Equal(new double[] { 1, 2, 0, 1, 0 }, Column(rows, 4));
            Assert.Equal(new[] { "A", "b", "c", "A", "x" }, Strings(rows, 5));
            Assert.Equal(new[] { "zero", null, "zwei \u00fc", "other" }, group.Channels[2].Conversion.Texts);
            Assert.Equal(new[] { "a", "A", "b", null, null }, group.Channels[5].Conversion.Texts);
            for (int i = 0; i < channels.Length; i++)
            {
                Assert.Equal(channels[i].Conversion.Kind, group.Channels[i + 1].Conversion.Kind);
                Assert.Equal(channels[i].Conversion.Parameters, group.Channels[i + 1].Conversion.Parameters);
            }
        }

        [Fact]
        public void Mdf3TextTablesRoundTrip()
        {
            MdfChannelDefinition[] channels =
            {
                Numeric("v2t", MdfConversionDefinition.ValueToText(new double[] { 0, 1, 2 }, new[] { "zero", null, new string('t', 31) }, null)),
                Numeric("r2t", MdfConversionDefinition.RangeToText(new double[] { 0, 3 }, new double[] { 2, 3 }, new[] { "low", null }, "high \u00e9")),
                Numeric("r2tNoDefault", MdfConversionDefinition.RangeToText(new double[] { 1 }, new double[] { 1 }, new[] { "one" }, null)),
            };
            MdfChannelGroup group;
            List<MdfSampleValue[]> rows = Physical(Write("3.30", channels), out group);
            Assert.Equal(new[] { "zero", "", new string('t', 31), "", "" }, Strings(rows, 1));
            Assert.Equal(new[] { "low", "low", "low", "", "high \u00e9" }, Strings(rows, 2));
            Assert.Equal(new[] { "zero", "", new string('t', 31), "" }, group.Channels[1].Conversion.Texts);
            Assert.Equal(new[] { "low", null, "high \u00e9" }, group.Channels[2].Conversion.Texts);
            Assert.Equal(new[] { "one", null }, group.Channels[3].Conversion.Texts);
            Assert.Equal(new double[] { 0, 2, 3, 3 }, group.Channels[2].Conversion.Parameters);
        }

        [Fact]
        public void Mdf3OnlyNumericKindsRoundTrip()
        {
            MdfConversionDefinition polynomial = MdfConversionDefinition.Polynomial(-1, 0, 0, -1, 1, 0);
            MdfConversionDefinition exponential = MdfConversionDefinition.Exponential(new double[] { 1, 1, 0, 0, 0, 1, 0 });
            MdfConversionDefinition logarithmic = MdfConversionDefinition.Logarithmic(new double[] { 0, 1, 1, 2, 1, 0, 0.5 });
            using (MdfReader reader = MdfReader.Open(new MemoryStream(Write("3.30", new[] { Numeric("p", polynomial), Numeric("e", exponential), Numeric("l", logarithmic) }))))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                MdfConversionDefinition[] expected = { polynomial, exponential, logarithmic };
                for (int i = 0; i < expected.Length; i++)
                {
                    Assert.Equal(expected[i].Kind, group.Channels[i + 1].Conversion.Kind);
                    Assert.Equal(expected[i].Parameters, group.Channels[i + 1].Conversion.Parameters);
                }
                MdfSampleBatch batch = reader.ReadSamples(group, new[] { 1, 2 }, representation: MdfValueRepresentation.Physical).Single();
                Assert.Equal(new double[] { -1, 0, 1, 2, 3 }, Enumerable.Range(0, Rows).Select(i => batch.GetValue(i, 0).FloatingPointValue));
                Assert.Equal(Math.Exp(3), batch.GetValue(3, 1).FloatingPointValue, 12);
            }
        }

        [Theory]
        [InlineData("4.10", "Polynomial")]
        [InlineData("4.20", "Exponential")]
        [InlineData("4.10", "Logarithmic")]
        [InlineData("3.30", "RangeTable")]
        [InlineData("3.30", "TextToValue")]
        [InlineData("3.30", "TextToText")]
        [InlineData("3.30", "DefaultText")]
        [InlineData("3.30", "LongEntry")]
        [InlineData("3.30", "LongFormula")]
        [InlineData("4.10", "TextOnNumeric")]
        [InlineData("4.10", "NumericOnText")]
        [InlineData("4.10", "ByteArray")]
        public void UnrepresentableConversionsAreRefusedAtAddChannel(string version, string scenario)
        {
            double[] seven = { 1, 1, 0, 0, 0, 1, 0 };
            MdfChannelDefinition channel;
            switch (scenario)
            {
                case "Polynomial": channel = Numeric("x", MdfConversionDefinition.Polynomial(1, 0, 0, 0, 0, 0)); break;
                case "Exponential": channel = Numeric("x", MdfConversionDefinition.Exponential(seven)); break;
                case "Logarithmic": channel = Numeric("x", MdfConversionDefinition.Logarithmic(seven)); break;
                case "RangeTable": channel = Numeric("x", MdfConversionDefinition.RangeTable(new double[] { 0 }, new double[] { 1 }, new double[] { 2 }, 3)); break;
                case "TextToValue": channel = Text("x", MdfConversionDefinition.TextToValue(new[] { "a" }, new double[] { 1 }, 0)); break;
                case "TextToText": channel = Text("x", MdfConversionDefinition.TextToText(new[] { "a" }, new[] { "b" }, null)); break;
                case "DefaultText": channel = Numeric("x", MdfConversionDefinition.ValueToText(new double[] { 0 }, new[] { "a" }, "d")); break;
                case "LongEntry": channel = Numeric("x", MdfConversionDefinition.ValueToText(new double[] { 0 }, new[] { new string('a', 32) }, null)); break;
                case "LongFormula": channel = Numeric("x", MdfConversionDefinition.Algebraic("X" + string.Concat(Enumerable.Repeat("+1", 130)))); break;
                case "TextOnNumeric": channel = Numeric("x", MdfConversionDefinition.TextToText(new[] { "a" }, new[] { "b" }, null)); break;
                case "NumericOnText": channel = Text("x", MdfConversionDefinition.Rational(0, 1, 0, 0, 0, 1)); break;
                default:
                    channel = new MdfChannelDefinition { Name = "x", ValueKind = MdfValueKind.ByteArray, BitCount = 16, Conversion = MdfConversionDefinition.Rational(0, 1, 0, 0, 0, 1) };
                    break;
            }
            using (MdfWriter writer = MdfWriter.Create(new TestWriteStream(), true, new MdfWriterOptions { Version = version }))
            {
                Assert.Throws<NotSupportedException>(() => writer.AddChannel(channel));
                Assert.Equal(0, writer.ChannelCount);
            }
        }

        [Fact]
        public void InvalidChannelCombinationsAreRejected()
        {
            using (MdfWriter writer = MdfWriter.Create(new TestWriteStream(), true))
            {
                MdfChannelDefinition both = Numeric("x", MdfConversionDefinition.Rational(0, 1, 0, 0, 0, 1));
                both.HasLinearConversion = true;
                Assert.Throws<ArgumentException>(() => writer.AddChannel(both));
                MdfChannelDefinition master = WriterMultiGroupTests.Master();
                master.Conversion = MdfConversionDefinition.ValueToText(new double[] { 0 }, new[] { "a" }, null);
                Assert.Throws<ArgumentException>(() => writer.AddChannel(master));
                master.Conversion = MdfConversionDefinition.Rational(0, 2, 0, 0, 0, 1); // Numeric conversions are allowed on a master.
                writer.AddChannel(master);
                MdfChannelDefinition latin = Numeric("y", MdfConversionDefinition.ValueToText(new double[] { 0 }, new[] { "\u20ac" }, null));
                writer.AddChannel(latin); // UTF-8 in MDF 4.
            }
            using (MdfWriter writer = MdfWriter.Create(new TestWriteStream(), true, new MdfWriterOptions { Version = "3.30" }))
            {
                Assert.Throws<ArgumentException>(() => writer.AddChannel(Numeric("y", MdfConversionDefinition.ValueToText(new double[] { 0 }, new[] { "\u20ac" }, null))));
            }
            using (MdfWriter writer = MdfWriter.Create(new TestWriteStream(), true, new MdfWriterOptions { MaximumTextBytes = 16 }))
            {
                Assert.Equal(nameof(MdfWriterOptions.MaximumTextBytes), Assert.Throws<MdfResourceLimitException>(
                    () => writer.AddChannel(Numeric("y", MdfConversionDefinition.ValueToText(new double[] { 0 }, new[] { new string('t', 20) }, null)))).LimitName);
            }
        }

        [Fact]
        public void FactoriesValidateTheShape()
        {
            Assert.Throws<ArgumentException>(() => MdfConversionDefinition.Rational(double.NaN, 0, 0, 0, 0, 1));
            Assert.Throws<ArgumentException>(() => MdfConversionDefinition.Exponential(new double[] { 1, 1, 0, 1, 0, 1, 0 })); // P1 and P4 nonzero.
            Assert.Throws<ArgumentException>(() => MdfConversionDefinition.Exponential(new double[6]));
            Assert.Throws<ArgumentNullException>(() => MdfConversionDefinition.Logarithmic(null));
            Assert.Throws<ArgumentException>(() => MdfConversionDefinition.Algebraic("Y + 1"));
            Assert.Throws<ArgumentException>(() => MdfConversionDefinition.Algebraic("X\0"));
            Assert.Throws<ArgumentNullException>(() => MdfConversionDefinition.Algebraic(null));
            Assert.Throws<ArgumentException>(() => MdfConversionDefinition.Table(new double[0], new double[0], true));
            Assert.Throws<ArgumentException>(() => MdfConversionDefinition.Table(new double[] { 1, 1 }, new double[] { 0, 0 }, false));
            Assert.Throws<ArgumentException>(() => MdfConversionDefinition.Table(new double[] { 1, 2 }, new double[] { 0 }, false));
            Assert.Throws<ArgumentException>(() => MdfConversionDefinition.RangeTable(new double[] { 2 }, new double[] { 1 }, new double[] { 0 }, 0));
            Assert.Throws<ArgumentException>(() => MdfConversionDefinition.RangeTable(new double[] { 0, 1 }, new double[] { 2, 3 }, new double[] { 0, 0 }, 0));
            Assert.Throws<ArgumentException>(() => MdfConversionDefinition.RangeTable(new double[] { 0 }, new double[] { 1 }, new double[] { 0 }, double.PositiveInfinity));
            Assert.Throws<ArgumentException>(() => MdfConversionDefinition.ValueToText(new double[] { 2, 1 }, new[] { "a", "b" }, null));
            Assert.Throws<ArgumentException>(() => MdfConversionDefinition.ValueToText(new double[] { 1 }, new[] { "a\0" }, null));
            Assert.Throws<ArgumentException>(() => MdfConversionDefinition.ValueToText(new double[] { 1 }, new[] { "a" }, "\0"));
            Assert.Throws<ArgumentException>(() => MdfConversionDefinition.RangeToText(new double[] { 1 }, new double[] { 2 }, new string[0], null));
            Assert.Throws<ArgumentException>(() => MdfConversionDefinition.TextToValue(new string[] { null }, new double[] { 1 }, 0));
            Assert.Throws<ArgumentException>(() => MdfConversionDefinition.TextToValue(new[] { "a" }, new double[] { 1 }, double.NaN));
            Assert.Throws<ArgumentException>(() => MdfConversionDefinition.TextToText(new[] { "a" }, new string[0], null));
            Assert.Throws<ArgumentException>(() => MdfConversionDefinition.TextToText(new[] { "a" }, new[] { "b" }, "c\0"));
            Assert.Throws<ArgumentException>(() => MdfConversionDefinition.Table(new double[30000], new double[30000], true));

            double[] keys = { 1, 2 };
            MdfConversionDefinition table = MdfConversionDefinition.Table(keys, new double[] { 10, 20 }, true);
            keys[0] = 99; // Inputs are copied.
            Assert.Equal(MdfConversionKind.TableInterpolation, table.Kind);
            Assert.Equal(new double[] { 1, 10, 2, 20 }, table.Parameters);
            Assert.Empty(table.Texts);
            Assert.Null(table.Formula);
            MdfConversionDefinition text = MdfConversionDefinition.TextToValue(new[] { "a", "a" }, new double[] { 1, 2 }, 3);
            Assert.Equal(new double[] { 1, 2, 3 }, text.Parameters);
            Assert.Equal(new[] { "a", "a" }, text.Texts);
            Assert.Equal(new double[] { 5, 6, 7, 8 }, MdfConversionDefinition.RangeTable(new double[] { 5 }, new double[] { 6 }, new double[] { 7 }, 8).Parameters);
            Assert.Equal(new[] { "x", null }, MdfConversionDefinition.RangeToText(new double[] { 0 }, new double[] { 0 }, new[] { "x" }, null).Texts);
        }

        [Fact]
        public void ExistingLinearOutputIsUnchanged()
        {
            MdfChannelDefinition linear = new MdfChannelDefinition { Name = "l", ValueKind = MdfValueKind.UnsignedInteger, BitCount = 8, HasLinearConversion = true, Factor = 2, Offset = 1 };
            byte[] file = Write("4.10", new[] { linear });
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file)))
            {
                MdfChannel channel = reader.ReadMetadata()[0].ChannelGroups[0].Channels[1];
                Assert.Equal(MdfConversionKind.Linear, channel.Conversion.Kind);
                Assert.Equal(2.0, channel.Conversion.Factor.Value);
            }
        }
    }
}
