// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MdfLib.Tests.TestDoubles;
using MdfLib.TestSupport;
using Xunit;

namespace MdfLib.Tests.Reading
{
    // explicit CANopen calendar decoding of the frozen channel bytes (raw/physical values stay the wire bytes).
    public sealed class CalendarDecodingTests
    {
        private const string Sorted = "m5z-canopen410"; // 0 t, 1 date @8, 2 time @15, 3 date_inv @21 (bit 0), 4 n @28; 31-byte records
        private const int Date = 1, Time = 2, DateInv = 3, Counter = 4;

        private static byte[] Hex(string text)
        {
            return Enumerable.Range(0, text.Length / 2).Select(i => Convert.ToByte(text.Substring(2 * i, 2), 16)).ToArray();
        }

        private static DateTime? Decode(MdfCalendarKind kind, string hex)
        {
            DateTime value;
            return MdfCalendar.TryDecode(kind, Hex(hex), out value) ? value : (DateTime?)null;
        }

        [Theory]
        [InlineData("00000000c10100", 2000, 1, 1, 0, 0, 0, 0)]
        [InlineData("5fea3b97ff0c63", 1999, 12, 31, 23, 59, 59, 999)] // summer-time bit, weekday 7, year 99
        [InlineData("2a76058e300b1a", 2026, 11, 16, 14, 5, 30, 250)]
        [InlineData("d2041e09af0654", 1984, 6, 15, 9, 30, 1, 234)]
        [InlineData("000000001d0218", 2024, 2, 29, 0, 0, 0, 0)]
        [InlineData("00000000010145", 2069, 1, 1, 0, 0, 0, 0)]
        [InlineData("00000000010146", 1970, 1, 1, 0, 0, 0, 0)]
        [InlineData("1027c0e0c1c198", 2024, 1, 1, 0, 0, 10, 0)]     // reserved minute/hour/month/year bits ignored
        public void CanOpenDatesDecodeAsWallClockValues(string hex, int year, int month, int day, int hour, int minute, int second, int ms)
        {
            DateTime? value = Decode(MdfCalendarKind.CanOpenDate, hex);
            Assert.Equal(new DateTime(year, month, day, hour, minute, second, ms), value);
            Assert.Equal(DateTimeKind.Unspecified, value.Value.Kind);
        }

        [Theory]
        [InlineData("60ea0000010100")]   // 60000 ms
        [InlineData("00003c0001010d")]   // minute 60
        [InlineData("00000018010100")]   // hour 24
        [InlineData("00000000000100")]   // day 0
        [InlineData("000000001e0218")]   // 2024-02-30
        [InlineData("000000001d0217")]   // 2023-02-29
        [InlineData("000000001f0418")]   // April 31
        [InlineData("00000000010000")]   // month 0
        [InlineData("000000000d0d00")]   // month 13
        [InlineData("00000000010164")]   // year 100
        [InlineData("000000000101")]     // 6 bytes
        [InlineData("00000000010100ff")] // 8 bytes
        [InlineData("")]
        public void OutOfRangeDatesAreNotDecodable(string hex)
        {
            DateTime value;
            Assert.False(MdfCalendar.TryDecode(MdfCalendarKind.CanOpenDate, Hex(hex), out value));
            Assert.Equal(default(DateTime), value);
        }

