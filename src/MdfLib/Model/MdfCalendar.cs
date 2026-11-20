// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;

namespace MdfLib
{
    /// <summary>Decodes MDF 4 CANopen date/time channel bytes into wall-clock <see cref="DateTime"/> values.</summary>
    /// <remarks>
    /// Raw and physical sample values of CANopen channels stay their exact wire bytes; decoding is explicit.
    /// Results have <see cref="DateTimeKind.Unspecified"/>: the encodings carry no time zone. Reserved bits, the
    /// summer-time flag and the day of week are ignored. A two-digit year 0..69 is 2000..2069 and 70..99 is 1970..1999.
    /// Field values outside their calendar ranges (or a wrong byte count) are not decodable.
    /// </remarks>
    internal static class MdfCalendar
    {
        private static readonly DateTime CanOpenEpoch = new DateTime(1984, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        private const uint MillisecondsPerDay = 86400000;

        /// <summary>Decodes stored bytes of the given calendar kind.</summary>
        /// <param name="kind">The calendar encoding.</param>
        /// <param name="bytes">Exactly 7 (date) or 6 (time) stored bytes.</param>
        /// <param name="value">The decoded wall-clock value, or default when decoding fails.</param>
        /// <returns>True when the bytes form a valid calendar value of that kind.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="bytes"/> is null.</exception>
        public static bool TryDecode(MdfCalendarKind kind, byte[] bytes, out DateTime value)
        {
            if (bytes == null) { throw new ArgumentNullException(nameof(bytes)); }
            value = default(DateTime);
            switch (kind)
            {
                case MdfCalendarKind.CanOpenDate: return TryDate(bytes, out value);
                case MdfCalendarKind.CanOpenTime: return TryTime(bytes, out value);
                default: return false;
            }
        }

        /// <summary>Decodes a sample value of a channel whose <see cref="MdfChannel.CalendarKind"/> is not None.</summary>
        /// <remarks>Validity is not consulted: an invalid sample decodes its stored bytes; check IsValid separately.</remarks>
        /// <param name="channel">The channel the value was read from.</param>
        /// <param name="sample">A byte-array value of that channel.</param>
        /// <param name="value">The decoded wall-clock value, or default when decoding fails.</param>
        /// <returns>False for non-calendar channels, non-byte-array values and undecodable bytes.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="channel"/> is null.</exception>
        public static bool TryDecode(MdfChannel channel, MdfSampleValue sample, out DateTime value)
        {
            if (channel == null) { throw new ArgumentNullException(nameof(channel)); }
            value = default(DateTime);
            byte[] stored = sample.Kind == MdfValueKind.ByteArray ? sample.StoredBytes : null;
            return stored != null && TryDecode(channel.CalendarKind, stored, out value);
        }

        private static bool TryDate(byte[] bytes, out DateTime value)
        {
            value = default(DateTime);
            if (bytes.Length != 7) { return false; }
            int milliseconds = bytes[0] | bytes[1] << 8;
            int minute = bytes[2] & 0x3F, hour = bytes[3] & 0x1F, day = bytes[4] & 0x1F, month = bytes[5] & 0x3F, year = bytes[6] & 0x7F;
            if (milliseconds > 59999 || minute > 59 || hour > 23 || month < 1 || month > 12 || year > 99) { return false; }
            year += year < 70 ? 2000 : 1900;
            if (day < 1 || day > DateTime.DaysInMonth(year, month)) { return false; }
            value = new DateTime(year, month, day, hour, minute, milliseconds / 1000, milliseconds % 1000, DateTimeKind.Unspecified);
            return true;
        }

        private static bool TryTime(byte[] bytes, out DateTime value)
        {
            value = default(DateTime);
            if (bytes.Length != 6) { return false; }
            uint milliseconds = (uint)(bytes[0] | bytes[1] << 8 | bytes[2] << 16 | (bytes[3] & 0x0F) << 24);
            int days = bytes[4] | bytes[5] << 8;
            if (milliseconds >= MillisecondsPerDay) { return false; }
            value = CanOpenEpoch.AddTicks(days * TimeSpan.TicksPerDay + milliseconds * TimeSpan.TicksPerMillisecond);
            return true;
        }
    }
}
