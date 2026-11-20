// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
// The scenarios of the oracle job. This one file is compiled three times: into
// the facade test project (tests/MdfLib.Tests), into the tool that reads the facade's files with the managed engine
// (tools/MdfLib.Oracle) and into the tool that drives the native wrapper mdflibrary.dll (tools/MdfLib.NativeOracle).
// Every line therefore uses the namespace MdfLibrary and only members of api/mdflibrary-api-baseline.txt, and is
// written in C# 7.3 like the rest of the repository. Nothing here depends on the machine, the clock or the culture.
using System;
using MdfLibrary;

namespace MdfOracle
{
    public static class Scenario
    {
        // 2023-11-14T22:13:20Z as a raw count, and the same stamp two seconds later as the stop.
        public const ulong StartNanoseconds = 1700000000000000000UL;
        public const ulong StopNanoseconds = StartNanoseconds + 2000000000UL;

        // The bus log runs five seconds, so its last frame (one second in) and the stop (five seconds in) differ.
        public const ulong BusStopNanoseconds = StartNanoseconds + 5000000000UL;

        /// <summary>The scenario names, in the order the oracle job runs them.</summary>
        public static readonly string[] Names = { "w1-scenario", "examples-scenario", "can-logger-scenario", "bus-logger-scenario", "bus-logger-start-scenario", "pre-trigger-scenario", "metadata-scenario", "header-tags-scenario", "conversion-texts-scenario", "conversion-numeric-scenario", "conversion-linear-scenario", "conversion-metadata-scenario", "attachment-event-scenario" };

        /// <summary>Writes one scenario to a new file; an unknown name is refused, never guessed.</summary>
        public static void Write(string name, string file)
        {
            if (name == "w1-scenario") { WriteW1(file); return; }
            if (name == "examples-scenario") { WriteExamples(file); return; }
            if (name == "can-logger-scenario") { WriteCanLogger(file); return; }
            if (name == "bus-logger-scenario") { WriteBusLogger(file); return; }
            if (name == "bus-logger-start-scenario") { WriteBusLoggerStart(file); return; }
            if (name == "pre-trigger-scenario") { WritePreTrigger(file); return; }
            if (name == "metadata-scenario") { WriteMetadata(file); return; }
            if (name == "header-tags-scenario") { WriteHeaderTags(file); return; }
            if (name == "conversion-texts-scenario") { WriteConversionTexts(file, true); return; }
            if (name == "conversion-numeric-scenario") { WriteConversionTexts(file, false); return; }
            if (name == "conversion-linear-scenario") { WriteConversionTexts(file, false, true); return; }
            if (name == "conversion-metadata-scenario") { WriteConversionTexts(file, true, false, true); return; }
            if (name == "attachment-event-scenario") { WriteAttachmentEvent(file); return; }
            throw new ArgumentException("Unknown oracle scenario: " + name, "name");
        }