        [Theory]
        [InlineData("000000000000", "1984-01-01 00:00:00.000")]
        [InlineData("ff5b2605ffff", "2163-06-06 23:59:59.999")]
        [InlineData("00000000ffff", "2163-06-06 00:00:00.000")]
        [InlineData("010000000100", "1984-01-02 00:00:00.001")]
        [InlineData("000000f00000", "1984-01-01 00:00:00.000")] // reserved bits 28-31 ignored
        [InlineData("952cb302bf3b", "2025-11-16 12:34:56.789")]
        public void CanOpenTimesCountMillisecondsAndDaysFrom1984(string hex, string expected)
        {
            DateTime? value = Decode(MdfCalendarKind.CanOpenTime, hex);
            Assert.Equal(expected, value.Value.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(DateTimeKind.Unspecified, value.Value.Kind);
        }

        [Theory]
        [InlineData("005c26050000")] // 86 400 000 ms
        [InlineData("ffffff0f0000")]
        [InlineData("0000000000")]
        [InlineData("00000000000000")]
        public void OutOfRangeTimesAreNotDecodable(string hex)
        {
            Assert.Null(Decode(MdfCalendarKind.CanOpenTime, hex));
        }

        [Fact]
        public void KindNoneAndNullArguments()
        {
            DateTime value;
            Assert.False(MdfCalendar.TryDecode(MdfCalendarKind.None, Hex("00000000c10100"), out value));
            Assert.False(MdfCalendar.TryDecode((MdfCalendarKind)7, Hex("000000000000"), out value));
            Assert.Throws<ArgumentNullException>(() => MdfCalendar.TryDecode(MdfCalendarKind.CanOpenDate, null, out value));
            Assert.Throws<ArgumentNullException>(() => MdfCalendar.TryDecode(null, default(MdfSampleValue), out value));
        }

        [Fact]
        public void ChannelsPublishTheirCalendarKindAndSamplesDecode()
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(Sorted), false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(new[] { MdfCalendarKind.None, MdfCalendarKind.CanOpenDate, MdfCalendarKind.CanOpenTime, MdfCalendarKind.CanOpenDate, MdfCalendarKind.None },
                    group.Channels.Select(c => c.CalendarKind));
                MdfSampleBatch batch = reader.ReadSamples(group, new[] { Date, Time, DateInv, Counter }).Single();
                DateTime value;
                Assert.True(MdfCalendar.TryDecode(group.Channels[Date], batch.GetValue(2, 0), out value));
                Assert.Equal(new DateTime(2026, 11, 16, 14, 5, 30, 250), value);
                Assert.True(MdfCalendar.TryDecode(group.Channels[Time], batch.GetValue(4, 1), out value));
                Assert.Equal(new DateTime(2011, 5, 19, 12, 0, 0), value);
                Assert.False(batch.GetValue(1, 2).IsValid);
                Assert.True(MdfCalendar.TryDecode(group.Channels[DateInv], batch.GetValue(1, 2), out value)); // validity is separate
                Assert.Equal(new DateTime(1984, 6, 15, 9, 30, 1, 234), value);
                Assert.False(MdfCalendar.TryDecode(group.Channels[Counter], batch.GetValue(0, 3), out value));
                Assert.False(MdfCalendar.TryDecode(group.Channels[Date], batch.GetValue(0, 3), out value)); // not a byte array
                Assert.False(MdfCalendar.TryDecode(group.Channels[Time], batch.GetValue(0, 0), out value)); // 7 bytes for a time
                Assert.Equal(default(DateTime), value);
            }
        }

        [Fact]
        public void DecodingDoesNotCopyOrAlterTheStoredBytes()
        {
            byte[] bytes = FixtureCorpus.Read(Sorted);
            int record = M3Wire.Payload(bytes, M5DWire.Root(bytes));
            bytes[record + 8 + 5] = 13; // month 13 in row 0
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                MdfSampleValue sample = reader.ReadSamples(group, new[] { Date }).First().GetValue(0, 0);
                DateTime value;
                Assert.False(MdfCalendar.TryDecode(group.Channels[Date], sample, out value));
                Assert.Equal(Hex("000000c1010d00").Length, sample.GetByteArrayValue().Length);
                Assert.Equal(13, sample.GetByteArrayValue()[5]);
            }
        }

        [Fact]
        public void Mdf3AndOtherMdf4ChannelsHaveNoCalendarKind()
        {
            foreach (string id in new[] { "m6d-formula330", "m5aa-complex420" })
            using (MdfReader reader = MdfReader.Open(new MemoryStream(FixtureCorpus.Read(id), false)))
            {
                Assert.All(reader.ReadMetadata()[0].ChannelGroups[0].Channels, c => Assert.Equal(MdfCalendarKind.None, c.CalendarKind));
            }
        }
    }
}
