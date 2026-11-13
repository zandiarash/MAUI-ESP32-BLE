using System.Text;
using IotStarter.App.Core.Ble;
using IotStarter.Shared;
using Microsoft.Extensions.Logging;

namespace IotStarter.App.Core.Provisioning;

/// <summary>
/// Client side of the BLE provisioning protocol over one connection: reads Info and State,
/// subscribes to notifications, sends chunked commands and reassembles chunked results.
/// The passphrase and token are never logged.
/// </summary>
public sealed class ProvisioningSession : IAsyncDisposable
{
    private static readonly Guid ServiceId = Guid.Parse(ProtocolConstants.ServiceUuid);
    private static readonly Guid InfoId = Guid.Parse(ProtocolConstants.InfoCharacteristicUuid);
    private static readonly Guid StateId = Guid.Parse(ProtocolConstants.StateCharacteristicUuid);
    private static readonly Guid CommandId = Guid.Parse(ProtocolConstants.CommandCharacteristicUuid);
    private static readonly Guid ResultId = Guid.Parse(ProtocolConstants.ResultCharacteristicUuid);

    /// <summary>How long writes are retried while the phone shows (or completes) its pairing prompt.</summary>
    internal static readonly TimeSpan PairingWindow = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan PairingRetryDelay = TimeSpan.FromMilliseconds(1500);
    internal static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(25);
    internal static readonly TimeSpan ProvisionTimeout = TimeSpan.FromMilliseconds(ProtocolConstants.AppProvisioningTimeoutMs);

    private readonly IBleConnection _connection;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _rxLock = new();
    private readonly FrameAssembler _assembler = new();
    private readonly Lock _waitersLock = new();
    private readonly Dictionary<byte, TaskCompletionSource<ErrorCode>> _ackWaiters = [];
    private readonly TaskCompletionSource _disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<ProvisionOutcome>? _provisionWaiter;
    private ScanCollector? _scan;
    private byte _nextMessageId;
    private bool _paired;

    private ProvisioningSession(IBleConnection connection, DeviceInfo info, DeviceStatus status, TimeProvider time, ILogger logger)
    {
        _connection = connection;
        _time = time;
        _logger = logger;
        Info = info;
        Status = status;
        _connection.Disconnected += OnDisconnected;
    }

    public DeviceInfo Info { get; }

    public DeviceStatus Status { get; private set; }

    public bool IsConnected => _connection.IsConnected && !_disconnected.Task.IsCompleted;

    /// <summary>Raised (on a background thread) whenever the State characteristic changes.</summary>
    public event Action<DeviceStatus>? StatusChanged;

    /// <summary>Raised once when the BLE link drops.</summary>
    public event Action? Disconnected;

