// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MdfLib.Internal;
using Xunit;

namespace MdfLib.Tests.Internal
{
    // the managed MD5 is checked against RFC 1321 appendix A.5 vectors and, independently, the test runtime's BCL MD5.
    public sealed class Md5Tests
    {
        [Theory]
        [InlineData("", "d41d8cd98f00b204e9800998ecf8427e")]
        [InlineData("a", "0cc175b9c0f1b6a831c399e269772661")]
        [InlineData("abc", "900150983cd24fb0d6963f7d28e17f72")]
        [InlineData("message digest", "f96b697d7cb7938d525a2f31aaf161d0")]
        [InlineData("abcdefghijklmnopqrstuvwxyz", "c3fcd3d76192e4007dfb496cca67e13b")]
        [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", "d174ab98d277d9f5a5611c2c9f419d9f")]
        [InlineData("12345678901234567890123456789012345678901234567890123456789012345678901234567890", "57edf4a22be3c955ac49da2e2107b67a")]
        public void Rfc1321VectorsMatch(string text, string expected)
        {
            byte[] data = Encoding.ASCII.GetBytes(text);
            Assert.Equal(expected, Hex(Md5.Hash(data, data.Length, null)));
        }

        [Fact]
        public void EveryLengthAroundBlockAndPaddingBoundariesMatchesTheBclDigest()
        {
            Random random = new Random(1321);
            using (MD5 reference = MD5.Create())
            {
                foreach (int length in Enumerable.Range(0, 200).Concat(new[] { 1000, 4095, 4096, 65535, 65536, 65600, 200003 }))
                {
                    byte[] data = new byte[length];
                    random.NextBytes(data);
                    Assert.Equal(reference.ComputeHash(data), Md5.Hash(data, data.Length, () => { }));
                }
            }
        }

        [Fact]
        public void APrefixCountHashesOnlyThatPrefix()
        {
            byte[] data = Encoding.ASCII.GetBytes("abcXYZ");
            Assert.Equal("900150983cd24fb0d6963f7d28e17f72", Hex(Md5.Hash(data, 3, null)));
        }

        [Fact]
        public void TheCheckpointRunsForLargeInputsAndCanCancel()
        {
            int calls = 0;
            byte[] data = new byte[64 * 1024 * 3];
            Md5.Hash(data, data.Length, () => calls++);
            Assert.Equal(3, calls);
            Assert.Throws<OperationCanceledException>(() => Md5.Hash(data, data.Length, () => { throw new OperationCanceledException(); }));
        }

        [Fact]
        public void InvalidArgumentsAreRejected()
        {
            Assert.Throws<ArgumentNullException>(() => Md5.Hash(null, 0, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => Md5.Hash(new byte[2], 3, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => Md5.Hash(new byte[2], -1, null));
        }

        private static string Hex(byte[] bytes) { return string.Concat(bytes.Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture))); }
    }
}
