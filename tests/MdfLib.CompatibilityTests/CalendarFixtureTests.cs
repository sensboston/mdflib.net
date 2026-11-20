// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using MdfLib.TestSupport;
using Xunit;

namespace MdfLib.CompatibilityTests
{
    // CANopen calendar values frozen before the C# change (tools/fixtures/m5aa-expectations.json `calendar`): the
    // frozen channel bytes and standalone vectors decoded by an independent stdlib-datetime decoder.
    public sealed class CalendarFixtureTests
    {
        private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff";

        public static IEnumerable<object[]> Channels()
        {
            return Calendar().Element("channels").Elements("item").Select((c, i) => new object[] { i });
        }

        [Theory]
        [MemberData(nameof(Channels))]
        public void FrozenCanOpenChannelsDecodeToTheFrozenWallClockValues(int ordinal)
        {
            XElement wanted = Calendar().Element("channels").Elements("item").ElementAt(ordinal);
            MdfCalendarKind kind = (MdfCalendarKind)Enum.Parse(typeof(MdfCalendarKind), Text(wanted, "calendarKind"));
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(Text(wanted, "id")), false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[Int(wanted, "group")];
                MdfChannel channel = group.Channels[Int(wanted, "index")];
                Assert.Equal(Text(wanted, "name"), channel.Name);
                Assert.Equal(kind, channel.CalendarKind);
                List<string> values = wanted.Element("values").Elements("item").Select(Value).ToList();
                foreach (MdfValueRepresentation representation in new[] { MdfValueRepresentation.Raw, MdfValueRepresentation.Physical })
                {
                    List<MdfSampleValue> samples = reader.ReadSamples(group, new[] { channel.Index }, batchSize: 2, representation: representation)
                        .SelectMany(b => Enumerable.Range(0, b.SampleCount).Select(r => b.GetValue(r, 0))).ToList();
                    Assert.Equal(values.Count, samples.Count);
                    for (int row = 0; row < samples.Count; row++)
                    {
                        Assert.Equal(MdfValueKind.ByteArray, samples[row].Kind);
                        DateTime decoded;
                        Assert.Equal(values[row] != null, MdfCalendar.TryDecode(channel, samples[row], out decoded));
                        Assert.Equal(values[row], values[row] == null ? null : Iso(decoded));
                        DateTime direct;
                        Assert.Equal(values[row] != null, MdfCalendar.TryDecode(kind, samples[row].GetByteArrayValue(), out direct));
                        Assert.Equal(decoded, direct);
                    }
                }
                foreach (MdfChannel other in group.Channels.Where(c => c.Index != channel.Index && c.ValueKind != MdfValueKind.ByteArray))
                {
                    Assert.Equal(MdfCalendarKind.None, other.CalendarKind);
                }
            }
        }

        [Fact]
        public void StandaloneVectorsMatchTheIndependentDecoder()
        {
            List<XElement> vectors = Calendar().Element("vectors").Elements("item").ToList();
            Assert.True(vectors.Count >= 25);
            Assert.Contains(vectors, v => Value(v.Element("value")) == null);
            foreach (XElement vector in vectors)
            {
                MdfCalendarKind kind = (MdfCalendarKind)Enum.Parse(typeof(MdfCalendarKind), Text(vector, "calendarKind"));
                string hex = Text(vector, "bytes");
                byte[] bytes = Enumerable.Range(0, hex.Length / 2).Select(i => byte.Parse(hex.Substring(2 * i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
                string wanted = Value(vector.Element("value"));
                DateTime decoded;
                Assert.True((wanted != null) == MdfCalendar.TryDecode(kind, bytes, out decoded), hex);
                if (wanted == null) { Assert.Equal(default(DateTime), decoded); }
                else
                {
                    Assert.Equal(wanted, Iso(decoded));
                    Assert.Equal(DateTimeKind.Unspecified, decoded.Kind);
                }
                Assert.False(MdfCalendar.TryDecode(MdfCalendarKind.None, bytes, out decoded));
            }
        }

        private static string Iso(DateTime value) { return value.ToString(Format, CultureInfo.InvariantCulture); }

        private static string Value(XElement item)
        {
            XAttribute type = item.Attribute("type");
            return type != null && type.Value == "null" ? null : item.Value;
        }

        private static XElement Calendar() { return ComplexFixtureTests.Load().Element("calendar"); }
        private static string Text(XElement parent, string name) { return parent.Element(name).Value; }
        private static int Int(XElement parent, string name) { return int.Parse(parent.Element(name).Value, CultureInfo.InvariantCulture); }
    }
}
