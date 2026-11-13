using IotStarter.Shared;

namespace IotStarter.App.Core.Demo;

/// <summary>A simulated Wi-Fi network the demo devices can "see".</summary>
public sealed record DemoNetwork(string Ssid, int Rssi, string Password);

/// <summary>One simulated device: unprovisioned (advertising over BLE) or on the demo network.</summary>
public sealed class DemoDevice
{
    public DemoDevice(byte[] mac, ushort modelId, int outputCount)
    {
        Mac = mac;
        ModelId = modelId;
        Outputs = new bool[outputCount];
    }

    public byte[] Mac { get; }

    public string DeviceId => DeviceIdentity.FormatDeviceId(Mac);

    public string PeripheralId => "demo-" + DeviceId;

    public ushort ModelId { get; }

    public bool[] Outputs { get; }

    public bool Provisioned { get; set; }

    public string? TokenHex { get; set; }

    public string? Name { get; set; }

    public string? IPv4 { get; set; }

    /// <summary>False while "rebooting".</summary>
    public bool Online { get; set; } = true;
}

/// <summary>
/// The demo's shared world: the networks, the devices and their state. Lets the whole UI be reviewed
/// without hardware. Password for "Home-WiFi" is "demo1234"; "Office" uses "office-pass"; "Cafe Guest"
/// is open. Any SSID not in the list behaves like a network that is out of range.
/// </summary>
public sealed class DemoWorld
{
    public const string HomeSsid = "Home-WiFi";
    public const string HomePassword = "demo1234";

    /// <summary>A model ID that is not in the catalogue, to show how unknown models look.</summary>
    public const ushort UnknownModelId = 0x00FF;

    private readonly Lock _lock = new();
    private int _nextHost = 41;

    public DemoWorld()
    {
        Networks =
        [
            new DemoNetwork(HomeSsid, -48, HomePassword),
            new DemoNetwork("Office", -63, "office-pass"),
            new DemoNetwork("Cafe Guest", -77, string.Empty),
            new DemoNetwork("Neighbour", -84, "secret-neighbour")
        ];

        Devices =
        [
            new DemoDevice([0x24, 0x6F, 0x28, 0x1A, 0xE5, 0xF6], ModelCatalog.FourChannelSwitchId, 4),
            new DemoDevice([0x24, 0x6F, 0x28, 0x3C, 0x91, 0x0B], ModelCatalog.FourChannelSwitchId, 4),
            new DemoDevice([0x24, 0x6F, 0x28, 0x77, 0x42, 0xD8], UnknownModelId, 2)
        ];
    }

    public IReadOnlyList<DemoNetwork> Networks { get; }

    public IReadOnlyList<DemoDevice> Devices { get; }

    /// <summary>Multiplies every simulated delay (1 = realistic pacing; tests use a small value).</summary>
    public double TimeScale { get; set; } = 1.0;

    /// <summary>Simulated network latency so loading states are visible.</summary>
    public TimeSpan Latency => Scaled(250);

    /// <summary>A simulated delay of <paramref name="milliseconds"/>, scaled by <see cref="TimeScale"/>.</summary>
    public TimeSpan Scaled(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds * TimeScale);

    public DemoDevice? FindByPeripheral(string peripheralId) =>
        Devices.FirstOrDefault(d => d.PeripheralId == peripheralId);

    public DemoDevice? FindOnNetwork(string ipv4)
    {
        lock (_lock)
        {
            return Devices.FirstOrDefault(d => d.Provisioned && d.Online && d.IPv4 == ipv4);
        }
    }

    public IReadOnlyList<DemoDevice> OnlineDevices()
    {
        lock (_lock)
        {
            return [.. Devices.Where(d => d.Provisioned && d.Online)];
        }
    }

    /// <summary>A fresh address on 192.168.1.0/24, as a DHCP server would hand out after a restart.</summary>
    public string NextAddress()
    {
        lock (_lock)
        {
            return $"192.168.1.{_nextHost++}";
        }
    }

    public void Provision(DemoDevice device, string tokenHex)
    {
        lock (_lock)
        {
            device.Provisioned = true;
            device.TokenHex = tokenHex;
            device.IPv4 = NextAddress();
            device.Online = true;
        }
    }

    public void FactoryReset(DemoDevice device)
    {
        lock (_lock)
        {
            device.Provisioned = false;
            device.TokenHex = null;
            device.Name = null;
            device.IPv4 = null;
            Array.Clear(device.Outputs);
        }
    }

    /// <summary>Simulates a restart: offline for a moment, then back with a new address (to exercise rediscovery).</summary>
    public async Task RebootAsync(DemoDevice device, TimeProvider time)
    {
        lock (_lock)
        {
            device.Online = false;
            Array.Clear(device.Outputs);
        }

        await Task.Delay(Scaled(4000), time).ConfigureAwait(false);

        lock (_lock)
        {
            device.IPv4 = NextAddress();
            device.Online = true;
        }
    }
}