        private static void WriteW1(string file)
        {
            Step("write: construct");
            MdfWriter writer = new MdfWriter(MdfWriterType.Mdf4Basic);
            Step("write: init");
            Require(writer.Init(file), "Init");
            Step("write: header");
            writer.Header.StartTime = StartNanoseconds;
            writer.Header.Author = "SeNS";
            writer.Header.Department = "Testing";
            writer.Header.Project = "MdfLibrary facade";
            writer.Header.Subject = "the oracle scenario";
            writer.Header.Description = "written by the managed facade";
            Step("write: data group");
            MdfDataGroup dataGroup = writer.CreateDataGroup();
            Step("write: channel group");
            MdfChannelGroup group = FirstGroup(dataGroup);
            group.Name = "Engine";
            group.Description = "bench run";
            Step("write: source");
            MdfSourceInformation source = group.CreateSourceInformation();
            source.Name = "TestBench";
            source.Path = "Rig/2";
            source.Bus = BusType.Can;
            Step("write: channels");
            MdfChannel master = group.CreateChannel();
            master.Name = "Time";
            master.Unit = "s";
            master.Type = ChannelType.Master;
            master.Sync = ChannelSyncType.Time;
            master.DataType = ChannelDataType.FloatLe;
            master.BitCount = 64;
            MdfChannel count = group.CreateChannel();
            count.Name = "Counter";
            count.Unit = "-";
            count.Description = "ticks per turn";
            count.DataType = ChannelDataType.UnsignedIntegerLe;
            count.BitCount = 32;
            MdfChannelConversion conversion = count.CreateChannelConversion();
            conversion.Type = ConversionType.Linear;
            conversion.Parameter(0, 10.0);
            conversion.Parameter(1, 2.0);
            MdfChannel label = group.CreateChannel();
            label.Name = "Label";
            label.Unit = "-";
            label.DataType = ChannelDataType.StringUTF8;
            label.BitCount = 32;
            Step("write: init measurement");
            Require(writer.InitMeasurement(), "InitMeasurement");
            Step("write: start");
            writer.StartMeasurement(StartNanoseconds);
            Step("write: samples");
            for (ulong sample = 0UL; sample < 3UL; sample++)
            {
                count.SetChannelValue(7UL + sample);
                label.SetChannelValue("abc");
                // The sample time is an absolute nanosecond stamp one second apart; the master stores the seconds from
                // the start, so it reads 0, 1, 2 here.
                writer.SaveSample(group, StartNanoseconds + sample * 1000000000UL);
            }
            Step("write: stop");
            writer.StopMeasurement(StopNanoseconds);
            Step("write: finalize");
            Require(writer.FinalizeMeasurement(), "FinalizeMeasurement");
            writer.Dispose();
        }

        // The examples of mdflibrary_test_net: a file history, a header tag, a linear conversion and a value-to-text
        // conversion with its default text, in one data group.
        private static void WriteExamples(string file)
        {
            MdfWriter writer = new MdfWriter(MdfWriterType.Mdf4Basic);
            Require(writer.Init(file), "Init");
            WriteHeader(writer, "examples of mdflibrary_test_net", "SeNS");
            MdfDataGroup dataGroup = writer.CreateDataGroup();
            MdfChannelGroup group = FirstGroup(dataGroup);
            group.Name = "Examples";
            group.Description = "the examples scenario";
            MdfChannel time = Master(group, "Time", "s");
            MdfChannel speed = AddChannel(group, "Speed", "km/h", ChannelDataType.FloatLe, 64);
            Linear(speed, 0.0, 0.5);
            MdfChannel gear = AddChannel(group, "Gear", "-", ChannelDataType.UnsignedIntegerLe, 8);
            ValueToText(gear, new[] { "Park", "Drive", "Sport", "Unknown" });
            Require(writer.InitMeasurement(), "InitMeasurement");
            writer.StartMeasurement(StartNanoseconds);
            for (ulong sample = 0UL; sample < 4UL; sample++)
            {
                speed.SetChannelValue(10.0 * sample);
                gear.SetChannelValue(sample % 3UL);
                writer.SaveSample(group, StartNanoseconds + sample * 1000000000UL);
            }
            writer.StopMeasurement(StopNanoseconds);
            Require(writer.FinalizeMeasurement(), "FinalizeMeasurement");
            writer.Dispose();
        }

