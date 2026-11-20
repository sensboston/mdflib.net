// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace MdfLib.Samples
{
    // the managed samples are part of the build and must keep working.
    public sealed class SampleTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "mdflib-samples-" + Guid.NewGuid().ToString("N"));

        public SampleTests() { Directory.CreateDirectory(root); }

        public void Dispose()
        {
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }

        private string Save(string name, Action<Stream> write)
        {
            string path = Path.Combine(root, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite)) { write(stream); }
            return path;
        }

        private static int Run(Func<string[], TextWriter, TextWriter, int> tool, out string output, out string error, params string[] args)
        {
            StringWriter o = new StringWriter(), e = new StringWriter();
            int code = tool(args, o, e);
            output = o.ToString();
            error = e.ToString();
            return code;
        }

        [Theory]
        [InlineData("4.10", false)]
        [InlineData("4.10", true)]
        [InlineData("4.20", false)]
        [InlineData("3.30", false)]
        public void SignalExampleRoundTripsEveryDataType(string version, bool compress)
        {
            MemoryStream file = new MemoryStream();
            LibraryExamples.WriteSignals(file, version, 300, compress);
            Assert.Equal(Enumerable.Range(0, 300).Select(i => i * 0.01), LibraryExamples.ReadChannel(file, "Time").Select(v => v.FloatingPointValue));
            Assert.Equal(Enumerable.Range(0, 300).Select(i => (ulong)i * 1000), LibraryExamples.ReadChannel(file, "UnsignedLe").Select(v => v.UnsignedValue));
            Assert.Equal(Enumerable.Range(0, 300).Select(i => (ulong)i), LibraryExamples.ReadChannel(file, "UnsignedBe").Select(v => v.UnsignedValue));
            Assert.Equal(Enumerable.Range(0, 300).Select(i => (long)-i * 1000), LibraryExamples.ReadChannel(file, "SignedLe").Select(v => v.SignedValue));
            Assert.Equal(Enumerable.Range(0, 300).Select(i => (long)(sbyte)i), LibraryExamples.ReadChannel(file, "SignedBe").Select(v => v.SignedValue));
            Assert.Equal(Enumerable.Range(0, 300).Select(i => i * 0.5), LibraryExamples.ReadChannel(file, "FloatLe").Select(v => v.FloatingPointValue));
            Assert.Equal(Enumerable.Range(0, 300).Select(i => i * -0.25), LibraryExamples.ReadChannel(file, "FloatBe").Select(v => v.FloatingPointValue));
            Assert.Equal(Enumerable.Range(0, 300).Select(i => "S" + i), LibraryExamples.ReadChannel(file, "String").Select(v => v.TextValue));
            List<MdfSampleValue> scaled = LibraryExamples.ReadChannel(file, "Scaled");
            for (int i = 0; i < 300; i++) { Assert.Equal((i % 1000) * 0.01 - 5, scaled[i].FloatingPointValue, 12); }
            if (version != "3.30")
            {
                List<MdfSampleValue> arrays = LibraryExamples.ReadChannel(file, "Array");
                Assert.Equal(new byte[] { 7, 0, 0, 1, 2 }, arrays[7].GetByteArrayValue());
            }
            Assert.Throws<ArgumentException>(() => LibraryExamples.ReadChannel(file, "Missing"));
            file.Position = 0;
            using (MdfReader reader = MdfReader.Open(file, true)) { Assert.True(MdfValidator.Validate(reader).IsValid); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CanBusLogExampleWritesOneFramePerSecond(bool compress)
        {
            MemoryStream file = new MemoryStream();
            LibraryExamples.WriteCanBusLog(file, compress, 1000);
            List<MdfBusMessage> frames = LibraryExamples.ReadCanBusLog(file);
            Assert.Equal(1000, frames.Count);
            Assert.All(frames, f => Assert.Equal(1234u, f.Id));
            Assert.All(frames, f => Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, f.Data));
            Assert.Equal(Enumerable.Range(0, 1000).Select(i => (double)i), frames.Select(f => f.Timestamp));
        }

        [Fact]
        public void Mdf2CsvConvertsFoldersWithUpstreamNamesAndSkipsExistingFiles()
        {
            Save("in/signals.mf4", s => LibraryExamples.WriteSignals(s, "4.10", 5));
            Save("in/sub/can log.mf4", s => LibraryExamples.WriteCanBusLog(s, false, 3));
            File.WriteAllText(Path.Combine(root, "in", "notes.txt"), "not an MDF file");
            string output, error;
            string target = Path.Combine(root, "csv");
            Assert.Equal(0, Run(Mdf2CsvTool.Run, out output, out error, Path.Combine(root, "in"), "-O", target, "-X"));
            Assert.Equal("", error);
            string[] signals = File.ReadAllLines(Path.Combine(target, "signals.csv"));
            Assert.Equal(6, signals.Length);
            Assert.StartsWith("Time [s],UnsignedLe [°C],UnsignedBe", signals[0]);
            Assert.StartsWith("0.01,1000,1,-1000,1,0.5,-0.25,S1,", signals[2]);
            string can = Directory.GetFiles(target, "canlog_DG_*.csv").Single();
            string[] canLines = File.ReadAllLines(can);
            Assert.Equal(4, canLines.Length);
            Assert.Contains("CAN_DataFrame.ID", canLines[0]);
            Assert.DoesNotContain("CAN_DataFrame,", canLines[0] + ",");
            Assert.Contains("signals.mf4 -> signals.csv", output);

            Assert.Equal(0, Run(Mdf2CsvTool.Run, out output, out error, "-i", Path.Combine(root, "in", "signals.mf4"), "-O", target));
            Assert.Contains("Skipping. CSV file exist.", output);
            File.WriteAllText(Path.Combine(target, "signals.csv"), "stale");
            Assert.Equal(0, Run(Mdf2CsvTool.Run, out output, out error, "--force", "-q", Path.Combine(root, "in", "signals.mf4"), "-O", target));
            Assert.Equal("", output);
            Assert.Equal(6, File.ReadAllLines(Path.Combine(target, "signals.csv")).Length);
        }

        [Fact]
        public void Mdf2CsvWritesNextToTheInputAppendsOutToFolderTargetsAndDeletesOnRequest()
        {
            string input = Save("one/data.mf4", s => LibraryExamples.WriteSignals(s, "3.30", 2));
            string output, error;
            Assert.Equal(0, Run(Mdf2CsvTool.Run, out output, out error, input));
            Assert.True(File.Exists(Path.Combine(root, "one", "data.csv")));
            Assert.Equal(0, Run(Mdf2CsvTool.Run, out output, out error, Path.Combine(root, "one"), "-O", Path.Combine(root, "target"), "-d"));
            Assert.True(File.Exists(Path.Combine(root, "target_out", "data.csv")));
            Assert.False(File.Exists(input));
        }

        [Fact]
        public void Mdf2CsvReportsBadArgumentsAndInputs()
        {
            string output, error;
            Assert.Equal(0, Run(Mdf2CsvTool.Run, out output, out error, "--help"));
            Assert.Contains("Usage: mdf2csv", output);
            Assert.Equal(0, Run(Mdf2CsvTool.Run, out output, out error, "-V"));
            Assert.Contains("mdf2csv", output);
            Assert.Equal(1, Run(Mdf2CsvTool.Run, out output, out error, "--bogus"));
            Assert.Contains("Unknown option --bogus", error);
            Assert.Equal(1, Run(Mdf2CsvTool.Run, out output, out error, "-O"));
            Assert.Equal(1, Run(Mdf2CsvTool.Run, out output, out error, Path.Combine(root, "missing.mf4")));
            Assert.Contains("doesn't exist", error);
            Assert.Contains("There is no MDF files to convert.", error);
            string text = Path.Combine(root, "plain.mf4");
            File.WriteAllText(text, "plain text, not MDF");
            Assert.Equal(1, Run(Mdf2CsvTool.Run, out output, out error, text));
            Assert.Contains("isn't an MDF file", error);
            string good = Save("good.mf4", s => LibraryExamples.WriteSignals(s, "4.20", 1));
            Assert.Equal(1, Run(Mdf2CsvTool.Run, out output, out error, good, Path.Combine(root, "missing.mf4")));
            Assert.True(File.Exists(Path.Combine(root, "good.csv")));
            Assert.Equal("abc_d-e", Mdf2CsvTool.StripName("a b.c_d-e"));
        }

        [Fact]
        public void Mdf2CsvSkipsUnreadableFiles()
        {
            string broken = Path.Combine(root, "broken.mf4");
            byte[] bytes = new byte[64];
            Encoding.ASCII.GetBytes("MDF     4.10    ").CopyTo(bytes, 0);
            File.WriteAllBytes(broken, bytes);
            string output, error;
            int code = Run(Mdf2CsvTool.Run, out output, out error, broken);
            Assert.Equal(1, code);
            Assert.True(error.Contains("Skipping. Invalid MDF file.") || error.Contains("isn't an MDF file"), error);
        }

        [Fact]
        public void MdfInfoPrintsTheTreeSamplesAndValidation()
        {
            string path = Save("info.mf4", s => LibraryExamples.WriteSignals(s, "4.10", 4));
            string output, error;
            Assert.Equal(0, Run(MdfInfoTool.Run, out output, out error, path, "--samples", "0", "2", "--validate"));
            Assert.Equal("", error);
            Assert.Contains("MDF 4.10 (", output);
            Assert.Contains("CG 0 Test samples=4", output);
            Assert.Contains("CN Time [s] FloatingPoint 64 bits master", output);
            Assert.Contains("CN Scaled [V] UnsignedInteger 16 bits cc=Linear", output);
            Assert.Contains("Time\tUnsignedLe\tUnsignedBe", output);
            Assert.Contains("0.01\t1000\t1\t-1000\t1\t0.5\t-0.25\tS1\t", output);
            Assert.DoesNotContain("0.02\t2000", output);
            Assert.Contains("Valid.", output);

            string bus = Save("bus.mf4", s => LibraryExamples.WriteCanBusLog(s, true, 2));
            Assert.Equal(0, Run(MdfInfoTool.Run, out output, out error, bus));
            Assert.Contains("CAN_DataFrame samples=2", output);
            Assert.Contains("CN CAN_DataFrame.ID", output);
        }

        [Fact]
        public void MdfInfoReportsBadArgumentsAndFiles()
        {
            string output, error;
            Assert.Equal(1, Run(MdfInfoTool.Run, out output, out error));
            Assert.Contains("Usage: mdfinfo", error);
            Assert.Equal(1, Run(MdfInfoTool.Run, out output, out error, Path.Combine(root, "none.mf4")));
            Assert.Contains("Cannot read", error);
            string path = Save("x.mf4", s => LibraryExamples.WriteSignals(s, "4.10", 1));
            Assert.Equal(1, Run(MdfInfoTool.Run, out output, out error, path, "--bogus"));
            Assert.Equal(1, Run(MdfInfoTool.Run, out output, out error, path, "--samples", "9"));
            Assert.Contains("No channel group 9.", error);
            string text = Path.Combine(root, "t.mf4");
            File.WriteAllText(text, "plain text, not MDF at all, long enough for an ID block................................");
            Assert.Equal(1, Run(MdfInfoTool.Run, out output, out error, text));
            Assert.Contains("Cannot read", error);
        }
    }
}
