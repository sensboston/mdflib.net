// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// The four timestamp wrappers hold the nanosecond value the C++/CLI
// wrapper keeps, and nothing else - the reviewed surface exposes no getter, so the value is read only
// by the writer. Local and timezone forms pass their raw nanoseconds through, as mdf:: does.
using System;
namespace MdfLibrary
{
    /// <summary>A timestamp that can be read as UTC nanoseconds since 1970-01-01.</summary>
    public interface IMdfTimeStamp
    {
        // The reviewed surface declares no members for this type.
    }

    /// <summary>UTC nanoseconds since the Unix epoch,
    /// from either the raw value or a DateTime converted through the same UTC rule as the wrapper.</summary>
    public class MdfUtcTimestamp : IMdfTimeStamp
    {
        /// <summary>The nanoseconds the wrapper holds in its private field; the reviewed surface exposes no
        /// getter, so only the facade's writer and header read it.</summary>
        internal readonly ulong TimestampNs;

        /// <summary>UTC nanoseconds since 1970-01-01 are stored as given.</summary>
        public MdfUtcTimestamp(ulong utcTimestamp)
        {
            TimestampNs = utcTimestamp;
        }

        /// <summary>A DateTime is converted with DateTime.ToUniversalTime,
        /// so an unspecified kind is read as local time, exactly as MdfNetHelper::GetUnixNanoTimestamp does.</summary>
        public MdfUtcTimestamp(System.DateTime time)
        {
            TimestampNs = FacadeClock.UnixNs(time);
        }
    }

    /// <summary>Local nanoseconds since the epoch (no
    /// timezone offset is recorded, which is what mdf::LocalTimestamp means).</summary>
    public class MdfLocalTimestamp : IMdfTimeStamp
    {
        /// <summary>The nanoseconds the wrapper holds in its private field; the reviewed surface exposes no
        /// getter, so only the facade's writer and header read it.</summary>
        internal readonly ulong TimestampNs;

        /// <summary>The raw local nanoseconds are stored as given.</summary>
        public MdfLocalTimestamp(ulong localTimestamp)
        {
            TimestampNs = localTimestamp;
        }

        /// <summary>A DateTime is converted with DateTime.ToLocalTime,
        /// as MdfNetHelper::GetLocalNanoTimestamp does.</summary>
        public MdfLocalTimestamp(System.DateTime time)
        {
            TimestampNs = FacadeClock.LocalNs(time);
        }
    }

    /// <summary>UTC nanoseconds plus the offsets that
    /// MDF 4.20 stores in the timestamp block.</summary>
    public class MdfTimezoneTimestamp : IMdfTimeStamp
    {
        /// <summary>The nanoseconds the wrapper holds in its private field; the reviewed surface exposes no
        /// getter, so only the facade's writer and header read it.</summary>
        internal readonly ulong TimestampNs;
        /// <summary>The minutes from UTC the wrapper keeps beside the time; no public getter exists.</summary>
        internal readonly short TimezoneOffsetMin;

        /// <summary>The minutes of daylight saving the wrapper keeps beside the time; no public getter exists.</summary>
        internal readonly short DstOffsetMin;

        /// <summary>The raw values are stored as given.</summary>
        public MdfTimezoneTimestamp(ulong utcTimestamp, short timezoneOffsetMin, short dstOffsetMin)
        {
            TimestampNs = utcTimestamp;
            TimezoneOffsetMin = timezoneOffsetMin;
            DstOffsetMin = dstOffsetMin;
        }

        /// <summary>The DateTime is converted with DateTime.ToUniversalTime.</summary>
        public MdfTimezoneTimestamp(System.DateTime time, short timezoneOffsetMin, short dstOffsetMin)
        {
            TimestampNs = FacadeClock.UnixNs(time);
            TimezoneOffsetMin = timezoneOffsetMin;
            DstOffsetMin = dstOffsetMin;
        }
    }

}
