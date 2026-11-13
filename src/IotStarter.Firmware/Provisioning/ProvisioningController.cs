using System;
using System.Collections;
using System.Device.Wifi;
using System.Text;
using System.Threading;
using IotStarter.Firmware.Diagnostics;
using IotStarter.Firmware.Hardware;
using IotStarter.Firmware.Network;
using IotStarter.Firmware.Storage;
using IotStarter.Shared;

namespace IotStarter.Firmware.Provisioning
{
    /// <summary>Why provisioning mode was entered.</summary>
    public enum ProvisioningKind
    {
        /// <summary>No credentials: anyone nearby may provision.</summary>
        Open = 0,

        /// <summary>Recovery window: credentials kept; new ones need the existing API token.</summary>
        Recovery = 1
    }

    /// <summary>
    /// Provisioning-mode state machine (docs/protocol.md §5). Commands arrive on the Bluetooth event
    /// thread; Identify and Cancel are handled there immediately, everything else is queued to a
    /// worker loop so long Wi-Fi operations never block the Bluetooth stack.
    /// </summary>
    public class ProvisioningController
    {
        private const int MaxQueuedMessages = 4;
        private const int LoopTickMs = 500;

        private readonly ProvisioningKind _kind;
        private readonly SettingsStore _settings;
        private readonly WifiCredentialStore _credentials;
        private readonly WifiService _wifi;
        private readonly StatusLed _led;
        private readonly byte[] _mac;
        private readonly ushort _capabilities;

        private readonly object _stateLock = new object();
        private readonly ArrayList _queue = new ArrayList();
        private readonly AutoResetEvent _queueSignal = new AutoResetEvent(false);
        private readonly CancelFlag _cancel = new CancelFlag();

        private BleProvisioningServer _server;
        private DeviceState _state = DeviceState.Ready;
        private ErrorCode _error = ErrorCode.None;

        public ProvisioningController(
            ProvisioningKind kind,
            SettingsStore settings,
            WifiCredentialStore credentials,
            WifiService wifi,
            StatusLed led,
            byte[] mac)
        {
            _kind = kind;
            _settings = settings;
            _credentials = credentials;
            _wifi = wifi;
            _led = led;
            _mac = mac;

            ushort caps = 0;
            if (FirmwareConfig.Strategy == ProvisioningStrategy.JoinWhileBleActive)
            {
                caps |= (ushort)Capability.CanScan;
            }
            else
            {
                caps |= (ushort)Capability.DeferredVerification;
            }

            if (kind == ProvisioningKind.Recovery)
            {
                caps |= (ushort)Capability.TokenRequired;
            }

            if (FirmwareConfig.EnableMdns)
            {
                caps |= (ushort)Capability.Mdns;
            }

            _capabilities = caps;

            // Report why the previous attempt failed (fallback strategy), until the next command.
            _error = settings.LastError;
        }

        /// <summary>Runs provisioning until the device restarts. Never returns normally.</summary>
        public void Run()
        {
            string name = DeviceIdentity.BleName(ProtocolConstants.BleNamePrefix, _mac);
            Log.Info("Provisioning mode (" + (_kind == ProvisioningKind.Open ? "open" : "recovery window") + ") as " + name);

            byte[] info = MessageCodec.EncodeInfo(
                FirmwareConfig.ModelId,
                _mac,
                FirmwareConfig.FirmwareMajor,
                FirmwareConfig.FirmwareMinor,
                FirmwareConfig.FirmwarePatch,
                _capabilities);
            byte[] manufacturer = MessageCodec.EncodeManufacturerPayload(FirmwareConfig.ModelId, _mac);

            Log.Memory("before BLE start");
            _server = new BleProvisioningServer(name, info, manufacturer, MessageCodec.EncodeState(_state, _error, null));
            _server.MessageReceived += OnMessageReceived;
            DeviceRuntime.SetBeforeRestart(ShutdownBle);
            _server.Start();
            Log.Memory("after BLE start");

            _led.Pattern = _kind == ProvisioningKind.Recovery ? LedPattern.RecoveryWindow : LedPattern.Provisioning;

            long windowEndsAt = DeviceRuntime.NowMs + ProtocolConstants.RecoveryWindowMs;
            long nextRetryAt = DeviceRuntime.NowMs + FirmwareConfig.RecoveryRetryIntervalMs;

            while (true)
            {
                byte[] message = Dequeue(LoopTickMs);
                if (message != null)
                {
                    Handle(message);
                }

                if (_kind != ProvisioningKind.Recovery)
                {
                    continue;
                }

                long now = DeviceRuntime.NowMs;
                if (CurrentState != DeviceState.Ready)
                {
                    continue;
                }

                if (_wifi.HasIPv4)
                {
                    LeaveRecoveryWindow("the network is back");
                }

                if (now >= windowEndsAt)
                {
                    LeaveRecoveryWindow("the recovery window ended");
                }

                if (FirmwareConfig.Strategy == ProvisioningStrategy.JoinWhileBleActive
                    && now >= nextRetryAt
                    && !_server.IsCentralConnected
                    && QueueIsEmpty)
                {
                    nextRetryAt = now + FirmwareConfig.RecoveryRetryIntervalMs;
                    Log.Info("Recovery window: retrying the stored network");
                    ErrorCode result = _wifi.Join(_credentials.Ssid, _credentials.Passphrase, WifiReconnectionKind.Automatic, null);
                    if (result == ErrorCode.None)
                    {
                        LeaveRecoveryWindow("the stored network is reachable again");
                    }
                }
            }
        }

