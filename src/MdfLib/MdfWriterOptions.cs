// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
namespace MdfLib
{
    /// <summary>Finite settings copied before a writer takes ownership of an empty destination.</summary>
    internal sealed class MdfWriterOptions
    {
        /// <summary>Gets or sets the exact output revision: 3.30, 4.10 (default) or 4.20.</summary>
        public string Version { get; set; } = "4.10";
        /// <summary>Gets or sets exact UTC nanoseconds since the Unix epoch; default zero, never the current clock.</summary>
        public ulong StartTimeNanoseconds { get; set; }
        /// <summary>Gets or sets the descriptor-plus-wire-prefix allowance; default 16 MiB, maximum Int32.MaxValue.</summary>
        public long MaximumMetadataBytes { get; set; } = 16777216;
        /// <summary>Gets or sets the maximum single normalized record buffer; default 1 MiB.</summary>
        public int MaximumRecordBytes { get; set; } = 1048576;
        /// <summary>Gets or sets the maximum channel count; default 4096.</summary>
        public int MaximumChannelCount { get; set; } = 4096;
        /// <summary>Gets or sets the maximum encoded name or unit including its NUL terminator; default 1 MiB.</summary>
        public int MaximumTextBytes { get; set; } = 1048576;
        /// <summary>Gets or sets whether MDF 4 record data is written as zlib DZ blocks under a DL list (managed encoder); default false.
        /// MDF 3.30 has no compressed storage and refuses this option.</summary>
        public bool CompressData { get; set; }

        /// <summary>Gets or sets whether the identification follows MDF Library v2.3.0: the MDF 4 program identifier is empty,
        /// the MDF 3 one is "MdfWrite", and the MDF 3 timer identification stays empty. False (default) keeps the engine's
        /// own "MdfLib" identification. The facade always sets it.</summary>
        public bool UpstreamIdentification { get; set; }

        /// <summary>Gets or sets the ID block's prg_id: up to eight ASCII characters, padded with zero bytes. Null (default)
        /// takes the identification UpstreamIdentification selects.</summary>
        public string ProgramIdentifier { get; set; }
        /// <summary>Gets or sets the uncompressed bytes per DZ block (and per DT block of multi-group output), rounded down to
        /// whole records (at least one); default 256 KiB. Readers inflate one whole block at a time, so keep it within their
        /// read-buffer limit.</summary>
        public int CompressionBlockBytes { get; set; } = 262144;
        /// <summary>Gets or sets the record write buffer in bytes; default 0 (every record is written at once). A positive value
        /// collects records of single-group, uncompressed output (MDF 3 and 4) and writes them in one call when full and at
        /// <see cref="MdfWriter.StopMeasurement"/>; records still buffered when an unstopped writer is disposed are lost.</summary>
        public int WriteBufferBytes { get; set; }
        /// <summary>Gets or sets the measurement description: the MDF 4 HDcomment TX element or the MDF 3 HD comment TX block;
        /// default null (none). Like all metadata texts it must consist of XML 1.0 characters (no NUL or other controls except
        /// tab, line feed and carriage return); MDF 3.30 requires Latin-1.</summary>
        public string Comment { get; set; }
        /// <summary>Gets or sets the further HDcomment common properties, beyond author, department, project and
        /// subject; MDF 4 only, and MDF 3.30 refuses them. Default null.</summary>
        public System.Collections.Generic.IReadOnlyList<MdfLib.MdfCommonProperty> CommonProperties { get; set; }
        /// <summary>Gets or sets the common properties of the file-history block's FHcomment; MDF 4 only, and MDF 3.30
        /// refuses them. The block is written when a tool field, the description or a property is set. Default null.</summary>
        public System.Collections.Generic.IReadOnlyList<MdfLib.MdfCommonProperty> HistoryCommonProperties { get; set; }

        /// <summary>Gets or sets the author: MDF 4 HDcomment common property "author", MDF 3 HD field (at most 31 Latin-1
        /// bytes); default null.</summary>
        public string Author { get; set; }
        /// <summary>Gets or sets the organization or department: MDF 4 common property "department", MDF 3 HD field; default null.</summary>
        public string Organization { get; set; }
        /// <summary>Gets or sets the project: MDF 4 common property "project", MDF 3 HD field; default null.</summary>
        public string Project { get; set; }
        /// <summary>Gets or sets the subject (measurement object): MDF 4 common property "subject", MDF 3 HD field; default null.</summary>
        public string Subject { get; set; }
        /// <summary>Gets or sets the creating tool of the MDF 4 file-history entry (FHcomment tool_id). An FH block, stamped
        /// with <see cref="StartTimeNanoseconds"/>, is written when any history option is set; tool_id then defaults to
        /// "MdfLib". MDF 3 has no file history: the history options are validated but not stored. Ignored by Append.</summary>
        public string ToolIdentifier { get; set; }
        /// <summary>Gets or sets the tool vendor of the MDF 4 file-history entry (FHcomment tool_vendor); default null.</summary>
        public string ToolVendor { get; set; }
        /// <summary>Gets or sets the tool version of the MDF 4 file-history entry (FHcomment tool_version); default null.</summary>
        public string ToolVersion { get; set; }
        /// <summary>Gets or sets the user of the MDF 4 file-history entry (FHcomment user_name); default null.</summary>
        public string UserName { get; set; }
        /// <summary>Gets or sets the description of the MDF 4 file-history entry (FHcomment TX); default null.</summary>
        public string HistoryComment { get; set; }

        /// <summary>Gets or sets the UTC time of the file-history block in nanoseconds; null uses StartTimeNanoseconds.</summary>
        public ulong? HistoryTimeNanoseconds { get; set; }

        /// <summary>Gets or sets the measurement id, the MDF 4 common property "Measurement.UUID" of the HD block; default null. MDF 3 has none: ignored.</summary>
        public string MeasurementId { get; set; }

        /// <summary>Gets or sets the recorder id, the MDF 4 common property "Recorder.UUID" of the HD block; default null. MDF 3 has none: ignored.</summary>
        public string RecorderId { get; set; }

        /// <summary>Gets or sets the recorder index, the MDF 4 common property "Recorder.FileIndex" of the HD block; null leaves it out. MDF 3 has none: ignored.</summary>
        public long? RecorderIndex { get; set; }

        /// <summary>Gets or sets the HD start angle in radians; null leaves the start angle out (hd_flags bit 0x01). MDF 3 has none: ignored.</summary>
        public double? StartAngle { get; set; }

        /// <summary>Gets or sets the HD start distance in meters; null leaves the start distance out (hd_flags bit 0x02). MDF 3 has none: ignored.</summary>
        public double? StartDistance { get; set; }
    }
}
