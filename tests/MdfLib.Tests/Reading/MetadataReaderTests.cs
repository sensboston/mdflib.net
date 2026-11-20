// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using MdfLib.TestSupport;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Reading
{
    public sealed class MetadataReaderTests
    {
        [Fact]
        public void MetadataIsExplicitCachedReadOnlyAndNeverReadsDtHeadersOrPayloads()
        {
            using (TestReadStream source = new TestReadStream(M3Wire.Read()))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                Assert.Equal(168L, source.Position);
                IReadOnlyList<MdfDataGroup> catalog = reader.ReadMetadata();
                int calls = source.ReadCalls;
                source.ReadFailure = new IOException("A successful metadata cache must perform no more reads.");
                Assert.Same(catalog, reader.ReadMetadata());
                Assert.Equal(calls, source.ReadCalls);
                Assert.All(source.ReadRanges, range =>
                {
                    Assert.False(range.Item1 < 3027 && range.Item1 + range.Item2 > M3Wire.Dt);
                    Assert.False(range.Item1 < 3506 && range.Item1 + range.Item2 > 3480);
                });
                Assert.Throws<NotSupportedException>(() => ((IList<MdfDataGroup>)catalog).Clear());
                Assert.Throws<NotSupportedException>(() => ((IList<MdfChannelGroup>)catalog[0].ChannelGroups).Clear());
                Assert.Throws<NotSupportedException>(() => ((IList<MdfChannel>)catalog[0].ChannelGroups[0].Channels).Clear());
                Assert.Equal(0, source.WriteCalls);
                Assert.Equal(0, source.FlushCalls);
            }
        }

        [Theory]
        [InlineData(M3Wire.Dg, 0)]
        [InlineData(M3Wire.Cg, 0)]
        [InlineData(M3Wire.LastCn, 0)]
        public void SiblingCyclesAreDetectedWithoutRecursiveWalking(int block, int link)
        {
            byte[] bytes = M3Wire.Read();
            M3Wire.Put(bytes, M3Wire.Link(block, link), (ulong)block);
            Failure(bytes, "MDF010");
        }

        [Theory]
        [InlineData(M3Wire.Dg2, 1, M3Wire.Cg)]
        [InlineData(M3Wire.Cg2, 1, M3Wire.Cn)]
        public void StructuralBlocksCannotHaveTwoDifferentParents(int block, int link, int target)
        {
            byte[] bytes = M3Wire.Read();
            M3Wire.Put(bytes, M3Wire.Link(block, link), (ulong)target);
            Failure(bytes, "MDF014");
        }

        [Theory]
        [InlineData(M3Wire.Dg, 3)]
        [InlineData(M3Wire.Cg, 5)]
        [InlineData(M3Wire.Cn, 7)]
        [InlineData(M3Wire.Si, 2)]
        public void RequiredBaseLinkCountsAreNotInferredFromRemainingBytes(int block, int count)
        {
            byte[] bytes = M3Wire.Read();
            M3Wire.Put(bytes, block + 16, (ulong)count);
            Failure(bytes, "MDF006");
        }

        [Theory]
        [InlineData(3, 8, 1)]
        [InlineData(8, 0, 4)]
        [InlineData(4, uint.MaxValue, 4)]
        [InlineData(16, 16, 4)]
        public void NumericAndInvalidationFieldsAreValidatedBeforeDataIo(int field, uint value, int width)
        {
            byte[] bytes = M3Wire.Read();
            int cn = 528;
            M3Wire.Put(bytes, M3Wire.Payload(bytes, cn) + field, value, width);
            Failure(bytes, "MDF013");
        }

        [Theory]
        [InlineData(4, 1)]
        [InlineData(6, 0)]
        [InlineData(6, 1)]
        [InlineData(6, 65535)]
        public void ConversionCountsCannotOverrunTheDeclaredSections(int field, int value)
        {
            byte[] bytes = M3Wire.Read();
            M3Wire.Put(bytes, M3Wire.Payload(bytes, M3Wire.Cc) + field, (ulong)value, 2);
            Failure(bytes, "MDF013");
        }

        [Theory]
        [InlineData("<!DOCTYPE DGcomment SYSTEM 'file:///never-read'><DGcomment/>")]
        [InlineData("<!DOCTYPE DGcomment SYSTEM 'https://example.invalid/never-read'><DGcomment/>")]
        [InlineData("<!DOCTYPE DGcomment [<!ENTITY x 'expanded'>]><DGcomment>&x;</DGcomment>")]
        [InlineData("<DGcomment><TX>unclosed")]
        public void AllMdCommentsAreValidatedAndDtdEntitiesAreProhibited(string xml)
        {
            byte[] bytes = M3Wire.Read();
            int address = M3Wire.AppendText(ref bytes, xml);
            M3Wire.Put(bytes, M3Wire.Link(M3Wire.Dg, 3), (ulong)address);
            Failure(bytes, "MDF012");
        }

        [Fact]
        public void Utf8IsStrictButFirstNulIgnoresTrailingBytesAndNoNulIsAllowed()
        {
            byte[] bytes = M3Wire.Read();
            int address = M3Wire.AppendBlock(ref bytes, "TX", new byte[] { 0xc3, 0x28 });
            M3Wire.Put(bytes, M3Wire.Link(M3Wire.Cn, 2), (ulong)address);
            Failure(bytes, "MDF009");
            bytes[address + 24] = (byte)'A'; bytes[address + 25] = 0;
            using (MemoryStream source = new MemoryStream(bytes, false))
            using (MdfReader reader = MdfReader.Open(source))
            {
                Assert.Equal("A", reader.ReadMetadata()[0].ChannelGroups[0].Channels[0].Name);
            }
            address = M3Wire.AppendBlock(ref bytes, "TX", new byte[] { 32, (byte)'B', 0, 0xff, 0xff });
            M3Wire.Put(bytes, M3Wire.Link(M3Wire.Cn, 2), (ulong)address);
            using (MemoryStream source = new MemoryStream(bytes, false))
            using (MdfReader reader = MdfReader.Open(source))
            {
                Assert.Equal(" B", reader.ReadMetadata()[0].ChannelGroups[0].Channels[0].Name);
            }
            address = M3Wire.AppendText(ref bytes, " μ no terminator ", false);
            M3Wire.Put(bytes, M3Wire.Link(M3Wire.Cn, 2), (ulong)address);
            using (MemoryStream source = new MemoryStream(bytes, false))
            using (MdfReader reader = MdfReader.Open(source))
            {
                Assert.Equal(" μ no terminator ", reader.ReadMetadata()[0].ChannelGroups[0].Channels[0].Name);
            }
        }

        [Theory]
        [InlineData("<other><TX>x</TX></other>")]
        [InlineData("<CNunit><TX><nested>x</nested></TX></CNunit>")]
        [InlineData("<CCunit><TX>x</TX><TX>y</TX></CCunit>")]
        public void UnsupportedUnitXmlShapesAreNotGuessedAsPlainText(string xml)
        {
            byte[] bytes = M3Wire.Read();
            int address = M3Wire.AppendText(ref bytes, xml);
            M3Wire.Put(bytes, M3Wire.Link(M3Wire.LastCn, 6), (ulong)address);
            using (MemoryStream source = new MemoryStream(bytes, false))
            using (MdfReader reader = MdfReader.Open(source))
            {
                Assert.Throws<NotSupportedException>(() => reader.ReadMetadata());
            }
        }

        [Fact]
        public void NamesRequireTxAndAbsentChannelUnitFallsBackToConversionUnit()
        {
            byte[] bytes = M3Wire.Read();
            M3Wire.Put(bytes, M3Wire.Link(M3Wire.LastCn, 6), 0);
            using (MemoryStream source = new MemoryStream(bytes, false))
            using (MdfReader reader = MdfReader.Open(source))
            {
                Assert.Equal("fallback unit", reader.ReadMetadata()[0].ChannelGroups[0].Channels[8].Unit);
            }
            M3Wire.Put(bytes, M3Wire.Link(M3Wire.Cn, 2), 2384);
            Failure(bytes, "MDF005");
        }

        [Theory]
        [InlineData("MaximumTextBytes", 16)]
        [InlineData("MaximumXmlCharacters", 16)]
        [InlineData("MaximumXmlDepth", 1)]
        [InlineData("MaximumTraversalDepth", 4)]
        [InlineData("MaximumReadBufferBytes", 64)]
        [InlineData("MaximumMetadataBytes", 4096)]
        public void TextXmlDepthAndModelAllowancesAreEnforcedWithNamedDiagnostics(string property, int value)
        {
            MdfReaderOptions options = new MdfReaderOptions();
            System.Reflection.PropertyInfo member = typeof(MdfReaderOptions).GetProperty(property);
            member.SetValue(options, Convert.ChangeType(value, member.PropertyType));
            using (TestReadStream source = new TestReadStream(M3Wire.Read()))
            using (MdfReader reader = MdfReader.Open(source, true, options))
            {
                int before = source.ReadCalls;
                MdfResourceLimitException error = Assert.Throws<MdfResourceLimitException>(() => reader.ReadMetadata());
                Assert.Equal(property, error.LimitName);
                Assert.True(error.Requested > error.Limit);
                if (property == "MaximumMetadataBytes") { Assert.Equal(before, source.ReadCalls); }
                Assert.Equal(0, source.DisposeCalls);
            }
        }

        [Fact]
        public void MetadataFailurePublishesNoPartialCatalogAndRetryDoesNotCloseTheSource()
        {
            byte[] bytes = M3Wire.Read();
            int address = M3Wire.AppendBlock(ref bytes, "TX", new byte[] { 0xff });
            M3Wire.Put(bytes, M3Wire.Link(M3Wire.Cn, 2), (ulong)address);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                Assert.Equal("MDF009", Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
                Assert.Equal(0, source.DisposeCalls);
                bytes[address + 24] = (byte)'X';
                Assert.Equal("X", reader.ReadMetadata()[0].ChannelGroups[0].Channels[0].Name);
            }
        }

        [Fact]
        public void CancellationAndReentrantMetadataCallsReleaseOrRespectTheGate()
        {
            using (TestReadStream source = new TestReadStream(M3Wire.Read()))
            using (MdfReader reader = MdfReader.Open(source, true))
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                source.AfterRead = cancellation.Cancel;
                Assert.Throws<OperationCanceledException>(() => reader.ReadMetadata(cancellation.Token));
                bool reentered = false;
                source.AfterRead = () =>
                {
                    reentered = true;
                    Assert.Throws<InvalidOperationException>(() => reader.ReadMetadata());
                    source.AfterRead = null;
                };
                Assert.Equal(2, reader.ReadMetadata().Count);
                Assert.True(reentered);
                int before = source.ReadCalls;
                Assert.Throws<OperationCanceledException>(() => reader.ReadMetadata(cancellation.Token));
                Assert.Equal(before, source.ReadCalls);
            }
        }

        [Fact]
        public void EmptyMdf3AndMdf4CatalogsAreSupportedWithoutInferringSamples()
        {
            using (MemoryStream source = new MemoryStream(FixtureCorpus.Read("m2-mdf330-le"), false))
            using (MdfReader reader = MdfReader.Open(source))
            {
                Assert.Empty(reader.ReadMetadata());
            }
            using (MemoryStream source = new MemoryStream(FixtureCorpus.Read("m2-mdf410-header"), false))
            using (MdfReader reader = MdfReader.Open(source))
            {
                Assert.Empty(reader.ReadMetadata());
            }
        }

        [Fact]
        public void FiveThousandActualCnSiblingsUseHierarchyDepthNotListLength()
        {
            const int count = 5000;
            byte[] original = M3Wire.Read();
            byte[] bytes = new byte[M3Wire.Cn + count * 160];
            Array.Copy(original, bytes, M3Wire.Cn);
            M3Wire.Put(bytes, M3Wire.Link(M3Wire.Dg, 0), 0);
            M3Wire.Put(bytes, M3Wire.Link(M3Wire.Dg, 2), 0);
            M3Wire.Put(bytes, M3Wire.Link(M3Wire.Dg, 3), 0);
            for (int link = 2; link <= 5; link++) { M3Wire.Put(bytes, M3Wire.Link(M3Wire.Cg, link), 0); }
            M3Wire.Put(bytes, M3Wire.Payload(bytes, M3Wire.Cg) + 8, 0);
            M3Wire.Put(bytes, M3Wire.Payload(bytes, M3Wire.Cg) + 24, 8, 4);
            M3Wire.Put(bytes, M3Wire.Payload(bytes, M3Wire.Cg) + 28, 0, 4);
            for (int index = 0; index < count; index++)
            {
                int address = M3Wire.Cn + index * 160;
                Array.Copy(original, M3Wire.Cn, bytes, address, 160);
                Array.Clear(bytes, address + 24, 64);
                if (index + 1 < count) { M3Wire.Put(bytes, address + 24, (ulong)address + 160); }
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false), options: new MdfReaderOptions { MaximumTraversalDepth = 3 }))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(count, group.Channels.Count);
                Assert.Equal(count - 1, group.Channels[count - 1].Index);
                Assert.All(group.Channels, channel => Assert.True(channel.CanReadRaw));
                Assert.Empty(reader.ReadSamples(group, new[] { count - 1 }));
            }
        }

        [Theory]
        [InlineData("MaximumTextBytes")]
        [InlineData("MaximumXmlCharacters")]
        [InlineData("MaximumXmlDepth")]
        [InlineData("MaximumSampleBatchBytes")]
        public void NegativeNewLimitsAreRejectedBeforeSourceOwnership(string property)
        {
            MdfReaderOptions options = new MdfReaderOptions();
            typeof(MdfReaderOptions).GetProperty(property).SetValue(options, -1);
            using (TestReadStream source = new TestReadStream(M3Wire.Read()))
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => MdfReader.Open(source, options: options));
                Assert.Equal(0, source.ReadCalls);
                Assert.Equal(0, source.DisposeCalls);
            }
        }

        private static void Failure(byte[] bytes, string code)
        {
            using (MemoryStream source = new MemoryStream(bytes, false))
            using (MdfReader reader = MdfReader.Open(source))
            {
                Assert.Equal(code, Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
            }
        }
    }
}
