// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Threading;
using MdfLib.Internal;

namespace MdfLib.Formats.Mdf3
{
    // MDF 3 has no FH/AT/EV/CH blocks; only the HD file comment (TX in the declared code page) is part of the model.
    internal static class FileModelReader
    {
        internal static MdfFileModel Read(BlockResolver resolver, ReaderLimits limits, ReadBudget budget, MdfFileHeader header, CancellationToken token)
        {
            int version = (header.Version[0] - '0') * 100 + (header.Version[2] - '0') * 10 + (header.Version[3] - '0');
            BlockHeader hd = resolver.Resolve(64, "HD", HeaderCodec.MinimumSize(version), 3, token);
            string comment = string.Empty;
            if (hd.Links[1] != 0)
            {
                budget.CheckDepth(2, hd.Links[1]);
                BlockHeader block = resolver.Resolve(hd.Links[1], "TX", 4, cancellationToken: token);
                ReadBudget.Check(nameof(MdfReaderOptions.MaximumTextBytes), (ulong)limits.MaximumTextBytes, 0, block.DataLength, block.Offset);
                budget.ReserveMetadata(96 + 2 * block.DataLength, block.Offset);
                byte[] data = resolver.ReadPayload(block, (int)block.DataLength, token);
                comment = FixedText.Read(data, 0, data.Length, header.CodePage.Value, block.DataOffset, block.Type);
            }
            token.ThrowIfCancellationRequested();
            return new MdfFileModel
            {
                Comment = comment,
                Description = comment,
                Author = header.Author ?? string.Empty,
                Organization = header.Organization ?? string.Empty,
                Project = header.Project ?? string.Empty,
                Subject = header.Subject ?? string.Empty,
                FileHistory = Array.AsReadOnly(new MdfFileHistory[0]),
                Attachments = MdfFileModel.NoAttachments,
                Events = Array.AsReadOnly(new MdfEvent[0]),
                ChannelHierarchy = Array.AsReadOnly(new MdfChannelHierarchy[0])
            };
        }
    }
}
