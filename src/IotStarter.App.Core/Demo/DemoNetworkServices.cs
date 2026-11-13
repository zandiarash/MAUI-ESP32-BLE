using IotStarter.App.Core.Network;
using IotStarter.App.Core.Platform;
using IotStarter.Shared;

namespace IotStarter.App.Core.Demo;

/// <summary>The demo devices' HTTP API, with the same status semantics as the firmware.</summary>
public sealed class DemoDeviceApiClient(DemoWorld world, TimeProvider time) : IDeviceApiClient
{
    public async Task<DeviceInfoResponse> GetInfoAsync(DeviceEndpoint endpoint, TimeSpan timeout, CancellationToken cancellationToken)
    {
        DemoDevice device = await ReachAsync(endpoint, cancellationToken).ConfigureAwait(false);
        return new DeviceInfoResponse(device.DeviceId, device.ModelId, device.Name ?? ModelCatalog.Find(device.ModelId)?.Name, "1.0.0", ProtocolConstants.ApiVersion);
    }

    public async Task<IReadOnlyList<OutputResponse>> GetOutputsAsync(DeviceEndpoint endpoint, string token, CancellationToken cancellationToken)
    {
        DemoDevice device = await AuthorizeAsync(endpoint, token, cancellationToken).ConfigureAwait(false);
        return [.. device.Outputs.Select((on, i) => new OutputResponse(i + 1, Label(device, i + 1), on))];
    }

    public async Task<OutputResponse> SetOutputAsync(DeviceEndpoint endpoint, string token, int outputId, bool on, CancellationToken cancellationToken)
    {
        DemoDevice device = await AuthorizeAsync(endpoint, token, cancellationToken).ConfigureAwait(false);
        if (outputId < 1 || outputId > device.Outputs.Length)
        {
            throw new DeviceApiException(DeviceApiError.NotFound, "unknown output");
        }

        device.Outputs[outputId - 1] = on;
        return new OutputResponse(outputId, Label(device, outputId), on);
    }

    public async Task<string> SetNameAsync(DeviceEndpoint endpoint, string token, string name, CancellationToken cancellationToken)
    {
        DemoDevice device = await AuthorizeAsync(endpoint, token, cancellationToken).ConfigureAwait(false);
        string trimmed = name.Trim();
        if (trimmed.Length == 0 || System.Text.Encoding.UTF8.GetByteCount(trimmed) > HttpContract.MaxDeviceNameBytes)
        {
            throw new DeviceApiException(DeviceApiError.BadRequest, "invalid name");
        }

        device.Name = trimmed;
        return trimmed;
    }

    public async Task IdentifyAsync(DeviceEndpoint endpoint, string token, CancellationToken cancellationToken) =>
        await AuthorizeAsync(endpoint, token, cancellationToken).ConfigureAwait(false);

    public async Task RebootAsync(DeviceEndpoint endpoint, string token, CancellationToken cancellationToken)
    {
        DemoDevice device = await AuthorizeAsync(endpoint, token, cancellationToken).ConfigureAwait(false);
        _ = world.RebootAsync(device, time);
    }

    public async Task FactoryResetAsync(DeviceEndpoint endpoint, string token, CancellationToken cancellationToken)
    {
        DemoDevice device = await AuthorizeAsync(endpoint, token, cancellationToken).ConfigureAwait(false);
        world.FactoryReset(device);
    }

    private static string Label(DemoDevice device, int id) =>
        ModelCatalog.Find(device.ModelId)?.FindOutput(id)?.Label ?? $"Output {id}";

    private async Task<DemoDevice> ReachAsync(DeviceEndpoint endpoint, CancellationToken cancellationToken)
    {
        await Task.Delay(world.Latency, time, cancellationToken).ConfigureAwait(false);
        return world.FindOnNetwork(endpoint.IPv4)
            ?? throw new DeviceApiException(DeviceApiError.Unreachable, "The device could not be reached.");
    }

    private async Task<DemoDevice> AuthorizeAsync(DeviceEndpoint endpoint, string token, CancellationToken cancellationToken)
    {
        DemoDevice device = await ReachAsync(endpoint, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(device.TokenHex, token, StringComparison.Ordinal))
        {
            throw new DeviceApiException(DeviceApiError.Unauthorized, "The device did not accept this phone's key.");
        }

        return device;
    }
}

/// <summary>Demo mDNS: answers for every online demo device.</summary>
public sealed class DemoMdnsBrowser(DemoWorld world, TimeProvider time) : IMdnsBrowser
{
    public async Task<IReadOnlyList<DiscoveryHit>> BrowseAsync(TimeSpan window, CancellationToken cancellationToken)
    {
        await Task.Delay(world.Scaled(600), time, cancellationToken).ConfigureAwait(false);
        return [.. world.OnlineDevices().Select(d => new DiscoveryHit(d.DeviceId, new DeviceEndpoint(d.IPv4!), d.ModelId, DiscoverySource.Mdns))];
    }
}

/// <summary>Demo UDP probe: answers for every online demo device.</summary>
public sealed class DemoUdpProber(DemoWorld world, TimeProvider time) : IUdpProber
{
    public async Task<IReadOnlyList<DiscoveryHit>> ProbeAsync(TimeSpan window, CancellationToken cancellationToken)
    {
        await Task.Delay(world.Scaled(400), time, cancellationToken).ConfigureAwait(false);
        return [.. world.OnlineDevices().Select(d => new DiscoveryHit(d.DeviceId, new DeviceEndpoint(d.IPv4!), d.ModelId, DiscoverySource.UdpProbe))];
    }
}

/// <summary>Demo mode needs no permissions.</summary>
public sealed class DemoPermissionService : IPermissionService
{
    public Task<PermissionState> CheckBluetoothAsync() => Task.FromResult(PermissionState.NotRequired);

    public Task<PermissionState> RequestBluetoothAsync() => Task.FromResult(PermissionState.NotRequired);

    public void OpenAppSettings()
    {
    }

    public void OpenBluetoothSettings()
    {
    }
}