        // A typical CAN logger: three channel groups, each with a source on the group and on every channel,
        // value-to-text conversions with a default, and the header metadata as ETags.
        private static void WriteCanLogger(string file)
        {
            MdfWriter writer = new MdfWriter(MdfWriterType.Mdf4Basic);
            Require(writer.Init(file), "Init");
            WriteHeader(writer, "typical CAN logger", "SeNS");
            // The HD common properties and start angle and distance: stored by the file, compiled against both wrappers.
            writer.Header.MeasurementId = "3c8f1e0a-2b7d-4f6e-9a51-7d2c4b8e6f10";
            writer.Header.RecorderId = "recorder-07";
            writer.Header.RecorderIndex = 7;
            writer.Header.StartAngle = 0.5;
            writer.Header.StartDistance = 120.25;

            // One data group holds the three CAN groups: the upstream sample queue is then one queue for all of them,
            // and SaveSample(group, time) routes each group's samples to its own group.
            MdfDataGroup canData = writer.CreateDataGroup();
            MdfChannelGroup engine = CanGroup(FirstGroup(canData), "EngineCan", "powertrain frames");
            MdfChannel rpm = AddChannel(engine, "Rpm", "rpm", ChannelDataType.FloatLe, 32);
            MdfChannel gear = AddChannel(engine, "Gear", "-", ChannelDataType.UnsignedIntegerLe, 8);
            ValueToText(gear, new[] { "Park", "Drive", "Sport", "Unknown" });
            MdfChannel coolant = AddChannel(engine, "Coolant", "degC", ChannelDataType.SignedIntegerLe, 16);
            Linear(coolant, -40.0, 1.0);

            MdfChannelGroup brake = CanGroup(canData.CreateChannelGroup(), "BrakeCan", "chassis frames");
            MdfChannel pressure = AddChannel(brake, "Pressure", "bar", ChannelDataType.FloatLe, 64);
            MdfChannel switchState = AddChannel(brake, "Switch", "-", ChannelDataType.UnsignedIntegerLe, 8);
            ValueToText(switchState, new[] { "Off", "On", "Invalid" });

            MdfChannelGroup gateway = CanGroup(canData.CreateChannelGroup(), "GatewayCan", "gateway status");
            MdfChannel status = AddChannel(gateway, "Status", "-", ChannelDataType.StringUTF8, 64);
            MdfChannel frames = AddChannel(gateway, "Frames", "-", ChannelDataType.UnsignedIntegerLe, 32);

            Require(writer.InitMeasurement(), "InitMeasurement");
            writer.StartMeasurement(StartNanoseconds);
            for (ulong sample = 0UL; sample < 3UL; sample++)
            {
                engine.Channels[0].SetChannelValue((double)sample);
                rpm.SetChannelValue(1500.0 + 10.0 * sample);
                gear.SetChannelValue(sample);
                coolant.SetChannelValue((long)(20 + sample));
                writer.SaveSample(engine, StartNanoseconds + sample * 1000000000UL);

                brake.Channels[0].SetChannelValue((double)sample);
                pressure.SetChannelValue(2.5 + 0.25 * sample);
                switchState.SetChannelValue(sample % 2UL);
                writer.SaveSample(brake, StartNanoseconds + sample * 1000000000UL);

                gateway.Channels[0].SetChannelValue((double)sample);
                status.SetChannelValue("ok");
                frames.SetChannelValue(100UL * sample);
                writer.SaveSample(gateway, StartNanoseconds + sample * 1000000000UL);
            }
            writer.StopMeasurement(StopNanoseconds);
            Require(writer.FinalizeMeasurement(), "FinalizeMeasurement");
            writer.Dispose();
        }

