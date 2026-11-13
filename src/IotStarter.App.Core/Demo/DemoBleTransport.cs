using System.Text;
using IotStarter.App.Core.Ble;
using IotStarter.Shared;

namespace IotStarter.App.Core.Demo;

/// <summary>
/// Simulated BLE: advertises the demo world's unprovisioned devices and runs the device side of the
/// provisioning protocol with the same shared codec as the firmware.
/// </summary>
public sealed class DemoBleTransport(DemoWorld world, TimeProvider time) : IBleTransport
{
    public BleAvailability Availability => BleAvailability.On;

    public event EventHandler? AvailabilityChanged
    {
        add { }
        remove { }
    }

    public async Task ScanAsync(Guid serviceUuid, Action<BleAdvertisement> onAdvertisement, CancellationToken cancellationToken)
    {
        var random = new Random();
        int round = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (DemoDevice device in world.Devices.Where(d => !d.Provisioned))
            {
                int rssi = -55 - (device.Mac[5] % 25) + random.Next(-3, 4);
                string name = DeviceIdentity.BleName(ProtocolConstants.BleNamePrefix, device.Mac);

                // Like a real phone: the name arrives first, the scan response (manufacturer data) a moment later.
                onAdvertisement(new BleAdvertisement(device.PeripheralId, name, rssi, null, null));
                if (round > 0)
                {
                    onAdvertisement(new BleAdvertisement(
                        device.PeripheralId,
                        null,
                        rssi,
                        ProtocolConstants.ManufacturerCompanyId,
                        MessageCodec.EncodeManufacturerPayload(device.ModelId, device.Mac)));
                }
            }

            round++;
            await Task.Delay(world.Scaled(700), time, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task<IBleConnection> ConnectAsync(string peripheralId, CancellationToken cancellationToken)
    {
        await Task.Delay(world.Scaled(600), time, cancellationToken).ConfigureAwait(false);
        DemoDevice device = world.FindByPeripheral(peripheralId) ?? throw new BleException("The device is out of range.");
        if (device.Provisioned)
        {
            throw new BleException("The device is no longer advertising.");
        }

        return new DemoBleConnection(world, device, time);
    }
}

/// <summary>Device side of one simulated GATT connection.</summary>
internal sealed class DemoBleConnection : IBleConnection
{
    private static readonly Guid InfoId = Guid.Parse(ProtocolConstants.InfoCharacteristicUuid);
    private static readonly Guid StateId = Guid.Parse(ProtocolConstants.StateCharacteristicUuid);
    private static readonly Guid CommandId = Guid.Parse(ProtocolConstants.CommandCharacteristicUuid);
    private static readonly Guid ResultId = Guid.Parse(ProtocolConstants.ResultCharacteristicUuid);

    private readonly DemoWorld _world;
    private readonly DemoDevice _device;
    private readonly TimeProvider _time;
    private readonly FrameAssembler _assembler = new();
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _closed = new();
    private Action<byte[]>? _onState;
    private Action<byte[]>? _onResult;
    private DeviceState _state = DeviceState.Ready;
    private ErrorCode _error = ErrorCode.None;
    private byte[]? _ip;
    private bool _pairingPromptShown;
    private bool _paired;
    private byte _txId;

    public DemoBleConnection(DemoWorld world, DemoDevice device, TimeProvider time)
    {
        _world = world;
        _device = device;
        _time = time;
    }

    public string PeripheralId => _device.PeripheralId;

    public bool IsConnected => !_closed.IsCancellationRequested;

    public int WriteChunkSize => ProtocolConstants.DefaultChunkSize;

    public event EventHandler? Disconnected;

    public async Task<byte[]> ReadAsync(Guid serviceUuid, Guid characteristicUuid, CancellationToken cancellationToken)
    {
        await Task.Delay(_world.Scaled(80), _time, cancellationToken).ConfigureAwait(false);
        if (characteristicUuid == InfoId)
        {
            ushort caps = (ushort)(Capability.CanScan | Capability.Mdns);
            return MessageCodec.EncodeInfo(_device.ModelId, _device.Mac, 1, 0, 0, caps);
        }

        if (characteristicUuid == StateId)
        {
            lock (_lock)
            {
                return MessageCodec.EncodeState(_state, _error, _ip);
            }
        }

        throw new BleException("Characteristic is not readable.");
    }

    public async Task SubscribeAsync(Guid serviceUuid, Guid characteristicUuid, Action<byte[]> onValue, CancellationToken cancellationToken)
    {
        await Task.Delay(_world.Scaled(60), _time, cancellationToken).ConfigureAwait(false);
        if (characteristicUuid == StateId)
        {
            _onState = onValue;
            return;
        }

        if (characteristicUuid == ResultId)
        {
            // Simulate the system pairing prompt: the first protected access fails, then succeeds.
            if (!_paired)
            {
                if (!_pairingPromptShown)
                {
                    _pairingPromptShown = true;
                    throw new BleException("Insufficient encryption (pairing requested).");
                }

                _paired = true;
            }

            _onResult = onValue;
            return;
        }

        throw new BleException("Characteristic does not notify.");
    }

    public async Task WriteAsync(Guid serviceUuid, Guid characteristicUuid, byte[] value, CancellationToken cancellationToken)
    {
        if (characteristicUuid != CommandId)
        {
            throw new BleException("Characteristic is not writable.");
        }

        if (!_paired)
        {
            throw new BleException("Insufficient encryption.");
        }

        await Task.Delay(_world.Scaled(15), _time, cancellationToken).ConfigureAwait(false);

        byte[]? message = null;
        lock (_lock)
        {
            FrameStatus status = _assembler.Accept((byte[])value.Clone(), _time.GetTimestamp() * 1000 / _time.TimestampFrequency);
            if (status == FrameStatus.Complete)
            {
                message = _assembler.TakeMessage();
            }
            else if (status == FrameStatus.Rejected)
            {
                Send(MessageCodec.EncodeAck(0, _assembler.LastError));
            }
        }

        if (message is not null)
        {
            _ = HandleAsync(message);
        }
    }

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }

    private async Task HandleAsync(byte[] message)
    {
        try
        {
            if (MessageCodec.Validate(message) != ErrorCode.None)
            {
                Send(MessageCodec.EncodeAck(0, ErrorCode.InvalidFrame));
                return;
            }

            var type = (CommandType)MessageCodec.MessageType(message);
            byte[] payload = MessageCodec.Payload(message);
            CancellationToken ct = _closed.Token;

            switch (type)
            {
                case CommandType.Identify:
                    Send(MessageCodec.EncodeAck((byte)type, ErrorCode.None));
                    break;

                case CommandType.ScanNetworks:
                    Send(MessageCodec.EncodeAck((byte)type, ErrorCode.None));
                    SetState(DeviceState.Scanning, ErrorCode.None);
                    await Task.Delay(_world.Scaled(1400), _time, ct).ConfigureAwait(false);
                    foreach (DemoNetwork network in _world.Networks)
                    {
                        Send(MessageCodec.EncodeScanEntry(network.Rssi, NetworkSecurity.Unknown, Encoding.UTF8.GetBytes(network.Ssid)));
                    }

                    Send(MessageCodec.EncodeScanDone(_world.Networks.Count));
                    SetState(DeviceState.Ready, ErrorCode.None);
                    break;

                case CommandType.SetCredentials:
                    CredentialsData? credentials = MessageCodec.DecodeSetCredentials(payload);
                    if (credentials is null)
                    {
                        Send(MessageCodec.EncodeAck((byte)type, ErrorCode.InvalidPayload));
                        return;
                    }

                    Send(MessageCodec.EncodeAck((byte)type, ErrorCode.None));
                    SetState(DeviceState.Connecting, ErrorCode.None);
                    await Task.Delay(_world.Scaled(2500), _time, ct).ConfigureAwait(false);

                    string ssid = Encoding.UTF8.GetString(credentials.Ssid);
                    string password = Encoding.ASCII.GetString(credentials.Passphrase);
                    string token = Convert.ToHexStringLower(credentials.Token);
                    credentials.Clear();

                    DemoNetwork? target = _world.Networks.FirstOrDefault(n => n.Ssid == ssid);
                    ErrorCode result = target is null ? ErrorCode.NetworkNotFound
                        : target.Password != password ? ErrorCode.WrongPassword
                        : ErrorCode.None;

                    if (result != ErrorCode.None)
                    {
                        SetState(DeviceState.Ready, result);
                        Send(MessageCodec.EncodeProvisionResult(result, null));
                        return;
                    }

                    _world.Provision(_device, token);
                    byte[] ip = ByteHelpers.ParseIPv4(_device.IPv4);
                    lock (_lock)
                    {
                        _ip = ip;
                    }

                    SetState(DeviceState.Connected, ErrorCode.None);
                    Send(MessageCodec.EncodeProvisionResult(ErrorCode.None, ip));

                    // The real device restarts with BLE off after 1.5 s.
                    await Task.Delay(_world.Scaled(ProtocolConstants.FinalStatusDelayMs), _time, ct).ConfigureAwait(false);
                    Close();
                    break;

                case CommandType.Cancel:
                    Send(MessageCodec.EncodeAck((byte)type, ErrorCode.InvalidCommand));
                    break;

                default:
                    Send(MessageCodec.EncodeAck((byte)type, ErrorCode.InvalidCommand));
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Connection closed.
        }
    }

    private void SetState(DeviceState state, ErrorCode error)
    {
        byte[] value;
        lock (_lock)
        {
            _state = state;
            _error = error;
            value = MessageCodec.EncodeState(state, error, _ip);
        }

        _onState?.Invoke(value);
    }

    private void Send(byte[] message)
    {
        byte[][] chunks = FrameSplitter.Split(message, _txId++, ProtocolConstants.DefaultChunkSize);
        foreach (byte[] chunk in chunks)
        {
            _onResult?.Invoke(chunk);
        }
    }

    private void Close()
    {
        if (_closed.IsCancellationRequested)
        {
            return;
        }

        _closed.Cancel();
        Disconnected?.Invoke(this, EventArgs.Empty);
    }
}
