// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Static project, document, fixture and workflow checks; no.NET invocation (the port of tools/ci/verify_repository.py).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Xml.Linq;

namespace MdfLib.Ci
{
    internal static class Repository
    {
        private static readonly Dictionary<string, string> ExpectedPackages = new Dictionary<string, string>
        {
            { "Microsoft.NETFramework.ReferenceAssemblies", "1.0.3" },
            { "Microsoft.NET.Test.Sdk", "17.14.1" },
            { "xunit", "2.9.3" },
            { "xunit.runner.visualstudio", "3.1.5" },
        };

        /// <summary>Every project in the repository and its role; a new.csproj must be listed here first.</summary>
        private static Dictionary<string, string> ExpectedProjects()
        {
            Dictionary<string, string> projects = new Dictionary<string, string>();
            projects[Contract.Library] = "Library";
            foreach (string test in Contract.Tests)
            {
                projects[test] = "Test";
            }
            projects[Contract.Tool] = "Tool";
            foreach (string oracle in Contract.OracleTools)
            {
                projects[oracle] = "Tool";
            }
            projects[Contract.CiTool] = "Tool";
            return projects;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new CiError(message);
            }
        }

        /// <summary>The first element matching a child path of every PropertyGroup, as findtext does in Python.</summary>
        private static string PropertyText(XDocument document, string name)
        {
            XElement element = document.Root.Elements("PropertyGroup").SelectMany(group => group.Elements(name)).FirstOrDefault();
            return element == null ? null : element.Value;
        }

