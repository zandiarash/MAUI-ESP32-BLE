using System;
using System.Threading;
using IotStarter.Firmware.Diagnostics;
using IotStarter.Shared;
using nanoFramework.Device.Bluetooth;
using nanoFramework.Device.Bluetooth.Advertisement;
using nanoFramework.Device.Bluetooth.GenericAttributeProfile;

namespace IotStarter.Firmware.Provisioning
{
    /// <summary>Raised on the Bluetooth event thread when a complete, CRC-valid message arrives.</summary>
    public delegate void BleMessageHandler(byte[] message);

    /// <summary>
    /// GATT server for the provisioning service (docs/protocol.md §2-4).
    /// Based on the nanoFramework samples ImprovWifi (service and advertising set-up) and
    /// BluetoothLESample2 (protected characteristics and pairing events).
    /// </summary>
    public class BleProvisioningServer
    {
        private const int NotifyPacingMs = 15;

        private readonly object _rxLock = new object();
        private readonly object _txLock = new object();
        private readonly object _stateLock = new object();
        private readonly FrameAssembler _assembler = new FrameAssembler();
        private readonly string _deviceName;
        private readonly byte[] _infoValue;
        private readonly byte[] _manufacturerPayload;

        private GattServiceProvider _provider;
        private GattLocalCharacteristic _stateCharacteristic;
        private GattLocalCharacteristic _resultCharacteristic;
        private byte[] _stateValue;
        private byte _txMessageId;
        private bool _centralConnected;
        private bool _shutDown;
        private Timer _readvertiseTimer;

        public BleProvisioningServer(string deviceName, byte[] infoValue, byte[] manufacturerPayload, byte[] initialState)
        {
            _deviceName = deviceName;
            _infoValue = infoValue;
            _manufacturerPayload = manufacturerPayload;
            _stateValue = initialState;
        }

        /// <summary>Complete messages written to the Command characteristic.</summary>
        public event BleMessageHandler MessageReceived;

        public bool IsCentralConnected
        {
            get
            {
                lock (_stateLock)
                {
                    return _centralConnected;
                }
            }
        }

        /// <summary>Creates the GATT service and starts advertising.</summary>
        public void Start()
        {
            BluetoothLEServer server = BluetoothLEServer.Instance;
            server.DeviceName = _deviceName;

            // Just Works pairing (no display, no keyboard) and no bonding: a factory-reset device must
            // not be locked out by keys left on the phone, and nothing is stored on the device.
            server.Pairing.IOCapabilities = DevicePairingIOCapabilities.NoInputNoOutput;
            server.Pairing.AllowBonding = false;
            server.Pairing.PairingComplete += OnPairingComplete;
            server.Session.SessionStatusChanged += OnSessionStatusChanged;

            GattServiceProviderResult providerResult = GattServiceProvider.Create(new Guid(ProtocolConstants.ServiceUuid));
            if (providerResult.Error != BluetoothError.Success)
            {
                throw new InvalidOperationException("GATT service creation failed: " + ((int)providerResult.Error).ToString());
            }

            _provider = providerResult.ServiceProvider;
            GattLocalService service = _provider.Service;

            GattLocalCharacteristic info = CreateCharacteristic(
                service,
                ProtocolConstants.InfoCharacteristicUuid,
                GattCharacteristicProperties.Read,
                GattProtectionLevel.Plain,
                GattProtectionLevel.Plain,
                "Info");
            info.ReadRequested += OnInfoReadRequested;

            _stateCharacteristic = CreateCharacteristic(
                service,
                ProtocolConstants.StateCharacteristicUuid,
                GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
                GattProtectionLevel.Plain,
                GattProtectionLevel.Plain,
                "State");
            _stateCharacteristic.ReadRequested += OnStateReadRequested;

            GattLocalCharacteristic command = CreateCharacteristic(
                service,
                ProtocolConstants.CommandCharacteristicUuid,
                GattCharacteristicProperties.Write,
                GattProtectionLevel.EncryptionRequired,
                GattProtectionLevel.EncryptionRequired,
                "Command");
            command.WriteRequested += OnCommandWriteRequested;

            // Notify-only. The protection levels are also meant to cover the subscription (CCCD) write,
            // which makes the phone pair when it subscribes. Results are only produced after an
            // encrypted Command write, so they always travel on an encrypted link.
            _resultCharacteristic = CreateCharacteristic(
                service,
                ProtocolConstants.ResultCharacteristicUuid,
                GattCharacteristicProperties.Notify,
                GattProtectionLevel.EncryptionRequired,
                GattProtectionLevel.EncryptionRequired,
                "Result");

            StartAdvertising();
        }

