// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Threading;
using MdfLib.Compression;
using Xunit;

namespace MdfLib.Tests.Compression
{
    public sealed class ZlibInflaterTests
    {
        public static IEnumerable<object[]> IndependentCases()
        {
            foreach (Vector item in Load().Cases) { yield return new object[] { item.Name }; }
        }

        [Theory]
        [MemberData(nameof(IndependentCases))]
        public void AllIndependentStoredFixedDynamicAndMultiblockStreamsMatchExactInputs(string name)
        {
            Evidence evidence = Load();
            Vector vector = evidence.Cases.Single(item => item.Name == name);
            Input input = evidence.Inputs.Single(item => item.Name == vector.Source);
            byte[] wanted = Convert.FromBase64String(input.Bytes);
            byte[] compressed = Convert.FromBase64String(vector.Bytes);
            Assert.Equal(vector.Sha256, Hash(compressed));
            Assert.Equal(input.Sha256, Hash(wanted));
            Assert.Equal(wanted, ZlibInflater.Inflate(compressed, wanted.Length, 100000, () => { }));
            Assert.Equal(wanted, ZlibInflater.Inflate(compressed, wanted.Length, 100000, () => { }));
        }

        [Fact]
        public void EveryByteTruncationOfSmallRepresentativesAndAllTrailerCorruptionsFailClosed()
        {
            Evidence evidence = Load();
            foreach (Vector vector in evidence.Cases)
            {
                byte[] compressed = Convert.FromBase64String(vector.Bytes);
                int expected = Convert.FromBase64String(evidence.Inputs.Single(item => item.Name == vector.Source).Bytes).Length;
                if (compressed.Length < 512)
                {
                    for (int end = 0; end < compressed.Length; end++)
                    {
                        byte[] truncated = compressed.Take(end).ToArray();
                        Assert.Throws<InvalidDataException>(() => ZlibInflater.Inflate(truncated, expected, 100000, () => { }));
                    }
                }
                byte[] corrupt = (byte[])compressed.Clone();
                corrupt[corrupt.Length - 1] ^= 1;
                Assert.Throws<InvalidDataException>(() => ZlibInflater.Inflate(corrupt, expected, 100000, () => { }));
                Assert.Throws<InvalidDataException>(() => ZlibInflater.Inflate(compressed.Concat(new byte[] { 0 }).ToArray(), expected, 100000, () => { }));
                Assert.Throws<InvalidDataException>(() => ZlibInflater.Inflate(compressed, expected + 1, 100000, () => { }));
                if (expected != 0) { Assert.Throws<InvalidDataException>(() => ZlibInflater.Inflate(compressed, expected - 1, 100000, () => { })); }
            }
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(0, 0x88)]
        [InlineData(1, 0)]
        public void InvalidMethodWindowAndHeaderCheckAreRejected(int index, int value)
        {
            byte[] compressed = Empty();
            compressed[index] = (byte)value;
            Assert.Throws<InvalidDataException>(() => ZlibInflater.Inflate(compressed, 0, 1024, () => { }));
        }

        [Fact]
        public void DictionaryGzipRawDeflateAndSecondMembersAreNotSilentlyAccepted()
        {
            byte[] dictionary = Empty();
            dictionary[1] = 0x20;
            while (((dictionary[0] << 8) + dictionary[1]) % 31 != 0) { dictionary[1]++; }
            Assert.Throws<NotSupportedException>(() => ZlibInflater.Inflate(dictionary, 0, 1024, () => { }));
            Assert.Throws<InvalidDataException>(() => ZlibInflater.Inflate(new byte[] { 31, 139, 8, 0, 0, 0, 0, 0 }, 0, 1024, () => { }));
            Assert.Throws<InvalidDataException>(() => ZlibInflater.Inflate(Empty().Skip(2).ToArray(), 0, 1024, () => { }));
            Assert.Throws<InvalidDataException>(() => ZlibInflater.Inflate(Empty().Concat(Empty()).ToArray(), 0, 1024, () => { }));
        }