        // Pre-trigger: PreTrigTime keeps the samples saved before StartMeasurement for 2.5 s. The samples from
        // -3.0 s to 0.0 s are saved before the start: -3.0 s is the one sample kept before the window, as upstream keeps
        // it; then the samples from 0.5 s to 3.0 s after the start. The stop at 2.75 s drops the 3.0 s and 5.0 s samples.
        private static void WritePreTrigger(string file)
        {
            MdfWriter writer = new MdfWriter(MdfWriterType.Mdf4Basic);
            Require(writer.Init(file), "Init");
            WriteHeader(writer, "pre-trigger window before StartMeasurement", "SeNS");
            writer.PreTrigTime = 2.5;
            MdfDataGroup data = writer.CreateDataGroup();
            MdfChannelGroup group = FirstGroup(data);
            group.Name = "Trigger";
            group.Description = "samples around the trigger";
            Master(group, "t", "s");
            MdfChannel value = AddChannel(group, "Value", "-", ChannelDataType.FloatLe, 64);
            Require(writer.InitMeasurement(), "InitMeasurement");

            for (int index = 0; index <= 6; index++)
            {
                value.SetChannelValue((double)index);
                writer.SaveSample(group, StartNanoseconds - 3000000000UL + (ulong)index * 500000000UL);
            }
            writer.StartMeasurement(StartNanoseconds);
            for (int index = 1; index <= 6; index++)
            {
                value.SetChannelValue(10.0 + index);
                writer.SaveSample(group, StartNanoseconds + (ulong)index * 500000000UL);
            }
            value.SetChannelValue(99.0);
            writer.SaveSample(group, StartNanoseconds + 5000000000UL);
            writer.StopMeasurement(StartNanoseconds + 2750000000UL);
            Require(writer.FinalizeMeasurement(), "FinalizeMeasurement");
            writer.Dispose();
        }

        // The CAN bus logger through SaveCanMessage: three data frames. A remote frame needs CanMessageType, which the
        // wrapper's DLL does not declare here, so remote and error frames are covered by the managed unit tests only.
        private static void WriteBusLogger(string file)
        {
            MdfWriter writer = new MdfWriter(MdfWriterType.MdfBusLogger);
            Require(writer.Init(file), "Init");
            writer.BusType = MdfBusType.CAN;
            WriteHeader(writer, "CAN bus log through SaveCanMessage", "SeNS");
            // The wrapper reports nothing useful here (its test ignores the result), so the frame groups below are
            // what proves the configuration was made.
            writer.PreTrigTime = 0.0;
            writer.CreateBusLogConfiguration();
            // The data frames are found by their group name, as the wrapper's own bus test does.
            MdfDataGroup frames = writer.Header.LastDataGroup;
            Require(frames != null, "the bus log data group");
            MdfChannelGroup dataFrame = frames.GetChannelGroup("CAN_DataFrame");
            Require(dataFrame != null, "the CAN_DataFrame group");
            Require(writer.InitMeasurement(), "InitMeasurement");
            writer.StartMeasurement(StartNanoseconds);

            CanMessage message = new CanMessage();
            for (uint index = 0U; index < 3U; index++)
            {
                message.Reset();
                message.MessageId = 0x123U + index;
                message.BusChannel = 1;
                message.DataBytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
                writer.SaveCanMessage(dataFrame, StartNanoseconds + (ulong)index * 1000000000UL, message);
            }

            // CAN FD frames: a 12-byte and a 64-byte payload (DLC 9 and 15). v2.3.0 stores the payload in full (SD), so the
            // payload is above 8 bytes in the file; the DataLength member converts the DLC code to the length.
            for (uint index = 0U; index < 2U; index++)
            {
                message.Reset();
                message.MessageId = 0x300U + index;
                message.BusChannel = 2;
                message.Edl = true;
                message.Brs = true;
                byte[] fd = new byte[index == 0U ? 12 : 64];
                for (int i = 0; i < fd.Length; i++) { fd[i] = (byte)(i * 3 + index); }
                message.DataBytes = fd;
                writer.SaveCanMessage(dataFrame, StartNanoseconds + (3UL + index) * 1000000000UL, message);
            }

            writer.StopMeasurement(BusStopNanoseconds);
            Require(writer.FinalizeMeasurement(), "FinalizeMeasurement");
            writer.Dispose();
        }

