// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// The four file timestamps the header hands out. Like the upstream
// MdfFileTimestamp.cpp they only present the nanoseconds a caller (or the file) recorded, together with the
// offsets that form carries; the DateTime getter does no offset arithmetic, it formats the stored value.
using System;
namespace MdfLibrary
{
    /// <summary>A timestamp of an MDF file (the HD or FH time).</summary>
    public interface IMdfFileTimestamp
    {
        /// <summary>The daylight saving offset in minutes, zero for the forms
        /// that store none.</summary>
        short DstMin { get; }

        /// <summary>The stored nanoseconds read back as a DateTime.</summary>
        System.DateTime Time { get; }

        /// <summary>The nanoseconds since 1970-01-01 the file recorded.</summary>
        ulong TimeNs { get; }

        /// <summary>The timezone offset in minutes, zero for the forms that store
        /// none.</summary>
        short TimezoneMin { get; }
    }

    /// <summary>The UTC nanoseconds of a file timestamp.</summary>
    public class MdfFileUtcTimestamp : IMdfFileTimestamp
    {
        private readonly ulong timeNs_;

        /// <summary>The value is stored as given.</summary>
        public MdfFileUtcTimestamp(ulong utcTimeNs)
        {
            timeNs_ = utcTimeNs;
        }

        /// <summary>This form stores no daylight saving offset.</summary>
        public short DstMin
        {
            get { return 0; }
        }

        /// <summary>The stored nanoseconds read back without any offset arithmetic.</summary>
        public System.DateTime Time
        {
            get { return FacadeClock.FromNs(timeNs_); }
        }

        /// <summary>The nanoseconds since 1970-01-01.</summary>
        public ulong TimeNs
        {
            get { return timeNs_; }
        }

        /// <summary>This form stores no timezone offset.</summary>
        public short TimezoneMin
        {
            get { return 0; }
        }
    }

    /// <summary>The local nanoseconds of a file timestamp.</summary>
    public class MdfFileLocalTimestamp : IMdfFileTimestamp
    {
        private readonly ulong timeNs_;

        /// <summary>The value is stored as given.</summary>
        public MdfFileLocalTimestamp(ulong localTimeNs)
        {
            timeNs_ = localTimeNs;
        }

        /// <summary>This form stores no daylight saving offset.</summary>
        public short DstMin
        {
            get { return 0; }
        }

        /// <summary>The stored nanoseconds read back without any offset arithmetic.</summary>
        public System.DateTime Time
        {
            get { return FacadeClock.FromNs(timeNs_); }
        }

        /// <summary>The local nanoseconds since 1970-01-01.</summary>
        public ulong TimeNs
        {
            get { return timeNs_; }
        }

        /// <summary>This form stores no timezone offset.</summary>
        public short TimezoneMin
        {
            get { return 0; }
        }
    }

    /// <summary>Local nanoseconds with the timezone offset
    /// the file recorded next to them.</summary>
    public class MdfFileLocalTimestampTz : IMdfFileTimestamp
    {
        private readonly ulong timeNs_;
        private readonly short timezoneMin_;

        /// <summary>Both values are stored as given.</summary>
        public MdfFileLocalTimestampTz(ulong localTimeNs, short tzMin)
        {
            timeNs_ = localTimeNs;
            timezoneMin_ = tzMin;
        }

        /// <summary>This form stores no daylight saving offset.</summary>
        public short DstMin
        {
            get { return 0; }
        }

        /// <summary>The stored local nanoseconds, not shifted by the offset.</summary>
        public System.DateTime Time
        {
            get { return FacadeClock.FromNs(timeNs_); }
        }

        /// <summary>The local nanoseconds since 1970-01-01.</summary>
        public ulong TimeNs
        {
            get { return timeNs_; }
        }

        /// <summary>The minutes the file added to UTC.</summary>
        public short TimezoneMin
        {
            get { return timezoneMin_; }
        }
    }

    /// <summary>The nanoseconds of an MDF 4.20 timestamp
    /// with both offsets.</summary>
    public class MdfFileTimezoneTimestamp : IMdfFileTimestamp
    {
        private readonly ulong timeNs_;
        private readonly short timezoneMin_;
        private readonly short dstMin_;

        /// <summary>All values are stored as given.</summary>
        public MdfFileTimezoneTimestamp(ulong timeNs, short timezoneMin, short dstMin)
        {
            timeNs_ = timeNs;
            timezoneMin_ = timezoneMin;
            dstMin_ = dstMin;
        }

        /// <summary>The minutes of daylight saving the file recorded.</summary>
        public short DstMin
        {
            get { return dstMin_; }
        }

        /// <summary>The stored nanoseconds read back without any offset arithmetic.</summary>
        public System.DateTime Time
        {
            get { return FacadeClock.FromNs(timeNs_); }
        }

        /// <summary>The nanoseconds since 1970-01-01.</summary>
        public ulong TimeNs
        {
            get { return timeNs_; }
        }

        /// <summary>The minutes from UTC the file recorded.</summary>
        public short TimezoneMin
        {
            get { return timezoneMin_; }
        }
    }
}
