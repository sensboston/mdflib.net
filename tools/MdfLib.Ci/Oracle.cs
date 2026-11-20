// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Records and compares the bytes the MdfLibrary facade writes for its oracle scenarios, and compares them with the
// native wrapper (the port of tools/ci/oracle.py). The comparison is of structure and values, not of bytes: the
// native wrapper is a different writer. The release zip is pinned by its SHA-256 in tools/ci/oracle-expected.json;
// a scenario with no recorded hash is in record mode, and the run only reports the hash it saw.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MdfLib.Ci
{
    /// <summary>What the oracle refuses to accept: a missing file, a differing leg or a hash that moved.</summary>
    internal sealed class OracleError : Exception
    {
        public OracleError(string message) : base(message)
        {
        }
    }

    internal sealed class Entry
    {
        public long Size;
        public string Sha;
    }

    /// <summary>A value the wrapper reports wrongly, excused only for the named prefix and value kinds.</summary>
    internal sealed class ValueDefect
    {
        public string Prefix;
        public string Ours;
        public string Theirs;
        public string Check;
    }

    internal static class Oracle
    {
        private const string NativeUrl = "https://github.com/ihedvall/mdflib/releases/download/v2.3.0/mdflibrary.zip";
        private const long NativeBytes = 115813601; // the size the release of v2.3.0 reports for mdflibrary.zip

        // The reading oracle reads every file of these fixture folders, and the files both writers left for the scenarios.
        private static readonly string[] ReadDirectories = { "m2", "m3", "m4", "m5", "m5aa", "m5ab", "m5b" };

        // The wrapper's own defects, named one by one the wrapper surface, section 9). A file listed here is
        // not compared when the wrapper refuses it; the facade reads it by the format.
        private static readonly Dictionary<string, string> NativeDefects = new Dictionary<string, string>
        {
            { "tests/Fixtures/m2/m2-mdf410-header.mf4", "the wrapper refuses this valid ID+HD-only MDF 4.10 file" },
            { "tests/Fixtures/m2/m2-mdf330-be-data.mdf", "the wrapper refuses an MDF 3 file whose default data is big endian" },
            { "tests/Fixtures/m4/m4-packed-be330.mdf", "the wrapper refuses an MDF 3 file whose default data is big endian" },
            { "tests/Fixtures/m5b/m5b-suffix-be330.mdf", "the wrapper refuses an MDF 3 file whose default data is big endian" },
        };

        // Values the wrapper reports wrongly, where the value is neither a refusal nor a corruption of the file
        // the wrapper surface, section 9). A line is excused only when it starts with the prefix, the two
        // value kinds are the ones named, and the check (when given) holds for the two lines.
        private static readonly Dictionary<string, List<ValueDefect>> NativeValueDefects = new Dictionary<string, List<ValueDefect>>
        {
            {
                "tests/Fixtures/m5ab/m5ab-canopen-vlsd420.mf4", new List<ValueDefect>
                {
                    new ValueDefect { Prefix = "v 0.0.1 ", Ours = "b", Theirs = "f" },
                    new ValueDefect { Prefix = "v 0.0.2 ", Ours = "b", Theirs = "f" },
                }
            },
            {
                "tests/Fixtures/m3/m3-packed410.mf4", new List<ValueDefect>
                {
                    new ValueDefect { Prefix = "v 0.0.7 ", Ours = "x", Theirs = "f" },
                }
            },
            {
                "tests/Fixtures/m5ab/m5ab-fixed-arrays420.mf4", new List<ValueDefect>
                {
                    new ValueDefect { Prefix = "v 0.0.4 1 ", Ours = "b", Theirs = "b" },
                    new ValueDefect { Prefix = "v 0.0.4 2 ", Ours = "b", Theirs = "b" },
                    new ValueDefect { Prefix = "v 0.0.4 3 ", Ours = "b", Theirs = "b" },
                    new ValueDefect { Prefix = "v 0.0.4 4 ", Ours = "b", Theirs = "b" },
                    new ValueDefect { Prefix = "v 0.0.8 ", Ours = "s", Theirs = "s", Check = "utf8_decimal" },
                }
            },
        };

        private static string Expected()
        {
            return Shell.Under("tools", "ci", "oracle-expected.json");
        }

        private static string OutRoot()
        {
            return Shell.Under("artifacts", "oracle");
        }

        /// <summary>The expectation file as a document; a missing file is record mode for everything.</summary>
        private static JsonElement ReadDocument(string path, out JsonDocument owner)
        {
            if (!File.Exists(path))
            {
                owner = JsonDocument.Parse("{\"schemaVersion\":1,\"scenarios\":{}}");
                return owner.RootElement;
            }
            owner = JsonDocument.Parse(File.ReadAllText(path, new UTF8Encoding(false)));
            JsonElement root = owner.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new OracleError("Oracle expectation schema mismatch: " + path);
            }
            bool known = root.EnumerateObject().All(p => p.Name == "schemaVersion" || p.Name == "scenarios" || p.Name == "native");
            JsonElement version;
            if (!root.TryGetProperty("schemaVersion", out version) || version.ValueKind != JsonValueKind.Number
                || version.GetInt32() != 1 || !root.TryGetProperty("scenarios", out JsonElement _) || !known)
            {
                throw new OracleError("Oracle expectation schema mismatch: " + path);
            }
            return root;
        }

        /// <summary>The recorded hashes, keyed by scenario; a missing file is record mode for every scenario.</summary>
        private static Dictionary<string, string> RecordedScenarios(string path)
        {
            JsonDocument owner;
            JsonElement root = ReadDocument(path, out owner);
            using (owner)
            {
                JsonElement scenarios = root.GetProperty("scenarios");
                if (scenarios.ValueKind != JsonValueKind.Object)
                {
                    throw new OracleError("Oracle scenarios must be an object: " + path);
                }
                Dictionary<string, string> result = new Dictionary<string, string>();
                foreach (JsonProperty scenario in scenarios.EnumerateObject())
                {
                    JsonElement entry = scenario.Value;
                    bool exact = entry.ValueKind == JsonValueKind.Object && entry.EnumerateObject().Count() == 1
                                 && entry.TryGetProperty("sha256", out JsonElement sha) && sha.ValueKind == JsonValueKind.String
                                 && sha.GetString().Length == 64;
                    if (!exact)
                    {
                        throw new OracleError("Oracle scenario " + scenario.Name + " needs a 64-character sha256 and nothing else");
                    }
                    result[scenario.Name] = entry.GetProperty("sha256").GetString();
                }
                return result;
            }
        }

        /// <summary>The SHA-256 of the release zip the comparison is pinned to, or null while it is not recorded yet.</summary>
        private static string PinnedNative(string path)
        {
            JsonDocument owner;
            JsonElement root = ReadDocument(path, out owner);
            using (owner)
            {
                JsonElement native;
                if (!root.TryGetProperty("native", out native))
                {
                    return null;
                }
                JsonElement zip;
                bool exact = native.ValueKind == JsonValueKind.Object && native.EnumerateObject().Count() == 1
                             && native.TryGetProperty("zipSha256", out zip) && zip.ValueKind == JsonValueKind.String
                             && Regex.IsMatch(zip.GetString(), "^[0-9a-f]{64}$");
                if (!exact)
                {
                    throw new OracleError("Native expectation needs a 64-character zipSha256 and nothing else");
                }
                return native.GetProperty("zipSha256").GetString();
            }
        }

        /// <summary>The scenario files one leg left behind, keyed by scenario name.</summary>
        private static Dictionary<string, Entry> Collect(string directory)
        {
            Dictionary<string, Entry> found = new Dictionary<string, Entry>();
            if (!Directory.Exists(directory))
            {
                return found;
            }
            foreach (string path in Directory.GetFiles(directory, "*.mf4").OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal))
            {
                byte[] data = File.ReadAllBytes(path);
                found[Path.GetFileNameWithoutExtension(path)] = new Entry { Size = data.Length, Sha = Hex(data) };
            }
            return found;
        }

        private static string Hex(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>Every problem with the legs of one run: a missing or extra file, or bytes that differ.</summary>
        private static List<string> Compare(Dictionary<string, Dictionary<string, Entry>> recorded, Dictionary<string, string> seen, string[] frameworks)
        {
            List<string> problems = new List<string>();
            foreach (string framework in frameworks)
            {
                if (!recorded.ContainsKey(framework))
                {
                    problems.Add("No scenario collected for " + framework + "; the leg wrote nothing to keep");
                }
            }
            if (problems.Count > 0 || !frameworks.Any(f => recorded[f].Count > 0))
            {
                if (problems.Count == 0)
                {
                    problems.Add("Nothing was produced for any of: " + string.Join(", ", frameworks));
                }
                return problems;
            }
            foreach (string name in seen.Keys.OrderBy(n => n, StringComparer.Ordinal))
            {
                List<string> missing = frameworks.Where(f => !recorded[f].ContainsKey(name)).ToList();
                if (missing.Count > 0)
                {
                    problems.Add("Recorded scenario " + name + " is no longer produced by: " + string.Join(", ", missing));
                }
            }
            SortedSet<string> names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string framework in frameworks)
            {
                names.UnionWith(recorded[framework].Keys);
            }
            foreach (string name in names)
            {
                List<string> missing = frameworks.Where(f => !recorded[f].ContainsKey(name)).ToList();
                if (missing.Count > 0)
                {
                    problems.Add("Scenario " + name + " is missing in: " + string.Join(", ", missing));
                    continue;
                }
                Dictionary<string, string> hashes = frameworks.ToDictionary(f => f, f => recorded[f][name].Sha);
                if (hashes.Values.Distinct().Count() > 1)
                {
                    problems.Add("Scenario " + name + " differs between the compilations: "
                        + string.Join(", ", frameworks.Select(f => f + "=" + hashes[f].Substring(0, 12))));
                }
                string want;
                if (seen.TryGetValue(name, out want) && want != hashes[frameworks[0]])
                {
                    problems.Add("Scenario " + name + " is " + hashes[frameworks[0]] + " and not the recorded " + want);
                }
            }
            return problems;
        }

        /// <summary>The produced files as one zip, so the run keeps the bytes it hashed; its members are the scenario names.</summary>
        private static List<KeyValuePair<string, long>> Archive(string directory, string target)
        {
            string[] paths = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.mf4", SearchOption.AllDirectories) : new string[0];
            Array.Sort(paths, StringComparer.Ordinal);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            if (File.Exists(target))
            {
                File.Delete(target);
            }
            using (ZipArchive package = ZipFile.Open(target, ZipArchiveMode.Create))
            {
                foreach (string path in paths)
                {
                    string member = Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/');
                    package.CreateEntryFromFile(path, member, CompressionLevel.Optimal);
                }
            }
            if (paths.Length == 0)
            {
                return new List<KeyValuePair<string, long>>();
            }
            using (ZipArchive package = ZipFile.OpenRead(target))
            {
                return package.Entries.Select(e => new KeyValuePair<string, long>(e.FullName, e.Length)).ToList();
            }
        }

        private static void Report(Dictionary<string, Dictionary<string, Entry>> recorded, List<KeyValuePair<string, long>> listing,
            Dictionary<string, string> seen, string target)
        {
            List<string> lines = new List<string>();
            foreach (string framework in recorded.Keys.OrderBy(f => f, StringComparer.Ordinal))
            {
                foreach (KeyValuePair<string, Entry> item in recorded[framework].OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    string mode = seen.ContainsKey(item.Key) ? "recorded" : "record mode, nothing recorded yet";
                    lines.Add(framework + " " + item.Key + " " + item.Value.Size + " bytes sha256=" + item.Value.Sha + " [" + mode + "]");
                }
            }
            string members = listing.Count == 0 ? "empty"
                : string.Join(", ", listing.Select(n => n.Key + " (" + n.Value + ")"));
            lines.Add("archive " + Path.GetFileName(target) + ": " + members);
            Shell.Annotation("notice", "MdfLibrary oracle scenario bytes", string.Join("\n", lines));
        }

        /// <summary>One line of a dump with its comment reduced to the text: an MD comment is XML around the TX text.</summary>
        private static string Normalize(string line)
        {
            int at = line.IndexOf(" comment=", StringComparison.Ordinal);
            if (at < 0)
            {
                return line;
            }
            string head = line.Substring(0, at + " comment=".Length);
            string rest = line.Substring(at + " comment=".Length);
            Match match = Regex.Match(rest, "<TX>(.*?)</TX>", RegexOptions.Singleline);
            return head + (match.Success ? match.Groups[1].Value : rest);
        }

        /// <summary>The characters of a string as code points (a surrogate pair is one item).</summary>
        private static List<string> CodePoints(string text)
        {
            List<string> result = new List<string>();
            for (int index = 0; index < text.Length; index++)
            {
                if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                {
                    result.Add(text.Substring(index, 2));
                    index++;
                }
                else
                {
                    result.Add(text.Substring(index, 1));
                }
            }
            return result;
        }

        /// <summary>True when the wrapper's text is the facade's text with each non-ASCII character as its UTF-8 byte codes.</summary>
        private static bool Utf8Decimal(string mine, string other)
        {
            string[] ours = mine.Split(new[] { ' ' }, 5);
            string[] theirs = other.Split(new[] { ' ' }, 5);
            if (ours.Length < 5 || theirs.Length < 5)
            {
                return false;
            }
            StringBuilder expected = new StringBuilder();
            foreach (string character in CodePoints(ours[4]))
            {
                if (character.Length == 1 && character[0] < 128)
                {
                    expected.Append(character);
                    continue;
                }
                foreach (byte value in Encoding.UTF8.GetBytes(character))
                {
                    expected.Append(value.ToString(CultureInfo.InvariantCulture));
                }
            }
            return theirs[4] == expected.ToString();
        }

        private static bool ValueDefectExcuses(string label, string mine, string other)
        {
            string[] ours = mine.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            string[] theirs = other.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (ours.Length < 4 || theirs.Length < 4)
            {
                return false;
            }
            List<ValueDefect> defects;
            if (!NativeValueDefects.TryGetValue(label, out defects))
            {
                return false;
            }
            foreach (ValueDefect defect in defects)
            {
                if (mine.StartsWith(defect.Prefix, StringComparison.Ordinal) && other.StartsWith(defect.Prefix, StringComparison.Ordinal)
                    && ours[3] == defect.Ours && theirs[3] == defect.Theirs
                    && (defect.Check == null || Utf8Decimal(mine, other)))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>True when the wrapper cut a long text to its first characters and '...'.</summary>
        private static bool WrapperTruncated(string native, string facade)
        {
            return native.EndsWith(" ...", StringComparison.Ordinal) && native.Length > 4
                   && facade.StartsWith(native.Substring(0, native.Length - 4), StringComparison.Ordinal)
                   && facade.Length > native.Length - 4;
        }

        private static string FoldNonAscii(string line)
        {
            return Regex.Replace(line, "[^\\x00-\\x7f]", "?");
        }

        /// <summary>True when the wrapper cut one long field inside the line: its first characters, '... ', then the next field.</summary>
        private static bool WrapperTruncatedInside(string native, string facade)
        {
            int at = native.IndexOf(" ... ", StringComparison.Ordinal);
            if (at < 0)
            {
                return false;
            }
            string head = native.Substring(0, at);
            string tail = native.Substring(at + 5);
            return facade.StartsWith(head, StringComparison.Ordinal) && facade.EndsWith(" " + tail, StringComparison.Ordinal)
                   && facade.Length > head.Length + tail.Length;
        }

        private static bool SameLine(string mine, string other)
        {
            if (mine == other || WrapperTruncated(other, mine) || WrapperTruncatedInside(other, mine))
            {
                return true;
            }
            return other.IndexOf('\uFFFD') >= 0 && FoldNonAscii(mine) == FoldNonAscii(other);
        }

        /// <summary>Every difference between the facade's and the native wrapper's description of one scenario file.</summary>
        private static List<string> CompareDumps(string scenario, List<string> facade, List<string> native, Func<string, string, bool> excused)
        {
            List<string> ours = facade.Select(Normalize).ToList();
            List<string> theirs = native.Select(Normalize).ToList();
            List<string> problems = new List<string>();
            const int limit = 20;
            if (ours.Count != theirs.Count)
            {
                problems.Add(scenario + ": the facade file has " + ours.Count + " lines, the native file " + theirs.Count);
            }
            for (int index = 0; index < Math.Min(ours.Count, theirs.Count); index++)
            {
                string mine = ours[index];
                string other = theirs[index];
                if (!SameLine(mine, other) && !(excused != null && excused(mine, other)))
                {
                    problems.Add(scenario + " line " + (index + 1) + ": facade [" + mine + "] native [" + other + "]");
                    if (problems.Count >= limit)
                    {
                        break;
                    }
                }
            }
            return problems;
        }

        /// <summary>The release asset, downloaded once; the SHA-256 of what was read. A file of another size is refused.</summary>
        private static string FetchNative(string url, string target, long size)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            if (!File.Exists(target) || new FileInfo(target).Length != size)
            {
                Console.Out.WriteLine("+ download " + url);
                using (HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(300) })
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("mdflib-oracle");
                    using (HttpResponseMessage response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                    {
                        response.EnsureSuccessStatusCode();
                        using (Stream source = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                        using (FileStream file = File.Create(target))
                        {
                            source.CopyTo(file);
                        }
                    }
                }
            }
            long actual = new FileInfo(target).Length;
            if (actual != size)
            {
                throw new OracleError("The downloaded " + Path.GetFileName(target) + " has " + actual + " bytes, the release asset has " + size);
            }
            using (SHA256 sha = SHA256.Create())
            using (FileStream file = File.OpenRead(target))
            {
                return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>The zip unpacked into an empty folder; the listing of its entries as (name, size) pairs.</summary>
        private static List<KeyValuePair<string, long>> UnpackNative(string archivePath, string folder)
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
            Directory.CreateDirectory(folder);
            using (ZipArchive package = ZipFile.OpenRead(archivePath))
            {
                List<KeyValuePair<string, long>> listing = package.Entries.Select(e => new KeyValuePair<string, long>(e.FullName, e.Length)).ToList();
                package.ExtractToDirectory(folder);
                return listing;
            }
        }

        /// <summary>The framework an assembly was built for, read from its TargetFrameworkAttribute text (stored as UTF-16LE).</summary>
        private static string TargetFramework(byte[] data)
        {
            foreach (string name in new[] { ".NETCoreApp,Version=v", ".NETFramework,Version=v" })
            {
                byte[] marker = Encoding.Unicode.GetBytes(name);
                int at = IndexOf(data, marker, 0);
                if (at < 0)
                {
                    continue;
                }
                StringBuilder digits = new StringBuilder();
                int position = at + marker.Length;
                while (position + 1 < data.Length && data[position + 1] == 0 && "0123456789.".IndexOf((char)data[position]) >= 0)
                {
                    digits.Append((char)data[position]);
                    position += 2;
                }
                return name + digits;
            }
            return "unknown (no TargetFrameworkAttribute text found)";
        }

        private static int IndexOf(byte[] haystack, byte[] needle, int start)
        {
            for (int index = start; index + needle.Length <= haystack.Length; index++)
            {
                bool match = true;
                for (int offset = 0; offset < needle.Length && match; offset++)
                {
                    match = haystack[index + offset] == needle[offset];
                }
                if (match)
                {
                    return index;
                }
            }
            return -1;
        }

        /// <summary>The names in an assembly's string heap that contain one of the words, in order of first appearance (evidence for the log only).</summary>
        private static List<string> ExportedNames(byte[] data, string[] words)
        {
            List<string> seen = new List<string>();
            foreach (Match match in Regex.Matches(Encoding.Latin1.GetString(data), "[A-Za-z_][A-Za-z0-9_]{2,80}"))
            {
                string name = match.Value;
                if (!seen.Contains(name) && words.Any(word => name.Contains(word)))
                {
                    seen.Add(name);
                }
            }
            return seen;
        }

        /// <summary>The standard output of one of our tools; a non-zero exit is an error with its own message.</summary>
        private static string Capture(params string[] args)
        {
            ProcessResult result = Shell.Execute(args[0], args.Skip(1).ToArray(), null);
            if (result.Code != 0)
            {
                throw new OracleError(Path.GetFileName(args[0]) + " " + (args.Length > 1 ? args[1] : "") + " exited with "
                    + result.Code + ": " + Shell.Tail(result.Error, 1500));
            }
            return result.Standard;
        }

        /// <summary>The problems as error annotations of at most 'size' characters each; GitHub caps a step at 50 annotations.</summary>
        private static void AnnotateProblems(string title, List<string> problems)
        {
            const int size = 7000;
            const int most = 40;
            List<string> chunks = new List<string>();
            string current = "";
            foreach (string problem in problems)
            {
                if (current.Length > 0 && current.Length + problem.Length + 2 > size)
                {
                    chunks.Add(current);
                    current = "";
                }
                current = current.Length > 0 ? current + "; " + problem : problem;
            }
            if (current.Length > 0)
            {
                chunks.Add(current);
            }
            for (int number = 0; number < Math.Min(chunks.Count, most); number++)
            {
                Shell.Annotation("error", title + " " + (number + 1) + "/" + chunks.Count, chunks[number]);
            }
            if (chunks.Count > most)
            {
                Shell.Annotation("error", title, problems.Count + " problems; the first " + most + " annotations are shown");
            }
        }

        /// <summary>The files the reading oracle reads, in a fixed order: the shared fixtures, then the files the writers left.</summary>
        private static List<string> ReadInputs(string outFolder)
        {
            List<string> files = new List<string>();
            foreach (string folder in ReadDirectories)
            {
                string bas = Shell.Under("tests", "Fixtures", folder);
                if (Directory.Exists(bas))
                {
                    files.AddRange(SortedFiles(bas, "*.mf4"));
                    files.AddRange(SortedFiles(bas, "*.mdf"));
                }
            }
            files.AddRange(SortedFiles(Shell.Under("artifacts", "native", "out"), "*.mf4"));
            files.AddRange(SortedFiles(Path.Combine(outFolder, "net10.0"), "*.mf4"));
            if (files.Count == 0)
            {
                throw new OracleError("The reading oracle found no files to read");
            }
            return files;
        }

        private static List<string> SortedFiles(string folder, string pattern)
        {
            if (!Directory.Exists(folder))
            {
                return new List<string>();
            }
            return Directory.GetFiles(folder, pattern).OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal).ToList();
        }

        /// <summary>The dump lines of one file, or one refusal line that names the exception type.</summary>
        private static List<string> DumpOrRefusal(string exe, string path)
        {
            Dictionary<string, string> environment = new Dictionary<string, string> { { "MDFLIB_ORACLE_HEADER", "1" } };
            ProcessResult result = Shell.Execute(exe, new[] { "dump", path }, environment);
            if (result.Code == 0)
            {
                return Shell.SplitLines(result.Standard);
            }
            Match match = Regex.Match(result.Error, "Unhandled exception\\. ([\\w.]+)(?:: ([^\\r\\n]*))?");
            if (match.Success)
            {
                return new List<string> { "!refused " + match.Groups[1].Value + (match.Groups[2].Success ? ": " + match.Groups[2].Value : "") };
            }
            return new List<string> { "!refused exit " + result.Code };
        }

        private static string Join(List<string> lines)
        {
            return string.Join("\n", lines) + "\n";
        }

        /// <summary>Reads every file with the native wrapper and with the facade, and compares the two dumps file by file.</summary>
        private static List<string> ReadLeg(string nativeExe, string facadeExe, string outFolder)
        {
            string dumps = Shell.Under("artifacts", "oracle", "read-dumps");
            Directory.CreateDirectory(dumps);
            List<string> problems = new List<string>();
            foreach (string path in ReadInputs(outFolder))
            {
                string full = Path.GetFullPath(path);
                string label = full.StartsWith(Shell.Root, StringComparison.Ordinal) ? Shell.Relative(full) : Path.GetFileName(full);
                List<string> nativeLines;
                List<string> facadeLines;
                try
                {
                    nativeLines = DumpOrRefusal(nativeExe, path);
                    facadeLines = DumpOrRefusal(facadeExe, path);
                }
                catch (Exception error) when (error is Win32Exception || error is IOException)
                {
                    // A tool that cannot start is a finding of the comparison, named with its file, not a silent stop.
                    problems.Add("read " + label + ": the dump did not run: " + error.Message);
                    continue;
                }
                string stem = label.Replace("/", "__");
                Shell.WriteText(Path.Combine(dumps, stem + ".native.txt"), Join(nativeLines));
                Shell.WriteText(Path.Combine(dumps, stem + ".facade.txt"), Join(facadeLines));
                string defect;
                if (NativeDefects.TryGetValue(label, out defect) && nativeLines.Count > 0 && nativeLines[0].StartsWith("!refused", StringComparison.Ordinal))
                {
                    Console.Out.WriteLine("+ " + label + " is not compared: " + defect);
                    continue;
                }
                string captured = label;
                problems.AddRange(CompareDumps("read " + label, facadeLines, nativeLines,
                    (mine, other) => ValueDefectExcuses(captured, mine, other)));
            }
            return problems;
        }

        /// <summary>Downloads the release, builds both tools, writes and reads the scenarios with both sides.
        /// Returns the problems, or null while the release hash is not recorded (the comparison is then skipped).</summary>
        private static List<string> NativeLeg(string outFolder, string pinned)
        {
            string nativeDir = Shell.Under("artifacts", "native");
            string archivePath = Path.Combine(nativeDir, "mdflibrary.zip");
            string digest = FetchNative(NativeUrl, archivePath, NativeBytes);
            string package = Path.Combine(nativeDir, "package");
            List<KeyValuePair<string, long>> listing = UnpackNative(archivePath, package);
            // The x64 release build is the one the scenarios are compared with. The choice is by folder name and must
            // match exactly one file, so a 32-bit debug build can never be picked by a sort order.
            string[] dlls = Directory.GetFiles(package, "mdflibrary.dll", SearchOption.AllDirectories);
            Array.Sort(dlls, StringComparer.Ordinal);
            if (dlls.Length == 0)
            {
                throw new OracleError("The release has no mdflibrary.dll; its entries are " + string.Join(", ", listing.Select(n => n.Key)));
            }
            List<string> chosen = dlls.Where(p => Path.GetFileName(Path.GetDirectoryName(p)) == "release_64bit").ToList();
            if (chosen.Count != 1)
            {
                throw new OracleError("The release has no single release_64bit/mdflibrary.dll; found: "
                    + string.Join(", ", dlls.Select(p => Path.GetRelativePath(package, p))));
            }
            string dll = chosen[0];
            List<string> assemblies = dlls.Select(p => Path.GetRelativePath(package, p) + " " + TargetFramework(File.ReadAllBytes(p))).ToList();
            List<string> names = ExportedNames(File.ReadAllBytes(dll), new[] { "CanMessage", "SaveSample", "SaveCanMessage", "ETag", "MdfWriter" });
            Shell.Annotation("notice", "MdfLibrary native wrapper", "mdflibrary.zip " + NativeBytes + " bytes sha256=" + digest
                + "\nassemblies: " + string.Join("; ", assemblies)
                + "\nchosen: " + Path.GetRelativePath(package, dll)
                + "\nnames: " + string.Join(", ", names)
                + "\nentries (" + listing.Count + "): " + string.Join(", ", listing.Select(n => n.Key + " (" + n.Value + ")")));

            string nativeBin = Path.Combine(nativeDir, "bin");
            string engineBin = Shell.Under("artifacts", "oracle-tools", "engine"); // not uploaded: the tool is rebuilt, its output is not kept
            Shell.Run("dotnet", "build", Shell.Under(Contract.OracleNative), "-c", "Release", "-nologo", "-p:MdfLibDll=" + dll,
                "-p:MdfLibDir=" + Path.GetDirectoryName(dll), "-o", nativeBin);
            Shell.Run("dotnet", "build", Shell.Under(Contract.OracleEngine), "-c", "Release", "-nologo", "-o", engineBin);
            string nativeExe = Path.Combine(nativeBin, "MdfLib.NativeOracle.exe");
            string engineExe = Path.Combine(engineBin, "MdfLib.Oracle.exe");
            string facadeBin = Shell.Under("artifacts", "oracle-tools", "facade");
            Shell.Run("dotnet", "build", Shell.Under(Contract.OracleFacade), "-c", "Release", "-nologo", "-o", facadeBin);
            string facadeExe = Path.Combine(facadeBin, "MdfLib.FacadeDump.exe");
            if (!File.Exists(facadeExe))
            {
                throw new OracleError("The facade dump was not built");
            }

            string dumps = Shell.Under("artifacts", "oracle", "dumps");
            Directory.CreateDirectory(dumps);
            List<string> problems = new List<string>();
            foreach (string raw in Capture(nativeExe, "list").Split('\n'))
            {
                string name = raw.Trim();
                if (name.Length == 0)
                {
                    continue;
                }
                string written = Path.Combine(nativeDir, "out", name + ".mf4");
                Directory.CreateDirectory(Path.GetDirectoryName(written));
                if (File.Exists(written))
                {
                    File.Delete(written);
                }
                Capture(nativeExe, "write", name, written);
                string facadeFile = Path.Combine(outFolder, "net10.0", name + ".mf4");
                if (!File.Exists(facadeFile))
                {
                    throw new OracleError("The net10.0 leg wrote no " + name + ".mf4 for the comparison");
                }
                List<string> nativeLines = Shell.SplitLines(Capture(nativeExe, "dump", written));
                List<string> facadeLines = Shell.SplitLines(Capture(engineExe, "dump", facadeFile));
                Shell.WriteText(Path.Combine(dumps, name + ".native.txt"), Join(nativeLines));
                Shell.WriteText(Path.Combine(dumps, name + ".facade.txt"), Join(facadeLines));
                List<string> scenario = CompareDumps(name, facadeLines, nativeLines, null);
                problems.AddRange(scenario);
                if (scenario.Count > 0)
                {
                    // The comparison names only the first mismatches; a layout is read whole from the two dumps.
                    Shell.Annotation("notice", "MdfLibrary dump " + name + " (native)", string.Join("\n", nativeLines));
                    Shell.Annotation("notice", "MdfLibrary dump " + name + " (facade)", string.Join("\n", facadeLines));
                }
            }
            problems.AddRange(ReadLeg(nativeExe, facadeExe, outFolder));
            if (pinned == null)
            {
                Shell.Annotation("notice", "MdfLibrary native comparison",
                    "skipped: no SHA-256 of mdflibrary.zip is recorded in tools/ci/oracle-expected.json yet. "
                    + "Both sides were written and read back; record the sha256 above to turn the comparison on.");
                return null;
            }
            if (pinned != digest)
            {
                throw new OracleError("mdflibrary.zip is " + digest + " and not the recorded " + pinned);
            }
            return problems;
        }

        /// <summary>The oracle command: runs the scenario legs, compares the bytes, then the native wrapper. Returns the exit code.</summary>
        public static int Run(string[] argv)
        {
            string frameworkList = "net8.0,net10.0";
            string outFolder = OutRoot();
            string expected = Expected();
            string zip = Shell.Under("artifacts", "mdflibrary-oracle.zip");
            bool collectOnly = false;
            bool noNative = false;
            for (int index = 0; index < argv.Length; index++)
            {
                string argument = argv[index];
                string value = null;
                int equals = argument.IndexOf('=');
                if (argument.StartsWith("--", StringComparison.Ordinal) && equals > 0)
                {
                    value = argument.Substring(equals + 1);
                    argument = argument.Substring(0, equals);
                }
                switch (argument)
                {
                    case "--frameworks": frameworkList = value ?? argv[++index]; break;
                    case "--out": outFolder = Path.GetFullPath(value ?? argv[++index]); break;
                    case "--expected": expected = Path.GetFullPath(value ?? argv[++index]); break;
                    case "--zip": zip = Path.GetFullPath(value ?? argv[++index]); break;
                    case "--collect-only": collectOnly = true; break;
                    case "--no-native": noNative = true; break;
                    default: throw new OracleError("Unknown oracle argument: " + argv[index]);
                }
            }
            string[] frameworks = frameworkList.Split(',').Where(n => n.Length > 0).ToArray();
            if (frameworks.Length == 0 || frameworks.Any(n => Array.IndexOf(Contract.Frameworks, n) < 0))
            {
                Shell.Annotation("error", "MdfLibrary oracle scenario", "Unknown target framework in: " + frameworkList);
                return 2;
            }
            try
            {
                if (!collectOnly)
                {
                    foreach (string framework in frameworks)
                    {
                        string keep = Path.Combine(outFolder, framework);
                        Directory.CreateDirectory(keep);
                        foreach (string stale in Directory.GetFiles(keep, "*.mf4"))
                        {
                            File.Delete(stale);
                        }
                        Console.Out.WriteLine("+ dotnet test tests/MdfLib.Tests --framework " + framework);
                        Dictionary<string, string> environment = new Dictionary<string, string> { { "MDFLIB_ORACLE_DIRECTORY", keep } };
                        int code = Shell.Inherit("dotnet", new[]
                        {
                            "test", Shell.Under("tests", "MdfLib.Tests", "MdfLib.Tests.csproj"), "--configuration", "Release",
                            "--framework", framework, "--nologo", "--logger", "console;verbosity=quiet",
                            "--filter", "FullyQualifiedName~MdfLibraryOracleTests",
                        }, environment);
                        if (code != 0)
                        {
                            throw new OracleError("The scenario leg for " + framework + " did not pass");
                        }
                    }
                }
                Dictionary<string, Dictionary<string, Entry>> recorded = new Dictionary<string, Dictionary<string, Entry>>();
                foreach (string framework in frameworks)
                {
                    recorded[framework] = Collect(Path.Combine(outFolder, framework));
                }
                List<KeyValuePair<string, long>> listing = Archive(outFolder, zip);
                Dictionary<string, string> seen = RecordedScenarios(expected);
                List<string> problems = Compare(recorded, seen, frameworks);
                Report(recorded, listing, seen, zip);
                if (problems.Count > 0)
                {
                    foreach (string problem in problems)
                    {
                        Console.Out.WriteLine("  " + problem);
                    }
                    Shell.Annotation("error", "MdfLibrary oracle scenario", string.Join("; ", problems));
                    return 1;
                }
                Shell.Annotation("notice", "MdfLibrary oracle scenario",
                    seen.Count > 0 ? "every compilation produced the same bytes" : "record mode: nothing to compare yet");
                if (!noNative && !collectOnly)
                {
                    List<string> nativeProblems = NativeLeg(outFolder, PinnedNative(expected));
                    if (nativeProblems != null)
                    {
                        if (nativeProblems.Count > 0)
                        {
                            foreach (string problem in nativeProblems)
                            {
                                Console.Out.WriteLine("  " + problem);
                            }
                            AnnotateProblems("MdfLibrary native comparison", nativeProblems);
                            return 1;
                        }
                        Shell.Annotation("notice", "MdfLibrary native comparison",
                            "the release wrapper and the facade describe the same structure and values");
                    }
                }
            }
            catch (OracleError error)
            {
                Shell.Annotation("error", "MdfLibrary oracle scenario", error.Message);
                return 2;
            }
            catch (CiError)
            {
                return 2;
            }
            catch (Exception error)
            {
                // An unexpected failure is reported with its place, not as a bare exit code.
                Shell.Annotation("error", "MdfLibrary oracle (unexpected)", error.GetType().Name + ": " + error.Message + "\n"
                    + Shell.Tail(error.StackTrace ?? "", 1500));
                return 3;
            }
            return 0;
        }
    }
}
