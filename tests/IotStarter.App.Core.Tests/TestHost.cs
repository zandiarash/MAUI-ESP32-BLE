using System.Collections.Concurrent;
using IotStarter.App.Core.Ble;
using IotStarter.App.Core.Demo;
using IotStarter.App.Core.Devices;
using IotStarter.App.Core.Network;
using IotStarter.App.Core.Platform;
using IotStarter.App.Core.Provisioning;
using IotStarter.App.Core.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IotStarter.App.Core.Tests;

/// <summary>
/// The app's real DI graph (AddIotStarterCore) with in-memory platform services and the demo world
/// sped up, so the wizard and device manager run against the real protocol code end to end.
/// </summary>
internal sealed class TestHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "iotstarter-tests", Guid.NewGuid().ToString("N"));

    public TestHost(bool demoMode = true, IPermissionService? realPermissions = null, IBleTransport? realBle = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddIotStarterCore();

        var keyValues = new MemoryKeyValueStore();
        services.AddSingleton<IKeyValueStore>(keyValues);
        services.AddSingleton<ITokenStore>(Tokens);
        services.AddSingleton<IAppPaths>(new TempPaths(_dataDirectory));
        services.AddSingleton<INetworkStatus>(Network);
        services.AddSingleton<IHaptics, NoHaptics>();

        // "Real" platform services are fakes here; demo mode routes around them anyway.
        services.AddKeyedSingleton<IBleTransport>(ModeKeys.Real, realBle ?? new UnavailableBle());
        services.AddKeyedSingleton<IMdnsBrowser>(ModeKeys.Real, new NoMdns());
        services.AddKeyedSingleton<IPermissionService>(ModeKeys.Real, realPermissions ?? new DemoPermissionService());

        _provider = services.BuildServiceProvider();
        Settings.DemoMode = demoMode;
        World.TimeScale = 0.01;
    }

    public MemoryTokenStore Tokens { get; } = new();

    public FixedNetworkStatus Network { get; } = new();

    public IServiceProvider Services => _provider;

    public DemoWorld World => _provider.GetRequiredService<DemoWorld>();

    public IAppSettings Settings => _provider.GetRequiredService<IAppSettings>();

    public AppState State => _provider.GetRequiredService<AppState>();

    public DeviceManager Devices => _provider.GetRequiredService<DeviceManager>();

    public IDeviceStore Store => _provider.GetRequiredService<IDeviceStore>();

    public AddDeviceWizard NewWizard() => _provider.GetRequiredService<AddDeviceWizard>();

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    /// <summary>Waits until the wizard reaches a state matching <paramref name="predicate"/>.</summary>
    public static async Task<WizardState> WaitForAsync(AddDeviceWizard wizard, Func<WizardState, bool> predicate, int timeoutMs = 10_000)
    {
        var reached = new TaskCompletionSource<WizardState>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(WizardState state)
        {
            if (predicate(state))
            {
                reached.TrySetResult(state);
            }
        }

        wizard.StateChanged += Handler;
        try
        {
            if (predicate(wizard.State))
            {
                return wizard.State;
            }

            return await reached.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Wizard stuck in {Describe(wizard.State)}");
        }
        finally
        {
            wizard.StateChanged -= Handler;
        }
    }

    public static string Describe(WizardState state) => state switch
    {
        WizardFailed f => $"Failed({f.Failure.Kind}, retry {f.Failure.Retry})",
        WizardChecking or WizardBlocked or WizardScanning or WizardConnecting or WizardChoosingNetwork
            or WizardProvisioning or WizardRejoining or WizardNaming or WizardDone => state.ToString() ?? "?",
    };

    internal sealed class MemoryKeyValueStore : IKeyValueStore
    {
        private readonly ConcurrentDictionary<string, string> _values = new();

        public string? Get(string key) => _values.TryGetValue(key, out string? value) ? value : null;

        public void Set(string key, string value) => _values[key] = value;
    }

    internal sealed class MemoryTokenStore : ITokenStore
    {
        private readonly ConcurrentDictionary<string, string> _tokens = new(StringComparer.OrdinalIgnoreCase);

        public Task<string?> GetAsync(string deviceId) => Task.FromResult(_tokens.TryGetValue(deviceId, out string? token) ? token : null);

        public Task SetAsync(string deviceId, string token)
        {
            _tokens[deviceId] = token;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string deviceId)
        {
            _tokens.TryRemove(deviceId, out _);
            return Task.CompletedTask;
        }
    }

    internal sealed class FixedNetworkStatus : INetworkStatus
    {
        public LocalNetworkState State { get; set; } = LocalNetworkState.LocalNetwork;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }

    private sealed class TempPaths(string directory) : IAppPaths
    {
        public string DataDirectory { get; } = directory;
    }

    private sealed class NoHaptics : IHaptics
    {
        public void Click()
        {
        }
    }

    private sealed class NoMdns : IMdnsBrowser
    {
        public Task<IReadOnlyList<DiscoveryHit>> BrowseAsync(TimeSpan window, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DiscoveryHit>>([]);
    }

    internal sealed class UnavailableBle : IBleTransport
    {
        public BleAvailability Availability { get; set; } = BleAvailability.Off;

        public event EventHandler? AvailabilityChanged
        {
            add { }
            remove { }
        }

        public Task ScanAsync(Guid serviceUuid, Action<BleAdvertisement> onAdvertisement, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.Infinite, cancellationToken);

        public Task<IBleConnection> ConnectAsync(string peripheralId, CancellationToken cancellationToken) =>
            throw new BleException("Bluetooth is off.");
    }
}

/// <summary>A permission service with a fixed answer.</summary>
internal sealed class FixedPermissions(PermissionState state) : IPermissionService
{
    public Task<PermissionState> CheckBluetoothAsync() => Task.FromResult(state);

    public Task<PermissionState> RequestBluetoothAsync() => Task.FromResult(state);

    public void OpenAppSettings()
    {
    }

    public void OpenBluetoothSettings()
    {
    }
}
