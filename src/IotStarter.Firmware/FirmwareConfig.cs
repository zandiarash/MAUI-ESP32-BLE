using IotStarter.Shared;

namespace IotStarter.Firmware
{
    /// <summary>How the device verifies Wi-Fi credentials received over BLE.</summary>
    public enum ProvisioningStrategy
    {
        /// <summary>
        /// Default. Join Wi-Fi while the BLE link stays up and report the outcome over BLE.
        /// Needs enough RAM for both stacks at once.
        /// </summary>
        JoinWhileBleActive = 0,

        /// <summary>
        /// Low-RAM fallback. Store the credentials unverified, reboot, join with BLE off.
        /// On failure, erase them and reboot into provisioning with the reason recorded.
        /// </summary>
        VerifyAfterReboot = 1
    }

    /// <summary>Build-time configuration of this firmware image.</summary>
    public static class FirmwareConfig
    {
        /// <summary>Product model this image is built for (see <see cref="ModelCatalog"/>).</summary>
        public const ushort ModelId = ModelCatalog.FourChannelSwitchId;

        public const byte FirmwareMajor = 1;
        public const byte FirmwareMinor = 0;
        public const byte FirmwarePatch = 0;

        /// <summary>Select the strategy with this one constant.</summary>
        public static readonly ProvisioningStrategy Strategy = ProvisioningStrategy.JoinWhileBleActive;

        /// <summary>
        /// Answer mDNS / DNS-SD queries. Requires the IOTSTARTER_MDNS compilation symbol and the
        /// MulticastDns package; off by default because it does not fit in RAM next to BLE on boards
        /// without PSRAM. UDP discovery and the last-known address always work.
        /// </summary>
#if IOTSTARTER_MDNS
        public static readonly bool EnableMdns = true;
#else
        public static readonly bool EnableMdns = false;
#endif

        /// <summary>Print nanoFramework GC messages (heap size and free memory) to the debug output.</summary>
        public static readonly bool LogGcMessages = true;

        public const int StatusLedGpio = ModelCatalog.StatusLedGpio;
        public const bool StatusLedActiveLow = false;
        public const int ResetButtonGpio = ModelCatalog.ResetButtonGpio;

        /// <summary>Settings file in the internal flash file system.</summary>
        public const string SettingsDirectory = "I:\\iotstarter";
        public const string SettingsFile = "I:\\iotstarter\\settings.json";
        public const string SettingsTempFile = "I:\\iotstarter\\settings.tmp";

        /// <summary>Most scan entries sent to the app (strongest first).</summary>
        public const int MaxScanEntries = 20;

        /// <summary>Recovery window: retry the stored network this often while no phone is connected.</summary>
        public const int RecoveryRetryIntervalMs = 60000;

        public static string FirmwareVersionString
        {
            get { return FirmwareMajor.ToString() + "." + FirmwareMinor.ToString() + "." + FirmwarePatch.ToString(); }
        }
    }
}
