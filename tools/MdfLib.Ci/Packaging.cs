// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Restore, license inventory, pack and the managed-only package audit
// The license summary covers the locked build/test packages, not the shipping closure.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace MdfLib.Ci
{
    internal static class Packaging
    {
        public static void Package()
        {
            string artifacts = Shell.Under("artifacts");
            string packages = Path.Combine(artifacts, "packages");
            string logs = Path.Combine(artifacts, "logs");
            Directory.CreateDirectory(packages);
            Directory.CreateDirectory(logs);
            Contract.RecordEnvironment("artifacts");
            Contract.VerifyBuildContract("artifacts/contracts", null);
            Shell.Run("dotnet", "restore", "MdfLib.sln", "--locked-mode");
            WriteInventory(Path.Combine(artifacts, "dependency-licenses.json"));
            Shell.Run("dotnet", "build", Contract.Library, "-c", "Release", "--no-restore", "-bl:" + Path.Combine(logs, "library.binlog"));
            Shell.Run("dotnet", "pack", Contract.Library, "-c", "Release", "--no-build", "--no-restore", "--output", packages);
            Shell.Run("dotnet", "build", Contract.Tool, "-c", "Release", "--no-restore", "-bl:" + Path.Combine(logs, "auditor.binlog"));
            string[] candidates = Directory.GetFiles(packages, "*.nupkg");
            if (candidates.Length != 1)
            {
                throw new CiError("Expected exactly one package");
            }
            string report = Path.Combine(artifacts, "package-audit.json");
            Shell.Run("dotnet", "run", "--project", Contract.Tool, "-c", "Release", "--no-build", "--no-restore", "--",
                candidates[0], Shell.Under("src", "MdfLib", "obj", "project.assets.json"), Shell.Root, report);
            Shell.Annotation("notice", "Package audit report", File.ReadAllText(report, new UTF8Encoding(false)));
        }

        /// <summary>The license of every package of the locked build/test graph, read from its nuspec, as JSON.</summary>
        public static void WriteInventory(string destination)
        {
            // A sorted tree of plain values, so the file is the same from run to run.
            SortedDictionary<string, SortedDictionary<string, object>> packages = new SortedDictionary<string, SortedDictionary<string, object>>(StringComparer.Ordinal);
            List<string> projects = new List<string> { Contract.Library };
            projects.AddRange(Contract.Tests);
            projects.Add(Contract.Tool);
            foreach (string project in projects)
            {
                string assets = Shell.Under(Path.GetDirectoryName(project.Replace('/', Path.DirectorySeparatorChar)), "obj", "project.assets.json");
                using (JsonDocument graph = JsonDocument.Parse(File.ReadAllText(assets, new UTF8Encoding(false))))
                {
                    List<string> folders = graph.RootElement.GetProperty("packageFolders").EnumerateObject().Select(p => p.Name).ToList();
                    foreach (JsonProperty library in graph.RootElement.GetProperty("libraries").EnumerateObject())
                    {
                        if (library.Value.GetProperty("type").GetString() != "package")
                        {
                            continue;
                        }
                        string name = library.Name;
                        if (!packages.ContainsKey(name))
                        {
                            packages[name] = new SortedDictionary<string, object>(StringComparer.Ordinal)
                            {
                                { "projects", new List<string>() },
                                { "licenses", new List<SortedDictionary<string, string>>() },
                            };
                        }
                        SortedDictionary<string, object> item = packages[name];
                        ((List<string>)item["projects"]).Add(project);
                        List<SortedDictionary<string, string>> licenses = (List<SortedDictionary<string, string>>)item["licenses"];
                        if (licenses.Count > 0)
                        {
                            continue;
                        }
                        string relative = library.Value.GetProperty("path").GetString();
                        string directory = folders.Select(folder => Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar)))
                            .First(Directory.Exists);
                        string nuspec = Directory.GetFiles(directory, "*.nuspec").First();
                        XElement metadata = XDocument.Load(nuspec).Root;
                        string licenseUrl = null;
                        foreach (XElement element in metadata.Descendants())
                        {
                            string kind = element.Name.LocalName;
                            if (kind == "license")
                            {
                                SortedDictionary<string, string> entry = new SortedDictionary<string, string>(StringComparer.Ordinal)
                                {
                                    { "type", (string)element.Attribute("type") },
                                    { "value", element.Value },
                                };
                                if (entry["type"] == "file")
                                {
                                    string document = Path.Combine(directory, entry["value"].Replace('/', Path.DirectorySeparatorChar));
                                    byte[] bytes = File.ReadAllBytes(document);
                                    entry["sha256"] = Hex(bytes);
                                    entry["text"] = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
                                }
                                licenses.Add(entry);
                            }
                            else if (kind == "licenseUrl" && !string.IsNullOrEmpty(element.Value))
                            {
                                licenseUrl = element.Value;
                            }
                        }
                        if (licenses.Count == 0 && licenseUrl == null)
                        {
                            throw new CiError("Dependency has no license metadata: " + name);
                        }
                        if (licenseUrl != null)
                        {
                            item["licenseUrl"] = licenseUrl;
                        }
                        if (licenses.Count == 0)
                        {
                            // Old packages may only declare a URL; inventory it rather than inventing an SPDX ID.
                            licenses.Add(new SortedDictionary<string, string>(StringComparer.Ordinal)
                            {
                                { "type", "legacy-url" }, { "value", licenseUrl },
                            });
                        }
                    }
                }
            }
            Shell.WriteText(destination, JsonSerializer.Serialize(packages, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            StringBuilder summary = new StringBuilder();
            foreach (KeyValuePair<string, SortedDictionary<string, object>> item in packages)
            {
                if (summary.Length > 0)
                {
                    summary.Append('\n');
                }
                List<SortedDictionary<string, string>> licenses = (List<SortedDictionary<string, string>>)item.Value["licenses"];
                summary.Append(item.Key).Append(": ").Append(string.Join(", ", licenses.Select(e => e["type"] + "=" + e["value"])));
            }
            Shell.Annotation("notice", "Build-test dependency license inventory (not shipping closure)", summary.ToString());
        }

        private static string Hex(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
