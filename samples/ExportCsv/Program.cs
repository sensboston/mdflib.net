// Copyright (c) 2026 SeNS
// Sample: writes a channel group with a linear conversion and a unit, exports every channel of the group to CSV, and checks
// the rows. The header of each column is "name [unit]"; a channel without a unit has the plain name as its header.
using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Samples
{
    internal static class Program
    {
        private const int Rows = 5;

        private static int Main()
        {
            try
            {
                Run();
                Console.WriteLine("ExportCsv: OK");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("ExportCsv: FAILED: " + error.Message);
                return 1;
            }
        }

        private static void Run()
        {
            string file = Path.Combine(Path.GetTempPath(), "mdflib-sample-export-" + Guid.NewGuid().ToString("N") + ".mf4");
            string csv = Path.Combine(Path.GetTempPath(), "mdflib-sample-export-" + Guid.NewGuid().ToString("N") + ".csv");
            try
            {
                Write(file);
                Export(file, csv);
                Check(csv);
            }
            finally
            {
                File.Delete(file);
                File.Delete(csv);
            }
        }

        private static void Write(string file)
        {
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            try
            {
                if (!writer.Init(file)) { throw new InvalidOperationException("the writer did not open " + file); }
                MdfLibrary.MdfChannelGroup group = writer.CreateDataGroup().ChannelGroups[0];
                group.Name = "Drive";

                MdfLibrary.MdfChannel time = group.CreateChannel();
                time.Name = "Time";
                time.Type = MdfLibrary.ChannelType.Master;
                time.Sync = MdfLibrary.ChannelSyncType.Time;
                time.DataType = MdfLibrary.ChannelDataType.FloatLe;
                time.BitCount = 64;
                time.Unit = "s";

                // Speed is stored as a raw count; the linear conversion gives km/h: value = 0 + 0.5 * raw.
                MdfLibrary.MdfChannel speed = group.CreateChannel();
                speed.Name = "Speed";
                speed.DataType = MdfLibrary.ChannelDataType.UnsignedIntegerLe;
                speed.BitCount = 16;
                speed.Unit = "km/h";
                MdfLibrary.MdfChannelConversion linear = speed.CreateChannelConversion();
                linear.Type = MdfLibrary.ConversionType.Linear;
                linear.Parameter(0, 0.0);
                linear.Parameter(1, 0.5);

                MdfLibrary.MdfChannel count = group.CreateChannel();
                count.Name = "Count";
                count.DataType = MdfLibrary.ChannelDataType.UnsignedIntegerLe;
                count.BitCount = 16;

                if (!writer.InitMeasurement()) { throw new InvalidOperationException("the measurement did not start"); }
                writer.StartMeasurement(1000000000UL);
                for (int row = 0; row < Rows; row++)
                {
                    speed.SetChannelValue((ulong)(row * 10));
                    count.SetChannelValue((ulong)(row + 1));
                    writer.SaveSample(group, 1000000000UL * (ulong)(row + 1));
                }
                if (!writer.FinalizeMeasurement()) { throw new InvalidOperationException("the measurement was not finalized"); }
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void Export(string file, string csv)
        {
            MdfLibrary.MdfReader reader = new MdfLibrary.MdfReader(file);
            try
            {
                if (!reader.ReadEverythingButData()) { throw new InvalidOperationException("the header did not read"); }
                MdfLibrary.MdfDataGroup dataGroup = reader[0];
                if (!reader.ReadData(dataGroup)) { throw new InvalidOperationException("the samples did not read"); }
                MdfLibrary.MdfChannelObserver[] observers = MdfLibrary.MdfLibrary.CreateChannelObserverForDataGroup(dataGroup);

                StringBuilder text = new StringBuilder();
                for (int column = 0; column < observers.Length; column++)
                {
                    if (column > 0) { text.Append(','); }
                    text.Append(Cell(Header(observers[column])));
                }
                text.Append('\n');

                for (ulong row = 0; row < (ulong)Rows; row++)
                {
                    for (int column = 0; column < observers.Length; column++)
                    {
                        double value = 0.0;
                        if (!observers[column].GetEngValueAsFloat(row, ref value))
                        {
                            throw new InvalidOperationException("no value in column " + observers[column].Name + " for row " + row);
                        }
                        if (column > 0) { text.Append(','); }
                        text.Append(value.ToString("R", CultureInfo.InvariantCulture));
                    }
                    text.Append('\n');
                }
                File.WriteAllText(csv, text.ToString(), new UTF8Encoding(false));
            }
            finally
            {
                reader.Dispose();
            }
        }

        // The column header: the channel name, followed by its unit in square brackets when the channel has one.
        private static string Header(MdfLibrary.MdfChannelObserver observer)
        {
            string unit = observer.Unit;
            return string.IsNullOrEmpty(unit) ? observer.Name : observer.Name + " [" + unit + "]";
        }

        private static string Cell(string text)
        {
            if (text.IndexOfAny(new[] { ',', '"', '\n' }) < 0) { return text; }
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        private static void Check(string csv)
        {
            string[] lines = File.ReadAllLines(csv);
            if (lines.Length != Rows + 1) { throw new InvalidOperationException("the CSV has " + lines.Length + " lines, expected " + (Rows + 1)); }
            string[] header = lines[0].Split(',');
            if (Array.IndexOf(header, "Speed [km/h]") < 0 || Array.IndexOf(header, "Time [s]") < 0 || Array.IndexOf(header, "Count") < 0)
            {
                throw new InvalidOperationException("the header is " + lines[0]);
            }
            int speed = Array.IndexOf(header, "Speed [km/h]");
            for (int row = 0; row < Rows; row++)
            {
                double value = double.Parse(lines[row + 1].Split(',')[speed], CultureInfo.InvariantCulture);
                double expected = row * 10 * 0.5;
                if (value != expected) { throw new InvalidOperationException("row " + row + " speed is " + value + ", expected " + expected); }
            }
            Console.WriteLine(lines[0]);
            Console.WriteLine(lines[1]);
            Console.WriteLine("... " + (lines.Length - 1) + " rows in total");
        }
    }
}