        // for the bus log: StartMeasurement(t) after InitMeasurement with a t other than the start time the header holds.
        // The wrapper keeps the start time of its header (it sets it only while it is zero), the facade patches the header to t
        // as it does for an ordinary MDF 4 writer. The frames are stored relative to t, so the frame times are the same on both sides.
        private static void WriteBusLoggerStart(string file)
        {
            MdfWriter writer = new MdfWriter(MdfWriterType.MdfBusLogger);
            Require(writer.Init(file), "Init");
            writer.BusType = MdfBusType.CAN;
            WriteHeader(writer, "CAN bus log with a start time set after InitMeasurement", "SeNS");
            writer.PreTrigTime = 0.0;
            Require(writer.CreateBusLogConfiguration(), "CreateBusLogConfiguration");
            MdfDataGroup frames = writer.Header.LastDataGroup;
            Require(frames != null, "the bus log data group");
            MdfChannelGroup dataFrame = frames.GetChannelGroup("CAN_DataFrame");
            Require(dataFrame != null, "the CAN_DataFrame group");
            Require(writer.InitMeasurement(), "InitMeasurement");

            ulong measurementStart = StartNanoseconds + 5000000000UL;
            writer.StartMeasurement(measurementStart);
            CanMessage message = new CanMessage();
            for (uint index = 0U; index < 3U; index++)
            {
                message.Reset();
                message.MessageId = 0x200U + index;
                message.BusChannel = 1;
                message.DataBytes = new byte[] { 0x10, (byte)index, 0x30 };
                writer.SaveCanMessage(dataFrame, measurementStart + (ulong)index * 1000000000UL, message);
            }

            writer.StopMeasurement(measurementStart + 3000000000UL);
            Require(writer.FinalizeMeasurement(), "FinalizeMeasurement");
            writer.Dispose();
        }

        // common properties of a channel group, its source and a channel: the MD blocks the facade writes with the tags,
        // beside the comments of the group, the source and the channel. Tags of the three kinds: text, integer and text again.
        private static void WriteMetadata(string file)
        {
            Step("MD construct");
            MdfWriter writer = new MdfWriter(MdfWriterType.Mdf4Basic);
            Step("MD init");
            Require(writer.Init(file), "Init");
            Step("MD header");
            WriteHeader(writer, "common properties of groups, sources and channels", "SeNS");
            Step("MD group");
            MdfDataGroup dataGroup = writer.CreateDataGroup();
            MdfChannelGroup group = FirstGroup(dataGroup);
            group.Name = "Meta";
            group.Description = "group comment";
            MdfMetaData groupMetaData = group.CreateMetaData();
            groupMetaData.AddCommonProperty(Text("Group.Tag", "alpha"));
            groupMetaData.AddCommonProperty(Integer("Group.Count", 42L));
            Step("MD source");
            MdfSourceInformation source = group.CreateSourceInformation();
            source.Name = "Logger";
            source.Description = "source comment";
            source.CreateMetaData().AddCommonProperty(Text("Source.Tag", "beta"));
            Step("MD channels");
            MdfChannel master = group.CreateChannel();
            master.Name = "Time";
            master.Unit = "s";
            master.Type = ChannelType.Master;
            master.Sync = ChannelSyncType.Time;
            master.DataType = ChannelDataType.FloatLe;
            master.BitCount = 64;
            MdfChannel value = group.CreateChannel();
            value.Name = "Value";
            value.Unit = "-";
            value.Description = "channel comment";
            value.DataType = ChannelDataType.UnsignedIntegerLe;
            value.BitCount = 32;
            value.CreateMetaData().AddCommonProperty(Text("Channel.Tag", "gamma"));
            Step("MD init measurement");
            Require(writer.InitMeasurement(), "InitMeasurement");
            Step("MD start");
            writer.StartMeasurement(StartNanoseconds);
            Step("MD samples");
            for (ulong sample = 0UL; sample < 3UL; sample++)
            {
                value.SetChannelValue(7UL + sample);
                writer.SaveSample(group, StartNanoseconds + sample * 1000000000UL);
            }
            Step("MD stop");
            writer.StopMeasurement(StopNanoseconds);
            Step("MD finalize");
            Require(writer.FinalizeMeasurement(), "FinalizeMeasurement");
            writer.Dispose();
        }

