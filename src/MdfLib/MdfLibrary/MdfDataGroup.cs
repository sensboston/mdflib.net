// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// The data group handle. A data group holds its first channel group from the
// start and any further ones added by CreateChannelGroup; those share its unsorted record stream (, record IDs).
using System;
namespace MdfLibrary
{
    /// <summary>One data group of a file.</summary>
    public class MdfDataGroup
    {
        private readonly PendingDataGroup pending_;

        /// <summary>The tree node behind this handle; the reader matches a group to its samples through it.</summary>
        internal PendingDataGroup Pending { get { return pending_; } }

        /// <summary>The handle of one pending data group; the wrapper creates it with the file, so neither side
        /// has a public constructor.</summary>
        internal MdfDataGroup(PendingDataGroup pending)
        {
            pending_ = pending;
        }

        /// <summary>The managed writer maps one channel group per data group, so a
        /// second group of the same data group needs a data group of its own.</summary>
        /// <summary>Adds a channel group to this data group. The groups of one data group share one unsorted record
        /// stream, each record behind the record ID of its group (MDF 4; MDF 3.30 has the same layout).</summary>
        public MdfChannelGroup CreateChannelGroup()
        {
            pending_.Context.RejectFlushed();
            PendingGroup group = pending_.Context.AddChannelGroup(pending_);
            return PendingGroup.FacadeOf(group);
        }

        /// <summary>The managed writer writes no MD blocks at all.</summary>
        public MdfMetaData CreateMetaData()
        {
            pending_.Context.RejectFlushed();
            if (pending_.Metadata == null) { pending_.Metadata = new MdfMetaData(); }
            return pending_.Metadata;
        }

        /// <summary>The channel group of this data group that owns the channel, or
        /// null when the channel is null or belongs to another group.</summary>
        public MdfChannelGroup FindParentChannelGroup(MdfChannel channel)
        {
            if (channel == null) { return null; }
            PendingChannel pending = channel.Pending;
            if (pending == null || pending.Group == null || pending.Group.DataGroupIndex != pending_.Sequence) { return null; }
            return PendingGroup.FacadeOf(pending.Group);
        }

        /// <summary>The channel group with that acquisition name, or null. The facade
        /// answers null for a name that is not set, because MDF 3.30 groups have no name at all.</summary>
        public MdfChannelGroup GetChannelGroup(string groupName)
        {
            if (groupName == null) { return null; }
            foreach (PendingGroup group in pending_.Groups)
            {
                if (group.Name == groupName) { return PendingGroup.FacadeOf(group); }
            }
            return null;
        }

        /// <summary>The channel groups of this data group. The facade knows exactly
        /// one, the group its channels are written with.</summary>
        public MdfChannelGroup[] ChannelGroups
        {
            get
            {
                MdfChannelGroup[] result = new MdfChannelGroup[pending_.Groups.Count];
                for (int i = 0; i < result.Length; i++) { result[i] = PendingGroup.FacadeOf(pending_.Groups[i]); }
                return result;
            }
        }

        /// <summary>The comment of the data group block. Neither MDF 3.30 nor MDF 4
        /// gives a DG a comment of its own, so the text belongs to the channel group.</summary>
        public string Description
        {
            get
            {
                return string.Empty;
            }
            set
            {
                if (value.Length == 0) { return; }
                throw new NotSupportedException("MDF stores no comment on a data group block; use the Description of the channel group instead.");
            }
        }

        /// <summary>The position of this data group in the file.</summary>
        public long Index
        {
            get
            {
                return pending_.Index;
            }
        }

        /// <summary>Whether the data of the group has been read into memory, which a
        /// writer's group never has.</summary>
        public bool IsRead
        {
            get
            {
                return pending_.Loaded;
            }
        }

        /// <summary>The MD block of the data group, which the writer never creates.</summary>
        public MdfMetaData MetaData
        {
            get
            {
                return null;
            }
        }

        /// <summary>The width of the record id. The engine derives it from the layout,
        /// so only the default can be read and set here.</summary>
        public byte RecordIdSize
        {
            get
            {
                return 0;
            }
            set
            {
                if (value == 0) { return; }
                throw new NotSupportedException("The managed writer derives the record id width from the number of channel groups, so RecordIdSize cannot be chosen.");
            }
        }
    }
}
