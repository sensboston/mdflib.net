// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

namespace MdfLib.PackageAudit
{
    internal static class Program
    {
        private static readonly string[] Frameworks = { "net48", "net8.0", "net10.0" };
        private static readonly HashSet<string> ForbiddenReferences = new HashSet<string>(StringComparer.Ordinal)
        {
            "System.Runtime.InteropServices.NativeLibrary",
            "System.Runtime.InteropServices.ComImportAttribute",
            "System.Runtime.InteropServices.DllImportAttribute",
            "System.Runtime.InteropServices.LibraryImportAttribute",
            "System.Diagnostics.Process",
            "System.IO.Compression.DeflateStream",
            "System.IO.Compression.ZLibStream",
            "System.IO.Compression.GZipStream",
            "System.Security.Cryptography.MD5",
            "System.Security.Cryptography.HashAlgorithm"
        };

        private static int Main(string[] args)
        {
            try
            {
                if (args.Length != 4)
                {
                    Console.Error.WriteLine("Usage: MdfLib.PackageAudit <package.nupkg> <project.assets.json> <repository-root> <report.json>");
                    return 2;
                }

                byte[] package = File.ReadAllBytes(args[0]);
                List<string> checks = AuditPackage(package, Path.GetFullPath(args[2]));
                AuditDependencyGraph(File.ReadAllText(args[1]));
                checks.Add("All restored library dependencies are private build-only reference packs; no runtime closure.");
                RunNegativeSelfTests(package, Path.GetFullPath(args[2]));
                checks.Add("Negative self-tests passed: native package asset, non-IL-only PE, native image, P/Invoke/ModuleRef metadata, runtime dependency, unexpected/missing API types/members/defaults, generic arity and varargs metadata; all ten forbidden loader/interop/process/BCL-compressor/BCL-hash TypeRefs and Reflection.Emit prefix rejected, harmless TypeRef positive control passed.");
                string report = JsonSerializer.Serialize(new
                {
                    commit = Environment.GetEnvironmentVariable("GITHUB_SHA"),
                    package = Path.GetFileName(args[0]),
                    sha256 = Hash(package),
                    frameworks = Frameworks,
                    checks = checks,
                    publicApi = "The MdfLibrary namespace is the whole public surface of the package: the managed facade of the MdfLibrary wrapper (reader and writer, the file model with channels, conversions, sources, events, attachments and file history, CAN messages and timestamps). The MdfLib engine is internal. Each shipped assembly matches api/public-api.txt",
                    success = true
                }, new JsonSerializerOptions { WriteIndented = true });
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[3])));
                File.WriteAllText(args[3], report + Environment.NewLine);
                Console.WriteLine(report);
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("Package audit FAILED: " + error);
                return 1;
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidDataException(message);
            }
        }

        private static List<string> AuditPackage(byte[] bytes, string root)
        {
            List<string> checks = new List<string>();
            using (ZipArchive zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
            {
                string[] names = zip.Entries.Select(entry => entry.FullName).ToArray();
                Require(names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Length, "Duplicate package paths.");
                foreach (string name in names)
                {
                    Require(!name.StartsWith("/", StringComparison.Ordinal) && !name.Contains("\\") && !name.Split('/').Contains(".."), "Unsafe package path: " + name);
                    string lower = name.ToLowerInvariant();
                    Require(!lower.StartsWith("runtimes/", StringComparison.Ordinal) && !lower.EndsWith(".so", StringComparison.Ordinal)
                        && !lower.Contains(".so.") && !lower.EndsWith(".dylib", StringComparison.Ordinal) && !lower.EndsWith(".exe", StringComparison.Ordinal), "Native/runtime asset: " + name);
                    if (lower.EndsWith(".dll", StringComparison.Ordinal))
                    {
                        Require(Frameworks.Any(tfm => name == "lib/" + tfm + "/MdfLibrary.Managed.dll"), "Unexpected assembly: " + name);
                    }
                    // No hidden targets, scripts, analyzers or executable payloads in the shipping package.
                    Require(name == "[Content_Types].xml" || name == "_rels/.rels" || name.EndsWith(".nuspec", StringComparison.Ordinal)
                        || name.EndsWith(".psmdcp", StringComparison.Ordinal) || name == "LICENSE" || name == "THIRD-PARTY-NOTICES.md" || name == "icon.png"
                        || name == "README.md" || Frameworks.Any(tfm => name == "lib/" + tfm + "/MdfLibrary.Managed.dll" || name == "lib/" + tfm + "/MdfLibrary.Managed.xml"),
                        "Unreviewed package content: " + name);
                }

                ZipArchiveEntry nuspec = zip.Entries.Single(entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
                XDocument spec;
                using (Stream stream = nuspec.Open())
                {
                    spec = XDocument.Load(stream);
                }
                Require(!spec.Descendants().Any(element => element.Name.LocalName == "dependency"), "Shipping dependency requires a reviewed closure audit.");
                Require(spec.Descendants().Single(element => element.Name.LocalName == "id").Value == "MdfLibrary.Managed", "Unexpected package ID.");
                foreach (string name in new[] { "LICENSE", "THIRD-PARTY-NOTICES.md", "README.md" })
                {
                    Require(ReadEntry(zip, name).SequenceEqual(File.ReadAllBytes(Path.Combine(root, name))), "Missing or incorrect package document: " + name);
                }
                foreach (string tfm in Frameworks)
                {
                    string name = "lib/" + tfm + "/MdfLibrary.Managed.dll";
                    AuditAssembly(ReadEntry(zip, name), ApiSurface.ReadBaseline(Path.Combine(root, "api", "public-api.txt")));
                    byte[] xml = ReadEntry(zip, "lib/" + tfm + "/MdfLibrary.Managed.xml");
                    using (MemoryStream stream = new MemoryStream(xml))
                    {
                        Require(XDocument.Load(stream).Root.Name.LocalName == "doc", "Invalid XML documentation: " + tfm);
                    }
                    checks.Add(tfm + ": IL-only, no native/mixed-mode/interop, reviewed API baseline, XML documentation present; SHA-256 " + Hash(ReadEntry(zip, name)));
                }
            }
            checks.Add("Exact three-TFM package content, no shipping NuGet dependencies, LICENSE/notices/README match repository.");
            return checks;
        }

        private static byte[] ReadEntry(ZipArchive zip, string name)
        {
            ZipArchiveEntry entry = zip.GetEntry(name);
            Require(entry != null, "Missing package entry: " + name);
            using (Stream source = entry.Open())
            using (MemoryStream destination = new MemoryStream())
            {
                source.CopyTo(destination);
                return destination.ToArray();
            }
        }

        private static void AuditAssembly(byte[] bytes, string[] expectedApi)
        {
            using (PEReader pe = new PEReader(new MemoryStream(bytes)))
            {
                Require(pe.HasMetadata && pe.PEHeaders.CorHeader != null, "Not a CLI assembly.");
                CorHeader header = pe.PEHeaders.CorHeader;
                Require((header.Flags & CorFlags.ILOnly) != 0, "Assembly is not IL-only.");
                Require((header.Flags & CorFlags.NativeEntryPoint) == 0 && header.ManagedNativeHeaderDirectory.Size == 0, "Native entry point or managed native header.");
                MetadataReader metadata = pe.GetMetadataReader();
                Require(metadata.IsAssembly, "Not an assembly manifest.");
                Require(metadata.GetTableRowCount(TableIndex.ImplMap) == 0 && metadata.GetTableRowCount(TableIndex.ModuleRef) == 0, "Interop metadata: ImplMap/ModuleRef.");
                foreach (MethodDefinitionHandle handle in metadata.MethodDefinitions)
                {
                    Require((metadata.GetMethodDefinition(handle).Attributes & MethodAttributes.PinvokeImpl) == 0, "P/Invoke method.");
                }
                foreach (TypeDefinitionHandle handle in metadata.TypeDefinitions)
                {
                    TypeDefinition type = metadata.GetTypeDefinition(handle);
                    Require((type.Attributes & TypeAttributes.Import) == 0, "COM import type.");
                }
                ApiSurface.Validate(ApiSurface.Read(metadata), expectedApi);
                Require(metadata.GetTableRowCount(TableIndex.ExportedType) == 0, "Unexpected exported/forwarded type.");
                foreach (TypeReferenceHandle handle in metadata.TypeReferences)
                {
                    TypeReference type = metadata.GetTypeReference(handle);
                    string fullName = metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name);
                    Require(!ForbiddenReferences.Contains(fullName) && !fullName.StartsWith("System.Reflection.Emit.", StringComparison.Ordinal), "Forbidden loader/interop/compressor/hash reference: " + fullName);
                }
            }
        }

        private static void AuditDependencyGraph(string json)
        {
            using (JsonDocument document = JsonDocument.Parse(json))
            {
                JsonElement root = document.RootElement;
                JsonElement frameworks = root.GetProperty("project").GetProperty("frameworks");
                Require(frameworks.EnumerateObject().Count() == 3, "Unexpected restored framework count.");
                foreach (string tfm in Frameworks)
                {
                    Require(frameworks.TryGetProperty(tfm, out JsonElement framework)
                        || (tfm == "net48" && frameworks.TryGetProperty(".NETFramework,Version=v4.8", out framework)), "Missing restored target: " + tfm);
                    if (framework.TryGetProperty("dependencies", out JsonElement dependencies))
                    {
                        foreach (JsonProperty dependency in dependencies.EnumerateObject())
                        {
                            Require(tfm == "net48" && dependency.Name == "Microsoft.NETFramework.ReferenceAssemblies", "Unreviewed direct dependency: " + dependency.Name);
                            Require(dependency.Value.GetProperty("suppressParent").GetString() == "All", "Reference pack must be PrivateAssets=all.");
                        }
                    }
                }
                foreach (JsonProperty library in root.GetProperty("libraries").EnumerateObject())
                {
                    Require(library.Name == "Microsoft.NETFramework.ReferenceAssemblies/1.0.3"
                        || library.Name == "Microsoft.NETFramework.ReferenceAssemblies.net48/1.0.3", "Unreviewed restored package: " + library.Name);
                }
                foreach (JsonProperty target in root.GetProperty("targets").EnumerateObject())
                {
                    foreach (JsonProperty library in target.Value.EnumerateObject())
                    {
                        foreach (string kind in new[] { "runtime", "native", "runtimeTargets" })
                        {
                            if (library.Value.TryGetProperty(kind, out JsonElement assets))
                            {
                                Require(assets.EnumerateObject().All(asset => asset.Name.EndsWith("/_._", StringComparison.Ordinal)), "Runtime/native asset in library restore graph: " + library.Name);
                            }
                        }
                    }
                }
            }
        }

        private static void ExpectRejected(string name, Action action, string reason)
        {
            try
            {
                action();
            }
            catch (InvalidDataException error)
            {
                Require(error.Message.Contains(reason), "Self-test rejected for the wrong reason: " + name + ": " + error.Message);
                Console.WriteLine("Self-test passed: " + name);
                return;
            }
            throw new InvalidOperationException("Audit self-test failed to detect " + name);
        }

        private static void RunNegativeSelfTests(byte[] package, string root)
        {
            byte[] assembly;
            using (ZipArchive zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read))
            {
                assembly = ReadEntry(zip, "lib/net10.0/MdfLibrary.Managed.dll");
            }
            byte[] mixed = (byte[])assembly.Clone();
            using (PEReader pe = new PEReader(new MemoryStream(assembly)))
            {
                // IMAGE_COR20_HEADER.Flags is at byte offset 16 in the CLI header.
                mixed[pe.PEHeaders.CorHeaderStartOffset + 16] &= 0xfe;
            }
            string[] expectedApi = ApiSurface.ReadBaseline(Path.Combine(root, "api", "public-api.txt"));
            // Exercise the actual binary TypeRef gate, not a source grep or execution of forbidden APIs.
            AuditAssembly(CreateTypeReferenceFixture("System.IO.Stream"), new string[0]);
            string[] rejectedReferences =
            {
                "System.Runtime.InteropServices.NativeLibrary",
                "System.Runtime.InteropServices.ComImportAttribute",
                "System.Runtime.InteropServices.DllImportAttribute",
                "System.Runtime.InteropServices.LibraryImportAttribute",
                "System.Diagnostics.Process",
                "System.IO.Compression.DeflateStream",
                "System.IO.Compression.ZLibStream",
                "System.IO.Compression.GZipStream",
                "System.Security.Cryptography.MD5",
                "System.Security.Cryptography.HashAlgorithm",
                "System.Reflection.Emit.DynamicMethod"
            };
            // Independent literal inputs: deleting a policy entry must not also delete its negative test.
            // The exact-name set holds the ten literal entries; System.Reflection.Emit.* is a prefix rule, tested below.
            string[] exactReferences = rejectedReferences.Where(reference => !reference.StartsWith("System.Reflection.Emit.", StringComparison.Ordinal)).ToArray();
            Require(ForbiddenReferences.SetEquals(exactReferences), "Forbidden-reference self-tests must cover the exact reviewed gate.");
            foreach (string reference in rejectedReferences)
            {
                ExpectRejected("forbidden TypeRef " + reference,
                    () => AuditAssembly(CreateTypeReferenceFixture(reference), new string[0]),
                    "Forbidden loader/interop/compressor/hash reference: " + reference);
            }
            ExpectRejected("forbidden TypeRef prefix System.Reflection.Emit.", () => AuditAssembly(CreateTypeReferenceFixture("System.Reflection.Emit.DynamicMethod"), new string[0]), "Forbidden loader/interop/compressor/hash reference: System.Reflection.Emit.DynamicMethod");
            ExpectRejected("non-IL-only PE", () => AuditAssembly(mixed, expectedApi), "not IL-only");
            ExpectRejected("P/Invoke/ModuleRef metadata", () => AuditAssembly(CreateInteropFixture(), expectedApi), "Interop metadata");
            // A parseable native PE has its CLI directory cleared, rather than just malformed bytes.
            byte[] native = (byte[])assembly.Clone();
            using (PEReader pe = new PEReader(new MemoryStream(assembly)))
            {
                int optionalHeader = BitConverter.ToInt32(native, 0x3c) + 24;
                int directoryStart = optionalHeader + (pe.PEHeaders.PEHeader.Magic == PEMagic.PE32 ? 96 : 112);
                Array.Clear(native, directoryStart + 14 * 8, 8);
            }
            ExpectRejected("native PE", () => AuditAssembly(native, expectedApi), "Not a CLI assembly");
            using (MemoryStream stream = new MemoryStream())
            {
                stream.Write(package, 0, package.Length);
                using (ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Update, true))
                using (Stream entry = zip.CreateEntry("runtimes/linux-x64/native/libbad.so").Open())
                {
                    entry.WriteByte(0);
                }
                ExpectRejected("native package asset", () => AuditPackage(stream.ToArray(), root), "Native/runtime asset");
            }
            using (PEReader pe = new PEReader(new MemoryStream(assembly)))
            {
                string[] actual = ApiSurface.Read(pe.GetMetadataReader());
                ExpectRejected("unregistered public type", () => ApiSurface.Validate(actual, expectedApi.Where(line => !line.StartsWith("T|MdfLibrary.MdfReader|", StringComparison.Ordinal)).ToArray()), "Public API mismatch");
                ExpectRejected("unregistered public member", () => ApiSurface.Validate(actual, expectedApi.Where(line => !line.StartsWith("M|MdfLibrary.MdfReader|Open|", StringComparison.Ordinal)).ToArray()), "Public API mismatch");
                ExpectRejected("missing API member", () => ApiSurface.Validate(actual, expectedApi.Concat(new[] { "M|MdfLibrary.MdfReader|Fake" }).OrderBy(line => line, StringComparer.Ordinal).ToArray()), "Public API mismatch");
                ExpectRejected("changed parameter name", () => ApiSurface.Validate(actual, expectedApi.Select(line => line.Replace("System.String filename=!", "System.String fileName=!")).OrderBy(line => line, StringComparer.Ordinal).ToArray()), "Public API mismatch");
            }
            string[] signatureApi =
            {
                "M|MdfLibrary.ApiProbe|Probe|System.Void|6|true|false|false|",
                "T|MdfLibrary.ApiProbe|-|true|true"
            };
            AuditAssembly(CreateSignatureFixture(false, false), signatureApi);
            ExpectRejected("unused generic public arity", () => AuditAssembly(CreateSignatureFixture(true, false), signatureApi), "Generic public methods");
            ExpectRejected("varargs public signature", () => AuditAssembly(CreateSignatureFixture(false, true), signatureApi), "calling convention");
            const string dependency = "{\"project\":{\"frameworks\":{\"net48\":{},\"net8.0\":{\"dependencies\":{\"Unreviewed.Native\":{}}},\"net10.0\":{}}}}";
            ExpectRejected("unreviewed runtime dependency", () => AuditDependencyGraph(dependency), "Unreviewed direct dependency");
        }

        private static byte[] CreateTypeReferenceFixture(string fullName)
        {
            // Parseable IL-only metadata with an unused TypeRef and no public API. Never load this image.
            MetadataBuilder metadata = new MetadataBuilder();
            metadata.AddModule(0, metadata.GetOrAddString("ReferenceProbe"), metadata.GetOrAddGuid(new Guid("9ef99eb6-cd2d-4d01-b1e3-86f2545aadfd")), default(GuidHandle), default(GuidHandle));
            metadata.AddAssembly(metadata.GetOrAddString("ReferenceProbe"), new Version(1, 0), default(StringHandle), default(BlobHandle), (AssemblyFlags)0, AssemblyHashAlgorithm.None);
            metadata.AddTypeDefinition(TypeAttributes.NotPublic, default(StringHandle), metadata.GetOrAddString("<Module>"), default(EntityHandle), MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
            AssemblyReferenceHandle scope = metadata.AddAssemblyReference(metadata.GetOrAddString("System.Runtime"), new Version(4, 0),
                default(StringHandle), default(BlobHandle), (AssemblyFlags)0, default(BlobHandle));
            int separator = fullName.LastIndexOf('.');
            metadata.AddTypeReference(scope, metadata.GetOrAddString(fullName.Substring(0, separator)), metadata.GetOrAddString(fullName.Substring(separator + 1)));
            BlobBuilder image = new BlobBuilder();
            new ManagedPEBuilder(PEHeaderBuilder.CreateLibraryHeader(), new MetadataRootBuilder(metadata), new BlobBuilder()).Serialize(image);
            return image.ToArray();
        }

        private static byte[] CreateSignatureFixture(bool generic, bool varargs)
        {
            // Metadata-only synthetic assembly: never loaded or invoked. The baseline must not hide arity/convention.
            MetadataBuilder metadata = new MetadataBuilder();
            metadata.AddModule(0, metadata.GetOrAddString("ApiProbe"), metadata.GetOrAddGuid(new Guid("e2be5032-82cb-47f7-b072-748504a4ade3")), default(GuidHandle), default(GuidHandle));
            metadata.AddAssembly(metadata.GetOrAddString("ApiProbe"), new Version(1, 0), default(StringHandle), default(BlobHandle), (AssemblyFlags)0, AssemblyHashAlgorithm.None);
            metadata.AddTypeDefinition(TypeAttributes.NotPublic, default(StringHandle), metadata.GetOrAddString("<Module>"), default(EntityHandle), MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
            metadata.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, metadata.GetOrAddString("MdfLibrary"), metadata.GetOrAddString("ApiProbe"), default(EntityHandle), MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
            BlobBuilder signature = new BlobBuilder();
            new BlobEncoder(signature).MethodSignature(varargs ? SignatureCallingConvention.VarArgs : SignatureCallingConvention.Default, generic ? 1 : 0)
                .Parameters(0, returnType => returnType.Void(), parameters => { });
            BlobBuilder il = new BlobBuilder();
            InstructionEncoder instructions = new InstructionEncoder(new BlobBuilder());
            instructions.OpCode(ILOpCode.Ret);
            int bodyOffset = new MethodBodyStreamEncoder(il).AddMethodBody(instructions);
            MethodDefinitionHandle method = metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static,
                MethodImplAttributes.IL, metadata.GetOrAddString("Probe"), metadata.GetOrAddBlob(signature), bodyOffset, MetadataTokens.ParameterHandle(1));
            if (generic)
            {
                metadata.AddGenericParameter(method, GenericParameterAttributes.None, metadata.GetOrAddString("T"), 0);
            }
            BlobBuilder image = new BlobBuilder();
            new ManagedPEBuilder(PEHeaderBuilder.CreateLibraryHeader(), new MetadataRootBuilder(metadata), il).Serialize(image);
            return image.ToArray();
        }

        private static byte[] CreateInteropFixture()
        {
            // Construct metadata only: never compile or call a real native entry point.
            MetadataBuilder metadata = new MetadataBuilder();
            metadata.AddModule(0, metadata.GetOrAddString("InteropFixture"), metadata.GetOrAddGuid(new Guid("d68609ed-ad57-48ab-8510-d7b280009f69")), default(GuidHandle), default(GuidHandle));
            metadata.AddAssembly(metadata.GetOrAddString("InteropFixture"), new Version(1, 0), default(StringHandle), default(BlobHandle), (AssemblyFlags)0, AssemblyHashAlgorithm.None);
            metadata.AddTypeDefinition(TypeAttributes.NotPublic, default(StringHandle), metadata.GetOrAddString("<Module>"), default(EntityHandle), MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
            BlobBuilder signature = new BlobBuilder();
            new BlobEncoder(signature).MethodSignature().Parameters(0, returnType => returnType.Void(), parameters => { });
            MethodDefinitionHandle method = metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.PinvokeImpl,
                MethodImplAttributes.PreserveSig, metadata.GetOrAddString("Forbidden"), metadata.GetOrAddBlob(signature), -1, MetadataTokens.ParameterHandle(1));
            metadata.AddMethodImport(method, MethodImportAttributes.CallingConventionCDecl, metadata.GetOrAddString("NeverCalled"), metadata.AddModuleReference(metadata.GetOrAddString("nonexistent-native-library")));
            BlobBuilder image = new BlobBuilder();
            new ManagedPEBuilder(PEHeaderBuilder.CreateLibraryHeader(), new MetadataRootBuilder(metadata), new BlobBuilder()).Serialize(image);
            return image.ToArray();
        }

        private static string Hash(byte[] bytes)
        {
            using (SHA256 algorithm = SHA256.Create())
            {
                return BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }
    }
}