        private static void CheckConfig(string root)
        {
            using (JsonDocument global = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "global.json"))))
            {
                JsonElement sdk = global.RootElement.GetProperty("sdk");
                Require(sdk.GetProperty("version").GetString() == "10.0.404"
                        && sdk.GetProperty("rollForward").GetString() == "disable"
                        && sdk.GetProperty("allowPrerelease").ValueKind == JsonValueKind.False,
                    "SDK contract mismatch");
            }
            XDocument props = XDocument.Load(Path.Combine(root, "Directory.Build.props"));
            foreach (KeyValuePair<string, string> pair in new[]
            {
                new KeyValuePair<string, string>("LangVersion", "7.3"),
                new KeyValuePair<string, string>("Nullable", "disable"),
                new KeyValuePair<string, string>("ImplicitUsings", "disable"),
                new KeyValuePair<string, string>("AllowUnsafeBlocks", "false"),
                new KeyValuePair<string, string>("Deterministic", "true"),
                new KeyValuePair<string, string>("TreatWarningsAsErrors", "true"),
                new KeyValuePair<string, string>("RestorePackagesWithLockFile", "true"),
            })
            {
                Require(PropertyText(props, pair.Key) == pair.Value, "MSBuild contract mismatch: " + pair.Key);
            }
            Dictionary<string, string> expected = ExpectedProjects();
            HashSet<string> actual = new HashSet<string>();
            foreach (string directory in new[] { "src", "tests", "tools" })
            {
                foreach (string file in Directory.EnumerateFiles(Path.Combine(root, directory), "*.csproj", SearchOption.AllDirectories))
                {
                    if (!file.Split(Path.DirectorySeparatorChar).Contains("obj"))
                    {
                        actual.Add(Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'));
                    }
                }
            }
            Require(actual.SetEquals(expected.Keys), "Unknown or missing .csproj: update project inventory before adding it");
            foreach (KeyValuePair<string, string> item in expected)
            {
                CheckProject(root, item.Key, item.Value);
            }
            foreach (XElement dependency in props.Descendants("PackageReference"))
            {
                Require(ExpectedPackages.ContainsKey(dependency.Attribute("Include")?.Value ?? "")
                        && ExpectedPackages[dependency.Attribute("Include").Value] == (string)dependency.Attribute("Version")
                        && (string)dependency.Attribute("PrivateAssets") == "all",
                    "Unreviewed central dependency");
            }
        }

        private static void CheckProject(string root, string path, string role)
        {
            XDocument project = XDocument.Load(Path.Combine(root, path));
            Require(PropertyText(project, "RepositoryProjectRole") == role, "Project role mismatch: " + path);
            string key = role == "Tool" ? "TargetFramework" : "TargetFrameworks";
            string value = role == "Tool" ? "net10.0" : string.Join(";", Contract.Frameworks);
            Require(PropertyText(project, key) == value, "TFM matrix mismatch: " + path);
            Require(project.Root.Attribute("Sdk")?.Value == "Microsoft.NET.Sdk", "Unreviewed SDK: " + path);
            foreach (XElement dependency in project.Descendants("PackageReference"))
            {
                string include = dependency.Attribute("Include")?.Value ?? "";
                Require(ExpectedPackages.ContainsKey(include) && ExpectedPackages[include] == (string)dependency.Attribute("Version"),
                    "Unreviewed dependency: " + include);
                Require(((string)dependency.Attribute("PrivateAssets") ?? "").ToLowerInvariant() == "all",
                    "Dependencies must be private");
            }
            string lockPath = Path.Combine(root, Path.GetDirectoryName(path), "packages.lock.json");
            Require(File.Exists(lockPath), "Missing dependency lock: " + lockPath);
            using (JsonDocument resolved = JsonDocument.Parse(File.ReadAllText(lockPath, new UTF8Encoding(false))))
            {
                Require(resolved.RootElement.GetProperty("version").GetInt32() == 1, "Unknown NuGet lock schema");
                HashSet<string> tfms = new HashSet<string>();
                foreach (JsonProperty framework in resolved.RootElement.GetProperty("dependencies").EnumerateObject())
                {
                    tfms.Add(framework.Name == ".NETFramework,Version=v4.8" ? "net48" : framework.Name);
                }
                HashSet<string> wanted = role == "Tool" ? new HashSet<string> { "net10.0" } : new HashSet<string>(Contract.Frameworks);
                Require(tfms.SetEquals(wanted), "Lock TFM matrix mismatch");
            }
        }

        private static void CheckFixtures(string root)
        {
            string folder = Path.Combine(root, "tests", "Fixtures");
            string fullFolder = Path.GetFullPath(folder);
            using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json"))))
            {
                JsonElement manifest = document.RootElement;
                Require(SameNames(manifest, "schemaVersion", "fixtures") && manifest.GetProperty("schemaVersion").GetInt32() == 1,
                    "Fixture schema mismatch");
                Require(manifest.GetProperty("fixtures").ValueKind == JsonValueKind.Array, "Fixture collection must be an array");
                HashSet<string> declared = new HashSet<string>();
                HashSet<string> identifiers = new HashSet<string>();
                string[] fields =
                {
                    "id", "path", "size", "sha256", "mdfVersion", "features", "source", "copyright", "license", "generator", "expected", "limitations",
                };
                foreach (JsonElement fixture in manifest.GetProperty("fixtures").EnumerateArray())
                {
                    Require(SameNames(fixture, fields), "Incomplete fixture provenance");
                    string id = fixture.GetProperty("id").ValueKind == JsonValueKind.String ? fixture.GetProperty("id").GetString() : null;
                    Require(!string.IsNullOrEmpty(id) && identifiers.Add(id), "Duplicate/empty fixture ID");
                    string path = fixture.GetProperty("path").GetString();
                    string[] segments = path.Split('/');
                    Require(!path.StartsWith("/", StringComparison.Ordinal) && !segments.Any(s => s == ".." || s == "." || s == "")
                            && !path.Contains("\\") && !path.Contains(":"), "Unsafe fixture path");
                    Require(declared.Add(path), "Duplicate/noncanonical fixture path");
                    string resolved = Path.GetFullPath(Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar)));
                    bool symlink = false;
                    string current = folder;
                    foreach (string segment in segments)
                    {
                        current = Path.Combine(current, segment);
                        if (new FileInfo(current).Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            symlink = true;
                        }
                    }
                    Require(resolved.StartsWith(fullFolder + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                            && File.Exists(resolved) && !symlink, "Missing/outside fixture file");
                    byte[] data = File.ReadAllBytes(resolved);
                    JsonElement size = fixture.GetProperty("size");
                    Require(size.ValueKind == JsonValueKind.Number && size.GetInt64() == data.Length && data.Length <= 262144,
                        "Fixture size mismatch or needs large-file decision");
                    // The SHA-256 of every fixture is checked by FixtureManifestTests (the test suites run on every TFM).
                    Require(Regex.IsMatch(fixture.GetProperty("mdfVersion").GetString(), "^[34]\\.\\d{2}$"), "Unknown MDF revision");
                    JsonElement features = fixture.GetProperty("features");
                    Require(features.ValueKind == JsonValueKind.Array && features.GetArrayLength() > 0
                            && features.EnumerateArray().All(x => Regex.IsMatch(x.GetString() ?? "", "^F-0(?:0[1-9]|1[0-8])$")),
                        "Invalid feature IDs");
                    foreach (string name in fields.Where(f => f != "size" && f != "features"))
                    {
                        JsonElement value = fixture.GetProperty(name);
                        Require(value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()),
                            "Empty fixture metadata: " + name);
                    }
                }
                HashSet<string> actual = new HashSet<string>();
                foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    actual.Add(Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '/'));
                }
                actual.Remove("manifest.json");
                actual.Remove("README.md");
                Require(declared.SetEquals(actual), "Undeclared or missing fixture files");
            }
        }

        private static bool SameNames(JsonElement element, params string[] names)
        {
            HashSet<string> actual = new HashSet<string>(element.EnumerateObject().Select(p => p.Name));
            return actual.SetEquals(names);
        }

        private static string Sha256Hex(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
            }
        }

        private static int CheckDocuments(string root, IEnumerable<string> files)
        {
            int count = 0;
            foreach (string relative in files)
            {
                if (!relative.EndsWith(".md", StringComparison.Ordinal))
                {
                    continue;
                }
                string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                string text = File.ReadAllText(path, new UTF8Encoding(false));
                foreach (Match match in Regex.Matches(text, "\\[[^\\]\\n]+\\]\\(([^\\s)]+)\\)"))
                {
                    string target = match.Groups[1].Value;
                    if (Regex.IsMatch(target, "^[a-zA-Z][a-zA-Z0-9+.-]*:"))
                    {
                        continue;
                    }
                    string targetPath = target.Split('?', '#')[0];
                    if (targetPath.Length == 0)
                    {
                        continue;
                    }
                    string resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path), Uri.UnescapeDataString(targetPath)
                        .Replace('/', Path.DirectorySeparatorChar)));
                    bool inside = resolved.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
                    Require(inside && (File.Exists(resolved) || Directory.Exists(resolved)),
                        "Missing document link: " + relative + ": " + target);
                    count++;
                }
            }
            return count;
        }

        private static void CheckSources(string root, IEnumerable<string> files)
        {
            const string forbidden = "\\b(?:DllImport|LibraryImport|ComImport|NativeLibrary|unsafe|stackalloc)\\b|\\bProcess\\s*\\.|\\bSystem\\.(?:Security\\.Cryptography|IO\\.Compression)\\b";
            foreach (string relative in files)
            {
                string extension = Path.GetExtension(relative).ToLowerInvariant();
                Require(!new[] { ".vcxproj", ".cpp", ".cxx", ".so", ".dylib", ".dll", ".exe" }.Contains(extension),
                    "Unreviewed native/binary source: " + relative);
                if (relative.StartsWith("src/", StringComparison.Ordinal) && extension == ".cs")
                {
                    string text = File.ReadAllText(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)), new UTF8Encoding(false));
                    Require(!Regex.IsMatch(text, forbidden), "Interop/unsafe source in library: " + relative);
                }
            }
            string workflowText = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"), new UTF8Encoding(false));
            foreach (Match match in Regex.Matches(workflowText, "uses:\\s*(\\S+)"))
            {
                Require(Regex.IsMatch(match.Groups[1].Value, "^[\\w.-]+/[\\w.-]+@[a-f0-9]{40}$"),
                    "Action must be pinned by commit SHA: " + match.Groups[1].Value);
            }
            Require(!workflowText.Contains("pull_request_target") && workflowText.Contains("contents: read"),
                "Unsafe workflow permissions/trigger");
            Require(File.ReadAllText(Path.Combine(root, "tools", "MdfLib.Ci", "Testing.cs")).Contains("--locked-mode"),
                "Missing locked restore");
            Require(!File.Exists(Path.Combine(root, ".github", "workflows", "bootstrap-locks.yml")),
                "Temporary lock bootstrap workflow must be removed");
        }

        /// <summary>The check job: every contract above, then the workflow notice with the counts.</summary>
        public static void Verify()
        {
            string root = Shell.Root;
            ProcessResult listing = Shell.Execute("git", new[] { "ls-files", "-c", "--others", "--exclude-standard" }, null);
            List<string> files = listing.Standard.Split('\n').Where(f => f.Length > 0).Distinct().OrderBy(f => f, StringComparer.Ordinal).ToList();
            CheckConfig(root);
            CheckFixtures(root);
            int links = CheckDocuments(root, files);
            CheckSources(root, files);
            Shell.Annotation("notice", "Repository checks", files.Count + " files; " + links
                + " local links; SDK/TFM/dependencies/fixture/source/workflow contracts passed");
        }
    }
}
