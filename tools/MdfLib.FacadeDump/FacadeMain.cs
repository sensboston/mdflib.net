// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Command line of the facade side of the reading oracle (tools/ci/oracle.py): the same dump as the native tool,
// produced by the MdfLibrary facade of src/MdfLib. The dump itself is shared source (NativeDump.cs).
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace MdfOracle
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "dump")
            {
                // The facade answers a read failure with false and keeps the exception to its trace (MdfReader.Report).
                // The refusal names that trace, so the oracle's report says why the facade could not read the file.
                List<string> failures = new List<string>();
                CollectingListener listener = new CollectingListener(failures);
                Trace.Listeners.Add(listener);
                List<string> lines;
                try { lines = Dumper.Lines(args[1]); }
                catch (InvalidOperationException error) when (error.Message.StartsWith("The wrapper could not read", StringComparison.Ordinal))
                {
                    string cause = failures.Count == 0 ? "no read failure was traced" : string.Join(" | ", failures);
                    // The refusal itself stays identical to the native dump's, so both sides compare equal when both
                    // refuse; the cause goes to standard error, where the oracle report shows it.
                    Console.Error.WriteLine("cause: " + cause.Replace('\r', ' ').Replace('\n', ' '));
                    throw;
                }
                finally { Trace.Listeners.Remove(listener); }
                foreach (string line in lines) { Console.WriteLine(line); }
                return 0;
            }
            Console.Error.WriteLine("usage: dump <file>");
            return 2;
        }
    }

    // Collects the facade's trace messages; the dump prints nothing else to the trace.
    internal sealed class CollectingListener : TraceListener
    {
        private readonly List<string> messages_;

        public CollectingListener(List<string> messages) { messages_ = messages; }

        public override void Write(string message) { messages_.Add(message); }

        public override void WriteLine(string message) { messages_.Add(message); }
    }
}
