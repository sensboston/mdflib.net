// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// The channel group handle. The wrapper mutates a live cg block; the facade
// collects the same values and refuses at the moment the file is written whatever the engine cannot store, so a
// flag that MDF 3.30 or the managed writer has no field for is reported instead of dropped.
using System;
namespace MdfLibrary
{
    /// <summary>One channel group of a file.</summary>
    public class MdfChannelGroup
    {
        private readonly FacadeContext context_;
        private readonly PendingGroup pending_;

        /// <summary>The tree node behind this handle; the read side and the observers reach the samples through it.</summary>
        internal PendingGroup Pending { get { return pending_; } }

        /// <summary>The handle of one pending channel group; the facade's factories create it, the wrapper has
        /// no public constructor for this type either.</summary>
        internal MdfChannelGroup(FacadeContext context, PendingGroup pending)
        {
            context_ = context;
            pending_ = pending;
        }

        /// <summary>The cg_flags bit of variable length signal data.</summary>
        public const ushort VlsdChannel = 1;

        /// <summary>The cg_flags bit of a bus event group.</summary>
        public const ushort BusEvent = 2;

        /// <summary>The cg_flags bit of a plain bus event group.</summary>
        public const ushort PlainBusEvent = 4;

        /// <summary>The cg_flags bit of a remote master group (MDF 4.20).</summary>
        public const ushort RemoteMaster = 8;

        /// <summary>The cg_flags bit of an event signal group.</summary>
        public const ushort EventSignal = 16;

        /// <summary>Adds a channel to the end of this group and returns its handle.</summary>
        public MdfChannel CreateChannel()
        {
            context_.RejectFlushed();
            PendingChannel channel = new PendingChannel(context_, pending_);
            pending_.Channels.Add(channel);
            channel.Facade = new MdfChannel(context_, channel);
            return channel.Facade;
        }

        /// <summary>The managed writer writes no MD blocks at all.</summary>
        public MdfMetaData CreateMetaData()
        {
            context_.RejectFlushed();
            if (pending_.Metadata == null) { pending_.Metadata = new MdfMetaData(); }
            return pending_.Metadata;
        }

        /// <summary>Creates the SI block of this group, or reports the one it already
        /// has, as the wrapper does. MDF 3.30 refuses the group source when the file is written.</summary>
        public MdfSourceInformation CreateSourceInformation()
        {
            context_.RejectFlushed();
            if (pending_.Source == null)
            {
                pending_.Source = new PendingSource(context_);
                pending_.Source.Index = context_.NewIndex();
                pending_.Source.Facade = new MdfSourceInformation(context_, pending_.Source);
            }
            return pending_.Source.Facade;
        }

        /// <summary>The master channel of this group, which is the X of every channel
        /// it holds, or null while the group has no master yet.</summary>
        public MdfChannel GetXChannel(MdfChannel refChannel)
        {
            if (refChannel == null) { return null; }
            foreach (PendingChannel channel in pending_.Channels)
            {
                if (channel.Type == ChannelType.Master) { return PendingChannel.FacadeOf(channel); }
            }
            return null;
        }

        /// <summary>The channels of this group, in the order they were created.</summary>
        public MdfChannel[] Channels
        {
            get
            {
                MdfChannel[] result = new MdfChannel[pending_.Channels.Count];
                for (int i = 0; i < result.Length; i++) { result[i] = PendingChannel.FacadeOf(pending_.Channels[i]); }
                return result;
            }
        }

        /// <summary>The comment of the group: the MDF 4 CGcomment block, which MDF 3.30
        /// does not have.</summary>
        public string Description
        {
            get
            {
                return pending_.Description;
            }
            set
            {
                context_.RejectFlushed();
                pending_.Description = value ?? string.Empty;
            }
        }

        /// <summary>Cg_flags. The managed writer sets the bits of a raw bus group
        /// itself, so an ordinary group cannot carry any.</summary>
        public ushort Flags
        {
            get
            {
                return pending_.Flags;
            }
            set
            {
                context_.RejectFlushed();
                pending_.Flags = value;
            }
        }

        /// <summary>The position of this group inside its data group, which the facade
        /// always keeps at zero because it pairs one group with one data group.</summary>
        public long Index
        {
            get
            {
                return pending_.Index;
            }
        }

        /// <summary>The MD block of the group, which the writer never creates.</summary>
        public MdfMetaData MetaData
        {
            get
            {
                return null;
            }
        }

        /// <summary>The acquisition name of the group. MDF 3.30 has no name for a
        /// channel group, so it is refused there when the file is written.</summary>
        public string Name
        {
            get
            {
                return pending_.Name;
            }
            set
            {
                context_.RejectFlushed();
                pending_.Name = value ?? string.Empty;
            }
        }

        /// <summary>The samples written so far. The wrapper lets a bus log declare the
        /// count it will write in advance, which the managed writer cannot.</summary>
        public ulong NofSamples
        {
            get
            {
                if (context_.Reading) { return pending_.SampleCount; }
                if (context_.BusLogger != null) { return pending_.BusFrames; }
                MdfLib.MdfWriter writer = context_.Writer;
                return writer == null ? 0UL : writer.GetSampleCount(pending_.EngineIndex);
            }
            set
            {
                if (value == 0) { return; }
                throw new NotSupportedException("The number of samples of a channel group is what its caller writes; it cannot be declared in advance.");
            }
        }

        /// <summary>Cg_path_separator, which the engine derives from the source path of
        /// the group.</summary>
        public char PathSeparator
        {
            get
            {
                return pending_.PathSeparator == 0 ? '/' : pending_.PathSeparator;
            }
            set
            {
                context_.RejectFlushed();
                pending_.PathSeparator = value;
            }
        }

        /// <summary>The record id of the group, which the engine assigns from its
        /// layout.</summary>
        public ulong RecordId
        {
            get
            {
                return pending_.RecordId;
            }
            set
            {
                context_.RejectFlushed();
                pending_.RecordId = value;
            }
        }

        /// <summary>The SI block of the group, or null while none was created.</summary>
        public MdfSourceInformation SourceInformation
        {
            get
            {
                return pending_.Source == null ? null : PendingSource.FacadeOf(context_, pending_.Source);
            }
        }
    }
}
