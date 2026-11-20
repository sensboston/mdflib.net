// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using MdfLib.TestSupport;
using Xunit;

namespace MdfLib.CompatibilityTests
{
    public sealed class WriterFixtureTests
    {
        public static IEnumerable<object[]> Cases()
        {
            foreach (string version in new[] { "330", "410", "420" })
            foreach (string shape in new[] { "numeric", "empty", "widths" })
            foreach (int batch in new[] { 1, 2, 1024 })
            foreach (MdfValueRepresentation representation in new[] { MdfValueRepresentation.Raw, MdfValueRepresentation.Physical })
            { yield return new object[] { "m7a-" + shape + version, batch, representation }; }
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void ActualWriterOutputEqualsPreCodeIndependentlyReadBytesAndRoundTrips(string id, int batch, object representationValue)
        {
            MdfValueRepresentation representation = (MdfValueRepresentation)representationValue;
            GoldenCase expected = ReadCase(id);
            byte[] actual = Write(expected);
            // This equality is the exact link between ACTUAL C# output and the saved external hash/read.
            Assert.Equal(FixtureCorpus.Read(id), actual);
            using (MemoryStream stream = new MemoryStream(actual, false))
            using (MdfReader reader = MdfReader.Open(stream))
            {
                Assert.Equal(expected.Version, reader.Header.Version);
                Assert.Equal(ulong.Parse(expected.StartTime, CultureInfo.InvariantCulture), reader.Header.RawStartTimeNanoseconds);
                Assert.Equal("MdfLib", reader.Header.ProgramIdentifier);
                Assert.Single(reader.ReadMetadata());
                MdfChannelGroup group = Assert.Single(reader.ReadMetadata()[0].ChannelGroups);
                Assert.True(group.CanReadSamples, group.UnsupportedReason);
                Assert.Equal((ulong)expected.Samples, group.SampleCount);
                Assert.Equal(expected.Channels.Count, group.Channels.Count);
                Assert.Equal((ulong)(expected.DataBytes + expected.InvalidationBytes), group.RecordByteCount);
                for (int i = 0; i < group.Channels.Count; i++)
                {
                    Assert.Equal(expected.Channels[i].Name, group.Channels[i].Name);
                    Assert.Equal(expected.Channels[i].Unit, group.Channels[i].Unit);
                    Assert.Equal(expected.Channels[i].Master, group.Channels[i].IsMaster);
                    Assert.Equal((uint)expected.Channels[i].Bits, group.Channels[i].BitCount);
                }
                int[] selection = Enumerable.Range(0, group.Channels.Count).Reverse().ToArray();
                int row = 0;
                foreach (MdfSampleBatch result in reader.ReadSamples(group, selection, batchSize: batch, representation: representation))
                {
                    Assert.Equal((ulong)row, result.FirstSampleIndex);
                    for (int r = 0; r < result.SampleCount; r++, row++)
                    for (int c = 0; c < selection.Length; c++)
                    {
                        GoldenChannel channel = expected.Channels[selection[c]];
                        Check(result.GetValue(r, c), channel, row, representation);
                    }
                }
                Assert.Equal(expected.Samples, row);
            }
        }

        [Theory]
        [InlineData("3.30")]
        [InlineData("4.10")]
        [InlineData("4.20")]
        public void EverySubrangeOfActualSerializedRowsUsesTheOriginalTypedInputs(string version)
        {
            GoldenCase expected = ReadCase("m7a-numeric" + version.Replace(".", string.Empty));
            using (MemoryStream stream = new MemoryStream(Write(expected)))
            using (MdfReader reader = MdfReader.Open(stream))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                int[] selection = Enumerable.Range(0, group.Channels.Count).ToArray();
                for (int first = 0; first <= expected.Samples; first++)
                for (int count = 0; count <= expected.Samples - first; count++)
                {
                    int actual = first;
                    foreach (MdfSampleBatch batch in reader.ReadSamples(group, selection, (ulong)first, (ulong)count, 2))
                    {
                        for (int r = 0; r < batch.SampleCount; r++, actual++)
                        for (int c = 0; c < selection.Length; c++) { Check(batch.GetValue(r, c), expected.Channels[c], actual, MdfValueRepresentation.Raw); }
                    }
                    Assert.Equal(first + count, actual);
                }
            }
        }

