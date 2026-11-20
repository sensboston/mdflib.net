// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Reading
{
    // localisation: a 16-bit raw channel with a conversion, written by the engine, read back by the engine.
    // The raw values are read first, so a failure shows whether the raw sample or the conversion is wrong.
    public sealed class ConversionPhysicalReadTests
    {
        private static readonly ulong[] Raw = { 0, 3, 7 };

        private static byte[] Write(MdfChannelDefinition raw)
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfWriter writer = MdfWriter.Create(output, true, new MdfWriterOptions { Version = "4.10" }))
            {
                writer.AddChannel(new MdfChannelDefinition { Name = "t", Unit = "s", BitCount = 64, ValueKind = MdfValueKind.FloatingPoint, IsMaster = true });
                writer.AddChannel(raw);
                writer.StartMeasurement();
                for (int row = 0; row < Raw.Length; row++)
                {
                    writer.SaveSample(new[] { MdfSampleValue.FromFloatingPoint(row * 1.0), MdfSampleValue.FromUnsigned(Raw[row]) });
                }
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
            }
            return output.Snapshot();
        }

        private static List<MdfSampleValue> Read(byte[] file, MdfValueRepresentation representation)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                int index = group.Channels.Single(c => c.Name == "raw").Index;
                List<MdfSampleValue> values = new List<MdfSampleValue>();
                foreach (MdfSampleBatch batch in reader.ReadSamples(group, new[] { index }, 0UL, (ulong)Raw.Length, 1024, representation))
                {
                    for (int r = 0; r < batch.SampleCount; r++) { values.Add(batch.GetValue(r, 0)); }
                }
                return values;
            }
        }

        [Fact]
        public void LinearConversionReadsRawThenPhysical()
        {
            byte[] file = Write(new MdfChannelDefinition
            {
                Name = "raw", BitCount = 16, ValueKind = MdfValueKind.UnsignedInteger,
                HasLinearConversion = true, Factor = 2.0, Offset = 1.0,
            });
            List<MdfSampleValue> raw = Read(file, MdfValueRepresentation.Raw);
            Assert.Equal(Raw, raw.Select(v => v.UnsignedValue).ToArray());
            List<MdfSampleValue> physical = Read(file, MdfValueRepresentation.Physical);
            Assert.Equal(new double[] { 1.0, 7.0, 15.0 }, physical.Select(v => v.FloatingPointValue).ToArray());
        }

        [Fact]
        public void RationalConversionReadsRawThenPhysical()
        {
            // (0.5 x^2 + 2 x + 1) for the raw values 0, 3, 7: 1, 11.5, 39.5.
            byte[] file = Write(new MdfChannelDefinition
            {
                Name = "raw", BitCount = 16, ValueKind = MdfValueKind.UnsignedInteger,
                Conversion = MdfConversionDefinition.Rational(0.5, 2.0, 1.0, 0.0, 0.0, 1.0),
            });
            List<MdfSampleValue> raw = Read(file, MdfValueRepresentation.Raw);
            Assert.Equal(Raw, raw.Select(v => v.UnsignedValue).ToArray());
            List<MdfSampleValue> physical = Read(file, MdfValueRepresentation.Physical);
            Assert.Equal(new double[] { 1.0, 11.5, 39.5 }, physical.Select(v => v.FloatingPointValue).ToArray());
        }
    }
}
