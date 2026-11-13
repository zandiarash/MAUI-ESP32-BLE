using IotStarter.App.Core.Ble;
using IotStarter.App.Core.Devices;
using IotStarter.Shared;

namespace IotStarter.App.Core.Provisioning;

/// <summary>Why the wizard cannot start scanning.</summary>
public enum BlockReason
{
    BluetoothOff,
    BluetoothUnsupported,
    PermissionDenied,

    /// <summary>The user must grant the permission in the system settings.</summary>
    PermissionDeniedPermanently
}

/// <summary>Sub-step while opening the BLE session.</summary>
public enum ConnectPhase
{
    Connecting,

    /// <summary>Waiting for the system pairing prompt to be accepted.</summary>
    Pairing
}

/// <summary>Progress of the device-side Wi-Fi scan.</summary>
public enum NetworkScanStatus
{
    Scanning,
    Done,
    Failed,

    /// <summary>The device cannot scan (low-RAM strategy); the user types the SSID.</summary>
    Unsupported
}

/// <summary>Specific failure reasons, each with its own message and advice in the UI.</summary>
public enum FailureKind
{
    ConnectFailed,
    PairingFailed,
    UnsupportedDevice,
    DeviceDisconnected,
    WrongPassword,
    NetworkNotFound,
    NoIpAddress,
    WifiTimeout,
    UnsupportedSecurity,

    /// <summary>Recovery window: the device refused this phone's token.</summary>
    NotAuthorized,

    /// <summary>Recovery window: this phone has no token for the device.</summary>
    NotOwner,

    DeviceError,

    /// <summary>Joined Wi-Fi, but the phone cannot reach it (different network or client isolation).</summary>
    Unreachable
}

/// <summary>Where "Try again" goes.</summary>
public enum RetryTarget
{
    /// <summary>Back to the device list.</summary>
    Scan,

    /// <summary>Back to the network step, keeping what was typed.</summary>
    Credentials,

    /// <summary>Look for the provisioned device on the network again.</summary>
    Handover
}

public sealed record WizardFailure(FailureKind Kind, RetryTarget Retry);

/// <summary>What the user typed. Lives only in memory for the wizard's lifetime (for retries).</summary>
public sealed record CredentialDraft(string Ssid, string Password, bool IsHidden)
{
    public static CredentialDraft Empty { get; } = new(string.Empty, string.Empty, false);

    // Keep the password out of logs and debugger displays.
    public override string ToString() => $"CredentialDraft {{ Ssid = {Ssid}, IsHidden = {IsHidden} }}";
}

// ---- States ---------------------------------------------------------------------------------------

public sealed record WizardChecking;

public sealed record WizardBlocked(BlockReason Reason);

public sealed record WizardScanning(IReadOnlyList<DiscoveredDevice> Devices);

public sealed record WizardConnecting(DiscoveredDevice Device, ConnectPhase Phase);

public sealed record WizardChoosingNetwork(
    DiscoveredDevice Device,
    DeviceInfo Info,
    IReadOnlyList<WifiNetwork> Networks,
    NetworkScanStatus ScanStatus,
    CredentialDraft Draft,
    WizardFailure? PreviousFailure);

/// <param name="DeviceState">Latest state notified by the device (drives the progress display).</param>
public sealed record WizardProvisioning(DiscoveredDevice Device, string Ssid, DeviceState DeviceState, bool CancelRequested);

/// <summary>Provisioned: waiting for the device to appear on the local network.</summary>
public sealed record WizardRejoining(DeviceInfo Info, string? ReportedIPv4);

public sealed record WizardNaming(DeviceRecord Record, string SuggestedName);

public sealed record WizardDone(string DeviceId);

public sealed record WizardFailed(WizardFailure Failure, DiscoveredDevice? Device);

/// <summary>The add-device wizard's state (closed set; the UI switches over it exhaustively).</summary>
public union WizardState(
    WizardChecking,
    WizardBlocked,
    WizardScanning,
    WizardConnecting,
    WizardChoosingNetwork,
    WizardProvisioning,
    WizardRejoining,
    WizardNaming,
    WizardDone,
    WizardFailed);