        private static byte[] Write(GoldenCase expected)
        {
            using (MemoryStream output = new MemoryStream())
            {
                using (MdfWriter writer = MdfWriter.Create(output, true, new MdfWriterOptions
                { Version = expected.Version, StartTimeNanoseconds = ulong.Parse(expected.StartTime, CultureInfo.InvariantCulture) }))
                {
                    for (int i = 0; i < expected.Channels.Count; i++)
                    {
                        GoldenChannel channel = expected.Channels[i];
                        Assert.Equal(i, writer.AddChannel(new MdfChannelDefinition
                        {
                            Name = channel.Name, Unit = channel.Unit, ValueKind = Kind(channel), BitCount = channel.Bits,
                            IsBigEndian = channel.Big, IsMaster = channel.Master, HasLinearConversion = channel.Linear,
                            Factor = Real(channel.Factor), Offset = Real(channel.Offset)
                        }));
                    }
                    Assert.Equal(0, output.Length);
                    Assert.Equal(expected.DataBytes + expected.InvalidationBytes, writer.RecordByteCount);
                    writer.StartMeasurement();
                    MdfSampleValue[] row = new MdfSampleValue[expected.Channels.Count];
                    for (int r = 0; r < expected.Samples; r++)
                    {
                        for (int c = 0; c < row.Length; c++)
                        {
                            GoldenChannel channel = expected.Channels[c];
                            row[c] = Value(Kind(channel), channel.Input[r], channel.Valid[r]);
                        }
                        writer.SaveSample(row);
                        Array.Clear(row, 0, row.Length); // Caller storage is never retained.
                    }
                    Assert.Equal((ulong)expected.Samples, writer.SampleCount);
                    writer.StopMeasurement();
                    writer.FinalizeMeasurement();
                    Assert.Equal(MdfWriterState.Finalized, writer.State);
                    Assert.Equal(output.Length, output.Position);
                    writer.FinalizeMeasurement(); // Exact idempotence, no bytes/time/history change.
                }
                return output.ToArray();
            }
        }

        private static void Check(MdfSampleValue actual, GoldenChannel expected, int row, MdfValueRepresentation representation)
        {
            MdfValueKind kind = representation == MdfValueRepresentation.Physical && expected.Linear ? MdfValueKind.FloatingPoint : Kind(expected);
            string text = (representation == MdfValueRepresentation.Raw ? expected.Raw : expected.Physical)[row];
            MdfSampleValue wanted = Value(kind, text, expected.Valid[row]);
            Assert.Equal(wanted.Kind, actual.Kind);
            Assert.Equal(wanted.IsValid, actual.IsValid);
            if (kind == MdfValueKind.UnsignedInteger) { Assert.Equal(wanted.UnsignedValue, actual.UnsignedValue); }
            else if (kind == MdfValueKind.SignedInteger) { Assert.Equal(wanted.SignedValue, actual.SignedValue); }
            else if (double.IsNaN(wanted.FloatingPointValue)) { Assert.True(double.IsNaN(actual.FloatingPointValue)); }
            else { Assert.Equal(BitConverter.DoubleToInt64Bits(wanted.FloatingPointValue), BitConverter.DoubleToInt64Bits(actual.FloatingPointValue)); }
        }

        private static MdfValueKind Kind(GoldenChannel value) { return (MdfValueKind)Enum.Parse(typeof(MdfValueKind), value.Kind); }
        private static MdfSampleValue Value(MdfValueKind kind, string value, bool valid)
        {
            return kind == MdfValueKind.UnsignedInteger ? MdfSampleValue.FromUnsigned(ulong.Parse(value, CultureInfo.InvariantCulture), valid)
                : kind == MdfValueKind.SignedInteger ? MdfSampleValue.FromSigned(long.Parse(value, CultureInfo.InvariantCulture), valid)
                : MdfSampleValue.FromFloatingPoint(Real(value), valid);
        }
        private static double Real(string value)
        {
            if (value == "NaN") { return double.NaN; }
            if (value == "Infinity") { return double.PositiveInfinity; }
            if (value == "-Infinity") { return double.NegativeInfinity; }
            if (value == "-0") { return BitConverter.Int64BitsToDouble(long.MinValue); }
            return double.Parse(value, CultureInfo.InvariantCulture);
        }
        private static GoldenCase ReadCase(string id)
        {
            using (FileStream stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "m7a-expectations.json")))
            { return ((GoldenFile)new DataContractJsonSerializer(typeof(GoldenFile)).ReadObject(stream)).Cases.Single(c => c.Id == id); }
        }

        [DataContract]
        private sealed class GoldenFile
        {
            [DataMember(Name = "cases")] public List<GoldenCase> Cases { get; set; }
        }
        [DataContract]
        private sealed class GoldenCase
        {
            [DataMember(Name = "id")] public string Id { get; set; }
            [DataMember(Name = "version")] public string Version { get; set; }
            [DataMember(Name = "startTimeNanoseconds")] public string StartTime { get; set; }
            [DataMember(Name = "samples")] public int Samples { get; set; }
            [DataMember(Name = "dataBytes")] public int DataBytes { get; set; }
            [DataMember(Name = "invalidationBytes")] public int InvalidationBytes { get; set; }
            [DataMember(Name = "channels")] public List<GoldenChannel> Channels { get; set; }
        }
        [DataContract]
        private sealed class GoldenChannel
        {
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "unit")] public string Unit { get; set; }
            [DataMember(Name = "kind")] public string Kind { get; set; }
            [DataMember(Name = "bitCount")] public int Bits { get; set; }
            [DataMember(Name = "bigEndian")] public bool Big { get; set; }
            [DataMember(Name = "master")] public bool Master { get; set; }
            [DataMember(Name = "linear")] public bool Linear { get; set; }
            [DataMember(Name = "factor")] public string Factor { get; set; }
            [DataMember(Name = "offset")] public string Offset { get; set; }
            [DataMember(Name = "input")] public string[] Input { get; set; }
            [DataMember(Name = "raw")] public string[] Raw { get; set; }
            [DataMember(Name = "physical")] public string[] Physical { get; set; }
            [DataMember(Name = "valid")] public bool[] Valid { get; set; }
        }
    }
}
