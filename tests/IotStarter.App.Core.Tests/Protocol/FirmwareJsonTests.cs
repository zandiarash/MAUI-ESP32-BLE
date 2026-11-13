using IotStarter.Firmware.Storage;

namespace IotStarter.App.Core.Tests.Protocol;

/// <summary>The firmware's strict flat-JSON parser (linked from src/IotStarter.Firmware).</summary>
public class FirmwareJsonTests
{
    [Theory]
    [InlineData("{\"on\":true}", true)]
    [InlineData("  { \"on\" : false , \"extra\": \"ignored\" }  ", false)]
    public void Reads_boolean_field(string json, bool expected)
    {
        FlatJsonObject? obj = FlatJsonObject.Parse(json);

        Assert.NotNull(obj);
        Assert.True(obj.TryGetBool("on", out bool on));
        Assert.Equal(expected, on);
    }

    [Theory]
    [InlineData("")]
    [InlineData("on=true")]
    [InlineData("{\"on\":true")]
    [InlineData("{\"on\":true}}")]
    [InlineData("{\"on\":[true]}")]
    [InlineData("{\"on\":{\"x\":1}}")]
    [InlineData("{on:true}")]
    [InlineData("{\"on\":tru}")]
    [InlineData("[]")]
    [InlineData("{\"a\":\"unterminated}")]
    [InlineData("{\"a\":\"bad \\q escape\"}")]
    public void Rejects_malformed_or_nested_json(string json)
    {
        Assert.Null(FlatJsonObject.Parse(json));
    }

    [Fact]
    public void Wrong_type_is_not_a_boolean()
    {
        FlatJsonObject? obj = FlatJsonObject.Parse("{\"on\":\"true\"}");

        Assert.NotNull(obj);
        Assert.False(obj.TryGetBool("on", out _));
    }

    [Fact]
    public void Writer_escapes_and_parser_reads_back()
    {
        string json = new JsonWriter().BeginObject().String("name", "Kitchen \"Main\"\n\\").Number("n", 7).EndObject().ToString();

        FlatJsonObject? obj = FlatJsonObject.Parse(json);

        Assert.NotNull(obj);
        Assert.True(obj.TryGetString("name", out string? name));
        Assert.Equal("Kitchen \"Main\"\n\\", name);
        Assert.True(obj.TryGetInt("n", out int n));
        Assert.Equal(7, n);
    }

    [Fact]
    public void Unicode_escapes_are_decoded()
    {
        FlatJsonObject? obj = FlatJsonObject.Parse("{\"name\":\"Caf\\u00e9\"}");

        Assert.True(obj!.TryGetString("name", out string? name));
        Assert.Equal("Café", name);
    }

    [Fact]
    public void Random_input_never_throws()
    {
        var random = new Random(7);
        const string alphabet = "{}[]\":,truefalsnl0123456789-.eE \\u\n";
        for (int i = 0; i < 20_000; i++)
        {
            char[] chars = new char[random.Next(0, 40)];
            for (int j = 0; j < chars.Length; j++)
            {
                chars[j] = alphabet[random.Next(alphabet.Length)];
            }

            _ = FlatJsonObject.Parse(new string(chars));
        }
    }
}
