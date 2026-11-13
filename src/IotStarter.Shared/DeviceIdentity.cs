// Shared between firmware and app. Conservative C# subset only.
#nullable disable

namespace IotStarter.Shared
{
    /// <summary>
    /// Stable device identity derived from the 6-byte Wi-Fi station MAC address.
    /// Device ID = 12 uppercase hex digits; short ID = last 3 bytes.
    /// </summary>
    public static class DeviceIdentity
    {
        /// <summary>"A1B2C3D4E5F6". Returns null if the MAC is not 6 bytes.</summary>
        public static string FormatDeviceId(byte[] mac)
        {
            if (mac == null || mac.Length != ProtocolConstants.DeviceIdBytes)
            {
                return null;
            }

            return ByteHelpers.ToHexUpper(mac, 0, mac.Length);
        }

        /// <summary>Parses "A1B2C3D4E5F6" (either case) into 6 bytes, or null.</summary>
        public static byte[] ParseDeviceId(string deviceId)
        {
            byte[] bytes = ByteHelpers.FromHex(deviceId);
            if (bytes == null || bytes.Length != ProtocolConstants.DeviceIdBytes)
            {
                return null;
            }

            return bytes;
        }

        /// <summary>Last 4 hex digits of the device ID, uppercase (e.g. "E5F6").</summary>
        public static string NameSuffix(byte[] mac)
        {
            return ByteHelpers.ToHexUpper(mac, ProtocolConstants.DeviceIdBytes - 2, 2);
        }

        /// <summary>BLE local name, e.g. "IOT-E5F6".</summary>
        public static string BleName(string prefix, byte[] mac)
        {
            return prefix + "-" + NameSuffix(mac);
        }

        /// <summary>DHCP / mDNS host label, e.g. "iot-a1b2c3d4e5f6" (without ".local").</summary>
        public static string HostName(string prefix, byte[] mac)
        {
            return prefix.ToLower() + "-" + ByteHelpers.ToHexLower(mac, 0, mac.Length);
        }

        /// <summary>Uppercase hex of the short ID carried in the advertisement (e.g. "D4E5F6").</summary>
        public static string FormatShortId(byte[] shortId)
        {
            if (shortId == null || shortId.Length != ProtocolConstants.ShortIdBytes)
            {
                return null;
            }

            return ByteHelpers.ToHexUpper(shortId, 0, shortId.Length);
        }

        /// <summary>True when the full device ID ends with the advertised short ID.</summary>
        public static bool MatchesShortId(string deviceId, byte[] shortId)
        {
            string shortHex = FormatShortId(shortId);
            if (deviceId == null || shortHex == null || deviceId.Length != ProtocolConstants.DeviceIdBytes * 2)
            {
                return false;
            }

            return deviceId.ToUpper().EndsWith(shortHex);
        }
    }
}
