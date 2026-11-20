// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// The CAN frame as the
// wrapper's CanMessage holds it. It is a plain managed model. Its length rules follow the wrapper: the payload
// length is DataLength, and Dlc is the code of that length; a remote frame keeps Dlc with no payload. The
// bus logger checks the frame against the frame kind when it writes it (MdfWriter.SaveCanMessage).
using System;
namespace MdfLibrary
{
    /// <summary>The error a CAN error frame reports.</summary>
    public enum CanErrorType : byte
    {
        /// <summary>No error type recorded.</summary>
        UNKNOWN_ERROR = 0,

        /// <summary>Bit error.</summary>
        BIT_ERROR = 1,

        /// <summary>Form error.</summary>
        FORM_ERROR = 2,

        /// <summary>Bit stuffing error.</summary>
        BIT_STUFFING_ERROR = 3,

        /// <summary>CRC error.</summary>
        CRC_ERROR = 4,

        /// <summary>ACK error.</summary>
        ACK_ERROR = 5,
    }

    /// <summary>The kind of a CAN frame.</summary>
    public enum CanMessageType : int
    {
        /// <summary>A data frame (the default).</summary>
        Can_DataFrame = 0,

        /// <summary>A remote frame: a request for a frame with the length Dlc.</summary>
        Can_RemoteFrame = 1,

        /// <summary>An error frame.</summary>
        Can_ErrorFrame = 2,

        /// <summary>An overload frame, which carries no identifier or payload.</summary>
        Can_OverloadFrame = 3,
    }

    /// <summary>One CAN frame.</summary>
    public class CanMessage
    {
        // The CAN FD payload lengths, indexed by Dlc; a classic frame uses the first nine.
        private static readonly int[] LengthOfDlc = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 12, 16, 20, 24, 32, 48, 64 };

        private uint id_;
        private byte[] data_ = new byte[0];
        private byte dlc_;

        /// <summary>Creates an empty data frame.</summary>
        public CanMessage()
        {
        }

        /// <summary>The bit position of a CAN error, for an error frame.</summary>
        public ushort BitPosition { get; set; }

        /// <summary>The CAN FD bit rate switch (BRS).</summary>
        public bool Brs { get; set; }

        /// <summary>The logical bus channel the frame was seen on.</summary>
        public byte BusChannel { get; set; }

        /// <summary>The identifier of the frame; the same value as MessageId.</summary>
        public uint CanId
        {
            get { return id_; }
        }

        /// <summary>The CRC of the frame. The v2.3.0 bus log does not store it; SaveCanMessage ignores it silently.</summary>
        public uint Crc { get; set; }

        /// <summary>The payload. A copy is returned, and setting the payload sets DataLength and Dlc to its length.</summary>
        public byte[] DataBytes
        {
            get { return (byte[])data_.Clone(); }
            set
            {
                byte[] copy = value == null ? new byte[0] : (byte[])value.Clone();
                if (copy.Length > 64) { throw new ArgumentOutOfRangeException("value", "A CAN FD payload is at most 64 bytes."); }
                data_ = copy;
                dlc_ = CodeOf(copy.Length);
            }
        }

        /// <summary>The payload length in bytes. Setting it resizes the payload (new bytes are zero) and sets Dlc to its code.</summary>
        public uint DataLength
        {
            get { return (uint)data_.Length; }
            set
            {
                if (value > 64) { throw new ArgumentOutOfRangeException("value", "A CAN FD payload is at most 64 bytes."); }
                byte[] resized = new byte[value];
                Array.Copy(data_, resized, Math.Min(data_.Length, resized.Length));
                data_ = resized;
                dlc_ = CodeOf(resized.Length);
            }
        }

        /// <summary>True for a frame received from the bus (false) or transmitted (true, Dir). The bus logger
        /// stores it as the direction of the frame.</summary>
        public bool Dir { get; set; }

        /// <summary>Releases the payload; the message stays usable.</summary>
        public void Dispose()
        {
            data_ = new byte[0];
        }

        /// <summary>The data length code, 0 to 15. A remote frame keeps the length it requests here.</summary>
        public byte Dlc
        {
            get { return dlc_; }
            set
            {
                if (value > 15) { throw new ArgumentOutOfRangeException("value", "A data length code is 0..15."); }
                dlc_ = value;
            }
        }

