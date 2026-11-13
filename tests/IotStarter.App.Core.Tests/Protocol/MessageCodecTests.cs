using System.Text;
using IotStarter.Shared;

namespace IotStarter.App.Core.Tests.Protocol;

public class MessageCodecTests
{
    private static readonly byte[] Token = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void Crc16_matches_the_ccitt_false_check_value()
    {
        byte[] data = Encoding.ASCII.GetBytes("123456789");
        Assert.Equal(0x29B1, ByteHelpers.Crc16(data, 0, data.Length));
    }

    [Fact]
    public void Set_credentials_round_trips()
    {
        byte[] message = MessageCodec.EncodeSetCredentials(Encoding.UTF8.GetBytes("Home-WiFi"), Encoding.ASCII.GetBytes("demo1234"), Token);

        Assert.Equal(ErrorCode.None, MessageCodec.Validate(message));
        Assert.Equal((byte)CommandType.SetCredentials, MessageCodec.MessageType(message));
        CredentialsData decoded = MessageCodec.DecodeSetCredentials(MessageCodec.Payload(message));
        Assert.NotNull(decoded);
        Assert.Equal("Home-WiFi", Encoding.UTF8.GetString(decoded.Ssid));
        Assert.Equal("demo1234", Encoding.ASCII.GetString(decoded.Passphrase));
        Assert.Equal(Token, decoded.Token);
    }

    [Fact]
    public void Open_network_has_empty_passphrase()
    {
        byte[] message = MessageCodec.EncodeSetCredentials(Encoding.UTF8.GetBytes("Cafe"), [], Token);

        CredentialsData decoded = MessageCodec.DecodeSetCredentials(MessageCodec.Payload(message));
        Assert.Empty(decoded.Passphrase);
    }

    [Fact]
    public void Largest_credentials_fit_in_eight_default_chunks()
    {
        byte[] message = MessageCodec.EncodeSetCredentials(new byte[32].Select(_ => (byte)'s').ToArray(), new byte[63].Select(_ => (byte)'p').ToArray(), Token);

        Assert.Equal(132, message.Length);
        Assert.Equal(8, FrameSplitter.Split(message, 0, ProtocolConstants.DefaultChunkSize).Length);
    }

    public static TheoryData<byte[], byte[], byte[]> InvalidCredentials => new()
    {
        { [], Encoding.ASCII.GetBytes("password"), Token },
        { new byte[33], Encoding.ASCII.GetBytes("password"), Token },
        { Encoding.ASCII.GetBytes("ssid"), Encoding.ASCII.GetBytes("short"), Token },
        { Encoding.ASCII.GetBytes("ssid"), new byte[64].Select(_ => (byte)'x').ToArray(), Token },
        { Encoding.ASCII.GetBytes("ssid"), Encoding.UTF8.GetBytes("pässwörd"), Token },
        { Encoding.ASCII.GetBytes("ssid"), Encoding.ASCII.GetBytes("password"), new byte[32] },
        { Encoding.ASCII.GetBytes("ssid"), Encoding.ASCII.GetBytes("password"), new byte[31] }
    };

    [Theory]
    [MemberData(nameof(InvalidCredentials))]
    public void Invalid_credentials_are_refused_by_encoder_and_validator(byte[] ssid, byte[] passphrase, byte[] token)
    {
        Assert.Null(MessageCodec.EncodeSetCredentials(ssid, passphrase, token));
        Assert.Equal(ErrorCode.InvalidPayload, MessageCodec.ValidateCredentials(ssid, passphrase, token));
    }

    [Theory]
    [InlineData(new byte[] { 5, 1, 2 })]
    [InlineData(new byte[] { 0 })]
    [InlineData(new byte[] { 2, 0x41, 0x42, 8 })]
    public void Truncated_set_credentials_payload_decodes_to_null(byte[] payload)
    {
        Assert.Null(MessageCodec.DecodeSetCredentials(payload));
    }

    [Fact]
    public void Corrupted_crc_is_detected()
    {
        byte[] message = MessageCodec.EncodeCommand(CommandType.Identify);
        message[^1] ^= 0xFF;
        Assert.Equal(ErrorCode.InvalidFrame, MessageCodec.Validate(message));
    }

