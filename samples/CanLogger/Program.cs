// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Sample: writes CAN data frames with standard and extended identifiers into a bus log (MdfWriterType.MdfBusLogger), reads the
// frame group back through channel observers and prints the frames. The bus log keeps the frames in the CAN_DataFrame group;
// its components are named CAN_DataFrame.ID, CAN_DataFrame.IDE and CAN_DataFrame.DLC.
using System;
using System.IO;

namespace Samples
{
    internal static class Program
    {
        private const ulong Start = 1000000000UL;
        private const ulong Step = 100000000UL;

        private static readonly uint[] Identifiers = { 0x123u, 0x7FFu, 0x18DAF110u, 0x1FFFFFFFu };
        private static readonly bool[] Extended = { false, false, true, true };
        private static readonly byte[][] Payloads =
        {
            new byte[] { 0x01, 0x02 },
            new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77 },
            new byte[] { 0xAA, 0xBB, 0xCC },
            new byte[0],
        };

        private static int Main()
        {
            try
            {
                Run();
                Console.WriteLine("CanLogger: OK");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("CanLogger: FAILED: " + error);
                return 1;
            }
        }

        private static void Run()
        {
            string file = Path.Combine(Path.GetTempPath(), "mdflib-sample-can-" + Guid.NewGuid().ToString("N") + ".mf4");
            try
            {
                Write(file);
                Read(file);
            }
            finally
            {
                File.Delete(file);
            }
        }

        private static MdfLibrary.CanMessage Frame(uint identifier, bool extended, byte[] payload)
        {
            MdfLibrary.CanMessage message = new MdfLibrary.CanMessage();
            message.TypeOfMessage = MdfLibrary.CanMessageType.Can_DataFrame;
            message.MessageId = identifier;
            message.ExtendedId = extended;
            message.DataBytes = payload;
            message.Dlc = (byte)payload.Length;
            return message;
        }

        private static void Write(string file)
        {
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.MdfBusLogger);
            try
            {
                if (!writer.Init(file)) { throw new InvalidOperationException("the writer did not open " + file); }
                writer.BusType = MdfLibrary.MdfBusType.CAN;
                if (!writer.CreateBusLogConfiguration()) { throw new InvalidOperationException("the bus log configuration was not created"); }

                // The bus log creates its CAN frame groups in one data group; the data frame group takes the frames.
                MdfLibrary.MdfDataGroup bus = writer.Header.LastDataGroup;
                MdfLibrary.MdfChannelGroup frames = null;
                foreach (MdfLibrary.MdfChannelGroup group in bus.ChannelGroups)
                {
                    if (group.Name == "CAN_DataFrame") { frames = group; }
                }
                if (frames == null) { throw new InvalidOperationException("the CAN_DataFrame group is missing"); }

                if (!writer.InitMeasurement()) { throw new InvalidOperationException("the measurement did not start"); }
                writer.StartMeasurement(Start);
                ulong time = Start;
                for (int index = 0; index < Identifiers.Length; index++)
                {
                    writer.SaveCanMessage(frames, time, Frame(Identifiers[index], Extended[index], Payloads[index]));
                    time += Step;
                }
                if (!writer.FinalizeMeasurement()) { throw new InvalidOperationException("the measurement was not finalized"); }
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void Read(string file)
        {
            MdfLibrary.MdfReader reader = new MdfLibrary.MdfReader(file);
            try
            {
                if (!reader.ReadEverythingButData()) { throw new InvalidOperationException("the header did not read"); }
                MdfLibrary.MdfDataGroup dataGroup = reader[0];
                if (!reader.ReadData(dataGroup)) { throw new InvalidOperationException("the frames did not read"); }

                MdfLibrary.MdfChannelGroup frames = null;
                foreach (MdfLibrary.MdfChannelGroup group in dataGroup.ChannelGroups)
                {
                    if (group.Name == "CAN_DataFrame") { frames = group; }
                }
                if (frames == null) { throw new InvalidOperationException("the CAN_DataFrame group is missing in the file"); }

                MdfLibrary.MdfChannelObserver[] observers = MdfLibrary.MdfLibrary.CreateChannelObserverForChannelGroup(dataGroup, frames);
                MdfLibrary.MdfChannelObserver id = Component(observers, "ID");
                MdfLibrary.MdfChannelObserver ide = Component(observers, "IDE");
                MdfLibrary.MdfChannelObserver dlc = Component(observers, "DLC");

                // Count the frames: the first sample without an identifier ends the loop.
                ulong count = 0UL;
                ulong probe = 0UL;
                while (id.GetChannelValueAsUnsigned(count, ref probe)) { count++; }
                if (count != (ulong)Identifiers.Length) { throw new InvalidOperationException("found " + count + " frames, expected " + Identifiers.Length); }

                for (ulong index = 0; index < count; index++)
                {
                    ulong identifier = 0UL;
                    ulong extended = 0UL;
                    ulong length = 0UL;
                    id.GetChannelValueAsUnsigned(index, ref identifier);
                    ide.GetChannelValueAsUnsigned(index, ref extended);
                    dlc.GetChannelValueAsUnsigned(index, ref length);
                    bool isExtended = extended != 0UL;
                    if (identifier != Identifiers[index] || isExtended != Extended[index] || length != (ulong)Payloads[index].Length)
                    {
                        throw new InvalidOperationException("frame " + index + " reads as id " + identifier + ", extended " + isExtended + ", dlc " + length);
                    }
                    Console.WriteLine("frame " + index + ": " + (isExtended ? "extended" : "standard") + " id 0x" + identifier.ToString("X")
                        + ", dlc " + length + ", data " + BitConverter.ToString(Payloads[index]));
                }
            }
            finally
            {
                reader.Dispose();
            }
        }

        // A component of the frame group is named either by its own name or with the group name in front of it.
        private static MdfLibrary.MdfChannelObserver Component(MdfLibrary.MdfChannelObserver[] observers, string field)
        {
            foreach (MdfLibrary.MdfChannelObserver observer in observers)
            {
                if (observer == null) { continue; }
                if (observer.Name == field || observer.Name == "CAN_DataFrame." + field) { return observer; }
            }
            string names = string.Empty;
            foreach (MdfLibrary.MdfChannelObserver observer in observers)
            {
                if (observer != null) { names += (names.Length == 0 ? string.Empty : ", ") + observer.Name; }
            }
            throw new InvalidOperationException("the component " + field + " is missing; the channels are: " + names);
        }
    }
}
