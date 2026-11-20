// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// Facade coverage): the refusals of the writer's tree check, FacadeTree.Validate
// and ValidateConversion, which run when the measurement starts. Each case sets one property the managed writer cannot
// store and expects the refusal; the valid configurations start. The messages are the facade's own and are not compared.
using System;
using System.IO;
using Xunit;

namespace MdfLib.Tests
{
    public sealed class MdfLibraryRefusalTests : IDisposable
    {
        private readonly string directory_;

        public MdfLibraryRefusalTests()
        {
            directory_ = Path.Combine(Path.GetTempPath(), "mdflib-facade-refusal-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory_);
        }

        public void Dispose()
        {
            try { Directory.Delete(directory_, true); }
            catch (IOException) { }
        }

        private MdfLibrary.MdfWriter NewWriter(string name, MdfLibrary.MdfWriterType type)
        {
            string extension = type == MdfLibrary.MdfWriterType.Mdf3Basic ? ".mdf" : ".mf4";
            MdfLibrary.MdfWriter writer = new MdfLibrary.MdfWriter(type);
            Assert.True(writer.Init(Path.Combine(directory_, name + extension)));
            return writer;
        }

        // The group every case starts from: a master channel and one 32-bit unsigned data channel.
        private static MdfLibrary.MdfChannelGroup Group(MdfLibrary.MdfWriter writer, out MdfLibrary.MdfChannel data)
        {
            MdfLibrary.MdfChannelGroup group = writer.CreateDataGroup().ChannelGroups[0];
            MdfLibrary.MdfChannel master = group.CreateChannel();
            master.Name = "Time";
            master.Type = MdfLibrary.ChannelType.Master;
            master.Sync = MdfLibrary.ChannelSyncType.Time;
            master.DataType = MdfLibrary.ChannelDataType.FloatLe;
            master.BitCount = 64;
            data = group.CreateChannel();
            data.Name = "Data";
            data.DataType = MdfLibrary.ChannelDataType.UnsignedIntegerLe;
            data.BitCount = 32;
            return group;
        }

        [Fact]
        public void TheBaseConfigurationStarts()
        {
            MdfLibrary.MdfWriter writer = NewWriter("base", MdfLibrary.MdfWriterType.Mdf4Basic);
            using (Release(writer))
            {
                MdfLibrary.MdfChannel data;
                Group(writer, out data);
                Assert.True(writer.InitMeasurement());
            }
        }

        [Fact]
        public void MdfFourCommonPropertiesStart()
        {
            MdfLibrary.MdfWriter writer = NewWriter("mdf4-common", MdfLibrary.MdfWriterType.Mdf4Basic);
            using (Release(writer))
            {
                MdfLibrary.MdfChannel data;
                MdfLibrary.MdfChannelGroup group = Group(writer, out data);
                group.Description = "bench";
                group.Name = "Engine";
                data.DisplayName = "Data display";
                data.CreateSourceInformation().Name = "source";
                MdfLibrary.MdfChannelConversion linear = data.CreateChannelConversion();
                linear.Type = MdfLibrary.ConversionType.Linear;
                linear.Parameter(0, 10.0);
                linear.Parameter(1, 2.0);
                Assert.True(writer.InitMeasurement());
            }
        }

        [Fact]
        public void GroupFlagsAreRefused()
        {
            Refused(writer =>
            {
                MdfLibrary.MdfChannel data;
                Group(writer, out data).Flags = 1;
            });
        }

        [Fact]
        public void GroupRecordIdIsRefused()
        {
            Refused(writer =>
            {
                MdfLibrary.MdfChannel data;
                Group(writer, out data).RecordId = 5UL;
            });
        }

        [Fact]
        public void GroupPathSeparatorIsRefused()
        {
            Refused(writer =>
            {
                MdfLibrary.MdfChannel data;
                Group(writer, out data).PathSeparator = '.';
            });
        }

        [Fact]
        public void MaxLengthChannelIsRefused()
        {
            Refused(writer =>
            {
                MdfLibrary.MdfChannel data;
                Group(writer, out data);
                data.Type = MdfLibrary.ChannelType.MaxLength;
            });
        }

        [Fact]
        public void VirtualChannelIsRefused()
        {
            Refused(writer =>
            {
                MdfLibrary.MdfChannel data;
                Group(writer, out data);
                data.Type = MdfLibrary.ChannelType.VirtualData;
            });
        }

        [Fact]
        public void SynchronizedChannelIsRefused()
        {
            Refused(writer =>
            {
                MdfLibrary.MdfChannel data;
                Group(writer, out data);
                data.Type = MdfLibrary.ChannelType.Sync;
            });
        }

        [Fact]
        public void BitPackedChannelIsRefused()
        {
            Refused(writer =>
            {
                MdfLibrary.MdfChannel data;
                Group(writer, out data);
                data.BitOffset = 3;
            });
        }

        [Fact]
        public void VariableLengthRecordIdIsRefused()
        {
            Refused(writer =>
            {
                MdfLibrary.MdfChannel data;
                Group(writer, out data);
                data.VlsdRecordId = 1UL;
            });
        }

        [Fact]
        public void FloatWidthOtherThanThirtyTwoOrSixtyFourBitsIsRefused()
        {
            Refused(writer =>
            {
                MdfLibrary.MdfChannel data;
                Group(writer, out data);
                data.DataType = MdfLibrary.ChannelDataType.FloatLe;
                data.BitCount = 16;
            });
        }

        [Fact]
        public void ComplexWidthOtherThanSixtyFourOrOneHundredTwentyEightBitsIsRefused()
        {
            Refused(writer =>
            {
                MdfLibrary.MdfChannel data;
                Group(writer, out data);
                data.DataType = MdfLibrary.ChannelDataType.ComplexLe;
                data.BitCount = 32;
            });
        }

        [Fact]
        public void TwoMasterChannelsAreRefused()
        {
            Refused(writer =>
            {
                MdfLibrary.MdfChannel data;
                MdfLibrary.MdfChannelGroup group = Group(writer, out data);
                MdfLibrary.MdfChannel second = group.CreateChannel();
                second.Name = "Second time";
                second.Type = MdfLibrary.ChannelType.Master;
                second.DataType = MdfLibrary.ChannelDataType.FloatLe;
                second.BitCount = 64;
            });
        }

        [Fact]
        public void ConversionWithOnlyParametersIsRefused()
        {
            Refused(writer =>
            {
                MdfLibrary.MdfChannel data;
                Group(writer, out data);
                MdfLibrary.MdfChannelConversion conversion = data.CreateChannelConversion();
                conversion.Parameter(0, 1.0);
            });
        }

        [Fact]
        public void LinearConversionWithANameIsRefused()
        {
            Refused(writer =>
            {
                MdfLibrary.MdfChannel data;
                Group(writer, out data);
                MdfLibrary.MdfChannelConversion conversion = data.CreateChannelConversion();
                conversion.Type = MdfLibrary.ConversionType.Linear;
                conversion.Name = "scaled";
                conversion.Parameter(0, 10.0);
                conversion.Parameter(1, 2.0);
            });
        }

        [Fact]
        public void UntypedConversionWithANameIsRefused()
        {
            Refused(writer =>
            {
                MdfLibrary.MdfChannel data;
                Group(writer, out data);
                MdfLibrary.MdfChannelConversion conversion = data.CreateChannelConversion();
                conversion.Name = "untyped";
            });
        }

        [Fact]
        public void MdfThreeRefusesTheGroupDescriptionAndName()
        {
            Refused(writer =>
            {
                MdfLibrary.MdfChannel data;
                Group(writer, out data).Description = "bench";
            }, MdfLibrary.MdfWriterType.Mdf3Basic);
            Refused(writer =>
            {
                MdfLibrary.MdfChannel data;
                Group(writer, out data).Name = "Engine";
            }, MdfLibrary.MdfWriterType.Mdf3Basic);
        }

        [Fact]
        public void MdfThreeRefusesTheLongIdentifierSourceAndVariableLengthChannels()
        {
            Refused(writer =>
            {
                MdfLibrary.MdfChannel data;
                Group(writer, out data);
                data.DisplayName = "Data display";
            }, MdfLibrary.MdfWriterType.Mdf3Basic);
            Refused(writer =>
            {
                MdfLibrary.MdfChannel data;
                Group(writer, out data);
                data.Type = MdfLibrary.ChannelType.VariableLength;
            }, MdfLibrary.MdfWriterType.Mdf3Basic);
        }

        [Fact]
        public void MdfThreeHasNoChannelComposition()
        {
            MdfLibrary.MdfWriter writer = NewWriter("mdf3-composition", MdfLibrary.MdfWriterType.Mdf3Basic);
            using (Release(writer))
            {
                MdfLibrary.MdfChannel data;
                Group(writer, out data);
                Assert.Throws<NotSupportedException>(() => data.CreateChannelComposition());
            }
        }

        [Fact]
        public void EmbeddedAttachmentWithoutAFileNameIsRefused()
        {
            Refused(writer =>
            {
                MdfLibrary.MdfChannel data;
                Group(writer, out data);
                MdfLibrary.MdfAttachment attachment = writer.Header.CreateAttachment();
                attachment.Embedded = true;
            });
        }

        [Fact]
        public void EventPreTriggerIsRefused()
        {
            MdfLibrary.MdfWriter writer = NewWriter("event-pretrig", MdfLibrary.MdfWriterType.Mdf4Basic);
            using (Release(writer))
            {
                MdfLibrary.MdfEvent evt = writer.Header.CreateEvent();
                // The engine has no field for the trigger times: a non-zero value is refused by the setter, zero is accepted.
                Assert.Throws<NotSupportedException>(() => evt.PreTrig = 1.0);
                evt.PreTrig = 0.0;
            }
        }

        [Fact]
        public void PolynomialExponentialAndLogarithmicConversionsAreRefused()
        {
            MdfLibrary.ConversionType[] kinds = { MdfLibrary.ConversionType.Polynomial, MdfLibrary.ConversionType.Exponential, MdfLibrary.ConversionType.Logarithmic };
            foreach (MdfLibrary.ConversionType kind in kinds)
            {
                Refused(writer =>
                {
                    MdfLibrary.MdfChannel data;
                    Group(writer, out data);
                    MdfLibrary.MdfChannelConversion conversion = data.CreateChannelConversion();
                    conversion.Type = kind;
                    // A polynomial takes six parameters; the MDF 3 exponential and logarithmic take seven, one of P1 and P4 zero.
                    int count = kind == MdfLibrary.ConversionType.Polynomial ? 6 : 7;
                    for (ushort index = 0; index < count; index++) { conversion.Parameter(index, index == 0 ? 0.0 : 1.0 + index); }
                });
            }
        }

        [Fact]
        public void StringChannelInAnMdfFourMeasurementIsRefused()
        {
            Refused(writer =>
            {
                MdfLibrary.MdfChannel data;
                MdfLibrary.MdfChannelGroup group = Group(writer, out data);
                MdfLibrary.MdfChannel text = group.CreateChannel();
                text.Name = "Text";
                text.DataType = MdfLibrary.ChannelDataType.StringAscii;
                text.BitCount = 64;
            });
        }

        [Fact]
        public void MdfThreeRefusesTheGroupSourceInformation()
        {
            Refused(writer =>
            {
                MdfLibrary.MdfChannel data;
                MdfLibrary.MdfChannelGroup group = Group(writer, out data);
                group.CreateSourceInformation().Name = "source";
            }, MdfLibrary.MdfWriterType.Mdf3Basic);
        }

        [Fact]
        public void SaveCanMessageNeedsABusLogWriter()
        {
            MdfLibrary.MdfWriter writer = NewWriter("can-refused", MdfLibrary.MdfWriterType.Mdf4Basic);
            using (Release(writer))
            {
                MdfLibrary.MdfChannel data;
                MdfLibrary.MdfChannelGroup group = Group(writer, out data);
                MdfLibrary.CanMessage message = new MdfLibrary.CanMessage();
                Assert.Throws<NotSupportedException>(() => writer.SaveCanMessage(group, 0UL, message));
            }
        }

        // Starts a measurement with the group the case built and expects the refusal; the writer is closed afterwards.
        private void Refused(Action<MdfLibrary.MdfWriter> configure, MdfLibrary.MdfWriterType type = MdfLibrary.MdfWriterType.Mdf4Basic)
        {
            MdfLibrary.MdfWriter writer = NewWriter("refused-" + Guid.NewGuid().ToString("N"), type);
            using (Release(writer))
            {
                configure(writer);
                Assert.Throws<NotSupportedException>(() => writer.InitMeasurement());
            }
        }

        // MdfWriter has a Dispose method but does not implement IDisposable, so a using statement cannot take it directly.
        private static IDisposable Release(MdfLibrary.MdfWriter writer)
        {
            return new Scope(writer.Dispose);
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
