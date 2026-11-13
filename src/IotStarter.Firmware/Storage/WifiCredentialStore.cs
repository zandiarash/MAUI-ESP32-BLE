using System;
using System.Net.NetworkInformation;
using IotStarter.Firmware.Diagnostics;

namespace IotStarter.Firmware.Storage
{
    /// <summary>
    /// Wi-Fi credentials in nanoFramework's own configuration store (Wireless80211Configuration).
    /// "Has credentials" means the stored SSID is non-empty. The passphrase is never logged.
    /// </summary>
    public class WifiCredentialStore
    {
        private readonly object _lock = new object();

        public bool HasCredentials
        {
            get
            {
                lock (_lock)
                {
                    Wireless80211Configuration config = GetConfig();
                    return config != null && config.Ssid != null && config.Ssid.Length > 0;
                }
            }
        }

        /// <summary>The stored SSID, or null.</summary>
        public string Ssid
        {
            get
            {
                lock (_lock)
                {
                    Wireless80211Configuration config = GetConfig();
                    return config == null ? null : config.Ssid;
                }
            }
        }

        /// <summary>The stored passphrase, for joining only. Never log the result.</summary>
        public string Passphrase
        {
            get
            {
                lock (_lock)
                {
                    Wireless80211Configuration config = GetConfig();
                    return config == null ? null : config.Password;
                }
            }
        }

        /// <summary>Stores SSID and passphrase (empty passphrase = open network) with auto-connect.</summary>
        public bool Save(string ssid, string passphrase)
        {
            lock (_lock)
            {
                try
                {
                    Wireless80211Configuration config = GetConfig();
                    if (config == null)
                    {
                        Log.Warn("Wi-Fi store: no configuration block on this device");
                        return false;
                    }

                    bool open = passphrase == null || passphrase.Length == 0;
                    config.Ssid = ssid;
                    config.Password = open ? string.Empty : passphrase;
                    config.Authentication = open ? AuthenticationType.None : AuthenticationType.WPA2;
                    config.Encryption = open ? EncryptionType.None : EncryptionType.WPA2;
                    config.Options = Wireless80211Configuration.ConfigurationOptions.Enable
                        | Wireless80211Configuration.ConfigurationOptions.AutoConnect;
                    config.SaveConfiguration();
                    Log.Info("Wi-Fi store: saved credentials for SSID '" + ssid + "' (" + (open ? "open" : "secured") + ")");
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Error("Wi-Fi store: save failed", ex);
                    return false;
                }
            }
        }

        /// <summary>
        /// Erases SSID and passphrase. The station stays enabled (without auto-connect) so the
        /// device can still scan during provisioning.
        /// </summary>
        public bool Erase()
        {
            lock (_lock)
            {
                try
                {
                    Wireless80211Configuration config = GetConfig();
                    if (config == null)
                    {
                        return true;
                    }

                    config.Ssid = string.Empty;
                    config.Password = string.Empty;
                    config.Authentication = AuthenticationType.None;
                    config.Encryption = EncryptionType.None;
                    config.Options = Wireless80211Configuration.ConfigurationOptions.Enable;
                    config.SaveConfiguration();
                    Log.Info("Wi-Fi store: credentials erased");
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Error("Wi-Fi store: erase failed", ex);
                    return false;
                }
            }
        }

        private static Wireless80211Configuration GetConfig()
        {
            Wireless80211Configuration[] configs = Wireless80211Configuration.GetAllWireless80211Configurations();
            return configs != null && configs.Length > 0 ? configs[0] : null;
        }
    }
}
