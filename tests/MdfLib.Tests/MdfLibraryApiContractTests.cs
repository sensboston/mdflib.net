// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MdfLib.TestSupport;
using Xunit;

namespace MdfLib.Tests
{
    // Guards the MdfLibrary facade against the reviewed target surface. The baseline lists the whole
    // wrapper API, the facade declares its types phase by phase the wrapper surface
    // section 7), so this suite checks two directions: nothing outside the baseline, and nothing of a
    // declared type missing from the assembly.
    public sealed class MdfLibraryApiContractTests
    {
        // Every facade type of the reviewed surface; the declaration layer covers all of them, with the
        // members of a later phase throwing NotImplementedException.
        private static readonly string[] DeclaredTypes =
        {
            "MdfLibrary.ArrayStorage",
            "MdfLibrary.ArrayType",
            "MdfLibrary.BusType",
            "MdfLibrary.CaFlag",
            "MdfLibrary.CanErrorType",
            "MdfLibrary.CanMessage",
            "MdfLibrary.CanMessageType",
            "MdfLibrary.ChannelDataType",
            "MdfLibrary.ChannelSyncType",
            "MdfLibrary.ChannelType",
            "MdfLibrary.CnFlag",
            "MdfLibrary.ConversionType",
            "MdfLibrary.DoLogEvent",
            "MdfLibrary.ETagDataType",
            "MdfLibrary.EventCause",
            "MdfLibrary.EventType",
            "MdfLibrary.IMdfFileTimestamp",
            "MdfLibrary.IMdfTimeStamp",
            "MdfLibrary.MdfAttachment",
            "MdfLibrary.MdfBusType",
            "MdfLibrary.MdfChannel",
            "MdfLibrary.MdfChannelArray",
            "MdfLibrary.MdfChannelConversion",
            "MdfLibrary.MdfChannelGroup",
            "MdfLibrary.MdfChannelObserver",
            "MdfLibrary.MdfDataGroup",
            "MdfLibrary.MdfETag",
            "MdfLibrary.MdfEvent",
            "MdfLibrary.MdfFile",
            "MdfLibrary.MdfFileHistory",
            "MdfLibrary.MdfFileLocalTimestamp",
            "MdfLibrary.MdfFileLocalTimestampTz",
            "MdfLibrary.MdfFileTimezoneTimestamp",
            "MdfLibrary.MdfFileUtcTimestamp",
            "MdfLibrary.MdfHeader",
            "MdfLibrary.MdfLibrary",
            "MdfLibrary.MdfLocalTimestamp",
            "MdfLibrary.MdfLogSeverity",
            "MdfLibrary.MdfMetaData",
            "MdfLibrary.MdfReader",
            "MdfLibrary.MdfSourceInformation",
            "MdfLibrary.MdfStorageType",
            "MdfLibrary.MdfTimezoneTimestamp",
            "MdfLibrary.MdfUtcTimestamp",
            "MdfLibrary.MdfWriter",
            "MdfLibrary.MdfWriterType",
            "MdfLibrary.RangeType",
            "MdfLibrary.SourceType",
            "MdfLibrary.SyncType",
        };

        private static Assembly Library { get { return Assembly.Load("MdfLibrary.Managed"); } }

        private static string[] Baseline { get { return FacadeApi.ReadBaseline(Path.Combine(AppContext.BaseDirectory, "mdflibrary-api-baseline.txt")); } }

        private static string[] FacadeLines { get { return FacadeApi.Read(Library); } }

        [Fact]
        public void BaselineIsTheReviewedProjectionOfTheInventory()
        {
            string[] baseline = Baseline;
            Assert.NotEmpty(baseline);
            Assert.Contains(baseline, line => line.StartsWith("T|MdfLibrary.MdfWriter|class|", StringComparison.Ordinal));
            Assert.Contains(baseline, line => line.StartsWith("V|MdfLibrary.MdfBusType|CAN|1", StringComparison.Ordinal));
            Assert.Contains(baseline, line => line.StartsWith("V|MdfLibrary.MdfLogSeverity|Emergency|8", StringComparison.Ordinal));
            Assert.DoesNotContain(baseline, line => line.StartsWith("M|MdfLibrary.MdfWriter|ReadSample", StringComparison.Ordinal));
        }

