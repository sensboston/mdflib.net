// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;

namespace MdfLib.PackageAudit
{
    // Never loads or executes the checked assembly. This is independent of the runtime reflection test.
    internal sealed class ApiSurface : ISignatureTypeProvider<string, object>
    {
        internal static string[] Read(MetadataReader reader)
        {
            ApiSurface provider = new ApiSurface();
            List<string> lines = new List<string>();
            foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
            {
                if (!Exported(reader, handle)) { continue; }
                TypeDefinition type = reader.GetTypeDefinition(handle);
                string name = provider.GetTypeFromDefinition(reader, handle, 0);
                // Since 1.0 the MdfLibrary namespace is the whole public surface; the engine is internal.
                if (!name.StartsWith("MdfLibrary.", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Public type outside the MdfLibrary namespace: " + name);
                }
                lines.Add("T|" + name + "|" + provider.EntityName(reader, type.BaseType) + "|" + Flag((type.Attributes & TypeAttributes.Abstract) != 0) + "|" + Flag((type.Attributes & TypeAttributes.Sealed) != 0));
                foreach (InterfaceImplementationHandle contract in type.GetInterfaceImplementations())
                {
                    lines.Add("I|" + name + "|" + provider.EntityName(reader, reader.GetInterfaceImplementation(contract).Interface));
                }
                foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
                {
                    MethodDefinition method = reader.GetMethodDefinition(methodHandle);
                    if (!Visible(method.Attributes)) { continue; }
                    MethodSignature<string> signature = method.DecodeSignature(provider, null);
                    // Fail closed: unused generic parameters and varargs otherwise disappear from this schema.
                    if (signature.GenericParameterCount != 0)
                    {
                        throw new InvalidDataException("Generic public methods require a reviewed signature schema.");
                    }
                    if (signature.Header.CallingConvention != SignatureCallingConvention.Default)
                    {
                        throw new InvalidDataException("Public method calling convention requires a reviewed signature schema.");
                    }
                    Dictionary<int, Parameter> parameters = method.GetParameters().Select(reader.GetParameter).Where(p => p.SequenceNumber != 0).ToDictionary(p => p.SequenceNumber);
                    List<string> descriptions = new List<string>();
                    for (int index = 0; index < signature.ParameterTypes.Length; index++)
                    {
                        Parameter parameter = parameters[index + 1];
                        descriptions.Add(signature.ParameterTypes[index] + " " + reader.GetString(parameter.Name) + "="
                            + ((parameter.Attributes & ParameterAttributes.Optional) != 0 ? Constant(reader, parameter.GetDefaultValue()) : "!"));
                    }
                    lines.Add("M|" + name + "|" + reader.GetString(method.Name) + "|" + signature.ReturnType
                        + "|" + ((int)(method.Attributes & MethodAttributes.MemberAccessMask)).ToString(CultureInfo.InvariantCulture)
                        + "|" + Flag((method.Attributes & MethodAttributes.Static) != 0) + "|" + Flag((method.Attributes & MethodAttributes.Virtual) != 0)
                        + "|" + Flag((method.Attributes & MethodAttributes.Final) != 0) + "|" + string.Join(";", descriptions));
                }
                foreach (PropertyDefinitionHandle propertyHandle in type.GetProperties())
                {
                    PropertyDefinition property = reader.GetPropertyDefinition(propertyHandle);
                    PropertyAccessors accessors = property.GetAccessors();
                    string visible = (MethodVisible(reader, accessors.Getter) ? "get" : "") + (MethodVisible(reader, accessors.Setter) ? "set" : "");
                    if (visible.Length != 0)
                    {
                        lines.Add("P|" + name + "|" + reader.GetString(property.Name) + "|" + property.DecodeSignature(provider, null).ReturnType + "|" + visible);
                    }
                }
                foreach (FieldDefinitionHandle fieldHandle in type.GetFields())
                {
                    FieldDefinition field = reader.GetFieldDefinition(fieldHandle);
                    int access = (int)(field.Attributes & FieldAttributes.FieldAccessMask);
                    if (access == (int)FieldAttributes.Public || access == (int)FieldAttributes.Family || access == (int)FieldAttributes.FamORAssem)
                    {
                        lines.Add("F|" + name + "|" + reader.GetString(field.Name) + "|" + field.DecodeSignature(provider, null) + "|" + access.ToString(CultureInfo.InvariantCulture)
                            + "|" + Flag((field.Attributes & FieldAttributes.Static) != 0) + "|" + ((field.Attributes & FieldAttributes.Literal) != 0 ? Constant(reader, field.GetDefaultValue()) : "!"));
                    }
                }
            }
            return lines.OrderBy(line => line, StringComparer.Ordinal).ToArray();
        }

        internal static void Validate(string[] actual, string[] expected)
        {
            if (!actual.SequenceEqual(expected))
            {
                throw new InvalidDataException("Public API mismatch. Unexpected: " + string.Join(" / ", actual.Except(expected).Take(4))
                    + "; missing: " + string.Join(" / ", expected.Except(actual).Take(4)));
            }
        }

        internal static string[] ReadBaseline(string path)
        {
            return File.ReadAllLines(path).Where(line => line.Length != 0 && !line.StartsWith("#", StringComparison.Ordinal)).OrderBy(line => line, StringComparer.Ordinal).ToArray();
        }

        private static bool Exported(MetadataReader reader, TypeDefinitionHandle handle)
        {
            TypeDefinition type = reader.GetTypeDefinition(handle);
            TypeAttributes access = type.Attributes & TypeAttributes.VisibilityMask;
            if (access == TypeAttributes.Public) { return true; }
            return (access == TypeAttributes.NestedPublic || access == TypeAttributes.NestedFamily || access == TypeAttributes.NestedFamORAssem)
                && !type.GetDeclaringType().IsNil && Exported(reader, type.GetDeclaringType());
        }
        private static bool Visible(MethodAttributes attributes)
        {
            MethodAttributes access = attributes & MethodAttributes.MemberAccessMask;
            return access == MethodAttributes.Public || access == MethodAttributes.Family || access == MethodAttributes.FamORAssem;
        }
        private static bool MethodVisible(MetadataReader reader, MethodDefinitionHandle handle) { return !handle.IsNil && Visible(reader.GetMethodDefinition(handle).Attributes); }
        private static string Flag(bool value) { return value ? "true" : "false"; }
        private static string Constant(MetadataReader reader, ConstantHandle handle)
        {
            if (handle.IsNil) { return "null"; }
            System.Reflection.Metadata.Constant constant = reader.GetConstant(handle);
            BlobReader blob = reader.GetBlobReader(constant.Value);
            switch (constant.TypeCode)
            {
                case ConstantTypeCode.NullReference: return "null";
                case ConstantTypeCode.Boolean: return Flag(blob.ReadBoolean());
                case ConstantTypeCode.Int32: return blob.ReadInt32().ToString(CultureInfo.InvariantCulture);
                case ConstantTypeCode.SByte: return blob.ReadSByte().ToString(CultureInfo.InvariantCulture);
                case ConstantTypeCode.Byte: return blob.ReadByte().ToString(CultureInfo.InvariantCulture);
                case ConstantTypeCode.Int16: return blob.ReadInt16().ToString(CultureInfo.InvariantCulture);
                case ConstantTypeCode.UInt16: return blob.ReadUInt16().ToString(CultureInfo.InvariantCulture);
                case ConstantTypeCode.UInt32: return blob.ReadUInt32().ToString(CultureInfo.InvariantCulture);
                case ConstantTypeCode.Int64: return blob.ReadInt64().ToString(CultureInfo.InvariantCulture);
                case ConstantTypeCode.UInt64: return blob.ReadUInt64().ToString(CultureInfo.InvariantCulture);
                case ConstantTypeCode.String: return blob.ReadUTF16(blob.Length);
                default: throw new InvalidDataException("An API constant type needs explicit baseline support: " + constant.TypeCode);
            }
        }
        private string EntityName(MetadataReader reader, EntityHandle handle)
        {
            if (handle.IsNil) { return "-"; }
            switch (handle.Kind)
            {
                case HandleKind.TypeDefinition: return GetTypeFromDefinition(reader, (TypeDefinitionHandle)handle, 0);
                case HandleKind.TypeReference: return GetTypeFromReference(reader, (TypeReferenceHandle)handle, 0);
                case HandleKind.TypeSpecification: return GetTypeFromSpecification(reader, null, (TypeSpecificationHandle)handle, 0);
                default: throw new InvalidDataException("Unknown API type handle: " + handle.Kind);
            }
        }
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            TypeDefinition type = reader.GetTypeDefinition(handle);
            return type.GetDeclaringType().IsNil ? Qualified(reader.GetString(type.Namespace), reader.GetString(type.Name))
                : GetTypeFromDefinition(reader, type.GetDeclaringType(), rawTypeKind) + "+" + reader.GetString(type.Name);
        }
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            TypeReference type = reader.GetTypeReference(handle);
            return type.ResolutionScope.Kind == HandleKind.TypeReference
                ? GetTypeFromReference(reader, (TypeReferenceHandle)type.ResolutionScope, rawTypeKind) + "+" + reader.GetString(type.Name)
                : Qualified(reader.GetString(type.Namespace), reader.GetString(type.Name));
        }
        private static string Qualified(string space, string name) { return space.Length == 0 ? name : space + "." + name; }
        public string GetTypeFromSpecification(MetadataReader reader, object context, TypeSpecificationHandle handle, byte rawTypeKind) { return reader.GetTypeSpecification(handle).DecodeSignature(this, context); }
        public string GetPrimitiveType(PrimitiveTypeCode code)
        {
            switch (code)
            {
                case PrimitiveTypeCode.Boolean: return "System.Boolean";
                case PrimitiveTypeCode.Byte: return "System.Byte";
                case PrimitiveTypeCode.SByte: return "System.SByte";
                case PrimitiveTypeCode.Char: return "System.Char";
                case PrimitiveTypeCode.Int16: return "System.Int16";
                case PrimitiveTypeCode.UInt16: return "System.UInt16";
                case PrimitiveTypeCode.Int32: return "System.Int32";
                case PrimitiveTypeCode.UInt32: return "System.UInt32";
                case PrimitiveTypeCode.Int64: return "System.Int64";
                case PrimitiveTypeCode.UInt64: return "System.UInt64";
                case PrimitiveTypeCode.Single: return "System.Single";
                case PrimitiveTypeCode.Double: return "System.Double";
                case PrimitiveTypeCode.String: return "System.String";
                case PrimitiveTypeCode.Object: return "System.Object";
                case PrimitiveTypeCode.Void: return "System.Void";
                case PrimitiveTypeCode.IntPtr: return "System.IntPtr";
                case PrimitiveTypeCode.UIntPtr: return "System.UIntPtr";
                default: throw new InvalidDataException("Unsupported public primitive: " + code);
            }
        }
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> args) { return genericType + "[" + string.Join(",", args) + "]"; }
        public string GetSZArrayType(string elementType) { return elementType + "[]"; }
        public string GetArrayType(string elementType, ArrayShape shape) { return elementType + "[" + new string(',', shape.Rank - 1) + "]"; }
        public string GetByReferenceType(string elementType) { return elementType + "&"; }
        public string GetPointerType(string elementType) { return elementType + "*"; }
        public string GetPinnedType(string elementType) { return "pinned " + elementType; }
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) { return unmodifiedType + (isRequired ? " modreq(" : " modopt(") + modifier + ")"; }
        public string GetGenericMethodParameter(object context, int index) { return "!!" + index.ToString(CultureInfo.InvariantCulture); }
        public string GetGenericTypeParameter(object context, int index) { return "!" + index.ToString(CultureInfo.InvariantCulture); }
        public string GetFunctionPointerType(MethodSignature<string> signature) { throw new InvalidDataException("Function pointers are not a reviewed public API."); }
    }
}
