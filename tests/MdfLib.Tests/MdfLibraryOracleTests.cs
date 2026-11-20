// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// The facade leg of the oracle job. The scenario itself is tools/oracle/Scenario.cs,
// the same source the native wrapper is compiled with; this test writes it, reads it back with our own reader and
// checks what it describes. The bytes are hashed and compared by tools/ci/oracle.py, which is why nothing in the
// scenario may depend on the machine, the clock or the culture.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MdfLib;
using Xunit;

namespace MdfLib.Tests
{
    public sealed class MdfLibraryOracleTests : IDisposable
    {
        /// <summary>The environment variable the oracle job sets to the directory the produced file is kept
        /// in. Without it the scenario runs into a temporary directory and is deleted again, so an ordinary
        /// test leg neither keeps nor needs the bytes.</summary>
        public const string KeepDirectoryVariable = "MDFLIB_ORACLE_DIRECTORY";

        /// <summary>The name the recorded file, the zip member and the expected hash all agree on.</summary>
        public const string Scenario = "w1-scenario";

        private readonly string directory_;
        private readonly bool keep_;

        public MdfLibraryOracleTests()
        {
            string kept = Environment.GetEnvironmentVariable(KeepDirectoryVariable);
            keep_ = !string.IsNullOrEmpty(kept);
            directory_ = keep_ ? kept : Path.Combine(Path.GetTempPath(), "mdflib-oracle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory_);
        }

        public void Dispose()
        {
            if (keep_) { return; }
            try { Directory.Delete(directory_, true); }
            catch (IOException) { }
        }

        /// <summary>Every scenario of the oracle job, one test case each; the job compares each with the wrapper's.</summary>
        public static IEnumerable<object[]> ScenarioNames()
        {
            foreach (string name in MdfOracle.Scenario.Names) { yield return new object[] { name }; }
        }

        [Theory]
        [MemberData(nameof(ScenarioNames))]
        public void TheScenarioWritesAFileTheReaderDescribesTheSameWay(string scenario)
        {
            string file = Path.Combine(directory_, scenario + ".mf4");
            // The facade never overwrites, and a rerun of the job must not be decided by a leftover file.
            if (File.Exists(file)) { File.Delete(file); }
            MdfOracle.Scenario.Write(scenario, file);
            if (scenario == Scenario) { Check(file); }
            else { CheckReadable(file); }
        }

        // The other scenarios are compared with the wrapper by their bytes and by the dump of tools/ci/oracle.py;
        // here the file only has to open and describe at least one data group.
        private static void CheckReadable(string file)
        {
            using (MdfReader reader = MdfReader.Open(File.OpenRead(file)))
            {
                IReadOnlyList<MdfDataGroup> groups = reader.ReadMetadata();
                Assert.NotEmpty(groups);
            }
        }

        private static void Check(string file)
        {
            using (MdfReader reader = MdfReader.Open(File.OpenRead(file)))
            {
                IReadOnlyList<MdfDataGroup> groups = reader.ReadMetadata();
                Assert.Single(groups);
                MdfChannelGroup read = groups[0].ChannelGroups[0];
                Assert.Equal("Engine", read.Name);
                // A comment of MDF 4 comes back as the block the writer stored, the schema document around the
                // text, so the scenario checks the text inside it rather than the shape of the document.
                Assert.Contains("bench run", read.Comment, StringComparison.Ordinal);
                Assert.Equal("TestBench", read.Source.Name);
                Assert.Equal("Rig/2", read.Source.Path);
                Assert.Equal(3UL, read.SampleCount);
                Assert.Equal(3, read.Channels.Count);
                Assert.True(read.Channels[0].IsMaster);
                Assert.Equal("Time", read.Channels[0].Name);
                Assert.Equal("s", read.Channels[0].Unit);
                Assert.Equal("Counter", read.Channels[1].Name);
                Assert.Contains("ticks per turn", read.Channels[1].Comment, StringComparison.Ordinal);
                Assert.Equal<double?>(10.0, read.Channels[1].Conversion.Offset);
                Assert.Equal<double?>(2.0, read.Channels[1].Conversion.Factor);
                for (int sample = 0; sample < 3; sample++)
                {
                    Assert.Equal((double)sample, Value(reader, read, 0, sample).FloatingPointValue);
                    MdfSampleValue raw = Value(reader, read, 1, sample);
                    Assert.Equal(7UL + (ulong)sample, raw.UnsignedValue);
                    MdfSampleValue physical = Value(reader, read, 1, sample, true);
                    Assert.Equal(10.0 + 2.0 * (7.0 + (double)sample), physical.FloatingPointValue);
                    Assert.Equal("abc", Value(reader, read, 2, sample).TextValue);
                }
            }
        }

        // One value of one channel, raw or physical, through the reader's own batch API.
        private static MdfSampleValue Value(MdfReader reader, MdfChannelGroup group, int channel, int sample, bool physical = false)
        {
            foreach (MdfSampleBatch batch in reader.ReadSamples(group, new int[] { channel }, (ulong)sample, 1UL,
                1024, physical ? MdfValueRepresentation.Physical : MdfValueRepresentation.Raw))
            {
                return batch.GetValue(0, 0);
            }
            throw new InvalidOperationException("The reader returned no batch for channel "
                + channel.ToString(CultureInfo.InvariantCulture) + " of the oracle scenario.");
        }
    }
}
