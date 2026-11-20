// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using Xunit;

namespace MdfLib.CompatibilityTests
{
    // These tests validate corpus infrastructure, not MDF compatibility.
    public sealed class FixtureManifestTests
    {
        private static Manifest ReadManifest()
        {
            // Assembly.Location can point at xUnit's Framework shadow-copy directory.
            // AppContext.BaseDirectory is the test application/content directory.
            string directory = AppContext.BaseDirectory;
            using (Stream stream = File.OpenRead(Path.Combine(directory, "Fixtures", "manifest.json")))
            {
                return (Manifest)new DataContractJsonSerializer(typeof(Manifest)).ReadObject(stream);
            }
        }

        [Fact]
        public void ManifestIsReadableAndUsesTheDocumentedSchema()
        {
            Manifest manifest = ReadManifest();
            Assert.Equal(1, manifest.SchemaVersion);
            Assert.NotNull(manifest.Fixtures);
        }

        [Fact]
        public void ManifestHasNoNullOrDuplicateFixtureIdentifiers()
        {
            Manifest manifest = ReadManifest();
            HashSet<string> identifiers = new HashSet<string>();
            foreach (Fixture fixture in manifest.Fixtures)
            {
                Assert.NotNull(fixture);
                Assert.False(string.IsNullOrWhiteSpace(fixture.Id));
                Assert.True(identifiers.Add(fixture.Id), "Duplicate fixture ID: " + fixture.Id);
            }
        }

        [Fact]
        public void EveryFixtureMatchesItsRecordedSizeAndSha256()
        {
            // The byte checks moved here from the repository checks: this suite runs on every target framework.
            Manifest manifest = ReadManifest();
            string folder = Path.Combine(AppContext.BaseDirectory, "Fixtures");
            List<string> mismatches = new List<string>();
            foreach (Fixture fixture in manifest.Fixtures)
            {
                byte[] data = File.ReadAllBytes(Path.Combine(folder, fixture.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (data.LongLength != fixture.Size || Sha256Hex(data) != fixture.Sha256)
                {
                    mismatches.Add(fixture.Path);
                }
            }
            Assert.Empty(mismatches);
        }

        private static string Sha256Hex(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
            }
        }

        [Fact]
        public void CompatibilityProjectLoadsTheActualLibrary()
        {
            Assert.Equal("MdfLibrary.Managed", Assembly.Load("MdfLibrary.Managed").GetName().Name);
        }

        [DataContract]
        private sealed class Manifest
        {
            [DataMember(Name = "schemaVersion", IsRequired = true)]
            public int SchemaVersion { get; set; }

            [DataMember(Name = "fixtures", IsRequired = true)]
            public List<Fixture> Fixtures { get; set; }
        }

        [DataContract]
        private sealed class Fixture
        {
            [DataMember(Name = "id", IsRequired = true)]
            public string Id { get; set; }

            [DataMember(Name = "path", IsRequired = true)]
            public string Path { get; set; }

            [DataMember(Name = "size", IsRequired = true)]
            public long Size { get; set; }

            [DataMember(Name = "sha256", IsRequired = true)]
            public string Sha256 { get; set; }
        }
    }
}
