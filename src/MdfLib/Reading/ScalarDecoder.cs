// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using MdfLib.IO;

namespace MdfLib.Reading
{
    internal static class ScalarDecoder
    {
        // Layout validation is done once by the metadata codec. Byte ranges still pass checked IO primitives.
        internal static MdfSampleValue Read(byte[] buffer, int recordOffset, RecordLayout record, MdfChannel channel,
            MdfValueRepresentation representation, ulong recordIndex, int elementIndex = 0)
        {
            ChannelLayout layout = channel.Layout;
            if (layout.Virtual)
            {
                // no record field and no invalidation; raw = absolute zero-based record index of the channel group.
                MdfSampleValue index = new MdfSampleValue(MdfValueKind.UnsignedInteger, true, recordIndex, 0);
                if (representation == MdfValueRepresentation.Raw || channel.Conversion.Kind == MdfConversionKind.Identity) { return index; }
                if (channel.Conversion.Kind == MdfConversionKind.BitfieldToText) { return PhysicalConversion.Bitfield(channel.Conversion, recordIndex, true); }
                if (PhysicalConversion.HasTextOutput(channel.Conversion.Kind)) { return PhysicalConversion.ToText(channel.Conversion, recordIndex, true, true); }
                return new MdfSampleValue(MdfValueKind.FloatingPoint, true, 0,
                    PhysicalConversion.Evaluate(channel.Conversion, recordIndex, true));
            }
            // Array count/stride/extent/invalidation bits are metadata-validated; one whole record was buffer-bounded eagerly.
            ulong stored = StoredIndex(layout, elementIndex);
            ulong byteOffset = (ulong)layout.ByteOffset + stored * (uint)layout.ElementByteStride;
            bool valid = (layout.Flags & 1) == 0;
            if (valid && (layout.Flags & 2) != 0)
            {
                ulong bit = (ulong)layout.InvalidationBit + stored * layout.ElementInvalidationStride;
                int invalidationByte = checked(recordOffset + (int)record.DataBytes + (int)(bit / 8));
                valid = (buffer[invalidationByte] & (1 << (int)(bit & 7))) == 0;
            }
            ByteOrder order = (layout.DataType & 1) == 0 ? ByteOrder.LittleEndian : ByteOrder.BigEndian;
            MdfSampleValue raw;
            if (layout.DataType >= 4)
            {
                int position = checked(recordOffset + (int)byteOffset);
                if (layout.Complex)
                {
                    // real part then imaginary part, each IEEE of BitCount bits in the channel's byte order.
                    return MdfSampleValue.Complex(Part(buffer, position, layout.BitCount, order),
                        Part(buffer, checked(position + layout.BitCount / 8), layout.BitCount, order), valid);
                }
                // byte-aligned IEEE binary16/32/64 only (metadata codec); binary16 widens exactly.
                double value = layout.BitCount == 16 ? EndianBitConverter.ReadHalf(buffer, position, order)
                    : layout.BitCount == 32 ? EndianBitConverter.ReadSingle(buffer, position, order)
                    : EndianBitConverter.ReadDouble(buffer, position, order);
                raw = new MdfSampleValue(MdfValueKind.FloatingPoint, valid, 0, value);
            }
            else
            {
                long start = ((long)recordOffset + (long)byteOffset) * 8;
                if (order == ByteOrder.LittleEndian) { start += layout.BitOffset; }
                else
                {
                    // CN's BE bit offset is an LSB shift of the minimal big-endian byte container,
                    // not an MSB-first position. A 64-bit value can therefore span nine bytes.
                    int span = (layout.BitOffset + layout.BitCount + 7) / 8;
                    start += span * 8 - layout.BitOffset - layout.BitCount;
                }
                if (layout.DataType <= 1)
                {
                    raw = new MdfSampleValue(MdfValueKind.UnsignedInteger, valid,
                        EndianBitConverter.ReadUnsignedBits(buffer, start, layout.BitCount, order), 0);
                }
                else
                {
                    raw = new MdfSampleValue(MdfValueKind.SignedInteger, valid,
                        unchecked((ulong)EndianBitConverter.ReadSignedBits(buffer, start, layout.BitCount, order)), 0);
                }
            }
            if (representation == MdfValueRepresentation.Raw || channel.Conversion.Kind == MdfConversionKind.Identity) { return raw; }
            // integer raw only (ConversionMismatch refuses IEEE input); signed values are already sign-extended.
            if (channel.Conversion.Kind == MdfConversionKind.BitfieldToText)
            {
                ulong bits = raw.Kind == MdfValueKind.UnsignedInteger ? raw.UnsignedValue : unchecked((ulong)raw.SignedValue);
                return PhysicalConversion.Bitfield(channel.Conversion, bits, valid);
            }
            double numeric = raw.Kind == MdfValueKind.UnsignedInteger ? raw.UnsignedValue
                : raw.Kind == MdfValueKind.SignedInteger ? raw.SignedValue : raw.FloatingPointValue;
            if (PhysicalConversion.HasTextOutput(channel.Conversion.Kind))
            { return PhysicalConversion.ToText(channel.Conversion, numeric, raw.Kind != MdfValueKind.FloatingPoint, valid); }
            return new MdfSampleValue(MdfValueKind.FloatingPoint, valid, 0,
                PhysicalConversion.Evaluate(channel.Conversion, numeric, raw.Kind != MdfValueKind.FloatingPoint));
        }

        private static double Part(byte[] buffer, int position, int bits, ByteOrder order)
        {
            return bits == 16 ? EndianBitConverter.ReadHalf(buffer, position, order)
                : bits == 32 ? EndianBitConverter.ReadSingle(buffer, position, order)
                : EndianBitConverter.ReadDouble(buffer, position, order);
        }

        internal static ulong StoredIndex(ChannelLayout layout, int elementIndex)
        {
            return layout.ElementColumnWeights == null ? (ulong)elementIndex : Stored(layout, (ulong)elementIndex);
        }

        // inverse layout: row-major element index -> column-major stored index, O(rank), no allocation.
        internal static ulong Stored(ChannelLayout layout, ulong element)
        {
            ulong[] dimensions = layout.ElementDimensions;
            ulong[] weights = layout.ElementColumnWeights;
            ulong stored = 0;
            for (int axis = dimensions.Length - 1; axis >= 0; axis--)
            {
                stored += element % dimensions[axis] * weights[axis];
                element /= dimensions[axis];
            }
            return stored;
        }
    }
}