        [Fact]
        public void LimitsArgumentsAndCheckpointsAreAppliedBeforeAllocatingOrPublishing()
        {
            Assert.Throws<ArgumentNullException>(() => ZlibInflater.Inflate(null, 0, 10, () => { }));
            Assert.Throws<ArgumentNullException>(() => ZlibInflater.Inflate(Empty(), 0, 10, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => ZlibInflater.Inflate(Empty(), -1, 10, () => { }));
            Assert.Throws<ArgumentOutOfRangeException>(() => ZlibInflater.Inflate(Empty(), 0, 0, () => { }));
            Assert.Equal("MaximumReadBufferBytes", Assert.Throws<MdfResourceLimitException>(() => ZlibInflater.Inflate(Empty(), int.MaxValue, 8, () => { })).LimitName);
            Assert.Throws<MdfResourceLimitException>(() => ZlibInflater.Inflate(Empty(), 0, 1, () => { }));
            IOException cause = new IOException("checkpoint fault");
            Assert.Same(cause, Assert.Throws<IOException>(() => ZlibInflater.Inflate(Empty(), int.MaxValue, 1, () => { throw cause; })));
            Evidence evidence = Load();
            Vector vector = evidence.Cases.Single(item => item.Name == "overlap-default");
            byte[] bytes = Convert.FromBase64String(vector.Bytes);
            for (int target = 1; target <= 15; target++)
            {
                int calls = 0;
                int selected = target;
                Assert.Throws<OperationCanceledException>(() => ZlibInflater.Inflate(bytes, 10000, 100000, () =>
                {
                    if (++calls == selected) { throw new OperationCanceledException(); }
                }));
            }
        }

        [Theory]
        [InlineData("reserved-block")]
        [InlineData("bad-stored")]
        [InlineData("reserved-literal")]
        [InlineData("reserved-distance")]
        [InlineData("early-distance")]
        [InlineData("empty-code-tree")]
        [InlineData("oversubscribed")]
        [InlineData("incomplete")]
        [InlineData("repeat-no-previous")]
        [InlineData("repeat-overflow")]
        [InlineData("missing-end")]
        [InlineData("reserved-hlit")]
        public void MalformedTreesRepeatsAndBackReferencesAreBoundedFormatFailures(string kind)
        {
            Writer w = new Writer();
            if (kind == "reserved-block") { w.Value(7, 3); }
            else if (kind == "bad-stored") { w.Value(1, 3); w.Align(); w.Value(1, 16); w.Value(0, 16); }
            else if (kind.StartsWith("reserved-literal") || kind.EndsWith("distance"))
            {
                w.Value(3, 3);
                w.Literal(kind == "reserved-literal" ? 286 : 257);
                w.Code(kind == "reserved-distance" ? 30 : 0, 5);
            }
            else
            {
                w.Value(5, 3);
                w.Value(kind == "reserved-hlit" ? 31 : 0, 5); w.Value(0, 5); w.Value(0, 4);
                int[] lengths = kind == "oversubscribed" ? new[] { 1, 1, 1, 0 }
                    : kind == "incomplete" ? new[] { 0, 0, 0, 1 }
                    : kind == "repeat-no-previous" ? new[] { 1, 0, 0, 1 }
                    : kind == "repeat-overflow" || kind == "missing-end" ? new[] { 0, 0, 1, 1 }
                    : new[] { 0, 0, 0, 0 };
                foreach (int bits in lengths) { w.Value(bits, 3); }
                if (kind == "repeat-no-previous") { w.Code(1, 1); w.Value(0, 2); }
                if (kind == "repeat-overflow" || kind == "missing-end")
                {
                    w.Code(1, 1); w.Value(127, 7);
                    w.Code(1, 1); w.Value(kind == "repeat-overflow" ? 127 : 109, 7);
                }
            }
            byte[] bytes = w.Wrap(new byte[0]);
            Assert.Throws<InvalidDataException>(() => ZlibInflater.Inflate(bytes, 100, 4096, () => { }));
        }

        [Fact]
        public void SingleEndCodeEmptyDistanceOverlapAndDeclaredWindowHaveExactSemantics()
        {
            Writer empty = new Writer();
            empty.Value(5, 3); empty.Value(0, 5); empty.Value(0, 5); empty.Value(14, 4);
            // Code-length alphabet: 0 and 1 each have one bit. Literal alphabet: EOB only. Distance alphabet empty.
            for (int index = 0; index < 18; index++) { empty.Value(index == 3 || index == 17 ? 1 : 0, 3); }
            for (int index = 0; index < 256; index++) { empty.Code(0, 1); }
            empty.Code(1, 1); empty.Code(0, 1); empty.Code(0, 1);
            Assert.Empty(ZlibInflater.Inflate(empty.Wrap(new byte[0]), 0, 4096, () => { }));
            Writer overlap = new Writer();
            overlap.Value(3, 3); overlap.Literal(65); overlap.Literal(257); overlap.Code(0, 5); overlap.Literal(256);
            Assert.Equal(new byte[] { 65, 65, 65, 65 }, ZlibInflater.Inflate(overlap.Wrap(new byte[] { 65, 65, 65, 65 }), 4, 4096, () => { }));
            Writer window = new Writer();
            window.Value(3, 3);
            for (int i = 0; i < 300; i++) { window.Literal(65); }
            window.Literal(257); window.Code(16, 5); window.Value(43, 7); window.Literal(256);
            byte[] wanted = Enumerable.Repeat((byte)65, 303).ToArray();
            byte[] compressed = window.Wrap(wanted);
            Assert.Equal(wanted, ZlibInflater.Inflate(compressed, 303, 4096, () => { }));
            compressed[0] = 8; compressed[1] = 29; // Valid zlib header, declared window 256 instead of 32768.
            Assert.Throws<InvalidDataException>(() => ZlibInflater.Inflate(compressed, 303, 4096, () => { }));
        }

        private static byte[] Empty() { return new byte[] { 0x78, 0x9c, 3, 0, 0, 0, 0, 1 }; }
        private static string Hash(byte[] bytes)
        {
            using (SHA256 hash = SHA256.Create()) { return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        }
        private static Evidence Load()
        {
            using (Stream stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "deflate-vectors.json")))
            { return (Evidence)new DataContractJsonSerializer(typeof(Evidence)).ReadObject(stream); }
        }
        [DataContract] public sealed class Evidence
        {
            [DataMember(Name = "inputs", IsRequired = true)] public List<Input> Inputs { get; set; }
            [DataMember(Name = "cases", IsRequired = true)] public List<Vector> Cases { get; set; }
        }
        [DataContract] public class Input
        {
            [DataMember(Name = "name", IsRequired = true)] public string Name { get; set; }
            [DataMember(Name = "bytes", IsRequired = true)] public string Bytes { get; set; }
            [DataMember(Name = "sha256", IsRequired = true)] public string Sha256 { get; set; }
        }
        [DataContract] public sealed class Vector : Input
        {
            [DataMember(Name = "source", IsRequired = true)] public string Source { get; set; }
        }
        private sealed class Writer
        {
            private readonly List<byte> bytes = new List<byte>();
            private int value;
            private int bits;
            internal void Value(int data, int width)
            {
                for (int index = 0; index < width; index++)
                {
                    value |= ((data >> index) & 1) << bits;
                    if (++bits == 8) { Align(); }
                }
            }
            internal void Code(int data, int width) { for (int index = width - 1; index >= 0; index--) { Value((data >> index) & 1, 1); } }
            internal void Align() { if (bits != 0) { bytes.Add((byte)value); value = bits = 0; } }
            internal void Literal(int symbol)
            {
                if (symbol < 144) { Code(48 + symbol, 8); }
                else if (symbol < 256) { Code(400 + symbol - 144, 9); }
                else if (symbol < 280) { Code(symbol - 256, 7); }
                else { Code(192 + symbol - 280, 8); }
            }
            internal byte[] Wrap(byte[] output)
            {
                Align();
                uint a = 1, b = 0;
                foreach (byte item in output) { a = (a + item) % 65521; b = (b + a) % 65521; }
                uint sum = (b << 16) | a;
                return new byte[] { 0x78, 0x9c }.Concat(bytes).Concat(new[] { (byte)(sum >> 24), (byte)(sum >> 16), (byte)(sum >> 8), (byte)sum }).ToArray();
            }
        }
    }
}
