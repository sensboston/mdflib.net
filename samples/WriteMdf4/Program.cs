// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Sample: writes a small MDF 4.10 file with a master channel and an unsigned counter, reads it back and checks the counter.
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
                Console.WriteLine("WriteMdf4: OK");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("WriteMdf4: FAILED: " + error.Message);
                return 1;
            }
        }

        private static void Run()
        {
            string file = Path.Combine(Path.GetTempPath(), "mdflib-sample-write-" + Guid.NewGuid().ToString("N") + ".mf4");
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
                group.Name = "Counter";

                // The master channel holds the time of each sample; SaveSample supplies it in nanoseconds.
                MdfLibrary.MdfChannel master = group.CreateChannel();
                master.Name = "Time";
                master.Type = MdfLibrary.ChannelType.Master;
                master.Sync = MdfLibrary.ChannelSyncType.Time;
                master.DataType = MdfLibrary.ChannelDataType.FloatLe;
                master.BitCount = 64;

                MdfLibrary.MdfChannel count = group.CreateChannel();
                count.Name = "Count";
                count.DataType = MdfLibrary.ChannelDataType.UnsignedIntegerLe;
                count.BitCount = 32;

                if (!writer.InitMeasurement()) { throw new InvalidOperationException("the measurement did not start"); }
                writer.StartMeasurement(1000000000UL);
                for (ulong sample = 0; sample < 3; sample++)
                {
                    count.SetChannelValue(sample + 1);
                    writer.SaveSample(group, 1000000000UL * (sample + 1));
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
                MdfLibrary.MdfChannelObserver counter = null;
                foreach (MdfLibrary.MdfChannelObserver observer in observers)
                {
                    if (observer != null && observer.Name == "Count") { counter = observer; }
                }
                if (counter == null) { throw new InvalidOperationException("the channel Count is missing"); }

                for (ulong sample = 0; sample < 3; sample++)
                {
                    ulong value = 0UL;
                    if (!counter.GetChannelValueAsUnsigned(sample, ref value)) { throw new InvalidOperationException("no value for sample " + sample); }
                    if (value != sample + 1) { throw new InvalidOperationException("sample " + sample + " is " + value + ", expected " + (sample + 1)); }
                    Console.WriteLine("sample " + sample + ": Count = " + value);
                }
            }
            finally
            {
                reader.Dispose();
            }
        }
    }
}
