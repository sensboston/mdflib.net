// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Facade coverage): the channel observer reads a value of one kind as another
// (FacadeFormat.ParseUnsigned and ParseSigned for text, the numeric casts, the hexadecimal text of a byte array). Each value
// is stored with the exact kind of its channel, which the writer checks (ScalarEncoder).
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace MdfLib.Tests
{
    public sealed class MdfLibraryObserverValueTests : IDisposable
    {
        private readonly string directory_;

        public MdfLibraryObserverValueTests()
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

        private static MdfLibrary.MdfChannelObserver Observer(MdfLibrary.MdfChannelObserver[] observers, string name)
        {
            return Enumerable.First(observers, observer => observer != null && observer.Name == name);
        }

        [Fact]
        public void ValuesAreReadAsTheRequestedKind()
        {
            string file = Path.Combine(directory_, "kinds.mf4");
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
                MdfLibrary.MdfChannel delta = Add(group, "Delta", MdfLibrary.ChannelDataType.SignedIntegerLe, 16);
                MdfLibrary.MdfChannel label = Add(group, "Label", MdfLibrary.ChannelDataType.StringUTF8, 64);
                MdfLibrary.MdfChannel bytes = Add(group, "Bytes", MdfLibrary.ChannelDataType.ByteArray, 16);

                Assert.True(writer.InitMeasurement());
                writer.StartMeasurement(1000000000UL);
                count.SetChannelValue(4UL);
                delta.SetChannelValue(-3L);
                label.SetChannelValue("42");
                bytes.SetChannelValue(new byte[] { 0x0A, 0x0B });
                writer.SaveSample(group, 1000000000UL);
                count.SetChannelValue(5UL);
                delta.SetChannelValue(7L);
                label.SetChannelValue("-7");
                bytes.SetChannelValue(new byte[] { 0x0C, 0x0D });
                writer.SaveSample(group, 2000000000UL);
                Assert.True(writer.FinalizeMeasurement());
            }
            finally
            {
                writer.Dispose();
            }

            MdfLibrary.MdfReader reader = new MdfLibrary.MdfReader(file);
            try
            {
                Assert.True(reader.ReadEverythingButData());
                MdfLibrary.MdfDataGroup dataGroup = reader[0];
                Assert.True(reader.ReadData(dataGroup));
                MdfLibrary.MdfChannelObserver[] observers = MdfLibrary.MdfLibrary.CreateChannelObserverForDataGroup(dataGroup);

                // An unsigned channel read as a float, a signed value and text.
                MdfLibrary.MdfChannelObserver countObserver = Observer(observers, "Count");
                double floating = 0.0;
                Assert.True(countObserver.GetChannelValueAsFloat(0, ref floating));
                Assert.Equal(4.0, floating);
                long signed = 0L;
                Assert.True(countObserver.GetChannelValueAsSigned(1, ref signed));
                Assert.Equal(5L, signed);
                string text = null;
                Assert.True(countObserver.GetChannelValueAsString(0, ref text));
                Assert.Equal("4", text);

                // A signed channel read as text and as a float.
                MdfLibrary.MdfChannelObserver deltaObserver = Observer(observers, "Delta");
                Assert.True(deltaObserver.GetChannelValueAsString(0, ref text));
                Assert.Equal("-3", text);
                Assert.True(deltaObserver.GetChannelValueAsFloat(0, ref floating));
                Assert.Equal(-3.0, floating);

                // A text channel read as numbers: the digits parse, the sign is kept.
                MdfLibrary.MdfChannelObserver labelObserver = Observer(observers, "Label");
                ulong unsigned = 0UL;
                Assert.True(labelObserver.GetChannelValueAsUnsigned(0, ref unsigned));
                Assert.Equal(42UL, unsigned);
                Assert.True(labelObserver.GetChannelValueAsSigned(1, ref signed));
                Assert.Equal(-7L, signed);

                // A byte array read as its first byte and as hexadecimal text of its two bytes.
                MdfLibrary.MdfChannelObserver bytesObserver = Observer(observers, "Bytes");
                Assert.True(bytesObserver.GetChannelValueAsUnsigned(0, ref unsigned));
                Assert.Equal(10UL, unsigned);
                Assert.True(bytesObserver.GetChannelValueAsString(0, ref text));
                Assert.Equal(4, text.Length);
            }
            finally
            {
                reader.Dispose();
            }
        }
    }
}
