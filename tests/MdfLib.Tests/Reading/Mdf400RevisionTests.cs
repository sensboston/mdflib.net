// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MdfLib.TestSupport;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Reading
{
    // MDF 4.00 is read by the MDF 4 reader with the 4.10/4.11 rules; ID bytes 60..63 are
    // the unfinalized flags only from 4.10 and are reserved (not interpreted) in 4.00; 4.20-only storage stays 4.20.
    public sealed class Mdf400RevisionTests
    {
        private const string Sorted = "m5y-sorted400";
        private const string List = "m5y-list400";
        private const string Interleaved = "m5y-interleaved400";

        [Theory]
        [InlineData(Sorted)]
        [InlineData(List)]
        [InlineData(Interleaved)]
        public void Version400OpensWithNonzeroReservedBytesAndReadsEveryGroup(string id)
        {
            byte[] bytes = FixtureCorpus.Read(id);
            Assert.NotEqual(0, bytes[60] | bytes[61] | bytes[62] | bytes[63]);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                Assert.Equal("4.00", reader.Header.Version);
                Assert.Equal("M5Ypy-v1", reader.Header.ProgramIdentifier);
                Assert.Equal(1763296496123456789UL, reader.Header.RawStartTimeNanoseconds);
                Assert.Equal(60, reader.Header.UtcOffsetMinutes);
                Assert.Null(reader.Header.CodePage);
                List<MdfChannelGroup> groups = reader.ReadMetadata().SelectMany(g => g.ChannelGroups).ToList();
                Assert.All(groups, g => Assert.True(g.CanReadSamples, g.UnsupportedReason));
                Assert.All(groups, g => Assert.Equal(5, reader.ReadSamples(g, Enumerable.Range(0, g.Channels.Count).ToArray()).Sum(b => b.SampleCount)));
                Assert.Equal(0, source.WriteCalls);
            }
        }

        [Theory]
        [InlineData(Sorted)]
        [InlineData(List)]
        [InlineData(Interleaved)]
        public void SameBytesLabelled410AreRefusedAsUnfinalizedBeforeHd(string id)
        {
            byte[] bytes = Relabel(FixtureCorpus.Read(id), "4.10");
            using (TestReadStream source = new TestReadStream(bytes))
            {
                Assert.Throws<NotSupportedException>(() => MdfReader.Open(source, true));
                Assert.True(source.Position <= 64);
            }
        }

        [Theory]
        [InlineData(Sorted, "4.10")]
        [InlineData(List, "4.11")]
        [InlineData(Interleaved, "4.10")]
        public void ClearingTheReservedBytesAndRelabellingGivesIdenticalValues(string id, string version)
        {
            byte[] relabelled = Relabel(FixtureCorpus.Read(id), version);
            for (int index = 60; index < 64; index++) { relabelled[index] = 0; }
            Assert.Equal(Describe(FixtureCorpus.Read(id)), Describe(relabelled));
        }

        [Fact]
        public void UnfinalizedSignatureIsRefusedIn400()
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            byte[] signature = Encoding.ASCII.GetBytes("UnFinMF ");
            Array.Copy(signature, bytes, signature.Length);
            using (MemoryStream source = new MemoryStream(bytes, false))
            {
                Assert.Throws<NotSupportedException>(() => MdfReader.Open(source, true));
            }
        }

        [Theory]
        [InlineData("4.01")]
        [InlineData("4.09")]
        [InlineData("3.40")]
        [InlineData("4.30")]
        public void NeighbouringRevisionsWithTheSameBytesStayUnsupported(string version)
        {
            using (MemoryStream source = new MemoryStream(Relabel(FixtureCorpus.Read(Sorted), version), false))
            {
                Assert.Throws<NotSupportedException>(() => MdfReader.Open(source, true));
            }
        }

        [Theory]
        [InlineData(410)]
        [InlineData(330)]
        [InlineData(401)]
        public void TextAndNumberMismatchIsMdf003(int numeric)
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            bytes[28] = (byte)numeric;
            bytes[29] = (byte)(numeric >> 8);
            using (MemoryStream source = new MemoryStream(bytes, false))
            {
                Assert.Equal("MDF003", Assert.Throws<MdfFormatException>(() => MdfReader.Open(source, true)).DiagnosticCode);
            }
        }

        [Theory]
        [InlineData("m5c-ld-single420")]
        [InlineData("m5c-ld-equal420")]
        [InlineData("m5c-ld-zsingle420")]
        public void Mdf420OnlyStorageIsTreatedExactlyAs410In400(string id)
        {
            string as400 = Describe(Relabel(FixtureCorpus.Read(id), "4.00"));
            Assert.Equal(Describe(Relabel(FixtureCorpus.Read(id), "4.10")), as400);
            Assert.NotEqual(Describe(FixtureCorpus.Read(id)), as400);
        }

        [Theory]
        [InlineData("m5c-hl420")]
        [InlineData("m5c-dt420")]
        [InlineData("m2-mdf410-header")]
        public void OtherMdf4FilesRelabelled400BehaveAs410(string id)
        {
            Assert.Equal(Describe(Relabel(FixtureCorpus.Read(id), "4.10")), Describe(Relabel(FixtureCorpus.Read(id), "4.00")));
        }

        private static byte[] Relabel(byte[] bytes, string version)
        {
            for (int index = 0; index < 4; index++) { bytes[8 + index] = (byte)version[index]; }
            int numeric = (version[0] - '0') * 100 + (version[2] - '0') * 10 + version[3] - '0';
            bytes[28] = (byte)numeric;
            bytes[29] = (byte)(numeric >> 8);
            return bytes;
        }

        // Version-independent summary of everything the reader publishes: catalog, reasons, and every value (or the failure).
        private static string Describe(byte[] bytes)
        {
            StringBuilder text = new StringBuilder();
            try
            {
                using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false), false))
                {
                    text.Append(reader.Header.ProgramIdentifier).Append('|').Append(reader.Header.RawStartTimeNanoseconds.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    foreach (MdfChannelGroup group in reader.ReadMetadata().SelectMany(g => g.ChannelGroups))
                    {
                        text.Append("G").Append(group.Index).Append(' ').Append(group.SampleCount.ToString(CultureInfo.InvariantCulture)).Append(' ')
                            .Append(group.RecordByteCount.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(group.UnsupportedReason).Append('\n');
                        foreach (MdfChannel channel in group.Channels)
                        {
                            text.Append(' ').Append(channel.Name).Append(' ').Append(channel.ValueKind).Append(' ').Append(channel.UnsupportedReason).Append('\n');
                        }
                        int[] readable = group.Channels.Where(c => c.CanReadPhysical).Select(c => c.Index).ToArray();
                        if (!group.CanReadSamples || readable.Length == 0) { continue; }
                        foreach (MdfSampleBatch batch in reader.ReadSamples(group, readable, representation: MdfValueRepresentation.Physical))
                        {
                            for (int row = 0; row < batch.SampleCount; row++)
                            {
                                for (int column = 0; column < readable.Length; column++)
                                {
                                    MdfChannel channel = batch.Channels[column];
                                    ulong elements = channel.Array == null ? 1 : channel.Array.ElementCount;
                                    for (ulong element = 0; element < elements; element++)
                                    {
                                        Value(text, channel.Array == null ? batch.GetValue(row, column) : batch.GetValue(row, column, element));
                                    }
                                }
                                text.Append('\n');
                            }
                        }
                    }
                }
            }
            catch (Exception error)
            {
                MdfFormatException format = error as MdfFormatException;
                text.Append("!").Append(error.GetType().Name).Append(' ').Append(format == null ? error.Message : format.DiagnosticCode);
            }
            return text.ToString();
        }

        private static void Value(StringBuilder text, MdfSampleValue value)
        {
            text.Append(value.IsValid ? " " : " ~");
            switch (value.Kind)
            {
                case MdfValueKind.UnsignedInteger: text.Append(value.UnsignedValue.ToString(CultureInfo.InvariantCulture)); break;
                case MdfValueKind.SignedInteger: text.Append(value.SignedValue.ToString(CultureInfo.InvariantCulture)); break;
                case MdfValueKind.FloatingPoint: text.Append(BitConverter.DoubleToInt64Bits(value.FloatingPointValue).ToString("X16", CultureInfo.InvariantCulture)); break;
                case MdfValueKind.Text: text.Append('"').Append(value.TextValue).Append('"'); break;
                case MdfValueKind.ByteArray: text.Append(BitConverter.ToString(value.GetByteArrayValue())); break;
                default: text.Append(value.Kind); break;
            }
        }
    }
}
