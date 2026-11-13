// Shared between firmware and app. Conservative C# subset only.
#nullable disable

namespace IotStarter.Shared
{
    /// <summary>Decoded Info characteristic.</summary>
    public class DeviceInfoData
    {
        public byte ProtocolVersion;
        public ushort ModelId;
        public byte[] DeviceId;
        public byte FirmwareMajor;
        public byte FirmwareMinor;
        public byte FirmwarePatch;
        public ushort Capabilities;
    }

    /// <summary>Decoded State characteristic.</summary>
    public class DeviceStateData
    {
        public DeviceState State;
        public ErrorCode Error;
        public byte[] IPv4;
    }

    /// <summary>Decoded identity block from the advertisement's manufacturer data.</summary>
    public class AdvertisedIdentity
    {
        public byte ProtocolVersion;
        public ushort ModelId;
        public byte[] ShortId;
    }

    /// <summary>Decoded SetCredentials command. Callers must clear the arrays after use.</summary>
    public class CredentialsData
    {
        public byte[] Ssid;
        public byte[] Passphrase;
        public byte[] Token;

        /// <summary>Overwrites the secret buffers with zeros.</summary>
        public void Clear()
        {
            Wipe(Passphrase);
            Wipe(Token);
        }

        private static void Wipe(byte[] data)
        {
            if (data == null)
            {
                return;
            }

            for (int i = 0; i < data.Length; i++)
            {
                data[i] = 0;
            }
        }
    }

    /// <summary>Decoded ScanEntry result.</summary>
    public class ScanEntryData
    {
        public int Rssi;
        public NetworkSecurity Security;
        public byte[] Ssid;
    }

    /// <summary>
    /// Encoders and decoders for the BLE provisioning protocol. Multi-byte integers are little-endian.
    /// Framed message layout: [type][payload...][crc16 LE over type+payload].
    /// Decoders never throw on malformed input; they return null or an <see cref="ErrorCode"/>.
    /// </summary>
    public static class MessageCodec
    {
        // ---- Message envelope -----------------------------------------------------------

        public static byte[] Encode(byte type, byte[] payload)
        {
            int payloadLength = payload == null ? 0 : payload.Length;
            byte[] message = new byte[1 + payloadLength + 2];
            message[0] = type;
            if (payloadLength > 0)
            {
                ByteHelpers.Copy(payload, 0, message, 1, payloadLength);
            }

            ushort crc = ByteHelpers.Crc16(message, 0, 1 + payloadLength);
            ByteHelpers.WriteUInt16(message, 1 + payloadLength, crc);
            return message;
        }

        /// <summary>Checks size and CRC. Returns <see cref="ErrorCode.None"/> when the envelope is valid.</summary>
        public static ErrorCode Validate(byte[] message)
        {
            if (message == null || message.Length < ProtocolConstants.MessageOverhead || message.Length > ProtocolConstants.MaxMessageSize)
            {
                return ErrorCode.InvalidFrame;
            }

            int bodyLength = message.Length - 2;
            ushort expected = ByteHelpers.Crc16(message, 0, bodyLength);
            ushort actual = ByteHelpers.ReadUInt16(message, bodyLength);
            return expected == actual ? ErrorCode.None : ErrorCode.InvalidFrame;
        }

        /// <summary>Type byte of a validated message.</summary>
        public static byte MessageType(byte[] message)
        {
            return message[0];
        }

        /// <summary>Payload of a validated message (empty array if none).</summary>
        public static byte[] Payload(byte[] message)
        {
            return ByteHelpers.Slice(message, 1, message.Length - ProtocolConstants.MessageOverhead);
        }

        // ---- Commands -------------------------------------------------------------------

        public static byte[] EncodeCommand(CommandType command)
        {
            return Encode((byte)command, null);
        }

        /// <summary>
        /// Checks credential rules: SSID 1..32 bytes; passphrase empty (open) or 8..63 printable ASCII;
        /// token exactly 32 bytes and not all zero.
        /// </summary>
        public static ErrorCode ValidateCredentials(byte[] ssid, byte[] passphrase, byte[] token)
        {
            if (ssid == null || ssid.Length == 0 || ssid.Length > ProtocolConstants.MaxSsidBytes)
            {
                return ErrorCode.InvalidPayload;
            }

            if (passphrase == null)
            {
                return ErrorCode.InvalidPayload;
            }

            if (passphrase.Length != 0)
            {
                if (passphrase.Length < ProtocolConstants.MinPassphraseLength || passphrase.Length > ProtocolConstants.MaxPassphraseLength)
                {
                    return ErrorCode.InvalidPayload;
                }

                if (!ByteHelpers.IsPrintableAscii(passphrase))
                {
                    return ErrorCode.InvalidPayload;
                }
            }

            if (token == null || token.Length != ProtocolConstants.TokenBytes)
            {
                return ErrorCode.InvalidPayload;
            }

            int any = 0;
            for (int i = 0; i < token.Length; i++)
            {
                any |= token[i];
            }

            return any == 0 ? ErrorCode.InvalidPayload : ErrorCode.None;
        }

