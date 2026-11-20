// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Sample: writes an MDF 3.30 file (the older format, with big-endian and little-endian integers), reads it back and checks
// the values. A master channel without a unit reads back with the unit of seconds.
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
                Console.WriteLine("Mdf3RoundTrip: OK");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("Mdf3RoundTrip: FAILED: " + error.Message);
                return 1;
            }
        }

        private static void Run()
        {
            string file = Path.Combine(Path.GetTempPath(), "mdflib-sample-mdf3-" + Guid.NewGuid().ToString("N") + ".mdf");
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
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf3Basic);
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
                MdfLibrary.MdfChannel big = group.CreateChannel();
                big.Name = "Big";
                big.DataType = MdfLibrary.ChannelDataType.UnsignedIntegerBe;
                big.BitCount = 16;

                if (!writer.InitMeasurement()) { throw new InvalidOperationException("the measurement did not start"); }
                writer.StartMeasurement(1000000000UL);
                for (int sample = 0; sample < 3; sample++)
                {
                    count.SetChannelValue((ulong)(5 + sample));
                    big.SetChannelValue((ulong)(300 + sample));
                    writer.SaveSample(group, 1000000000UL * (ulong)(sample + 1));
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

                MdfLibrary.MdfChannelObserver time = null;
                MdfLibrary.MdfChannelObserver count = null;
                MdfLibrary.MdfChannelObserver big = null;
                foreach (MdfLibrary.MdfChannelObserver observer in observers)
                {
                    if (observer == null) { continue; }
                    if (observer.Name == "Time") { time = observer; }
                    if (observer.Name == "Count") { count = observer; }
                    if (observer.Name == "Big") { big = observer; }
                }
                if (time == null || count == null || big == null) { throw new InvalidOperationException("a channel is missing"); }
                if (time.Unit != "s" || !time.IsMaster()) { throw new InvalidOperationException("the master unit is " + time.Unit + ", expected s"); }
                Console.WriteLine("master channel " + time.Name + " in " + time.Unit);

                ulong value = 0UL;
                if (!count.GetChannelValueAsUnsigned(2, ref value) || value != 7UL) { throw new InvalidOperationException("Count of sample 2 is " + value + ", expected 7"); }
                if (!big.GetChannelValueAsUnsigned(1, ref value) || value != 301UL) { throw new InvalidOperationException("Big of sample 1 is " + value + ", expected 301"); }
                Console.WriteLine("Count[2] = 7, Big[1] = 301");
            }
            finally
            {
                reader.Dispose();
            }
        }
    }
}
