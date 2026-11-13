using IotStarter.App.Core.Devices;
using IotStarter.App.Core.Platform;
using Microsoft.Extensions.Logging;

namespace IotStarter.App.Services;

/// <summary>
/// Runtime Bluetooth permission through MAUI's Permissions.Bluetooth: BLUETOOTH_SCAN and
/// BLUETOOTH_CONNECT on Android 12+, location on Android 11 and older, CoreBluetooth authorisation on iOS.
/// Windows needs no runtime permission.
/// </summary>
public sealed class MauiPermissionService : IPermissionService
{
    public async Task<PermissionState> CheckBluetoothAsync()
    {
        if (DeviceInfo.Current.Platform == DevicePlatform.WinUI)
        {
            return PermissionState.NotRequired;
        }

        PermissionStatus status = await MainThread.InvokeOnMainThreadAsync(Permissions.CheckStatusAsync<Permissions.Bluetooth>);
        return status == PermissionStatus.Granted ? PermissionState.Granted : PermissionState.Denied;
    }

    public async Task<PermissionState> RequestBluetoothAsync()
    {
        if (DeviceInfo.Current.Platform == DevicePlatform.WinUI)
        {
            return PermissionState.NotRequired;
        }

        PermissionStatus status = await MainThread.InvokeOnMainThreadAsync(Permissions.RequestAsync<Permissions.Bluetooth>);
        if (status == PermissionStatus.Granted)
        {
            return PermissionState.Granted;
        }

        // On Android, a denial without a rationale means "don't ask again": only Settings can fix it.
        // iOS never re-prompts after the first answer.
        bool canAskAgain = DeviceInfo.Current.Platform == DevicePlatform.Android
            && Permissions.ShouldShowRationale<Permissions.Bluetooth>();
        return canAskAgain ? PermissionState.Denied : PermissionState.DeniedPermanently;
    }

    public void OpenAppSettings() => AppInfo.Current.ShowSettingsUI();

    public void OpenBluetoothSettings()
    {
#if ANDROID
        var intent = new Android.Content.Intent(Android.Provider.Settings.ActionBluetoothSettings);
        intent.AddFlags(Android.Content.ActivityFlags.NewTask);
        Platform.AppContext.StartActivity(intent);
#elif WINDOWS
        _ = Launcher.Default.OpenAsync(new Uri("ms-settings:bluetooth"));
#else
        // iOS does not allow apps to open the Bluetooth page directly.
        AppInfo.Current.ShowSettingsUI();
#endif
    }
}

/// <summary>API tokens in SecureStorage (Android Keystore, iOS Keychain, Windows DPAPI).</summary>
public sealed class SecureTokenStore(ILogger<SecureTokenStore> logger) : ITokenStore
{
    private static string Key(string deviceId) => "token." + deviceId.ToUpperInvariant();

    public async Task<string?> GetAsync(string deviceId)
    {
        try
        {
            return await SecureStorage.Default.GetAsync(Key(deviceId));
        }
        catch (Exception ex)
        {
            // Keys can become unreadable after a backup restore; the device then shows as unauthorized.
            logger.LogWarning("Secure storage read failed: {Message}", ex.Message);
            return null;
        }
    }

    public Task SetAsync(string deviceId, string token) => SecureStorage.Default.SetAsync(Key(deviceId), token);

    public Task RemoveAsync(string deviceId)
    {
        SecureStorage.Default.Remove(Key(deviceId));
        return Task.CompletedTask;
    }
}

public sealed class MauiAppPaths : IAppPaths
{
    public string DataDirectory => FileSystem.Current.AppDataDirectory;
}

public sealed class MauiKeyValueStore : IKeyValueStore
{
    public string? Get(string key) => Preferences.Default.Get<string?>(key, null);

    public void Set(string key, string value) => Preferences.Default.Set(key, value);
}

/// <summary>Wi-Fi/Ethernet versus mobile data, with change notifications.</summary>
public sealed class MauiNetworkStatus : INetworkStatus
{
    public MauiNetworkStatus()
    {
        Connectivity.Current.ConnectivityChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    public LocalNetworkState State
    {
        get
        {
            IConnectivity connectivity = Connectivity.Current;
            IEnumerable<ConnectionProfile> profiles = connectivity.ConnectionProfiles;
            if (profiles.Contains(ConnectionProfile.WiFi) || profiles.Contains(ConnectionProfile.Ethernet))
            {
                return LocalNetworkState.LocalNetwork;
            }

            return connectivity.NetworkAccess == NetworkAccess.None ? LocalNetworkState.None : LocalNetworkState.CellularOnly;
        }
    }
}

public sealed class MauiHaptics : IHaptics
{
    public void Click()
    {
        try
        {
            if (HapticFeedback.Default.IsSupported)
            {
                HapticFeedback.Default.Perform(HapticFeedbackType.Click);
            }
        }
        catch (Exception)
        {
            // Haptics are a nicety; never fail an interaction because of them.
        }
    }
}
