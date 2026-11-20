// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Linq;
using System.Threading;
using MdfLib.Internal;
using MdfLib.Reading;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Reading
{
    public sealed class Mdf420StorageTests
    {
        [Theory]
        [InlineData("ld-short", "MDF006")]
        [InlineData("ld-shape", "MDF006")]
        [InlineData("ld-extra-fields", "MDF006")]
        [InlineData("count-zero", "MDF013")]
        [InlineData("count-mismatch", "MDF013")]
        [InlineData("count-overflow", "MDF013")]
        [InlineData("dv-null", "MDF013")]
        [InlineData("dv-empty", "MDF013")]
        [InlineData("dv-nonmultiple", "MDF013")]
        [InlineData("dv-links", "MDF006")]
        [InlineData("di-links", "MDF006")]
        [InlineData("di-length", "MDF013")]
        [InlineData("equal-zero", "MDF013")]
        [InlineData("equal-bytes", "MDF013")]
        [InlineData("equal-small", "MDF013")]
        [InlineData("equal-last-large", "MDF013")]
        [InlineData("sum-short", "MDF013")]
        [InlineData("sum-overflow", "MDF013")]
        [InlineData("di-without-bytes", "MDF013")]
        [InlineData("dz-short", "MDF006")]
        [InlineData("dz-compressed", "MDF013")]
        [InlineData("dz-transpose", "MDF015")]
        [InlineData("offset-start", "MDF013")]
        [InlineData("offset-bytes", "MDF013")]
        [InlineData("offset-overflow", "MDF013")]
        [InlineData("offset-chain-reset", "MDF013")]
        [InlineData("cycle", "MDF010")]
        [InlineData("next-type", "MDF005")]
        [InlineData("overlap", "MDF011")]
        public void MalformedListsAndPairsAreAtomicHeaderErrorsBeforeAnySamplePayloadOrBatch(string mutation, string code)
        {
            string id = mutation.StartsWith("offset-chain", StringComparison.Ordinal) || mutation == "cycle" || mutation == "next-type"
                ? "m5c-ld-chain420" : mutation.StartsWith("offset", StringComparison.Ordinal) ? "m5c-ld-offset420" : "m5c-ld-equal420";
            byte[] bytes = M5CWire.Read(id);
            int root = M5CWire.Root(bytes), fields = M3Wire.Payload(bytes, root);
            int dv = M5CWire.Leaf(bytes), di = M5CWire.Leaf(bytes, invalidation: true);
            if (mutation == "ld-short") { M3Wire.Put(bytes, root + 8, (ulong)(fields - root + 7)); }
            else if (mutation == "ld-shape")
            { root = M5Wire.Add(ref bytes, "LD", new[] { 0UL }, new byte[16]); M5CWire.SetRoot(bytes, root); }
            else if (mutation == "ld-extra-fields") { Array.Resize(ref bytes, bytes.Length + 1); M3Wire.Put(bytes, root + 8, M3Wire.U64(bytes, root + 8) + 1); }
            else if (mutation == "count-zero") { M3Wire.Put(bytes, fields + 4, 0, 4); }
            else if (mutation == "count-mismatch") { M3Wire.Put(bytes, fields + 4, 2, 4); }
            else if (mutation == "count-overflow") { M3Wire.Put(bytes, fields + 4, uint.MaxValue, 4); }
            else if (mutation == "dv-null") { M3Wire.Put(bytes, M3Wire.Link(root, 1), 0); }
            else if (mutation == "dv-empty") { M3Wire.Put(bytes, dv + 8, 24); }
            else if (mutation == "dv-nonmultiple") { M3Wire.Put(bytes, dv + 8, M3Wire.U64(bytes, dv + 8) - 1); }
            else if (mutation == "dv-links") { M3Wire.Put(bytes, dv + 16, 1); M3Wire.Put(bytes, dv + 24, 0); }
            else if (mutation == "di-links") { M3Wire.Put(bytes, di + 16, 1); M3Wire.Put(bytes, di + 24, 0); }
            else if (mutation == "di-length") { int rawDi = M5CWire.Leaf(bytes, 1, true); M3Wire.Put(bytes, rawDi + 8, 29); }
            else if (mutation == "equal-zero") { M3Wire.Put(bytes, fields + 8, 0); }
            else if (mutation == "equal-bytes") { M3Wire.Put(bytes, fields + 8, 66); }
            else if (mutation == "equal-small") { M3Wire.Put(bytes, fields + 8, 2); }
            else if (mutation == "equal-last-large") { M3Wire.Put(bytes, M5CWire.Leaf(bytes, 2) + 32, 88); }
            else if (mutation == "sum-short") { M3Wire.Put(bytes, M3Wire.Payload(bytes, M5CWire.Cg(bytes)) + 8, 8); }
            else if (mutation == "sum-overflow") { M3Wire.Put(bytes, M3Wire.Payload(bytes, M5CWire.Cg(bytes)) + 8, ulong.MaxValue); }
            else if (mutation == "di-without-bytes")
            {
                M3Wire.Put(bytes, M3Wire.Payload(bytes, M5CWire.Cg(bytes)) + 28, 0, 4);
                for (int cn = (int)M3Wire.U64(bytes, M3Wire.Link(M5CWire.Cg(bytes), 1)); cn != 0; cn = (int)M3Wire.U64(bytes, M3Wire.Link(cn, 0)))
                { M3Wire.Put(bytes, M3Wire.Payload(bytes, cn) + 12, 0, 4); }
            }
            else if (mutation == "dz-short") { M3Wire.Put(bytes, di + 8, 47); }
            else if (mutation == "dz-compressed") { M3Wire.Put(bytes, di + 40, 1); }
            else if (mutation == "dz-transpose") { M3Wire.Put(bytes, di + 28, 0, 4); }
            else if (mutation == "offset-start") { M3Wire.Put(bytes, fields + 8, 1); }
            else if (mutation == "offset-bytes") { M3Wire.Put(bytes, fields + 16, 44); }
            else if (mutation == "offset-overflow") { M3Wire.Put(bytes, fields + 24, ulong.MaxValue); }
            else if (mutation == "offset-chain-reset")
            { int next = (int)M3Wire.U64(bytes, root + 24); M3Wire.Put(bytes, M3Wire.Payload(bytes, next) + 8, 0); }
            else if (mutation == "cycle") { M3Wire.Put(bytes, root + 24, (ulong)root); }
            else if (mutation == "next-type") { M3Wire.Put(bytes, root + 24, (ulong)M5CWire.Cg(bytes)); }
            else if (mutation == "overlap")
            {
                Array.Copy(M5Wire.Header("DI", 6), 0, bytes, dv + 24, 24); // Valid DI header inside the already discovered DV extent.
                M3Wire.Put(bytes, M3Wire.Link(root, 4), (ulong)(dv + 24));
            }
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Empty(reader.ReadSamples(group, new[] { 1 }, sampleCount: 0));
                for (int retry = 0; retry < 2; retry++)
                {
                    Assert.Equal(code, Assert.Throws<MdfFormatException>(() => reader.ReadSamples(group, new[] { 1 }, 0, 1).ToList()).DiagnosticCode);
                    Assert.Null(group.Layout.ListDataMap);
                    Assert.Null(group.Layout.DataMap);
                    Assert.Same(group, reader.ReadMetadata()[0].ChannelGroups[0]);
                }
                Assert.Equal(0, source.DisposeCalls);
                Assert.Equal(0, source.WriteCalls);
            }
        }

        [Theory]
        [InlineData("dv-role")]
        [InlineData("di-role")]
        [InlineData("dv-dz-role")]
        [InlineData("di-dz-role")]
        [InlineData("dv-dz-dt")]
        [InlineData("di-dz-dt")]
        [InlineData("zip")]
        [InlineData("summary-time")]
        [InlineData("summary-angle")]
        [InlineData("summary-distance")]
        [InlineData("zip-info")]
        [InlineData("missing-di")]
        [InlineData("nested-ld")]
        [InlineData("nested-dl")]
        [InlineData("nested-hl")]
        [InlineData("root-dv")]
        [InlineData("root-di")]
        public void RequiredUnsupportedRolesCodecsFlagsAndNestedStructuresAreNeverReinterpreted(string mutation)
        {
            byte[] bytes = M5CWire.Read();
            int root = M5CWire.Root(bytes), fields = M3Wire.Payload(bytes, root);
            int dv = M5CWire.Leaf(bytes), di = M5CWire.Leaf(bytes, 1, true);
            if (mutation == "dv-role") { bytes[dv + 3] = (byte)'I'; }
            else if (mutation == "di-role") { bytes[di + 3] = (byte)'V'; }
            else if (mutation == "dv-dz-role") { bytes[M5CWire.Leaf(bytes, 1) + 25] = (byte)'I'; }
            else if (mutation == "di-dz-role") { bytes[M5CWire.Leaf(bytes, invalidation: true) + 25] = (byte)'V'; }
            else if (mutation == "dv-dz-dt") { bytes[M5CWire.Leaf(bytes, 1) + 25] = (byte)'T'; }
            else if (mutation == "di-dz-dt") { bytes[M5CWire.Leaf(bytes, invalidation: true) + 25] = (byte)'T'; }
            else if (mutation == "zip") { bytes[M5CWire.Leaf(bytes, invalidation: true) + 26] = 2; }
            else if (mutation.StartsWith("summary", StringComparison.Ordinal) || mutation == "zip-info")
            { bytes[fields] |= mutation == "summary-time" ? (byte)2 : mutation == "summary-angle" ? (byte)4 : mutation == "summary-distance" ? (byte)8 : (byte)16; }
            else if (mutation == "missing-di")
            {
                int ld = M5CWire.List(ref bytes, new[] { (ulong)dv, (ulong)M5CWire.Leaf(bytes, 1), (ulong)M5CWire.Leaf(bytes, 2) }, null, null, 3);
                M5CWire.SetRoot(bytes, ld);
            }
            else if (mutation.StartsWith("nested", StringComparison.Ordinal))
            {
                int child = mutation == "nested-ld" ? root : M5Wire.Add(ref bytes, mutation == "nested-dl" ? "DL" : "HL", new[] { 0UL }, new byte[16]);
                M3Wire.Put(bytes, M3Wire.Link(root, 1), (ulong)child);
            }
            else { M5CWire.SetRoot(bytes, mutation == "root-dv" ? dv : di); }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.True(group.CanReadSamples); // Layout capability is not an unread storage certificate.
                if (mutation.StartsWith("summary", StringComparison.Ordinal))
                {
                    // narrowed: summary bits are accepted only with their exact arrays; a bit alone is an LD shape error.
                    Assert.Equal("MDF006", Assert.Throws<MdfFormatException>(() => reader.ReadSamples(group, new[] { 1 }).ToList()).DiagnosticCode);
                }
                else { Assert.Throws<NotSupportedException>(() => reader.ReadSamples(group, new[] { 1 }).ToList()); }
                Assert.Null(group.Layout.ListDataMap);
                Assert.Null(group.Layout.DataMap);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void OnlyIntersectingValueAndInvalidationDzPayloadsAreReadAndChecksummed(bool invalidation)
        {
            byte[] bytes = M5CWire.Read();
            int bad = M5CWire.Leaf(bytes, invalidation ? 0 : 1, invalidation);
            int length = (int)M3Wire.U64(bytes, bad + 8);
            bytes[bad + length - 1] ^= 1;
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                ulong skipped = invalidation ? 3UL : 0UL;
                Assert.Single(reader.ReadSamples(group, new[] { 1 }, skipped, 1));
                Assert.DoesNotContain(source.ReadRanges, r => r.Item1 < bad + length && r.Item1 + r.Item2 > bad + 48);
                MdfFormatException error = Assert.Throws<MdfFormatException>(() => reader.ReadSamples(group, new[] { 1 }, invalidation ? 0UL : 3UL, 1).ToList());
                Assert.Equal("MDF015", error.DiagnosticCode);
                Assert.Equal((ulong)bad, error.Offset);
                Assert.IsType<InvalidDataException>(error.InnerException);
                Assert.NotNull(group.Layout.ListDataMap);
                Assert.Single(reader.ReadSamples(group, new[] { 1 }, skipped, 1));
            }
        }

        [Theory]
        [InlineData("MaximumMetadataBytes", 1023)]
        [InlineData("MaximumMetadataBytes", 1151)]
        [InlineData("MaximumMetadataBytes", 1279)]
        [InlineData("MaximumMetadataBytes", 1407)]
        [InlineData("MaximumMetadataBytes", 1535)]
        [InlineData("MaximumTraversalDepth", 1)]
        [InlineData("MaximumReadBufferBytes", 15)]
        public void MapAllowancesAreReservedBeforeHolderChainPairAndEitherSegmentAllocation(string name, int cap)
        {
            byte[] bytes = M5CWire.Read("m5c-ld-single420");
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                MdfReaderOptions options = new MdfReaderOptions();
                var member = typeof(MdfReaderOptions).GetProperty(name);
                member.SetValue(options, Convert.ChangeType(cap, member.PropertyType));
                ReaderLimits limits = new ReaderLimits(options);
                ReadBudget budget = new ReadBudget(limits);
                MdfResourceLimitException error = Assert.Throws<MdfResourceLimitException>(() => Mdf4ListDataMap.Build(reader.Resolver, limits, budget,
                    (ulong)M5CWire.Root(bytes), group.SampleCount, 22, 2, default(CancellationToken)));
                Assert.Equal(name, error.LimitName);
                Assert.Null(group.Layout.ListDataMap);
                if (cap == 1023) { Assert.DoesNotContain(source.ReadRanges, r => r.Item1 >= 1256); }
                Assert.Equal(7, reader.ReadSamples(group, new[] { 1 }).Sum(b => b.SampleCount));
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ExactReadAndBatchCapsIncludeBothNormalizedAreasWithoutExtraSampleScratch(bool output)
        {
            byte[] bytes = M5CWire.Read("m5c-ld-single420");
            // Input cap96 permits four normalized rows; output cap440 = header256 + one row160.
            MdfReaderOptions options = output ? new MdfReaderOptions { MaximumSampleBatchBytes = 440 }
                : new MdfReaderOptions { MaximumReadBufferBytes = 96 };
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true, options))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                MdfSampleBatch[] batches = reader.ReadSamples(group, new[] { 0, 1, 2, 3 }, batchSize: int.MaxValue).ToArray();
                Assert.Equal(output ? new[] { 1, 1, 1, 1, 1, 1, 1 } : new[] { 4, 3 }, batches.Select(b => b.SampleCount));
                MdfSampleValue invalid = (output ? batches[2] : batches[0]).GetValue(output ? 0 : 2, 3);
                Assert.False(invalid.IsValid);
                Assert.Equal(0L, invalid.SignedValue);
                Assert.InRange(source.LargestReadRequest, 1, output ? 1048576 : 96);
            }
        }
    }
}
