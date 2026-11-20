// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MdfLib.TestSupport;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Reading
{
    // literal mutations of the pre-C# m6f corpus (block addresses frozen from tools/fixtures/build_m6f.py).
    public sealed class SampleReductionTests
    {
        private const int Cg = 232, CgSrLink = Cg + 24 + 32, CgInvalidationBytes = Cg + 24 + 48 + 28;
        private const int Cn1Flags = 528 + 24 + 64 + 12;
        private const int Dt = 1008;
        private const int Sr0 = 1104, Sr0Next = Sr0 + 24, Sr0Data = Sr0 + 32, Sr0Cycles = Sr0 + 40, Sr0Flags = Sr0 + 57;
        private const int Sr1 = 1296, Sr1Next = Sr1 + 24, Sr1Cycles = Sr1 + 40, Sr1Flags = Sr1 + 57;
        private const int Dz = 1360, DzOriginalType = Dz + 24;
        private const int Ld420 = 1168, Ld420Invalidation = Ld420 + 40;
        private const int Cg330 = 300, Sr330 = 786, Sr330Cycles = Sr330 + 12;
        private static readonly int[] Speed = { 1 };

        private static byte[] Original(string version = "410") { return FixtureCorpus.Read("m6f-reduction" + version); }
        private static MdfReader Open(byte[] bytes, MdfReaderOptions options = null) { return MdfReader.Open(new MemoryStream(bytes, false), options: options); }
        private static MdfChannelGroup Group(MdfReader reader) { return reader.ReadMetadata()[0].ChannelGroups[0]; }
        private static IReadOnlyList<MdfSampleReduction> Reductions(MdfReader reader) { return reader.ReadSampleReductions(Group(reader)); }
        private static void Put(byte[] bytes, int at, ulong value, int width = 8) { M3Wire.Put(bytes, at, value, width); }

        private static T ListingFails<T>(byte[] bytes) where T : Exception
        {
            using (MdfReader reader = Open(bytes))
            {
                MdfChannelGroup group = Group(reader);
                T error = Assert.Throws<T>(() => reader.ReadSampleReductions(group));
                Assert.Throws<T>(() => reader.ReadSampleReductions(group)); // Nothing partial was cached.
                return error;
            }
        }

        private static T ValuesFail<T>(byte[] bytes, int reduction) where T : Exception
        {
            using (MdfReader reader = Open(bytes))
            {
                MdfSampleReduction target = Reductions(reader)[reduction];
                return Assert.Throws<T>(() => reader.ReadSampleReduction(target, Speed).ToList());
            }
        }

        [Fact]
        public void TheFixtureAddressesAreTheFrozenBlocks()
        {
            byte[] bytes = Original();
            Assert.Equal("##CG", Encoding.ASCII.GetString(bytes, Cg, 4));
            Assert.Equal("##DT", Encoding.ASCII.GetString(bytes, Dt, 4));
            Assert.Equal("##SR", Encoding.ASCII.GetString(bytes, Sr0, 4));
            Assert.Equal("##SR", Encoding.ASCII.GetString(bytes, Sr1, 4));
            Assert.Equal("##DZ", Encoding.ASCII.GetString(bytes, Dz, 4));
            Assert.Equal((ulong)Sr0, BitConverter.ToUInt64(bytes, CgSrLink));
            Assert.Equal(1, bytes[CgInvalidationBytes]);
            Assert.Equal(2, bytes[Cn1Flags]);
            Assert.Equal(3, bytes[Sr0Cycles]);
            Assert.Equal(1, bytes[Sr0Flags]);
            Assert.Equal(2, bytes[Sr1Cycles]);
            Assert.Equal("RD", Encoding.ASCII.GetString(bytes, DzOriginalType, 2));
            byte[] v420 = Original("420");
            Assert.Equal("##LD", Encoding.ASCII.GetString(v420, Ld420, 4));
            byte[] v330 = Original("330");
            Assert.Equal("CG", Encoding.ASCII.GetString(v330, Cg330, 2));
            Assert.Equal("SR", Encoding.ASCII.GetString(v330, Sr330, 2));
            Assert.Equal(2, v330[Sr330Cycles]);
        }

        [Fact]
        public void TheChainIsCachedPerGroupAndSurvivesDisposal()
        {
            MdfReader reader = Open(Original());
            IReadOnlyList<MdfSampleReduction> reductions = Reductions(reader);
            Assert.Same(reductions, reader.ReadSampleReductions(Group(reader)));
            reader.Dispose();
            Assert.Equal(2, reductions.Count);
            Assert.Equal(MdfReductionSyncType.Index, reductions[1].SyncType);
            Assert.Throws<ObjectDisposedException>(() => reader.ReadSampleReductions(reductions[0].Group));
            Assert.Throws<ObjectDisposedException>(() => reader.ReadSampleReduction(reductions[0], Speed));
        }

        [Fact]
        public void AGroupWithoutReductionsHasAnEmptyChain()
        {
            byte[] bytes = Original();
            Put(bytes, CgSrLink, 0);
            using (MdfReader reader = Open(bytes)) { Assert.Empty(Reductions(reader)); }
            byte[] v330 = Original("330");
            Put(v330, Cg330 + 2, 26, 2); // A pre-3.30 CG without the trailing SR link.
            using (MdfReader reader = Open(v330)) { Assert.Empty(Reductions(reader)); }
        }

        [Fact]
        public void ChainCyclesAndForeignBlocksAreRejected()
        {
            byte[] cycle = Original();
            Put(cycle, Sr1Next, Sr0);
            Assert.Equal("MDF010", ListingFails<MdfFormatException>(cycle).DiagnosticCode);
            byte[] foreign = Original();
            Put(foreign, CgSrLink, Dt);
            Assert.Equal("MDF005", ListingFails<MdfFormatException>(foreign).DiagnosticCode);
            byte[] outside = Original();
            Put(outside, Sr0Next, (ulong)outside.Length + 64);
            Assert.Equal("MDF007", ListingFails<MdfFormatException>(outside).DiagnosticCode);
        }

        [Fact]
        public void MissingDataAndUnbackedInvalidationAreFormatErrors()
        {
            byte[] noData = Original();
            Put(noData, Sr0Data, 0);
            Assert.Equal("MDF013", ListingFails<MdfFormatException>(noData).DiagnosticCode);
            byte[] noBytes = Original();
            Put(noBytes, CgInvalidationBytes, 0, 4); // The group drops its invalidation byte; SR 0 still declares one.
            Put(noBytes, Cn1Flags, 0, 4);
            Assert.Equal("MDF013", ListingFails<MdfFormatException>(noBytes).DiagnosticCode);
        }

        [Fact]
        public void AnEmptyReductionNeedsNoDataAndYieldsNoBatches()
        {
            byte[] bytes = Original();
            Put(bytes, Sr0Cycles, 0);
            Put(bytes, Sr0Data, 0);
            using (MdfReader reader = Open(bytes))
            {
                MdfSampleReduction reduction = Reductions(reader)[0];
                Assert.Equal(0UL, reduction.CycleCount);
                Assert.Empty(reader.ReadSampleReduction(reduction, Speed));
                Assert.Empty(reader.ReadSampleReduction(reduction, Speed, 0, 0));
                Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadSampleReduction(reduction, Speed, 1));
            }
        }

        [Fact]
        public void UnknownFlagsMakeValuesUnsupportedButKeepTheDescriptor()
        {
            byte[] bytes = Original();
            Put(bytes, Sr1Flags, 4, 1);
            using (MdfReader reader = Open(bytes))
            {
                MdfSampleReduction reduction = Reductions(reader)[1];
                Assert.Equal(4, reduction.Flags);
                Assert.False(reduction.CanReadValues);
                Assert.NotNull(reduction.UnsupportedReason);
                Assert.Throws<NotSupportedException>(() => reader.ReadSampleReduction(reduction, Speed));
                Assert.Equal(3, reader.ReadSampleReduction(Reductions(reader)[0], Speed).Single().CycleCount);
            }
        }

        [Fact]
        public void StoredLengthsMustMatchTheDeclaredCycles()
        {
            byte[] longer = Original();
            Put(longer, Sr0Cycles, 4);
            Assert.Equal("MDF013", ValuesFail<MdfFormatException>(longer, 0).DiagnosticCode);
            byte[] widened = Original();
            Put(widened, Sr1Flags, 1, 1); // 34-byte records no longer tile the 66-byte DZ payload.
            Assert.Equal("MDF013", ValuesFail<MdfFormatException>(widened, 1).DiagnosticCode);
        }

        [Fact]
        public void TheDataMustBeReductionStorage()
        {
            byte[] records = Original();
            Put(records, Sr0Data, Dt);
            ValuesFail<NotSupportedException>(records, 0);
            byte[] original = Original();
            original[DzOriginalType + 1] = (byte)'T'; // DZ of "RT"
            ValuesFail<NotSupportedException>(original, 1);
        }

        [Fact]
        public void ANullRiLinkMeansEveryReductionValueIsValid()
        {
            byte[] bytes = Original("420");
            using (MdfReader reader = Open(bytes))
            {
                MdfSampleReductionBatch batch = reader.ReadSampleReduction(Reductions(reader)[0], Speed).Single();
                Assert.False(batch.Mean.GetValue(1, 0).IsValid);
                Assert.False(batch.Maximum.GetValue(1, 0).IsValid);
            }
            Put(bytes, Ld420Invalidation, 0);
            using (MdfReader reader = Open(bytes))
            {
                MdfSampleReductionBatch batch = reader.ReadSampleReduction(Reductions(reader)[0], Speed).Single();
                for (int row = 0; row < 3; row++)
                {
                    Assert.True(batch.Mean.GetValue(row, 0).IsValid);
                    Assert.True(batch.Minimum.GetValue(row, 0).IsValid);
                }
                Assert.Equal(1500UL, batch.Mean.GetValue(1, 0).UnsignedValue);
            }
        }

        [Fact]
        public void AnMdf3RawRangeOutsideTheSourceIsRejected()
        {
            byte[] bytes = Original("330");
            Put(bytes, Sr330Cycles, 1000, 4);
            Assert.Equal("MDF007", ListingFails<MdfFormatException>(bytes).DiagnosticCode);
        }

        [Fact]
        public void ArgumentsAreValidatedEagerly()
        {
            using (MdfReader reader = Open(Original()))
            using (MdfReader other = Open(Original()))
            {
                MdfSampleReduction reduction = Reductions(reader)[0];
                Assert.Throws<ArgumentNullException>(() => reader.ReadSampleReductions(null));
                Assert.Throws<ArgumentException>(() => other.ReadSampleReductions(reduction.Group));
                Assert.Throws<ArgumentNullException>(() => reader.ReadSampleReduction(null, Speed));
                Assert.Throws<ArgumentException>(() => other.ReadSampleReduction(reduction, Speed));
                Assert.Throws<ArgumentNullException>(() => reader.ReadSampleReduction(reduction, null));
                Assert.Throws<ArgumentException>(() => reader.ReadSampleReduction(reduction, new int[0]));
                Assert.Throws<ArgumentException>(() => reader.ReadSampleReduction(reduction, new[] { 1, 1 }));
                Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadSampleReduction(reduction, new[] { 3 }));
                Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadSampleReduction(reduction, Speed, 4));
                Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadSampleReduction(reduction, Speed, 1, 3));
                Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadSampleReduction(reduction, Speed, batchSize: 0));
                Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadSampleReduction(reduction, Speed, representation: (MdfValueRepresentation)7));
                Assert.Equal(2, reader.ReadSampleReduction(reduction, Speed, 1).Single().CycleCount);
            }
        }

        [Fact]
        public void OneEnumerationHoldsTheOperationGate()
        {
            using (MdfReader reader = Open(Original()))
            {
                MdfSampleReduction reduction = Reductions(reader)[0];
                using (IEnumerator<MdfSampleReductionBatch> batches = reader.ReadSampleReduction(reduction, Speed, batchSize: 1).GetEnumerator())
                {
                    Assert.True(batches.MoveNext());
                    Assert.Throws<InvalidOperationException>(() => reader.ReadSampleReductions(reduction.Group));
                    Assert.Throws<InvalidOperationException>(() => reader.ReadSampleReduction(reduction, Speed));
                }
                Assert.Equal(3, reader.ReadSampleReduction(reduction, Speed).Single().CycleCount);
            }
        }

        [Fact]
        public void TheBatchByteLimitCoversAllThreeParts()
        {
            // One selected scalar: 3 x (128 + 32) header bytes and 3 x 40 bytes per cycle.
            MdfReaderOptions tight = new MdfReaderOptions { MaximumSampleBatchBytes = 600 };
            using (MdfReader reader = Open(Original(), tight))
            {
                List<MdfSampleReductionBatch> batches = reader.ReadSampleReduction(Reductions(reader)[0], Speed).ToList();
                Assert.Equal(3, batches.Count);
                Assert.Equal(new ulong[] { 0, 1, 2 }, batches.Select(b => b.FirstCycleIndex));
                Assert.Equal(new ulong[] { 1100, 1500, 1900 }, batches.Select(b => b.Mean.GetValue(0, 0).UnsignedValue));
            }
            MdfReaderOptions tooTight = new MdfReaderOptions { MaximumSampleBatchBytes = 599 };
            using (MdfReader reader = Open(Original(), tooTight))
            {
                MdfSampleReduction reduction = Reductions(reader)[0];
                MdfResourceLimitException error = Assert.Throws<MdfResourceLimitException>(() => reader.ReadSampleReduction(reduction, Speed));
                Assert.Equal(nameof(MdfReaderOptions.MaximumSampleBatchBytes), error.LimitName);
                Assert.Empty(reader.ReadSampleReduction(reduction, Speed, cycleCount: 0));
            }
        }
    }
}
