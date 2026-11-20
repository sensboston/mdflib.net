// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;

namespace MdfLib
{
    /// <summary>A bus system of ASAM MDF 4 bus logging; values are the SI block's si_bus_type codes.</summary>
    internal enum MdfBusType
    {
        /// <summary>CAN and CAN FD.</summary>
        Can = 2,
        /// <summary>LIN.</summary>
        Lin = 3,
        /// <summary>MOST.</summary>
        Most = 4,
        /// <summary>FlexRay.</summary>
        FlexRay = 5,
        /// <summary>Ethernet.</summary>
        Ethernet = 7
    }

    /// <summary>The ASAM bus logging frame kinds that <see cref="MdfBusLogger"/> writes and <see cref="MdfBusReader"/> reads.</summary>
    internal enum MdfBusMessageType
    {
        /// <summary>CAN_DataFrame (classic or FD).</summary>
        CanDataFrame,
        /// <summary>CAN_RemoteFrame.</summary>
        CanRemoteFrame,
        /// <summary>CAN_ErrorFrame.</summary>
        CanErrorFrame,
        /// <summary>LIN_Frame.</summary>
        LinFrame,
        /// <summary>ETH_Frame.</summary>
        EthernetFrame,
        /// <summary>FLX_Frame.</summary>
        FlexRayFrame,
        /// <summary>CAN_OverloadFrame.</summary>
        CanOverloadFrame,
        /// <summary>LIN_ChecksumError.</summary>
        LinChecksumError,
        /// <summary>LIN_ReceiveError.</summary>
        LinReceiveError,
        /// <summary>LIN_SyncError.</summary>
        LinSyncError,
        /// <summary>LIN_WakeUp.</summary>
        LinWakeUp,
        /// <summary>LIN_TransmissionError.</summary>
        LinTransmissionError,
        /// <summary>LIN_Spike.</summary>
        LinSpike,
        /// <summary>LIN_LongDom (long dominant signal).</summary>
        LinLongDominant,
        /// <summary>ETH_ChecksumError.</summary>
        EthernetChecksumError,
        /// <summary>ETH_LengthError.</summary>
        EthernetLengthError,
        /// <summary>ETH_ReceiveError.</summary>
        EthernetReceiveError,
        /// <summary>FLX_Pdu.</summary>
        FlexRayPdu,
        /// <summary>FLX_FrameHeader (payload not logged; optional fill data).</summary>
        FlexRayFrameHeader,
        /// <summary>FLX_NullFrame.</summary>
        FlexRayNullFrame,
        /// <summary>FLX_ErrorFrame.</summary>
        FlexRayErrorFrame,
        /// <summary>FLX_Symbol.</summary>
        FlexRaySymbol,
        /// <summary>MOST_Message (control message).</summary>
        MostMessage,
        /// <summary>MOST_Packet (asynchronous packet).</summary>
        MostPacket,
        /// <summary>MOST_EthernetPacket.</summary>
        MostEthernetPacket,
        /// <summary>MOST_SignalState.</summary>
        MostSignalState,
        /// <summary>MOST_MaxPosInfo.</summary>
        MostMaxPosInfo,
        /// <summary>MOST_BoundDesc (boundary descriptor).</summary>
        MostBoundaryDescriptor,
        /// <summary>MOST_AllocTable (allocation table; the table bytes are the payload).</summary>
        MostAllocationTable,
        /// <summary>MOST_SysLockState.</summary>
        MostSystemLockState,
        /// <summary>MOST_ShutdownFlag.</summary>
        MostShutdownFlag
    }

    /// <summary>The frame direction (the ASAM Dir signal).</summary>
    internal enum MdfBusDirection
    {
        /// <summary>Received.</summary>
        Rx = 0,
        /// <summary>Transmitted.</summary>
        Tx = 1
    }