        [Fact]
        public void FacadeDeclaresExactlyTheTypesOfThisPhase()
        {
            string[] actual = Library.GetExportedTypes()
                .Where(type => type.Namespace == "MdfLibrary")
                .Select(type => type.FullName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(DeclaredTypes.OrderBy(name => name, StringComparer.Ordinal).ToArray(), actual);
        }

        [Fact]
        public void FacadeSurfaceMatchesTheBaselineOfEveryDeclaredType()
        {
            HashSet<string> expected = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in Baseline)
            {
                string owner = line.Split('|')[1];
                if (DeclaredTypes.Contains(owner))
                {
                    expected.Add(line);
                }
            }
            List<string> actual = FacadeLines.ToList();
            Assert.NotEmpty(actual);
            List<string> extra = actual.Where(line => !Baseline.Contains(line)).OrderBy(line => line, StringComparer.Ordinal).ToList();
            Assert.True(extra.Count == 0, "Facade surface outside the reviewed baseline:" + Environment.NewLine + Join(extra));
            List<string> missing = expected.Where(line => !actual.Contains(line)).OrderBy(line => line, StringComparer.Ordinal).ToList();
            Assert.True(missing.Count == 0, "Declared facade type incomplete against the baseline:" + Environment.NewLine + Join(missing));
        }

        [Fact]
        public void FacadeDoesNotExposeTheEngineOrInteropTypes()
        {
            List<string> leaks = new List<string>();
            foreach (Type type in Library.GetExportedTypes().Where(item => item.Namespace == "MdfLibrary"))
            {
                foreach (Type used in Members(type).Where(used => !Exposable(used)))
                {
                    leaks.Add(type.FullName + " -> " + used.FullName);
                }
            }
            Assert.True(leaks.Count == 0, "Facade member type outside MdfLibrary/System:" + Environment.NewLine + Join(leaks.Distinct().ToList()));
        }

        private static IEnumerable<Type> Members(Type type)
        {
            const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public;
            foreach (ConstructorInfo constructor in type.GetConstructors(flags))
            {
                foreach (ParameterInfo parameter in constructor.GetParameters()) { yield return parameter.ParameterType; }
            }
            foreach (MethodInfo method in type.GetMethods(flags).Where(method => !method.IsSpecialName))
            {
                yield return method.ReturnType;
                foreach (ParameterInfo parameter in method.GetParameters()) { yield return parameter.ParameterType; }
            }
            foreach (PropertyInfo property in type.GetProperties(flags))
            {
                yield return property.PropertyType;
                foreach (ParameterInfo parameter in property.GetIndexParameters()) { yield return parameter.ParameterType; }
            }
            foreach (FieldInfo field in type.GetFields(flags)) { yield return field.FieldType; }
            foreach (EventInfo info in type.GetEvents(flags)) { yield return info.EventHandlerType; }
        }

        // A public facade member may only name wrapper types and BCL types. The walk is total and
        // depth-limited on purpose: GetGenericTypeDefinition returns null for an open generic
        // parameter, and the definition of a constructed generic type is itself generic, so recursing
        // into it repeats forever (that is what a first version of this check did in CI).
        private static bool Exposable(Type type)
        {
            return Exposable(type, 0);
        }

        private static bool Exposable(Type type, int depth)
        {
            if (type == null || type.IsGenericParameter || type.ContainsGenericParameters || depth > 8)
            {
                return false;
            }
            if (type.IsArray || type.IsByRef || type.IsPointer)
            {
                return Exposable(type.GetElementType(), depth + 1);
            }
            if (!Published(type))
            {
                return false;
            }
            return !type.IsGenericType || type.GetGenericArguments().All(argument => Exposable(argument, depth + 1));
        }

        // Namespace, not FullName: FullName is null for the types above, and the engine is recognised
        // by its own namespaces (MdfLib, MdfLib.*).
        private static bool Published(Type type)
        {
            string ns = type.Namespace ?? (type.DeclaringType != null ? type.DeclaringType.Namespace : null);
            return ns == "MdfLibrary"
                || ns == "System"
                || ns != null && (ns.StartsWith("System.", StringComparison.Ordinal) || ns.StartsWith("Microsoft.", StringComparison.Ordinal));
        }

        private static string Join(List<string> lines)
        {
            return string.Join(Environment.NewLine, lines.Take(25)) + (lines.Count > 25 ? Environment.NewLine + "+ " + (lines.Count - 25) + " more" : "");
        }
    }
}