        // the creator indices of an attachment and an event (16 bits each), and the MD comments of both.
        private static void WriteAttachmentEvent(string file)
        {
            Step("AE construct");
            MdfWriter writer = new MdfWriter(MdfWriterType.Mdf4Basic);
            Step("AE init");
            Require(writer.Init(file), "Init");
            Step("AE header");
            WriteHeader(writer, "attachments and events with creator indices", "SeNS");
            Step("AE group");
            MdfDataGroup dataGroup = writer.CreateDataGroup();
            MdfChannelGroup group = FirstGroup(dataGroup);
            group.Name = "Annotated";
            MdfChannel master = group.CreateChannel();
            master.Name = "Time";
            master.Unit = "s";
            master.Type = ChannelType.Master;
            master.Sync = ChannelSyncType.Time;
            master.DataType = ChannelDataType.FloatLe;
            master.BitCount = 64;
            Step("AE attachment");
            MdfAttachment attachment = writer.Header.CreateAttachment();
            attachment.FileName = "notes.txt";
            attachment.FileType = "text/plain";
            attachment.CreatorIndex = 2;
            attachment.CreateMetaData().AddCommonProperty(Text("Attachment.Tag", "delta"));
            Step("AE event");
            MdfEvent evt = writer.Header.CreateEvent();
            evt.Name = "Trigger";
            evt.Description = "marker text";
            evt.CreatorIndex = 3UL;
            evt.AddAttachment(attachment);
            evt.MetaData.AddCommonProperty(Text("Event.Tag", "epsilon"));
            Step("AE init measurement");
            Require(writer.InitMeasurement(), "InitMeasurement");
            Step("AE start");
            writer.StartMeasurement(StartNanoseconds);
            Step("AE samples");
            master.SetChannelValue(0.0);
            writer.SaveSample(group, StartNanoseconds);
            Step("AE stop");
            writer.StopMeasurement(StopNanoseconds);
            Step("AE finalize");
            Require(writer.FinalizeMeasurement(), "FinalizeMeasurement");
            writer.Dispose();
        }

        // header common properties beyond the four HD fields, and a source that has only a comment (no name): the SI
        // block must still be written with its comment, as v2.3.0 writes it.
        private static void WriteHeaderTags(string file)
        {
            Step("HT construct");
            MdfWriter writer = new MdfWriter(MdfWriterType.Mdf4Basic);
            Step("HT init");
            Require(writer.Init(file), "Init");
            Step("HT header");
            writer.Header.StartTime = StartNanoseconds;
            writer.Header.Description = "header tags and a comment-only source";
            MdfMetaData headerMetaData = writer.Header.CreateMetaData();
            headerMetaData.AddCommonProperty(Text("Lab.Name", "bench 4"));
            headerMetaData.AddCommonProperty(Integer("Lab.Number", 7L));
            Step("HT group");
            MdfDataGroup dataGroup = writer.CreateDataGroup();
            MdfChannelGroup group = FirstGroup(dataGroup);
            group.Name = "Tagged";
            MdfSourceInformation source = group.CreateSourceInformation();
            source.Description = "only a comment, no name";
            Step("HT channel");
            MdfChannel value = group.CreateChannel();
            value.Name = "Value";
            value.DataType = ChannelDataType.FloatLe;
            value.BitCount = 64;
            value.Type = ChannelType.Master;
            value.Sync = ChannelSyncType.Time;
            Step("HT init measurement");
            Require(writer.InitMeasurement(), "InitMeasurement");
            Step("HT start");
            writer.StartMeasurement(StartNanoseconds);
            Step("HT samples");
            for (ulong sample = 0UL; sample < 2UL; sample++)
            {
                value.SetChannelValue(sample * 1.0);
                writer.SaveSample(group, StartNanoseconds + sample * 1000000000UL);
            }
            Step("HT stop");
            writer.StopMeasurement(StopNanoseconds);
            Step("HT finalize");
            Require(writer.FinalizeMeasurement(), "FinalizeMeasurement");
            writer.Dispose();
        }

