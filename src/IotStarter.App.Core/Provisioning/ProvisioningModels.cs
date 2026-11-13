using IotStarter.Shared;

namespace IotStarter.App.Core.Provisioning;

/// <summary>Decoded Info characteristic.</summary>
public sealed record DeviceInfo(
    byte ProtocolVersion,
    ushort ModelId,
    string DeviceId,
    string FirmwareVersion,
    Capability Capabilities)
{
    public bool Has(Capability capability) => (Capabilities & capability) == capability;

    public ModelDefinition? Model => ModelCatalog.Find(ModelId);

    internal static DeviceInfo? FromBytes(byte[] value)
    {
        DeviceInfoData? data = MessageCodec.DecodeInfo(value);
        if (data is null)
        {
            return null;
        }

        return new DeviceInfo(
            data.ProtocolVersion,
            data.ModelId,
            DeviceIdentity.FormatDeviceId(data.DeviceId),
            $"{data.FirmwareMajor}.{data.FirmwareMinor}.{data.FirmwarePatch}",
            (Capability)data.Capabilities);
    }
}

/// <summary>Decoded State characteristic.</summary>
public sealed record DeviceStatus(DeviceState State, ErrorCode Error, string? IPv4)
{
    internal static DeviceStatus? FromBytes(byte[] value)
    {
        DeviceStateData? data = MessageCodec.DecodeState(value);
        if (data is null)
        {
            return null;
        }

        string? ip = ByteHelpers.IsZeroIPv4(data.IPv4) ? null : ByteHelpers.FormatIPv4(data.IPv4);
        return new DeviceStatus(data.State, data.Error, ip);
    }
}

/// <summary>A network found by the device's own scan.</summary>
public sealed record WifiNetwork(string Ssid, int Rssi, NetworkSecurity Security)
{
    /// <summary>0..4 bars from RSSI (dBm).</summary>
    public int SignalBars => Rssi switch
    {
        >= -55 => 4,
        >= -67 => 3,
        >= -75 => 2,
        >= -85 => 1,
        _ => 0
    };
}

/// <summary>The device joined the network and reported its address.</summary>
public sealed record ProvisionSucceeded(string IPv4);

/// <summary>Fallback strategy: credentials stored unverified; the device is restarting.</summary>
public sealed record ProvisionRestarting;

/// <summary>The device reported a specific failure and stays in provisioning mode.</summary>
public sealed record ProvisionFailed(ErrorCode Error);

/// <summary>Outcome of a SetCredentials exchange.</summary>
public union ProvisionOutcome(ProvisionSucceeded, ProvisionRestarting, ProvisionFailed);

/// <summary>A protocol-level failure (bad data, unsupported version, unexpected disconnect).</summary>
public sealed class ProvisioningProtocolException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The device answered a command with an error acknowledgement.</summary>
public sealed class ProvisioningCommandException(CommandType command, ErrorCode error)
    : Exception($"Command {command} was refused with {error}")
{
    public CommandType Command { get; } = command;

    public ErrorCode Error { get; } = error;
}

/// <summary>Problems with credentials typed by the user (checked before anything is sent).</summary>
public enum CredentialProblem
{
    None,
    SsidMissing,
    SsidTooLong,
    PasswordTooShort,
    PasswordTooLong,
    PasswordInvalidCharacters
}

public static class CredentialRules
{
    /// <summary>Same rules as the device (docs/protocol.md §4.3). An empty password means an open network.</summary>
    public static CredentialProblem Validate(string ssid, string password)
    {
        if (string.IsNullOrEmpty(ssid))
        {
            return CredentialProblem.SsidMissing;
        }

        if (System.Text.Encoding.UTF8.GetByteCount(ssid) > ProtocolConstants.MaxSsidBytes)
        {
            return CredentialProblem.SsidTooLong;
        }

        if (password.Length == 0)
        {
            return CredentialProblem.None;
        }

        if (password.Length < ProtocolConstants.MinPassphraseLength)
        {
            return CredentialProblem.PasswordTooShort;
        }

        if (password.Length > ProtocolConstants.MaxPassphraseLength)
        {
            return CredentialProblem.PasswordTooLong;
        }

        foreach (char c in password)
        {
            if (c < 0x20 || c > 0x7E)
            {
                return CredentialProblem.PasswordInvalidCharacters;
            }
        }

        return CredentialProblem.None;
    }
}