        /// <summary>SetCredentials payload: ssidLen, ssid, passLen, pass, token[32]. Returns null if invalid.</summary>
        public static byte[] EncodeSetCredentials(byte[] ssid, byte[] passphrase, byte[] token)
        {
            if (ValidateCredentials(ssid, passphrase, token) != ErrorCode.None)
            {
                return null;
            }

            byte[] payload = new byte[1 + ssid.Length + 1 + passphrase.Length + token.Length];
            int offset = 0;
            payload[offset++] = (byte)ssid.Length;
            ByteHelpers.Copy(ssid, 0, payload, offset, ssid.Length);
            offset += ssid.Length;
            payload[offset++] = (byte)passphrase.Length;
            ByteHelpers.Copy(passphrase, 0, payload, offset, passphrase.Length);
            offset += passphrase.Length;
            ByteHelpers.Copy(token, 0, payload, offset, token.Length);

            byte[] message = Encode((byte)CommandType.SetCredentials, payload);
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = 0;
            }

            return message;
        }

        /// <summary>Decodes and validates a SetCredentials payload. Returns null when malformed.</summary>
        public static CredentialsData DecodeSetCredentials(byte[] payload)
        {
            if (payload == null || payload.Length < 2)
            {
                return null;
            }

            int offset = 0;
            int ssidLength = payload[offset++];
            if (offset + ssidLength + 1 > payload.Length)
            {
                return null;
            }

            byte[] ssid = ByteHelpers.Slice(payload, offset, ssidLength);
            offset += ssidLength;
            int passLength = payload[offset++];
            if (offset + passLength + ProtocolConstants.TokenBytes != payload.Length)
            {
                return null;
            }

            byte[] passphrase = ByteHelpers.Slice(payload, offset, passLength);
            offset += passLength;
            byte[] token = ByteHelpers.Slice(payload, offset, ProtocolConstants.TokenBytes);

            CredentialsData data = new CredentialsData();
            data.Ssid = ssid;
            data.Passphrase = passphrase;
            data.Token = token;

            if (ValidateCredentials(ssid, passphrase, token) != ErrorCode.None)
            {
                data.Clear();
                return null;
            }

            return data;
        }

        // ---- Results --------------------------------------------------------------------

        public static byte[] EncodeAck(byte command, ErrorCode error)
        {
            return Encode((byte)ResultType.Ack, new byte[] { command, (byte)error });
        }

        public static bool DecodeAck(byte[] payload, out byte command, out ErrorCode error)
        {
            command = 0;
            error = ErrorCode.None;
            if (payload == null || payload.Length != 2)
            {
                return false;
            }

            command = payload[0];
            error = (ErrorCode)payload[1];
            return true;
        }

        /// <summary>ScanEntry payload: rssi(i8), security(1), ssidLen(1), ssid.</summary>
        public static byte[] EncodeScanEntry(int rssi, NetworkSecurity security, byte[] ssid)
        {
            if (ssid == null || ssid.Length == 0 || ssid.Length > ProtocolConstants.MaxSsidBytes)
            {
                return null;
            }

            if (rssi < -128)
            {
                rssi = -128;
            }
            else if (rssi > 127)
            {
                rssi = 127;
            }

            byte[] payload = new byte[3 + ssid.Length];
            payload[0] = (byte)(sbyte)rssi;
            payload[1] = (byte)security;
            payload[2] = (byte)ssid.Length;
            ByteHelpers.Copy(ssid, 0, payload, 3, ssid.Length);
            return Encode((byte)ResultType.ScanEntry, payload);
        }

        public static ScanEntryData DecodeScanEntry(byte[] payload)
        {
            if (payload == null || payload.Length < 4)
            {
                return null;
            }

            int ssidLength = payload[2];
            if (ssidLength == 0 || ssidLength > ProtocolConstants.MaxSsidBytes || payload.Length != 3 + ssidLength)
            {
                return null;
            }

            ScanEntryData entry = new ScanEntryData();
            entry.Rssi = (sbyte)payload[0];
            entry.Security = (NetworkSecurity)payload[1];
            entry.Ssid = ByteHelpers.Slice(payload, 3, ssidLength);
            return entry;
        }

