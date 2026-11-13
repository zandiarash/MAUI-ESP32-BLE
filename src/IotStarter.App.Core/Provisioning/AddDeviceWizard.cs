using System.Security.Cryptography;
using IotStarter.App.Core.Ble;
using IotStarter.App.Core.Devices;
using IotStarter.App.Core.Network;
using IotStarter.App.Core.Platform;
using IotStarter.App.Core.State;
using IotStarter.Shared;
using Microsoft.Extensions.Logging;

namespace IotStarter.App.Core.Provisioning;

/// <summary>
/// The add-device wizard as a state machine (docs/design-review.md §7). The UI renders
/// <see cref="State"/> and calls the action methods; it holds no behaviour itself.
/// One instance per wizard run; dispose it when the wizard closes.
/// </summary>
public sealed class AddDeviceWizard(
    IBleTransport ble,
    IPermissionService permissions,
    IDeviceLocator locator,
    IDeviceApiClient api,
    DeviceManager devices,
    IAppSettings settings,
    TimeProvider time,
    ILogger<AddDeviceWizard> logger) : IAsyncDisposable
{
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan RejoinTimeout = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan RejoinPollInterval = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan ScanPublishInterval = TimeSpan.FromMilliseconds(400);

    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly Lock _scanLock = new();
    private readonly Dictionary<string, DiscoveredDevice> _found = [];
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _operationCts;
    private ProvisioningSession? _session;
    private DiscoveredDevice? _device;
    private byte[]? _token;
    private CredentialDraft _draft = CredentialDraft.Empty;
    private string? _reportedIp;
    private long _lastScanPublish;
    private bool _disposed;

    public WizardState State { get; private set; } = new WizardChecking();

    /// <summary>Raised on any thread when <see cref="State"/> changes.</summary>
    public event Action<WizardState>? StateChanged;

    /// <summary>Checks Bluetooth and permissions, then starts scanning.</summary>
    public Task StartAsync() => RunExclusiveAsync(StartCoreAsync);

    /// <summary>Connects to a device from the scan list, pairs, and asks it to scan for networks.</summary>
    public Task SelectDeviceAsync(string peripheralId) => RunExclusiveAsync(() => SelectCoreAsync(peripheralId));

    /// <summary>Asks the device to scan for Wi-Fi networks again.</summary>
    public Task RefreshNetworksAsync() => RunExclusiveAsync(ScanNetworksCoreAsync);

    /// <summary>
    /// Validates and sends the credentials. Returns the validation problem without changing state when
    /// the input is not acceptable.
    /// </summary>
    public async Task<CredentialProblem> SubmitCredentialsAsync(string ssid, string password, bool isHidden)
    {
        CredentialProblem problem = CredentialRules.Validate(ssid, password);
        if (problem != CredentialProblem.None)
        {
            return problem;
        }

        await RunExclusiveAsync(() => ProvisionCoreAsync(new CredentialDraft(ssid, password, isHidden))).ConfigureAwait(false);
        return CredentialProblem.None;
    }

    /// <summary>Asks the device to stop joining. The wizard returns to the network step.</summary>
    public async Task CancelProvisioningAsync()
    {
        if (State is not WizardProvisioning provisioning || _session is null)
        {
            return;
        }

        SetState(provisioning with { CancelRequested = true });
        try
        {
            await _session.CancelAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation("Cancel was not acknowledged: {Message}", ex.Message);
        }
    }

    /// <summary>Follows the retry target of the current failure.</summary>
    public Task RetryAsync() => RunExclusiveAsync(async () =>
    {
        if (State is not WizardFailed failed)
        {
            return;
        }

        switch (failed.Failure.Retry)
        {
            case RetryTarget.Scan:
                await CloseSessionAsync().ConfigureAwait(false);
                await StartCoreAsync().ConfigureAwait(false);
                break;
            case RetryTarget.Credentials when _session is { IsConnected: true } session && _device is not null:
                ShowNetworkStep(_device, session.Info, [], session.Info.Has(Capability.CanScan) ? NetworkScanStatus.Done : NetworkScanStatus.Unsupported, failed.Failure);
                break;
            case RetryTarget.Credentials when _device is not null:
                // The link dropped (for example the device restarted): reconnect, keeping the typed values.
                await SelectCoreAsync(_device.PeripheralId).ConfigureAwait(false);
                break;
            case RetryTarget.Handover when _lastInfo is not null:
                await HandoverCoreAsync(_lastInfo!, _reportedIp, verifyOnly: false).ConfigureAwait(false);
                break;
            default:
                await CloseSessionAsync().ConfigureAwait(false);
                await StartCoreAsync().ConfigureAwait(false);
                break;
        }
    });

    /// <summary>Goes back from the network step to the device list.</summary>
    public Task BackToScanAsync() => RunExclusiveAsync(async () =>
    {
        await CloseSessionAsync().ConfigureAwait(false);
        await StartCoreAsync().ConfigureAwait(false);
    });

    /// <summary>Saves the chosen name (best effort) and finishes.</summary>
    public Task FinishAsync(string? name) => RunExclusiveAsync(async () =>
    {
        if (State is not WizardNaming naming)
        {
            return;
        }

        string trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length > 0 && trimmed != naming.Record.Name)
        {
            DeviceActionResult result = await devices.RenameAsync(naming.Record.Id, trimmed, _lifetime.Token).ConfigureAwait(false);
            if (result != DeviceActionResult.Ok)
            {
                logger.LogInformation("Naming the new device failed ({Result}); keeping the default name", result);
            }
        }

        ClearSecrets();
        SetState(new WizardDone(naming.Record.Id));
    });

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        StopScan();
        await CloseSessionAsync().ConfigureAwait(false);
        ClearSecrets();
        ble.AvailabilityChanged -= OnAvailabilityChanged;
        _lifetime.Dispose();
    }

    // ---- Steps ------------------------------------------------------------------------------------

    private DeviceInfo? _lastInfo;

    private async Task StartCoreAsync()
    {
        SetState(new WizardChecking());
        ble.AvailabilityChanged -= OnAvailabilityChanged;
        ble.AvailabilityChanged += OnAvailabilityChanged;

        if (ble.Availability == BleAvailability.Unsupported)
        {
            SetState(new WizardBlocked(BlockReason.BluetoothUnsupported));
            return;
        }

        PermissionState permission = await permissions.CheckBluetoothAsync().ConfigureAwait(false);
        if (permission == PermissionState.Denied)
        {
            permission = await permissions.RequestBluetoothAsync().ConfigureAwait(false);
        }

        if (permission is PermissionState.Denied or PermissionState.DeniedPermanently)
        {
            SetState(new WizardBlocked(permission == PermissionState.DeniedPermanently
                ? BlockReason.PermissionDeniedPermanently
                : BlockReason.PermissionDenied));
            return;
        }

        switch (ble.Availability)
        {
            case BleAvailability.Off:
                SetState(new WizardBlocked(BlockReason.BluetoothOff));
                return;
            case BleAvailability.Unauthorized:
                SetState(new WizardBlocked(BlockReason.PermissionDeniedPermanently));
                return;
        }

        StartScan();
    }

    private void StartScan()
    {
        StopScan();
        lock (_scanLock)
        {
            _found.Clear();
        }

        SetState(new WizardScanning([]));
        var scanCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _scanCts = scanCts;
        _ = ScanLoopAsync(scanCts.Token);
    }

    private async Task ScanLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ble.ScanAsync(Guid.Parse(ProtocolConstants.ServiceUuid), OnAdvertisement, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Scan stopped on purpose.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "BLE scan failed");
            if (State is WizardScanning)
            {
                SetState(ble.Availability == BleAvailability.Off
                    ? new WizardBlocked(BlockReason.BluetoothOff)
                    : new WizardFailed(new WizardFailure(FailureKind.ConnectFailed, RetryTarget.Scan), null));
            }
        }
    }

    private void OnAdvertisement(BleAdvertisement advertisement)
    {
        IReadOnlyList<DiscoveredDevice> snapshot;
        bool publish;
        lock (_scanLock)
        {
            _found.TryGetValue(advertisement.PeripheralId, out DiscoveredDevice? previous);
            DiscoveredDevice? merged = AdvertisementParser.Merge(previous, advertisement);
            if (merged is null)
            {
                return;
            }

            _found[advertisement.PeripheralId] = merged;

            // Publish new devices and identity changes at once; RSSI-only changes at most every 400 ms.
            long now = time.GetTimestamp();
            bool structural = previous is null || previous.ModelId != merged.ModelId || previous.Name != merged.Name;
            publish = structural || time.GetElapsedTime(_lastScanPublish, now) >= ScanPublishInterval;
            if (!publish)
            {
                return;
            }

            _lastScanPublish = now;
            snapshot = [.. _found.Values.OrderByDescending(d => d.Rssi).ThenBy(d => d.Name, StringComparer.Ordinal)];
        }

        if (State is WizardScanning)
        {
            SetState(new WizardScanning(snapshot));
        }
    }

    private async Task SelectCoreAsync(string peripheralId)
    {
        DiscoveredDevice? device;
        lock (_scanLock)
        {
            _found.TryGetValue(peripheralId, out device);
        }

        device ??= _device?.PeripheralId == peripheralId ? _device : null;
        if (device is null)
        {
            return;
        }

        if (device.IsUnsupportedProtocol)
        {
            SetState(new WizardFailed(new WizardFailure(FailureKind.UnsupportedDevice, RetryTarget.Scan), device));
            return;
        }

        StopScan();
        await CloseSessionAsync().ConfigureAwait(false);
        _device = device;
        SetState(new WizardConnecting(device, ConnectPhase.Connecting));

        CancellationToken ct = NewOperationToken();
        ProvisioningSession session;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectTimeout);
            session = await ProvisioningSession.OpenAsync(ble, peripheralId, time, logger, timeout.Token).ConfigureAwait(false);
        }
        catch (ProvisioningProtocolException ex)
        {
            logger.LogWarning("Unsupported device: {Message}", ex.Message);
            SetState(new WizardFailed(new WizardFailure(FailureKind.UnsupportedDevice, RetryTarget.Scan), device));
            return;
        }
        catch (Exception ex) when (ex is BleException or TimeoutException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning("Connecting failed: {Message}", ex.Message);
            SetState(new WizardFailed(new WizardFailure(FailureKind.ConnectFailed, RetryTarget.Scan), device));
            return;
        }

        _session = session;
        _lastInfo = session.Info;
        session.StatusChanged += OnDeviceStatus;
        session.Disconnected += OnSessionDisconnected;

        // Token: the existing one in the recovery window, otherwise a fresh random one (kept across retries).
        if (session.Info.Has(Capability.TokenRequired))
        {
            string? existing = await devices.GetTokenAsync(session.Info.DeviceId).ConfigureAwait(false);
            byte[]? parsed = existing is null ? null : Convert.FromHexString(existing);
            if (parsed is not { Length: ProtocolConstants.TokenBytes })
            {
                await CloseSessionAsync().ConfigureAwait(false);
                SetState(new WizardFailed(new WizardFailure(FailureKind.NotOwner, RetryTarget.Scan), device));
                return;
            }

            _token = parsed;
        }
        else
        {
            _token ??= RandomNumberGenerator.GetBytes(ProtocolConstants.TokenBytes);
        }

        SetState(new WizardConnecting(device, ConnectPhase.Pairing));
        try
        {
            await session.EnsurePairedAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is BleException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning("Pairing failed: {Message}", ex.Message);
            await CloseSessionAsync().ConfigureAwait(false);
            SetState(new WizardFailed(new WizardFailure(FailureKind.PairingFailed, RetryTarget.Scan), device));
            return;
        }

        // A device that comes back from a failed post-restart join reports the reason in State.
        WizardFailure? previous = session.Status is { State: DeviceState.Ready, Error: not ErrorCode.None and not ErrorCode.Cancelled } status
            ? new WizardFailure(MapError(status.Error), RetryTarget.Credentials)
            : null;

        bool canScan = session.Info.Has(Capability.CanScan);
        ShowNetworkStep(device, session.Info, [], canScan ? NetworkScanStatus.Scanning : NetworkScanStatus.Unsupported, previous);
        if (canScan)
        {
            await ScanNetworksCoreAsync().ConfigureAwait(false);
        }
    }

    private async Task ScanNetworksCoreAsync()
    {
        if (State is not WizardChoosingNetwork step || _session is not { } session || !session.Info.Has(Capability.CanScan))
        {
            return;
        }

        SetState(step with { ScanStatus = NetworkScanStatus.Scanning });
        CancellationToken ct = NewOperationToken();
        try
        {
            IReadOnlyList<WifiNetwork> networks = await session.ScanNetworksAsync(ct).ConfigureAwait(false);
            if (State is WizardChoosingNetwork current)
            {
                SetState(current with { Networks = networks, ScanStatus = NetworkScanStatus.Done });
            }
        }
        catch (Exception ex) when (ex is ProvisioningCommandException or TimeoutException or BleException)
        {
            logger.LogInformation("Device Wi-Fi scan failed: {Message}", ex.Message);
            if (State is WizardChoosingNetwork current)
            {
                SetState(current with { ScanStatus = NetworkScanStatus.Failed });
            }
        }
        catch (ProvisioningProtocolException)
        {
            SetState(new WizardFailed(new WizardFailure(FailureKind.DeviceDisconnected, RetryTarget.Credentials), _device));
        }
    }

    private async Task ProvisionCoreAsync(CredentialDraft draft)
    {
        if (_session is not { } session || _device is not { } device || _token is null)
        {
            return;
        }

        _draft = draft;
        SetState(new WizardProvisioning(device, draft.Ssid, DeviceState.Connecting, CancelRequested: false));

        CancellationToken ct = NewOperationToken();
        ProvisionOutcome outcome;
        try
        {
            outcome = await session.ProvisionAsync(draft.Ssid, draft.Password, _token, ct).ConfigureAwait(false);
        }
        catch (ProvisioningProtocolException)
        {
            // The link dropped without a result. The device may still have joined and restarted:
            // look for it on the network before reporting a failure.
            await HandoverCoreAsync(session.Info, reportedIp: null, verifyOnly: true).ConfigureAwait(false);
            return;
        }
        catch (TimeoutException)
        {
            SetState(new WizardFailed(new WizardFailure(FailureKind.WifiTimeout, RetryTarget.Credentials), device));
            return;
        }
        catch (BleException)
        {
            SetState(new WizardFailed(new WizardFailure(FailureKind.DeviceDisconnected, RetryTarget.Credentials), device));
            return;
        }

        switch (outcome)
        {
            case ProvisionSucceeded succeeded:
                await HandoverCoreAsync(session.Info, succeeded.IPv4, verifyOnly: false).ConfigureAwait(false);
                break;
            case ProvisionRestarting:
                await HandoverCoreAsync(session.Info, reportedIp: null, verifyOnly: false).ConfigureAwait(false);
                break;
            case ProvisionFailed { Error: ErrorCode.Cancelled }:
                ShowNetworkStep(device, session.Info, [], session.Info.Has(Capability.CanScan) ? NetworkScanStatus.Done : NetworkScanStatus.Unsupported, null);
                break;
            case ProvisionFailed failed:
                FailureKind kind = MapError(failed.Error);
                RetryTarget retry = kind is FailureKind.NotAuthorized ? RetryTarget.Scan : RetryTarget.Credentials;
                SetState(new WizardFailed(new WizardFailure(kind, retry), device));
                break;
        }
    }

    /// <summary>
    /// Waits for the device on the local network, confirms its ID through GET /info, saves it with its
    /// token and moves to naming. With <paramref name="verifyOnly"/>, a miss is reported as a lost
    /// BLE link rather than as an unreachable network.
    /// </summary>
    private async Task HandoverCoreAsync(DeviceInfo info, string? reportedIp, bool verifyOnly)
    {
        _reportedIp = reportedIp;
        _lastInfo = info;
        await CloseSessionAsync().ConfigureAwait(false);
        SetState(new WizardRejoining(info, reportedIp));

        CancellationToken ct = NewOperationToken();
        DeviceEndpoint? hint = reportedIp is null ? null : new DeviceEndpoint(reportedIp);
        LocatedDevice? located = null;
        long started = time.GetTimestamp();

        while (located is null && time.GetElapsedTime(started) < RejoinTimeout)
        {
            IReadOnlyDictionary<string, LocatedDevice> found = await locator.LocateAsync([new LocateRequest(info.DeviceId, hint)], ct).ConfigureAwait(false);
            if (found.TryGetValue(info.DeviceId, out located))
            {
                break;
            }

            await Task.Delay(RejoinPollInterval, time, ct).ConfigureAwait(false);
        }

        if (located is null)
        {
            FailureKind kind = verifyOnly ? FailureKind.DeviceDisconnected : FailureKind.Unreachable;
            RetryTarget retry = verifyOnly ? RetryTarget.Credentials : RetryTarget.Handover;
            SetState(new WizardFailed(new WizardFailure(kind, retry), _device));
            return;
        }

        string tokenHex = Convert.ToHexStringLower(_token!);
        IReadOnlyList<OutputResponse>? outputs = null;
        try
        {
            outputs = await api.GetOutputsAsync(located.Endpoint, tokenHex, ct).ConfigureAwait(false);
        }
        catch (DeviceApiException ex)
        {
            // Saved anyway: the device list refreshes outputs later.
            logger.LogInformation("Reading outputs after provisioning failed: {Error}", ex.Error);
        }

        string defaultName = string.IsNullOrWhiteSpace(located.Info.Name)
            ? info.Model?.Name ?? _device?.Name ?? info.DeviceId
            : located.Info.Name;
        var record = new DeviceRecord(
            info.DeviceId,
            info.ModelId,
            defaultName,
            located.Endpoint.IPv4,
            located.Endpoint.Port,
            time.GetUtcNow(),
            settings.DemoMode);

        await devices.AddProvisionedAsync(record, tokenHex, outputs, located.Info.Firmware, ct).ConfigureAwait(false);
        logger.LogInformation("Device {DeviceId} provisioned and found at {Endpoint}", info.DeviceId, located.Endpoint);
        SetState(new WizardNaming(record, defaultName));
    }

    // ---- Helpers ----------------------------------------------------------------------------------

    private void ShowNetworkStep(DiscoveredDevice device, DeviceInfo info, IReadOnlyList<WifiNetwork> networks, NetworkScanStatus status, WizardFailure? previous)
    {
        IReadOnlyList<WifiNetwork> keep = networks.Count > 0
            ? networks
            : State is WizardChoosingNetwork current ? current.Networks : _lastNetworks;
        SetState(new WizardChoosingNetwork(device, info, keep, status, _draft, previous));
    }

    private IReadOnlyList<WifiNetwork> _lastNetworks = [];

    internal static FailureKind MapError(ErrorCode error) => error switch
    {
        ErrorCode.WrongPassword => FailureKind.WrongPassword,
        ErrorCode.NetworkNotFound => FailureKind.NetworkNotFound,
        ErrorCode.NoIpAddress => FailureKind.NoIpAddress,
        ErrorCode.Timeout or ErrorCode.PreviousAttemptFailed => FailureKind.WifiTimeout,
        ErrorCode.UnsupportedSecurity => FailureKind.UnsupportedSecurity,
        ErrorCode.NotAuthorized => FailureKind.NotAuthorized,
        _ => FailureKind.DeviceError
    };

    private void OnDeviceStatus(DeviceStatus status)
    {
        if (State is WizardProvisioning provisioning && provisioning.DeviceState != status.State)
        {
            SetState(provisioning with { DeviceState = status.State });
        }
    }

    private void OnSessionDisconnected()
    {
        // Only meaningful while the user is on a step that needs the link.
        if (State is WizardChoosingNetwork)
        {
            SetState(new WizardFailed(new WizardFailure(FailureKind.DeviceDisconnected, RetryTarget.Credentials), _device));
        }
    }

    private void OnAvailabilityChanged(object? sender, EventArgs e)
    {
        BleAvailability availability = ble.Availability;
        if (availability == BleAvailability.On && State is WizardBlocked { Reason: BlockReason.BluetoothOff })
        {
            _ = StartAsync();
        }
        else if (availability == BleAvailability.Off && State is WizardScanning or WizardConnecting or WizardChoosingNetwork)
        {
            StopScan();
            SetState(new WizardBlocked(BlockReason.BluetoothOff));
        }
    }

    private CancellationToken NewOperationToken()
    {
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _operationCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        return _operationCts.Token;
    }

    private void StopScan()
    {
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = null;
    }

    private async Task CloseSessionAsync()
    {
        if (_session is not { } session)
        {
            return;
        }

        _session = null;
        session.StatusChanged -= OnDeviceStatus;
        session.Disconnected -= OnSessionDisconnected;
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogInformation("Closing the BLE session failed: {Message}", ex.Message);
        }
    }

    private void ClearSecrets()
    {
        if (_token is not null)
        {
            CryptographicOperations.ZeroMemory(_token);
            _token = null;
        }

        _draft = CredentialDraft.Empty;
    }

    private void SetState(WizardState state)
    {
        if (_disposed)
        {
            return;
        }

        if (state is WizardChoosingNetwork step && step.Networks.Count > 0)
        {
            _lastNetworks = step.Networks;
        }

        State = state;
        StateChanged?.Invoke(state);
    }

    private async Task RunExclusiveAsync(Func<Task> action)
    {
        if (_disposed)
        {
            return;
        }

        // Ignore double taps: one wizard action at a time.
        if (!await _busy.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // The wizard was closed.
        }
        catch (OperationCanceledException)
        {
            // A newer operation superseded this one.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Wizard step failed unexpectedly");
            SetState(new WizardFailed(new WizardFailure(FailureKind.DeviceError, RetryTarget.Scan), _device));
        }
        finally
        {
            _busy.Release();
        }
    }
}