    /// <summary>
    /// Connects, reads Info and State, and subscribes to State. Result is subscribed lazily by the
    /// first command so that the pairing prompt appears only when the user acts.
    /// </summary>
    public static async Task<ProvisioningSession> OpenAsync(
        IBleTransport transport,
        string peripheralId,
        TimeProvider time,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        IBleConnection connection = await transport.ConnectAsync(peripheralId, cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] infoBytes = await connection.ReadAsync(ServiceId, InfoId, cancellationToken).ConfigureAwait(false);
            DeviceInfo info = DeviceInfo.FromBytes(infoBytes)
                ?? throw new ProvisioningProtocolException("The Info characteristic is malformed.");
            if (info.ProtocolVersion != ProtocolConstants.ProtocolVersion)
            {
                throw new ProvisioningProtocolException($"Unsupported protocol version {info.ProtocolVersion}.");
            }

            byte[] stateBytes = await connection.ReadAsync(ServiceId, StateId, cancellationToken).ConfigureAwait(false);
            DeviceStatus status = DeviceStatus.FromBytes(stateBytes)
                ?? throw new ProvisioningProtocolException("The State characteristic is malformed.");

            var session = new ProvisioningSession(connection, info, status, time, logger);
            await connection.SubscribeAsync(ServiceId, StateId, session.OnStateValue, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Connected to device {DeviceId} (model {Model}, fw {Firmware})", info.DeviceId, info.ModelId, info.FirmwareVersion);
            return session;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Subscribes to Result, which requires an encrypted link: this is where the phone pairs
    /// (Just Works). Retries while the user answers the system pairing prompt.
    /// </summary>
    public async Task EnsurePairedAsync(CancellationToken cancellationToken)
    {
        if (_paired)
        {
            return;
        }

        await RetryWhilePairingAsync(
            ct => _connection.SubscribeAsync(ServiceId, ResultId, OnResultValue, ct),
            cancellationToken).ConfigureAwait(false);
        _paired = true;
    }

    public async Task IdentifyAsync(CancellationToken cancellationToken)
    {
        ErrorCode error = await SendCommandAsync(MessageCodec.EncodeCommand(CommandType.Identify), CommandType.Identify, cancellationToken)
            .ConfigureAwait(false);
        ThrowIfRefused(CommandType.Identify, error);
    }

    /// <summary>Asks the device to scan. Returns networks strongest first.</summary>
    public async Task<IReadOnlyList<WifiNetwork>> ScanNetworksAsync(CancellationToken cancellationToken)
    {
        var collector = new ScanCollector();
        lock (_waitersLock)
        {
            _scan = collector;
        }

        try
        {
            ErrorCode error = await SendCommandAsync(MessageCodec.EncodeCommand(CommandType.ScanNetworks), CommandType.ScanNetworks, cancellationToken)
                .ConfigureAwait(false);
            ThrowIfRefused(CommandType.ScanNetworks, error);

            Task finished;
            try
            {
                finished = await Task.WhenAny(collector.Done.Task, _disconnected.Task)
                    .WaitAsync(ScanTimeout, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException("The device did not finish its Wi-Fi scan in time.");
            }

            if (finished == _disconnected.Task)
            {
                throw new ProvisioningProtocolException("The device disconnected during the scan.");
            }

            List<WifiNetwork> networks;
            lock (_waitersLock)
            {
                networks = [.. collector.Networks];
            }

            return networks
                .GroupBy(n => n.Ssid, StringComparer.Ordinal)
                .Select(g => g.MaxBy(n => n.Rssi)!)
                .OrderByDescending(n => n.Rssi)
                .ToList();
        }
        finally
        {
            lock (_waitersLock)
            {
                if (ReferenceEquals(_scan, collector))
                {
                    _scan = null;
                }
            }
        }
    }

    /// <summary>
    /// Sends the credentials and waits for the device's final result. A refused command is reported
    /// as <see cref="ProvisionFailed"/> rather than thrown.
    /// </summary>
    public async Task<ProvisionOutcome> ProvisionAsync(string ssid, string passphrase, byte[] token, CancellationToken cancellationToken)
    {
        byte[] ssidBytes = Encoding.UTF8.GetBytes(ssid);
        byte[] passBytes = Encoding.ASCII.GetBytes(passphrase);
        byte[]? message = MessageCodec.EncodeSetCredentials(ssidBytes, passBytes, token);
        Array.Clear(passBytes);
        if (message is null)
        {
            throw new ArgumentException("The credentials do not meet the protocol rules.");
        }

        var waiter = new TaskCompletionSource<ProvisionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waitersLock)
        {
            _provisionWaiter = waiter;
        }

        try
        {
            ErrorCode ack = await SendCommandAsync(message, CommandType.SetCredentials, cancellationToken).ConfigureAwait(false);
            if (ack != ErrorCode.None)
            {
                return new ProvisionFailed(ack);
            }

            Task? finished = null;
            try
            {
                finished = await Task.WhenAny(waiter.Task, _disconnected.Task)
                    .WaitAsync(ProvisionTimeout, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Fall through to the last State we saw.
            }

            if (finished == waiter.Task)
            {
                return await waiter.Task.ConfigureAwait(false);
            }

            // Lost link or no final result: use the last State we saw, if it is conclusive.
            bool lostLink = finished == _disconnected.Task;
            return Status switch
            {
                { State: DeviceState.Connected, IPv4: { } ip } => new ProvisionSucceeded(ip),
                { State: DeviceState.Restarting } => new ProvisionRestarting(),
                { State: DeviceState.Ready, Error: not ErrorCode.None } s => new ProvisionFailed(s.Error),
                _ when lostLink => throw new ProvisioningProtocolException("The device disconnected before reporting a result."),
                _ => throw new TimeoutException("The device did not report a result in time.")
            };
        }
        finally
        {
            Array.Clear(message);
            lock (_waitersLock)
            {
                if (ReferenceEquals(_provisionWaiter, waiter))
                {
                    _provisionWaiter = null;
                }
            }
        }
    }

    /// <summary>Asks the device to abandon a running scan or join.</summary>
    public async Task CancelAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            return;
        }

        await SendCommandAsync(MessageCodec.EncodeCommand(CommandType.Cancel), CommandType.Cancel, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _connection.Disconnected -= OnDisconnected;
        await _connection.DisposeAsync().ConfigureAwait(false);
        _disconnected.TrySetResult();
        FailAllWaiters();
    }

    // ---- Sending ----------------------------------------------------------------------------

    private async Task<ErrorCode> SendCommandAsync(byte[] message, CommandType command, CancellationToken cancellationToken)
    {
        await EnsurePairedAsync(cancellationToken).ConfigureAwait(false);

        var ack = new TaskCompletionSource<ErrorCode>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waitersLock)
        {
            _ackWaiters[(byte)command] = ack;
        }

        try
        {
            byte messageId = unchecked(_nextMessageId++);
            byte[][] chunks = FrameSplitter.Split(message, messageId, Math.Clamp(_connection.WriteChunkSize, ProtocolConstants.DefaultChunkSize, ProtocolConstants.MaxChunkSize))
                ?? throw new ArgumentException("Message too large for the protocol.");

            await RetryWhilePairingAsync(
                async ct =>
                {
                    foreach (byte[] chunk in chunks)
                    {
                        await _connection.WriteAsync(ServiceId, CommandId, chunk, ct).ConfigureAwait(false);
                    }
                },
                cancellationToken).ConfigureAwait(false);

            foreach (byte[] chunk in chunks)
            {
                Array.Clear(chunk);
            }

            Task finished;
            try
            {
                finished = await Task.WhenAny(ack.Task, _disconnected.Task)
                    .WaitAsync(AckTimeout, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"The device did not acknowledge {command}.");
            }

            if (finished == ack.Task)
            {
                return await ack.Task.ConfigureAwait(false);
            }

            if (finished == _disconnected.Task)
            {
                throw new ProvisioningProtocolException("The device disconnected.");
            }

            throw new TimeoutException($"The device did not acknowledge {command}.");
        }
        finally
        {
            lock (_waitersLock)
            {
                if (_ackWaiters.TryGetValue((byte)command, out var current) && ReferenceEquals(current, ack))
                {
                    _ackWaiters.Remove((byte)command);
                }
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="operation"/>, retrying while the link is up and the pairing window lasts:
    /// the first access to an encrypted characteristic fails until the user accepts pairing.
    /// </summary>
    private async Task RetryWhilePairingAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        long started = _time.GetTimestamp();
        int attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                await operation(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (BleException ex) when (IsConnected && _time.GetElapsedTime(started) < PairingWindow)
            {
                _logger.LogInformation("Encrypted access failed (attempt {Attempt}), waiting for pairing: {Message}", attempt, ex.Message);
                await Task.Delay(PairingRetryDelay, _time, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void ThrowIfRefused(CommandType command, ErrorCode error)
    {
        if (error != ErrorCode.None)
        {
            throw new ProvisioningCommandException(command, error);
        }
    }

    // ---- Receiving (BLE callback threads) ---------------------------------------------------

    private void OnStateValue(byte[] value)
    {
        DeviceStatus? status = DeviceStatus.FromBytes(value);
        if (status is null)
        {
            _logger.LogWarning("Ignored a malformed State notification");
            return;
        }

        Status = status;
        StatusChanged?.Invoke(status);
    }

    private void OnResultValue(byte[] chunk)
    {
        byte[]? message = null;
        lock (_rxLock)
        {
            FrameStatus frame = _assembler.Accept(chunk, _time.GetTimestamp() * 1000 / _time.TimestampFrequency);
            if (frame == FrameStatus.Complete)
            {
                message = _assembler.TakeMessage();
            }
            else if (frame == FrameStatus.Rejected)
            {
                _logger.LogWarning("Dropped a malformed result frame ({Error})", _assembler.LastError);
            }
        }

        if (message is null)
        {
            return;
        }

        if (MessageCodec.Validate(message) != ErrorCode.None)
        {
            _logger.LogWarning("Dropped a result message with a bad CRC");
            return;
        }

        Dispatch((ResultType)MessageCodec.MessageType(message), MessageCodec.Payload(message));
    }

    private void Dispatch(ResultType type, byte[] payload)
    {
        switch (type)
        {
            case ResultType.Ack when MessageCodec.DecodeAck(payload, out byte command, out ErrorCode error):
                if (command == 0)
                {
                    _logger.LogWarning("The device rejected a frame: {Error}", error);
                    FailAllAcks(error);
                    return;
                }

                TaskCompletionSource<ErrorCode>? ack;
                lock (_waitersLock)
                {
                    _ackWaiters.TryGetValue(command, out ack);
                }

                ack?.TrySetResult(error);
                break;

            case ResultType.ScanEntry when MessageCodec.DecodeScanEntry(payload) is { } entry:
                string ssid = Encoding.UTF8.GetString(entry.Ssid);
                lock (_waitersLock)
                {
                    _scan?.Networks.Add(new WifiNetwork(ssid, entry.Rssi, entry.Security));
                }

                break;

            case ResultType.ScanDone when MessageCodec.DecodeScanDone(payload, out _):
                lock (_waitersLock)
                {
                    _scan?.Done.TrySetResult();
                }

                break;

            case ResultType.ProvisionResult when MessageCodec.DecodeProvisionResult(payload, out ErrorCode result, out byte[] ip):
                ProvisionOutcome outcome = result != ErrorCode.None
                    ? new ProvisionFailed(result)
                    : ByteHelpers.IsZeroIPv4(ip)
                        ? new ProvisionRestarting()
                        : new ProvisionSucceeded(ByteHelpers.FormatIPv4(ip));
                lock (_waitersLock)
                {
                    _provisionWaiter?.TrySetResult(outcome);
                }

                break;

            default:
                _logger.LogWarning("Ignored an unknown or malformed result of type {Type}", type);
                break;
        }
    }

    private void FailAllAcks(ErrorCode error)
    {
        lock (_waitersLock)
        {
            foreach (var waiter in _ackWaiters.Values)
            {
                waiter.TrySetResult(error);
            }
        }
    }

    private void FailAllWaiters()
    {
        lock (_waitersLock)
        {
            foreach (var waiter in _ackWaiters.Values)
            {
                waiter.TrySetCanceled();
            }

            _scan?.Done.TrySetCanceled();
        }
    }

    private void OnDisconnected(object? sender, EventArgs e)
    {
        if (_disconnected.TrySetResult())
        {
            _logger.LogInformation("BLE link to {DeviceId} dropped", Info.DeviceId);
            Disconnected?.Invoke();
        }
    }

    private sealed class ScanCollector
    {
        public List<WifiNetwork> Networks { get; } = [];

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
