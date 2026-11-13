using System;
using System.Device.Gpio;
using System.Threading;
using IotStarter.Firmware.Diagnostics;
using IotStarter.Shared;

namespace IotStarter.Firmware.Hardware
{
    /// <summary>Raised when the reset button has been held for the factory-reset time.</summary>
    public delegate void FactoryResetRequestedHandler();

    /// <summary>
    /// Watches the BOOT button (GPIO 0, active-low) while the firmware runs. It is never sampled
    /// at power-on: holding GPIO 0 during reset puts the ESP32 into download mode.
    /// While held, the LED switches to a fast blink; after 5 s the factory reset is requested.
    /// </summary>
    public class ResetButton
    {
        private const int PollIntervalMs = 50;
        private const int FeedbackAfterMs = 1000;

        private readonly GpioPin _pin;
        private readonly StatusLed _led;
        private readonly FactoryResetRequestedHandler _onReset;

        public ResetButton(GpioController gpio, int pinNumber, StatusLed led, FactoryResetRequestedHandler onReset)
        {
            _led = led;
            _onReset = onReset;
            _pin = gpio.OpenPin(pinNumber, PinMode.InputPullUp);

            Thread thread = new Thread(Run);
            thread.Start();
        }

        private void Run()
        {
            try
            {
                // Ignore a button that is already held when the firmware starts (released after boot).
                while (_pin.Read() == PinValue.Low)
                {
                    Thread.Sleep(PollIntervalMs);
                }

                long pressedAt = -1;
                LedPattern patternBeforePress = LedPattern.Off;
                bool feedbackShown = false;

                while (true)
                {
                    bool pressed = _pin.Read() == PinValue.Low;
                    long now = DeviceRuntime.NowMs;

                    if (pressed)
                    {
                        if (pressedAt < 0)
                        {
                            pressedAt = now;
                        }

                        long held = now - pressedAt;
                        if (!feedbackShown && held >= FeedbackAfterMs)
                        {
                            patternBeforePress = _led.Pattern;
                            _led.Pattern = LedPattern.ResetPending;
                            feedbackShown = true;
                        }

                        if (held >= ProtocolConstants.FactoryResetHoldMs)
                        {
                            Log.Info("Reset button held for 5 s: factory reset");
                            _onReset();
                            return;
                        }
                    }
                    else if (pressedAt >= 0)
                    {
                        pressedAt = -1;
                        if (feedbackShown)
                        {
                            _led.Pattern = patternBeforePress;
                            feedbackShown = false;
                        }
                    }

                    Thread.Sleep(PollIntervalMs);
                }
            }
            catch (Exception ex)
            {
                DeviceRuntime.Fatal("ResetButton", ex);
            }
        }
    }
}
