// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// The source information handle. The wrapper edits a live SI block; the facade
// collects the same texts and codes and gives them to the engine as MdfSourceDefinition, whose SI block MDF 3.30
// does not have at all - the refusal then comes with the file, together with the rest of the tree.
using System;
namespace MdfLibrary
{
    /// <summary>One SI block of a file.</summary>
    public class MdfSourceInformation
    {
        private readonly FacadeContext context_;
        private readonly PendingSource pending_;

        /// <summary>The handle of one pending SI block; only the facade's factories create it.</summary>
        internal MdfSourceInformation(FacadeContext context, PendingSource pending)
        {
            context_ = context;
            pending_ = pending;
        }

        /// <summary>The si_flags bit of a simulated source.</summary>
        public const byte Simulated = 1;

        /// <summary>The managed writer writes no MD blocks at all.</summary>
        public MdfMetaData CreateMetaData()
        {
            context_.RejectFlushed();
            if (pending_.Metadata == null) { pending_.Metadata = new MdfMetaData(); }
            return pending_.Metadata;
        }

        /// <summary>Si_bus_type, the bus the source reads from.</summary>
        public BusType Bus
        {
            get
            {
                return pending_.BusType < 0 ? BusType.None : (BusType)pending_.BusType;
            }
            set
            {
                context_.RejectFlushed();
                pending_.BusType = (int)value;
            }
        }

        /// <summary>The description of the source, stored as the SIcomment block.</summary>
        public string Description
        {
            get
            {
                return pending_.Comment;
            }
            set
            {
                context_.RejectFlushed();
                pending_.Comment = value ?? string.Empty;
            }
        }

        /// <summary>Si_flags. The file's value when the source was read; otherwise 0, as in
        /// MDF Library v2.3.0, until it is set. The facade writes the value as it is, so the bits can be set by hand.</summary>
        public byte Flags
        {
            get
            {
                return pending_.SiFlags;
            }
            set
            {
                context_.RejectFlushed();
                pending_.SiFlags = value;
            }
        }

        /// <summary>The id of the live block, which the facade numbers as it creates
        /// them; the file has no such field.</summary>
        public long Index
        {
            get
            {
                return pending_.Index;
            }
        }

        /// <summary>The MD block of the source, which the writer never creates.</summary>
        public MdfMetaData MetaData
        {
            get
            {
                return pending_.Metadata;
            }
        }

        /// <summary>Si_name, the name of the source. An SI block without a name is not
        /// written at all by the engine, so the whole block stays away from the file.</summary>
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

        /// <summary>Si_path, the path of the source; the separator the group records
        /// follows from it.</summary>
        public string Path
        {
            get
            {
                return pending_.Path;
            }
            set
            {
                context_.RejectFlushed();
                pending_.Path = value ?? string.Empty;
            }
        }

        /// <summary>Si_type, what kind of source this is.</summary>
        public SourceType Type
        {
            get
            {
                return pending_.SourceType < 0 ? SourceType.Other : (SourceType)pending_.SourceType;
            }
            set
            {
                context_.RejectFlushed();
                pending_.SourceType = (int)value;
            }
        }
    }

    /// <summary>The kind of a source (si_type).</summary>
    public enum SourceType : byte
    {
        /// <summary>Some other source.</summary>
        Other = 0,

        /// <summary>An ECU.</summary>
        Ecu = 1,

        /// <summary>A bus.</summary>
        Bus = 2,

        /// <summary>An I/O device.</summary>
        IoDevice = 3,

        /// <summary>A software tool.</summary>
        Tool = 4,

        /// <summary>The user.</summary>
        User = 5,
    }


    /// <summary>The bus a source is attached to (si_bus_type).</summary>
    public enum BusType : byte
    {
        /// <summary>No bus.</summary>
        None = 0,

        /// <summary>Some other bus.</summary>
        Other = 1,

        /// <summary>CAN.</summary>
        Can = 2,

        /// <summary>LIN.</summary>
        Lin = 3,

        /// <summary>MOST.</summary>
        Most = 4,

        /// <summary>FlexRay.</summary>
        FlexRay = 5,

        /// <summary>K-Line.</summary>
        Kline = 6,

        /// <summary>Ethernet.</summary>
        Ethernet = 7,

        /// <summary>USB.</summary>
        Usb = 8,
    }
}
