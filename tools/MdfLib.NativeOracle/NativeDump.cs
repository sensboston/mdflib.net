// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Reads one MDF file with the native wrapper mdflibrary.dll (the release of ihedvall/mdflib, pinned by
// tools/ci/oracle.py) and prints the same canonical lines as tools/MdfLib.Oracle/EngineDump.cs. Values come from the
// wrapper's channel observers: a physical number when the wrapper gives one, text otherwise.
using System.Collections.Generic;
using System.Globalization;
using MdfLibrary;

namespace MdfOracle
{
    internal static class Dumper
    {
        // Set to 1 by the reading oracle (tools/ci/oracle.py) to add the HD lines to the dump.
        public const string HeaderVariable = "MDFLIB_ORACLE_HEADER";

        public static List<string> Lines(string path)
        {
            List<string> lines = new List<string>();
            // The facade's reader is not IDisposable; both wrappers have a Dispose method, so it is called directly.
            MdfReader reader = new MdfReader(path);
            try
            {
                if (!reader.ReadEverythingButData()) { throw new System.InvalidOperationException("The wrapper could not read " + path); }
                // The HD lines belong to the reading oracle only: the writing oracle compares the bytes it wrote with the
                // canonical lines of the engine, which has no HD dump, so the default dump keeps its lines unchanged.
                if (System.Environment.GetEnvironmentVariable(HeaderVariable) == "1") { AddHeader(lines, reader.File.Header); }
                MdfDataGroup[] groups = reader.File.DataGroups;
                lines.Add("dataGroups " + groups.Length.ToString(CultureInfo.InvariantCulture));
                for (int g = 0; g < groups.Length; g++)
                {
                    MdfDataGroup dataGroup = groups[g];
                    MdfChannelGroup[] channelGroups = dataGroup.ChannelGroups;
                    // The observers bind to the buffers the read fills, so they are made before the read, as the upstream test does.
                    MdfChannelObserver[][] observerSets = new MdfChannelObserver[channelGroups.Length][];
                    for (int k = 0; k < channelGroups.Length; k++)
                    {
                        observerSets[k] = global::MdfLibrary.MdfLibrary.CreateChannelObserverForChannelGroup(dataGroup, channelGroups[k]);
                    }
                    if (!reader.Open()) { throw new System.InvalidOperationException("The wrapper could not open " + path); }
                    bool dataRead = reader.ReadData(dataGroup);
                    reader.Close();
                    if (!dataRead) { throw new System.InvalidOperationException("The wrapper could not read the data of group " + g + "."); }
                    lines.Add("dg " + g.ToString(CultureInfo.InvariantCulture) + " channelGroups "
                        + channelGroups.Length.ToString(CultureInfo.InvariantCulture));
                    for (int j = 0; j < channelGroups.Length; j++)
                    {
                        MdfChannelGroup group = channelGroups[j];
                        string groupId = g.ToString(CultureInfo.InvariantCulture) + "." + j.ToString(CultureInfo.InvariantCulture);
                        MdfChannel[] channels = group.Channels;
                        lines.Add("cg " + groupId + " samples=" + group.NofSamples.ToString(CultureInfo.InvariantCulture)
                            + " source=" + SourceOf(group.SourceInformation) + " name=" + Text(group.Name)
                            + " comment=" + Text(group.Description));
                        for (int c = 0; c < channels.Length; c++)
                        {
                            MdfChannel channel = channels[c];
                            lines.Add("cn " + groupId + "." + c.ToString(CultureInfo.InvariantCulture)
                                + " master=" + (channel.Type == ChannelType.Master ? "1" : "0")
                                + " unit=" + Text(channel.Unit) + " name=" + Text(channel.Name)
                                + " conv=" + (channel.ChannelConversion == null ? "none" : "set")
                                + " comment=" + Text(channel.Description));
                        }
                        MdfChannelObserver[] observers = observerSets[j];
                        for (int c = 0; c < observers.Length; c++)
                        {
                            for (ulong s = 0UL; s < observers[c].NofSamples; s++)
                            {
                                lines.Add("v " + groupId + "." + c.ToString(CultureInfo.InvariantCulture)
                                    + " " + s.ToString(CultureInfo.InvariantCulture) + " " + Print(observers[c], s));
                            }
                        }
                    }
                }
            }
            finally { reader.Dispose(); }
            return lines;
        }

        // The HD block: the header fields the wrapper reports, one line, in a fixed order.
        private static void AddHeader(List<string> lines, MdfHeader header)
        {
            lines.Add("hd author=" + Text(header.Author) + " department=" + Text(header.Department)
                + " project=" + Text(header.Project) + " subject=" + Text(header.Subject)
                + " description=" + Text(header.Description) + " measurementId=" + Text(header.MeasurementId)
                + " recorderId=" + Text(header.RecorderId)
                + " recorderIndex=" + header.RecorderIndex.ToString(CultureInfo.InvariantCulture)
                + " startTime=" + header.StartTime.ToString(CultureInfo.InvariantCulture)
                + " startAngle=" + header.StartAngle.ToString("R", CultureInfo.InvariantCulture)
                + " angleUsed=" + Flag(header.IsStartAngleUsed)
                + " startDistance=" + header.StartDistance.ToString("R", CultureInfo.InvariantCulture)
                + " distanceUsed=" + Flag(header.IsStartDistanceUsed));
        }

        private static string Flag(bool value)
        {
            return value ? "1" : "0";
        }

        private static string Print(MdfChannelObserver observer, ulong sample)
        {
            // A text channel is read as text first; read as a number it would answer 0 and hide the text.
            bool isText = observer.Channel != null && (observer.Channel.DataType == ChannelDataType.StringAscii
                || observer.Channel.DataType == ChannelDataType.StringUTF8
                || observer.Channel.DataType == ChannelDataType.StringUTF16Le
                || observer.Channel.DataType == ChannelDataType.StringUTF16Be
                || (observer.Channel.ChannelConversion != null && observer.Channel.ChannelConversion.Type == ConversionType.ValueToText));
            string text = null;
            if (isText && observer.GetEngValueAsString(sample, ref text)) { return "s " + Text(text); }
            // A byte-array channel is compared by its raw bytes; read as a number the native reader answers an unrelated value.
            byte[] raw = null;
            if (observer.GetChannelValueAsArray(sample, ref raw)) { return "b " + Hex(raw); }
            if (observer.Channel != null && observer.Channel.DataType == ChannelDataType.ByteArray) { return "x"; }
            double number = 0.0;
            if (observer.GetEngValueAsFloat(sample, ref number)) { return "f " + number.ToString("R", CultureInfo.InvariantCulture); }
            text = null;
            if (observer.GetEngValueAsString(sample, ref text)) { return "s " + Text(text); }
            return "x";
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
            return (value ?? string.Empty).Replace("\r", "\\r").Replace("\n", "\\n");
        }
    }
}
