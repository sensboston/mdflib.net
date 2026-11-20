// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;

namespace MdfLib
{
    /// <summary>Reports malformed MDF structure without hiding unrelated I/O failures.</summary>
    /// <remarks>Legacy formatter-based exception serialization is not supported.</remarks>
    internal sealed class MdfFormatException : FormatException
    {
        /// <summary>Creates a structural MDF error with a stable code and wire offset.</summary>
        /// <param name="message">Human-readable explanation.</param>
        /// <param name="diagnosticCode">Stable diagnostic identifier.</param>
        /// <param name="offset">Unsigned file offset associated with the failure.</param>
        /// <param name="blockType">Block identifier, when known; otherwise null.</param>
        /// <param name="innerException">Underlying truncation or decoding error, when applicable.</param>
        public MdfFormatException(string message, string diagnosticCode, ulong offset, string blockType = null, Exception innerException = null)
            : base(message, innerException)
        {
            if (string.IsNullOrWhiteSpace(diagnosticCode))
            {
                throw new ArgumentException("A diagnostic code is required.", nameof(diagnosticCode));
            }
            DiagnosticCode = diagnosticCode;
            Offset = offset;
            BlockType = blockType;
        }

        /// <summary>Gets the stable diagnostic identifier; messages are not machine-readable codes.</summary>
        public string DiagnosticCode { get; }

        /// <summary>Gets the file offset, including invalid addresses above Int64.MaxValue.</summary>
        public ulong Offset { get; }

        /// <summary>Gets the block identifier if known, or null.</summary>
        public string BlockType { get; }
    }
}
