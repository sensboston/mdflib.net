// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Threading;
using MdfLib.Formats;
using MdfLib.Internal;
using MdfLib.IO;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Formats
{
    public sealed class BlockResolverTests
    {
        [Fact]
        public void NullAndCachedSharedLinksDoNotPerformMoreIoButStillCheckContracts()
        {
            using (TestReadStream source = Source(Block("##TX", 24)))
            using (BinaryInput input = new BinaryInput(source, true))
            {
                BlockResolver resolver = Resolver(input);
                Assert.Null(resolver.Resolve(0));
                Assert.Equal(0, source.ReadCalls);
                BlockHeader first = resolver.Resolve(64, "##TX", 24);
                int reads = source.ReadCalls;
                Assert.Same(first, resolver.Resolve(64, "##TX", 24));
                Assert.Equal(reads, source.ReadCalls);
                Assert.Equal("MDF005", Assert.Throws<MdfFormatException>(() => resolver.Resolve(64, "##MD")).DiagnosticCode);
                Assert.Equal("MDF006", Assert.Throws<MdfFormatException>(() => resolver.Resolve(64, minimumSize: 25)).DiagnosticCode);
                using (CancellationTokenSource token = new CancellationTokenSource())
                {
                    token.Cancel();
                    Assert.Throws<OperationCanceledException>(() => resolver.Resolve(64, cancellationToken: token.Token));
                }
            }
        }

        [Fact]
        public void ResolverIdentityIncludesTheDocumentAndPayloadCannotCrossDocuments()
        {
            using (TestReadStream a = Source(Block("##TX", 24)))
            using (TestReadStream b = Source(Block("##MD", 24)))
            using (BinaryInput ia = new BinaryInput(a, true))
            using (BinaryInput ib = new BinaryInput(b, true))
            {
                BlockResolver ra = Resolver(ia), rb = Resolver(ib);
                BlockHeader ha = ra.Resolve(64), hb = rb.Resolve(64);
                Assert.NotSame(ha, hb);
                Assert.Equal("##TX", ha.Type);
                Assert.Equal("##MD", hb.Type);
                Assert.Throws<ArgumentException>(() => ra.ReadPayload(hb, 0));
            }
        }

        [Theory]
        [InlineData(1UL, "MDF007")]
        [InlineData(63UL, "MDF007")]
        [InlineData(65UL, "MDF008")]
        [InlineData(248UL, "MDF007")]
        [InlineData(0x8000000000000000UL, "MDF007")]
        [InlineData(ulong.MaxValue, "MDF007")]
        public void InvalidAddressesFailBeforeAnySeekOrRead(ulong offset, string code)
        {
            using (TestReadStream source = new TestReadStream(256L))
            using (BinaryInput input = new BinaryInput(source, true))
            {
                Assert.Equal(code, Assert.Throws<MdfFormatException>(() => Resolver(input).Resolve(offset)).DiagnosticCode);
                Assert.Equal(0, source.SeekCalls);
                Assert.Equal(0, source.ReadCalls);
            }
        }

        [Theory]
        [InlineData(0UL)]
        [InlineData(23UL)]
        [InlineData(ulong.MaxValue)]
        public void InvalidWireLengthsDoNotReachLinkAllocation(ulong length)
        {
            byte[] block = Block("##ZZ", 24);
            Put64(block, 8, length);
            using (TestReadStream source = Source(block))
            using (BinaryInput input = new BinaryInput(source, true))
            {
                Assert.Equal("MDF006", Assert.Throws<MdfFormatException>(() => Resolver(input).Resolve(64)).DiagnosticCode);
                Assert.Equal(1, source.ReadCalls);
            }
        }

        [Theory]
        [InlineData(1UL)]
        [InlineData(ulong.MaxValue)]
        public void LinkCountMustFitInsideTheDeclaredBlockBeforeMultiplication(ulong count)
        {
            byte[] block = Block("##ZZ", 24);
            Put64(block, 16, count);
            using (TestReadStream source = Source(block))
            using (BinaryInput input = new BinaryInput(source, true))
            {
                Assert.Equal("MDF006", Assert.Throws<MdfFormatException>(() => Resolver(input).Resolve(64)).DiagnosticCode);
                Assert.Equal(1, source.ReadCalls);
            }
        }

        [Theory]
        [InlineData("MaximumLinksPerBlock")]
        [InlineData("MaximumTotalLinks")]
        [InlineData("MaximumReadBufferBytes")]
        [InlineData("MaximumMetadataBytes")]
        public void UntrustedLinkArraysAreBudgetedBeforeTheyAreRead(string setting)
        {
            MdfReaderOptions options = new MdfReaderOptions();
            if (setting == "MaximumLinksPerBlock") { options.MaximumLinksPerBlock = 1; }
            if (setting == "MaximumTotalLinks") { options.MaximumTotalLinks = 1; }
            if (setting == "MaximumReadBufferBytes") { options.MaximumReadBufferBytes = 8; }
            if (setting == "MaximumMetadataBytes") { options.MaximumMetadataBytes = 143; }
            using (TestReadStream source = Source(Block("##ZZ", 40, 0, 0)))
            using (BinaryInput input = new BinaryInput(source, true))
            {
                MdfResourceLimitException error = Assert.Throws<MdfResourceLimitException>(() => Resolver(input, options).Resolve(64));
                Assert.Equal(setting, error.LimitName);
                Assert.Equal(1, source.ReadCalls);
                Assert.Equal(88L, source.Position);
            }
        }

        [Theory]
        [InlineData(8UL, "MDF007")]
        [InlineData(96UL, "MDF007")]
        [InlineData(65UL, "MDF008")]
        [InlineData(ulong.MaxValue, "MDF007")]
        public void OutgoingLinkDiagnosticsIdentifyTheLinkField(ulong target, string code)
        {
            using (TestReadStream source = Source(Block("##ZZ", 32, target)))
            using (BinaryInput input = new BinaryInput(source, true))
            {
                MdfFormatException error = Assert.Throws<MdfFormatException>(() => Resolver(input).Resolve(64));
                Assert.Equal(code, error.DiagnosticCode);
                Assert.Equal(88UL, error.Offset);
                Assert.Equal("##ZZ", error.BlockType);
            }
        }

        [Fact]
        public void TotalCountsAreChargedOncePerCachedHeaderAndAccumulateAcrossBlocks()
        {
            byte[] bytes = new byte[144];
            Array.Copy(Block("##ZZ", 40, 0, 0), 0, bytes, 64, 40);
            Array.Copy(Block("##TX", 40, 0, 0), 0, bytes, 104, 40);
            using (TestReadStream source = new TestReadStream(bytes))
            using (BinaryInput input = new BinaryInput(source, true))
            {
                BlockResolver resolver = Resolver(input, new MdfReaderOptions { MaximumTotalLinks = 3 });
                BlockHeader first = resolver.Resolve(64);
                Assert.Same(first, resolver.Resolve(64));
                MdfResourceLimitException error = Assert.Throws<MdfResourceLimitException>(() => resolver.Resolve(104));
                Assert.Equal("MaximumTotalLinks", error.LimitName);
                Assert.Equal(4UL, error.Requested);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void OverlappingBlocksAreRejectedRegardlessOfDiscoveryOrder(bool reverse)
        {
            byte[] outer = Block("##ZZ", 64);
            Array.Copy(Block("##TX", 24), 0, outer, 24, 24);
            using (TestReadStream source = Source(outer))
            using (BinaryInput input = new BinaryInput(source, true))
            {
                BlockResolver resolver = Resolver(input);
                resolver.Resolve(reverse ? 88UL : 64UL);
                Assert.Equal("MDF011", Assert.Throws<MdfFormatException>(() => resolver.Resolve(reverse ? 64UL : 88UL)).DiagnosticCode);
            }
        }

        [Fact]
        public void AdjacentBlocksAndUnalignedLengthsAreNotFalseOverlapErrors()
        {
            byte[] bytes = new byte[120];
            Array.Copy(Block("##ZZ", 25), 0, bytes, 64, 25);
            Array.Copy(Block("##TX", 24), 0, bytes, 96, 24);
            using (TestReadStream source = new TestReadStream(bytes))
            using (BinaryInput input = new BinaryInput(source, true))
            {
                BlockResolver resolver = Resolver(input);
                Assert.Equal(25UL, resolver.Resolve(64).Length);
                Assert.Equal("##TX", resolver.Resolve(96).Type);
            }
        }

        [Fact]
        public void PayloadLimitsAreCheckedBeforeAllocationAndRepeatedReadsSpendBudget()
        {
            using (TestReadStream source = Source(Block("##ZZ", 28)))
            using (BinaryInput input = new BinaryInput(source, true))
            {
                BlockResolver resolver = Resolver(input, new MdfReaderOptions { MaximumMetadataBytes = 132 });
                BlockHeader header = resolver.Resolve(64);
                Assert.Same(Array.Empty<byte>(), resolver.ReadPayload(header, 0));
                Assert.Equal(4, resolver.ReadPayload(header, 4).Length);
                int reads = source.ReadCalls;
                Assert.Equal("MaximumMetadataBytes", Assert.Throws<MdfResourceLimitException>(() => resolver.ReadPayload(header, 1)).LimitName);
                Assert.Equal(reads, source.ReadCalls);
                Assert.Throws<ArgumentOutOfRangeException>(() => resolver.ReadPayload(header, -1));
                Assert.Throws<ArgumentNullException>(() => resolver.ReadPayload(null, 0));
                Assert.Equal("MDF006", Assert.Throws<MdfFormatException>(() => resolver.ReadPayload(header, 5)).DiagnosticCode);
            }
        }

        [Fact]
        public void LargeDataBlocksAreRangeCheckedButNotMaterializedByResolve()
        {
            using (SparseReadStream source = new SparseReadStream((long)int.MaxValue + 1024))
            using (BinaryInput input = new BinaryInput(source, true))
            {
                byte[] common = Block("##DT", 24);
                Put64(common, 8, (ulong)int.MaxValue + 128);
                source.Add(64, common);
                BlockResolver resolver = Resolver(input);
                BlockHeader block = resolver.Resolve(64);
                Assert.Equal((ulong)int.MaxValue + 128, block.Length);
                Assert.Equal(1, source.ReadCalls);
                Assert.Equal("MaximumReadBufferBytes", Assert.Throws<MdfResourceLimitException>(() => resolver.ReadPayload(block, int.MaxValue)).LimitName);
                Assert.Equal(1, source.ReadCalls);
            }
        }

        [Theory]
        [InlineData(2147483648L)]
        [InlineData(long.MaxValue - 31)]
        public void SixtyFourBitAddressesAreNotNarrowed( long offset)
        {
            using (SparseReadStream source = new SparseReadStream(long.MaxValue))
            using (BinaryInput input = new BinaryInput(source, true))
            {
                source.Add(offset, Block("##TX", 24));
                BlockHeader block = Resolver(input).Resolve((ulong)offset, "##TX", 24);
                Assert.Equal((ulong)offset, block.Offset);
                Assert.Equal(offset + 24, input.Position);
                Assert.Equal(1, source.ReadCalls);
            }
        }

        [Fact]
        public void ManyDisjointHeadersAreResolvedOnceAndReusedWithoutMoreIo()
        {
            const int count = 5_000;
            byte[] bytes = new byte[64 + count * 24];
            byte[] header = Block("##TX", 24);
            for (int index = 0; index < count; index++) { Array.Copy(header, 0, bytes, 64 + index * 24, 24); }
            using (TestReadStream source = new TestReadStream(bytes))
            using (BinaryInput input = new BinaryInput(source, true))
            {
                BlockResolver resolver = Resolver(input);
                BlockHeader[] resolved = new BlockHeader[count];
                for (int index = 0; index < count; index++)
                {
                    resolved[index] = resolver.Resolve((ulong)(64 + index * 24), "##TX", 24);
                }
                Assert.Equal(count, source.ReadCalls);
                for (int index = count - 1; index >= 0; index--)
                {
                    Assert.Same(resolved[index], resolver.Resolve((ulong)(64 + index * 24), "##TX", 24));
                }
                Assert.Equal(count, source.ReadCalls);
            }
        }

        [Fact]
        public void Mdf3HasNoMdf4AlignmentRuleAndItsRawPointersNeedNotHaveBlockHeaders()
        {
            byte[] bytes = new byte[76];
            bytes[65] = (byte)'H'; bytes[66] = (byte)'D'; bytes[67] = 8; // Block @65, size8, one UInt32 pointer.
            bytes[69] = 75; // Raw target is the last available byte, not a framed block.
            using (TestReadStream source = new TestReadStream(bytes))
            using (BinaryInput input = new BinaryInput(source, true))
            {
                BlockResolver resolver = Resolver(input, mdf4: false);
                BlockHeader block = resolver.Resolve(65, "HD", 8, 1);
                Assert.Equal(75UL, block.Links[0]);
                Assert.Same(block, resolver.Resolve(65, "HD", 8, 1));
                Assert.Throws<InvalidOperationException>(() => resolver.Resolve(65, "HD", 8, 0));
                Assert.Equal("MDF007", Assert.Throws<MdfFormatException>(() => resolver.Resolve(75, "TX")).DiagnosticCode);
            }
        }

        [Fact]
        public void HeaderlessReservationsAreBudgetedIdempotentNonoverlappingAndCannotGrowTheDocument()
        {
            byte[] bytes = new byte[512];
            bytes[64] = (byte)'T'; bytes[65] = (byte)'X'; bytes[66] = 4;
            using (TestReadStream source = new TestReadStream(bytes))
            using (BinaryInput input = new BinaryInput(source, true))
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                BlockResolver resolver = Resolver(input, new MdfReaderOptions { MaximumMetadataBytes = 128 }, false);
                resolver.RegisterRawDataRange(64, 64, default(CancellationToken));
                for (int index = 0; index < 100; index++) { resolver.RegisterRawDataRange(64, 64, default(CancellationToken)); }
                Assert.Equal(0, source.ReadCalls);
                Assert.Equal("MDF011", Assert.Throws<MdfFormatException>(() => resolver.RegisterRawDataRange(80, 1, default(CancellationToken))).DiagnosticCode);
                MdfResourceLimitException budget = Assert.Throws<MdfResourceLimitException>(() => resolver.RegisterRawDataRange(128, 64, default(CancellationToken)));
                Assert.Equal("MaximumMetadataBytes", budget.LimitName);
                Assert.Equal(256UL, budget.Requested);
                Assert.Equal("MDF011", Assert.Throws<MdfFormatException>(() => resolver.Resolve(64, "TX", 4)).DiagnosticCode);
                cancellation.Cancel();
                Assert.Throws<OperationCanceledException>(() => resolver.RegisterRawDataRange(64, 64, cancellation.Token));
                source.ReportedLength = 1024;
                Assert.Equal("MDF007", Assert.Throws<MdfFormatException>(() => resolver.RegisterRawDataRange(500, 20, default(CancellationToken))).DiagnosticCode);
                resolver.RegisterRawDataRange(64, 64, default(CancellationToken));
                Assert.Equal(1, source.ReadCalls); // Only the explicitly attempted conflicting TX header.
                Assert.Equal(0, source.WriteCalls);
            }
        }

        [Fact]
        public void TruncationWhileReadingLinksPreservesItsCause()
        {
            using (TestReadStream source = Source(Block("##ZZ", 32, 0)))
            using (BinaryInput input = new BinaryInput(source, true))
            {
                source.AvailableLength = 90;
                MdfFormatException error = Assert.Throws<MdfFormatException>(() => Resolver(input).Resolve(64));
                Assert.Equal("MDF004", error.DiagnosticCode);
                Assert.IsType<EndOfStreamException>(error.InnerException);
            }
        }

        internal static BlockResolver Resolver(BinaryInput input, MdfReaderOptions options = null, bool mdf4 = true)
        {
            return new BlockResolver(input, mdf4, new ReadBudget(new ReaderLimits(options)));
        }

        private static TestReadStream Source(byte[] block)
        {
            byte[] bytes = new byte[64 + block.Length];
            Array.Copy(block, 0, bytes, 64, block.Length);
            return new TestReadStream(bytes);
        }

        private static byte[] Block(string type, int size, params ulong[] links)
        {
            byte[] data = new byte[size];
            for (int i = 0; i < 4; i++) { data[i] = (byte)type[i]; }
            Put64(data, 8, (ulong)size);
            Put64(data, 16, (ulong)links.Length);
            for (int i = 0; i < links.Length; i++) { Put64(data, 24 + 8 * i, links[i]); }
            return data;
        }

        private static void Put64(byte[] bytes, int offset, ulong value)
        {
            for (int i = 0; i < 8; i++) { bytes[offset + i] = (byte)(value >> (8 * i)); }
        }
    }
}
