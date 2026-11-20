// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Facade coverage): the channel observers the facade hands out for a file read
// back by the managed reader (MdfChannelObserver and the CreateChannelObserver* factories). The file is written by the
// facade itself with the types the managed writer stores: unsigned, signed and IEEE float integers and floats (both byte
// orders), UTF-8 text and byte arrays. ASCII, UTF-16, MIME and CANopen values are read-only in the managed engine,
// so the writer refuses them and no test here writes them.
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace MdfLib.Tests
{
    public sealed class MdfLibraryObserverTests : IDisposable
    {
        private readonly string directory_;

        public MdfLibraryObserverTests()
        {
            directory_ = Path.Combine(Path.GetTempPath(), "mdflib-facade-observer-" + Guid.NewGuid().ToString("N"));
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

        // Three samples of nine channels: the master time is 1 s, 2 s and 3 s after the start, the other channels hold the
        // values the tests below check. The text is four bytes long, so the fixed-width field has no padding to compare.
        private string Build(string name)
        {
            string file = Path.Combine(directory_, name + ".mf4");
            ulong[] raw = { 0, 3, 7 };
            string[] texts = { "abcd", "wxyz", "okok" };
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(MdfLibrary.MdfWriterType.Mdf4Basic);
            try
            {
                Assert.True(writer.Init(file));
                MdfLibrary.MdfChannelGroup group = writer.CreateDataGroup().ChannelGroups[0];
                group.Name = "Observed";
                MdfLibrary.MdfChannel master = group.CreateChannel();
                master.Name = "Time";
                master.Unit = "s";
                master.Type = MdfLibrary.ChannelType.Master;
                master.Sync = MdfLibrary.ChannelSyncType.Time;
                master.DataType = MdfLibrary.ChannelDataType.FloatLe;
                master.BitCount = 64;
                MdfLibrary.MdfChannel u32 = Add(group, "U32", MdfLibrary.ChannelDataType.UnsignedIntegerLe, 32);
                u32.Unit = "cnt";
                MdfLibrary.MdfChannelConversion linear = u32.CreateChannelConversion();
                linear.Type = MdfLibrary.ConversionType.Linear;
                linear.Parameter(0, 10.0);
                linear.Parameter(1, 2.0);
                MdfLibrary.MdfChannel u16be = Add(group, "U16BE", MdfLibrary.ChannelDataType.UnsignedIntegerBe, 16);
                MdfLibrary.MdfChannel s16 = Add(group, "S16", MdfLibrary.ChannelDataType.SignedIntegerLe, 16);
                MdfLibrary.MdfChannel s16be = Add(group, "S16BE", MdfLibrary.ChannelDataType.SignedIntegerBe, 16);
                MdfLibrary.MdfChannel f64 = Add(group, "F64", MdfLibrary.ChannelDataType.FloatLe, 64);
                MdfLibrary.MdfChannel f64be = Add(group, "F64BE", MdfLibrary.ChannelDataType.FloatBe, 64);
                MdfLibrary.MdfChannel utf8 = Add(group, "Utf8", MdfLibrary.ChannelDataType.StringUTF8, 32);
                MdfLibrary.MdfChannel bytes = Add(group, "Bytes", MdfLibrary.ChannelDataType.ByteArray, 64);
                Assert.True(writer.InitMeasurement());
                writer.StartMeasurement(1000000000UL);
                for (int sample = 0; sample < raw.Length; sample++)
                {
                    u32.SetChannelValue(raw[sample]);
                    u16be.SetChannelValue((ulong)(256 + sample));
                    s16.SetChannelValue((long)(-5 + sample));
                    s16be.SetChannelValue((long)(-7 + sample));
                    f64.SetChannelValue(1.5 * sample);
                    f64be.SetChannelValue(0.25 * sample);
                    utf8.SetChannelValue(texts[sample]);
                    bytes.SetChannelValue(new byte[] { 1, 2, (byte)sample, 0, 0, 0, 0, 0 });
                    writer.SaveSample(group, 1000000000UL + (ulong)sample * 1000000000UL);
                }
                Assert.True(writer.FinalizeMeasurement());
            }
            finally
            {
                writer.Dispose();
            }
            return file;
        }

        private static MdfLibrary.MdfChannelObserver Find(MdfLibrary.MdfChannelObserver[] observers, string name)
        {
            return Enumerable.First(observers, observer => observer != null && observer.Name == name);
        }

        [Fact]
        public void FactoriesAgreeOnTheChannelsOfTheGroup()
        {
            MdfLibrary.MdfReader reader = new MdfLibrary.MdfReader(Build("factories"));
            using (Release(reader))
            {
                Assert.True(reader.ReadEverythingButData());
                MdfLibrary.MdfDataGroup dataGroup = reader[0];
                Assert.True(reader.ReadData(dataGroup));
                MdfLibrary.MdfChannelGroup channelGroup = dataGroup.ChannelGroups[0];

                Assert.Equal(9, MdfLibrary.MdfLibrary.CreateChannelObserverForDataGroup(dataGroup).Length);
                Assert.Equal(9, MdfLibrary.MdfLibrary.CreateChannelObserverForChannelGroup(dataGroup, channelGroup).Length);

                MdfLibrary.MdfChannel u32 = Enumerable.First(channelGroup.Channels, channel => channel.Name == "U32");
                MdfLibrary.MdfChannelObserver single = MdfLibrary.MdfLibrary.CreateChannelObserver(dataGroup, channelGroup, u32);
                Assert.Equal("U32", single.Name);
                MdfLibrary.MdfChannelObserver byName = MdfLibrary.MdfLibrary.CreateChannelObserverByChannelName(dataGroup, "U32");
                Assert.Equal("U32", byName.Name);
                Assert.Null(MdfLibrary.MdfLibrary.CreateChannelObserverByChannelName(dataGroup, "NoSuchChannel"));
                Assert.Null(MdfLibrary.MdfLibrary.CreateChannelObserverByChannelName(null, "U32"));
            }
        }

        [Fact]
        public void MasterChannelIsReportedAsAMasterAndItsUnitIsKept()
        {
            MdfLibrary.MdfReader reader = new MdfLibrary.MdfReader(Build("master"));
            using (Release(reader))
            {
                Assert.True(reader.ReadEverythingButData());
                MdfLibrary.MdfDataGroup dataGroup = reader[0];
                Assert.True(reader.ReadData(dataGroup));
                MdfLibrary.MdfChannelObserver[] observers = MdfLibrary.MdfLibrary.CreateChannelObserverForDataGroup(dataGroup);
                Assert.True(Find(observers, "Time").IsMaster());
                Assert.Equal("s", Find(observers, "Time").Unit);
                Assert.False(Find(observers, "U32").IsMaster());
                Assert.Equal(3UL, Find(observers, "U32").NofSamples);
            }
        }

        [Fact]
        public void UnsignedChannelReturnsItsRawAndEngineeringValues()
        {
            MdfLibrary.MdfReader reader = new MdfLibrary.MdfReader(Build("unsigned"));
            using (Release(reader))
            {
                Assert.True(reader.ReadEverythingButData());
                MdfLibrary.MdfDataGroup dataGroup = reader[0];
                Assert.True(reader.ReadData(dataGroup));
                MdfLibrary.MdfChannelObserver observer = Find(MdfLibrary.MdfLibrary.CreateChannelObserverForDataGroup(dataGroup), "U32");
                Assert.Equal("cnt", observer.Unit);
                Assert.Equal("U32", observer.Channel.Name);
                ulong raw = 0UL;
                Assert.True(observer.GetChannelValueAsUnsigned(1, ref raw));
                Assert.Equal(3UL, raw);
                double eng = 0.0;
                Assert.True(observer.GetEngValueAsFloat(1, ref eng));
                Assert.Equal(16.0, eng);
                Assert.False(observer.GetChannelValueAsUnsigned(99, ref raw));
            }
        }

        [Fact]
        public void SignedBigEndianFloatAndTextChannelsReturnTheirTypedValues()
        {
            MdfLibrary.MdfReader reader = new MdfLibrary.MdfReader(Build("typed"));
            using (Release(reader))
            {
                Assert.True(reader.ReadEverythingButData());
                MdfLibrary.MdfDataGroup dataGroup = reader[0];
                Assert.True(reader.ReadData(dataGroup));
                MdfLibrary.MdfChannelObserver[] observers = MdfLibrary.MdfLibrary.CreateChannelObserverForDataGroup(dataGroup);

                long signed = 0L;
                Assert.True(Find(observers, "S16").GetChannelValueAsSigned(0, ref signed));
                Assert.Equal(-5L, signed);
                Assert.True(Find(observers, "S16BE").GetChannelValueAsSigned(2, ref signed));
                Assert.Equal(-5L, signed);

                ulong unsigned = 0UL;
                Assert.True(Find(observers, "U16BE").GetChannelValueAsUnsigned(2, ref unsigned));
                Assert.Equal(258UL, unsigned);

                double floating = 0.0;
                Assert.True(Find(observers, "F64").GetChannelValueAsFloat(2, ref floating));
                Assert.Equal(3.0, floating);
                Assert.True(Find(observers, "F64BE").GetChannelValueAsFloat(2, ref floating));
                Assert.Equal(0.5, floating);

                string text = null;
                Assert.True(Find(observers, "Utf8").GetChannelValueAsString(1, ref text));
                Assert.Equal("wxyz", text);

                byte[] bytes = null;
                Assert.True(Find(observers, "Bytes").GetChannelValueAsArray(1, ref bytes));
                Assert.Equal(new byte[] { 1, 2, 1, 0, 0, 0, 0, 0 }, bytes);
            }
        }

        [Fact]
        public void PartialReadKeepsTheHeaderAndTheChannelsOfTheGroup()
        {
            MdfLibrary.MdfReader reader = new MdfLibrary.MdfReader(Build("partial"));
            using (Release(reader))
            {
                Assert.True(reader.ReadHeader());
                Assert.True(reader.ReadMeasurementInfo());
                Assert.True(reader.ReadEverythingButData());
                MdfLibrary.MdfDataGroup dataGroup = reader[0];
                Assert.True(reader.ReadPartialData(dataGroup, 1UL, 2UL));
                Assert.NotNull(reader.Header);
                Assert.NotNull(reader.File);
                Assert.Equal(9, MdfLibrary.MdfLibrary.CreateChannelObserverForDataGroup(dataGroup).Length);
            }
        }

        [Fact]
        public void EveryObserverAnswersEveryGetterForEverySampleWithoutThrowing()
        {
            // The checks above prove the values; this pass proves that every getter of every observer answers, also past the
            // last sample, where the answer is false rather than an exception.
            MdfLibrary.MdfReader reader = new MdfLibrary.MdfReader(Build("sweep"));
            using (Release(reader))
            {
                Assert.True(reader.ReadEverythingButData());
                MdfLibrary.MdfDataGroup dataGroup = reader[0];
                Assert.True(reader.ReadData(dataGroup));
                foreach (MdfLibrary.MdfChannelObserver observer in MdfLibrary.MdfLibrary.CreateChannelObserverForDataGroup(dataGroup))
                {
                    for (ulong sample = 0; sample <= observer.NofSamples + 1; sample++)
                    {
                        ulong unsigned = 0UL;
                        long signed = 0L;
                        double floating = 0.0;
                        string text = null;
                        byte[] bytes = null;
                        observer.GetChannelValueAsUnsigned(sample, ref unsigned);
                        observer.GetChannelValueAsSigned(sample, ref signed);
                        observer.GetChannelValueAsFloat(sample, ref floating);
                        observer.GetChannelValueAsString(sample, ref text);
                        observer.GetChannelValueAsArray(sample, ref bytes);
                        observer.GetEngValueAsUnsigned(sample, ref unsigned);
                        observer.GetEngValueAsSigned(sample, ref signed);
                        observer.GetEngValueAsFloat(sample, ref floating);
                        observer.GetEngValueAsString(sample, ref text);
                        observer.GetEngValueAsArray(sample, ref bytes);
                    }
                    observer.IsMaster();
                }
            }
        }

        private IDisposable Release(MdfLibrary.MdfReader reader)
        {
            return new Scope(reader.Dispose);
        }

        // MdfReader has a Dispose method but does not implement IDisposable, so a using statement cannot take it directly.
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
