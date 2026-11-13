namespace IotStarter.App.Core.Ble;

/// <summary>State of the phone's Bluetooth radio and the app's access to it.</summary>
public enum BleAvailability
{
    Unknown,
    Unsupported,
    Unauthorized,
    Off,
    On
}

/// <summary>One advertisement (merged with its scan response when the platform delivers both).</summary>
/// <param name="PeripheralId">Platform peripheral identifier (opaque; not the device ID).</param>
/// <param name="LocalName">Advertised local name, if any.</param>
/// <param name="Rssi">Signal strength in dBm.</param>
/// <param name="CompanyId">Company ID of the manufacturer data, if present.</param>
/// <param name="ManufacturerPayload">Manufacturer data after the 2-byte company ID.</param>
public sealed record BleAdvertisement(
    string PeripheralId,
    string? LocalName,
    int Rssi,
    ushort? CompanyId,
    byte[]? ManufacturerPayload);

/// <summary>
/// The thin platform BLE layer (Plugin.BLE on devices, a simulator in demo mode). Everything
/// protocol-specific lives above it, in <see cref="Provisioning.ProvisioningSession"/>.
/// </summary>
public interface IBleTransport
{
    BleAvailability Availability { get; }

    event EventHandler? AvailabilityChanged;

    /// <summary>Scans for peripherals advertising <paramref name="serviceUuid"/> until cancelled.</summary>
    Task ScanAsync(Guid serviceUuid, Action<BleAdvertisement> onAdvertisement, CancellationToken cancellationToken);

    /// <summary>Connects to a peripheral seen in a scan. Throws <see cref="BleException"/> on failure.</summary>
    Task<IBleConnection> ConnectAsync(string peripheralId, CancellationToken cancellationToken);
}

/// <summary>An open GATT connection.</summary>
public interface IBleConnection : IAsyncDisposable
{
    string PeripheralId { get; }

    bool IsConnected { get; }

    /// <summary>Largest write the link accepts (negotiated MTU − 3, at least 20).</summary>
    int WriteChunkSize { get; }

    event EventHandler? Disconnected;

    Task<byte[]> ReadAsync(Guid serviceUuid, Guid characteristicUuid, CancellationToken cancellationToken);

    /// <summary>Writes with response. Throws <see cref="BleException"/>, for example while pairing is pending.</summary>
    Task WriteAsync(Guid serviceUuid, Guid characteristicUuid, byte[] value, CancellationToken cancellationToken);

    Task SubscribeAsync(Guid serviceUuid, Guid characteristicUuid, Action<byte[]> onValue, CancellationToken cancellationToken);
}

/// <summary>A BLE operation failed.</summary>
public sealed class BleException(string message, Exception? inner = null) : Exception(message, inner);
