using System;
using System.Threading;
using IotStarter.Firmware.Diagnostics;
using IotStarter.Firmware.Hardware;
using IotStarter.Firmware.Storage;
using IotStarter.Shared;

namespace IotStarter.Firmware.Normal
{
    /// <summary>
    /// Services shared by the HTTP controllers and the discovery responders. nanoFramework.WebServer
    /// creates controllers itself and has no dependency injection here, so they read from this
    /// holder, which <see cref="Program"/> fills once at start-up.
    /// </summary>
    public static class DeviceContext
    {
        private static Timer _pendingAction;

        public static OutputController Outputs { get; private set; }

        public static SettingsStore Settings { get; private set; }

        public static WifiCredentialStore Credentials { get; private set; }

        public static StatusLed Led { get; private set; }

        public static ModelDefinition Model { get; private set; }

        /// <summary>12 uppercase hex digits.</summary>
        public static string DeviceId { get; private set; }

        /// <summary>DHCP / mDNS host label, e.g. "iot-a1b2c3d4e5f6".</summary>
        public static string HostName { get; private set; }

        public static void Initialize(
            OutputController outputs,
            SettingsStore settings,
            WifiCredentialStore credentials,
            StatusLed led,
            byte[] mac)
        {
            Outputs = outputs;
            Settings = settings;
            Credentials = credentials;
            Led = led;
            Model = outputs.Model;
            DeviceId = DeviceIdentity.FormatDeviceId(mac);
            HostName = DeviceIdentity.HostName(ProtocolConstants.BleNamePrefix, mac);
        }

        /// <summary>Name shown to users: the stored name, else the model name.</summary>
        public static string DisplayName
        {
            get
            {
                string name = Settings.Name;
                return name != null && name.Length > 0 ? name : Model.Name;
            }
        }

        /// <summary>
        /// Factory reset: outputs off, Wi-Fi credentials, API token and settings erased, then restart
        /// into provisioning mode. Used by the reset button and the HTTP API.
        /// </summary>
        public static void FactoryReset()
        {
            Log.Info("Factory reset");
            try
            {
                Outputs.AllOff();
                Credentials.Erase();
                Settings.Erase();
            }
            catch (Exception ex)
            {
                Log.Error("Factory reset: erase failed", ex);
            }

            DeviceRuntime.Restart("factory reset");
        }

        /// <summary>Runs a reboot shortly after the HTTP response has been sent.</summary>
        public static void ScheduleRestart()
        {
            Schedule(RestartCallback);
        }

        /// <summary>Runs a factory reset shortly after the HTTP response has been sent.</summary>
        public static void ScheduleFactoryReset()
        {
            Schedule(FactoryResetCallback);
        }

        private static void Schedule(TimerCallback callback)
        {
            // Keep a reference so the timer is not collected before it fires.
            _pendingAction = new Timer(callback, null, 500, Timeout.Infinite);
        }

        private static void RestartCallback(object state)
        {
            DeviceRuntime.Restart("requested over HTTP");
        }

        private static void FactoryResetCallback(object state)
        {
            FactoryReset();
        }
    }
}
