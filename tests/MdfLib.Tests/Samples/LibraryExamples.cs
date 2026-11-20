// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// managed equivalent: the scenarios of the upstream C++/CLI wrapper tests (mdflibrary_test_net: TestWriter,
// TestNoCompressCanBus/TestCompressCanBus, TestReader2/TestReadData) written directly against MdfLib, which needs no
// native ABI. Each method is a self-contained example; the test suite runs them.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MdfLib.Samples
{
    /// <summary>Managed counterparts of the MdfLibrary (C++/CLI) usage examples.</summary>
    public static class LibraryExamples
    {
        /// <summary>The channel set of upstream TestWriter: a time master (64-bit here) and one channel per data type.</summary>
        internal static MdfChannelDefinition[] SignalChannels()
        {
            return new[]
            {
                new MdfChannelDefinition { Name = "Time", Unit = "s", ValueKind = MdfValueKind.FloatingPoint, BitCount = 64, IsMaster = true },
                new MdfChannelDefinition { Name = "UnsignedLe", Unit = "°C", ValueKind = MdfValueKind.UnsignedInteger, BitCount = 32 },
                new MdfChannelDefinition { Name = "UnsignedBe", ValueKind = MdfValueKind.UnsignedInteger, BitCount = 16, IsBigEndian = true },
                new MdfChannelDefinition { Name = "SignedLe", ValueKind = MdfValueKind.SignedInteger, BitCount = 32 },
                new MdfChannelDefinition { Name = "SignedBe", ValueKind = MdfValueKind.SignedInteger, BitCount = 8, IsBigEndian = true },
                new MdfChannelDefinition { Name = "FloatLe", ValueKind = MdfValueKind.FloatingPoint, BitCount = 32 },
                new MdfChannelDefinition { Name = "FloatBe", ValueKind = MdfValueKind.FloatingPoint, BitCount = 64, IsBigEndian = true },
                new MdfChannelDefinition { Name = "String", ValueKind = MdfValueKind.Text, BitCount = 80 },
                new MdfChannelDefinition { Name = "Array", ValueKind = MdfValueKind.ByteArray, BitCount = 40 },
                new MdfChannelDefinition { Name = "Scaled", Unit = "V", ValueKind = MdfValueKind.UnsignedInteger, BitCount = 16, HasLinearConversion = true, Factor = 0.01, Offset = -5 },
            };
        }

        /// <summary>The values of sample <paramref name="i"/> for <see cref="SignalChannels"/>.</summary>
        internal static MdfSampleValue[] SignalRow(int i)
        {
            return new[]
            {
                MdfSampleValue.FromFloatingPoint(i * 0.01),
                MdfSampleValue.FromUnsigned((ulong)i * 1000),
                MdfSampleValue.FromUnsigned((ulong)(i & 0xFFFF)),
                MdfSampleValue.FromSigned(-i * 1000),
                MdfSampleValue.FromSigned((sbyte)i),
                MdfSampleValue.FromFloatingPoint(i * 0.5),
                MdfSampleValue.FromFloatingPoint(i * -0.25),
                MdfSampleValue.FromText("S" + i),
                MdfSampleValue.FromByteArray(new byte[] { (byte)i, (byte)(i >> 8), 0, 1, 2 }),
                MdfSampleValue.FromUnsigned((ulong)(i % 1000)),
            };
        }

        /// <summary>Upstream TestWriter (Mdf4Basic) / Mdf3Basic: writes <paramref name="samples"/> rows of every data type.</summary>
        public static void WriteSignals(Stream destination, string version, int samples, bool compress = false)
        {
            MdfWriterOptions options = new MdfWriterOptions { Version = version, CompressData = compress, StartTimeNanoseconds = 1_700_000_000_000_000_000UL };
            using (MdfWriter writer = MdfWriter.Create(destination, true, options))
            {
                if (version != "3.30") { writer.SetChannelGroupName(0, "Test"); } // MDF 3 groups have no name.
                foreach (MdfChannelDefinition channel in SignalChannels())
                {
                    if (version == "3.30" && channel.ValueKind == MdfValueKind.ByteArray) { continue; }
                    writer.AddChannel(channel);
                }
                writer.StartMeasurement();
                for (int i = 0; i < samples; i++)
                {
                    MdfSampleValue[] row = SignalRow(i);
                    writer.SaveSample(version == "3.30" ? row.Where((v, index) => index != 8).ToArray() : row);
                }
                writer.StopMeasurement();
                writer.FinalizeMeasurement();
            }
        }

        /// <summary>Upstream TestNoCompressCanBus/TestCompressCanBus: one CAN data frame per second, ID 1234, 8 bytes.</summary>
        public static void WriteCanBusLog(Stream destination, bool compress, int frames)
        {
            MdfBusLoggerOptions options = new MdfBusLoggerOptions { CompressData = compress };
            using (MdfBusLogger logger = MdfBusLogger.Create(destination, MdfBusType.Can, true, options))
            {
                logger.StartMeasurement();
                MdfBusMessage message = new MdfBusMessage { Type = MdfBusMessageType.CanDataFrame, Id = 1234, Data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 } };
                for (int sample = 0; sample < frames; sample++)
                {
                    message.Timestamp = sample; // 1 s apart.
                    logger.Write(message);
                }
                logger.StopMeasurement();
                logger.FinalizeMeasurement();
            }
        }

        /// <summary>Upstream TestReadData (channel observers): reads every value of the named channel as physical values.</summary>
        internal static List<MdfSampleValue> ReadChannel(Stream source, string channelName)
        {
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                foreach (MdfChannelGroup group in reader.ReadMetadata().SelectMany(d => d.ChannelGroups))
                {
                    MdfChannel channel = group.Channels.FirstOrDefault(c => c.Name == channelName);
                    if (channel == null) { continue; }
                    List<MdfSampleValue> values = new List<MdfSampleValue>();
                    MdfValueRepresentation representation = channel.CanReadPhysical ? MdfValueRepresentation.Physical : MdfValueRepresentation.Raw;
                    foreach (MdfSampleBatch batch in reader.ReadSamples(group, new[] { channel.Index }, representation: representation))
                    {
                        for (int i = 0; i < batch.SampleCount; i++) { values.Add(batch.GetValue(i, 0)); }
                    }
                    return values;
                }
            }
            throw new ArgumentException("No channel named " + channelName + ".", nameof(channelName));
        }

        /// <summary>Upstream TestBusLogging read side: decodes every CAN frame of a bus log.</summary>
        internal static List<MdfBusMessage> ReadCanBusLog(Stream source)
        {
            using (MdfReader reader = MdfReader.Open(source, true)) { return MdfBusReader.ReadMessages(reader).ToList(); }
        }
    }
}
