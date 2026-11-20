// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MdfLib.Tests.TestDoubles;
using Xunit;

namespace MdfLib.Tests.Writing
{
    // the v2.3.0 CAN layout (the facade's layout) keeps the four CAN frame groups in one data group, in the
    // release's channel order, and the extended layout (the engine default) keeps one data group per frame kind.
    public sealed class BusLoggerLayoutTests
    {
        private static byte[] OneDataFrame(MdfBusLoggerOptions options)
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfBusLogger logger = MdfBusLogger.Create(output, MdfBusType.Can, true, options))
            {
                logger.StartMeasurement();
                logger.Write(new MdfBusMessage { Type = MdfBusMessageType.CanDataFrame, Timestamp = 0.5, Id = 0x123, Data = new byte[] { 1, 2, 3 } });
                logger.StopMeasurement();
                logger.FinalizeMeasurement();
            }
            return output.Snapshot();
        }

        [Fact]
        public void V230LayoutSharesOneDataGroupAndDropsExtendedMembers()
        {
            byte[] file = OneDataFrame(new MdfBusLoggerOptions { Layout = MdfBusLoggerLayout.V230 });
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file)))
            {
                IReadOnlyList<MdfDataGroup> groups = reader.ReadMetadata();
                MdfDataGroup only = Assert.Single(groups);
                Assert.Equal(new[] { "CAN_DataFrame", "CAN_RemoteFrame", "CAN_ErrorFrame", "CAN_OverloadFrame" },
                    only.ChannelGroups.Select(g => g.Name).ToArray());
                string[] members = only.ChannelGroups[0].Channels.Select(c => c.Name).ToArray();
                Assert.DoesNotContain("CAN_DataFrame.FrameDuration", members);
                Assert.DoesNotContain("CAN_DataFrame.CRC", members);
                // The payload follows DataLength, as in the release's channel list.
                int length = System.Array.IndexOf(members, "CAN_DataFrame.DataLength");
                Assert.True(length >= 0);
                Assert.Equal("CAN_DataFrame.DataBytes", members[length + 1]);
            }
        }

        [Fact]
        public void ExtendedLayoutIsTheDefaultAndKeepsOneDataGroupPerKind()
        {
            byte[] file = OneDataFrame(null);
            using (MdfReader reader = MdfReader.Open(new MemoryStream(file)))
            {
                IReadOnlyList<MdfDataGroup> groups = reader.ReadMetadata();
                Assert.Equal(3, groups.Count);
                Assert.Contains("CAN_DataFrame.FrameDuration", groups[0].ChannelGroups[0].Channels.Select(c => c.Name));
            }
        }

        // the payload is stored in an SD block, [UInt32 length][bytes] per frame, and the record holds its offset.
        [Fact]
        public void V230StoresAFdPayloadInTheSdBlock()
        {
            byte[] payload = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
            TestWriteStream output = new TestWriteStream();
            using (MdfBusLogger logger = MdfBusLogger.Create(output, MdfBusType.Can, true, new MdfBusLoggerOptions { Layout = MdfBusLoggerLayout.V230 }))
            {
                logger.StartMeasurement();
                logger.Write(new MdfBusMessage { Type = MdfBusMessageType.CanDataFrame, Timestamp = 0.5, Id = 0x123, IsFd = true, Dlc = 9, Data = payload });
                logger.StopMeasurement();
                logger.FinalizeMeasurement();
            }
            byte[] file = output.Snapshot();
            int sd = IndexOf(file, Encoding.ASCII.GetBytes("##SD"));
            Assert.True(sd >= 0);
            byte[] entry = new byte[] { 12, 0, 0, 0 }.Concat(payload).ToArray();
            Assert.Equal(entry, file.Skip(sd + 24).Take(entry.Length).ToArray());
        }

        // v2.3.0 keeps no payload of a remote frame and writes what the frame gives it: no refusal.
        [Fact]
        public void V230IgnoresThePayloadOfARemoteFrame()
        {
            TestWriteStream output = new TestWriteStream();
            using (MdfBusLogger logger = MdfBusLogger.Create(output, MdfBusType.Can, true, new MdfBusLoggerOptions { Layout = MdfBusLoggerLayout.V230 }))
            {
                logger.StartMeasurement();
                logger.Write(new MdfBusMessage { Type = MdfBusMessageType.CanRemoteFrame, Timestamp = 0.5, Id = 0x123, Dlc = 4, Data = new byte[] { 1, 2 } });
                logger.StopMeasurement();
                logger.FinalizeMeasurement();
            }
            Assert.True(output.Snapshot().Length > 0);
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length && match; j++) { match = haystack[i + j] == needle[j]; }
                if (match) { return i; }
            }
            return -1;
        }

        [Fact]
        public void V230LayoutIsCanOnly()
        {
            Assert.Throws<System.NotSupportedException>(() =>
                MdfBusLogger.Create(new TestWriteStream(), MdfBusType.Lin, true, new MdfBusLoggerOptions { Layout = MdfBusLoggerLayout.V230 }));
        }
    }
}
