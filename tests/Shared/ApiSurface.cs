// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace MdfLib.TestSupport
{
    // Runtime reflection implementation, independent of the metadata-only package auditor.
    internal static class ApiSurface
    {
        // api/public-api.txt is the reviewed surface of the package: since 1.0 that is the MdfLibrary namespace only
        // (the engine is internal), and the facade also has its own baseline (api/mdflibrary-api-baseline.txt).
        internal static string[] Read(Assembly assembly)
        {
            return Read(assembly.GetExportedTypes());
        }

        internal static string[] Read(IEnumerable<Type> types)
        {
            List<string> lines = new List<string>();
            foreach (Type type in types)
            {
                string name = TypeName(type);
                lines.Add("T|" + name + "|" + TypeName(type.BaseType) + "|" + Flag(type.IsAbstract) + "|" + Flag(type.IsSealed));
                Type[] inherited = type.BaseType == null ? Type.EmptyTypes : type.BaseType.GetInterfaces();
                foreach (Type contract in type.GetInterfaces().Except(inherited))
                {
                    lines.Add("I|" + name + "|" + TypeName(contract));
                }
                const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                foreach (MethodBase method in type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags)))
                {
                    if (!Visible(method)) { continue; }
                    MethodInfo info = method as MethodInfo;
                    if (info != null && info.IsGenericMethod)
                    {
                        throw new InvalidDataException("Generic public methods require a reviewed signature schema.");
                    }
                    if ((method.CallingConvention & CallingConventions.VarArgs) != 0)
                    {
                        throw new InvalidDataException("Public method calling convention requires a reviewed signature schema.");
                    }
                    string parameters = string.Join(";", method.GetParameters().Select(p => TypeName(p.ParameterType) + " " + p.Name + "=" + (p.IsOptional ? Constant(p.RawDefaultValue) : "!")));
                    lines.Add("M|" + name + "|" + method.Name + "|" + (info == null ? "System.Void" : TypeName(info.ReturnType))
                        + "|" + Access(method) + "|" + Flag(method.IsStatic) + "|" + Flag(method.IsVirtual) + "|" + Flag(method.IsFinal) + "|" + parameters);
                }
                foreach (PropertyInfo property in type.GetProperties(flags))
                {
                    string accessors = (Visible(property.GetGetMethod(true)) ? "get" : "") + (Visible(property.GetSetMethod(true)) ? "set" : "");
                    if (accessors.Length != 0)
                    {
                        lines.Add("P|" + name + "|" + property.Name + "|" + TypeName(property.PropertyType) + "|" + accessors);
                    }
                }
                foreach (FieldInfo field in type.GetFields(flags))
                {
                    if (field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly)
                    {
                        lines.Add("F|" + name + "|" + field.Name + "|" + TypeName(field.FieldType) + "|" + (int)(field.Attributes & FieldAttributes.FieldAccessMask)
                            + "|" + Flag(field.IsStatic) + "|" + (field.IsLiteral ? Constant(field.GetRawConstantValue()) : "!"));
                    }
                }
            }
            return lines.OrderBy(line => line, StringComparer.Ordinal).ToArray();
        }

        private static bool Visible(MethodBase method) { return method != null && (method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly); }
        private static string Access(MethodBase method) { return ((int)(method.Attributes & MethodAttributes.MemberAccessMask)).ToString(CultureInfo.InvariantCulture); }
        private static string Flag(bool value) { return value ? "true" : "false"; }
        private static string Constant(object value) { return value == null ? "null" : value is bool ? Flag((bool)value) : Convert.ToString(value, CultureInfo.InvariantCulture); }
        private static string TypeName(Type type)
        {
            if (type == null) { return "-"; }
            if (type.IsGenericType)
            {
                return type.GetGenericTypeDefinition().FullName + "[" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + "]";
            }
            return type.FullName;
        }
    }
}
