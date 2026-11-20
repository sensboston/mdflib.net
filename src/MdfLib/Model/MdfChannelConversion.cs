// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace MdfLib
{
    /// <summary>A conversion descriptor; unsupported conversions are never silently evaluated as identity.</summary>
    internal sealed class MdfChannelConversion
    {
        private static readonly double[] NoValues = new double[0];
        private static readonly ReadOnlyCollection<double> NoParameters = new ReadOnlyCollection<double>(NoValues);
        private static readonly string[] NoReferences = new string[0];
        private static readonly ReadOnlyCollection<string> NoTexts = new ReadOnlyCollection<string>(NoReferences);
        // Declared before Identity: static initializers run in textual order and the instance fields below use them.
        private static readonly ulong[] NoMasks = new ulong[0];
        private static readonly MdfChannelConversion[] NoParts = new MdfChannelConversion[0];
        private static readonly System.Text.Encoding Utf8 = new System.Text.UTF8Encoding(false, true);

        internal static readonly MdfChannelConversion Identity = new MdfChannelConversion
        {
            Kind = MdfConversionKind.Identity, Factor = 1, Offset = 0,
            Name = string.Empty, Unit = string.Empty, Comment = string.Empty
        };

        private double[] values = NoValues;
        private string[] references = NoReferences;
        private ReadOnlyCollection<string> texts = NoTexts;
        private ReadOnlyCollection<double> parameters = NoParameters;
        private ulong[] masks = NoMasks;
        private MdfChannelConversion[] parts = NoParts;
        private ReadOnlyCollection<ulong> bitMasks = new ReadOnlyCollection<ulong>(NoMasks);
        private ReadOnlyCollection<MdfChannelConversion> bitfieldConversions = new ReadOnlyCollection<MdfChannelConversion>(NoParts);

        internal MdfChannelConversion() { }
        /// <summary>Gets the evaluated conversion kind, or <see cref="MdfConversionKind.Unsupported"/>.</summary>
        public MdfConversionKind Kind { get; internal set; }
        /// <summary>Gets a for a * raw + b; 1 for identity, null otherwise.</summary>
        public double? Factor { get; internal set; }
        /// <summary>Gets b for a * raw + b; 0 for identity, null otherwise.</summary>
        public double? Offset { get; internal set; }
        /// <summary>Gets the MDF 4 conversion name; empty for MDF 3.</summary>
        public string Name { get; internal set; }
        /// <summary>Gets the decoded conversion unit: fixed field for MDF 3, TX or simple XML for MDF 4.</summary>
        public string Unit { get; internal set; }
        /// <summary>Gets the MDF 4 decoded raw TX or validated XML comment; empty for MDF 3.</summary>
        public string Comment { get; internal set; }

        /// <summary>
        /// Gets the numeric parameters in wire order: empty for identity and unsupported conversions, [offset, factor] for
        /// linear, P1..P6 for rational/polynomial, (key, value) pairs for tables and (lower, upper, value) triples plus the
        /// default for range tables.
        /// </summary>
        public IReadOnlyList<double> Parameters => parameters;

        /// <summary>
        /// Gets the referenced TX texts in wire order (cc_ref), null for a NIL reference: n + 1 for value/range to text
        /// (last = default), n keys for text to value, 2n + 1 for text to text (key, output pairs, then the default).
        /// Empty for numeric and unsupported conversions.
        /// </summary>
        public IReadOnlyList<string> Texts => texts;

        /// <summary>Algebraic conversion formula text as stored (also for an unsupported formula); null for other kinds.</summary>
        public string Formula { get; internal set; }

        /// <summary>Compiled formula when the text is inside the supported grammar.</summary>
        internal MdfLib.Reading.AlgebraicFormula CompiledFormula { get; set; }

        /// <summary>Bitfield to text: the UINT64 bit mask of each part (empty for other kinds).</summary>
        public IReadOnlyList<ulong> BitMasks => bitMasks;

        /// <summary>Bitfield to text: the value/range to text conversion of each part, in mask order (empty for other kinds).</summary>
        public IReadOnlyList<MdfChannelConversion> BitfieldConversions => bitfieldConversions;

        /// <summary>Sets the bitfield masks and part conversions together (equal lengths, validated by the codec).</summary>
        internal void SetBitfield(ulong[] wireMasks, MdfChannelConversion[] wireParts)
        {
            masks = wireMasks;
            parts = wireParts;
            bitMasks = new ReadOnlyCollection<ulong>(masks);
            bitfieldConversions = new ReadOnlyCollection<MdfChannelConversion>(parts);
            NameBytes = new byte[parts.Length][];
            long total = parts.Length - 1; // separators
            for (int i = 0; i < parts.Length; i++)
            {
                NameBytes[i] = string.IsNullOrEmpty(parts[i].Name) ? null : Utf8.GetBytes(parts[i].Name + "=");
                total += (NameBytes[i] == null ? 0 : NameBytes[i].Length) + parts[i].MaximumTextBytes;
            }
            MaximumTextBytes = total > int.MaxValue ? int.MaxValue : (int)total;
        }

        internal ulong[] Masks => masks;

        internal MdfChannelConversion[] Parts => parts;

        /// <summary>UTF-8 "name=" prefix of each bitfield part (null for an unnamed part).</summary>
        internal byte[][] NameBytes { get; private set; }

        /// <summary>Referenced texts and their UTF-8 bytes (null for NIL), shared with the evaluator.</summary>
        internal string[] References
        {
            get { return references; }
            set
            {
                references = value ?? NoReferences;
                texts = references.Length == 0 ? NoTexts : new ReadOnlyCollection<string>(references);
                ReferenceBytes = new byte[references.Length][];
                MaximumTextBytes = 0;
                for (int i = 0; i < references.Length; i++)
                {
                    if (references[i] == null) { continue; }
                    ReferenceBytes[i] = Utf8.GetBytes(references[i]);
                    MaximumTextBytes = System.Math.Max(MaximumTextBytes, ReferenceBytes[i].Length);
                }
            }
        }

        internal byte[][] ReferenceBytes { get; private set; } = new byte[0][];

        /// <summary>Largest referenced UTF-8 text in bytes (batch accounting of text outputs).</summary>
        internal int MaximumTextBytes { get; set; }

        /// <summary>First wire index of each distinct key text (text to value/text), ordinal.</summary>
        internal Dictionary<string, int> KeyIndex { get; set; }

        /// <summary>Wire-order parameters shared with the evaluator; never exposed mutably.</summary>
        internal double[] Values
        {
            get { return values; }
            set
            {
                values = value ?? NoValues;
                parameters = values.Length == 0 ? NoParameters : new ReadOnlyCollection<double>(values);
            }
        }
    }
}