        private DeviceState CurrentState
        {
            get
            {
                lock (_stateLock)
                {
                    return _state;
                }
            }
        }

        private bool QueueIsEmpty
        {
            get
            {
                lock (_queue)
                {
                    return _queue.Count == 0;
                }
            }
        }

        // ---- Bluetooth event thread ---------------------------------------------------------

        private void OnMessageReceived(byte[] message)
        {
            try
            {
                byte type = MessageCodec.MessageType(message);
                if (type == (byte)CommandType.Identify)
                {
                    _led.Identify();
                    _server.Send(MessageCodec.EncodeAck(type, ErrorCode.None));
                    return;
                }

                if (type == (byte)CommandType.Cancel)
                {
                    DeviceState state = CurrentState;
                    if (state == DeviceState.Scanning || state == DeviceState.Connecting)
                    {
                        _cancel.Set();
                        _server.Send(MessageCodec.EncodeAck(type, ErrorCode.None));
                    }
                    else
                    {
                        _server.Send(MessageCodec.EncodeAck(type, ErrorCode.InvalidCommand));
                    }

                    return;
                }

                lock (_queue)
                {
                    if (_queue.Count >= MaxQueuedMessages)
                    {
                        _server.Send(MessageCodec.EncodeAck(type, ErrorCode.Busy));
                        return;
                    }

                    _queue.Add(message);
                }

                _queueSignal.Set();
            }
            catch (Exception ex)
            {
                Log.Error("Provisioning: handling a BLE message failed", ex);
            }
        }

        // ---- Worker loop ----------------------------------------------------------------

        private byte[] Dequeue(int timeoutMs)
        {
            lock (_queue)
            {
                if (_queue.Count > 0)
                {
                    byte[] first = (byte[])_queue[0];
                    _queue.RemoveAt(0);
                    return first;
                }
            }

            _queueSignal.WaitOne(timeoutMs, false);

            lock (_queue)
            {
                if (_queue.Count > 0)
                {
                    byte[] first = (byte[])_queue[0];
                    _queue.RemoveAt(0);
                    return first;
                }
            }

            return null;
        }

        private void Handle(byte[] message)
        {
            byte type = MessageCodec.MessageType(message);
            try
            {
                switch (type)
                {
                    case (byte)CommandType.ScanNetworks:
                        HandleScan();
                        break;
                    case (byte)CommandType.SetCredentials:
                        HandleSetCredentials(MessageCodec.Payload(message));
                        break;
                    default:
                        _server.Send(MessageCodec.EncodeAck(type, ErrorCode.InvalidCommand));
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Provisioning: command " + type.ToString() + " failed", ex);
                SetState(DeviceState.Ready, ErrorCode.Internal, null);
                _server.Send(MessageCodec.EncodeAck(type, ErrorCode.Internal));
            }
            finally
            {
                // The payload may contain the passphrase and token.
                for (int i = 0; i < message.Length; i++)
                {
                    message[i] = 0;
                }
            }
        }

        private void HandleScan()
        {
            byte type = (byte)CommandType.ScanNetworks;
            if (FirmwareConfig.Strategy != ProvisioningStrategy.JoinWhileBleActive)
            {
                // Without CanScan the app does not ask; refuse cleanly if it does.
                _server.Send(MessageCodec.EncodeAck(type, ErrorCode.InvalidCommand));
                return;
            }

            if (CurrentState != DeviceState.Ready)
            {
                _server.Send(MessageCodec.EncodeAck(type, ErrorCode.Busy));
                return;
            }

            _server.Send(MessageCodec.EncodeAck(type, ErrorCode.None));
            _cancel.Clear();
            SetState(DeviceState.Scanning, ErrorCode.None, null);

            ScannedNetwork[] networks = _wifi.Scan(_cancel);
            if (networks == null)
            {
                _server.Send(MessageCodec.EncodeScanDone(0));
                SetState(DeviceState.Ready, _cancel.IsSet ? ErrorCode.Cancelled : ErrorCode.Timeout, null);
                return;
            }

            int sent = 0;
            for (int i = 0; i < networks.Length && sent < FirmwareConfig.MaxScanEntries; i++)
            {
                byte[] ssid = Encoding.UTF8.GetBytes(networks[i].Ssid);
                byte[] entry = MessageCodec.EncodeScanEntry(networks[i].Rssi, NetworkSecurity.Unknown, ssid);
                if (entry != null)
                {
                    _server.Send(entry);
                    sent++;
                }
            }

            _server.Send(MessageCodec.EncodeScanDone(sent));
            Log.Info("Provisioning: scan sent " + sent.ToString() + " networks");
            SetState(DeviceState.Ready, ErrorCode.None, null);
        }

        private void HandleSetCredentials(byte[] payload)
        {
            byte type = (byte)CommandType.SetCredentials;
            if (CurrentState != DeviceState.Ready)
            {
                _server.Send(MessageCodec.EncodeAck(type, ErrorCode.Busy));
                return;
            }

            CredentialsData credentials = MessageCodec.DecodeSetCredentials(payload);
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = 0;
            }

            if (credentials == null)
            {
                Log.Warn("Provisioning: malformed credentials rejected");
                _server.Send(MessageCodec.EncodeAck(type, ErrorCode.InvalidPayload));
                return;
            }

            try
            {
                if (_kind == ProvisioningKind.Recovery)
                {
                    byte[] stored = ByteHelpers.FromHex(_settings.TokenHex);
                    if (!ByteHelpers.ConstantTimeEquals(stored, credentials.Token))
                    {
                        Log.Warn("Provisioning: recovery window refused credentials with a wrong token");
                        _server.Send(MessageCodec.EncodeAck(type, ErrorCode.NotAuthorized));
                        SetState(DeviceState.Ready, ErrorCode.NotAuthorized, null);
                        return;
                    }
                }

                _server.Send(MessageCodec.EncodeAck(type, ErrorCode.None));

                // The SSID may be any bytes; nanoFramework's configuration API takes a string.
                string ssid = Encoding.UTF8.GetString(credentials.Ssid, 0, credentials.Ssid.Length);
                string passphrase = Encoding.UTF8.GetString(credentials.Passphrase, 0, credentials.Passphrase.Length);

                if (FirmwareConfig.Strategy == ProvisioningStrategy.VerifyAfterReboot)
                {
                    StoreAndRestartForVerification(ssid, passphrase, credentials.Token);
                    return;
                }

                JoinAndReport(ssid, passphrase, credentials.Token);
            }
            finally
            {
                credentials.Clear();
            }
        }

