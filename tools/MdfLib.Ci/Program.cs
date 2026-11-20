// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// The repository's CI commands in one tool (the port of the Python scripts that tools/ci held). Each command is run
// from the repository root with `dotnet run --project tools/MdfLib.Ci -- <command>`.
using System;
using System.Linq;

namespace MdfLib.Ci
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("usage: MdfLib.Ci <verify-repository|run-tests <tfm>|check-test-results <dir> <suite>...|package|oracle [options]>");
                return 2;
            }
            string command = args[0];
            string[] rest = args.Skip(1).ToArray();
            string title = "CI command failed";
            try
            {
                switch (command)
                {
                    case "verify-repository":
                        title = "Repository contract failed";
                        Repository.Verify();
                        return 0;
                    case "run-tests":
                        title = "Build/test job failed";
                        Testing.RunTests(rest[0]);
                        return 0;
                    case "check-test-results":
                        title = "Test result verification failed";
                        Shell.Annotation("notice", "Executed test suites", Testing.Describe(Testing.InspectSuites(rest[0], rest.Skip(1).ToArray())));
                        return 0;
                    case "package":
                        title = "Package job failed";
                        Packaging.Package();
                        return 0;
                    case "oracle":
                        return Oracle.Run(rest);
                    case "facade-coverage":
                        title = "Facade coverage report failed";
                        return Coverage.Run();
                    case "public-api":
                        title = "Public API baseline";
                        return PublicApi.Run(rest);
                    default:
                        Console.Error.WriteLine("Unknown command: " + command);
                        return 2;
                }
            }
            catch (Exception error)
            {
                Shell.Annotation("error", title, error.Message);
                Console.Error.WriteLine(error);
                return 1;
            }
        }
    }
}
