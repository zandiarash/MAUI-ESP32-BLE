using IotStarter.App.Core.Devices;
using IotStarter.App.Core.Network;
using IotStarter.App.Core.Platform;
using Microsoft.Extensions.Logging;

namespace IotStarter.App.Core.State;

/// <summary>Outcome of a user action on a device, for a specific message in the UI.</summary>
public enum DeviceActionResult
{
    Ok,
    Unreachable,
    Unauthorized,
    Rejected,
    Failed
}

/// <summary>
/// Behaviour behind the device screens: load, refresh (rediscovery), switching with optimistic
/// update and rollback, rename, identify, reboot, factory reset and forget.
/// </summary>
public sealed class DeviceManager(
    AppState state,
    IDeviceStore store,
    ITokenStore tokens,
    IDeviceLocator locator,
    IDeviceApiClient api,
    IAppSettings settings,
    INetworkStatus network,
    TimeProvider time,
    ILogger<DeviceManager> logger)
{
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    /// <summary>Reads the stored list for the current mode (demo or real) and publishes it.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<DeviceRecord> records = await store.GetAllAsync(cancellationToken).ConfigureAwait(false);
        bool demo = settings.DemoMode;
        state.SetNetwork(network.State);
        state.SetDevices([.. records.Where(r => r.IsDemo == demo).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).Select(DeviceView.Initial)]);
    }

    /// <summary>
    /// Finds every device again (last IP, mDNS, UDP) and reads its outputs. Runs on app start, resume,
    /// network change and pull-to-refresh. Concurrent calls share one refresh.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!await _refreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            // A refresh is already running; wait for it instead of starting another.
            await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            _refreshGate.Release();
            return;
        }

        try
        {
            state.SetNetwork(network.State);
            IReadOnlyList<DeviceView> devices = state.Devices;
            if (devices.Count == 0)
            {
                return;
            }

            state.SetRefreshing(true);
            if (network.State != LocalNetworkState.LocalNetwork && !settings.DemoMode)
            {
                foreach (DeviceView device in devices)
                {
                    state.UpdateDevice(device.Id, d => d with { Connectivity = DeviceConnectivity.Offline });
                }

                state.Publish(new AppNotice(NoticeKind.NotOnLocalNetwork, null));
                return;
            }

            foreach (DeviceView device in devices)
            {
                state.UpdateDevice(device.Id, d => d with { Connectivity = DeviceConnectivity.Checking });
            }

            IReadOnlyDictionary<string, LocatedDevice> located = await locator.LocateAsync(
                [.. devices.Select(d => new LocateRequest(d.Id, d.Record.LastEndpoint))],
                cancellationToken).ConfigureAwait(false);

            await Task.WhenAll(devices.Select(d => RefreshOneAsync(d, located.GetValueOrDefault(d.Id), cancellationToken))).ConfigureAwait(false);
        }
        finally
        {
            state.SetRefreshing(false);
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// Switches an output. The UI flips immediately (pending); on failure the switch rolls back and a
    /// notice is published.
    /// </summary>
    public async Task<DeviceActionResult> SetOutputAsync(string deviceId, int outputId, bool on, CancellationToken cancellationToken)
    {
        DeviceView? device = state.Find(deviceId);
        OutputView? before = device?.Outputs.FirstOrDefault(o => o.Id == outputId);
        if (device is null || before is null)
        {
            return DeviceActionResult.Failed;
        }

        SetOutput(deviceId, outputId, o => o with { On = on, Pending = true });

        (DeviceActionResult result, OutputResponse? confirmed) = await CallAsync(device, (endpoint, token, ct) =>
            api.SetOutputAsync(endpoint, token, outputId, on, ct), cancellationToken).ConfigureAwait(false);

        if (result == DeviceActionResult.Ok && confirmed is not null)
        {
            SetOutput(deviceId, outputId, o => o with { On = confirmed.On, Pending = false });
            return result;
        }

        SetOutput(deviceId, outputId, o => o with { On = before.On, Pending = false });
        state.Publish(new AppNotice(NoticeKind.SwitchFailed, deviceId));
        return result;
    }

    public async Task<DeviceActionResult> RenameAsync(string deviceId, string name, CancellationToken cancellationToken)
    {
        DeviceView? device = state.Find(deviceId);
        if (device is null)
        {
            return DeviceActionResult.Failed;
        }

        (DeviceActionResult result, string? saved) = await CallAsync(device, (endpoint, token, ct) =>
            api.SetNameAsync(endpoint, token, name, ct), cancellationToken).ConfigureAwait(false);
        if (result != DeviceActionResult.Ok || saved is null)
        {
            return result;
        }

        DeviceRecord record = device.Record with { Name = saved };
        await store.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
        state.UpdateDevice(deviceId, d => d with { Record = record });
        return result;
    }

    public async Task<DeviceActionResult> IdentifyAsync(string deviceId, CancellationToken cancellationToken) =>
        await RunAsync(deviceId, (endpoint, token, ct) => api.IdentifyAsync(endpoint, token, ct), cancellationToken).ConfigureAwait(false);

    public async Task<DeviceActionResult> RebootAsync(string deviceId, CancellationToken cancellationToken)
    {
        DeviceActionResult result = await RunAsync(deviceId, (endpoint, token, ct) => api.RebootAsync(endpoint, token, ct), cancellationToken)
            .ConfigureAwait(false);
        if (result == DeviceActionResult.Ok)
        {
            state.UpdateDevice(deviceId, d => d with { Connectivity = DeviceConnectivity.Checking });
        }

        return result;
    }

    /// <summary>Factory-resets the device over the network, then forgets it.</summary>
    public async Task<DeviceActionResult> FactoryResetAsync(string deviceId, CancellationToken cancellationToken)
    {
        DeviceActionResult result = await RunAsync(deviceId, (endpoint, token, ct) => api.FactoryResetAsync(endpoint, token, ct), cancellationToken)
            .ConfigureAwait(false);
        if (result == DeviceActionResult.Ok)
        {
            await ForgetAsync(deviceId, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Removes the device and its token from this phone only.</summary>
    public async Task ForgetAsync(string deviceId, CancellationToken cancellationToken)
    {
        await store.RemoveAsync(deviceId, cancellationToken).ConfigureAwait(false);
        await tokens.RemoveAsync(deviceId).ConfigureAwait(false);
        state.Remove(deviceId);
        logger.LogInformation("Forgot device {DeviceId}", deviceId);
    }

    /// <summary>Saves a newly provisioned device (called by the add-device wizard) and shows it online.</summary>
    public async Task<DeviceView> AddProvisionedAsync(
        DeviceRecord record,
        string token,
        IReadOnlyList<OutputResponse>? outputs,
        string? firmware,
        CancellationToken cancellationToken)
    {
        await tokens.SetAsync(record.Id, token).ConfigureAwait(false);
        await store.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
        DeviceView view = DeviceView.Initial(record) with
        {
            Connectivity = DeviceConnectivity.Online,
            Firmware = firmware,
            Outputs = outputs is null ? DeviceView.Initial(record).Outputs : ToViews(outputs),
            FoundVia = DiscoverySource.LastKnownAddress
        };
        state.AddOrReplace(view);
        return view;
    }

    /// <summary>Returns the stored token for a device (needed by the recovery-window provisioning).</summary>
    public Task<string?> GetTokenAsync(string deviceId) => tokens.GetAsync(deviceId);

    // ---- Helpers --------------------------------------------------------------------------------

    private async Task RefreshOneAsync(DeviceView device, LocatedDevice? located, CancellationToken cancellationToken)
    {
        if (located is null)
        {
            state.UpdateDevice(device.Id, d => d with { Connectivity = DeviceConnectivity.Offline });
            return;
        }

        DeviceRecord record = device.Record with
        {
            LastIPv4 = located.Endpoint.IPv4,
            Port = located.Endpoint.Port,
            LastSeen = time.GetUtcNow(),
            Name = string.IsNullOrWhiteSpace(located.Info.Name) ? device.Record.Name : located.Info.Name
        };
        await store.UpsertAsync(record, cancellationToken).ConfigureAwait(false);

        string? token = await tokens.GetAsync(device.Id).ConfigureAwait(false);
        if (token is null)
        {
            state.UpdateDevice(device.Id, d => d with { Record = record, Connectivity = DeviceConnectivity.Unauthorized, Firmware = located.Info.Firmware });
            return;
        }

        try
        {
            IReadOnlyList<OutputResponse> outputs = await api.GetOutputsAsync(located.Endpoint, token, cancellationToken).ConfigureAwait(false);
            state.UpdateDevice(device.Id, d => d with
            {
                Record = record,
                Connectivity = DeviceConnectivity.Online,
                Outputs = ToViews(outputs),
                Firmware = located.Info.Firmware,
                FoundVia = located.Source
            });
        }
        catch (DeviceApiException ex)
        {
            DeviceConnectivity connectivity = ex.Error == DeviceApiError.Unauthorized ? DeviceConnectivity.Unauthorized : DeviceConnectivity.Offline;
            state.UpdateDevice(device.Id, d => d with { Record = record, Connectivity = connectivity, Firmware = located.Info.Firmware });
        }
    }

    private static IReadOnlyList<OutputView> ToViews(IReadOnlyList<OutputResponse> outputs) =>
        [.. outputs.OrderBy(o => o.Id).Select(o => new OutputView(o.Id, o.Label, o.On, Pending: false))];

    private void SetOutput(string deviceId, int outputId, Func<OutputView, OutputView> update) =>
        state.UpdateDevice(deviceId, d => d with { Outputs = [.. d.Outputs.Select(o => o.Id == outputId ? update(o) : o)] });

    private async Task<DeviceActionResult> RunAsync(
        string deviceId,
        Func<DeviceEndpoint, string, CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        DeviceView? device = state.Find(deviceId);
        if (device is null)
        {
            return DeviceActionResult.Failed;
        }

        (DeviceActionResult result, bool _) = await CallAsync(device, async (endpoint, token, ct) =>
        {
            await action(endpoint, token, ct).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<(DeviceActionResult Result, T? Value)> CallAsync<T>(
        DeviceView device,
        Func<DeviceEndpoint, string, CancellationToken, Task<T>> call,
        CancellationToken cancellationToken)
    {
        DeviceEndpoint? endpoint = device.Record.LastEndpoint;
        string? token = await tokens.GetAsync(device.Id).ConfigureAwait(false);
        if (token is null)
        {
            state.UpdateDevice(device.Id, d => d with { Connectivity = DeviceConnectivity.Unauthorized });
            state.Publish(new AppNotice(NoticeKind.DeviceUnauthorized, device.Id));
            return (DeviceActionResult.Unauthorized, default);
        }

        if (endpoint is null)
        {
            state.Publish(new AppNotice(NoticeKind.DeviceUnreachable, device.Id));
            return (DeviceActionResult.Unreachable, default);
        }

        try
        {
            T value = await call(endpoint, token, cancellationToken).ConfigureAwait(false);
            if (device.Connectivity != DeviceConnectivity.Online)
            {
                state.UpdateDevice(device.Id, d => d with { Connectivity = DeviceConnectivity.Online });
            }

            return (DeviceActionResult.Ok, value);
        }
        catch (DeviceApiException ex)
        {
            logger.LogInformation("Request to {DeviceId} failed: {Error}", device.Id, ex.Error);
            switch (ex.Error)
            {
                case DeviceApiError.Unauthorized:
                    state.UpdateDevice(device.Id, d => d with { Connectivity = DeviceConnectivity.Unauthorized });
                    state.Publish(new AppNotice(NoticeKind.DeviceUnauthorized, device.Id));
                    return (DeviceActionResult.Unauthorized, default);
                case DeviceApiError.Unreachable:
                    state.UpdateDevice(device.Id, d => d with { Connectivity = DeviceConnectivity.Offline });
                    state.Publish(new AppNotice(NoticeKind.DeviceUnreachable, device.Id));
                    // The address may have changed; look for the device again in the background.
                    _ = RefreshInBackgroundAsync();
                    return (DeviceActionResult.Unreachable, default);
                case DeviceApiError.NotFound:
                case DeviceApiError.BadRequest:
                    return (DeviceActionResult.Rejected, default);
                default:
                    return (DeviceActionResult.Failed, default);
            }
        }
    }

    private async Task RefreshInBackgroundAsync()
    {
        try
        {
            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Background refresh failed");
        }
    }
}
