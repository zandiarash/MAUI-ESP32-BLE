// Shared between firmware and app. Conservative C# subset only.
#nullable disable

namespace IotStarter.Shared
{
    /// <summary>Provisioning state reported by the State characteristic.</summary>
    public enum DeviceState : byte
    {
        /// <summary>Waiting for a command; the last error (if any) is in the error byte.</summary>
        Ready = 1,

        /// <summary>A Wi-Fi scan is running.</summary>
        Scanning = 2,

        /// <summary>Joining the Wi-Fi network with the received credentials.</summary>
        Connecting = 3,

        /// <summary>Joined; the IPv4 address is valid. The device reboots shortly.</summary>
        Connected = 4,

        /// <summary>Credentials stored unverified; the device reboots to join with BLE off.</summary>
        Restarting = 5
    }

    /// <summary>Error codes used over BLE and in the device's last-error record.</summary>
    public enum ErrorCode : byte
    {
        None = 0,
        InvalidFrame = 1,
        InvalidCommand = 2,
        InvalidPayload = 3,
        Busy = 4,
        NetworkNotFound = 5,
        WrongPassword = 6,
        NoIpAddress = 7,
        Timeout = 8,
        NotAuthorized = 9,
        StorageFailed = 10,
        UnsupportedSecurity = 11,
        PreviousAttemptFailed = 12,
        Cancelled = 13,
        Internal = 255
    }

    /// <summary>Message types written to the Command characteristic.</summary>
    public enum CommandType : byte
    {
        Identify = 0x01,
        ScanNetworks = 0x02,
        SetCredentials = 0x03,
        Cancel = 0x04
    }

    /// <summary>Message types notified on the Result characteristic.</summary>
    public enum ResultType : byte
    {
        Ack = 0x81,
        ScanEntry = 0x82,
        ScanDone = 0x83,
        ProvisionResult = 0x84
    }

    /// <summary>Security of a scanned network as far as the device can tell.</summary>
    public enum NetworkSecurity : byte
    {
        Open = 0,
        Secured = 1,

        /// <summary>The nanoFramework 1.x scan report does not expose the security type.</summary>
        Unknown = 2
    }

    /// <summary>Capability flags in the Info characteristic (bit field).</summary>
    public enum Capability : ushort
    {
        None = 0x0000,

        /// <summary>The device can scan for Wi-Fi networks while BLE is up.</summary>
        CanScan = 0x0001,

        /// <summary>Recovery window: SetCredentials must carry the existing API token.</summary>
        TokenRequired = 0x0002,

        /// <summary>Low-RAM strategy: credentials are verified after a reboot with BLE off.</summary>
        DeferredVerification = 0x0004,

        /// <summary>The device answers mDNS / DNS-SD queries on the network.</summary>
        Mdns = 0x0008,

        /// <summary>Scan entries carry real open/secured information.</summary>
        ScanReportsSecurity = 0x0010
    }

    /// <summary>Result of feeding a chunk into a <see cref="FrameAssembler"/>.</summary>
    public enum FrameStatus : byte
    {
        /// <summary>More chunks are needed.</summary>
        InProgress = 0,

        /// <summary>A complete message is ready.</summary>
        Complete = 1,

        /// <summary>The chunk was rejected and any partial message was discarded.</summary>
        Rejected = 2
    }
}
