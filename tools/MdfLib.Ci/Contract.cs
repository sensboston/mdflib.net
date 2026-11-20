// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// The project inventory and the build contract shared by every CI command.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MdfLib.Ci
{
    internal static class Contract
    {
        public const string Library = "src/MdfLib/MdfLib.csproj";
        public const string Tool = "tools/MdfLib.PackageAudit/MdfLib.PackageAudit.csproj";
        public const string OracleEngine = "tools/MdfLib.Oracle/MdfLib.Oracle.csproj";
        public const string OracleNative = "tools/MdfLib.NativeOracle/MdfLib.NativeOracle.csproj";
        public const string OracleFacade = "tools/MdfLib.FacadeDump/MdfLib.FacadeDump.csproj";
        public const string CiTool = "tools/MdfLib.Ci/MdfLib.Ci.csproj";

        public static readonly string[] Tests =
        {
            "tests/MdfLib.Tests/MdfLib.Tests.csproj",
            "tests/MdfLib.CompatibilityTests/MdfLib.CompatibilityTests.csproj",
        };

        public static readonly string[] OracleTools = { OracleEngine, OracleNative, OracleFacade };

        public static readonly string[] Frameworks = { "net48", "net8.0", "net10.0" };

        /// <summary>The MSBuild properties every library and test project must report (the build contract).</summary>
        public static readonly string[][] Properties =
        {
            new[] { "LangVersion", "7.3" },
            new[] { "ImplicitUsings", "disable" },
            new[] { "Nullable", "disable" },
            new[] { "AllowUnsafeBlocks", "false" },
            new[] { "Deterministic", "true" },
            new[] { "TreatWarningsAsErrors", "true" },
            new[] { "RestorePackagesWithLockFile", "true" },
            new[] { "RestoreLockedMode", "true" },
            new[] { "NETCoreSdkVersion", "10.0.404" },
        };

        /// <summary>Writes the.NET and OS description of the runner next to the artifacts and to the log.</summary>
        public static void RecordEnvironment(string output)
        {
            Directory.CreateDirectory(Path.Combine(Shell.Root, output));
            string details = Shell.Run("dotnet", "--info") + Shell.Run("dotnet", "--list-runtimes");
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                ProcessResult registry = Shell.Execute("pwsh", new[]
                {
                    "-NoProfile", "-Command",
                    "Get-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\NET Framework Setup\\NDP\\v4\\Full' | Select-Object Release,Version | Format-List",
                }, null);
                details += registry.Standard + registry.Error;
            }
            Shell.WriteText(Path.Combine(Shell.Root, output, "environment.txt"), details);
            // Keep the registry/runtime tail below GitHub's per-annotation truncation limit.
            Shell.Annotation("notice", "Runtime environment", Shell.Tail(details, 3500));
        }

        /// <summary>
        /// Checks the effective MSBuild properties of every project for a framework (or the whole matrix when none
        /// is given) and writes one JSON document per project and framework into the output folder.
        /// </summary>
        public static void VerifyBuildContract(string output, string selected)
        {
            string folder = Path.Combine(Shell.Root, output);
            Directory.CreateDirectory(folder);
            string names = string.Join(",", Properties.Select(pair => pair[0])
                .Concat(new[] { "TargetFramework", "TargetFrameworks", "RepositoryProjectRole" }));
            List<KeyValuePair<string, string>> projects = new List<KeyValuePair<string, string>>();
            projects.Add(new KeyValuePair<string, string>(Library, "Library"));
            foreach (string test in Tests)
            {
                projects.Add(new KeyValuePair<string, string>(test, "Test"));
            }
            projects.Add(new KeyValuePair<string, string>(Tool, "Tool"));
            foreach (KeyValuePair<string, string> item in projects)
            {
                string project = item.Key;
                string role = item.Value;
                string[] frameworks = role == "Tool" ? new[] { "net10.0" }
                    : selected != null ? new[] { selected } : Frameworks;
                foreach (string framework in frameworks)
                {
                    string[] args =
                    {
                        "msbuild", project, "-nologo", "-p:Configuration=Release", "-p:TargetFramework=" + framework,
                    };
                    Shell.Run("dotnet", Concat(args, "-target:ValidateBuildContract"));
                    string text = Shell.Run("dotnet", Concat(args, "-getProperty:" + names));
                    Dictionary<string, string> values = ReadProperties(text);
                    foreach (string[] pair in Properties)
                    {
                        string actual;
                        values.TryGetValue(pair[0], out actual);
                        if (actual != pair[1])
                        {
                            throw new CiError(project + "/" + framework + ": " + pair[0] + " must be " + pair[1] + ", got " + actual);
                        }
                    }
                    if (role != values["RepositoryProjectRole"] || values["TargetFramework"] != framework)
                    {
                        throw new CiError("Unexpected effective project role/framework");
                    }
                    if (role != "Tool" && values["TargetFrameworks"] != string.Join(";", Frameworks))
                    {
                        throw new CiError("Target matrix differs from the contract");
                    }
                    string file = Path.GetFileNameWithoutExtension(project) + "-" + framework + ".json";
                    Shell.WriteText(Path.Combine(folder, file), JsonSerializer.Serialize(values,
                        new JsonSerializerOptions { WriteIndented = true }) + "\n");
                }
            }
        }

        private static string[] Concat(string[] first, string last)
        {
            string[] result = new string[first.Length + 1];
            first.CopyTo(result, 0);
            result[first.Length] = last;
            return result;
        }

        /// <summary>The Properties object of the msbuild -getProperty output, as strings.</summary>
        private static Dictionary<string, string> ReadProperties(string text)
        {
            int start = text.IndexOf('{');
            using (JsonDocument document = JsonDocument.Parse(text.Substring(start)))
            {
                Dictionary<string, string> values = new Dictionary<string, string>();
                foreach (JsonProperty property in document.RootElement.GetProperty("Properties").EnumerateObject())
                {
                    values[property.Name] = property.Value.GetString();
                }
                return values;
            }
        }
    }
}
