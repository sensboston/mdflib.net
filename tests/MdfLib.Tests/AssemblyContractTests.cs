// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Linq;
using MdfLib.TestSupport;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Xunit;

namespace MdfLib.Tests
{
    public sealed class AssemblyContractTests
    {
        private static Assembly LoadLibrary()
        {
            return Assembly.Load("MdfLibrary.Managed");
        }

        [Fact]
        public void LibraryCanBeLoadedAsManagedAssembly()
        {
            Assert.Equal("MdfLibrary.Managed", LoadLibrary().GetName().Name);
            Assert.Equal(new Version(1, 0, 0, 0), LoadLibrary().GetName().Version);
        }

        [Fact]
        public void OnlyTheMdfLibraryNamespaceIsPublic()
        {
            // The engine is internal in 1.0 and reaches the test suites through InternalsVisibleTo.
            string[] namespaces = LoadLibrary().GetExportedTypes().Select(type => type.Namespace).Distinct().ToArray();
            Assert.Equal(new[] { "MdfLibrary" }, namespaces);
        }

        [Fact]
        public void LibraryDoesNotExposeComTypes()
        {
            ComVisibleAttribute attribute = LoadLibrary().GetCustomAttribute<ComVisibleAttribute>();
            Assert.NotNull(attribute);
            Assert.False(attribute.Value);
        }

        [Fact]
        public void PublicApiMatchesTheReviewedCommonSignatureBaseline()
        {
            string[] expected = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "public-api.txt"))
                .Where(line => line.Length != 0 && !line.StartsWith("#", StringComparison.Ordinal))
                .OrderBy(line => line, StringComparer.Ordinal).ToArray();
            Assert.NotEmpty(expected);
            Assert.Equal(expected, ApiSurface.Read(LoadLibrary()));
        }

        [Fact]
        public void LibraryTargetsTheSameFrameworkAsTheRunningTest()
        {
            string expected = typeof(AssemblyContractTests).Assembly
                .GetCustomAttribute<TargetFrameworkAttribute>().FrameworkName;
            Assert.Equal(expected, LoadLibrary().GetCustomAttribute<TargetFrameworkAttribute>().FrameworkName);
        }

        [Fact]
        public void TestsExecuteOnTheRequestedRuntime()
        {
#if NETFRAMEWORK
            Assert.Equal(4, Environment.Version.Major);
            Assert.Equal(PlatformID.Win32NT, Environment.OSVersion.Platform);
            Assert.Null(Type.GetType("Mono.Runtime"));
#elif NET8_0
            Assert.Equal(8, Environment.Version.Major);
#elif NET10_0
            Assert.Equal(10, Environment.Version.Major);
#else
#error This test suite must run on a documented target framework.
#endif
        }

        [Fact]
        public void LibraryHasNoTestFrameworkDependency()
        {
            string[] names = LoadLibrary().GetReferencedAssemblies().Select(name => name.Name).ToArray();
            Assert.DoesNotContain(names, name => name.StartsWith("xunit", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, name => name.StartsWith("Microsoft.Test", StringComparison.OrdinalIgnoreCase));
        }
    }
}
