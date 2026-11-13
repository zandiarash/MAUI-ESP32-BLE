using System;
using System.Device.Gpio;
using System.Threading;
using IotStarter.Firmware.Diagnostics;

namespace IotStarter.Firmware.Hardware
{
    /// <summary>Status LED patterns.</summary>
    public enum LedPattern
    {
        Off = 0,

        /// <summary>Two short blinks every 1.5 s: waiting for provisioning over BLE.</summary>
        Provisioning = 1,

        /// <summary>Three short blinks every 1.5 s: recovery window (credentials kept).</summary>
        RecoveryWindow = 2,

        /// <summary>Slow blink: joining Wi-Fi.</summary>
        Connecting = 3,

        /// <summary>Solid on: online.</summary>
        Online = 4,

        /// <summary>Fast blink: reset button held, keep holding.</summary>
        ResetPending = 5
    }

    /// <summary>
    /// Runs LED patterns on a background thread. <see cref="Identify"/> overlays a 3-second fast
    /// blink on top of the current pattern.
    /// </summary>
    public class StatusLed
    {
        private const int IdentifyDurationMs = 3000;

        // Durations in ms, alternating on, off, on, off...
        private static readonly int[] ProvisioningSteps = new int[] { 100, 150, 100, 1150 };
        private static readonly int[] RecoverySteps = new int[] { 100, 150, 100, 150, 100, 900 };
        private static readonly int[] ConnectingSteps = new int[] { 500, 500 };
        private static readonly int[] OnlineSteps = new int[] { 1000 };
        private static readonly int[] OffSteps = new int[] { 0, 1000 };
        private static readonly int[] FastSteps = new int[] { 100, 100 };

        private readonly GpioPin _pin;
        private readonly bool _activeLow;
        private readonly AutoResetEvent _changed = new AutoResetEvent(false);
        private readonly object _lock = new object();
        private LedPattern _pattern = LedPattern.Off;
        private long _identifyUntilMs;

        public StatusLed(GpioController gpio, int pinNumber, bool activeLow)
        {
            _activeLow = activeLow;
            _pin = gpio.OpenPin(pinNumber, PinMode.Output);
            SetLevel(false);

            Thread thread = new Thread(Run);
            thread.Start();
        }

        public LedPattern Pattern
        {
            get
            {
                lock (_lock)
                {
                    return _pattern;
                }
            }

            set
            {
                lock (_lock)
                {
                    _pattern = value;
                }

                _changed.Set();
            }
        }

        /// <summary>Blinks fast for 3 seconds so the user can find the device.</summary>
        public void Identify()
        {
            lock (_lock)
            {
                _identifyUntilMs = DeviceRuntime.NowMs + IdentifyDurationMs;
            }

            _changed.Set();
        }

        private void Run()
        {
            try
            {
                while (true)
                {
                    int[] steps = CurrentSteps();
                    bool interrupted = false;
                    for (int i = 0; i < steps.Length && !interrupted; i++)
                    {
                        bool on = (i % 2) == 0;
                        SetLevel(on && steps[i] > 0);
                        if (steps[i] > 0)
                        {
                            interrupted = _changed.WaitOne(steps[i], false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DeviceRuntime.Fatal("StatusLed", ex);
            }
        }

        private int[] CurrentSteps()
        {
            lock (_lock)
            {
                if (DeviceRuntime.NowMs < _identifyUntilMs)
                {
                    return FastSteps;
                }

                switch (_pattern)
                {
                    case LedPattern.Provisioning:
                        return ProvisioningSteps;
                    case LedPattern.RecoveryWindow:
                        return RecoverySteps;
                    case LedPattern.Connecting:
                        return ConnectingSteps;
                    case LedPattern.Online:
                        return OnlineSteps;
                    case LedPattern.ResetPending:
                        return FastSteps;
                    default:
                        return OffSteps;
                }
            }
        }

        private void SetLevel(bool on)
        {
            bool high = _activeLow ? !on : on;
            _pin.Write(high ? PinValue.High : PinValue.Low);
        }
    }
}
