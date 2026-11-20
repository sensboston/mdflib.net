// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
namespace MdfLibrary
{
    /// <summary>Entry points of the library: channel observers and the log event.</summary>
    public class MdfLibrary
    {
        // Instances come from the facade factories; the wrapper has no public constructor
        // either, so the declaration layer keeps the type non-constructible by name.
        private MdfLibrary()
        {
        }

        // The backing field of LogEvent.
        private DoLogEvent logEvent_;

        // The wrapper hands out one object on which the log event is registered; the managed facade keeps the
        // same shape, because the event is an instance event here as well and not a static one.
        private static readonly object InstanceGate = new object();
        private static MdfLibrary instance_;

        /// <summary>Raised for each message of the library's log.</summary>
        public event DoLogEvent LogEvent
        {
            add { logEvent_ += value; }
            remove { logEvent_ -= value; }
        }

        /// <summary>The log line the wrapper hands to its spdlog sink and to the
        /// registered log function. The managed engine has no log channel of its own, so raising the event is
        /// the whole behaviour.</summary>
        public void AddLog(MdfLogSeverity severity, string function, string message)
        {
            DoLogEvent handler = logEvent_;
            if (handler != null) { handler(severity, function, message); }
        }

        /// <summary>Creates an observer that collects the samples of one channel while the data group is read.</summary>
        public static MdfChannelObserver CreateChannelObserver(MdfDataGroup dg, MdfChannelGroup cg, MdfChannel cn)
        {
            if (dg == null || cg == null || cn == null || dg.Pending == null || cg.Pending == null || cn.Pending == null)
            { return null; }
            return MdfChannelObserver.Create(cg.Pending, cn.Pending);
        }

        /// <summary>Creates an observer for the channel with the given name in a data group, or null if there is none.</summary>
        public static MdfChannelObserver CreateChannelObserverByChannelName(MdfDataGroup dg, string channelName)
        {
            if (dg == null || string.IsNullOrEmpty(channelName) || dg.Pending == null) { return null; }
            // The wrapper's rule: among the channels with the name the one of the group with the most samples wins;
            // a tie goes to the later group, as the core compares with less-or-equal.
            PendingGroup chosenGroup = null;
            PendingChannel chosen = null;
            ulong samples = 0UL;
            foreach (PendingGroup group in dg.Pending.Groups)
            {
                foreach (PendingChannel channel in group.Channels)
                {
                    if (!string.Equals(channelName, channel.Name, StringComparison.OrdinalIgnoreCase)) { continue; }
                    if (samples <= group.SampleCount)
                    {
                        samples = group.SampleCount;
                        chosenGroup = group;
                        chosen = channel;
                    }
                }
            }
            return chosen == null ? null : MdfChannelObserver.Create(chosenGroup, chosen);
        }

        /// <summary>Creates an observer for each channel of a channel group.</summary>
        public static MdfChannelObserver[] CreateChannelObserverForChannelGroup(MdfDataGroup dg, MdfChannelGroup cg)
        {
            if (dg == null || cg == null || dg.Pending == null || cg.Pending == null) { return null; }
            MdfChannelObserver[] result = new MdfChannelObserver[cg.Pending.Channels.Count];
            for (int i = 0; i < result.Length; i++) { result[i] = MdfChannelObserver.CreateOrEmpty(cg.Pending, cg.Pending.Channels[i]); }
            return result;
        }

        /// <summary>Creates an observer for each channel of every channel group in a data group.</summary>
        public static MdfChannelObserver[] CreateChannelObserverForDataGroup(MdfDataGroup dg)
        {
            if (dg == null || dg.Pending == null) { return null; }
            // Groups without samples are skipped, as the core does; every other channel gets its entry, empty if unreadable.
            int count = 0;
            foreach (PendingGroup group in dg.Pending.Groups)
            {
                if (group.SampleCount != 0UL) { count += group.Channels.Count; }
            }
            MdfChannelObserver[] result = new MdfChannelObserver[count];
            int next = 0;
            foreach (PendingGroup group in dg.Pending.Groups)
            {
                if (group.SampleCount == 0UL) { continue; }
                foreach (PendingChannel channel in group.Channels)
                {
                    result[next++] = MdfChannelObserver.CreateOrEmpty(group, channel);
                }
            }
            return result;
        }

        /// <summary>The destructor of the wrapper unregisters the native log sink, and
        /// nothing else is owned. Dropping the handlers is the managed equivalent, and like the wrapper it may be
        /// called twice.</summary>
        public void Dispose()
        {
            logEvent_ = null;
        }

        /// <summary>The lazily created singleton on which a log handler is registered,
        /// which is what <c>MdfLibrary.Instance.LogEvent +=...</c> means in the wrapper.</summary>
        public static MdfLibrary Instance
        {
            get
            {
                MdfLibrary instance = instance_;
                if (instance != null) { return instance; }
                lock (InstanceGate)
                {
                    instance = instance_;
                    if (instance == null) { instance = instance_ = new MdfLibrary(); }
                }
                return instance;
            }
        }

        /// <summary>The eight-byte identification of the file, which is all the wrapper
        /// reads. A name that cannot be opened is answered as false, like the wrapper's own probe.</summary>
        public static bool IsMdfFile(string filename)
        {
            if (string.IsNullOrEmpty(filename)) { return false; }
            try
            {
                using (System.IO.FileStream stream = new System.IO.FileStream(
                    filename, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                {
                    return MdfLib.MdfReader.IsMdfFile(stream);
                }
            }
            catch (ArgumentException) { return false; }
            catch (System.IO.IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        /// <summary>The wall clock in nanoseconds since 1970-01-01 UTC, the same value
        /// the wrapper's <c>MdfNetHelper</c> builds. It is not monotonic, which is what the writer's timestamp
        /// arguments expect.</summary>
        public static ulong NowNs()
        {
            return FacadeClock.NowNs();
        }
    }

    /// <summary>The severity of a log message.</summary>
    public enum MdfLogSeverity : byte
    {
        /// <summary>Trace messages.</summary>
        Trace = 0,

        /// <summary>Debug messages.</summary>
        Debug = 1,

        /// <summary>Information.</summary>
        Info = 2,

        /// <summary>Normal but significant events.</summary>
        Notice = 3,

        /// <summary>Warnings.</summary>
        Warning = 4,

        /// <summary>Errors.</summary>
        Error = 5,

        /// <summary>Critical errors.</summary>
        Critical = 6,

        /// <summary>Action must be taken immediately.</summary>
        Alert = 7,

        /// <summary>The library is unusable.</summary>
        Emergency = 8,
    }

    /// <summary>Handles a message of the library's log.</summary>
    public delegate void DoLogEvent(MdfLogSeverity severity, string function, string message);

}
