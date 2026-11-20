// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Reads one MDF file with the managed engine and prints the canonical description the oracle compares with the
// native wrapper's (tools/MdfLib.NativeOracle/NativeDump.cs). The two dumpers print the same lines in the same order;
// tools/ci/oracle.py normalizes the comment text before it compares. Values print as "f" (a number, physical value)
// or "s" (text), so a channel reads the same on both sides whatever integer width it was stored in.
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MdfLib;

namespace MdfOracle
{
    internal static class Dumper
    {
        public static List<string> Lines(string path)
        {
            List<string> lines = new List<string>();
            using (MdfReader reader = MdfReader.Open(File.OpenRead(path)))
            {
                IReadOnlyList<MdfDataGroup> groups = reader.ReadMetadata();
                lines.Add("dataGroups " + groups.Count.ToString(CultureInfo.InvariantCulture));
                for (int g = 0; g < groups.Count; g++)
                {
                    IReadOnlyList<MdfChannelGroup> channelGroups = groups[g].ChannelGroups;
                    lines.Add("dg " + g.ToString(CultureInfo.InvariantCulture) + " channelGroups "
                        + channelGroups.Count.ToString(CultureInfo.InvariantCulture));
                    for (int j = 0; j < channelGroups.Count; j++)
                    {
                        MdfChannelGroup group = channelGroups[j];
                        string groupId = g.ToString(CultureInfo.InvariantCulture) + "." + j.ToString(CultureInfo.InvariantCulture);
                        lines.Add("cg " + groupId + " samples=" + group.SampleCount.ToString(CultureInfo.InvariantCulture)
                            + " source=" + SourceOf(group.Source) + " name=" + Text(group.Name)
                            + " comment=" + Text(group.Comment));
                        for (int c = 0; c < group.Channels.Count; c++)
                        {
                            MdfChannel channel = group.Channels[c];
                            lines.Add("cn " + groupId + "." + c.ToString(CultureInfo.InvariantCulture)
                                + " master=" + (channel.IsMaster ? "1" : "0")
                                + " unit=" + Text(channel.Unit) + " name=" + Text(channel.Name)
                                + " conv=" + (channel.Conversion == null || channel.Conversion.Kind == MdfConversionKind.Identity ? "none" : "set")
                                + " comment=" + Text(channel.Comment));
                        }
                        int samples = checked((int)group.SampleCount);
                        for (int c = 0; c < group.Channels.Count; c++)
                        {
                            for (int s = 0; s < samples; s++)
                            {
                                MdfSampleValue value = Value(reader, group, c, s);
                                lines.Add("v " + groupId + "." + c.ToString(CultureInfo.InvariantCulture)
                                    + " " + s.ToString(CultureInfo.InvariantCulture) + " " + Print(value));
                            }
                        }
                    }
                }
            }
            return lines;
        }

        private static MdfSampleValue Value(MdfReader reader, MdfChannelGroup group, int channel, int sample)
        {
            foreach (MdfSampleBatch batch in reader.ReadSamples(group, new int[] { channel }, (ulong)sample, 1UL,
                1024, MdfValueRepresentation.Physical))
            {
                return batch.GetValue(0, 0);
            }
            throw new System.InvalidOperationException("The engine returned no batch for channel " + channel + ".");
        }

        private static string Print(MdfSampleValue value)
        {
            switch (value.Kind)
            {
                case MdfValueKind.Text:
                    return "s " + Text(value.TextValue);
                case MdfValueKind.FloatingPoint:
                    return "f " + value.FloatingPointValue.ToString("R", CultureInfo.InvariantCulture);
                case MdfValueKind.SignedInteger:
                    return "f " + ((double)value.SignedValue).ToString("R", CultureInfo.InvariantCulture);
                case MdfValueKind.UnsignedInteger:
                    return "f " + ((double)value.UnsignedValue).ToString("R", CultureInfo.InvariantCulture);
                case MdfValueKind.ByteArray:
                    return "b " + Hex(value.GetByteArrayValue());
                default:
                    return "x " + value.Kind;
            }
        }

        private static string Hex(byte[] bytes)
        {
            return bytes == null ? string.Empty : System.BitConverter.ToString(bytes).Replace("-", string.Empty);
        }

        private static string SourceOf(MdfSourceInformation source)
        {
            return source == null ? "-" : Text(source.Name) + "|" + Text(source.Path);
        }

        private static string Text(string value)
        {
            return value ?? string.Empty;
        }
    }
}
