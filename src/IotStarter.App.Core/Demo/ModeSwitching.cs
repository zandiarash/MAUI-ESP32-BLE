using IotStarter.App.Core.Ble;
using IotStarter.App.Core.Network;
using IotStarter.App.Core.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace IotStarter.App.Core.Demo;

/// <summary>DI keys for the two implementations behind each switchable service.</summary>
public static class ModeKeys
{
    public const string Real = "real";
    public const string Demo = "demo";
}

/// <summary>Routes BLE calls to the real or the simulated transport, following Settings › Demo mode.</summary>
public sealed class SwitchingBleTransport(
    [FromKeyedServices(ModeKeys.Real)] IBleTransport real,
    [FromKeyedServices(ModeKeys.Demo)] IBleTransport demo,
    IAppSettings settings) : IBleTransport
{
    private IBleTransport Current => settings.DemoMode ? demo : real;

    public BleAvailability Availability => Current.Availability;

    public event EventHandler? AvailabilityChanged
    {
        add
        {
            real.AvailabilityChanged += value;
            demo.AvailabilityChanged += value;
        }

        remove
        {
            real.AvailabilityChanged -= value;
            demo.AvailabilityChanged -= value;
        }
    }

    public Task ScanAsync(Guid serviceUuid, Action<BleAdvertisement> onAdvertisement, CancellationToken cancellationToken) =>
        Current.ScanAsync(serviceUuid, onAdvertisement, cancellationToken);

    public Task<IBleConnection> ConnectAsync(string peripheralId, CancellationToken cancellationToken) =>
        Current.ConnectAsync(peripheralId, cancellationToken);
}

public sealed class SwitchingDeviceApiClient(
    [FromKeyedServices(ModeKeys.Real)] IDeviceApiClient real,
    [FromKeyedServices(ModeKeys.Demo)] IDeviceApiClient demo,
    IAppSettings settings) : IDeviceApiClient
{
    private IDeviceApiClient Current => settings.DemoMode ? demo : real;

    public Task<DeviceInfoResponse> GetInfoAsync(DeviceEndpoint endpoint, TimeSpan timeout, CancellationToken cancellationToken) =>
        Current.GetInfoAsync(endpoint, timeout, cancellationToken);

    public Task<IReadOnlyList<OutputResponse>> GetOutputsAsync(DeviceEndpoint endpoint, string token, CancellationToken cancellationToken) =>
        Current.GetOutputsAsync(endpoint, token, cancellationToken);

    public Task<OutputResponse> SetOutputAsync(DeviceEndpoint endpoint, string token, int outputId, bool on, CancellationToken cancellationToken) =>
        Current.SetOutputAsync(endpoint, token, outputId, on, cancellationToken);

    public Task<string> SetNameAsync(DeviceEndpoint endpoint, string token, string name, CancellationToken cancellationToken) =>
        Current.SetNameAsync(endpoint, token, name, cancellationToken);

    public Task IdentifyAsync(DeviceEndpoint endpoint, string token, CancellationToken cancellationToken) =>
        Current.IdentifyAsync(endpoint, token, cancellationToken);

    public Task RebootAsync(DeviceEndpoint endpoint, string token, CancellationToken cancellationToken) =>
        Current.RebootAsync(endpoint, token, cancellationToken);

    public Task FactoryResetAsync(DeviceEndpoint endpoint, string token, CancellationToken cancellationToken) =>
        Current.FactoryResetAsync(endpoint, token, cancellationToken);
}

public sealed class SwitchingMdnsBrowser(
    [FromKeyedServices(ModeKeys.Real)] IMdnsBrowser real,
    [FromKeyedServices(ModeKeys.Demo)] IMdnsBrowser demo,
    IAppSettings settings) : IMdnsBrowser
{
    public Task<IReadOnlyList<DiscoveryHit>> BrowseAsync(TimeSpan window, CancellationToken cancellationToken) =>
        (settings.DemoMode ? demo : real).BrowseAsync(window, cancellationToken);
}

public sealed class SwitchingUdpProber(
    [FromKeyedServices(ModeKeys.Real)] IUdpProber real,
    [FromKeyedServices(ModeKeys.Demo)] IUdpProber demo,
    IAppSettings settings) : IUdpProber
{
    public Task<IReadOnlyList<DiscoveryHit>> ProbeAsync(TimeSpan window, CancellationToken cancellationToken) =>
        (settings.DemoMode ? demo : real).ProbeAsync(window, cancellationToken);
}

public sealed class SwitchingPermissionService(
    [FromKeyedServices(ModeKeys.Real)] IPermissionService real,
    [FromKeyedServices(ModeKeys.Demo)] IPermissionService demo,
    IAppSettings settings) : IPermissionService
{
    private IPermissionService Current => settings.DemoMode ? demo : real;

    public Task<PermissionState> CheckBluetoothAsync() => Current.CheckBluetoothAsync();

    public Task<PermissionState> RequestBluetoothAsync() => Current.RequestBluetoothAsync();

    public void OpenAppSettings() => Current.OpenAppSettings();

    public void OpenBluetoothSettings() => Current.OpenBluetoothSettings();
}
