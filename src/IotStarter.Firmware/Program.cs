using System;
using System.Device.Gpio;
using System.Threading;
using IotStarter.Firmware.Diagnostics;
using IotStarter.Firmware.Hardware;
using IotStarter.Firmware.Network;
using IotStarter.Firmware.Normal;
using IotStarter.Firmware.Provisioning;
using IotStarter.Firmware.Storage;
using IotStarter.Shared;

namespace IotStarter.Firmware
{
    /// <summary>
    /// Start-up: outputs safe, settings loaded, then provisioning mode (no credentials or recovery
    /// window) or normal mode (BLE never initialised).
    /// </summary>
    public class Program
    {
        public static void Main()
        {
            // Heap numbers first, before anything else allocates. The GC messages come from
            // nanoFramework itself and need no managed memory to print.
            if (FirmwareConfig.LogGcMessages)
            {
                nanoFramework.Runtime.Native.GC.EnableGCMessages(true);
            }

            Log.Memory("start of Main");

            try
            {
                Run();
            }
            catch (Exception ex)
            {
                DeviceRuntime.Fatal("Main", ex);
            }

            Thread.Sleep(Timeout.Infinite);
        }

        private static void Run()
        {
            Log.Info("IotStarter firmware " + FirmwareConfig.FirmwareVersionString
                + ", model " + ModelCatalog.FormatModelId(FirmwareConfig.ModelId));

            // 1. Every output to its safe (off) state before anything else.
            GpioController gpio = new GpioController();
            ModelDefinition model = ModelCatalog.Find(FirmwareConfig.ModelId);
            if (model == null)
            {
                throw new InvalidOperationException("Model is missing from the catalogue");
            }

            OutputController outputs = new OutputController(gpio, model);
            DeviceRuntime.Register(outputs);

            StatusLed led = new StatusLed(gpio, FirmwareConfig.StatusLedGpio, FirmwareConfig.StatusLedActiveLow);
            Log.Memory("boot");

            SettingsStore settings = new SettingsStore();
            settings.Load();
            WifiCredentialStore credentials = new WifiCredentialStore();
            WifiService wifi = new WifiService();
            byte[] mac = WifiService.GetStationMac();
            Log.Info("Device ID " + DeviceIdentity.FormatDeviceId(mac));

            DeviceContext.Initialize(outputs, settings, credentials, led, mac);

            // The button is watched only while the firmware runs (never sampled at power-on).
            new ResetButton(gpio, FirmwareConfig.ResetButtonGpio, led, DeviceContext.FactoryReset);

            // 2. Choose the mode.
            bool hasCredentials = credentials.HasCredentials;
            if (hasCredentials && !settings.HasValidToken)
            {
                // Credentials without a token cannot be controlled by any app; start over.
                Log.Warn("Wi-Fi credentials found without an API token: erasing them");
                credentials.Erase();
                hasCredentials = false;
            }

            if (!hasCredentials)
            {
                if (settings.TokenHex != null || settings.PendingVerification || settings.RecoveryWindow)
                {
                    // Keep only the reason of the last failure, for the app to read.
                    settings.DiscardProvisioning(settings.LastError);
                }

                new ProvisioningController(ProvisioningKind.Open, settings, credentials, wifi, led, mac).Run();
                return;
            }

            if (settings.RecoveryWindow)
            {
                new ProvisioningController(ProvisioningKind.Recovery, settings, credentials, wifi, led, mac).Run();
                return;
            }

            NormalModeController normal = new NormalModeController(settings, credentials, wifi, led);
            if (settings.PendingVerification)
            {
                normal.VerifyPendingCredentials();
            }

            normal.Run();
        }
    }
}
