using System;
using System.Device.Wifi;
using System.Threading;
using IotStarter.Firmware.Api;
using IotStarter.Firmware.Diagnostics;
using IotStarter.Firmware.Discovery;
using IotStarter.Firmware.Hardware;
using IotStarter.Firmware.Network;
using IotStarter.Firmware.Storage;
using IotStarter.Shared;

namespace IotStarter.Firmware.Normal
{
    /// <summary>
    /// Normal mode: BLE is never initialised. Keeps Wi-Fi connected with back-off, starts the HTTP
    /// API and discovery once online, and opens the recovery window after 5 minutes offline.
    /// A failed connection never erases the credentials.
    /// </summary>
    public class NormalModeController
    {
        private const int InitialBackoffMs = 2000;
        private const int MaxBackoffMs = 60000;

        // The ESP32 auto-connects from the stored configuration at boot; give it this long first.
        private const int AutoConnectGraceMs = 8000;
        private const int OnlineCheckMs = 2000;
        private const int OfflineTickMs = 500;

        private readonly SettingsStore _settings;
        private readonly WifiCredentialStore _credentials;
        private readonly WifiService _wifi;
        private readonly StatusLed _led;

        private HttpServer _httpServer;
        private UdpDiscoveryResponder _udp;
#if IOTSTARTER_MDNS
        private MdnsResponder _mdns;
#endif

        public NormalModeController(SettingsStore settings, WifiCredentialStore credentials, WifiService wifi, StatusLed led)
        {
            _settings = settings;
            _credentials = credentials;
            _wifi = wifi;
            _led = led;
        }

        /// <summary>
        /// Fallback strategy only: joins with BLE off. On failure, erases the unverified credentials and
        /// token, records the reason and restarts into provisioning. Returns only on success.
        /// </summary>
        public void VerifyPendingCredentials()
        {
            Log.Info("Verifying credentials received before the restart");
            _led.Pattern = LedPattern.Connecting;
            _wifi.SetHostName(DeviceContext.HostName);

            ErrorCode result = ErrorCode.Internal;
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                result = _wifi.Join(_credentials.Ssid, _credentials.Passphrase, WifiReconnectionKind.Automatic, null);
                if (result == ErrorCode.None)
                {
                    _settings.SetPendingVerification(false);
                    Log.Info("Credentials verified");
                    return;
                }

                Log.Warn("Verification attempt " + attempt.ToString() + " failed with error " + ((int)result).ToString());
            }

            _credentials.Erase();
            _settings.DiscardProvisioning(result);
            DeviceRuntime.Restart("unverified credentials failed");
        }

        /// <summary>Runs the connection supervisor forever.</summary>
        public void Run()
        {
            Log.Info("Normal mode");
            Log.Memory("normal mode start");
            _wifi.SetHostName(DeviceContext.HostName);
            _led.Pattern = LedPattern.Connecting;

            long now = DeviceRuntime.NowMs;
            long offlineSince = now;
            long nextAttemptAt = now + AutoConnectGraceMs;
            int backoffMs = InitialBackoffMs;
            bool online = false;

            while (true)
            {
                now = DeviceRuntime.NowMs;

                if (_wifi.HasIPv4)
                {
                    if (!online)
                    {
                        online = true;
                        backoffMs = InitialBackoffMs;
                        _led.Pattern = LedPattern.Online;
                        Log.Info("Online at " + ByteHelpers.FormatIPv4(_wifi.CurrentIPv4()));
                        StartServicesOnce();
                    }

                    offlineSince = now;
                    Thread.Sleep(OnlineCheckMs);
                    continue;
                }

                if (online)
                {
                    online = false;
                    offlineSince = now;
                    nextAttemptAt = now + AutoConnectGraceMs;
                    _led.Pattern = LedPattern.Connecting;
                    Log.Warn("Wi-Fi connection lost; the station reconnects automatically");
                }

                if (now - offlineSince >= ProtocolConstants.OfflineBeforeRecoveryWindowMs)
                {
                    Log.Warn("Offline for 5 minutes: opening the recovery window (credentials kept)");
                    _settings.SetRecoveryWindow(true);
                    DeviceRuntime.Restart("recovery window");
                }

                if (now >= nextAttemptAt)
                {
                    ErrorCode result = _wifi.Join(_credentials.Ssid, _credentials.Passphrase, WifiReconnectionKind.Automatic, null);
                    if (result != ErrorCode.None)
                    {
                        Log.Info("Reconnect failed (error " + ((int)result).ToString() + "), next try in " + (backoffMs / 1000).ToString() + " s");
                        nextAttemptAt = DeviceRuntime.NowMs + backoffMs;
                        backoffMs = backoffMs * 2 > MaxBackoffMs ? MaxBackoffMs : backoffMs * 2;
                    }
                }

                Thread.Sleep(OfflineTickMs);
            }
        }

        private void StartServicesOnce()
        {
            if (_httpServer != null)
            {
                return;
            }

            Log.Memory("before HTTP API start");
            _httpServer = new HttpServer(DeviceApi.Handle);
            _httpServer.Start(ProtocolConstants.HttpPort);
            Log.Memory("after HTTP API start");

            try
            {
                _udp = new UdpDiscoveryResponder();
                _udp.Start();
            }
            catch (Exception ex)
            {
                Log.Error("UDP discovery could not start", ex);
            }

#if IOTSTARTER_MDNS
            if (FirmwareConfig.EnableMdns)
            {
                try
                {
                    _mdns = new MdnsResponder(_wifi);
                    _mdns.Start();
                    Log.Memory("after mDNS start");
                }
                catch (Exception ex)
                {
                    Log.Error("mDNS could not start; UDP discovery remains", ex);
                    _mdns = null;
                }
            }
#endif
        }
    }
}
