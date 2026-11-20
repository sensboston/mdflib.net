// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Reading
{
    public sealed class Mdf330MetadataTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CatalogIsExplicitCachedReadOnlyAndReadsNoHeaderlessSampleBytes(bool bigEndian)
        {
            using (TestReadStream source = new TestReadStream(M4Wire.Read(bigEndian)) { MaximumBytesPerRead = 3 })
            using (MdfReader reader = MdfReader.Open(source, true, new MdfReaderOptions { MaximumXmlCharacters = 1, MaximumXmlDepth = 1 }))
            {
                Assert.Equal(272L, source.Position);
                IReadOnlyList<MdfDataGroup> catalog = reader.ReadMetadata();
                Assert.Equal(2, catalog.Count);
                Assert.All(source.ReadRanges, range =>
                {
                    Assert.False(range.Item1 < M4Wire.Dg2 && range.Item1 + range.Item2 > M4Wire.Raw);
                    Assert.False(range.Item1 < 21561 && range.Item1 + range.Item2 > M4Wire.Raw2);
                });
                Assert.Throws<NotSupportedException>(() => ((IList<MdfDataGroup>)catalog).Clear());
                Assert.Throws<NotSupportedException>(() => ((IList<MdfChannelGroup>)catalog[0].ChannelGroups).Clear());
                Assert.Throws<NotSupportedException>(() => ((IList<MdfChannel>)catalog[0].ChannelGroups[0].Channels).Clear());
                int before = source.ReadCalls;
                source.ReadFailure = new IOException("Metadata cache must perform no more reads.");
                Assert.Same(catalog, reader.ReadMetadata());
                Assert.Equal(before, source.ReadCalls);
                Assert.Equal(0, source.WriteCalls);
                Assert.Equal(0, source.FlushCalls);
            }
        }

        [Theory]
        [InlineData(M4Wire.Dg)]
        [InlineData(M4Wire.Cg)]
        [InlineData(M4Wire.LastCn)]
        public void SiblingCyclesAreRejectedIteratively(int address)
        {
            byte[] bytes = M4Wire.Read();
            M4Wire.Put(bytes, address + 4, (ulong)address);
            Failure(bytes, "MDF010");
        }

        [Theory]
        [InlineData(M4Wire.Dg2, M4Wire.Cg)]
        [InlineData(M4Wire.Cg2, M4Wire.Cn)]
        public void StructuralBlocksCannotBeSharedBetweenParents(int address, int target)
        {
            byte[] bytes = M4Wire.Read();
            M4Wire.Put(bytes, address + 8, (ulong)target);
            Failure(bytes, "MDF014");
        }

        [Theory]
        [InlineData(80, 0)]
        [InlineData(80, 3)]
        [InlineData(M4Wire.Dg + 20, 0)]
        [InlineData(M4Wire.Dg + 20, 2)]
        [InlineData(M4Wire.Cg + 18, 0)]
        [InlineData(M4Wire.Cg + 18, 65535)]
        public void DeclaredStructuralCountsMustEqualActualSiblingCounts(int field, int value)
        {
            byte[] bytes = M4Wire.Read();
            M4Wire.Put(bytes, field, (ulong)value, 2);
            Failure(bytes, "MDF013");
        }

        [Theory]
        [InlineData(M4Wire.Dg, 25)]
        [InlineData(M4Wire.Dg, 27)]
        [InlineData(M4Wire.Cg, 27)]
        [InlineData(M4Wire.Cg, 29)]
        [InlineData(M4Wire.Cn, 219)]
        [InlineData(M4Wire.Cn, 221)]
        [InlineData(M4Wire.Cn, 224)]
        [InlineData(M4Wire.Cn, 226)]
        [InlineData(M4Wire.Cn, 227)]
        [InlineData(M4Wire.Cn, 229)]
        public void PartialOrUnknownStructuralTailsAreNotGuessed(int block, int size)
        {
            byte[] bytes = M4Wire.Read();
            M4Wire.Put(bytes, block + 2, (ulong)size, 2);
            Failure(bytes, "MDF006");
        }

        [Theory]
        [InlineData(M4Wire.Cn + 218, uint.MaxValue)]
        [InlineData(M4Wire.Cn + 222, 63U)]
        [InlineData(M4Wire.Cg + 26, 1U)]
        public void AdditionalTailLinksAreRangeCheckedEvenWhenAncillary(int field, uint value)
        {
            byte[] bytes = M4Wire.Read();
            M4Wire.Put(bytes, field, value);
            Failure(bytes, "MDF007");
        }

        [Theory]
        [InlineData(M4Wire.Cn + 218, M4Wire.Cn)]
        [InlineData(M4Wire.Cn + 222, M4Wire.Cg)]
        [InlineData(M4Wire.Cn + 12, M4Wire.Cg)]
        [InlineData(M4Wire.Cn + 20, M4Wire.Cg)]
        public void CachedWrongBlockTypesReportFormatErrorsNotLinkLayoutReinterpretation(int field, int target)
        {
            byte[] bytes = M4Wire.Read();
            M4Wire.Put(bytes, field, (ulong)target);
            Failure(bytes, "MDF005");
        }

        [Theory]
        [InlineData("MaximumLinksPerBlock", 6)]
        [InlineData("MaximumTotalLinks", 17)]
        public void TailLinksConsumeBothPerBlockAndTotalLinkBudgets(string property, int value)
        {
            MdfReaderOptions options = new MdfReaderOptions();
            if (property == "MaximumLinksPerBlock") { options.MaximumLinksPerBlock = value; }
            else { options.MaximumTotalLinks = value; }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(M4Wire.ScalarTemplate(), false), options: options))
            {
                MdfResourceLimitException error = Assert.Throws<MdfResourceLimitException>(() => reader.ReadMetadata());
                Assert.Equal(property, error.LimitName);
                Assert.Equal(property == "MaximumLinksPerBlock" ? 7UL : 18UL, error.Requested);
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(M4Wire.ScalarTemplate(), false), options: new MdfReaderOptions
            {
                MaximumLinksPerBlock = 7, MaximumTotalLinks = 18
            }))
            {
                Assert.Single(reader.ReadMetadata()[0].ChannelGroups[0].Channels);
            }
        }

        [Theory]
        [InlineData("MaximumTextBytes", 16)]
        [InlineData("MaximumTraversalDepth", 3)]
        [InlineData("MaximumReadBufferBytes", 192)]
        [InlineData("MaximumMetadataBytes", 4096)]
        [InlineData("MaximumBlockCount", 3)]
        public void MetadataLimitsAreEnforcedWithNamedDiagnostics(string property, int value)
        {
            MdfReaderOptions options = new MdfReaderOptions();
            System.Reflection.PropertyInfo member = typeof(MdfReaderOptions).GetProperty(property);
            member.SetValue(options, Convert.ChangeType(value, member.PropertyType));
            using (TestReadStream source = new TestReadStream(M4Wire.Read()))
            using (MdfReader reader = MdfReader.Open(source, true, options))
            {
                int before = source.ReadCalls;
                Assert.Equal(property, Assert.Throws<MdfResourceLimitException>(() => reader.ReadMetadata()).LimitName);
                if (property == "MaximumMetadataBytes") { Assert.Equal(before, source.ReadCalls); }
                Assert.Equal(0, source.DisposeCalls);
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(28591)]
        [InlineData(20127)]
        [InlineData(65001)]
        public void DeclaredCodePageIsUsedForTxAndTextNeverBecomesActiveXml(int codePage)
        {
            byte[] bytes = M4Wire.ScalarTemplate();
            M4Wire.Put(bytes, 30, (ulong)codePage, 2);
            string word = codePage == 65001 ? "  Иван" : codePage == 20127 ? "  ASCII" : "  café";
            Encoding encoding = codePage == 65001 ? Encoding.UTF8 : codePage == 20127 ? Encoding.ASCII : Encoding.GetEncoding(28591);
            byte[] name = encoding.GetBytes(word + "  \0").Concat(new byte[] { 0xff, 0xff }).ToArray();
            int address = M4Wire.AppendTx(ref bytes, name);
            M4Wire.Put(bytes, M4Wire.Cn + 218, (ulong)address);
            string comment = "<!DOCTYPE inert SYSTEM 'file:///never-opened'><TX> plain text </TX>";
            address = M4Wire.AppendTx(ref bytes, Encoding.ASCII.GetBytes(comment + "  ")); // No NUL is legal.
            M4Wire.Put(bytes, M4Wire.Cn + 20, (ulong)address);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false), options: new MdfReaderOptions { MaximumXmlCharacters = 1, MaximumXmlDepth = 1 }))
            {
                MdfChannel channel = reader.ReadMetadata()[0].ChannelGroups[0].Channels[0];
                Assert.Equal(word, channel.Name);
                Assert.Equal(comment, channel.Comment);
            }
        }

        [Theory]
        [InlineData(20127)]
        [InlineData(65001)]
        public void MalformedStrictTextIsRejectedAndNoPartialCatalogIsPublished(int codePage)
        {
            byte[] bytes = M4Wire.ScalarTemplate();
            M4Wire.Put(bytes, 30, (ulong)codePage, 2);
            int address = M4Wire.AppendTx(ref bytes, codePage == 65001 ? new byte[] { 0xc3, 0x28 } : new byte[] { 0xe9 });
            M4Wire.Put(bytes, M4Wire.Cn + 20, (ulong)address);
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                Assert.Equal("MDF009", Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
                Assert.Equal("MDF009", Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
                Assert.Equal(0, source.DisposeCalls);
                Assert.Equal("3.30", reader.Header.Version);
            }
        }

        [Fact]
        public void WholeTxExtentIsBudgetedEvenWhenFirstNulIsEarlyAndDescriptionIsFallbackOnly()
        {
            byte[] bytes = M4Wire.ScalarTemplate();
            Array.Copy(Encoding.ASCII.GetBytes("  fixed description  "), 0, bytes, M4Wire.Cn + 58, 21);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                Assert.Equal("  fixed description", reader.ReadMetadata()[0].ChannelGroups[0].Channels[0].Comment);
            }
            int address = M4Wire.AppendTx(ref bytes, new byte[80]);
            M4Wire.Put(bytes, M4Wire.Cn + 20, (ulong)address);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false), options: new MdfReaderOptions { MaximumTextBytes = 16 }))
            {
                Assert.Equal("MaximumTextBytes", Assert.Throws<MdfResourceLimitException>(() => reader.ReadMetadata()).LimitName);
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                Assert.Equal(string.Empty, reader.ReadMetadata()[0].ChannelGroups[0].Channels[0].Comment); // Nonzero empty TX wins.
            }
        }

        [Theory]
        [InlineData(M4Wire.LinearCc + 44, 0)]
        [InlineData(M4Wire.LinearCc + 44, 1)]
        [InlineData(M4Wire.LinearCc + 44, 65535)]
        [InlineData(M4Wire.LinearCc + 2, 61)]
        [InlineData(M4Wire.IdentityCc + 44, 1)]
        [InlineData(M4Wire.IdentityCc + 2, 47)]
        public void SupportedConversionsRequireExactParameterShapes(int field, int value)
        {
            byte[] bytes = M4Wire.Read();
            M4Wire.Put(bytes, field, (ulong)value, 2);
            Failure(bytes, "MDF013");
        }

        [Theory]
        [InlineData(0)]
        [InlineData(256)]
        [InlineData(65535)]
        public void UnknownCeTypesAreExplicitlyUnsupportedNotTruncatedToByte(int type)
        {
            byte[] bytes = M4Wire.Read();
            M4Wire.Put(bytes, M4Wire.Ecu + 4, (ulong)type, 2);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                Assert.Throws<NotSupportedException>(() => reader.ReadMetadata());
            }
        }

        [Fact]
        public void KnownSourceWithWrongSizeIsMalformed()
        {
            byte[] bytes = M4Wire.Read();
            M4Wire.Put(bytes, M4Wire.Ecu + 2, 127, 2);
            Failure(bytes, "MDF006");
        }

        [Theory]
        [InlineData("missing", "MDF013")]
        [InlineData("count", "MDF007")]
        [InlineData("past-end", "MDF007")]
        [InlineData("metadata", "MDF011")]
        [InlineData("one-extra-byte", "MDF011")]
        [InlineData("shared-data", "MDF014")]
        [InlineData("overlapping-data", "MDF011")]
        public void WholeDeclaredHeaderlessRangesAreCheckedWithoutReadingSamples(string mutation, string code)
        {
            byte[] bytes = M4Wire.Read();
            if (mutation == "missing") { M4Wire.Put(bytes, M4Wire.Dg + 16, 0); }
            else if (mutation == "count") { M4Wire.Put(bytes, M4Wire.Cg + 22, uint.MaxValue); }
            else if (mutation == "past-end") { M4Wire.Put(bytes, M4Wire.Dg + 16, (ulong)bytes.Length - 1); }
            else if (mutation == "metadata") { M4Wire.Put(bytes, M4Wire.Dg + 16, M4Wire.Cn); }
            else if (mutation == "one-extra-byte") { M4Wire.Put(bytes, M4Wire.Dg + 16, M4Wire.Raw + 1); }
            else if (mutation == "shared-data") { M4Wire.Put(bytes, M4Wire.Dg2 + 16, M4Wire.Raw); }
            else { M4Wire.Put(bytes, M4Wire.Dg2 + 16, M4Wire.Raw + 88); }
            Failure(bytes, code);
        }

        [Fact]
        public void EmptyGroupsNeedNoRawPointerAndUnknownAncillaryPointersRemainInert()
        {
            byte[] bytes = M4Wire.ScalarTemplate();
            M4Wire.Put(bytes, M4Wire.Cg + 22, 0);
            M4Wire.Put(bytes, M4Wire.Dg + 16, 0);
            M4Wire.Put(bytes, 72, M4Wire.ScalarRaw); // HD comment not exposed by this subset.
            M4Wire.Put(bytes, 76, M4Wire.ScalarRaw); // PR
            M4Wire.Put(bytes, M4Wire.Dg + 12, M4Wire.ScalarRaw); // TR, not a comment.
            M4Wire.Put(bytes, M4Wire.Cg + 26, M4Wire.ScalarRaw); // SR, not original data.
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.True(group.CanReadSamples);
                Assert.Empty(reader.ReadSamples(group, new[] { 0 }));
            }
        }

        [Fact]
        public void LateRawExtentFailurePublishesNoCatalogAndEarlierReservationsCanBeRetried()
        {
            using (TestReadStream source = new TestReadStream(M4Wire.Read()))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                long fullLength = source.ReportedLength;
                source.AfterRead = () =>
                {
                    // Last CE payload ends exactly where DG2 raw data starts. All metadata has been read;
                    // DG1 raw reservation succeeds, then a live one-byte truncation rejects DG2's extent.
                    if (source.Position == M4Wire.Raw2) { source.ReportedLength = fullLength - 1; }
                };
                Assert.Equal("MDF007", Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
                Assert.Equal(0, source.DisposeCalls);
                source.AfterRead = null;
                source.ReportedLength = fullLength;
                IReadOnlyList<MdfDataGroup> catalog = reader.ReadMetadata();
                Assert.Equal(2, catalog.Count);
                Assert.Equal(5, reader.ReadSamples(catalog[0].ChannelGroups[0], new[] { 1 }).Sum(batch => batch.SampleCount));
                Assert.Equal(2, reader.ReadSamples(catalog[1].ChannelGroups[0], new[] { 0 }).Sum(batch => batch.SampleCount));
                int before = source.ReadCalls;
                Assert.Same(catalog, reader.ReadMetadata());
                Assert.Equal(before, source.ReadCalls);
            }
        }

        [Fact]
        public void CancellationAndReentrantMetadataCallsDoNotLeakTheReaderGate()
        {
            using (TestReadStream source = new TestReadStream(M4Wire.Read()))
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
                Assert.Equal(0, source.DisposeCalls);
            }
        }

        [Fact]
        public void FiveThousandRealMdf3ChannelSiblingsUseHierarchyDepthNotListLength()
        {
            const int count = 5000;
            byte[] original = M4Wire.ScalarTemplate();
            byte[] bytes = new byte[M4Wire.Cn + count * 218];
            Array.Copy(original, bytes, M4Wire.Cn);
            M4Wire.Put(bytes, M4Wire.Dg + 16, 0);
            M4Wire.Put(bytes, M4Wire.Cg + 18, count, 2);
            M4Wire.Put(bytes, M4Wire.Cg + 20, 8, 2);
            M4Wire.Put(bytes, M4Wire.Cg + 22, 0);
            for (int index = 0; index < count; index++)
            {
                int address = M4Wire.Cn + index * 218;
                Array.Copy(original, M4Wire.Cn, bytes, address, 218);
                M4Wire.Put(bytes, address + 2, 218, 2);
                M4Wire.Put(bytes, address + 4, index + 1 < count ? (ulong)(address + 218) : 0);
                M4Wire.Put(bytes, address + 188, 64, 2);
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false), options: new MdfReaderOptions { MaximumTraversalDepth = 3 }))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(count, group.Channels.Count);
                Assert.Equal(count - 1, group.Channels[count - 1].Index);
                Assert.All(group.Channels, channel => Assert.True(channel.CanReadRaw));
            }
        }

        private static void Failure(byte[] bytes, string code)
        {
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                Assert.Equal(code, Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode);
                Assert.Equal(0, source.DisposeCalls);
                Assert.Equal(0, source.WriteCalls);
            }
        }
    }
}
