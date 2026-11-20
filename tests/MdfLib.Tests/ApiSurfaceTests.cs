// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.IO;
using MdfLib.TestSupport;
using Xunit;

namespace MdfLib.Tests
{
    public sealed class ApiSurfaceTests
    {
        [Fact]
        public void UnusedGenericMethodParametersCannotHideBehindANongenericBaseline()
        {
            InvalidDataException error = Assert.Throws<InvalidDataException>(() => ApiSurface.Read(new[] { typeof(GenericProbe) }));
            Assert.Contains("Generic public methods", error.Message);
        }

        [Fact]
        public void VarargsCannotHideBehindAnOrdinaryParameterList()
        {
            // Inspect metadata through reflection; never invoke a varargs method on any runtime.
            InvalidDataException error = Assert.Throws<InvalidDataException>(() => ApiSurface.Read(new[] { typeof(VarargsProbe) }));
            Assert.Contains("calling convention", error.Message);
        }

        [Fact]
        public void OrdinaryMethodsStillHaveAnExactStableSignature()
        {
            string name = typeof(OrdinaryProbe).FullName;
            Assert.Equal(new[]
            {
                "M|" + name + "|Probe|System.Void|6|true|false|false|",
                "T|" + name + "|System.Object|true|true"
            }, ApiSurface.Read(new[] { typeof(OrdinaryProbe) }));
        }

        private static class OrdinaryProbe
        {
            public static void Probe() { }
        }

        private static class GenericProbe
        {
            public static void Probe<T>() { }
        }

        private static class VarargsProbe
        {
            public static void Probe(__arglist) { }
        }
    }
}
