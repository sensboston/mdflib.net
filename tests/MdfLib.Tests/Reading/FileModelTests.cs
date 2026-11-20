// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using MdfLib.TestSupport;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Reading
{
    // literal mutations of the pre-C# m6e-model410 corpus (block addresses frozen from tools/fixtures/build_m6e.py).
    public sealed class FileModelTests
    {
        private const int HdFh = 96, HdCh = 104, HdAt = 112, HdEv = 120;
        private const int Fh0 = 320, Fh1 = 560;
        private const int At0 = 792, At0Data = At0 + 56, At0Payload = At0Data + 40;
        private const int At1 = 1288, At1Data = At1 + 56, At1Payload = At1Data + 40;
        private const int At2 = 2296, At2Data = At2 + 56;
        private const int Cg = 2544, Cn0 = 2648, Cn1 = 2840, Cn1AtLink = Cn1 + 24 + 64;
        private const int Ev0 = 3304, Ev0Scope = Ev0 + 64, Ev0At = Ev0 + 72, Ev0Data = Ev0 + 80;
        private const int Ev1 = 3448, Ev2 = 3584, Ev2Parent = Ev2 + 32;
        private const int Ch0 = 3824, Ch0FirstChild = Ch0 + 32, Ch0Element = Ch0 + 56, Ch0Data = Ch0 + 80;
        private const int Ch0a = 3944, Ch0b = 4088, Ch1 = 4216;

        private static byte[] Original() { return FixtureCorpus.Read("m6e-model410"); }

        private static MdfFileModel Model(byte[] bytes, MdfReaderOptions options = null)
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false), options: options)) { return reader.ReadFileModel(); }
        }

        private static T Fails<T>(byte[] bytes) where T : Exception
        {
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                return Assert.Throws<T>(() => reader.ReadFileModel());
            }
        }

        private static void Put(byte[] bytes, int at, ulong value, int width = 8) { M3Wire.Put(bytes, at, value, width); }

        [Fact]
        public void TheFixtureAddressesAreTheFrozenBlocks()
        {
            byte[] bytes = Original();
            foreach (var item in new[] { Tuple.Create(Fh0, "##FH"), Tuple.Create(Fh1, "##FH"), Tuple.Create(At0, "##AT"), Tuple.Create(At1, "##AT"),
                Tuple.Create(At2, "##AT"), Tuple.Create(Cg, "##CG"), Tuple.Create(Cn1, "##CN"), Tuple.Create(Ev0, "##EV"), Tuple.Create(Ev2, "##EV"),
                Tuple.Create(Ch0, "##CH"), Tuple.Create(Ch0a, "##CH") })
            {
                Assert.Equal(item.Item2, Encoding.ASCII.GetString(bytes, item.Item1, 4));
            }
        }

        [Fact]
        public void AbsentChainsGiveEmptyCollections()
        {
            byte[] bytes = Original();
            Put(bytes, HdFh, 0);
            Put(bytes, HdCh, 0);
            Put(bytes, HdEv, 0);
            MdfFileModel model = Model(bytes);
            Assert.Empty(model.FileHistory);
            Assert.Empty(model.ChannelHierarchy);
            Assert.Empty(model.Events);
            Assert.Equal(3, model.Attachments.Count);
        }

        [Fact]
        public void ADanglingChannelAttachmentReferenceIsAFormatError()
        {
            byte[] bytes = Original();
            Put(bytes, HdAt, 0);
            Put(bytes, HdEv, 0);
            Assert.Equal("MDF013", Fails<MdfFormatException>(bytes).DiagnosticCode);
        }

        [Fact]
        public void PlainFileHistoryCommentsAndForeignRootsYieldEmptyFields()
        {
            byte[] bytes = Original();
            Put(bytes, Fh0 + 32, 0); // FH0 without comment
            MdfFileModel model = Model(bytes);
            Assert.Equal(string.Empty, model.FileHistory[0].Comment);
            Assert.Equal(string.Empty, model.FileHistory[0].ToolIdentifier);
            Assert.Equal("M6Epy", model.FileHistory[1].ToolIdentifier);
            Assert.Equal(string.Empty, model.FileHistory[1].UserName);
        }

        [Fact]
        public void FileHistoryCyclesAndLinkShapesAreRejected()
        {
            byte[] cycle = Original();
            Put(cycle, Fh1 + 24, Fh0);
            Assert.Equal("MDF010", Fails<MdfFormatException>(cycle).DiagnosticCode);
            byte[] wrongType = Original();
            Put(wrongType, HdFh, At0);
            Assert.Equal("MDF005", Fails<MdfFormatException>(wrongType).DiagnosticCode);
        }

        [Fact]
        public void TamperedEmbeddedPayloadFailsTheChecksumOnlyWhenVerified()
        {
            byte[] bytes = Original();
            bytes[At0Payload + 5] ^= 0x40;
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfAttachment attachment = reader.ReadFileModel().Attachments[0];
                MdfFormatException error = Assert.Throws<MdfFormatException>(() => reader.ReadAttachment(attachment));
                Assert.Equal("MDF016", error.DiagnosticCode);
                Assert.Equal((ulong)At0, error.Offset);
                byte[] unverified = reader.ReadAttachment(attachment, verifyChecksum: false);
                Assert.Equal(bytes.Skip(At0Payload).Take(unverified.Length), unverified);
            }
        }

        [Fact]
        public void AClearedChecksumFlagSkipsVerification()
        {
            byte[] bytes = Original();
            bytes[At0Payload] ^= 1;
            bytes[At0Data] = 1; // embedded, MD5 not valid
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfAttachment attachment = reader.ReadFileModel().Attachments[0];
                Assert.False(attachment.HasChecksum);
                Assert.Equal(284, reader.ReadAttachment(attachment).Length);
            }
        }

        [Fact]
        public void CorruptCompressedPayloadIsMdf015WithCause()
        {
            byte[] bytes = Original();
            bytes[At1Payload + 200] ^= 0xFF;
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfAttachment attachment = reader.ReadFileModel().Attachments[1];
                MdfFormatException error = Assert.Throws<MdfFormatException>(() => reader.ReadAttachment(attachment, false));
                Assert.Equal("MDF015", error.DiagnosticCode);
                Assert.IsType<InvalidDataException>(error.InnerException);
            }
        }

        [Fact]
        public void AWrongDeclaredOriginalSizeOfACompressedPayloadIsMdf015()
        {
            byte[] bytes = Original();
            Put(bytes, At1Data + 24, 4181);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                MdfAttachment attachment = reader.ReadFileModel().Attachments[1];
                Assert.Equal("MDF015", Assert.Throws<MdfFormatException>(() => reader.ReadAttachment(attachment, false)).DiagnosticCode);
            }
        }

        [Fact]
        public void AttachmentSizesAreBoundedByTheReadBuffer()
        {
            MdfReaderOptions options = new MdfReaderOptions { MaximumReadBufferBytes = 4000 };
            using (MdfReader reader = MdfReader.Open(new MemoryStream(Original(), false), options: options))
            {
                MdfFileModel model = reader.ReadFileModel();
                Assert.Equal(284, reader.ReadAttachment(model.Attachments[0]).Length);
                MdfResourceLimitException error = Assert.Throws<MdfResourceLimitException>(() => reader.ReadAttachment(model.Attachments[1]));
                Assert.Equal(nameof(MdfReaderOptions.MaximumReadBufferBytes), error.LimitName);
            }
        }

        [Fact]
        public void EmbeddedSizesBeyondTheBlockOrInconsistentSizesAreFormatErrors()
        {
            byte[] beyond = Original();
            Put(beyond, At0Data + 32, 285);
            Assert.Equal("MDF006", Fails<MdfFormatException>(beyond).DiagnosticCode);
            byte[] unequal = Original();
            Put(unequal, At0Data + 24, 283);
            Assert.Equal("MDF013", Fails<MdfFormatException>(unequal).DiagnosticCode);
            byte[] external = Original();
            Put(external, At2Data + 32, 1);
            Assert.Equal("MDF013", Fails<MdfFormatException>(external).DiagnosticCode);
        }

        [Fact]
        public void AttachmentCyclesAreRejected()
        {
            byte[] bytes = Original();
            Put(bytes, At2 + 24, At1);
            Assert.Equal("MDF010", Fails<MdfFormatException>(bytes).DiagnosticCode);
        }

        [Fact]
        public void EventScopesParentsRangesAndAttachmentsMustResolve()
        {
            byte[] scope = Original();
            Put(scope, Ev0Scope, At0); // neither CG nor CN of the catalog
            Assert.Equal("MDF013", Fails<MdfFormatException>(scope).DiagnosticCode);
            byte[] attachment = Original();
            Put(attachment, Ev0At, Cg);
            Assert.Equal("MDF013", Fails<MdfFormatException>(attachment).DiagnosticCode);
            byte[] parent = Original();
            Put(parent, Ev2Parent, Ev2); // self
            Assert.Equal("MDF013", Fails<MdfFormatException>(parent).DiagnosticCode);
            byte[] foreign = Original();
            Put(foreign, Ev2Parent, Fh0);
            Assert.Equal("MDF013", Fails<MdfFormatException>(foreign).DiagnosticCode);
        }

        [Fact]
        public void EventLinkCountsMustMatchTheirDeclaredCounts()
        {
            byte[] bytes = Original();
            Put(bytes, Ev0Data + 8, 2, 4); // two scopes declared, one link present
            Assert.Equal("MDF006", Fails<MdfFormatException>(bytes).DiagnosticCode);
        }

        [Fact]
        public void UnknownEnumValuesArePreservedNumerically()
        {
            byte[] bytes = Original();
            bytes[Ev0Data] = 200;
            bytes[Ev0Data + 3] = 77;
            bytes[Ch0Data + 4] = 99;
            MdfFileModel model = Model(bytes);
            Assert.Equal(200, (int)model.Events[0].Type);
            Assert.Equal(77, (int)model.Events[0].Cause);
            Assert.Equal(99, (int)model.ChannelHierarchy[0].Type);
        }

        [Fact]
        public void HierarchyElementsMustMatchTheirOwnDataAndChannelGroup()
        {
            byte[] wrongGroup = Original();
            Put(wrongGroup, Ch0Element + 8, 0);
            Assert.Equal("MDF013", Fails<MdfFormatException>(wrongGroup).DiagnosticCode);
            byte[] notChannel = Original();
            Put(notChannel, Ch0Element + 16, Cg);
            Assert.Equal("MDF013", Fails<MdfFormatException>(notChannel).DiagnosticCode);
            byte[] count = Original();
            Put(count, Ch0Data, 2, 4);
            Assert.Equal("MDF006", Fails<MdfFormatException>(count).DiagnosticCode);
        }

        [Fact]
        public void HierarchyCyclesAndSharedSubtreesAreRejected()
        {
            byte[] cycle = Original();
            Put(cycle, Ch0a + 32, Ch0); // the child's first child is its parent
            Assert.Equal("MDF010", Fails<MdfFormatException>(cycle).DiagnosticCode);
            byte[] shared = Original();
            Put(shared, Ch1 + 32, Ch0a); // a second owner of the Inputs subtree
            Assert.Equal("MDF010", Fails<MdfFormatException>(shared).DiagnosticCode);
            byte[] rooted = Original();
            Put(rooted, HdCh, Ch0a); // a subtree may itself be the root chain when nothing else owns it
            Put(rooted, Ch0FirstChild, 0);
            Assert.Equal(new[] { "Inputs", "Outputs" }, Model(rooted).ChannelHierarchy.Select(n => n.Name));
        }

        [Fact]
        public void HierarchyDepthIsBounded()
        {
            MdfReaderOptions options = new MdfReaderOptions { MaximumTraversalDepth = 4 };
            Assert.Equal(2, Model(Original(), options).ChannelHierarchy.Count);
            byte[] deeper = Original();
            Put(deeper, Ch0a + 24, 0); // Outputs becomes the child of Inputs (depth 4; its name is at depth 5)
            Put(deeper, Ch0a + 32, Ch0b);
            Assert.Single(Model(deeper).ChannelHierarchy[0].Children[0].Children);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(deeper, false), options: options))
            {
                Assert.NotEmpty(reader.ReadMetadata());
                MdfResourceLimitException error = Assert.Throws<MdfResourceLimitException>(() => reader.ReadFileModel());
                Assert.Equal(nameof(MdfReaderOptions.MaximumTraversalDepth), error.LimitName);
            }
        }

        [Fact]
        public void FailuresDoNotPublishAPartialModelAndCancellationIsHonoured()
        {
            byte[] bytes = Original();
            Put(bytes, Ev0Scope, At0);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(bytes, false)))
            {
                Assert.Throws<MdfFormatException>(() => reader.ReadFileModel());
                Assert.Throws<MdfFormatException>(() => reader.ReadFileModel());
                Assert.NotEmpty(reader.ReadMetadata());
            }
            using (MdfReader reader = MdfReader.Open(new MemoryStream(Original(), false)))
            using (CancellationTokenSource source = new CancellationTokenSource())
            {
                source.Cancel();
                Assert.Throws<OperationCanceledException>(() => reader.ReadFileModel(source.Token));
                MdfAttachment attachment = reader.ReadFileModel().Attachments[0];
                Assert.Throws<OperationCanceledException>(() => reader.ReadAttachment(attachment, true, source.Token));
            }
        }

        [Fact]
        public void TheMetadataBudgetCoversTheModel()
        {
            int failures = 0;
            for (long limit = 4096; limit < 262144; limit += 2048)
            {
                MdfReaderOptions options = new MdfReaderOptions { MaximumMetadataBytes = limit };
                using (MdfReader reader = MdfReader.Open(new MemoryStream(Original(), false), options: options))
                {
                    try
                    {
                        reader.ReadFileModel();
                        Assert.True(failures > 0);
                        return;
                    }
                    catch (MdfResourceLimitException error)
                    {
                        Assert.Equal(nameof(MdfReaderOptions.MaximumMetadataBytes), error.LimitName);
                        failures++;
                    }
                }
            }
            Assert.Fail("The model did not fit 256 KiB of metadata accounting.");
        }

        [Fact]
        public void Mdf3HasOnlyItsHeaderComment()
        {
            byte[] bytes = FixtureCorpus.Read("m6e-comment330");
            MdfFileModel model = Model(bytes);
            Assert.Equal("Pr\u00fcfstand \u00c4, Lauf 7", model.Comment);
            Assert.Empty(model.FileHistory);
            Assert.Empty(model.Attachments);
            Put(bytes, 64 + 8, 0, 4); // no comment link
            Assert.Equal(string.Empty, Model(bytes).Comment);
        }
    }
}