        // a conversion block with its own name, unit and comment (MDF 4 cc_tx_name, cc_md_unit, cc_md_comment).
        // The same file with and without the CC texts: a split that shows whether the texts change the numeric result.
        private static void WriteConversionTexts(string file, bool withTexts, bool linear = false, bool tagged = false)
        {
            Step("CT construct");
            MdfWriter writer = new MdfWriter(MdfWriterType.Mdf4Basic);
            Step("CT init");
            Require(writer.Init(file), "Init");
            Step("CT header");
            writer.Header.StartTime = StartNanoseconds;
            writer.Header.Description = "conversion names, units and comments";
            Step("CT group");
            MdfDataGroup dataGroup = writer.CreateDataGroup();
            MdfChannelGroup group = FirstGroup(dataGroup);
            group.Name = "Converted";
            Step("CT master");
            MdfChannel master = group.CreateChannel();
            master.Name = "Time";
            master.Unit = "s";
            master.Type = ChannelType.Master;
            master.Sync = ChannelSyncType.Time;
            master.DataType = ChannelDataType.FloatLe;
            master.BitCount = 64;
            Step("CT raw");
            MdfChannel raw = group.CreateChannel();
            raw.Name = "Raw";
            raw.DataType = ChannelDataType.UnsignedIntegerLe;
            raw.BitCount = 16;
            Step("CT conversion");
            MdfChannelConversion conversion = raw.CreateChannelConversion();
            if (linear)
            {
                // Bisection: the same 16-bit raw channel with a linear conversion (2 + 0.5 * raw), which the examples already read.
                conversion.Type = ConversionType.Linear;
                conversion.Parameter(0, 1.0);
                conversion.Parameter(1, 2.0);
            }
            else
            {
                conversion.Type = ConversionType.Rational;
                conversion.Parameter(0, 0.5);
                conversion.Parameter(1, 2.0);
                conversion.Parameter(2, 1.0);
                conversion.Parameter(3, 0.0);
                conversion.Parameter(4, 0.0);
                conversion.Parameter(5, 1.0);
            }
            if (withTexts)
            {
                conversion.Name = "Calibration";
                conversion.Unit = "rpm";
                conversion.Description = "bench gain";
            }
            if (tagged)
            {
                // the conversion's MD block; its tags go into the comment block with the text (MD XML).
                conversion.CreateMetaData().AddCommonProperty(Text("Calibration.Tag", "delta"));
            }
            Step("CT init measurement");
            Require(writer.InitMeasurement(), "InitMeasurement");
            Step("CT start");
            writer.StartMeasurement(StartNanoseconds);
            Step("CT samples");
            for (ulong sample = 0UL; sample < 2UL; sample++)
            {
                master.SetChannelValue(sample * 1.0);
                raw.SetChannelValue(sample * 3.0);
                writer.SaveSample(group, StartNanoseconds + sample * 1000000000UL);
            }
            Step("CT stop");
            writer.StopMeasurement(StopNanoseconds);
            Step("CT finalize");
            Require(writer.FinalizeMeasurement(), "FinalizeMeasurement");
            writer.Dispose();
        }

        private static MdfETag Integer(string name, long value)
        {
            return new MdfETag { Name = name, DataType = ETagDataType.IntegerType, ValueAsSigned = value };
        }

