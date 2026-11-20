// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// managed sample: the upstream mdf2csv command line converter (programargument, createfilelist) on the public
// MdfLib API. Compiled and exercised by the test suite so the sample cannot rot; copy it into a console project as needed.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MdfLib.Samples
{
    /// <summary>mdf2csv: converts MDF files and folders into one CSV file per channel group.</summary>
    public static class Mdf2CsvTool
    {
        private static readonly string[] BusFrameChannels = { "CAN_DataFrame", "CAN_RemoteFrame", "CAN_ErrorFrame", "CAN_OverloadFrame" };

        private sealed class Arguments
        {
            internal bool Help, Version, Quiet, Force, DeleteConverted, NoAppendRoot;
            internal string OutputDirectory;
            internal readonly List<string> Inputs = new List<string>();
        }

        /// <summary>Runs the tool like a console entry point and returns the process exit code (0 success, 1 failure).</summary>
        public static int Run(string[] args, TextWriter output, TextWriter error)
        {
            Arguments arguments;
            try { arguments = Parse(args); }
            catch (ArgumentException failure)
            {
                error.WriteLine("Argument error: " + failure.Message);
                return 1;
            }
            if (arguments.Help) { ShowHelp(output); return 0; }
            if (arguments.Version) { output.WriteLine("mdf2csv (MdfLib managed sample) 1.0"); return 0; }
            TextWriter info = arguments.Quiet ? TextWriter.Null : output;
            bool ok;
            List<string> files = MakeFileList(arguments.Inputs, error, out ok);
            if (files.Count == 0)
            {
                error.WriteLine("There is no MDF files to convert.");
                return 1;
            }
            foreach (string file in files)
            {
                string directory = arguments.OutputDirectory;
                if (string.IsNullOrEmpty(directory)) { directory = Path.GetDirectoryName(Path.GetFullPath(file)); }
                else if (!arguments.NoAppendRoot && arguments.Inputs.Any(Directory.Exists))
                {
                    directory = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "_out";
                }
                Directory.CreateDirectory(directory);
                bool converted = ConvertFile(file, directory, arguments.Force, info, error);
                ok &= converted;
                if (converted && arguments.DeleteConverted) { File.Delete(file); }
            }
            return ok ? 0 : 1;
        }

        private static Arguments Parse(string[] args)
        {
            if (args == null) { throw new ArgumentException("No arguments."); }
            Arguments result = new Arguments();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "-h": case "--help": result.Help = true; break;
                    case "-V": case "--version": result.Version = true; break;
                    case "-q": case "--quiet": case "--silent": case "--non-interactive": result.Quiet = true; break;
                    case "--force": result.Force = true; break;
                    case "-d": case "--delete-converted": result.DeleteConverted = true; break;
                    case "-X": case "--no-append-root": result.NoAppendRoot = true; break;
                    case "-O": case "--output-directory":
                        if (++i >= args.Length) { throw new ArgumentException(arg + " requires a path."); }
                        result.OutputDirectory = args[i];
                        break;
                    case "-i": case "--input-files":
                        while (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal)) { result.Inputs.Add(args[++i]); }
                        break;
                    default:
                        if (arg.StartsWith("-", StringComparison.Ordinal)) { throw new ArgumentException("Unknown option " + arg + "."); }
                        result.Inputs.Add(arg);
                        break;
                }
            }
            return result;
        }

        private static void ShowHelp(TextWriter output)
        {
            output.WriteLine("Usage: mdf2csv [options] <input files/folders>");
            output.WriteLine("  -h, --help              Display all available command line options and then exit.");
            output.WriteLine("  -V, --version           Displays the tool version and then exit.");
            output.WriteLine("  -q, --quiet             Does not print any progress output.");
            output.WriteLine("      --force             Overwrite existing CSV files.");
            output.WriteLine("  -d, --delete-converted  Delete original files after they are converted.");
            output.WriteLine("  -X, --no-append-root    Do not append \"_out\" when converting folders.");
            output.WriteLine("  -O, --output-directory  Output directory path.");
            output.WriteLine("  -i, --input-files       List of input file(s)/folder(s).");
        }

        /// <summary>Lists explicit MDF files (non-MDF files are errors) and every MDF file below folders (others are skipped).</summary>
        public static List<string> MakeFileList(IEnumerable<string> inputs, TextWriter error, out bool ok)
        {
            ok = true;
            List<string> files = new List<string>();
            foreach (string input in inputs)
            {
                if (Directory.Exists(input))
                {
                    files.AddRange(Directory.GetFiles(input, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).Where(IsMdf));
                }
                else if (!File.Exists(input))
                {
                    error.WriteLine("The input path/file doesn't exist. File: " + input);
                    ok = false;
                }
                else if (!IsMdf(input))
                {
                    error.WriteLine("The file isn't an MDF file. File: " + input);
                    ok = false;
                }
                else { files.Add(input); }
            }
            return files;
        }

        private static bool IsMdf(string file)
        {
            using (FileStream stream = File.OpenRead(file)) { return MdfReader.IsMdfFile(stream); }
        }

        /// <summary>Converts one file; returns false when the file could not be read.</summary>
        public static bool ConvertFile(string file, string outputDirectory, bool force, TextWriter info, TextWriter error)
        {
            string name = Path.GetFileName(file), stem = Path.GetFileNameWithoutExtension(file);
            try
            {
                using (MdfReader reader = MdfReader.Open(File.OpenRead(file)))
                {
                    IReadOnlyList<MdfDataGroup> groups = reader.ReadMetadata();
                    if (groups.Count == 0) { info.WriteLine(name + ". Skipping. No data groups."); return true; }
                    foreach (MdfDataGroup dataGroup in groups)
                    {
                        List<MdfChannelGroup> list = dataGroup.ChannelGroups.Where(g => (g.Flags & 1) == 0 && g.SampleCount > 0).ToList();
                        for (int c = 0; c < list.Count; c++)
                        {
                            MdfChannelGroup group = list[c];
                            StringBuilder csvName = new StringBuilder(stem);
                            if (groups.Count > 1) { csvName.Append("_DG_").Append((dataGroup.Index + 1).ToString(CultureInfo.InvariantCulture)); }
                            if (list.Count > 1 && string.IsNullOrEmpty(group.Name)) { csvName.Append("_CG_").Append((c + 1).ToString(CultureInfo.InvariantCulture)); }
                            else if (list.Count > 1) { csvName.Append('_').Append(group.Name); }
                            string csvFile = Path.Combine(outputDirectory, StripName(csvName.ToString()) + ".csv");
                            if (File.Exists(csvFile) && !force) { info.WriteLine(name + ". Skipping. CSV file exist."); continue; }
                            bool bus = (group.Flags & 2) != 0;
                            List<int> channels = new List<int>();
                            for (int i = 0; i < group.Channels.Count; i++)
                            {
                                MdfChannel channel = group.Channels[i];
                                if (!channel.CanReadPhysical || (bus && BusFrameChannels.Contains(channel.Name))) { continue; }
                                channels.Add(i);
                            }
                            if (channels.Count == 0 || !group.CanReadSamples) { continue; }
                            info.WriteLine(name + " -> " + Path.GetFileName(csvFile));
                            using (StreamWriter writer = new StreamWriter(csvFile, false, new UTF8Encoding(false)))
                            {
                                MdfCsvExporter.Export(reader, group, writer, channels);
                            }
                        }
                    }
                }
                return true;
            }
            catch (Exception failure) when (failure is MdfFormatException || failure is MdfResourceLimitException || failure is IOException || failure is NotSupportedException)
            {
                error.WriteLine(name + ". Skipping. Invalid MDF file. " + failure.Message);
                return false;
            }
        }

        /// <summary>Keeps letters, digits, '_' and '-' (upstream StripName).</summary>
        public static string StripName(string text)
        {
            StringBuilder output = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (c == '_' || c == '-' || (!char.IsWhiteSpace(c) && !char.IsPunctuation(c) && !char.IsSymbol(c))) { output.Append(c); }
            }
            return output.ToString();
        }
    }
}
