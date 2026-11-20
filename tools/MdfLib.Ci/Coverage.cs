// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Reports the line coverage of the MdfLibrary facade from the Cobertura files that `dotnet test --collect "Code Coverage;
// Format=cobertura"` writes. Both suites are merged per line (a line hit by either suite is covered). The command reports
// the figure and the uncovered ranges per file; it does not fail on a number: no threshold is set (.).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace MdfLib.Ci
{
    internal static class Coverage
    {
        // The facade's own source folder; a Cobertura file name is matched by this suffix, whatever the checkout path.
        private const string Marker = "src/MdfLib/MdfLibrary/";

        public static int Run()
        {
            string root = Shell.Under("artifacts", "test-results");
            if (!Directory.Exists(root))
            {
                throw new CiError("no test results folder " + root + "; run-tests did not run");
            }
            List<string> files = Directory.GetFiles(root, "*.cobertura.xml", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal).ToList();
            if (files.Count == 0)
            {
                List<string> found = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                    .Select(f => f.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar)).OrderBy(f => f, StringComparer.Ordinal)
                    .Take(40).ToList();
                throw new CiError("no *.cobertura.xml under " + root + "; the test run did not collect coverage. Found: "
                    + (found.Count == 0 ? "nothing" : string.Join(", ", found)));
            }
            Dictionary<string, Dictionary<int, bool>> lines = new Dictionary<string, Dictionary<int, bool>>();
            foreach (string file in files)
            {
                foreach (XElement cls in XDocument.Load(file).Descendants("class"))
                {
                    string name = ((string)cls.Attribute("filename") ?? "").Replace('\\', '/');
                    int at = name.IndexOf(Marker, StringComparison.Ordinal);
                    if (at < 0)
                    {
                        continue;
                    }
                    string key = name.Substring(at + Marker.Length);
                    Dictionary<int, bool> map;
                    if (!lines.TryGetValue(key, out map))
                    {
                        map = new Dictionary<int, bool>();
                        lines[key] = map;
                    }
                    foreach (XElement line in cls.Descendants("line"))
                    {
                        int number = int.Parse((string)line.Attribute("number"), CultureInfo.InvariantCulture);
                        bool hit = int.Parse((string)line.Attribute("hits"), CultureInfo.InvariantCulture) > 0;
                        bool before;
                        map[number] = map.TryGetValue(number, out before) && before || hit;
                    }
                }
            }
            if (lines.Count == 0)
            {
                throw new CiError("no facade source (" + Marker + ") in the coverage files; the measurement would prove nothing");
            }
            int valid = lines.Values.Sum(map => map.Count);
            int covered = lines.Values.Sum(map => map.Values.Count(hit => hit));
            string percent = (valid == 0 ? 0.0 : 100.0 * covered / valid).ToString("F1", CultureInfo.InvariantCulture);
            string headline = covered + " of " + valid + " facade lines covered (" + percent + "%), " + lines.Count
                + " files, " + files.Count + " coverage file(s); no threshold";
            Console.Out.WriteLine("facade coverage: " + headline);
            Shell.Annotation("notice", "Facade line coverage", headline);
            StringBuilder table = new StringBuilder();
            table.AppendLine("## Facade line coverage");
            table.AppendLine();
            table.AppendLine(headline);
            table.AppendLine();
            table.AppendLine("| File | Covered | Lines | Coverage |");
            table.AppendLine("| --- | ---: | ---: | ---: |");
            foreach (KeyValuePair<string, Dictionary<int, bool>> entry in lines.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                int fileValid = entry.Value.Count;
                int fileCovered = entry.Value.Values.Count(hit => hit);
                string filePercent = (fileValid == 0 ? 0.0 : 100.0 * fileCovered / fileValid).ToString("F1", CultureInfo.InvariantCulture);
                table.AppendLine("| " + entry.Key + " | " + fileCovered + " | " + fileValid + " | " + filePercent + "% |");
                List<int> uncovered = entry.Value.Where(pair => !pair.Value).Select(pair => pair.Key).OrderBy(n => n).ToList();
                if (uncovered.Count > 0)
                {
                    string ranges = Ranges(uncovered);
                    Shell.Annotation("notice", "Uncovered facade lines: " + entry.Key, uncovered.Count + " line(s): " + ranges);
                }
            }
            string summary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
            if (!string.IsNullOrEmpty(summary))
            {
                File.AppendAllText(summary, table.ToString(), new UTF8Encoding(false));
            }
            return 0;
        }

        /// <summary>Sorted line numbers folded into ranges: 3, 4, 5, 9 becomes "3-5, 9".</summary>
        private static string Ranges(List<int> numbers)
        {
            List<string> parts = new List<string>();
            int start = numbers[0];
            int previous = numbers[0];
            for (int index = 1; index <= numbers.Count; index++)
            {
                if (index < numbers.Count && numbers[index] == previous + 1)
                {
                    previous = numbers[index];
                    continue;
                }
                parts.Add(start == previous ? start.ToString(CultureInfo.InvariantCulture)
                    : start + "-" + previous);
                if (index < numbers.Count)
                {
                    start = numbers[index];
                    previous = numbers[index];
                }
            }
            return string.Join(", ", parts);
        }
    }
}
