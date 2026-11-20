// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Facade coverage): the text, number and time helpers that the reader and the
// writer share (FacadeFormat in src/MdfLib/MdfLibrary/FacadeReader.cs). They are pure functions, so each test states the
// value the code path must return. Where the exact value depends on the local time zone or the runtime, the test checks
// a difference between two calls instead of an absolute value.
using System;
using MdfLibrary;
using Xunit;

namespace MdfLib.Tests
{
    public sealed class MdfLibraryFormatTests
    {
        [Theory]
        [InlineData("42", 42UL)]
        [InlineData("  +7", 7UL)]
        [InlineData("-1", ulong.MaxValue)]
        [InlineData("abc", 0UL)]
        [InlineData("", 0UL)]
        [InlineData("18446744073709551616", ulong.MaxValue)]
        public void ParseUnsignedReadsDigitsAndSaturates(string text, ulong expected)
        {
            Assert.Equal(expected, FacadeFormat.ParseUnsigned(text));
        }

        [Theory]
        [InlineData("-5", -5L)]
        [InlineData("+12", 12L)]
        [InlineData("x", 0L)]
        [InlineData("9223372036854775807", long.MaxValue)]
        [InlineData("9223372036854775808", long.MaxValue)]
        [InlineData("-9223372036854775808", long.MinValue)]
        [InlineData("-99999999999999999999", long.MinValue)]
        public void ParseSignedReadsDigitsAndSaturates(string text, long expected)
        {
            Assert.Equal(expected, FacadeFormat.ParseSigned(text));
        }

        [Theory]
        [InlineData("3.5", 3.5)]
        [InlineData("  -2.25e2", -225.0)]
        [InlineData("1e", 1.0)]
        [InlineData("1e3", 1000.0)]
        [InlineData(".5", 0.5)]
        [InlineData("-.5e-1", -0.05)]
        [InlineData("+", 0.0)]
        [InlineData("abc", 0.0)]
        public void ParseDoubleReadsTheLongestNumericPrefix(string text, double expected)
        {
            Assert.Equal(expected, FacadeFormat.ParseDouble(text), 10);
        }

        [Theory]
        [InlineData(3.0, 2, "3")]
        [InlineData(2.5, 2, "2.5")]
        [InlineData(1.25, 0, "1")]
        [InlineData(2.5, 0, "3")]
        [InlineData(-2.5, 1, "-2.5")]
        [InlineData(1234567.891, 3, "1.23457E+06")]
        public void FormatDoubleTrimsTrailingZerosAndSwitchesToG6ForLongText(double value, int decimals, string expected)
        {
            Assert.Equal(expected, FacadeFormat.FormatDouble(value, decimals));
        }

        [Theory]
        [InlineData(4.0, 2, "4.00")]
        [InlineData(2.5, 2, "2.50")]
        [InlineData(2.5, 4, "2.5000")]
        [InlineData(-2.5, 1, "-2.5")]
        public void FormatDoubleKeepsFixedDecimalsWhenAsked(double value, int decimals, string expected)
        {
            Assert.Equal(expected, FacadeFormat.FormatDouble(value, decimals, true));
        }

        [Fact]
        public void FormatDoubleWithoutFixedFlagDelegatesToTheNonFixedForm()
        {
            Assert.Equal(FacadeFormat.FormatDouble(2.5, 3, false), FacadeFormat.FormatDouble(2.5, 3));
        }

        [Fact]
        public void NsToLocalIsoTimeAddsMillisecondsOnlyWhenPresent()
        {
            const ulong second = 1500000000UL * 1000000000UL;
            Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", FacadeFormat.NsToLocalIsoTime(second));
            Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.250$", FacadeFormat.NsToLocalIsoTime(second + 250000000UL));
        }

        [Fact]
        public void CanOpenTimeAddsMillisecondsAndDaysAsDeclared()
        {
            byte[] zero = new byte[6];
            byte[] tenSeconds = new byte[] { 0x10, 0x27, 0, 0, 0, 0 };
            byte[] oneDay = new byte[] { 0, 0, 0, 0, 1, 0 };
            ulong baseline = FacadeFormat.CanOpenNs(zero, false);
            Assert.Equal(10000UL * 1000000UL, FacadeFormat.CanOpenNs(tenSeconds, false) - baseline);
            Assert.Equal(86400UL * 1000000000UL, FacadeFormat.CanOpenNs(oneDay, false) - baseline);
        }

        [Fact]
        public void CanOpenDateCountsTheDayAndTheMinuteFields()
        {
            // Byte 4 is the day of the month, byte 2 the minute; the milliseconds of the minute are bytes 0 and 1.
            byte[] day1 = new byte[] { 0, 0, 0, 0, 1, 1, 84 };
            byte[] day2 = new byte[] { 0, 0, 0, 0, 2, 1, 84 };
            byte[] minute5 = new byte[] { 0, 0, 5, 0, 1, 1, 84 };
            byte[] ms5000 = new byte[] { 0x88, 0x13, 0, 0, 1, 1, 84 };
            ulong first = FacadeFormat.CanOpenNs(day1, true);
            Assert.Equal(86400UL * 1000000000UL, FacadeFormat.CanOpenNs(day2, true) - first);
            Assert.Equal(300UL * 1000000000UL, FacadeFormat.CanOpenNs(minute5, true) - first);
            Assert.Equal(5000UL * 1000000UL, FacadeFormat.CanOpenNs(ms5000, true) - first);
        }

        [Fact]
        public void TxTextUnwrapsTheTxElementAndKeepsEverythingElse()
        {
            Assert.Equal(string.Empty, FacadeFormat.TxText(null));
            Assert.Equal(string.Empty, FacadeFormat.TxText(string.Empty));
            Assert.Equal("plain text", FacadeFormat.TxText("plain text"));
            Assert.Equal("<TX>unclosed", FacadeFormat.TxText("<TX>unclosed"));
            Assert.Equal("hello", FacadeFormat.TxText("<TX>hello</TX>"));
            Assert.Equal("abc", FacadeFormat.TxText("<CN><TX>abc</TX></CN>"));
            Assert.Equal("<CN>no text</CN>", FacadeFormat.TxText("<CN>no text</CN>"));
            Assert.Equal("<!DOCTYPE x [<!ENTITY e 'v'>]><TX>a</TX>", FacadeFormat.TxText("<!DOCTYPE x [<!ENTITY e 'v'>]><TX>a</TX>"));
        }

        [Fact]
        public void HexTextWritesTwoUpperCaseDigitsPerByte()
        {
            Assert.Equal(string.Empty, FacadeFormat.HexText(null));
            Assert.Equal(string.Empty, FacadeFormat.HexText(new byte[0]));
            Assert.Equal("0AFF", FacadeFormat.HexText(new byte[] { 0x0A, 0xFF }));
        }
    }
}
