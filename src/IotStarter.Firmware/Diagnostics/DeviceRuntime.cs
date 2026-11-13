using System;
using System.Threading;
using IotStarter.Firmware.Hardware;
using nanoFramework.Hardware.Esp32;
using nanoFramework.Runtime.Native;

namespace IotStarter.Firmware.Diagnostics
{
    /// <summary>Work to do just before a restart (for example, releasing the BLE stack).</summary>
    public delegate void RestartHook();

    /// <summary>Restart and fatal-error handling.</summary>
    public static class DeviceRuntime
    {
        private static readonly object RestartLock = new object();
        private static OutputController _outputs;
        private static RestartHook _beforeRestart;
        private static bool _restarting;

        /// <summary>Sets the hook run before any restart (one hook; the active mode owns it).</summary>
        public static void SetBeforeRestart(RestartHook hook)
        {
            _beforeRestart = hook;
        }

        /// <summary>Registers the outputs so every restart path drives them off first.</summary>
        public static void Register(OutputController outputs)
        {
            _outputs = outputs;
        }

        /// <summary>Milliseconds since boot (monotonic).</summary>
        public static long NowMs
        {
            get { return Environment.TickCount64; }
        }

        /// <summary>
        /// Restarts the device cleanly. Callers that own the BLE stack shut it down first.
        /// Uses a software reboot; <see cref="FirmwareConfig.RestartViaDeepSleep"/> switches to a 1 s
        /// deep-sleep timer wake-up if testing shows the reboot leaves the radio in a bad state.
        /// </summary>
        public static void Restart(string reason)
        {
            lock (RestartLock)
            {
                if (_restarting)
                {
                    return;
                }

                _restarting = true;
            }

            Log.Info("Restarting: " + reason);
            try
            {
                if (_beforeRestart != null)
                {
                    _beforeRestart();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Pre-restart hook failed", ex);
            }

            try
            {
                if (_outputs != null)
                {
                    _outputs.AllOff();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Outputs off before restart failed", ex);
            }

            // Let the debug output drain.
            Thread.Sleep(100);

            if (FirmwareConfig.RestartViaDeepSleep)
            {
                Sleep.EnableWakeupByTimer(TimeSpan.FromSeconds(1));
                Sleep.StartDeepSleep();
            }

            Power.RebootDevice();
        }

        /// <summary>Logs an unrecoverable error from a worker thread and restarts.</summary>
        public static void Fatal(string where, Exception ex)
        {
            Log.Error("Unrecoverable error in " + where, ex);
            Restart("fatal error in " + where);
        }
    }
}
