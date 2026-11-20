// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Facade coverage): the conversion types the managed writer stores
// (FacadeTree.TypedDefinition) are read back through the reader's mapping (FacadeReader.ConversionOf and TypeOf), and the
// MDF 3.30 reader gives the master channel the unit of seconds when the file has none (FacadeReader.UnitOf). The types
// listed here are the ones the writer can write; the others are read-only.
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace MdfLib.Tests
{
    public sealed class MdfLibraryConversionReadTests : IDisposable
    {
        private readonly string directory_;

        public MdfLibraryConversionReadTests()
        {
            directory_ = Path.Combine(Path.GetTempPath(), "mdflib-facade-conversion-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory_);
        }

        public void Dispose()
        {
            try { Directory.Delete(directory_, true); }
            catch (IOException) { }
        }

        private static MdfLibrary.MdfChannel Add(MdfLibrary.MdfChannelGroup group, string name, MdfLibrary.ChannelDataType type, uint bits)
        {
            MdfLibrary.MdfChannel channel = group.CreateChannel();
            channel.Name = name;
            channel.DataType = type;
            channel.BitCount = bits;
            return channel;
        }

        private static MdfLibrary.MdfChannel Named(MdfLibrary.MdfChannelGroup group, string name)
        {
            return Enumerable.First(group.Channels, channel => channel.Name == name);
        }

        [Fact]
        public void ConversionTypesWrittenAreReadBackWithTheirType()
        {
            string file = Path.Combine(directory_, "conversions.mf4");
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            try
            {
                Assert.True(writer.Init(file));
                MdfLibrary.MdfChannelGroup group = writer.CreateDataGroup().ChannelGroups[0];
                group.Name = "Conversions";
                MdfLibrary.MdfChannel master = group.CreateChannel();
                master.Name = "Time";
                master.Type = MdfLibrary.ChannelType.Master;
                master.Sync = MdfLibrary.ChannelSyncType.Time;
                master.DataType = MdfLibrary.ChannelDataType.FloatLe;
                master.BitCount = 64;

                MdfLibrary.MdfChannel rational = Add(group, "Rational", MdfLibrary.ChannelDataType.UnsignedIntegerLe, 16);
                MdfLibrary.MdfChannelConversion rationalConversion = rational.CreateChannelConversion();
                rationalConversion.Type = MdfLibrary.ConversionType.Rational;
                rationalConversion.Name = "rational";
                rationalConversion.Unit = "V";
                double[] six = { 0.0, 2.0, 1.0, 0.0, 0.0, 1.0 };
                for (ushort index = 0; index < six.Length; index++) { rationalConversion.Parameter(index, six[index]); }

                MdfLibrary.MdfChannel algebraic = Add(group, "Algebraic", MdfLibrary.ChannelDataType.UnsignedIntegerLe, 16);
                MdfLibrary.MdfChannelConversion algebraicConversion = algebraic.CreateChannelConversion();
                algebraicConversion.Type = MdfLibrary.ConversionType.Algebraic;
                algebraicConversion.Formula = "X*2";

                MdfLibrary.MdfChannel table = Add(group, "Table", MdfLibrary.ChannelDataType.UnsignedIntegerLe, 16);
                MdfLibrary.MdfChannelConversion tableConversion = table.CreateChannelConversion();
                tableConversion.Type = MdfLibrary.ConversionType.ValueToValue;
                tableConversion.Parameter(0, 1.0);
                tableConversion.Parameter(1, 10.0);
                tableConversion.Parameter(2, 2.0);
                tableConversion.Parameter(3, 20.0);

                MdfLibrary.MdfChannel interpolated = Add(group, "Interpolated", MdfLibrary.ChannelDataType.UnsignedIntegerLe, 16);
                MdfLibrary.MdfChannelConversion interpolatedConversion = interpolated.CreateChannelConversion();
                interpolatedConversion.Type = MdfLibrary.ConversionType.ValueToValueInterpolation;
                interpolatedConversion.Parameter(0, 1.0);
                interpolatedConversion.Parameter(1, 10.0);
                interpolatedConversion.Parameter(2, 2.0);
                interpolatedConversion.Parameter(3, 20.0);

                // Value range to value: two ranges [0, 10) and [10, 20) and a default of 999.
                MdfLibrary.MdfChannel ranges = Add(group, "Ranges", MdfLibrary.ChannelDataType.UnsignedIntegerLe, 16);
                MdfLibrary.MdfChannelConversion rangesConversion = ranges.CreateChannelConversion();
                rangesConversion.Type = MdfLibrary.ConversionType.ValueRangeToValue;
                double[] bounds = { 0.0, 10.0, 100.0, 10.0, 20.0, 200.0, 999.0 };
                for (ushort index = 0; index < bounds.Length; index++) { rangesConversion.Parameter(index, bounds[index]); }

                // Value range to text: [0, 9] is "a", [10, 19] is "b", anything else "other".
                MdfLibrary.MdfChannel rangeText = Add(group, "RangeText", MdfLibrary.ChannelDataType.UnsignedIntegerLe, 16);
                MdfLibrary.MdfChannelConversion rangeTextConversion = rangeText.CreateChannelConversion();
                rangeTextConversion.Type = MdfLibrary.ConversionType.ValueRangeToText;
                double[] limits = { 0.0, 9.0, 0.0, 10.0, 19.0, 0.0, 0.0 };
                for (ushort index = 0; index < limits.Length; index++) { rangeTextConversion.Parameter(index, limits[index]); }
                rangeTextConversion.Reference(0, "a");
                rangeTextConversion.Reference(1, "b");
                rangeTextConversion.Reference(2, "other");

                MdfLibrary.MdfChannel text = Add(group, "Text", MdfLibrary.ChannelDataType.UnsignedIntegerLe, 16);
                MdfLibrary.MdfChannelConversion textConversion = text.CreateChannelConversion();
                textConversion.Type = MdfLibrary.ConversionType.ValueToText;
                textConversion.Parameter(0, 1.0);
                textConversion.Parameter(1, 2.0);
                textConversion.Reference(0, "one");
                textConversion.Reference(1, "two");
                textConversion.Reference(2, "other");

                Assert.True(writer.InitMeasurement());
                writer.StartMeasurement(1000000000UL);
                for (int sample = 0; sample < 2; sample++)
                {
                    rational.SetChannelValue(1UL);
                    interpolated.SetChannelValue(1UL);
                    ranges.SetChannelValue(1UL);
                    rangeText.SetChannelValue(1UL);
                    algebraic.SetChannelValue(1UL);
                    table.SetChannelValue(1UL);
                    text.SetChannelValue(1UL);
                    writer.SaveSample(group, 1000000000UL + (ulong)sample * 1000000000UL);
                }
                Assert.True(writer.FinalizeMeasurement());
            }
            finally
            {
                writer.Dispose();
            }

            MdfLibrary.MdfReader reader = new MdfLibrary.MdfReader(file);
            using (Release(reader))
            {
                Assert.True(reader.ReadEverythingButData());
                MdfLibrary.MdfDataGroup dataGroup = reader[0];
                Assert.True(reader.ReadData(dataGroup));
                MdfLibrary.MdfChannelGroup channelGroup = dataGroup.ChannelGroups[0];

                MdfLibrary.MdfChannelConversion rational = Named(channelGroup, "Rational").ChannelConversion;
                Assert.Equal(MdfLibrary.ConversionType.Rational, rational.Type);
                Assert.Equal("rational", rational.Name);
                Assert.Equal("V", rational.Unit);
                Assert.Equal(MdfLibrary.ConversionType.ValueToValueInterpolation, Named(channelGroup, "Interpolated").ChannelConversion.Type);
                Assert.Equal(MdfLibrary.ConversionType.Algebraic, Named(channelGroup, "Algebraic").ChannelConversion.Type);
                Assert.Equal(MdfLibrary.ConversionType.ValueToValue, Named(channelGroup, "Table").ChannelConversion.Type);
                Assert.Equal(MdfLibrary.ConversionType.ValueToText, Named(channelGroup, "Text").ChannelConversion.Type);
                Assert.Equal(MdfLibrary.ConversionType.ValueRangeToValue, Named(channelGroup, "Ranges").ChannelConversion.Type);
                Assert.Equal(MdfLibrary.ConversionType.ValueRangeToText, Named(channelGroup, "RangeText").ChannelConversion.Type);
                Assert.Null(Named(channelGroup, "Time").ChannelConversion);
            }
        }

        [Fact]
        public void EngineeringValuesFollowTheConversionOfEachChannel()
        {
            string file = Path.Combine(directory_, "engineering.mf4");
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            try
            {
                Assert.True(writer.Init(file));
                MdfLibrary.MdfChannelGroup group = writer.CreateDataGroup().ChannelGroups[0];
                group.Name = "Engineering";
                MdfLibrary.MdfChannel master = group.CreateChannel();
                master.Name = "Time";
                master.Type = MdfLibrary.ChannelType.Master;
                master.Sync = MdfLibrary.ChannelSyncType.Time;
                master.DataType = MdfLibrary.ChannelDataType.FloatLe;
                master.BitCount = 64;

                // Rational with P2 = 2, P3 = 1, P6 = 1: the value is 2 * raw + 1.
                MdfLibrary.MdfChannel rational = Add(group, "Rational", MdfLibrary.ChannelDataType.UnsignedIntegerLe, 16);
                MdfLibrary.MdfChannelConversion rationalConversion = rational.CreateChannelConversion();
                rationalConversion.Type = MdfLibrary.ConversionType.Rational;
                double[] six = { 0.0, 2.0, 1.0, 0.0, 0.0, 1.0 };
                for (ushort index = 0; index < six.Length; index++) { rationalConversion.Parameter(index, six[index]); }

                // A table of two keys: 1 gives 10 and 2 gives 20.
                MdfLibrary.MdfChannel table = Add(group, "Table", MdfLibrary.ChannelDataType.UnsignedIntegerLe, 16);
                MdfLibrary.MdfChannelConversion tableConversion = table.CreateChannelConversion();
                tableConversion.Type = MdfLibrary.ConversionType.ValueToValue;
                tableConversion.Parameter(0, 1.0);
                tableConversion.Parameter(1, 10.0);
                tableConversion.Parameter(2, 2.0);
                tableConversion.Parameter(3, 20.0);

                // Value to text: 1 is "one", 2 is "two", anything else the default "other".
                MdfLibrary.MdfChannel text = Add(group, "Text", MdfLibrary.ChannelDataType.UnsignedIntegerLe, 16);
                MdfLibrary.MdfChannelConversion textConversion = text.CreateChannelConversion();
                textConversion.Type = MdfLibrary.ConversionType.ValueToText;
                textConversion.Parameter(0, 1.0);
                textConversion.Parameter(1, 2.0);
                textConversion.Reference(0, "one");
                textConversion.Reference(1, "two");
                textConversion.Reference(2, "other");

                Assert.True(writer.InitMeasurement());
                writer.StartMeasurement(1000000000UL);
                ulong[] raw = { 1, 2, 3 };
                for (int sample = 0; sample < raw.Length; sample++)
                {
                    rational.SetChannelValue(raw[sample]);
                    table.SetChannelValue(raw[sample]);
                    text.SetChannelValue(raw[sample]);
                    writer.SaveSample(group, 1000000000UL + (ulong)sample * 1000000000UL);
                }
                Assert.True(writer.FinalizeMeasurement());
            }
            finally
            {
                writer.Dispose();
            }

            MdfLibrary.MdfReader reader = new MdfLibrary.MdfReader(file);
            using (Release(reader))
            {
                Assert.True(reader.ReadEverythingButData());
                MdfLibrary.MdfDataGroup dataGroup = reader[0];
                Assert.True(reader.ReadData(dataGroup));
                MdfLibrary.MdfChannelObserver[] observers = MdfLibrary.MdfLibrary.CreateChannelObserverForDataGroup(dataGroup);

                MdfLibrary.MdfChannelObserver rationalObserver = Enumerable.First(observers, observer => observer != null && observer.Name == "Rational");
                double value = 0.0;
                Assert.True(rationalObserver.GetEngValueAsFloat(0, ref value));
                Assert.Equal(3.0, value);
                Assert.True(rationalObserver.GetEngValueAsFloat(2, ref value));
                Assert.Equal(7.0, value);

                MdfLibrary.MdfChannelObserver tableObserver = Enumerable.First(observers, observer => observer != null && observer.Name == "Table");
                Assert.True(tableObserver.GetEngValueAsFloat(0, ref value));
                Assert.Equal(10.0, value);
                Assert.True(tableObserver.GetEngValueAsFloat(1, ref value));
                Assert.Equal(20.0, value);

                MdfLibrary.MdfChannelObserver textObserver = Enumerable.First(observers, observer => observer != null && observer.Name == "Text");
                string words = null;
                Assert.True(textObserver.GetEngValueAsString(0, ref words));
                Assert.Equal("one", words);
                Assert.True(textObserver.GetEngValueAsString(1, ref words));
                Assert.Equal("two", words);
                ulong key = 0UL;
                Assert.True(textObserver.GetChannelValueAsUnsigned(2, ref key));
                Assert.Equal(3UL, key);
            }
        }

        [Fact]
        public void ValuesOfEachClrTypeAreStoredInTheChannelsThatHoldThem()
        {
            string file = Path.Combine(directory_, "values.mf4");
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            try
            {
                Assert.True(writer.Init(file));
                MdfLibrary.MdfChannelGroup group = writer.CreateDataGroup().ChannelGroups[0];
                MdfLibrary.MdfChannel master = group.CreateChannel();
                master.Name = "Time";
                master.Type = MdfLibrary.ChannelType.Master;
                master.Sync = MdfLibrary.ChannelSyncType.Time;
                master.DataType = MdfLibrary.ChannelDataType.FloatLe;
                master.BitCount = 64;
                MdfLibrary.MdfChannel count = Add(group, "Count", MdfLibrary.ChannelDataType.UnsignedIntegerLe, 32);
                MdfLibrary.MdfChannel level = Add(group, "Level", MdfLibrary.ChannelDataType.FloatLe, 64);

                Assert.True(writer.InitMeasurement());
                writer.StartMeasurement(1000000000UL);
                // The unsigned value goes to the integer channel, a double and a signed value to the float channel.
                count.SetChannelValue(4UL);
                level.SetChannelValue(6.5);
                writer.SaveSample(group, 1000000000UL);
                count.SetChannelValue(5UL);
                level.SetChannelValue(-7L);
                writer.SaveSample(group, 2000000000UL);
                Assert.True(writer.FinalizeMeasurement());
            }
            finally
            {
                writer.Dispose();
            }

            MdfLibrary.MdfReader reader = new MdfLibrary.MdfReader(file);
            using (Release(reader))
            {
                Assert.True(reader.ReadEverythingButData());
                MdfLibrary.MdfDataGroup dataGroup = reader[0];
                Assert.True(reader.ReadData(dataGroup));
                MdfLibrary.MdfChannelObserver[] observers = MdfLibrary.MdfLibrary.CreateChannelObserverForDataGroup(dataGroup);

                ulong unsigned = 0UL;
                MdfLibrary.MdfChannelObserver countObserver = Enumerable.First(observers, observer => observer != null && observer.Name == "Count");
                Assert.True(countObserver.GetChannelValueAsUnsigned(0, ref unsigned));
                Assert.Equal(4UL, unsigned);
                Assert.True(countObserver.GetChannelValueAsUnsigned(1, ref unsigned));
                Assert.Equal(5UL, unsigned);

                double floating = 0.0;
                MdfLibrary.MdfChannelObserver levelObserver = Enumerable.First(observers, observer => observer != null && observer.Name == "Level");
                Assert.True(levelObserver.GetChannelValueAsFloat(0, ref floating));
                Assert.Equal(6.5, floating);
                Assert.True(levelObserver.GetChannelValueAsFloat(1, ref floating));
                Assert.Equal(-7.0, floating);
            }
        }

        [Fact]
        public void TextConversionsAreWrittenAndReadBackWithTheirType()
        {
            string file = Path.Combine(directory_, "text.mf4");
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            try
            {
                Assert.True(writer.Init(file));
                MdfLibrary.MdfChannelGroup group = writer.CreateDataGroup().ChannelGroups[0];
                MdfLibrary.MdfChannel master = group.CreateChannel();
                master.Name = "Time";
                master.Type = MdfLibrary.ChannelType.Master;
                master.Sync = MdfLibrary.ChannelSyncType.Time;
                master.DataType = MdfLibrary.ChannelDataType.FloatLe;
                master.BitCount = 64;

                // Text to value needs a text channel: "low" is 1 and "high" is 2.
                MdfLibrary.MdfChannel textToValue = Add(group, "TextToValue", MdfLibrary.ChannelDataType.StringUTF8, 64);
                MdfLibrary.MdfChannelConversion textToValueConversion = textToValue.CreateChannelConversion();
                textToValueConversion.Type = MdfLibrary.ConversionType.TextToValue;
                textToValueConversion.Parameter(0, 1.0);
                textToValueConversion.Parameter(1, 2.0);
                textToValueConversion.Reference(0, "low");
                textToValueConversion.Reference(1, "high");

                // Text to text: a translation of the texts into the texts of the same list.
                MdfLibrary.MdfChannel textToText = Add(group, "TextToText", MdfLibrary.ChannelDataType.StringUTF8, 64);
                MdfLibrary.MdfChannelConversion textToTextConversion = textToText.CreateChannelConversion();
                textToTextConversion.Type = MdfLibrary.ConversionType.TextToTranslation;
                textToTextConversion.Reference(0, "on");
                textToTextConversion.Reference(1, "off");

                Assert.True(writer.InitMeasurement());
                writer.StartMeasurement(1000000000UL);
                textToValue.SetChannelValue("low");
                textToText.SetChannelValue("on");
                writer.SaveSample(group, 1000000000UL);
                Assert.True(writer.FinalizeMeasurement());
            }
            finally
            {
                writer.Dispose();
            }

            MdfLibrary.MdfReader reader = new MdfLibrary.MdfReader(file);
            using (Release(reader))
            {
                Assert.True(reader.ReadEverythingButData());
                MdfLibrary.MdfDataGroup dataGroup = reader[0];
                Assert.True(reader.ReadData(dataGroup));
                MdfLibrary.MdfChannelGroup channelGroup = dataGroup.ChannelGroups[0];
                Assert.Equal(MdfLibrary.ConversionType.TextToValue, Named(channelGroup, "TextToValue").ChannelConversion.Type);
                Assert.Equal(MdfLibrary.ConversionType.TextToTranslation, Named(channelGroup, "TextToText").ChannelConversion.Type);
            }
        }

        [Fact]
        public void MdfThreeSignedFloatAndBigEndianTypesReadBack()
        {
            string file = Path.Combine(directory_, "types.mdf");
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf3Basic);
            try
            {
                Assert.True(writer.Init(file));
                MdfLibrary.MdfChannelGroup group = writer.CreateDataGroup().ChannelGroups[0];
                MdfLibrary.MdfChannel master = group.CreateChannel();
                master.Name = "Time";
                master.Type = MdfLibrary.ChannelType.Master;
                master.Sync = MdfLibrary.ChannelSyncType.Time;
                master.DataType = MdfLibrary.ChannelDataType.FloatLe;
                master.BitCount = 64;
                MdfLibrary.MdfChannel signedLe = Add(group, "SignedLe", MdfLibrary.ChannelDataType.SignedIntegerLe, 16);
                MdfLibrary.MdfChannel signedBe = Add(group, "SignedBe", MdfLibrary.ChannelDataType.SignedIntegerBe, 16);
                MdfLibrary.MdfChannel floatLe = Add(group, "FloatLe", MdfLibrary.ChannelDataType.FloatLe, 32);
                MdfLibrary.MdfChannel floatBe = Add(group, "FloatBe", MdfLibrary.ChannelDataType.FloatBe, 64);

                Assert.True(writer.InitMeasurement());
                writer.StartMeasurement(1000000000UL);
                signedLe.SetChannelValue(-3L);
                signedBe.SetChannelValue(-300L);
                floatLe.SetChannelValue(1.5);
                floatBe.SetChannelValue(-2.25);
                writer.SaveSample(group, 1000000000UL);
                Assert.True(writer.FinalizeMeasurement());
            }
            finally
            {
                writer.Dispose();
            }

            MdfLibrary.MdfReader reader = new MdfLibrary.MdfReader(file);
            using (Release(reader))
            {
                Assert.True(reader.ReadEverythingButData());
                MdfLibrary.MdfDataGroup dataGroup = reader[0];
                Assert.True(reader.ReadData(dataGroup));
                MdfLibrary.MdfChannelObserver[] observers = MdfLibrary.MdfLibrary.CreateChannelObserverForDataGroup(dataGroup);

                long signed = 0L;
                MdfLibrary.MdfChannelObserver signedLe = Enumerable.First(observers, observer => observer != null && observer.Name == "SignedLe");
                Assert.True(signedLe.GetChannelValueAsSigned(0, ref signed));
                Assert.Equal(-3L, signed);
                MdfLibrary.MdfChannelObserver signedBe = Enumerable.First(observers, observer => observer != null && observer.Name == "SignedBe");
                Assert.True(signedBe.GetChannelValueAsSigned(0, ref signed));
                Assert.Equal(-300L, signed);

                double floating = 0.0;
                MdfLibrary.MdfChannelObserver floatLe = Enumerable.First(observers, observer => observer != null && observer.Name == "FloatLe");
                Assert.True(floatLe.GetChannelValueAsFloat(0, ref floating));
                Assert.Equal(1.5, floating);
                MdfLibrary.MdfChannelObserver floatBe = Enumerable.First(observers, observer => observer != null && observer.Name == "FloatBe");
                Assert.True(floatBe.GetChannelValueAsFloat(0, ref floating));
                Assert.Equal(-2.25, floating);
            }
        }

        [Fact]
        public void MdfThreeMasterWithoutAUnitReadsAsSeconds()
        {
            string file = Path.Combine(directory_, "master.mdf");
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf3Basic);
            try
            {
                Assert.True(writer.Init(file));
                MdfLibrary.MdfChannelGroup group = writer.CreateDataGroup().ChannelGroups[0];
                MdfLibrary.MdfChannel master = group.CreateChannel();
                master.Name = "Time";
                master.Type = MdfLibrary.ChannelType.Master;
                master.Sync = MdfLibrary.ChannelSyncType.Time;
                master.DataType = MdfLibrary.ChannelDataType.FloatLe;
                master.BitCount = 64;
                MdfLibrary.MdfChannel count = Add(group, "Count", MdfLibrary.ChannelDataType.UnsignedIntegerLe, 16);
                MdfLibrary.MdfChannel big = Add(group, "Big", MdfLibrary.ChannelDataType.UnsignedIntegerBe, 16);

                Assert.True(writer.InitMeasurement());
                writer.StartMeasurement(1000000000UL);
                for (int sample = 0; sample < 3; sample++)
                {
                    count.SetChannelValue((ulong)(5 + sample));
                    big.SetChannelValue((ulong)(300 + sample));
                    writer.SaveSample(group, 1000000000UL + (ulong)sample * 1000000000UL);
                }
                Assert.True(writer.FinalizeMeasurement());
            }
            finally
            {
                writer.Dispose();
            }

            MdfLibrary.MdfReader reader = new MdfLibrary.MdfReader(file);
            using (Release(reader))
            {
                Assert.True(reader.ReadEverythingButData());
                MdfLibrary.MdfDataGroup dataGroup = reader[0];
                Assert.True(reader.ReadData(dataGroup));
                MdfLibrary.MdfChannelObserver[] observers = MdfLibrary.MdfLibrary.CreateChannelObserverForDataGroup(dataGroup);
                MdfLibrary.MdfChannelObserver time = Enumerable.First(observers, observer => observer != null && observer.Name == "Time");
                Assert.Equal("s", time.Unit);
                Assert.True(time.IsMaster());

                ulong value = 0UL;
                MdfLibrary.MdfChannelObserver count = Enumerable.First(observers, observer => observer != null && observer.Name == "Count");
                Assert.True(count.GetChannelValueAsUnsigned(2, ref value));
                Assert.Equal(7UL, value);
                MdfLibrary.MdfChannelObserver big = Enumerable.First(observers, observer => observer != null && observer.Name == "Big");
                Assert.True(big.GetChannelValueAsUnsigned(1, ref value));
                Assert.Equal(301UL, value);
            }
        }

        // MdfReader has a Dispose method but does not implement IDisposable, so a using statement cannot take it directly.
        private static IDisposable Release(MdfLibrary.MdfReader reader)
        {
            return new Scope(reader.Dispose);
        }

        private sealed class Scope : IDisposable
        {
            private readonly Action action_;

            public Scope(Action action)
            {
                action_ = action;
            }

            public void Dispose()
            {
                action_();
            }
        }
    }
}
