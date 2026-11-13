// Shared between firmware and app. Conservative C# subset only.
#nullable disable

namespace IotStarter.Shared
{
    /// <summary>Small byte[] helpers that compile unchanged on nanoFramework and .NET 11.</summary>
    public static class ByteHelpers
    {
        private const string HexDigitsUpper = "0123456789ABCDEF";
        private const string HexDigitsLower = "0123456789abcdef";

        /// <summary>CRC-16/CCITT-FALSE (poly 0x1021, init 0xFFFF, no reflection, no final XOR).</summary>
        public static ushort Crc16(byte[] data, int offset, int count)
        {
            ushort crc = 0xFFFF;
            for (int i = offset; i < offset + count; i++)
            {
                crc ^= (ushort)(data[i] << 8);
                for (int bit = 0; bit < 8; bit++)
                {
                    if ((crc & 0x8000) != 0)
                    {
                        crc = (ushort)((crc << 1) ^ 0x1021);
                    }
                    else
                    {
                        crc = (ushort)(crc << 1);
                    }
                }
            }

            return crc;
        }

        public static void WriteUInt16(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)(value >> 8);
        }

        public static ushort ReadUInt16(byte[] buffer, int offset)
        {
            return (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
        }

        /// <summary>Uppercase hex without separators.</summary>
        public static string ToHexUpper(byte[] data, int offset, int count)
        {
            return ToHex(data, offset, count, HexDigitsUpper);
        }

        /// <summary>Lowercase hex without separators.</summary>
        public static string ToHexLower(byte[] data, int offset, int count)
        {
            return ToHex(data, offset, count, HexDigitsLower);
        }

        private static string ToHex(byte[] data, int offset, int count, string digits)
        {
            char[] chars = new char[count * 2];
            for (int i = 0; i < count; i++)
            {
                byte b = data[offset + i];
                chars[i * 2] = digits[b >> 4];
                chars[(i * 2) + 1] = digits[b & 0x0F];
            }

            return new string(chars);
        }

        /// <summary>Parses hex (either case). Returns null for odd length or non-hex characters.</summary>
        public static byte[] FromHex(string hex)
        {
            if (hex == null || (hex.Length % 2) != 0)
            {
                return null;
            }

            byte[] result = new byte[hex.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                int hi = HexValue(hex[i * 2]);
                int lo = HexValue(hex[(i * 2) + 1]);
                if (hi < 0 || lo < 0)
                {
                    return null;
                }

                result[i] = (byte)((hi << 4) | lo);
            }

            return result;
        }

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9')
            {
                return c - '0';
            }

            if (c >= 'a' && c <= 'f')
            {
                return c - 'a' + 10;
            }

            if (c >= 'A' && c <= 'F')
            {
                return c - 'A' + 10;
            }

            return -1;
        }

        /// <summary>Compares two arrays in time that depends only on their length.</summary>
        public static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }

            int diff = 0;
            for (int i = 0; i < a.Length; i++)
            {
                diff |= a[i] ^ b[i];
            }

            return diff == 0;
        }

        /// <summary>True if every byte is printable ASCII (0x20..0x7E), as WPA2 passphrases require.</summary>
        public static bool IsPrintableAscii(byte[] data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] < 0x20 || data[i] > 0x7E)
                {
                    return false;
                }
            }

            return true;
        }

        public static byte[] Slice(byte[] source, int offset, int count)
        {
            byte[] result = new byte[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = source[offset + i];
            }

            return result;
        }

        public static void Copy(byte[] source, int sourceOffset, byte[] destination, int destinationOffset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                destination[destinationOffset + i] = source[sourceOffset + i];
            }
        }

        /// <summary>Formats 4 bytes as dotted IPv4 ("0.0.0.0" for null or wrong length).</summary>
        public static string FormatIPv4(byte[] address)
        {
            if (address == null || address.Length != 4)
            {
                return "0.0.0.0";
            }

            return address[0].ToString() + "." + address[1].ToString() + "." + address[2].ToString() + "." + address[3].ToString();
        }

        /// <summary>Parses dotted IPv4 into 4 bytes. Returns null when invalid.</summary>
        public static byte[] ParseIPv4(string text)
        {
            if (text == null)
            {
                return null;
            }

            byte[] result = new byte[4];
            int part = 0;
            int value = 0;
            int digits = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                if (i == text.Length || text[i] == '.')
                {
                    if (digits == 0 || part > 3)
                    {
                        return null;
                    }

                    result[part++] = (byte)value;
                    value = 0;
                    digits = 0;
                    continue;
                }

                char c = text[i];
                if (c < '0' || c > '9' || digits == 3)
                {
                    return null;
                }

                value = (value * 10) + (c - '0');
                digits++;
                if (value > 255)
                {
                    return null;
                }
            }

            return part == 4 ? result : null;
        }

        public static bool IsZeroIPv4(byte[] address)
        {
            return address == null || address.Length != 4 || (address[0] | address[1] | address[2] | address[3]) == 0;
        }
    }
}
