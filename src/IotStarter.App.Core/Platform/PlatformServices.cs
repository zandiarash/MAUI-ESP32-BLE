namespace IotStarter.App.Core.Platform;

/// <summary>Result of a permission check.</summary>
public enum PermissionState
{
    Granted,
    Denied,

    /// <summary>Denied and the system will not ask again: the user must change it in Settings.</summary>
    DeniedPermanently,

    /// <summary>The platform has no such permission (for example Windows).</summary>
    NotRequired
}

/// <summary>
/// Runtime permissions needed for BLE scanning: BLUETOOTH_SCAN and BLUETOOTH_CONNECT on Android 12+,
/// location on Android 11 and older, Bluetooth authorisation on iOS.
/// </summary>
public interface IPermissionService
{
    Task<PermissionState> CheckBluetoothAsync();

    Task<PermissionState> RequestBluetoothAsync();

    /// <summary>Opens this app's page in the system settings.</summary>
    void OpenAppSettings();

    /// <summary>Opens the system Bluetooth settings where the platform allows it.</summary>
    void OpenBluetoothSettings();
}

/// <summary>What the phone is connected to.</summary>
public enum LocalNetworkState
{
    /// <summary>Connected over Wi-Fi or Ethernet: local devices may be reachable.</summary>
    LocalNetwork,

    /// <summary>Only mobile data: local devices are not reachable.</summary>
    CellularOnly,

    None
}

public interface INetworkStatus
{
    LocalNetworkState State { get; }

    /// <summary>Raised when the phone changes network.</summary>
    event EventHandler? Changed;
}

public interface IHaptics
{
    /// <summary>A short tick for a switch change. Must never throw.</summary>
    void Click();
}

/// <summary>Theme choice in Settings.</summary>
public enum ThemePreference
{
    System,
    Light,
    Dark
}

/// <summary>Simple persistent key/value store (MAUI Preferences on devices).</summary>
public interface IKeyValueStore
{
    string? Get(string key);

    void Set(string key, string value);
}
