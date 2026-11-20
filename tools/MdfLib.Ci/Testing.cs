// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Build and test both suites for one framework, then fail closed on the TRX results
// (the port of tools/ci/run_tests.py and tools/ci/check_test_results.py).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace MdfLib.Ci
{
    internal static class Testing
    {
        private const string TeamTest = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

        public static void RunTests(string framework)
        {
            if (Array.IndexOf(Contract.Frameworks, framework) < 0)
            {
                throw new CiError("Unknown target framework");
            }
            string results = Path.Combine(Shell.Root, "artifacts", "test-results");
            string logs = Path.Combine(Shell.Root, "artifacts", "logs");
            Directory.CreateDirectory(results);
            Directory.CreateDirectory(logs);
            Contract.RecordEnvironment("artifacts");
            Contract.VerifyBuildContract("artifacts/contracts", framework);
            Shell.Run("dotnet", "restore", "MdfLib.sln", "--locked-mode");
            List<string> suites = new List<string>();
            foreach (string project in Contract.Tests)
            {
                string suite = Path.GetFileNameWithoutExtension(project);
                suites.Add(suite);
                Shell.Run("dotnet", "build", project, "--configuration", "Release", "--framework", framework,
                    "--no-restore", "-bl:" + Path.Combine(logs, suite + ".binlog"));
                Shell.Run("dotnet", "test", project, "--configuration", "Release", "--framework", framework,
                    "--no-build", "--no-restore", "--logger", "trx;LogFileName=" + suite + ".trx",
                    "--collect", "Code Coverage;Format=cobertura", "--results-directory", results);
            }
            Dictionary<string, int> counts = InspectSuites(results, suites);
            Shell.Annotation("notice", "Executed test suites " + framework, Describe(counts));
        }

        /// <summary>Fails closed on a missing, empty, skipped or unsuccessful suite; returns the test count per suite.</summary>
        public static Dictionary<string, int> InspectSuites(string directory, IList<string> suites)
        {
            HashSet<string> expected = new HashSet<string>(suites.Select(s => s + ".trx"));
            HashSet<string> actual = new HashSet<string>(Directory.GetFiles(directory, "*.trx").Select(f => Path.GetFileName(f)));
            if (!actual.SetEquals(expected))
            {
                throw new CiError("Test result set differs: expected " + string.Join(", ", expected.OrderBy(x => x, StringComparer.Ordinal))
                    + ", got " + string.Join(", ", actual.OrderBy(x => x, StringComparer.Ordinal)));
            }
            Dictionary<string, int> counts = new Dictionary<string, int>();
            foreach (string suite in suites)
            {
                counts[suite] = InspectResult(Path.Combine(directory, suite + ".trx"));
            }
            return counts;
        }

        public static string Describe(Dictionary<string, int> counts)
        {
            return string.Join(", ", counts.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => "'" + pair.Key + "': " + pair.Value));
        }

        private static int Counter(XElement counters, string name)
        {
            string text = (string)counters.Attribute(name) ?? "0";
            return int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static int InspectResult(string path)
        {
            XElement root = XDocument.Load(path).Root;
            XElement summary = root.Element(XName.Get("ResultSummary", TeamTest));
            string outcome = summary == null ? null : (string)summary.Attribute("outcome");
            if (outcome != "Completed" && outcome != "Passed")
            {
                throw new CiError(path + ": incomplete or failed test run");
            }
            XElement counters = summary.Element(XName.Get("Counters", TeamTest));
            if (counters == null)
            {
                throw new CiError(path + ": missing counters");
            }
            int total = Counter(counters, "total");
            int executed = Counter(counters, "executed");
            int passed = Counter(counters, "passed");
            List<XElement> results = root.Elements(XName.Get("Results", TeamTest)).Elements(XName.Get("UnitTestResult", TeamTest)).ToList();
            if (total <= 0 || executed != total || passed != total || results.Count != total)
            {
                throw new CiError(path + ": missing, skipped or unsuccessful tests: total " + total + ", executed " + executed + ", passed " + passed);
            }
            if (results.Any(result => (string)result.Attribute("outcome") != "Passed"))
            {
                throw new CiError(path + ": not all individual tests passed");
            }
            foreach (string name in new[] { "failed", "error", "timeout", "aborted", "notExecuted", "inconclusive" })
            {
                if (Counter(counters, name) != 0)
                {
                    throw new CiError(path + ": unsuccessful test counters");
                }
            }
            return total;
        }
    }
}
