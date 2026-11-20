// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Command line of the oracle tools, shared by the engine tool and the native tool (see Scenario.cs):
//  list the scenario names
//  write <scenario> <file> writes one scenario to a new file
//  dump <file> prints the canonical description of a file, one line per fact (Dumper.cs)
using System;

namespace MdfOracle
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "list")
            {
                foreach (string name in Scenario.Names) { Console.WriteLine(name); }
                return 0;
            }
            if (args.Length == 3 && args[0] == "write")
            {
                Scenario.Write(args[1], args[2]);
                return 0;
            }
            if (args.Length == 2 && args[0] == "dump")
            {
                foreach (string line in Dumper.Lines(args[1])) { Console.WriteLine(line); }
                return 0;
            }
            Console.Error.WriteLine("usage: list | write <scenario> <file> | dump <file>");
            return 2;
        }
    }
}
