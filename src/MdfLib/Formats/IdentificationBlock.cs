// Copyright 2021 Ingemar Hedvall
// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// ID field layout adapted from the pinned MIT upstream.
using System;
using System.Threading;
using MdfLib.Internal;
using MdfLib.IO;

namespace MdfLib.Formats
{
    internal sealed class IdentificationBlock
    {
        internal int Version { get; private set; }
        internal string VersionText { get; private set; }
        internal string ProgramIdentifier { get; private set; }
        internal bool DefaultDataIsBigEndian { get; private set; }
        internal int CodePage { get; private set; }

        internal static bool HasSignature(byte[] data)
        {
            // Compare all eight bytes, not a prefix accepted after trimming arbitrary text.
            return Matches(data, "MDF     ") || Matches(data, "UnFinMF ");
        }

        private static bool Matches(byte[] data, string expected)
        {
            for (int index = 0; index < 8; index++)
            {
                if (data[index] != expected[index])
                {
                    return false;
                }
            }
            return true;
        }

        internal static IdentificationBlock Read(BinaryInput input, ReadBudget budget, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            budget.ReservePayload(64, 0);
            byte[] data = new byte[64];
            FormatInput.ReadAt(input, 0, data, data.Length, "ID", token);
            if (!HasSignature(data))
            {
                throw new MdfFormatException("Not an MDF signature.", "MDF001", 0, "ID");
            }
            string text = new string(new[] { (char)data[8], (char)data[9], (char)data[10], (char)data[11] });
            if (text[0] < '0' || text[0] > '9' || text[1] != '.'
                || text[2] < '0' || text[2] > '9' || text[3] < '0' || text[3] > '9')
            {
                throw new MdfFormatException("Invalid MDF version identifier.", "MDF002", 8, "ID");
            }
            for (int index = 12; index < 16; index++)
            {
                if (data[index] != 0 && data[index] != 32)
                {
                    throw new MdfFormatException("Invalid version padding.", "MDF002", 8, "ID");
                }
            }
            int numeric = EndianBitConverter.ReadUInt16(data, 28, ByteOrder.LittleEndian);
            int declared = (text[0] - '0') * 100 + (text[2] - '0') * 10 + text[3] - '0';
            if (numeric != declared)
            {
                throw new MdfFormatException("Text and numeric MDF versions disagree.", "MDF003", 28, "ID");
            }
            bool mdf3 = numeric == 300 || numeric == 310 || numeric == 320 || numeric == 330;
            if (!mdf3 && numeric != 400 && numeric != 410 && numeric != 411 && numeric != 420)
            {
                throw new NotSupportedException("MDF " + text + " is not supported; only 3.00, 3.10, 3.20, 3.30, 4.00, 4.10, 4.11 and 4.20 are implemented (limited reader subsets).");
            }
            // bytes 60..63 are the unfinalized flags only from 4.10; in 4.00 they are reserved and not interpreted.
            if (data[0] == (byte)'U' || (numeric >= 410 && (data[60] != 0 || data[61] != 0 || data[62] != 0 || data[63] != 0)))
            {
                throw new NotSupportedException("Unfinalized MDF is not opened or repaired by the normal reader.");
            }
            int order = mdf3 ? EndianBitConverter.ReadUInt16(data, 24, ByteOrder.LittleEndian) : 0;
            if (order != 0 && order != 1)
            {
                throw new NotSupportedException("Unsupported MDF 3 default data byte order.");
            }
            if (mdf3 && EndianBitConverter.ReadUInt16(data, 26, ByteOrder.LittleEndian) != 0)
            {
                throw new NotSupportedException("Non-IEEE MDF 3 floating-point format is not supported.");
            }
            // the ID code page field exists from 3.30; before that it is reserved and ignored (single-byte text).
            int codePage = numeric == 330 ? EndianBitConverter.ReadUInt16(data, 30, ByteOrder.LittleEndian) : 0;
            FixedText.ValidateCodePage(codePage);
            return new IdentificationBlock
            {
                Version = numeric,
                VersionText = text,
                ProgramIdentifier = FixedText.Read(data, 16, 8, 0, 16, "ID"),
                DefaultDataIsBigEndian = order == 1,
                CodePage = codePage
            };
        }
    }
}