        /// <summary>The payload length of a CAN FD code, 0 to 15; 0 for any other value.</summary>
        public static byte DlcToLength(byte dlc)
        {
            return dlc < LengthOfDlc.Length ? (byte)LengthOfDlc[dlc] : (byte)0;
        }

        /// <summary>The CAN FD format flag (EDL).</summary>
        public bool Edl { get; set; }

        /// <summary>The error state indicator (ESI) of a CAN FD frame.</summary>
        public bool Esi { get; set; }

        /// <summary>The error type of an error frame.</summary>
        public CanErrorType ErrorType { get; set; }

        /// <summary>True for a 29-bit identifier (IDE), false for an 11-bit identifier.</summary>
        public bool ExtendedId { get; set; }

        /// <summary>The duration of the frame on the bus in nanoseconds. The v2.3.0 bus log does not store it; SaveCanMessage ignores it silently.</summary>
        public uint FrameDuration { get; set; }

        /// <summary>The identifier of the frame; the same value as CanId.</summary>
        public uint MessageId
        {
            get { return id_; }
            set { id_ = value; }
        }

        /// <summary>The reserved bit R0 of the frame. The v2.3.0 bus log has no R0 bit, so SaveCanMessage ignores it.</summary>
        public bool R0 { get; set; }

        /// <summary>The reserved bit R1 of the frame. The v2.3.0 bus log has no R1 bit, so SaveCanMessage ignores it.</summary>
        public bool R1 { get; set; }

        /// <summary>True for a remote frame; it is the TypeOfMessage Can_RemoteFrame, so the two cannot disagree.</summary>
        public bool Rtr
        {
            get { return TypeOfMessage == CanMessageType.Can_RemoteFrame; }
            set
            {
                if (value) { TypeOfMessage = CanMessageType.Can_RemoteFrame; }
                else if (TypeOfMessage == CanMessageType.Can_RemoteFrame) { TypeOfMessage = CanMessageType.Can_DataFrame; }
            }
        }

        /// <summary>Clears every member to the state a new CanMessage has.</summary>
        public void Reset()
        {
            id_ = 0;
            data_ = new byte[0];
            dlc_ = 0;
            BitPosition = 0;
            Brs = false;
            BusChannel = 0;
            Crc = 0;
            Dir = false;
            Edl = false;
            Esi = false;
            ErrorType = CanErrorType.UNKNOWN_ERROR;
            ExtendedId = false;
            FrameDuration = 0;
            R0 = false;
            R1 = false;
            SingleWire = false;
            Srr = false;
            Timestamp = 0.0;
            TypeOfMessage = CanMessageType.Can_DataFrame;
            WakeUp = false;
        }

        /// <summary>The single-wire flag of a low-speed CAN frame, stored by the bus log.</summary>
        public bool SingleWire { get; set; }

        /// <summary>The start-of-frame substitute rate bit (SRR) of an extended frame, stored by the bus log.</summary>
        public bool Srr { get; set; }

        /// <summary>The time of the frame in seconds. The bus logger takes its time from the time argument of
        /// SaveCanMessage; this value is kept for the caller.</summary>
        public double Timestamp { get; set; }

        /// <summary>The kind of the frame.</summary>
        public CanMessageType TypeOfMessage { get; set; }

        /// <summary>The wake-up flag of a low-speed CAN frame, stored by the bus log.</summary>
        public bool WakeUp { get; set; }

        /// <summary>The identifier of the frame, for the bus logger (MessageId, CanId).</summary>
        internal uint Identifier { get { return id_; } }

        /// <summary>The payload, for the bus logger: the array itself, which the logger only reads.</summary>
        internal byte[] Payload { get { return data_; } }

        // The smallest Dlc whose length holds the payload; the bytes between two valid lengths are padding.
        private static byte CodeOf(int length)
        {
            for (int dlc = 0; dlc < LengthOfDlc.Length; dlc++)
            {
                if (LengthOfDlc[dlc] >= length) { return (byte)dlc; }
            }
            throw new ArgumentOutOfRangeException("length", "A CAN FD payload is at most 64 bytes.");
        }
    }
}
