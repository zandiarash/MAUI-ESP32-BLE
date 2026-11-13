using System.Device.Gpio;
using IotStarter.Shared;

namespace IotStarter.Firmware.Hardware
{
    /// <summary>
    /// Drives the model's outputs. Every pin is opened and driven to its OFF level before anything
    /// else runs. All access is serialised, so concurrent HTTP requests cannot interleave.
    /// </summary>
    public class OutputController
    {
        private readonly object _lock = new object();
        private readonly ModelDefinition _model;
        private readonly GpioPin[] _pins;
        private readonly bool[] _states;

        public OutputController(GpioController gpio, ModelDefinition model)
        {
            _model = model;
            _pins = new GpioPin[model.Outputs.Length];
            _states = new bool[model.Outputs.Length];

            for (int i = 0; i < model.Outputs.Length; i++)
            {
                OutputDefinition output = model.Outputs[i];
                GpioPin pin = gpio.OpenPin(output.Gpio, PinMode.Output);
                pin.Write(LevelFor(output, false));
                _pins[i] = pin;
                _states[i] = false;
            }
        }

        public ModelDefinition Model
        {
            get { return _model; }
        }

        /// <summary>Sets an output. Returns false if the output ID is not defined for this model.</summary>
        public bool TrySet(int outputId, bool on)
        {
            lock (_lock)
            {
                int index = IndexOf(outputId);
                if (index < 0)
                {
                    return false;
                }

                _pins[index].Write(LevelFor(_model.Outputs[index], on));
                _states[index] = on;
                return true;
            }
        }

        /// <summary>Reads the logical state. Returns false if the output does not exist.</summary>
        public bool TryGet(int outputId, out bool on)
        {
            lock (_lock)
            {
                int index = IndexOf(outputId);
                if (index < 0)
                {
                    on = false;
                    return false;
                }

                on = _states[index];
                return true;
            }
        }

        /// <summary>Snapshot of all logical states, in catalogue order.</summary>
        public bool[] Snapshot()
        {
            lock (_lock)
            {
                bool[] copy = new bool[_states.Length];
                for (int i = 0; i < _states.Length; i++)
                {
                    copy[i] = _states[i];
                }

                return copy;
            }
        }

        /// <summary>Drives every output to its safe (off) level.</summary>
        public void AllOff()
        {
            lock (_lock)
            {
                for (int i = 0; i < _pins.Length; i++)
                {
                    _pins[i].Write(LevelFor(_model.Outputs[i], false));
                    _states[i] = false;
                }
            }
        }

        private int IndexOf(int outputId)
        {
            for (int i = 0; i < _model.Outputs.Length; i++)
            {
                if (_model.Outputs[i].Id == outputId)
                {
                    return i;
                }
            }

            return -1;
        }

        private static PinValue LevelFor(OutputDefinition output, bool on)
        {
            // Active-high: on = High. Active-low: on = Low.
            bool high = output.ActiveLow ? !on : on;
            return high ? PinValue.High : PinValue.Low;
        }
    }
}
