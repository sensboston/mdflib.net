// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Process execution, GitHub annotations, the repository root and small text helpers for the CI tool.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MdfLib.Ci
{
    /// <summary>A failed CI step. Its message is the one the annotation carries.</summary>
    internal sealed class CiError : Exception
    {
        public CiError(string message) : base(message)
        {
        }
    }

    /// <summary>The outcome of one process: its exit code and the two streams, kept apart.</summary>
    internal sealed class ProcessResult
    {
        public int Code;
        public string Standard;
        public string Error;
    }

    internal static class Shell
    {
        private static string root_;

        /// <summary>The repository root: the nearest folder above the current one that holds MdfLib.sln.</summary>
        public static string Root
        {
            get
            {
                if (root_ == null)
                {
                    string folder = Directory.GetCurrentDirectory();
                    while (folder != null && !File.Exists(Path.Combine(folder, "MdfLib.sln")))
                    {
                        folder = Path.GetDirectoryName(folder);
                    }
                    if (folder == null)
                    {
                        throw new CiError("MdfLib.sln not found above " + Directory.GetCurrentDirectory());
                    }
                    root_ = folder;
                }
                return root_;
            }
        }

        /// <summary>A path under the root; the parts use '/' as in the repository.</summary>
        public static string Under(params string[] parts)
        {
            string result = Root;
            foreach (string part in parts)
            {
                result = Path.Combine(result, part.Replace('/', Path.DirectorySeparatorChar));
            }
            return result;
        }

        /// <summary>The repository-relative POSIX path of a file under the root.</summary>
        public static string Relative(string path)
        {
            string full = Path.GetFullPath(path);
            string prefix = Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.Ordinal))
            {
                return full;
            }
            return full.Substring(prefix.Length).Replace(Path.DirectorySeparatorChar, '/');
        }

        public static void Annotation(string level, string title, string message)
        {
            Console.Out.WriteLine("::" + level + " title=" + Escape(title) + "::" + Escape(message));
            Console.Out.Flush();
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");
        }

        public static string Tail(string text, int length)
        {
            return text.Length <= length ? text : text.Substring(text.Length - length);
        }

        /// <summary>Runs a tool in the root with its merged output echoed; a non-zero exit is annotated and fails.</summary>
        public static string Run(string file, params string[] args)
        {
            Console.Out.WriteLine("+ " + file + " " + string.Join(" ", args));
            Console.Out.Flush();
            ProcessResult result = Execute(file, args, null);
            string merged = result.Standard + result.Error;
            Console.Out.WriteLine(merged);
            Console.Out.Flush();
            if (result.Code != 0)
            {
                Annotation("error", "CI command failed", Tail(merged, 3500));
                throw new CiError(Path.GetFileName(file) + " " + string.Join(" ", args) + " exited with " + result.Code);
            }
            return merged;
        }

        /// <summary>Runs a tool and returns the outcome without failing; the caller decides what a code means.</summary>
        public static ProcessResult Execute(string file, string[] args, IDictionary<string, string> environment)
        {
            ProcessStartInfo info = new ProcessStartInfo
            {
                FileName = file,
                WorkingDirectory = Root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (string argument in args)
            {
                info.ArgumentList.Add(argument);
            }
            if (environment != null)
            {
                foreach (KeyValuePair<string, string> pair in environment)
                {
                    info.Environment[pair.Key] = pair.Value;
                }
            }
            StringBuilder standard = new StringBuilder();
            StringBuilder error = new StringBuilder();
            using (Process process = new Process { StartInfo = info })
            {
                process.OutputDataReceived += (sender, data) => { if (data.Data != null) { lock (standard) { standard.Append(data.Data).Append('\n'); } } };
                process.ErrorDataReceived += (sender, data) => { if (data.Data != null) { lock (error) { error.Append(data.Data).Append('\n'); } } };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                return new ProcessResult { Code = process.ExitCode, Standard = standard.ToString(), Error = error.ToString() };
            }
        }

        /// <summary>Runs a tool with its streams inherited (the output goes straight to the log) and returns the code.</summary>
        public static int Inherit(string file, string[] args, IDictionary<string, string> environment)
        {
            ProcessStartInfo info = new ProcessStartInfo { FileName = file, WorkingDirectory = Root, UseShellExecute = false };
            foreach (string argument in args)
            {
                info.ArgumentList.Add(argument);
            }
            if (environment != null)
            {
                foreach (KeyValuePair<string, string> pair in environment)
                {
                    info.Environment[pair.Key] = pair.Value;
                }
            }
            using (Process process = Process.Start(info))
            {
                process.WaitForExit();
                return process.ExitCode;
            }
        }

        /// <summary>Appends a Markdown block to the job summary when GitHub provides one; otherwise prints it.</summary>
        public static void Summary(string markdown)
        {
            string target = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
            if (string.IsNullOrEmpty(target))
            {
                Console.Out.WriteLine(markdown);
                return;
            }
            File.AppendAllText(target, markdown + "\n", new UTF8Encoding(false));
        }

        /// <summary>The lines the way Python's str.splitlines splits them: no trailing empty line.</summary>
        public static List<string> SplitLines(string text)
        {
            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] parts = normalized.Split('\n');
            List<string> result = new List<string>();
            for (int index = 0; index < parts.Length; index++)
            {
                if (index == parts.Length - 1 && parts[index].Length == 0)
                {
                    break;
                }
                result.Add(parts[index]);
            }
            return result;
        }

        public static string ReadText(string path)
        {
            return File.ReadAllText(path, new UTF8Encoding(false));
        }

        public static void WriteText(string path, string text)
        {
            string folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        public static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new CiError(message);
            }
        }
    }
}