        /// <summary>Updates the State value and notifies subscribers.</summary>
        public void UpdateState(byte[] stateValue)
        {
            lock (_stateLock)
            {
                _stateValue = stateValue;
            }

            if (_stateCharacteristic == null || IsShutDown)
            {
                return;
            }

            try
            {
                _stateCharacteristic.NotifyValue(new Buffer(stateValue));
            }
            catch (Exception ex)
            {
                Log.Error("BLE: state notify failed", ex);
            }
        }

        /// <summary>Sends a framed message on the Result characteristic, chunked to fit the MTU.</summary>
        public void Send(byte[] message)
        {
            if (_resultCharacteristic == null || IsShutDown)
            {
                return;
            }

            lock (_txLock)
            {
                byte[][] chunks = FrameSplitter.Split(message, _txMessageId++, CurrentChunkSize());
                if (chunks == null)
                {
                    Log.Warn("BLE: message too large to send");
                    return;
                }

                for (int i = 0; i < chunks.Length; i++)
                {
                    try
                    {
                        _resultCharacteristic.NotifyValue(new Buffer(chunks[i]));
                    }
                    catch (Exception ex)
                    {
                        Log.Error("BLE: result notify failed", ex);
                        return;
                    }

                    Thread.Sleep(NotifyPacingMs);
                }
            }
        }

        /// <summary>Stops advertising and disposes the BLE server so the stack is fully released.</summary>
        public void Shutdown()
        {
            lock (_stateLock)
            {
                if (_shutDown)
                {
                    return;
                }

                _shutDown = true;
            }

            try
            {
                if (_readvertiseTimer != null)
                {
                    _readvertiseTimer.Dispose();
                }

                if (_provider != null)
                {
                    _provider.StopAdvertising();
                }

                BluetoothLEServer.Instance.Dispose();
                Log.Info("BLE: stopped and disposed");
            }
            catch (Exception ex)
            {
                Log.Error("BLE: shutdown failed", ex);
            }
        }

        private bool IsShutDown
        {
            get
            {
                lock (_stateLock)
                {
                    return _shutDown;
                }
            }
        }

        private void StartAdvertising()
        {
            GattServiceProviderAdvertisingParameters parameters = new GattServiceProviderAdvertisingParameters();
            parameters.IsConnectable = true;
            parameters.IsDiscoverable = true;

            // Custom layout (docs/protocol.md §2): Flags + 128-bit UUID + name fill the 31-byte ADV
            // packet; the library always appends manufacturer data last, which then lands in the
            // scan response.
            parameters.CustomAdvertisement = true;
            BluetoothLEAdvertisement advertisement = parameters.Advertisement;
            advertisement.Flags = BluetoothLEAdvertisementFlags.GeneralDiscoverableMode | BluetoothLEAdvertisementFlags.ClassicNotSupported;

            DataWriter uuidWriter = new DataWriter();
            uuidWriter.WriteUuid(new Guid(ProtocolConstants.ServiceUuid));
            advertisement.DataSections.Add(new BluetoothLEAdvertisementDataSection(
                (byte)BluetoothLEAdvertisementDataSectionType.CompleteList128uuid,
                uuidWriter.DetachBuffer()));

            advertisement.LocalName = _deviceName;
            advertisement.ManufacturerData.Add(new BluetoothLEManufacturerData(ProtocolConstants.ManufacturerCompanyId, new Buffer(_manufacturerPayload)));

            _provider.StartAdvertising(parameters);
            Log.Info("BLE: advertising as '" + _deviceName + "' (status " + ((int)_provider.AdvertisementStatus).ToString() + ")");
        }

        private static GattLocalCharacteristic CreateCharacteristic(
            GattLocalService service,
            string uuid,
            GattCharacteristicProperties properties,
            GattProtectionLevel readProtection,
            GattProtectionLevel writeProtection,
            string description)
        {
            GattLocalCharacteristicParameters parameters = new GattLocalCharacteristicParameters();
            parameters.CharacteristicProperties = properties;
            parameters.ReadProtectionLevel = readProtection;
            parameters.WriteProtectionLevel = writeProtection;
            parameters.UserDescription = description;

            GattLocalCharacteristicResult result = service.CreateCharacteristic(new Guid(uuid), parameters);
            if (result.Error != BluetoothError.Success)
            {
                throw new InvalidOperationException("Characteristic '" + description + "' failed: " + ((int)result.Error).ToString());
            }

            return result.Characteristic;
        }

