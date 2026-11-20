// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Sample: writes a file with an unsigned counter and a float level, then reads it through channel observers. The number of
// samples is found by reading until the observer reports no value: a read past the last sample returns false.
using System;
using System.IO;

namespace Samples
{
    internal static class Program
    {
        private static readonly double[] Levels = { 0.5, 1.25, -2.0, 4.75 };

        private static int Main()
        {
            try
            {
                Run();
                Console.WriteLine("ReadObservers: OK");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("ReadObservers: FAILED: " + error.Message);
                return 1;
            }
        }

        private static void Run()
        {
            string file = Path.Combine(Path.GetTempPath(), "mdflib-sample-observers-" + Guid.NewGuid().ToString("N") + ".mf4");
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
                MdfLibrary.MdfChannelGroup group = writer.CreateDataGroup().ChannelGroups[0];
                MdfLibrary.MdfChannel master = group.CreateChannel();
                master.Name = "Time";
                master.Type = MdfLibrary.ChannelType.Master;
                master.Sync = MdfLibrary.ChannelSyncType.Time;
                master.DataType = MdfLibrary.ChannelDataType.FloatLe;
                master.BitCount = 64;
                MdfLibrary.MdfChannel count = group.CreateChannel();
                count.Name = "Count";
                count.DataType = MdfLibrary.ChannelDataType.UnsignedIntegerLe;
                count.BitCount = 16;
                MdfLibrary.MdfChannel level = group.CreateChannel();
                level.Name = "Level";
                level.DataType = MdfLibrary.ChannelDataType.FloatLe;
                level.BitCount = 64;

                if (!writer.InitMeasurement()) { throw new InvalidOperationException("the measurement did not start"); }
                writer.StartMeasurement(1000000000UL);
                for (int index = 0; index < Levels.Length; index++)
                {
                    count.SetChannelValue((ulong)(index * 10));
                    level.SetChannelValue(Levels[index]);
                    writer.SaveSample(group, 1000000000UL * (ulong)(index + 1));
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
                if (!reader.ReadData(dataGroup)) { throw new InvalidOperationException("the samples did not read"); }

                MdfLibrary.MdfChannelObserver[] observers = MdfLibrary.MdfLibrary.CreateChannelObserverForDataGroup(dataGroup);
                MdfLibrary.MdfChannelObserver count = Find(observers, "Count");
                MdfLibrary.MdfChannelObserver level = Find(observers, "Level");

                // Count the samples: the first sample that has no value ends the loop.
                ulong samples = 0UL;
                ulong probe = 0UL;
                while (count.GetChannelValueAsUnsigned(samples, ref probe))
                {
                    samples++;
                }
                if (samples != (ulong)Levels.Length) { throw new InvalidOperationException("found " + samples + " samples, expected " + Levels.Length); }
                Console.WriteLine("samples in the group: " + samples);

                for (ulong sample = 0; sample < samples; sample++)
                {
                    ulong counted = 0UL;
                    double value = 0.0;
                    count.GetChannelValueAsUnsigned(sample, ref counted);
                    level.GetChannelValueAsFloat(sample, ref value);
                    if (counted != sample * 10UL) { throw new InvalidOperationException("Count of sample " + sample + " is " + counted); }
                    if (value != Levels[sample]) { throw new InvalidOperationException("Level of sample " + sample + " is " + value); }
                    Console.WriteLine("sample " + sample + ": Count = " + counted + ", Level = " + value);
                }
            }
            finally
            {
                reader.Dispose();
            }
        }

        private static MdfLibrary.MdfChannelObserver Find(MdfLibrary.MdfChannelObserver[] observers, string name)
        {
            foreach (MdfLibrary.MdfChannelObserver observer in observers)
            {
                if (observer != null && observer.Name == name) { return observer; }
            }
            throw new InvalidOperationException("the channel " + name + " is missing");
        }
    }
}
