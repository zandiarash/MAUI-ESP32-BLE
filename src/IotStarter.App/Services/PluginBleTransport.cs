using System.Collections.Concurrent;
using IotStarter.App.Core.Ble;
using Microsoft.Extensions.Logging;
using Plugin.BLE;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using Plugin.BLE.Abstractions.Exceptions;

namespace IotStarter.App.Services;

/// <summary>
/// <see cref="IBleTransport"/> on Plugin.BLE 3.2.1. The package ships net9.0 platform assets that
/// .NET 11 consumes; everything Plugin.BLE-specific stays in this file so it can be replaced.
/// </summary>
public sealed class PluginBleTransport : IBleTransport
{
    private const int RequestedMtu = 247;

    private readonly IBluetoothLE _ble = CrossBluetoothLE.Current;
    private readonly ConcurrentDictionary<string, IDevice> _seen = new(StringComparer.Ordinal);
    private readonly ILogger<PluginBleTransport> _logger;

    public PluginBleTransport(ILogger<PluginBleTransport> logger)
    {
        _logger = logger;
        _ble.StateChanged += (_, _) => AvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? AvailabilityChanged;

    public BleAvailability Availability => _ble.State switch
    {
        BluetoothState.On => BleAvailability.On,
        BluetoothState.Off or BluetoothState.TurningOff or BluetoothState.TurningOn => BleAvailability.Off,
        BluetoothState.Unauthorized => BleAvailability.Unauthorized,
        BluetoothState.Unavailable => BleAvailability.Unsupported,
        _ => _ble.IsAvailable ? BleAvailability.Unknown : BleAvailability.Unsupported
    };

    public async Task ScanAsync(Guid serviceUuid, Action<BleAdvertisement> onAdvertisement, CancellationToken cancellationToken)
    {
        IAdapter adapter = _ble.Adapter;
        void OnAdvertised(object? sender, DeviceEventArgs e)
        {
            IDevice device = e.Device;
            string id = device.Id.ToString();
            _seen[id] = device;
            onAdvertisement(ToAdvertisement(id, device));
        }

        adapter.ScanMode = ScanMode.LowLatency;
        adapter.ScanTimeout = (int)TimeSpan.FromMinutes(5).TotalMilliseconds;
        adapter.DeviceAdvertised += OnAdvertised;
        try
        {
            // The plugin stops after ScanTimeout; keep scanning until the caller cancels.
            while (!cancellationToken.IsCancellationRequested)
            {
                var filter = new ScanFilterOptions { ServiceUuids = [serviceUuid] };
                await adapter.StartScanningForDevicesAsync(filter, deviceFilter: null, allowDuplicatesKey: true, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new BleException("Scanning failed.", ex);
        }
        finally
        {
            adapter.DeviceAdvertised -= OnAdvertised;
            if (adapter.IsScanning)
            {
                try
                {
                    await adapter.StopScanningForDevicesAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogInformation("Stopping the scan failed: {Message}", ex.Message);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task<IBleConnection> ConnectAsync(string peripheralId, CancellationToken cancellationToken)
    {
        IAdapter adapter = _ble.Adapter;
        var parameters = new ConnectParameters(autoConnect: false, forceBleTransport: true);
        IDevice device;
        try
        {
            if (_seen.TryGetValue(peripheralId, out IDevice? known))
            {
                await adapter.ConnectToDeviceAsync(known, parameters, cancellationToken).ConfigureAwait(false);
                device = known;
            }
            else
            {
                device = await adapter.ConnectToKnownDeviceAsync(Guid.Parse(peripheralId), parameters, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (DeviceConnectionException ex)
        {
            throw new BleException("Could not connect to the device.", ex);
        }

        int chunkSize = 20;
        try
        {
            // Ask for a larger MTU where the platform allows it; never depend on it.
            int mtu = await device.RequestMtuAsync(RequestedMtu, cancellationToken).ConfigureAwait(false);
            chunkSize = Math.Max(20, mtu - 3);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation("MTU request not supported: {Message}", ex.Message);
        }

        return new PluginBleConnection(adapter, device, chunkSize, _logger);
    }

    private static BleAdvertisement ToAdvertisement(string id, IDevice device)
    {
        ushort? companyId = null;
        byte[]? payload = null;
        foreach (AdvertisementRecord record in device.AdvertisementRecords ?? [])
        {
            if (record.Type == AdvertisementRecordType.ManufacturerSpecificData && record.Data is { Length: >= 2 } data)
            {
                companyId = (ushort)(data[0] | (data[1] << 8));
                payload = data[2..];
            }
        }

        return new BleAdvertisement(id, device.Name, device.Rssi, companyId, payload);
    }
}

/// <summary>One GATT connection through Plugin.BLE.</summary>
internal sealed class PluginBleConnection : IBleConnection
{
    private readonly IAdapter _adapter;
    private readonly IDevice _device;
    private readonly ILogger _logger;
    private readonly Dictionary<Guid, ICharacteristic> _characteristics = [];
    private readonly List<(ICharacteristic Characteristic, EventHandler<CharacteristicUpdatedEventArgs> Handler)> _subscriptions = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _connected = true;

    public PluginBleConnection(IAdapter adapter, IDevice device, int chunkSize, ILogger logger)
    {
        _adapter = adapter;
        _device = device;
        _logger = logger;
        WriteChunkSize = chunkSize;
        _adapter.DeviceDisconnected += OnDeviceGone;
        _adapter.DeviceConnectionLost += OnDeviceLost;
    }

    public string PeripheralId => _device.Id.ToString();

    public bool IsConnected => _connected;

    public int WriteChunkSize { get; }

    public event EventHandler? Disconnected;

    public async Task<byte[]> ReadAsync(Guid serviceUuid, Guid characteristicUuid, CancellationToken cancellationToken)
    {
        ICharacteristic characteristic = await GetAsync(serviceUuid, characteristicUuid, cancellationToken).ConfigureAwait(false);
        try
        {
            (byte[] data, int resultCode) = await characteristic.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (resultCode != 0)
            {
                throw new BleException($"Read failed with GATT status {resultCode}.");
            }

            return data;
        }
        catch (Exception ex) when (ex is not (BleException or OperationCanceledException))
        {
            throw new BleException("Read failed.", ex);
        }
    }

    public async Task WriteAsync(Guid serviceUuid, Guid characteristicUuid, byte[] value, CancellationToken cancellationToken)
    {
        ICharacteristic characteristic = await GetAsync(serviceUuid, characteristicUuid, cancellationToken).ConfigureAwait(false);
        try
        {
            characteristic.WriteType = CharacteristicWriteType.WithResponse;
            int resultCode = await characteristic.WriteAsync(value, cancellationToken).ConfigureAwait(false);
            if (resultCode != 0)
            {
                // Typically 5/15 (insufficient authentication/encryption) until pairing completes.
                throw new BleException($"Write failed with GATT status {resultCode}.");
            }
        }
        catch (Exception ex) when (ex is not (BleException or OperationCanceledException))
        {
            throw new BleException("Write failed.", ex);
        }
    }

    public async Task SubscribeAsync(Guid serviceUuid, Guid characteristicUuid, Action<byte[]> onValue, CancellationToken cancellationToken)
    {
        ICharacteristic characteristic = await GetAsync(serviceUuid, characteristicUuid, cancellationToken).ConfigureAwait(false);
        EventHandler<CharacteristicUpdatedEventArgs> handler = (_, e) =>
        {
            byte[]? value = e.Characteristic.Value;
            if (value is not null)
            {
                onValue((byte[])value.Clone());
            }
        };

        characteristic.ValueUpdated += handler;
        try
        {
            await characteristic.StartUpdatesAsync(cancellationToken).ConfigureAwait(false);
            _subscriptions.Add((characteristic, handler));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            characteristic.ValueUpdated -= handler;
            throw new BleException("Subscribing failed.", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _adapter.DeviceDisconnected -= OnDeviceGone;
        _adapter.DeviceConnectionLost -= OnDeviceLost;
        foreach ((ICharacteristic characteristic, EventHandler<CharacteristicUpdatedEventArgs> handler) in _subscriptions)
        {
            characteristic.ValueUpdated -= handler;
        }

        _subscriptions.Clear();
        if (_connected)
        {
            _connected = false;
            try
            {
                await _adapter.DisconnectDeviceAsync(_device, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogInformation("Disconnect failed: {Message}", ex.Message);
            }
        }
    }

    private async Task<ICharacteristic> GetAsync(Guid serviceUuid, Guid characteristicUuid, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_characteristics.TryGetValue(characteristicUuid, out ICharacteristic? cached))
            {
                return cached;
            }

            IService service = await _device.GetServiceAsync(serviceUuid, cancellationToken).ConfigureAwait(false)
                ?? throw new BleException("The provisioning service is missing.");
            ICharacteristic characteristic = await service.GetCharacteristicAsync(characteristicUuid, cancellationToken).ConfigureAwait(false)
                ?? throw new BleException("A provisioning characteristic is missing.");
            _characteristics[characteristicUuid] = characteristic;
            return characteristic;
        }
        catch (Exception ex) when (ex is not (BleException or OperationCanceledException))
        {
            throw new BleException("Service discovery failed.", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnDeviceGone(object? sender, DeviceEventArgs e) => HandleGone(e.Device);

    private void OnDeviceLost(object? sender, DeviceErrorEventArgs e) => HandleGone(e.Device);

    private void HandleGone(IDevice device)
    {
        if (device.Id != _device.Id || !_connected)
        {
            return;
        }

        _connected = false;
        Disconnected?.Invoke(this, EventArgs.Empty);
    }
}
