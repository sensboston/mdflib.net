// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// The file handle of the writer. Like the wrapper's MdfFile it owns the
// header object, forwards data group creation and answers the revision questions; the version string comes from
// the engine writer once the file exists and from the value the caller selected before that.
using System;
namespace MdfLibrary
{
    /// <summary>The file a writer is building.</summary>
    public class MdfFile
    {
        private readonly FacadeContext context_;
        private readonly MdfHeader header_;

        /// <summary>The file handle of one writer; the wrapper creates it with the writer, so neither
        /// side has a public constructor for this type.</summary>
        internal MdfFile(FacadeContext context)
        {
            context_ = context;
            header_ = new MdfHeader(context);
        }

        /// <summary>Adds an attachment to the file (MDF 4 only).</summary>
        public MdfAttachment CreateAttachment()
        {
            context_.RejectFlushed();
            if (context_.IsMdf3) { throw new NotSupportedException("MDF 3.30 has no attachments."); }
            PendingAttachment attachment = new PendingAttachment(context_);
            context_.Header.Attachments.Add(attachment);
            return PendingAttachment.FacadeOf(attachment);
        }

        /// <summary>Adds a data group with its first channel group to the tree.</summary>
        public MdfDataGroup CreateDataGroup()
        {
            context_.RejectFlushed();
            PendingDataGroup dataGroup = context_.AddDataGroup();
            dataGroup.Facade = new MdfDataGroup(dataGroup);
            context_.AddChannelGroup(dataGroup);
            return dataGroup.Facade;
        }

        /// <summary>The data group that owns the channel, or null when the channel is
        /// null or belongs to another file, which is where the wrapper's lookup returns nullptr.</summary>
        public MdfDataGroup FindParentDataGroup(MdfChannel channel)
        {
            if (channel == null) { return null; }
            PendingChannel pending = channel.Pending;
            if (pending == null || pending.Group == null) { return null; }
            return PendingDataGroup.FacadeOf(context_.DataGroups[pending.Group.DataGroupIndex]);
        }

        /// <summary>The attachments of the file, in the order they were created.</summary>
        public MdfAttachment[] Attachments
        {
            get
            {
                MdfAttachment[] result = new MdfAttachment[context_.Header.Attachments.Count];
                for (int i = 0; i < result.Length; i++) { result[i] = PendingAttachment.FacadeOf(context_.Header.Attachments[i]); }
                return result;
            }
        }

        /// <summary>The data groups of the file, in creation order.</summary>
        public MdfDataGroup[] DataGroups
        {
            get
            {
                MdfDataGroup[] result = new MdfDataGroup[context_.DataGroups.Count];
                for (int i = 0; i < result.Length; i++) { result[i] = PendingDataGroup.FacadeOf(context_.DataGroups[i]); }
                return result;
            }
        }

        /// <summary>The full file name. After the metadata blocks are written the
        /// engine writer owns the file, so only the same name can be set again.</summary>
        public string FileName
        {
            get
            {
                return context_.FileName ?? string.Empty;
            }
            set
            {
                SetFileName(value);
            }
        }

        /// <summary>Records the path the writer was given; the facade's own Init and the property share it.</summary>
        internal void SetFileName(string value)
        {
            if (context_.FileName == value) { return; }
            if (context_.TreeFlushed)
            { throw new InvalidOperationException("The file is created, so its name cannot change."); }
            context_.FileName = value;
            context_.Path = value;
        }

        /// <summary>True once FinalizeMeasurement has written the finalized
        /// identification; the flag is the facade's own, because the engine writer reports no such thing.</summary>
        public bool Finalized
        {
            get
            {
                return context_.Finalized;
            }
        }

        /// <summary>The header of this file, which the writer's own header property
        /// shares, exactly as the wrapper hands out the same object.</summary>
        public MdfHeader Header
        {
            get
            {
                return header_;
            }
        }

        /// <summary>Whether the file is an MDF 4 revision.</summary>
        public bool IsMdf4
        {
            get
            {
                return MainVersion == 4;
            }
        }

        /// <summary>The major revision digit of the file.</summary>
        public int MainVersion
        {
            get
            {
                int ignored;
                return SplitVersion(Version, out ignored);
            }
        }

        /// <summary>The revision of the file. The engine writes the exact revisions
        /// 3.30, 4.10 and 4.20, so only those can be selected, and only before the file is created.</summary>
        public int MinorVersion
        {
            get
            {
                int ignored;
                SplitVersion(Version, out ignored);
                return ignored;
            }
            set
            {
                int ignored;
                if (value == SplitVersion(Version, out ignored)) { return; }
                context_.SetRevision(MainVersion, value);
            }
        }

        private static int SplitVersion(string version, out int minor)
        {
            string[] parts = version.Split('.');
            // The out argument of a call the && may never reach stays unassigned, so both start at zero and
            // the flags below say whether the digits were there.
            int major = 0, minorValue = 0;
            bool hasMajor = parts.Length > 0 && int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out major);
            bool hasMinor = parts.Length > 1 && int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out minorValue);
            minor = hasMinor ? minorValue : 0;
            return hasMajor ? major : 0;
        }

        /// <summary>The file name without extension, which the wrapper keeps as its
        /// own property of the file object.</summary>
        public string Name
        {
            get
            {
                if (context_.DisplayName != null) { return context_.DisplayName; }
                return context_.FileName == null ? string.Empty : System.IO.Path.GetFileNameWithoutExtension(context_.FileName);
            }
            set
            {
                context_.DisplayName = value;
            }
        }

        /// <summary>The creating tool in the ID block. The value is written when
        /// InitMeasurement creates the file, as in MDF Library v2.3.0. The wrapper's reader has no such member.</summary>
        public string ProgramId
        {
            get
            {
                return context_.ProgramIdentifier;
            }
            set
            {
                context_.RejectFlushed();
                context_.ProgramIdentifier = value ?? string.Empty;
            }
        }

        /// <summary>The exact revision string of the file.</summary>
        public string Version
        {
            get
            {
                return context_.Writer != null ? context_.Writer.Version : context_.VersionText;
            }
        }
    }
}
