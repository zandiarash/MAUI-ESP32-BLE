using System.Device.Gpio;
using IotStarter.Firmware.Diagnostics;
using IotStarter.Shared;

namespace IotStarter.Firmware.Hardware
{
    /// <summary>Raised when the reset button has been held for the factory-reset time.</summary>
    public delegate void FactoryResetRequestedHandler();

    /// <summary>
    /// Watches the BOOT button (GPIO 0, active-low) while the firmware runs. It is never sampled
    /// at power-on: holding GPIO 0 during reset puts the ESP32 into download mode.
    /// It has no thread of its own (threads cost managed heap): <see cref="StatusLed"/> calls
    /// <see cref="Poll"/> every 50 ms. While held, the LED fast-blinks; after 5 s the factory reset runs.
    /// </summary>
    public class ResetButton
    {
        private const int FeedbackAfterMs = 1000;

        private readonly GpioPin _pin;
        private readonly StatusLed _led;
        private readonly FactoryResetRequestedHandler _onReset;
        private bool _armed;
        private bool _fired;
        private long _pressedAt = -1;
        private bool _feedbackShown;
        private LedPattern _patternBeforePress;

        public ResetButton(GpioController gpio, int pinNumber, StatusLed led, FactoryResetRequestedHandler onReset)
        {
            _led = led;
            _onReset = onReset;
            _pin = gpio.OpenPin(pinNumber, PinMode.InputPullUp);
            led.AttachButton(this);
        }

        /// <summary>Samples the button once. Called from the LED thread.</summary>
        public void Poll()
        {
            if (_fired)
            {
                return;
            }

            bool pressed = _pin.Read() == PinValue.Low;

            // Ignore a button that is already held when the firmware starts, until it is released.
            if (!_armed)
            {
                _armed = !pressed;
                return;
            }

            long now = DeviceRuntime.NowMs;
            if (pressed)
            {
                if (_pressedAt < 0)
                {
                    _pressedAt = now;
                }

                long held = now - _pressedAt;
                if (!_feedbackShown && held >= FeedbackAfterMs)
                {
                    _patternBeforePress = _led.Pattern;
                    _led.Pattern = LedPattern.ResetPending;
                    _feedbackShown = true;
                }

                if (held >= ProtocolConstants.FactoryResetHoldMs)
                {
                    _fired = true;
                    Log.Info("Reset button held for 5 s: factory reset");
                    _onReset();
                }
            }
            else if (_pressedAt >= 0)
            {
                _pressedAt = -1;
                if (_feedbackShown)
                {
                    _led.Pattern = _patternBeforePress;
                    _feedbackShown = false;
                }
            }
        }
    }
}