    /// <summary>One bus frame as stored in an ASAM MDF 4 bus logging channel group. Members that the frame kind
    /// does not use stay zero/false; the table on each member names the kinds that store it.</summary>
    internal sealed class MdfBusMessage
    {
        /// <summary>Gets or sets the frame kind.</summary>
        public MdfBusMessageType Type { get; set; }
        /// <summary>Gets or sets the time in seconds relative to the measurement start (master channel t).</summary>
        public double Timestamp { get; set; }
        /// <summary>Gets or sets the logical bus channel number (BusChannel).</summary>
        public byte BusChannel { get; set; }
        /// <summary>Gets or sets the frame identifier: CAN 11/29 bits, LIN 6 bits, FlexRay slot 11 bits; unused for Ethernet.</summary>
        public uint Id { get; set; }
        /// <summary>Gets or sets whether a CAN identifier is a 29-bit extended identifier (IDE).</summary>
        public bool IsExtendedId { get; set; }
        /// <summary>Gets or sets the direction (Dir).</summary>
        public MdfBusDirection Direction { get; set; }
        /// <summary>Gets or sets the payload (DataBytes); null is an empty payload. Remote frames carry none.</summary>
        public byte[] Data { get; set; }
        /// <summary>Gets or sets the CAN data length code. Derived from <see cref="Data"/> when writing data and error
        /// frames; set explicitly for remote frames. Readers always fill it for CAN.</summary>
        public byte Dlc { get; set; }
        /// <summary>Gets or sets whether a CAN frame uses the FD format (EDL).</summary>
        public bool IsFd { get; set; }
        /// <summary>Gets or sets the CAN FD bit rate switch (BRS).</summary>
        public bool BitRateSwitch { get; set; }
        /// <summary>Gets or sets the CAN FD error state indicator (ESI).</summary>
        public bool ErrorStateIndicator { get; set; }
        /// <summary>Gets or sets the CAN error type 0..7 (CAN_ErrorFrame.ErrorType: 0 unknown, 1 bit, 2 form, 3 stuffing,
        /// 4 ACK, 5 CRC, 6 other).</summary>
        public byte ErrorType { get; set; }
        /// <summary>Gets or sets the bit position of a CAN error (CAN_ErrorFrame.BitPosition).</summary>
        public ushort BitPosition { get; set; }
        /// <summary>Gets or sets the CRC: CAN/Ethernet 32 bits, FlexRay 24 bits.</summary>
        public uint Crc { get; set; }
        /// <summary>Gets or sets the frame duration in nanoseconds (CAN FrameDuration, FlexRay FrameLength).</summary>
        public uint FrameDuration { get; set; }
        /// <summary>Gets or sets the LIN checksum.</summary>
        public byte Checksum { get; set; }
        /// <summary>Gets or sets the source address: Ethernet frames store the 48-bit MAC as a 6-byte array, most significant
        /// byte first (as transmitted); MOST stores a 16-bit (messages, packets) or 48-bit (Ethernet packets) integer.</summary>
        public ulong Source { get; set; }
        /// <summary>Gets or sets the destination address, stored like <see cref="Source"/>.</summary>
        public ulong Destination { get; set; }
        /// <summary>Gets or sets the Ethernet EtherType.</summary>
        public ushort EtherType { get; set; }
        /// <summary>Gets or sets the FlexRay cycle counter 0..63.</summary>
        public byte Cycle { get; set; }
        /// <summary>Gets or sets the FlexRay channel: 0 = A, 1 = B (FlxChannel).</summary>
        public byte FlexRayChannel { get; set; }

        /// <summary>Gets the values of frame members without a dedicated property, keyed by the ASAM member name after the
        /// frame name (for example "SRR", "ChecksumModel", "Baudrate", "SOF", "HeaderCRC", "PIndex"). When writing, an entry
        /// overrides the derived value of any member except the payload and its length; unknown names, invalid values and
        /// values outside the member's range are refused. Readers fill every such member that the file stores.</summary>
        public IDictionary<string, MdfSampleValue> Fields { get { return fields; } }
        private Dictionary<string, MdfSampleValue> fields = new Dictionary<string, MdfSampleValue>(StringComparer.Ordinal);

        // an independent copy for queued writing (the caller may reuse the instance).
        internal MdfBusMessage Snapshot()
        {
            MdfBusMessage copy = (MdfBusMessage)MemberwiseClone();
            copy.Data = Data == null ? null : (byte[])Data.Clone();
            copy.fields = new Dictionary<string, MdfSampleValue>(fields, StringComparer.Ordinal);
            return copy;
        }

        /// <summary>Gets the CAN FD payload length of a data length code 0..15 (12, 16, 20, 24, 32, 48, 64 above 8).</summary>
        public static int CanFdLength(int dlc)
        {
            if (dlc < 0 || dlc > 15) { throw new ArgumentOutOfRangeException(nameof(dlc)); }
            return dlc <= 8 ? dlc : FdLengths[dlc - 9];
        }

        private static readonly int[] FdLengths = { 12, 16, 20, 24, 32, 48, 64 };

        internal static int CanFdCode(int length)
        {
            if (length <= 8) { return length; }
            int index = Array.IndexOf(FdLengths, length);
            return index < 0 ? -1 : index + 9;
        }
    }
}