        public static byte[] EncodeScanDone(int count)
        {
            if (count < 0)
            {
                count = 0;
            }
            else if (count > 255)
            {
                count = 255;
            }

            return Encode((byte)ResultType.ScanDone, new byte[] { (byte)count });
        }

        public static bool DecodeScanDone(byte[] payload, out int count)
        {
            count = 0;
            if (payload == null || payload.Length != 1)
            {
                return false;
            }

            count = payload[0];
            return true;
        }

        /// <summary>ProvisionResult payload: error(1), ipv4(4).</summary>
        public static byte[] EncodeProvisionResult(ErrorCode error, byte[] ipv4)
        {
            byte[] payload = new byte[5];
            payload[0] = (byte)error;
            if (ipv4 != null && ipv4.Length == 4)
            {
                ByteHelpers.Copy(ipv4, 0, payload, 1, 4);
            }

            return Encode((byte)ResultType.ProvisionResult, payload);
        }

        public static bool DecodeProvisionResult(byte[] payload, out ErrorCode error, out byte[] ipv4)
        {
            error = ErrorCode.None;
            ipv4 = null;
            if (payload == null || payload.Length != 5)
            {
                return false;
            }

            error = (ErrorCode)payload[0];
            ipv4 = ByteHelpers.Slice(payload, 1, 4);
            return true;
        }

        // ---- Fixed-layout characteristics -----------------------------------------------

        public static byte[] EncodeInfo(ushort modelId, byte[] deviceId, byte fwMajor, byte fwMinor, byte fwPatch, ushort capabilities)
        {
            byte[] data = new byte[ProtocolConstants.InfoLength];
            data[0] = ProtocolConstants.ProtocolVersion;
            ByteHelpers.WriteUInt16(data, 1, modelId);
            ByteHelpers.Copy(deviceId, 0, data, 3, ProtocolConstants.DeviceIdBytes);
            data[9] = fwMajor;
            data[10] = fwMinor;
            data[11] = fwPatch;
            ByteHelpers.WriteUInt16(data, 12, capabilities);
            return data;
        }

        /// <summary>Decodes Info. Longer values are accepted so later versions can append fields.</summary>
        public static DeviceInfoData DecodeInfo(byte[] data)
        {
            if (data == null || data.Length < ProtocolConstants.InfoLength)
            {
                return null;
            }

            DeviceInfoData info = new DeviceInfoData();
            info.ProtocolVersion = data[0];
            info.ModelId = ByteHelpers.ReadUInt16(data, 1);
            info.DeviceId = ByteHelpers.Slice(data, 3, ProtocolConstants.DeviceIdBytes);
            info.FirmwareMajor = data[9];
            info.FirmwareMinor = data[10];
            info.FirmwarePatch = data[11];
            info.Capabilities = ByteHelpers.ReadUInt16(data, 12);
            return info;
        }

        public static byte[] EncodeState(DeviceState state, ErrorCode error, byte[] ipv4)
        {
            byte[] data = new byte[ProtocolConstants.StateLength];
            data[0] = (byte)state;
            data[1] = (byte)error;
            if (ipv4 != null && ipv4.Length == 4)
            {
                ByteHelpers.Copy(ipv4, 0, data, 2, 4);
            }

            return data;
        }

        public static DeviceStateData DecodeState(byte[] data)
        {
            if (data == null || data.Length < ProtocolConstants.StateLength)
            {
                return null;
            }

            DeviceStateData state = new DeviceStateData();
            state.State = (DeviceState)data[0];
            state.Error = (ErrorCode)data[1];
            state.IPv4 = ByteHelpers.Slice(data, 2, 4);
            return state;
        }

        /// <summary>Manufacturer data after the company ID: version(1) model(2) shortId(3).</summary>
        public static byte[] EncodeManufacturerPayload(ushort modelId, byte[] deviceId)
        {
            byte[] data = new byte[ProtocolConstants.ManufacturerPayloadLength];
            data[0] = ProtocolConstants.ProtocolVersion;
            ByteHelpers.WriteUInt16(data, 1, modelId);
            ByteHelpers.Copy(deviceId, ProtocolConstants.DeviceIdBytes - ProtocolConstants.ShortIdBytes, data, 3, ProtocolConstants.ShortIdBytes);
            return data;
        }

        public static AdvertisedIdentity DecodeManufacturerPayload(byte[] data)
        {
            if (data == null || data.Length < ProtocolConstants.ManufacturerPayloadLength)
            {
                return null;
            }

            AdvertisedIdentity identity = new AdvertisedIdentity();
            identity.ProtocolVersion = data[0];
            identity.ModelId = ByteHelpers.ReadUInt16(data, 1);
            identity.ShortId = ByteHelpers.Slice(data, 3, ProtocolConstants.ShortIdBytes);
            return identity;
        }
    }
}
