using IotStarter.App.Core.Devices;
using IotStarter.App.Core.Network;
using IotStarter.App.Core.Platform;
using IotStarter.Shared;

namespace IotStarter.App.Core.State;

/// <summary>Whether the app can currently talk to a device.</summary>
public enum DeviceConnectivity
{
    /// <summary>Not checked yet in this session.</summary>
    Unknown,
    Checking,
    Online,

    /// <summary>Not found on this network.</summary>
    Offline,

    /// <summary>Found, but it refuses this phone's token (it was reset or provisioned by another phone).</summary>
    Unauthorized
}

/// <summary>One output as shown in the UI.</summary>
/// <param name="Pending">A change was sent and not yet confirmed (optimistic update).</param>
public sealed record OutputView(int Id, string Label, bool On, bool Pending);

/// <summary>Everything the UI shows about one device.</summary>
public sealed record DeviceView(
    DeviceRecord Record,
    DeviceConnectivity Connectivity,
    IReadOnlyList<OutputView> Outputs,
    string? Firmware,
    DiscoverySource? FoundVia)
{
    public string Id => Record.Id;

    /// <summary>Catalogue entry; null for models this app does not know (controls still come from the device).</summary>
    public ModelDefinition? Model => ModelCatalog.Find(Record.ModelId);

    public static DeviceView Initial(DeviceRecord record) =>
        new(record, DeviceConnectivity.Unknown, DefaultOutputs(record.ModelId), Firmware: null, FoundVia: null);

    /// <summary>Catalogue outputs (all off) so the card can render before the first refresh.</summary>
    private static IReadOnlyList<OutputView> DefaultOutputs(ushort modelId) =>
        ModelCatalog.Find(modelId) is { } model
            ? [.. model.Outputs.Select(o => new OutputView(o.Id, o.Label, On: false, Pending: false))]
            : [];
}

/// <summary>Kinds of transient messages the UI shows as toasts.</summary>
public enum NoticeKind
{
    SwitchFailed,
    DeviceUnreachable,
    DeviceUnauthorized,
    ActionFailed,
    NotOnLocalNetwork
}

public sealed record AppNotice(NoticeKind Kind, string? DeviceId);

/// <summary>
/// The single observable state the UI subscribes to. Snapshots are immutable; every change replaces
/// them and raises <see cref="Changed"/> (possibly on a background thread: components marshal with
/// InvokeAsync).
/// </summary>
public sealed class AppState
{
    private readonly Lock _lock = new();
    private IReadOnlyList<DeviceView> _devices = [];
    private bool _isLoaded;
    private bool _isRefreshing;
    private LocalNetworkState _network = LocalNetworkState.LocalNetwork;

    public event Action? Changed;

    public event Action<AppNotice>? Notice;

    public IReadOnlyList<DeviceView> Devices
    {
        get
        {
            lock (_lock)
            {
                return _devices;
            }
        }
    }

    /// <summary>False until the stored device list has been read (the UI shows skeletons until then).</summary>
    public bool IsLoaded
    {
        get
        {
            lock (_lock)
            {
                return _isLoaded;
            }
        }
    }

    public bool IsRefreshing
    {
        get
        {
            lock (_lock)
            {
                return _isRefreshing;
            }
        }
    }

    public LocalNetworkState Network
    {
        get
        {
            lock (_lock)
            {
                return _network;
            }
        }
    }

    public DeviceView? Find(string deviceId) =>
        Devices.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase));

    internal void SetDevices(IReadOnlyList<DeviceView> devices, bool loaded = true)
    {
        lock (_lock)
        {
            _devices = devices;
            _isLoaded = loaded;
        }

        Changed?.Invoke();
    }

    internal void UpdateDevice(string deviceId, Func<DeviceView, DeviceView> update)
    {
        bool changed = false;
        lock (_lock)
        {
            _devices = [.. _devices.Select(d =>
            {
                if (!string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    return d;
                }

                changed = true;
                return update(d);
            })];
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    internal void AddOrReplace(DeviceView device)
    {
        lock (_lock)
        {
            _devices = [.. _devices.Where(d => !string.Equals(d.Id, device.Id, StringComparison.OrdinalIgnoreCase)), device];
        }

        Changed?.Invoke();
    }

    internal void Remove(string deviceId)
    {
        lock (_lock)
        {
            _devices = [.. _devices.Where(d => !string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase))];
        }

        Changed?.Invoke();
    }

    internal void SetRefreshing(bool refreshing)
    {
        lock (_lock)
        {
            _isRefreshing = refreshing;
        }

        Changed?.Invoke();
    }

    internal void SetNetwork(LocalNetworkState network)
    {
        lock (_lock)
        {
            _network = network;
        }

        Changed?.Invoke();
    }

    internal void Publish(AppNotice notice) => Notice?.Invoke(notice);
}
