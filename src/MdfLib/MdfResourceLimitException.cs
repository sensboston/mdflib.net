// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Globalization;

namespace MdfLib
{
    /// <summary>Reports a configured resource budget refusal, not malformed MDF.</summary>
    /// <remarks>Legacy formatter-based exception serialization is not supported.</remarks>
    internal sealed class MdfResourceLimitException : InvalidOperationException
    {
        /// <summary>Creates a resource-limit error.</summary>
        /// <param name="limitName">Name of the exceeded reader option.</param>
        /// <param name="limit">Configured positive limit.</param>
        /// <param name="requested">Requested or accumulated resource amount.</param>
        /// <param name="offset">Wire offset at which the limit was encountered.</param>
        public MdfResourceLimitException(string limitName, ulong limit, ulong requested, ulong offset)
            : base(string.Format(CultureInfo.InvariantCulture, "{0} limit {1} exceeded by request {2} at offset 0x{3:X}.", limitName, limit, requested, offset))
        {
            if (string.IsNullOrWhiteSpace(limitName))
            {
                throw new ArgumentException("A limit name is required.", nameof(limitName));
            }
            if (limit == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(limit));
            }
            LimitName = limitName;
            Limit = limit;
            Requested = requested;
            Offset = offset;
        }

        /// <summary>Gets the name of the exceeded option.</summary>
        public string LimitName { get; }

        /// <summary>Gets the configured positive limit.</summary>
        public ulong Limit { get; }

        /// <summary>Gets the requested or accumulated amount.</summary>
        public ulong Requested { get; }

        /// <summary>Gets the related wire offset.</summary>
        public ulong Offset { get; }
    }
}
