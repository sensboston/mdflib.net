// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Linq;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Reading
{
    public sealed class Mdf330EmptyDataTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void EmptyHeaderlessRangeMayStartAtEofWithoutReadingOrReservingARecord(bool nullData)
        {
            byte[] bytes = EmptyTemplate();
            if (nullData) { M4Wire.Put(bytes, M4Wire.Dg + 16, 0); }
            using (TestReadStream source = new TestReadStream(bytes))
            using (MdfReader reader = MdfReader.Open(source, true))
            {
                MdfChannelGroup group = reader.ReadMetadata()[0].ChannelGroups[0];
                Assert.Equal(0UL, group.SampleCount); Assert.True(group.CanReadSamples, group.UnsupportedReason);
                Assert.All(source.ReadRanges, r => Assert.True(r.Item1 + r.Item2 <= M4Wire.ScalarRaw));
                int before = source.ReadCalls;
                source.ReadFailure = new IOException("An empty range must not read any sample or data header.");
                Assert.Empty(reader.ReadSamples(group, new[] { 0 }));
                Assert.Same(group, reader.ReadMetadata()[0].ChannelGroups[0]);
                Assert.Equal(before, source.ReadCalls); Assert.Equal(0, source.WriteCalls);
            }
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(1, 0)]
        public void PointerPastEofOrNonemptyRangeAtEofRemainsAFormatError(int afterEof, int samples)
        {
            byte[] bytes = EmptyTemplate();
            M4Wire.Put(bytes, M4Wire.Dg + 16, (ulong)(bytes.Length + afterEof));
            M4Wire.Put(bytes, M4Wire.Cg + 22, (ulong)samples);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes)))
            { Assert.Equal("MDF007", Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode); }
        }

        [Theory]
        [InlineData(M4Wire.Dg + 4)]
        [InlineData(M4Wire.Dg + 8)]
        [InlineData(M4Wire.Cg + 4)]
        [InlineData(M4Wire.Cg + 8)]
        [InlineData(M4Wire.Cg + 12)]
        [InlineData(M4Wire.Cn + 4)]
        [InlineData(M4Wire.Cn + 218)]
        [InlineData(M4Wire.Cn + 222)]
        public void EofExceptionCannotBeUsedForMetadataOrTailLinks(int field)
        {
            byte[] bytes = EmptyTemplate();
            M4Wire.Put(bytes, field, (ulong)bytes.Length);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes)))
            { Assert.Equal("MDF007", Assert.Throws<MdfFormatException>(() => reader.ReadMetadata()).DiagnosticCode); }
        }

        private static byte[] EmptyTemplate()
        {
            byte[] bytes = M4Wire.ScalarTemplate(); // Independent pre-writer literal-wire fixture, not MdfWriter output.
            M4Wire.Put(bytes, M4Wire.Cg + 22, 0);
            M4Wire.Put(bytes, M4Wire.Dg + 16, (ulong)bytes.Length);
            return bytes;
        }
    }
}
