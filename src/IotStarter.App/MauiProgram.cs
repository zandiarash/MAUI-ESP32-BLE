using IotStarter.App.Core;
using IotStarter.App.Core.Ble;
using IotStarter.App.Core.Demo;
using IotStarter.App.Core.Devices;
using IotStarter.App.Core.Network;
using IotStarter.App.Core.Platform;
using IotStarter.App.Services;
using Microsoft.Extensions.Logging;

namespace IotStarter.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddAntDesign();
        builder.Services.AddLocalization();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
#endif

        // Platform-independent services, demo mode and the switching facades.
        builder.Services.AddIotStarterCore();

        // Platform implementations ("real" side of each switch).
        builder.Services.AddKeyedSingleton<IBleTransport, PluginBleTransport>(ModeKeys.Real);
        builder.Services.AddKeyedSingleton<IMdnsBrowser, ZeroconfMdnsBrowser>(ModeKeys.Real);
        builder.Services.AddKeyedSingleton<IPermissionService, MauiPermissionService>(ModeKeys.Real);

        builder.Services.AddSingleton<ITokenStore, SecureTokenStore>();
        builder.Services.AddSingleton<IAppPaths, MauiAppPaths>();
        builder.Services.AddSingleton<IKeyValueStore, MauiKeyValueStore>();
        builder.Services.AddSingleton<INetworkStatus, MauiNetworkStatus>();
        builder.Services.AddSingleton<IHaptics, MauiHaptics>();
        builder.Services.AddSingleton<AppLifecycle>();
        builder.Services.AddSingleton<UiCulture>();

        return builder.Build();
    }
}
