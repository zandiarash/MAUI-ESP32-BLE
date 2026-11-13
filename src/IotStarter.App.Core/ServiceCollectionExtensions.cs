using System.Net;
using IotStarter.App.Core.Ble;
using IotStarter.App.Core.Demo;
using IotStarter.App.Core.Devices;
using IotStarter.App.Core.Network;
using IotStarter.App.Core.Platform;
using IotStarter.App.Core.Provisioning;
using IotStarter.App.Core.State;
using Microsoft.Extensions.DependencyInjection;

namespace IotStarter.App.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the platform-independent services. The host must also register, keyed with
    /// <see cref="ModeKeys.Real"/>: <see cref="IBleTransport"/>, <see cref="IMdnsBrowser"/> and
    /// <see cref="IPermissionService"/>; and unkeyed: <see cref="ITokenStore"/>, <see cref="IAppPaths"/>,
    /// <see cref="IKeyValueStore"/>, <see cref="INetworkStatus"/> and <see cref="IHaptics"/>.
    /// </summary>
    public static IServiceCollection AddIotStarterCore(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IAppSettings, AppSettings>();
        services.AddSingleton<AppState>();
        services.AddSingleton<DeviceManager>();
        services.AddSingleton<IDeviceStore, JsonDeviceStore>();
        services.AddSingleton<IDeviceLocator, DeviceLocator>();

        // One HttpClient for all devices. Connection reuse is short-lived: devices close connections.
        services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(3),
            PooledConnectionLifetime = TimeSpan.FromSeconds(30),
            AutomaticDecompression = DecompressionMethods.None,
            UseProxy = false
        }));

        // Demo world and simulated implementations.
        services.AddSingleton<DemoWorld>();
        services.AddSingleton<DemoSession>();
        services.AddKeyedSingleton<IBleTransport, DemoBleTransport>(ModeKeys.Demo);
        services.AddKeyedSingleton<IDeviceApiClient, DemoDeviceApiClient>(ModeKeys.Demo);
        services.AddKeyedSingleton<IMdnsBrowser, DemoMdnsBrowser>(ModeKeys.Demo);
        services.AddKeyedSingleton<IUdpProber, DemoUdpProber>(ModeKeys.Demo);
        services.AddKeyedSingleton<IPermissionService, DemoPermissionService>(ModeKeys.Demo);

        // Real implementations that need no platform code.
        services.AddKeyedSingleton<IDeviceApiClient, DeviceApiClient>(ModeKeys.Real);
        services.AddKeyedSingleton<IUdpProber, UdpProber>(ModeKeys.Real);

        // What the rest of the app uses: switch on Settings › Demo mode.
        services.AddSingleton<IBleTransport, SwitchingBleTransport>();
        services.AddSingleton<IDeviceApiClient, SwitchingDeviceApiClient>();
        services.AddSingleton<IMdnsBrowser, SwitchingMdnsBrowser>();
        services.AddSingleton<IUdpProber, SwitchingUdpProber>();
        services.AddSingleton<IPermissionService, SwitchingPermissionService>();

        // A fresh wizard per run.
        services.AddTransient<AddDeviceWizard>();
        return services;
    }
}
