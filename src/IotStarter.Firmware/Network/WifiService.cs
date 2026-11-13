using System;
using System.Collections;
using System.Device.Wifi;
using System.Net.NetworkInformation;
using System.Threading;
using IotStarter.Firmware.Diagnostics;
using IotStarter.Shared;

namespace IotStarter.Firmware.Network
{
    /// <summary>One scanned network, merged by SSID.</summary>
    public class ScannedNetwork
    {
        public string Ssid;
        public int Rssi;
    }

    /// <summary>
    /// Wraps the ESP32 Wi-Fi station. Joining uses WifiAdapter.Connect directly instead of
    /// WifiNetworkHelper, because the helper accepts one setup per run while provisioning must allow
    /// a retry after a wrong password (the approach of the official ImprovWifi sample).
    /// Thread-safe: one Wi-Fi operation at a time.
    /// </summary>
    public class WifiService
    {
        private const int PollIntervalMs = 250;

        private readonly object _opLock = new object();
        private readonly WifiAdapter _adapter;
        private readonly AutoResetEvent _scanDone = new AutoResetEvent(false);

        public WifiService()
        {
            _adapter = WifiAdapter.FindAllAdapters()[0];
            _adapter.AvailableNetworksChanged += OnAvailableNetworksChanged;
        }

