using System.Text.Json.Serialization;
using IotStarter.Shared;

namespace IotStarter.App.Core.Network;

/// <summary>GET /api/v1/info.</summary>
public sealed record DeviceInfoResponse(
    [property: JsonPropertyName(HttpContract.FieldId)] string Id,
    [property: JsonPropertyName(HttpContract.FieldModel)] int Model,
    [property: JsonPropertyName(HttpContract.FieldName)] string? Name,
    [property: JsonPropertyName(HttpContract.FieldFirmware)] string Firmware,
    [property: JsonPropertyName(HttpContract.FieldApi)] int Api);

/// <summary>One output as reported by the device.</summary>
public sealed record OutputResponse(
    [property: JsonPropertyName(HttpContract.FieldId)] int Id,
    [property: JsonPropertyName(HttpContract.FieldLabel)] string Label,
    [property: JsonPropertyName(HttpContract.FieldOn)] bool On);

/// <summary>GET /api/v1/outputs.</summary>
public sealed record OutputsResponse(
    [property: JsonPropertyName(HttpContract.FieldOutputs)] IReadOnlyList<OutputResponse> Outputs);

public sealed record SetOutputRequest([property: JsonPropertyName(HttpContract.FieldOn)] bool On);

public sealed record SetNameRequest([property: JsonPropertyName(HttpContract.FieldName)] string Name);

public sealed record NameResponse([property: JsonPropertyName(HttpContract.FieldName)] string Name);

/// <summary>UDP discovery reply.</summary>
public sealed record DiscoveryReply(
    [property: JsonPropertyName(HttpContract.FieldId)] string Id,
    [property: JsonPropertyName(HttpContract.FieldModel)] int Model,
    [property: JsonPropertyName(HttpContract.FieldPort)] int Port,
    [property: JsonPropertyName(HttpContract.FieldApi)] int Api);

/// <summary>Source-generated serialisation for every DTO the app exchanges with devices.</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(DeviceInfoResponse))]
[JsonSerializable(typeof(OutputsResponse))]
[JsonSerializable(typeof(OutputResponse))]
[JsonSerializable(typeof(SetOutputRequest))]
[JsonSerializable(typeof(SetNameRequest))]
[JsonSerializable(typeof(NameResponse))]
[JsonSerializable(typeof(DiscoveryReply))]
public sealed partial class ApiJsonContext : JsonSerializerContext;