    [Fact]
    public void Info_round_trips()
    {
        byte[] mac = [0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0xF6];
        byte[] value = MessageCodec.EncodeInfo(0x0001, mac, 1, 2, 3, (ushort)(Capability.CanScan | Capability.Mdns));

        DeviceInfoData info = MessageCodec.DecodeInfo(value);

        Assert.Equal(ProtocolConstants.InfoLength, value.Length);
        Assert.Equal(1, info.ProtocolVersion);
        Assert.Equal(0x0001, info.ModelId);
        Assert.Equal(mac, info.DeviceId);
        Assert.Equal((2, 3), (info.FirmwareMinor, info.FirmwarePatch));
        Assert.Equal((ushort)(Capability.CanScan | Capability.Mdns), info.Capabilities);
    }

    [Fact]
    public void State_round_trips_with_address()
    {
        byte[] value = MessageCodec.EncodeState(DeviceState.Connected, ErrorCode.None, [192, 168, 1, 40]);

        DeviceStateData state = MessageCodec.DecodeState(value);

        Assert.Equal(DeviceState.Connected, state.State);
        Assert.Equal("192.168.1.40", ByteHelpers.FormatIPv4(state.IPv4));
    }

    [Fact]
    public void Scan_entry_keeps_negative_rssi()
    {
        byte[] message = MessageCodec.EncodeScanEntry(-87, NetworkSecurity.Unknown, Encoding.UTF8.GetBytes("Café"));

        ScanEntryData entry = MessageCodec.DecodeScanEntry(MessageCodec.Payload(message));

        Assert.Equal(-87, entry.Rssi);
        Assert.Equal("Café", Encoding.UTF8.GetString(entry.Ssid));
    }

    [Fact]
    public void Manufacturer_payload_carries_short_id()
    {
        byte[] mac = [0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0xF6];

        AdvertisedIdentity identity = MessageCodec.DecodeManufacturerPayload(MessageCodec.EncodeManufacturerPayload(0x0001, mac));

        Assert.Equal("D4E5F6", DeviceIdentity.FormatShortId(identity.ShortId));
        Assert.True(DeviceIdentity.MatchesShortId("A1B2C3D4E5F6", identity.ShortId));
        Assert.Equal("IOT-E5F6", DeviceIdentity.BleName("IOT", mac));
        Assert.Equal("iot-a1b2c3d4e5f6", DeviceIdentity.HostName("IOT", mac));
    }

    [Fact]
    public void Decoders_never_throw_on_random_input()
    {
        var random = new Random(99);
        for (int i = 0; i < 20_000; i++)
        {
            byte[] data = new byte[random.Next(0, 80)];
            random.NextBytes(data);
            _ = MessageCodec.Validate(data);
            _ = MessageCodec.DecodeSetCredentials(data);
            _ = MessageCodec.DecodeScanEntry(data);
            _ = MessageCodec.DecodeInfo(data);
            _ = MessageCodec.DecodeState(data);
            _ = MessageCodec.DecodeManufacturerPayload(data);
            _ = MessageCodec.DecodeAck(data, out _, out _);
            _ = MessageCodec.DecodeProvisionResult(data, out _, out _);
        }
    }

    [Theory]
    [InlineData("192.168.1.40", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("256.1.1.1", false)]
    [InlineData("1.2.3", false)]
    [InlineData("1.2.3.4.5", false)]
    [InlineData("1..2.3", false)]
    [InlineData("a.b.c.d", false)]
    public void IPv4_parser_accepts_only_dotted_quads(string text, bool valid)
    {
        Assert.Equal(valid, ByteHelpers.ParseIPv4(text) is not null);
    }

    [Fact]
    public void Hex_round_trips_and_rejects_bad_input()
    {
        byte[] data = [0x00, 0xAB, 0xFF];
        Assert.Equal("00abff", ByteHelpers.ToHexLower(data, 0, 3));
        Assert.Equal(data, ByteHelpers.FromHex("00ABff"));
        Assert.Null(ByteHelpers.FromHex("abc"));
        Assert.Null(ByteHelpers.FromHex("zz"));
    }
}
