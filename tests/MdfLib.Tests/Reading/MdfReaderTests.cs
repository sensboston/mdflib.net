// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Linq;
using System.Threading;
using MdfLib.TestSupport;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Reading
{
    public sealed class MdfReaderTests
    {
        [Theory]
        [InlineData("m2-mdf330-le")]
        [InlineData("m2-mdf410-header")]
        public void EveryTruncatedPrefixIsRejectedWithoutWritingTheSource(string fixture)
        {
            byte[] complete = FixtureCorpus.Read(fixture);
            for (int length = 0; length < complete.Length; length++)
            {
                using (TestReadStream source = new TestReadStream(complete.Take(length).ToArray()))
                {
                    MdfFormatException error = Assert.Throws<MdfFormatException>(() => MdfReader.Open(source, true));
                    Assert.NotEmpty(error.DiagnosticCode);
                    Assert.Equal(0, source.WriteCalls);
                    Assert.Equal(0, source.FlushCalls);
                    Assert.Equal(0, source.DisposeCalls);
                }
            }
        }

        [Theory]
        [InlineData("2.14")]
        [InlineData("3.31")]
        [InlineData("3.40")]
        [InlineData("4.01")]
        [InlineData("4.12")]
        [InlineData("4.21")]
        [InlineData("9.99")]
        public void UnsupportedVersionsNeverDispatchToANeighboringCodec(string version)
        {
            byte[] data = FixtureCorpus.Read("m2-mdf410-header");
            for (int index = 0; index < 4; index++) { data[8 + index] = (byte)version[index]; }
            int number = (version[0] - '0') * 100 + (version[2] - '0') * 10 + version[3] - '0';
            data[28] = (byte)number;
            data[29] = (byte)(number >> 8);
            using (TestReadStream source = new TestReadStream(data))
            {
                Assert.True(MdfReader.IsMdfFile(source)); // Signature alone is deliberately weaker than Open.
                Assert.Equal(0L, source.Position);
                int reads = source.ReadCalls;
                Assert.Throws<NotSupportedException>(() => MdfReader.Open(source, true));
                Assert.Equal(reads + 1, source.ReadCalls); // ID only; no attempted HD dispatch.
            }
        }

        [Theory]
        [InlineData(0, (byte)'X', "MDF001")]
        [InlineData(7, (byte)0, "MDF001")]
        [InlineData(8, (byte)'x', "MDF002")]
        [InlineData(8, (byte)255, "MDF002")]
        [InlineData(9, (byte)',', "MDF002")]
        [InlineData(12, (byte)'x', "MDF002")]
        [InlineData(12, (byte)255, "MDF002")]
        [InlineData(28, (byte)0, "MDF003")]
        [InlineData(64, (byte)'X', "MDF005")]
        public void CorruptIdentificationAndBlockTypesHaveStableCodes(int index, byte value, string code)
        {
            byte[] bytes = FixtureCorpus.Read("m2-mdf410-header");
            bytes[index] = value;
            using (MemoryStream source = new MemoryStream(bytes, false))
            {
                Assert.Equal(code, Assert.Throws<MdfFormatException>(() => MdfReader.Open(source, true)).DiagnosticCode);
            }
        }

        [Theory]
        [InlineData((byte)0)]
        [InlineData((byte)32)]
        public void VersionPaddingCanBeNulOrSpace(byte padding)
        {
            byte[] data = FixtureCorpus.Read("m2-mdf410-header");
            for (int index = 12; index < 16; index++) { data[index] = padding; }
            using (MemoryStream source = new MemoryStream(data, false))
            using (MdfReader reader = MdfReader.Open(source))
            {
                Assert.Equal("4.10", reader.Header.Version);
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(5)]
        [InlineData(7)]
        public void Mdf410HdRequiresExactlySixLinksEvenWhenTheWireSectionFits(int count)
        {
            byte[] data = FixtureCorpus.Read("m2-mdf410-header");
            data[80] = (byte)count;
            Array.Clear(data, 88, count * 8); // All declared targets valid/null, including the extra seventh slot.
            using (MemoryStream source = new MemoryStream(data, false))
            {
                MdfFormatException error = Assert.Throws<MdfFormatException>(() => MdfReader.Open(source, true));
                Assert.Equal("MDF006", error.DiagnosticCode);
                Assert.Equal(64UL, error.Offset);
            }
        }

        [Fact]
        public void ReaderDefaultsAreFiniteAndMatchTheDocumentedBudgets()
        {
            MdfReaderOptions options = new MdfReaderOptions();
            Assert.Equal(100_000, options.MaximumBlockCount);
            Assert.Equal(4_096, options.MaximumLinksPerBlock);
            Assert.Equal(1_000_000, options.MaximumTotalLinks);
            Assert.Equal(256, options.MaximumTraversalDepth);
            Assert.Equal(1_048_576, options.MaximumReadBufferBytes);
            Assert.Equal(67_108_864L, options.MaximumMetadataBytes);
            Assert.Equal(1_048_576, options.MaximumTextBytes);
            Assert.Equal(1_048_576, options.MaximumXmlCharacters);
            Assert.Equal(64, options.MaximumXmlDepth);
            Assert.Equal(8_388_608, options.MaximumSampleBatchBytes);
        }

        [Fact]
        public void APositiveSignatureProbeDoesNotRequireTheWholeIdOrProveOpenCanSucceed()
        {
            using (MemoryStream source = new MemoryStream(new byte[] { 77, 68, 70, 32, 32, 32, 32, 32 }, false))
            {
                source.Position = 3;
                Assert.True(MdfReader.IsMdfFile(source));
                Assert.Equal(3L, source.Position);
                Assert.Equal("MDF004", Assert.Throws<MdfFormatException>(() => MdfReader.Open(source, true)).DiagnosticCode);
                Assert.True(source.CanRead);
            }
        }

        [Theory]
        [InlineData(24, 2)]
        [InlineData(26, 1)]
        [InlineData(30, 1252)]
        public void UnsupportedMdf3SettingsAreNotSilentlyReinterpreted(int offset, int value)
        {
            byte[] data = FixtureCorpus.Read("m2-mdf330-le");
            data[offset] = (byte)value;
            data[offset + 1] = (byte)(value >> 8);
            using (MemoryStream source = new MemoryStream(data, false))
            {
                Assert.Throws<NotSupportedException>(() => MdfReader.Open(source, true));
            }
        }

        [Theory]
        [InlineData(60)]
        [InlineData(61)]
        [InlineData(62)]
        [InlineData(63)]
        public void EveryUnfinalizedFlagByteIsRefusedEvenWithNormalSignature(int offset)
        {
            byte[] data = FixtureCorpus.Read("m2-mdf410-header");
            data[offset] = 1;
            using (MemoryStream source = new MemoryStream(data, false))
            {
                Assert.Throws<NotSupportedException>(() => MdfReader.Open(source, true));
            }
        }

        [Theory]
        [InlineData("m2-mdf330-le", 228)]
        [InlineData("m2-mdf410-header", 136)]
        public void RawTimestampKeepsAllSixtyFourBits(string fixture, int offset)
        {
            byte[] data = FixtureCorpus.Read(fixture);
            for (int index = 0; index < 8; index++) { data[offset + index] = 255; }
            using (MemoryStream source = new MemoryStream(data, false))
            using (MdfReader reader = MdfReader.Open(source))
            {
                Assert.Equal(ulong.MaxValue, reader.Header.RawStartTimeNanoseconds);
            }
        }

        [Fact]
        public void Mdf3ByteOrderFlagDoesNotChangeMetadataDecoding()
        {
            using (MemoryStream source = new MemoryStream(FixtureCorpus.Read("m2-mdf330-be-data"), false))
            using (MdfReader reader = MdfReader.Open(source))
            {
                Assert.True(reader.Header.DefaultDataIsBigEndian);
                Assert.Equal(9_007_199_254_740_993UL, reader.Header.RawStartTimeNanoseconds);
            }
            byte[] swapped = FixtureCorpus.Read("m2-mdf330-be-data");
            swapped[66] = 0;
            swapped[67] = 208;
            using (MemoryStream source = new MemoryStream(swapped, false))
            {
                Assert.Equal("MDF006", Assert.Throws<MdfFormatException>(() => MdfReader.Open(source, true)).DiagnosticCode);
            }
        }

        [Fact]
        public void StrictTextDecodersNeverInsertReplacementCharacters()
        {
            byte[] invalid = FixtureCorpus.Read("m2-mdf330-utf8");
            invalid[100] = 0xc3;
            invalid[101] = 0;
            using (MemoryStream source = new MemoryStream(invalid, false))
            {
                MdfFormatException error = Assert.Throws<MdfFormatException>(() => MdfReader.Open(source, true));
                Assert.Equal("MDF009", error.DiagnosticCode);
                Assert.Equal(100UL, error.Offset);
                Assert.IsType<System.Text.DecoderFallbackException>(error.InnerException);
            }
            byte[] ascii = FixtureCorpus.Read("m2-mdf330-le");
            ascii[30] = 0x9f; ascii[31] = 0x4e; // code page 20127; the original Latin-1 é is invalid ASCII.
            using (MemoryStream source = new MemoryStream(ascii, false))
            {
                Assert.Equal("MDF009", Assert.Throws<MdfFormatException>(() => MdfReader.Open(source, true)).DiagnosticCode);
            }
        }

        [Fact]
        public void Latin1AliasAndFirstNulTerminationAreExplicit()
        {
            byte[] data = FixtureCorpus.Read("m2-mdf330-le");
            data[30] = 0xaf; data[31] = 0x6f; // 28591
            data[100] = (byte)'A'; data[101] = 0; data[102] = 255;
            using (MemoryStream source = new MemoryStream(data, false))
            using (MdfReader reader = MdfReader.Open(source))
            {
                Assert.Equal("A", reader.Header.Author);
                Assert.Equal((int?)28591, reader.Header.CodePage);
            }
        }

        [Fact]
        public void UnknownTimeFlagsAndFloatingBitsArePreservedRatherThanGuessed()
        {
            byte[] data = FixtureCorpus.Read("m2-mdf410-header");
            data[148] = 255; data[150] = 255;
            byte[] nan = { 1, 0, 0, 0, 0, 0, 0xf8, 0x7f };
            Array.Copy(nan, 0, data, 152, 8);
            using (MemoryStream source = new MemoryStream(data, false))
            using (MdfReader reader = MdfReader.Open(source))
            {
                Assert.Equal((byte?)255, reader.Header.TimeFlags);
                Assert.Equal((byte?)255, reader.Header.Flags);
                Assert.Equal(0x7ff8000000000001L, BitConverter.DoubleToInt64Bits(reader.Header.StartAngle.Value));
            }
        }

        [Fact]
        public void ExtendedHdLengthDoesNotForceTailMaterializationOrLengthAlignment()
        {
            byte[] data = FixtureCorpus.Read("m2-mdf410-header").Concat(new byte[] { 0xff }).ToArray();
            data[72] = 105; // HD length is not the address of its successor.
            using (TestReadStream source = new TestReadStream(data))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                Assert.Equal("4.10", reader.Header.Version);
                Assert.Equal(168L, source.Position);
            }
        }

        [Fact]
        public void ShortReadsAreHandledAndLinkedMetadataIsNotReadAutomatically()
        {
            byte[] bytes = FixtureCorpus.Read("m2-mdf410-linked-metadata");
            byte[] before = (byte[])bytes.Clone();
            using (TestReadStream source = new TestReadStream(bytes) { MaximumBytesPerRead = 1 })
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                Assert.Equal(168, source.ReadCalls);
                Assert.Equal(168L, source.Position);
                Assert.Equal(before, bytes);
                Assert.Equal(0, source.WriteCalls);
                Assert.Equal(0, source.FlushCalls);
            }
        }

        [Fact]
        public void HeaderSnapshotSurvivesDisposeWhileFutureIoCannotUseTheDisposedInput()
        {
            TestReadStream source = new TestReadStream(FixtureCorpus.Read("m2-mdf410-header"));
            MdfReader reader = MdfReader.Open(source);
            MdfFileHeader header = reader.Header;
            reader.Dispose(); reader.Dispose();
            Assert.Same(header, reader.Header);
            Assert.Equal("4.10", header.Version);
            Assert.Equal(1, source.DisposeCalls);
            Assert.Throws<ObjectDisposedException>(() => reader.Resolver.Resolve(64));
        }

        [Theory]
        [InlineData(false, 1)]
        [InlineData(true, 0)]
        public void ParseFailureHonorsOwnership(bool leaveOpen, int expectedDisposals)
        {
            TestReadStream source = new TestReadStream(new byte[64]);
            Assert.Throws<MdfFormatException>(() => MdfReader.Open(source, leaveOpen));
            Assert.Equal(expectedDisposals, source.DisposeCalls);
            if (leaveOpen) { source.Dispose(); }
        }

        [Theory]
        [InlineData("MaximumBlockCount")]
        [InlineData("MaximumLinksPerBlock")]
        [InlineData("MaximumTotalLinks")]
        [InlineData("MaximumTraversalDepth")]
        [InlineData("MaximumReadBufferBytes")]
        [InlineData("MaximumMetadataBytes")]
        [InlineData("MaximumTextBytes")]
        [InlineData("MaximumXmlCharacters")]
        [InlineData("MaximumXmlDepth")]
        [InlineData("MaximumSampleBatchBytes")]
        public void InvalidOptionsFailBeforeStreamOwnership(string property)
        {
            MdfReaderOptions options = new MdfReaderOptions();
            System.Reflection.PropertyInfo member = typeof(MdfReaderOptions).GetProperty(property);
            member.SetValue(options, Convert.ChangeType(0, member.PropertyType));
            using (TestReadStream source = new TestReadStream(100L))
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => MdfReader.Open(source, options: options));
                Assert.Equal(0, source.ReadCalls);
                Assert.Equal(0, source.DisposeCalls);
            }
        }

        [Fact]
        public void ConstructorAndPreCancellationFailuresDoNotTakeOwnership()
        {
            Assert.Throws<ArgumentNullException>(() => MdfReader.Open(null));
            using (TestReadStream source = new TestReadStream(100L))
            using (CancellationTokenSource token = new CancellationTokenSource())
            {
                source.Readable = false;
                Assert.Throws<ArgumentException>(() => MdfReader.Open(source));
                source.Readable = true; source.Seekable = false;
                Assert.Throws<ArgumentException>(() => MdfReader.Open(source));
                source.Seekable = true; source.ReportedLength = -1;
                Assert.Throws<ArgumentException>(() => MdfReader.Open(source));
                source.ReportedLength = 100; token.Cancel();
                Assert.Throws<OperationCanceledException>(() => MdfReader.Open(source, cancellationToken: token.Token));
                Assert.Equal(0, source.DisposeCalls);
                Assert.Equal(0, source.ReadCalls);
            }
        }

        [Fact]
        public void IoAndCancellationCausesAreNotConvertedToFormatFailures()
        {
            TestReadStream source = new TestReadStream(100L);
            IOException failure = new IOException("Injected read failure.");
            source.ReadFailure = failure;
            source.DisposeFailure = new IOException("Injected cleanup failure.");
            Assert.Same(failure, Assert.Throws<IOException>(() => MdfReader.Open(source)));
            Assert.Equal(1, source.DisposeCalls);
            using (CancellationTokenSource token = new CancellationTokenSource())
            using (TestReadStream shortReads = new TestReadStream(FixtureCorpus.Read("m2-mdf410-header")) { MaximumBytesPerRead = 1 })
            {
                shortReads.AfterRead = token.Cancel;
                Assert.Equal(token.Token, Assert.Throws<OperationCanceledException>(() => MdfReader.Open(shortReads, true, cancellationToken: token.Token)).CancellationToken);
            }
        }

        [Fact]
        public void UnexpectedTruncationRetainsTheEndOfStreamCause()
        {
            using (TestReadStream source = new TestReadStream(FixtureCorpus.Read("m2-mdf410-header")) { AvailableLength = 4 })
            {
                MdfFormatException error = Assert.Throws<MdfFormatException>(() => MdfReader.Open(source, true));
                Assert.Equal("MDF004", error.DiagnosticCode);
                Assert.IsType<EndOfStreamException>(error.InnerException);
            }
        }

        [Fact]
        public void OptionsAreSnapshottedAndLimitsIdentifyTheRefusal()
        {
            MdfReaderOptions options = new MdfReaderOptions { MaximumBlockCount = 1 };
            using (TestReadStream source = new TestReadStream(FixtureCorpus.Read("m2-mdf410-linked-metadata")))
            using (MdfReader reader = MdfReader.Open(source, true, options))
            {
                options.MaximumBlockCount = 100;
                Assert.Equal(1, reader.Limits.MaximumBlockCount);
                MdfResourceLimitException error = Assert.Throws<MdfResourceLimitException>(() => reader.Resolver.Resolve(168));
                Assert.Equal("MaximumBlockCount", error.LimitName);
                Assert.Equal(1UL, error.Limit);
                Assert.Equal(2UL, error.Requested);
                Assert.Equal(168UL, error.Offset);
            }
            using (MemoryStream source = new MemoryStream(FixtureCorpus.Read("m2-mdf410-header"), false))
            {
                MdfResourceLimitException error = Assert.Throws<MdfResourceLimitException>(() => MdfReader.Open(source, true, new MdfReaderOptions { MaximumReadBufferBytes = 63 }));
                Assert.Equal("MaximumReadBufferBytes", error.LimitName);
                Assert.Equal(0L, source.Position);
            }
        }

        [Theory]
        [InlineData(0L)]
        [InlineData(3L)]
        [InlineData(1000L)]
        public void SignatureProbeRestoresEvenPositionsBeyondEof(long initial)
        {
            using (TestReadStream source = new TestReadStream(FixtureCorpus.Read("m2-mdf410-header")))
            {
                source.Position = initial;
                Assert.True(MdfReader.IsMdfFile(source));
                Assert.Equal(initial, source.Position);
                Assert.Equal(0, source.DisposeCalls);
                Assert.Equal(0, source.WriteCalls);
            }
        }

        [Fact]
        public void SignatureProbeRejectsShortOrInvalidSourcesAndPreservesIoErrors()
        {
            for (int length = 0; length < 8; length++)
            {
                using (TestReadStream source = new TestReadStream(new byte[length]))
                {
                    Assert.False(MdfReader.IsMdfFile(source));
                    Assert.Equal(0, source.ReadCalls);
                    Assert.Equal(0, source.DisposeCalls);
                }
            }
            using (TestReadStream source = new TestReadStream(new byte[8]))
            {
                source.Position = 3;
                Assert.False(MdfReader.IsMdfFile(source));
                IOException failure = new IOException("Probe read failure.");
                source.ReadFailure = failure;
                Assert.Same(failure, Assert.Throws<IOException>(() => MdfReader.IsMdfFile(source)));
                Assert.Equal(3L, source.Position);
                Assert.Equal(0, source.DisposeCalls);
                source.ReadFailure = null;
                source.SeekResultOverride = -1;
                Assert.Throws<IOException>(() => MdfReader.IsMdfFile(source));
            }
        }

        [Fact]
        public void PublicDiagnosticsRetainContextAndValidateTheirIdentifiers()
        {
            Exception cause = new EndOfStreamException();
            MdfFormatException error = new MdfFormatException("message", "TEST", ulong.MaxValue, "##HD", cause);
            Assert.Equal(ulong.MaxValue, error.Offset);
            Assert.Equal("##HD", error.BlockType);
            Assert.Same(cause, error.InnerException);
            Assert.Throws<ArgumentException>(() => new MdfFormatException("message", " ", 0));
            Assert.Throws<ArgumentException>(() => new MdfResourceLimitException("", 1, 2, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new MdfResourceLimitException("limit", 0, 1, 0));
        }
    }
}