        private static void WriteHeader(MdfWriter writer, string description, string author)
        {
            writer.Header.StartTime = StartNanoseconds;
            writer.Header.Description = description;
            MdfFileHistory history = writer.Header.CreateFileHistory();
            history.Description = "written by the oracle scenario";
            history.ToolName = "MdfLibrary facade";
            history.ToolVendor = "IH Development";
            history.ToolVersion = "2.3.0";
            history.UserName = "oracle";
            MdfMetaData metaData = writer.Header.CreateMetaData();
            metaData.AddCommonProperty(Text("author", author));
            metaData.AddCommonProperty(Text("department", "Testing"));
            metaData.AddCommonProperty(Text("project", "MdfLibrary facade"));
            metaData.AddCommonProperty(Text("subject", "oracle scenario"));
        }

        private static MdfETag Text(string name, string value)
        {
            return new MdfETag { Name = name, DataType = ETagDataType.StringType, ValueAsString = value };
        }

        // A CAN channel group with its source and its time master, which is the first channel.
        private static MdfChannelGroup CanGroup(MdfChannelGroup group, string name, string description)
        {
            group.Name = name;
            group.Description = description;
            MdfSourceInformation source = group.CreateSourceInformation();
            source.Name = name;
            source.Path = "Bus/" + name;
            source.Bus = BusType.Can;
            // The logger sets the time of each frame itself (seconds from the start); a computed time master in these
            // groups crashed the native wrapper in SaveSample (access violation), so the scenario does not use one here.
            MdfChannel time = Master(group, "t", "s");
            time.Sync = ChannelSyncType.None;
            return group;
        }

        private static MdfChannel Master(MdfChannelGroup group, string name, string unit)
        {
            MdfChannel master = AddChannel(group, name, unit, ChannelDataType.FloatLe, 64);
            master.Type = ChannelType.Master;
            master.Sync = ChannelSyncType.Time; // The writer computes a master of sync Time from the sample stamps.
            return master;
        }

        // A channel with its unit, its type and its own source, the way the logger writes each signal.
        private static MdfChannel AddChannel(MdfChannelGroup group, string name, string unit, ChannelDataType type, uint bits)
        {
            MdfChannel channel = group.CreateChannel();
            channel.Name = name;
            channel.Unit = unit;
            channel.DataType = type;
            channel.BitCount = bits;
            MdfSourceInformation source = channel.CreateSourceInformation();
            source.Name = name;
            source.Path = group.Name + "/" + name;
            source.Bus = BusType.Can;
            return channel;
        }

        private static void Linear(MdfChannel channel, double offset, double factor)
        {
            MdfChannelConversion conversion = channel.CreateChannelConversion();
            conversion.Type = ConversionType.Linear;
            conversion.Parameter(0, offset);
            conversion.Parameter(1, factor);
        }

        // One key per text but the last: texts[keys] is the default text of the conversion, as the wrapper keeps it.
        private static void ValueToText(MdfChannel channel, string[] texts)
        {
            MdfChannelConversion conversion = channel.CreateChannelConversion();
            conversion.Type = ConversionType.ValueToText;
            int keys = texts.Length - 1;
            // One key per text, as the upstream test writes it (Parameter(0, 0.0), Parameter(1, 1.0),...); the last text is the default.
            for (int key = 0; key < keys; key++)
            {
                conversion.Parameter((ushort)key, (double)key);
                conversion.Reference((ushort)key, texts[key]);
            }
            conversion.Reference((ushort)keys, texts[keys]);
        }

        // The channel group of a new data group. The managed writer creates the first one with the data group and
        // allows no second; the wrapper starts with none and needs CreateChannelGroup. Both sides end with one group.
        private static MdfChannelGroup FirstGroup(MdfDataGroup dataGroup)
        {
            MdfChannelGroup[] groups = dataGroup.ChannelGroups;
            return groups.Length > 0 ? groups[0] : dataGroup.CreateChannelGroup();
        }

        // Progress marks on the error output: a failing leg names the last step it reached.
        private static void Step(string name)
        {
            Console.Error.WriteLine("oracle step: " + name);
        }

        private static void Require(bool ok, string step)
        {
            if (!ok) { throw new InvalidOperationException("The oracle scenario stopped at " + step + "."); }
        }
    }
}