        private int CurrentChunkSize()
        {
            try
            {
                GattSubscribedClient[] clients = _resultCharacteristic.SubscribedClients;
                if (clients != null && clients.Length > 0)
                {
                    int size = clients[0].MaxNotificationSize;
                    if (size > ProtocolConstants.MaxChunkSize)
                    {
                        size = ProtocolConstants.MaxChunkSize;
                    }

                    if (size >= ProtocolConstants.DefaultChunkSize)
                    {
                        return size;
                    }
                }
            }
            catch (Exception)
            {
                // Fall back to the default ATT MTU.
            }

            return ProtocolConstants.DefaultChunkSize;
        }

        private void OnInfoReadRequested(GattLocalCharacteristic sender, GattReadRequestedEventArgs args)
        {
            try
            {
                args.GetRequest().RespondWithValue(new Buffer(_infoValue));
            }
            catch (Exception ex)
            {
                Log.Error("BLE: info read failed", ex);
            }
        }

        private void OnStateReadRequested(GattLocalCharacteristic sender, GattReadRequestedEventArgs args)
        {
            try
            {
                byte[] value;
                lock (_stateLock)
                {
                    value = _stateValue;
                }

                args.GetRequest().RespondWithValue(new Buffer(value));
            }
            catch (Exception ex)
            {
                Log.Error("BLE: state read failed", ex);
            }
        }

        private void OnCommandWriteRequested(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
        {
            byte[] complete = null;
            ErrorCode frameError = ErrorCode.None;

            try
            {
                GattWriteRequest request = args.GetRequest();
                Buffer value = request.Value;
                byte[] chunk = new byte[value.Length];
                DataReader reader = DataReader.FromBuffer(value);
                reader.ReadBytes(chunk);

                lock (_rxLock)
                {
                    FrameStatus status = _assembler.Accept(chunk, DeviceRuntime.NowMs);
                    if (status == FrameStatus.Complete)
                    {
                        complete = _assembler.TakeMessage();
                    }
                    else if (status == FrameStatus.Rejected)
                    {
                        frameError = _assembler.LastError;
                    }
                }

                // Wipe the raw chunk: it may contain part of the passphrase.
                for (int i = 0; i < chunk.Length; i++)
                {
                    chunk[i] = 0;
                }

                if (request.Option == GattWriteOption.WriteWithResponse)
                {
                    request.Respond();
                }
            }
            catch (Exception ex)
            {
                Log.Error("BLE: command write failed", ex);
                frameError = ErrorCode.InvalidFrame;
            }

            if (frameError != ErrorCode.None)
            {
                Log.Warn("BLE: rejected frame, error " + ((int)frameError).ToString());
                Send(MessageCodec.EncodeAck(0, frameError));
                return;
            }

            if (complete == null)
            {
                return;
            }

            ErrorCode envelope = MessageCodec.Validate(complete);
            if (envelope != ErrorCode.None)
            {
                Log.Warn("BLE: message failed CRC/size check");
                Send(MessageCodec.EncodeAck(0, envelope));
                return;
            }

            BleMessageHandler handler = MessageReceived;
            if (handler != null)
            {
                handler(complete);
            }
        }

        private void OnSessionStatusChanged(object sender, GattSessionStatusChangedEventArgs args)
        {
            bool connected = args.Status == GattSessionStatus.Active;
            lock (_stateLock)
            {
                _centralConnected = connected;
            }

            Log.Info(connected ? "BLE: phone connected" : "BLE: phone disconnected");

            if (!connected)
            {
                lock (_rxLock)
                {
                    _assembler.Reset();
                }

                // Make sure the device is discoverable again after a disconnect.
                if (_readvertiseTimer != null)
                {
                    _readvertiseTimer.Dispose();
                }

                _readvertiseTimer = new Timer(EnsureAdvertising, null, 500, Timeout.Infinite);
            }
        }

        private void EnsureAdvertising(object state)
        {
            if (IsShutDown || IsCentralConnected || _provider == null)
            {
                return;
            }

            try
            {
                GattServiceProviderAdvertisementStatus status = _provider.AdvertisementStatus;
                if (status != GattServiceProviderAdvertisementStatus.Started
                    && status != GattServiceProviderAdvertisementStatus.StartedWithoutAllAdvertisementData)
                {
                    Log.Info("BLE: restarting advertising");
                    StartAdvertising();
                }
            }
            catch (Exception ex)
            {
                Log.Error("BLE: restarting advertising failed", ex);
            }
        }

        private void OnPairingComplete(object sender, DevicePairingEventArgs args)
        {
            DevicePairing pairing = sender as DevicePairing;
            Log.Info("BLE: pairing complete, status " + ((int)args.Status).ToString()
                + (pairing != null ? ", paired=" + (pairing.IsPaired ? "yes" : "no") : string.Empty));
        }
    }
}
