// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

namespace MdfLib.TestSupport
{
    internal static class FixtureCorpus
    {
        internal static List<Entry> Entries()
        {
            using (Stream stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "manifest.json")))
            {
                Manifest manifest = (Manifest)new DataContractJsonSerializer(typeof(Manifest)).ReadObject(stream);
                if (manifest.SchemaVersion != 1 || manifest.Fixtures == null) { throw new InvalidDataException("Invalid corpus manifest."); }
                return manifest.Fixtures;
            }
        }

        internal static byte[] Read(string id)
        {
            Entry entry = Entries().Single(item => item.Id == id);
            string relative = entry.Path;
            if (string.IsNullOrWhiteSpace(relative) || System.IO.Path.IsPathRooted(relative) || relative.Contains("\\")
                || relative.Contains(":") || relative.Split('/').Any(part => part == ".." || part == "." || part.Length == 0))
            {
                throw new InvalidDataException("Invalid corpus path.");
            }
            byte[] bytes = File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            using (SHA256 hash = SHA256.Create())
            {
                string digest = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
                if (entry.Size != bytes.LongLength || entry.Sha256 != digest) { throw new InvalidDataException("Corpus checksum/size mismatch: " + id); }
            }
            return bytes;
        }

        [DataContract]
        private sealed class Manifest
        {
            [DataMember(Name = "schemaVersion", IsRequired = true)] public int SchemaVersion { get; set; }
            [DataMember(Name = "fixtures", IsRequired = true)] public List<Entry> Fixtures { get; set; }
        }

        [DataContract]
        internal sealed class Entry
        {
            [DataMember(Name = "id", IsRequired = true)] public string Id { get; set; }
            [DataMember(Name = "path", IsRequired = true)] public string Path { get; set; }
            [DataMember(Name = "size", IsRequired = true)] public long Size { get; set; }
            [DataMember(Name = "sha256", IsRequired = true)] public string Sha256 { get; set; }
            [DataMember(Name = "mdfVersion", IsRequired = true)] public string MdfVersion { get; set; }
        }
    }
}