        /// <summary>The station MAC address (6 bytes): the source of the device ID.</summary>
        public static byte[] GetStationMac()
        {
            NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
            for (int i = 0; i < interfaces.Length; i++)
            {
                if (interfaces[i].NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                {
                    byte[] mac = interfaces[i].PhysicalAddress;
                    if (mac != null && mac.Length == ProtocolConstants.DeviceIdBytes)
                    {
                        return mac;
                    }
                }
            }

            throw new InvalidOperationException("No Wi-Fi station interface with a 6-byte MAC");
        }

        /// <summary>Current IPv4 address as 4 bytes, or null if none.</summary>
        public byte[] CurrentIPv4()
        {
            NetworkInterface ni = StationInterface();
            if (ni == null)
            {
                return null;
            }

            byte[] address = ByteHelpers.ParseIPv4(ni.IPv4Address);
            return ByteHelpers.IsZeroIPv4(address) ? null : address;
        }

        public bool HasIPv4
        {
            get { return CurrentIPv4() != null; }
        }

        /// <summary>Sets the DHCP host name used on the next association (WifiAdapter.SetDeviceName).</summary>
        public void SetHostName(string hostName)
        {
            try
            {
                _adapter.SetDeviceName(hostName);
                Log.Info("Wi-Fi: DHCP host name '" + hostName + "'");
            }
            catch (Exception ex)
            {
                Log.Error("Wi-Fi: setting the DHCP host name failed", ex);
            }
        }

        /// <summary>
        /// Joins <paramref name="ssid"/> and waits for a DHCP address. Returns ErrorCode.None on success.
        /// <paramref name="cancel"/> is polled while waiting for DHCP; the association itself (up to 20 s
        /// inside the native call) cannot be interrupted, so a cancel takes effect when it returns.
        /// </summary>
        public ErrorCode Join(string ssid, string passphrase, WifiReconnectionKind reconnection, CancelFlag cancel)
        {
            lock (_opLock)
            {
                bool open = passphrase == null || passphrase.Length == 0;
                Log.Info("Wi-Fi: joining '" + ssid + "' (" + (open ? "open" : "passphrase length " + passphrase.Length.ToString()) + ")");
                Log.Memory("before Wi-Fi join");

                try
                {
                    EnsureDhcp();
                    _adapter.Disconnect();

                    WifiConnectionResult result = _adapter.Connect(ssid, reconnection, open ? string.Empty : passphrase);
                    ErrorCode error = MapStatus(result.ConnectionStatus, open);
                    Log.Info("Wi-Fi: association result " + StatusName(result.ConnectionStatus));

                    if (cancel != null && cancel.IsSet)
                    {
                        _adapter.Disconnect();
                        return ErrorCode.Cancelled;
                    }

                    if (error != ErrorCode.None)
                    {
                        _adapter.Disconnect();
                        return error;
                    }

                    long deadline = DeviceRuntime.NowMs + ProtocolConstants.DhcpTimeoutMs;
                    while (DeviceRuntime.NowMs < deadline)
                    {
                        if (CurrentIPv4() != null)
                        {
                            Log.Info("Wi-Fi: got address " + ByteHelpers.FormatIPv4(CurrentIPv4()));
                            Log.Memory("after Wi-Fi join");
                            return ErrorCode.None;
                        }

                        if (cancel != null && cancel.IsSet)
                        {
                            _adapter.Disconnect();
                            return ErrorCode.Cancelled;
                        }

                        Thread.Sleep(PollIntervalMs);
                    }

                    Log.Warn("Wi-Fi: associated but no DHCP address");
                    _adapter.Disconnect();
                    return ErrorCode.NoIpAddress;
                }
                catch (Exception ex)
                {
                    Log.Error("Wi-Fi: join failed", ex);
                    try
                    {
                        _adapter.Disconnect();
                    }
                    catch (Exception)
                    {
                        // Already disconnected.
                    }

                    return ErrorCode.Internal;
                }
            }
        }

        /// <summary>
        /// Scans for networks. Returns entries merged by SSID (strongest RSSI kept), strongest first,
        /// hidden networks removed. Returns null on failure or timeout.
        /// </summary>
        public ScannedNetwork[] Scan(CancelFlag cancel)
        {
            lock (_opLock)
            {
                try
                {
                    _scanDone.Reset();
                    _adapter.ScanAsync();

                    long deadline = DeviceRuntime.NowMs + ProtocolConstants.WifiScanTimeoutMs;
                    bool done = false;
                    while (!done && DeviceRuntime.NowMs < deadline)
                    {
                        if (cancel != null && cancel.IsSet)
                        {
                            return null;
                        }

                        done = _scanDone.WaitOne(PollIntervalMs, false);
                    }

                    if (!done)
                    {
                        Log.Warn("Wi-Fi: scan timed out");
                        return null;
                    }

                    return Merge(_adapter.NetworkReport);
                }
                catch (Exception ex)
                {
                    Log.Error("Wi-Fi: scan failed", ex);
                    return null;
                }
            }
        }

        public void Disconnect()
        {
            lock (_opLock)
            {
                try
                {
                    _adapter.Disconnect();
                }
                catch (Exception ex)
                {
                    Log.Error("Wi-Fi: disconnect failed", ex);
                }
            }
        }

        private void OnAvailableNetworksChanged(WifiAdapter sender, object e)
        {
            _scanDone.Set();
        }

        private static ScannedNetwork[] Merge(WifiNetworkReport report)
        {
            ArrayList list = new ArrayList();
            if (report == null || report.AvailableNetworks == null)
            {
                return new ScannedNetwork[0];
            }

            foreach (WifiAvailableNetwork net in report.AvailableNetworks)
            {
                string ssid = net.Ssid;
                if (ssid == null || ssid.Length == 0)
                {
                    continue;
                }

                int rssi = (int)net.NetworkRssiInDecibelMilliwatts;
                bool merged = false;
                for (int i = 0; i < list.Count; i++)
                {
                    ScannedNetwork existing = (ScannedNetwork)list[i];
                    if (existing.Ssid == ssid)
                    {
                        if (rssi > existing.Rssi)
                        {
                            existing.Rssi = rssi;
                        }

                        merged = true;
                        break;
                    }
                }

                if (!merged)
                {
                    ScannedNetwork entry = new ScannedNetwork();
                    entry.Ssid = ssid;
                    entry.Rssi = rssi;
                    list.Add(entry);
                }
            }

            ScannedNetwork[] result = new ScannedNetwork[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                result[i] = (ScannedNetwork)list[i];
            }

            // Insertion sort, strongest first (lists are short).
            for (int i = 1; i < result.Length; i++)
            {
                ScannedNetwork key = result[i];
                int j = i - 1;
                while (j >= 0 && result[j].Rssi < key.Rssi)
                {
                    result[j + 1] = result[j];
                    j--;
                }

                result[j + 1] = key;
            }

            return result;
        }

        /// <summary>
        /// Maps the native connection status. The ESP32 native layer reports AUTH_FAIL, AUTH_EXPIRE,
        /// (4-way) handshake timeout, ASSOC_FAIL and BEACON_TIMEOUT as InvalidCredential, and
        /// NO_AP_FOUND as NetworkNotAvailable.
        /// </summary>
        private static ErrorCode MapStatus(WifiConnectionStatus status, bool open)
        {
            switch (status)
            {
                case WifiConnectionStatus.Success:
                    return ErrorCode.None;
                case WifiConnectionStatus.InvalidCredential:
                    // An open network has no password to be wrong; the AP refused or vanished.
                    return open ? ErrorCode.Timeout : ErrorCode.WrongPassword;
                case WifiConnectionStatus.AccessRevoked:
                    return ErrorCode.WrongPassword;
                case WifiConnectionStatus.NetworkNotAvailable:
                    return ErrorCode.NetworkNotFound;
                case WifiConnectionStatus.UnsupportedAuthenticationProtocol:
                    return ErrorCode.UnsupportedSecurity;
                case WifiConnectionStatus.Timeout:
                    return ErrorCode.Timeout;
                default:
                    return ErrorCode.Timeout;
            }
        }

        private static string StatusName(WifiConnectionStatus status)
        {
            switch (status)
            {
                case WifiConnectionStatus.Success:
                    return "Success";
                case WifiConnectionStatus.InvalidCredential:
                    return "InvalidCredential";
                case WifiConnectionStatus.AccessRevoked:
                    return "AccessRevoked";
                case WifiConnectionStatus.NetworkNotAvailable:
                    return "NetworkNotAvailable";
                case WifiConnectionStatus.UnsupportedAuthenticationProtocol:
                    return "UnsupportedAuthenticationProtocol";
                case WifiConnectionStatus.Timeout:
                    return "Timeout";
                default:
                    return "UnspecifiedFailure";
            }
        }

        private static NetworkInterface StationInterface()
        {
            NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
            for (int i = 0; i < interfaces.Length; i++)
            {
                if (interfaces[i].NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                {
                    return interfaces[i];
                }
            }

            return null;
        }

        private static void EnsureDhcp()
        {
            NetworkInterface ni = StationInterface();
            if (ni != null && !ni.IsDhcpEnabled)
            {
                ni.EnableDhcp();
            }
        }
    }

    /// <summary>A thread-safe cancellation flag (CancellationToken is not used across this code base).</summary>
    public class CancelFlag
    {
        private readonly object _lock = new object();
        private bool _set;

        public bool IsSet
        {
            get
            {
                lock (_lock)
                {
                    return _set;
                }
            }
        }

        public void Set()
        {
            lock (_lock)
            {
                _set = true;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _set = false;
            }
        }
    }
}