        private void JoinAndReport(string ssid, string passphrase, byte[] token)
        {
            _cancel.Clear();
            SetState(DeviceState.Connecting, ErrorCode.None, null);

            ErrorCode result = _wifi.Join(ssid, passphrase, WifiReconnectionKind.Manual, _cancel);
            if (result != ErrorCode.None)
            {
                Log.Info("Provisioning: join failed with error " + ((int)result).ToString());
                SetState(DeviceState.Ready, result, null);
                _server.Send(MessageCodec.EncodeProvisionResult(result, null));
                return;
            }

            bool stored = _credentials.Save(ssid, passphrase) && _settings.CompleteProvisioning(token, false);
            if (!stored)
            {
                _wifi.Disconnect();
                SetState(DeviceState.Ready, ErrorCode.StorageFailed, null);
                _server.Send(MessageCodec.EncodeProvisionResult(ErrorCode.StorageFailed, null));
                return;
            }

            byte[] ip = _wifi.CurrentIPv4();
            SetState(DeviceState.Connected, ErrorCode.None, ip);
            _server.Send(MessageCodec.EncodeProvisionResult(ErrorCode.None, ip));
            Log.Info("Provisioning: success, address " + ByteHelpers.FormatIPv4(ip));

            // Give the final notifications time to reach the phone, then restart with BLE off.
            Thread.Sleep(ProtocolConstants.FinalStatusDelayMs);
            DeviceRuntime.Restart("provisioned");
        }

        private void StoreAndRestartForVerification(string ssid, string passphrase, byte[] token)
        {
            bool stored = _credentials.Save(ssid, passphrase) && _settings.CompleteProvisioning(token, true);
            if (!stored)
            {
                SetState(DeviceState.Ready, ErrorCode.StorageFailed, null);
                _server.Send(MessageCodec.EncodeProvisionResult(ErrorCode.StorageFailed, null));
                return;
            }

            SetState(DeviceState.Restarting, ErrorCode.None, null);
            _server.Send(MessageCodec.EncodeProvisionResult(ErrorCode.None, null));
            Thread.Sleep(ProtocolConstants.FinalStatusDelayMs);
            DeviceRuntime.Restart("verifying credentials with BLE off");
        }

        private void LeaveRecoveryWindow(string reason)
        {
            Log.Info("Leaving the recovery window: " + reason);
            _settings.SetRecoveryWindow(false);
            DeviceRuntime.Restart("recovery window closed");
        }

        private void SetState(DeviceState state, ErrorCode error, byte[] ip)
        {
            lock (_stateLock)
            {
                _state = state;
                _error = error;
            }

            _server.UpdateState(MessageCodec.EncodeState(state, error, ip));
        }

        private void ShutdownBle()
        {
            if (_server != null)
            {
                _server.Shutdown();
            }
        }
    }
}
