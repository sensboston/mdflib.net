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
    // Canonical projection of the MdfLibrary facade surface as the assembly really exposes it.
    // The line grammar is the one tools/MdfLib.Ci (facade-api) writes into
    // api/mdflibrary-api-baseline.txt, so both sides can be compared as plain strings.
    internal static class FacadeApi
    {
        private const string FacadeNamespace = "MdfLibrary";

        internal static string[] Read(Assembly library)
        {
            List<string> lines = new List<string>();
            foreach (Type type in library.GetExportedTypes().Where(item => item.Namespace == FacadeNamespace).OrderBy(item => item.FullName, StringComparer.Ordinal))
            {
                ReadType(lines, type);
            }
            return lines.OrderBy(line => line, StringComparer.Ordinal).ToArray();
        }

        internal static string[] ReadBaseline(string path)
        {
            return File.ReadAllLines(path)
                .Where(line => line.Length != 0 && !line.StartsWith("#", StringComparison.Ordinal))
                .OrderBy(line => line, StringComparer.Ordinal).ToArray();
        }

        private static void ReadType(List<string> lines, Type type)
        {
            string name = type.FullName;
            const BindingFlags declared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public;
            if (type.IsEnum)
            {
                lines.Add("T|" + name + "|enum|" + TypeName(Enum.GetUnderlyingType(type)));
                foreach (FieldInfo field in type.GetFields(declared))
                {
                    if (field.IsLiteral)
                    {
                        lines.Add("V|" + name + "|" + field.Name + "|" + Value(field.GetRawConstantValue()));
                    }
                }
                return;
            }
            if (typeof(Delegate).IsAssignableFrom(type))
            {
                MethodInfo invoke = type.GetMethod("Invoke", BindingFlags.Instance | BindingFlags.Public);
                if (invoke == null)
                {
                    throw new InvalidDataException("Facade delegate " + name + " has no Invoke method.");
                }
                lines.Add("T|" + name + "|delegate|-");
                lines.Add("G|" + name + "|" + TypeName(invoke.ReturnType) + "|" + Parameters(invoke));
                return;
            }
            lines.Add("T|" + name + "|" + Kind(type) + "|" + BaseType(type));
            foreach (ConstructorInfo constructor in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public))
            {
                lines.Add("C|" + name + "|" + Parameters(constructor));
            }
            foreach (MethodInfo method in type.GetMethods(declared))
            {
                if (method.IsSpecialName)
                {
                    continue;
                }
                if (method.IsGenericMethod || (method.CallingConvention & CallingConventions.VarArgs) != 0)
                {
                    throw new InvalidDataException("Facade method " + name + "." + method.Name + " must not be generic or variadic.");
                }
                lines.Add("M|" + name + "|" + method.Name + "|" + TypeName(method.ReturnType)
                    + "|" + (method.IsStatic ? "static" : "") + "|" + Parameters(method));
            }
            foreach (PropertyInfo property in type.GetProperties(declared))
            {
                string accessors = Accessors(property);
                if (accessors.Length == 0)
                {
                    continue;
                }
                ParameterInfo[] index = property.GetIndexParameters();
                MethodInfo accessor = property.GetGetMethod(false) ?? property.GetSetMethod(false);
                string shared = TypeName(property.PropertyType) + "|" + (accessor.IsStatic ? "static" : "") + "|";
                if (index.Length == 0)
                {
                    lines.Add("P|" + name + "|" + property.Name + "|" + shared + accessors);
                    continue;
                }
                lines.Add("X|" + name + "|Item|" + shared + accessors + "|" + Signature(index));
            }
            foreach (FieldInfo field in type.GetFields(declared))
            {
                if (field.IsPublic && field.IsLiteral)
                {
                    lines.Add("F|" + name + "|" + field.Name + "|" + TypeName(field.FieldType) + "|" + Value(field.GetRawConstantValue()));
                }
            }
            foreach (EventInfo info in type.GetEvents(declared))
            {
                MethodInfo add = info.GetAddMethod(false);
                if (add != null && add.IsPublic)
                {
                    lines.Add("E|" + name + "|" + info.Name + "|" + TypeName(info.EventHandlerType));
                }
            }
        }

        private static string Kind(Type type)
        {
            if (type.IsInterface) { return "interface"; }
            if (type.IsValueType) { return "struct"; }
            return "class";
        }

        // The wrapper records `: IMdfTimeStamp` for the timestamp classes; in C# that is an interface
        // list, so a single non-Object base entry is reported the same way either direction.
        private static string BaseType(Type type)
        {
            Type parent = type.BaseType;
            if (parent != null && parent != typeof(object))
            {
                return TypeName(parent);
            }
            Type[] interfaces = type.GetInterfaces();
            return interfaces.Length == 1 ? TypeName(interfaces[0]) : "-";
        }

        private static string Accessors(PropertyInfo property)
        {
            return (property.GetGetMethod(false) != null ? "get;" : "") + (property.GetSetMethod(false) != null ? "set;" : "");
        }

        private static string Parameters(MethodBase method)
        {
            return Signature(method.GetParameters());
        }

        private static string Signature(ParameterInfo[] parameters)
        {
            return string.Join(";", parameters.Select(parameter => TypeName(parameter.ParameterType) + " " + parameter.Name));
        }

        private static string TypeName(Type type)
        {
            if (type.IsByRef)
            {
                return "ref " + TypeName(type.GetElementType());
            }
            if (type.IsArray)
            {
                if (type.GetArrayRank() != 1)
                {
                    throw new InvalidDataException("Facade type " + type.FullName + " must not use multi-rank arrays.");
                }
                return TypeName(type.GetElementType()) + "[]";
            }
            if (type.IsGenericType)
            {
                string definition = type.GetGenericTypeDefinition().FullName;
                int arity = definition.IndexOf('`');
                return (arity < 0 ? definition : definition.Substring(0, arity)) + "<"
                    + string.Join(";", type.GetGenericArguments().Select(TypeName)) + ">";
            }
            return type.FullName;
        }

        private static string Value(object constant)
        {
            return constant is string ? "\"" + constant + "\"" : Convert.ToString(constant, CultureInfo.InvariantCulture);
        }
    }
}
