// Copyright (c) 2026 SeNS
// Sample: writes a file with two data groups and reads its structure back, like a file-information tool: the header metadata,
// the data groups, the channel groups with their sample counts, and every channel with its data type, unit and conversion.
using System;
using System.IO;

namespace Samples
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                Run();
                Console.WriteLine("ReadInfo: OK");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("ReadInfo: FAILED: " + error.Message);
                return 1;
            }
        }

        private static void Run()
        {
            string file = Path.Combine(Path.GetTempPath(), "mdflib-sample-info-" + Guid.NewGuid().ToString("N") + ".mf4");
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

        private static void Write(string file)
        {
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            try
            {
                if (!writer.Init(file)) { throw new InvalidOperationException("the writer did not open " + file); }
                writer.Header.Author = "ReadInfo sample";
                writer.Header.Department = "Test bench";
                writer.Header.Description = "Two groups: an engine and a brake";

                // Data group 1: engine. Speed has a linear conversion in km/h; Gear is a value-to-text table.
                MdfLibrary.MdfChannelGroup engine = writer.CreateDataGroup().ChannelGroups[0];
                engine.Name = "Engine";
                MdfLibrary.MdfChannel engineTime = Master(engine);
                engineTime.Name = "Time";
                MdfLibrary.MdfChannel speed = engine.CreateChannel();
                speed.Name = "Speed";
                speed.DataType = MdfLibrary.ChannelDataType.UnsignedIntegerLe;
                speed.BitCount = 16;
                speed.Unit = "km/h";
                MdfLibrary.MdfChannelConversion linear = speed.CreateChannelConversion();
                linear.Type = MdfLibrary.ConversionType.Linear;
                linear.Parameter(0, 0.0);
                linear.Parameter(1, 0.5);
                MdfLibrary.MdfChannel gear = engine.CreateChannel();
                gear.Name = "Gear";
                gear.DataType = MdfLibrary.ChannelDataType.UnsignedIntegerLe;
                gear.BitCount = 8;
                MdfLibrary.MdfChannelConversion table = gear.CreateChannelConversion();
                table.Type = MdfLibrary.ConversionType.ValueToText;
                table.Parameter(0, 1.0);
                table.Parameter(1, 2.0);
                table.Reference(0, "first");
                table.Reference(1, "second");
                table.Reference(2, "other");

                // Data group 2: brake. Pressure is a float in bar.
                MdfLibrary.MdfChannelGroup brake = writer.CreateDataGroup().ChannelGroups[0];
                brake.Name = "Brake";
                MdfLibrary.MdfChannel brakeTime = Master(brake);
                brakeTime.Name = "Time";
                MdfLibrary.MdfChannel pressure = brake.CreateChannel();
                pressure.Name = "Pressure";
                pressure.DataType = MdfLibrary.ChannelDataType.FloatLe;
                pressure.BitCount = 64;
                pressure.Unit = "bar";

                if (!writer.InitMeasurement()) { throw new InvalidOperationException("the measurement did not start"); }
                writer.StartMeasurement(1000000000UL);
                for (int row = 0; row < 3; row++)
                {
                    speed.SetChannelValue((ulong)(row * 20));
                    gear.SetChannelValue((ulong)(row + 1));
                    writer.SaveSample(engine, 1000000000UL * (ulong)(row + 1));
                    pressure.SetChannelValue(1.5 * row);
                    writer.SaveSample(brake, 1000000000UL * (ulong)(row + 1));
                }
                if (!writer.FinalizeMeasurement()) { throw new InvalidOperationException("the measurement was not finalized"); }
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static MdfLibrary.MdfChannel Master(MdfLibrary.MdfChannelGroup group)
        {
            MdfLibrary.MdfChannel master = group.CreateChannel();
            master.Type = MdfLibrary.ChannelType.Master;
            master.Sync = MdfLibrary.ChannelSyncType.Time;
            master.DataType = MdfLibrary.ChannelDataType.FloatLe;
            master.BitCount = 64;
            master.Unit = "s";
            return master;
        }

        private static void Read(string file)
        {
            MdfLibrary.MdfReader reader = new MdfLibrary.MdfReader(file);
            try
            {
                if (!reader.ReadEverythingButData()) { throw new InvalidOperationException("the header did not read"); }
                MdfLibrary.MdfHeader header = reader.Header;
                Console.WriteLine("File header");
                Console.WriteLine("  Author:      " + header.Author);
                Console.WriteLine("  Department:  " + header.Department);
                Console.WriteLine("  Description: " + header.Description);
                Console.WriteLine("  Attachments: " + header.Attachments.Length);

                MdfLibrary.MdfDataGroup[] dataGroups = header.DataGroups;
                if (dataGroups.Length != 2) { throw new InvalidOperationException("found " + dataGroups.Length + " data groups, expected 2"); }

                for (int index = 0; index < dataGroups.Length; index++)
                {
                    if (!reader.ReadData(dataGroups[index])) { throw new InvalidOperationException("the samples of group " + index + " did not read"); }
                    Console.WriteLine();
                    Console.WriteLine("Data group " + index);
                    foreach (MdfLibrary.MdfChannelGroup group in dataGroups[index].ChannelGroups)
                    {
                        Console.WriteLine("  Channel group " + group.Name + ": " + group.NofSamples + " samples");
                        if (group.NofSamples != 3UL) { throw new InvalidOperationException("group " + group.Name + " has " + group.NofSamples + " samples, expected 3"); }
                        foreach (MdfLibrary.MdfChannel channel in group.Channels)
                        {
                            Console.WriteLine("    " + Describe(channel));
                        }
                    }
                }
            }
            finally
            {
                reader.Dispose();
            }
        }

        private static string Describe(MdfLibrary.MdfChannel channel)
        {
            string text = channel.Name + " (" + channel.DataType + ", " + channel.BitCount + " bit, " + channel.Type + ")";
            if (channel.UnitUsed) { text += " unit " + channel.Unit; }
            if (channel.ChannelConversion != null) { text += " conversion " + channel.ChannelConversion.Type; }
            return text;
        }
    }
}
