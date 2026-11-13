using IotStarter.Shared;

namespace IotStarter.App.Core.Ble;

/// <summary>A provisionable device seen in a BLE scan.</summary>
/// <param name="PeripheralId">Platform peripheral identifier used to connect.</param>
/// <param name="Name">Advertised name, e.g. "IOT-E5F6".</param>
/// <param name="Rssi">Latest signal strength in dBm.</param>
/// <param name="ModelId">Model from the manufacturer data; null until the scan response arrives.</param>
/// <param name="ShortId">Last 3 bytes of the device ID as hex, when known.</param>
/// <param name="ProtocolVersion">Advertised protocol version, when known.</param>
public sealed record DiscoveredDevice(
    string PeripheralId,
    string Name,
    int Rssi,
    ushort? ModelId,
    string? ShortId,
    byte? ProtocolVersion)
{
    /// <summary>Catalogue entry, or null for unknown models (and while the model is not yet known).</summary>
    public ModelDefinition? Model => ModelId is { } id ? ModelCatalog.Find(id) : null;

    /// <summary>True once the manufacturer data has been seen and the model is not in the catalogue.</summary>
    public bool IsUnknownModel => ModelId is not null && Model is null;

    /// <summary>True when the device speaks a protocol version this app does not support.</summary>
    public bool IsUnsupportedProtocol => ProtocolVersion is { } v && v != ProtocolConstants.ProtocolVersion;
}

/// <summary>Turns raw advertisements into <see cref="DiscoveredDevice"/> values.</summary>
public static class AdvertisementParser
{
    /// <summary>
    /// Merges <paramref name="advertisement"/> into <paramref name="previous"/> (the scan response with
    /// the manufacturer data may arrive separately from the packet carrying the name).
    /// Returns null when the advertisement is not one of ours.
    /// </summary>
    public static DiscoveredDevice? Merge(DiscoveredDevice? previous, BleAdvertisement advertisement)
    {
        AdvertisedIdentity? identity = null;
        if (advertisement.CompanyId == ProtocolConstants.ManufacturerCompanyId)
        {
            identity = MessageCodec.DecodeManufacturerPayload(advertisement.ManufacturerPayload);
        }

        string? name = string.IsNullOrWhiteSpace(advertisement.LocalName) ? previous?.Name : advertisement.LocalName;
        bool namedLikeOurs = name is not null && name.StartsWith(ProtocolConstants.BleNamePrefix + "-", StringComparison.Ordinal);
        if (previous is null && identity is null && !namedLikeOurs)
        {
            // The scan is filtered on our service UUID, but stay defensive about stray results.
            return null;
        }

        return new DiscoveredDevice(
            advertisement.PeripheralId,
            name ?? previous?.Name ?? ProtocolConstants.BleNamePrefix,
            advertisement.Rssi,
            identity?.ModelId ?? previous?.ModelId,
            identity is not null ? DeviceIdentity.FormatShortId(identity.ShortId) : previous?.ShortId,
            identity?.ProtocolVersion ?? previous?.ProtocolVersion);
    }
}
