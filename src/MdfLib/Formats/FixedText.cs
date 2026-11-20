// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Text;

namespace MdfLib.Formats
{
    internal static class FixedText
    {
        internal static void ValidateCodePage(int codePage)
        {
            if (codePage != 0 && codePage != 28591 && codePage != 20127 && codePage != 65001)
            {
                throw new NotSupportedException("MDF 3 code page " + codePage + " is not supported by this reader.");
            }
        }

        // The text of an MDF 3 field as the wrapper reports it (MdfHelper::Trim): the white space at both ends goes.
        internal static string ReadTrimmed(byte[] bytes, int offset, int width, int codePage, ulong wireOffset, string blockType)
        {
            return Read(bytes, offset, width, codePage, wireOffset, blockType).Trim(' ', '\t', '\n', '\v', '\f', '\r');
        }

        internal static string Read(byte[] bytes, int offset, int width, int codePage, ulong wireOffset, string blockType)
        {
            ValidateCodePage(codePage);
            int count = 0;
            while (count < width && bytes[offset + count] != 0)
            {
                count++;
            }
            while (count > 0 && bytes[offset + count - 1] == 32)
            {
                count--;
            }
            if (codePage == 65001)
            {
                try
                {
                    return new UTF8Encoding(false, true).GetString(bytes, offset, count);
                }
                catch (DecoderFallbackException error)
                {
                    throw new MdfFormatException("Invalid UTF-8 text.", "MDF009", wireOffset, blockType, error);
                }
            }
            char[] chars = new char[count];
            for (int index = 0; index < count; index++)
            {
                byte value = bytes[offset + index];
                if (codePage == 20127 && value > 127)
                {
                    throw new MdfFormatException("Invalid ASCII text.", "MDF009", wireOffset, blockType);
                }
                chars[index] = (char)value;
            }
            return new string(chars);
        }
    }
}
