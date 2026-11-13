// Shared between firmware and app. Conservative C# subset only.
#nullable disable

namespace IotStarter.Shared
{
    /// <summary>One switchable output of a product model.</summary>
    public class OutputDefinition
    {
        public OutputDefinition(byte id, string label, int gpio, bool activeLow)
        {
            Id = id;
            Label = label;
            Gpio = gpio;
            ActiveLow = activeLow;
        }

        /// <summary>Output ID used in the HTTP API (1-based, stable per model).</summary>
        public byte Id { get; private set; }

        /// <summary>Default English label; the device reports it, the app may localise it.</summary>
        public string Label { get; private set; }

        /// <summary>ESP32 GPIO number driving the output.</summary>
        public int Gpio { get; private set; }

        /// <summary>True if the load is on when the pin is low.</summary>
        public bool ActiveLow { get; private set; }
    }

    /// <summary>A product model in the catalogue.</summary>
    public class ModelDefinition
    {
        public ModelDefinition(ushort id, string name, string iconKey, OutputDefinition[] outputs)
        {
            Id = id;
            Name = name;
            IconKey = iconKey;
            Outputs = outputs;
        }

        public ushort Id { get; private set; }

        /// <summary>Default English model name.</summary>
        public string Name { get; private set; }

        /// <summary>Key the app maps to an icon.</summary>
        public string IconKey { get; private set; }

        public OutputDefinition[] Outputs { get; private set; }

        public OutputDefinition FindOutput(int outputId)
        {
            for (int i = 0; i < Outputs.Length; i++)
            {
                if (Outputs[i].Id == outputId)
                {
                    return Outputs[i];
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Product catalogue. Pins checked against the ESP32-WROOM-32 datasheet: none is a strapping pin
    /// (0, 2, 5, 12, 15), a flash pin (6-11) or an input-only pin (34-39).
    /// </summary>
    public static class ModelCatalog
    {
        public const ushort FourChannelSwitchId = 0x0001;

        // Board-level pins (not outputs a user can switch).
        public const int StatusLedGpio = 2;
        public const int ResetButtonGpio = 0;

        private static ModelDefinition[] _models;

        public static ModelDefinition[] All
        {
            get
            {
                if (_models == null)
                {
                    _models = new ModelDefinition[]
                    {
                        new ModelDefinition(
                            FourChannelSwitchId,
                            "4-channel switch",
                            "switch4",
                            new OutputDefinition[]
                            {
                                new OutputDefinition(1, "Channel 1", 25, false),
                                new OutputDefinition(2, "Channel 2", 26, false),
                                new OutputDefinition(3, "Channel 3", 27, false),
                                new OutputDefinition(4, "Channel 4", 33, false)
                            })
                    };
                }

                return _models;
            }
        }

        /// <summary>Returns the model or null if it is not in this catalogue.</summary>
        public static ModelDefinition Find(ushort modelId)
        {
            ModelDefinition[] models = All;
            for (int i = 0; i < models.Length; i++)
            {
                if (models[i].Id == modelId)
                {
                    return models[i];
                }
            }

            return null;
        }

        /// <summary>Formats a model ID as "0x0001".</summary>
        public static string FormatModelId(ushort modelId)
        {
            byte[] bytes = new byte[] { (byte)(modelId >> 8), (byte)(modelId & 0xFF) };
            return "0x" + ByteHelpers.ToHexUpper(bytes, 0, 2);
        }
    }
}
