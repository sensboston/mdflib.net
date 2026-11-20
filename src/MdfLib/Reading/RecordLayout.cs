// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
namespace MdfLib.Reading
{
    // Physical details stay internal; public entities do not inherit wire block classes.
    internal sealed class RecordLayout
    {
        internal ulong DataAddress { get; set; }
        internal ulong BlockAddress { get; set; }
        internal uint DataBytes { get; set; }
        internal uint InvalidationBytes { get; set; }
        internal bool HeaderlessData { get; set; }
        internal Mdf4DataMap DataMap { get; set; }
        internal Mdf4ListDataMap ListDataMap { get; set; }
        internal bool IsMdf420 { get; set; }
        internal bool HasRemoteMaster { get; set; }
        internal bool HasStoredMasterChannel { get; set; }
        internal ulong RemoteMasterAddress { get; set; }
        internal ulong RecordId { get; set; }
        internal InterleavedLayout Interleaved { get; set; }
        // a VLSD CG (flag bit 0) whose records are UInt32 length + bytes; it has no channels or fixed record.
        internal bool IsVlsdGroup { get; set; }
    }

    internal sealed class ChannelLayout
    {
        internal uint ByteOffset { get; set; }
        internal int ElementByteStride { get; set; }
        // per-element invalidation-bit stride (ca_inval_bit_pos_base); 0 = one shared CN bit.
        internal uint ElementInvalidationStride { get; set; }
        // inverse (column-major) layout: dimension sizes and column-major weights; null = row-major storage.
        internal ulong[] ElementDimensions { get; set; }
        internal ulong[] ElementColumnWeights { get; set; }
        internal int BitOffset { get; set; }
        internal int BitCount { get; set; }
        internal byte DataType { get; set; }
        // The stored cn_type and cn_data_type as the file has them (DataType above is normalized for the decoder);
        // the facade reads them to report the channel type and data type the wrapper reports.
        internal byte CnType { get; set; }
        internal byte CnDataType { get; set; }
        // complex (MDF 4 data types 15/16) stored as DataType 4/5 with BitCount = the width of one part.
        internal bool Complex { get; set; }
        internal uint Flags { get; set; }
        internal uint InvalidationBit { get; set; }
        // text/byte: 0 = numeric, 1 = fixed in-record field, 2 = VLSD offset into a logical SD stream,
        // 3 = maximum-length (MLSD) in-record field whose first N bytes are used, N = SizeChannel's raw value.
        internal int TextStorage { get; set; }
        internal uint FieldBytes { get; set; }
        internal ulong SignalDataAddress { get; set; }
        internal Mdf4DataMap SignalData { get; set; }
        // the VLSD data link addresses a CG; resolved per DG to the referenced VLSD CG's layout.
        internal bool SignalDataIsGroup { get; set; }
        internal RecordLayout SignalGroup { get; set; }
        // virtual master/data channel; raw value = zero-based record index of its channel group.
        internal bool Virtual { get; set; }
        // supported cn_type 4 (stored numeric value whose cn_data is an AT block); decoded like cn_type 0.
        internal bool Synchronization { get; set; }
        // MLSD cn_data (address of the size CN; never dereferenced) and the resolved same-CG top-level size channel.
        internal ulong SizeAddress { get; set; }
        internal MdfChannel SizeChannel { get; set; }
    }
}
