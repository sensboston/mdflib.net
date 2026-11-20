// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Sample: writes a raw value through two conversions, a rational function and a value-to-text table, and reads the
// engineering values back. The rational function is 2 * raw + 1; the text table maps 1 to "one" and 2 to "two".
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
                Console.WriteLine("Conversions: OK");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("Conversions: FAILED: " + error.Message);
                return 1;
            }
        }

        private static void Run()
        {
            string file = Path.Combine(Path.GetTempPath(), "mdflib-sample-conversions-" + Guid.NewGuid().ToString("N") + ".mf4");
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

                // Rational: P2 = 2, P3 = 1, P6 = 1, so the engineering value is 2 * raw + 1.
                MdfLibrary.MdfChannel scaled = group.CreateChannel();
                scaled.Name = "Scaled";
                scaled.DataType = MdfLibrary.ChannelDataType.UnsignedIntegerLe;
                scaled.BitCount = 16;
                MdfLibrary.MdfChannelConversion rational = scaled.CreateChannelConversion();
                rational.Type = MdfLibrary.ConversionType.Rational;
                double[] six = { 0.0, 2.0, 1.0, 0.0, 0.0, 1.0 };
                for (ushort index = 0; index < six.Length; index++) { rational.Parameter(index, six[index]); }

                // Value to text: 1 is "one", 2 is "two", anything else is the default "other".
                MdfLibrary.MdfChannel words = group.CreateChannel();
                words.Name = "Words";
                words.DataType = MdfLibrary.ChannelDataType.UnsignedIntegerLe;
                words.BitCount = 16;
                MdfLibrary.MdfChannelConversion table = words.CreateChannelConversion();
                table.Type = MdfLibrary.ConversionType.ValueToText;
                table.Parameter(0, 1.0);
                table.Parameter(1, 2.0);
                table.Reference(0, "one");
                table.Reference(1, "two");
                table.Reference(2, "other");

                if (!writer.InitMeasurement()) { throw new InvalidOperationException("the measurement did not start"); }
                writer.StartMeasurement(1000000000UL);
                ulong[] raw = { 1, 2, 3 };
                for (int sample = 0; sample < raw.Length; sample++)
                {
                    scaled.SetChannelValue(raw[sample]);
                    words.SetChannelValue(raw[sample]);
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

                MdfLibrary.MdfChannelObserver scaled = null;
                MdfLibrary.MdfChannelObserver words = null;
                foreach (MdfLibrary.MdfChannelObserver observer in observers)
                {
                    if (observer == null) { continue; }
                    if (observer.Name == "Scaled") { scaled = observer; }
                    if (observer.Name == "Words") { words = observer; }
                }
                if (scaled == null || words == null) { throw new InvalidOperationException("a channel is missing"); }

                double value = 0.0;
                if (!scaled.GetEngValueAsFloat(0, ref value) || value != 3.0) { throw new InvalidOperationException("raw 1 gives " + value + ", expected 3"); }
                if (!scaled.GetEngValueAsFloat(2, ref value) || value != 7.0) { throw new InvalidOperationException("raw 3 gives " + value + ", expected 7"); }
                Console.WriteLine("Scaled: raw 1 -> " + "3, raw 3 -> 7 (2 * raw + 1)");

                string text = null;
                if (!words.GetEngValueAsString(0, ref text) || text != "one") { throw new InvalidOperationException("raw 1 gives text " + text); }
                if (!words.GetEngValueAsString(1, ref text) || text != "two") { throw new InvalidOperationException("raw 2 gives text " + text); }
                if (!words.GetEngValueAsString(2, ref text) || text != "other") { throw new InvalidOperationException("raw 3 gives text " + text); }
                Console.WriteLine("Words: 1 -> one, 2 -> two, 3 -> other");
            }
            finally
            {
                reader.Dispose();
            }
        }
    }
}
