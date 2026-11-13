using Microsoft.Extensions.Logging;

namespace IotStarter.App.Core.Network;

/// <summary>A device confirmed (by GET /info) at an endpoint.</summary>
public sealed record LocatedDevice(string DeviceId, DeviceEndpoint Endpoint, DeviceInfoResponse Info, DiscoverySource Source);

/// <summary>Finds devices on the local network again after DHCP changes their address.</summary>
public interface IDeviceLocator
{
    /// <summary>
    /// Locates many devices in one pass: last known addresses first (in parallel), then one mDNS
    /// browse and one UDP probe for the rest. Every result is confirmed through GET /info.
    /// </summary>
    Task<IReadOnlyDictionary<string, LocatedDevice>> LocateAsync(
        IReadOnlyList<LocateRequest> devices,
        CancellationToken cancellationToken);
}

/// <summary>A device to find: its ID and the address it had last time.</summary>
public sealed record LocateRequest(string DeviceId, DeviceEndpoint? LastKnown);

/// <summary>Order: last known address (2 s), then mDNS, then UDP broadcast probe.</summary>
public sealed class DeviceLocator(
    IDeviceApiClient api,
    IMdnsBrowser mdns,
    IUdpProber udp,
    ILogger<DeviceLocator> logger) : IDeviceLocator
{
    internal static readonly TimeSpan LastKnownTimeout = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan MdnsWindow = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan UdpWindow = TimeSpan.FromSeconds(2.5);

    public async Task<IReadOnlyDictionary<string, LocatedDevice>> LocateAsync(
        IReadOnlyList<LocateRequest> devices,
        CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, LocatedDevice>(StringComparer.OrdinalIgnoreCase);
        if (devices.Count == 0)
        {
            return found;
        }

        // 1. Last known addresses, in parallel.
        LocatedDevice?[] direct = await Task.WhenAll(devices
            .Where(d => d.LastKnown is not null)
            .Select(d => ConfirmAsync(d.DeviceId, d.LastKnown!, DiscoverySource.LastKnownAddress, LastKnownTimeout, cancellationToken)))
            .ConfigureAwait(false);
        foreach (LocatedDevice? hit in direct)
        {
            if (hit is not null)
            {
                found[hit.DeviceId] = hit;
            }
        }

        HashSet<string> missing = devices.Select(d => d.DeviceId).Where(id => !found.ContainsKey(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (missing.Count == 0)
        {
            return found;
        }

        // 2. mDNS / DNS-SD.
        await ConfirmHitsAsync(await SafeBrowseAsync(cancellationToken).ConfigureAwait(false), missing, found, cancellationToken).ConfigureAwait(false);
        if (missing.Count == 0)
        {
            return found;
        }

        // 3. UDP broadcast probe.
        await ConfirmHitsAsync(await SafeProbeAsync(cancellationToken).ConfigureAwait(false), missing, found, cancellationToken).ConfigureAwait(false);

        if (missing.Count > 0)
        {
            logger.LogInformation("Not found on this network: {Count} device(s)", missing.Count);
        }

        return found;
    }

    private async Task ConfirmHitsAsync(
        IReadOnlyList<DiscoveryHit> hits,
        HashSet<string> missing,
        Dictionary<string, LocatedDevice> found,
        CancellationToken cancellationToken)
    {
        // Hits with a matching ID first; hits without an ID (mDNS without TXT) are checked against any missing device.
        IEnumerable<Task<LocatedDevice?>> confirmations = hits
            .Where(h => h.DeviceId is null || missing.Contains(h.DeviceId))
            .DistinctBy(h => h.Endpoint)
            .Select(h => ConfirmAsync(h.DeviceId, h.Endpoint, h.Source, ConfirmTimeout, cancellationToken));

        foreach (LocatedDevice? located in await Task.WhenAll(confirmations).ConfigureAwait(false))
        {
            if (located is not null && missing.Remove(located.DeviceId))
            {
                found[located.DeviceId] = located;
            }
        }
    }

    /// <summary>
    /// Asks the endpoint who it is. Returns null when it does not answer, or when it answers with a
    /// different ID than <paramref name="expectedId"/> (another device took over the address).
    /// </summary>
    private async Task<LocatedDevice?> ConfirmAsync(
        string? expectedId,
        DeviceEndpoint endpoint,
        DiscoverySource source,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            DeviceInfoResponse info = await api.GetInfoAsync(endpoint, timeout, cancellationToken).ConfigureAwait(false);
            if (expectedId is not null && !string.Equals(info.Id, expectedId, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("{Endpoint} now belongs to another device", endpoint);
                return null;
            }

            return new LocatedDevice(info.Id.ToUpperInvariant(), endpoint, info, source);
        }
        catch (DeviceApiException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<DiscoveryHit>> SafeBrowseAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await mdns.BrowseAsync(MdnsWindow, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation("mDNS browse failed: {Message}", ex.Message);
            return [];
        }
    }

    private async Task<IReadOnlyList<DiscoveryHit>> SafeProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await udp.ProbeAsync(UdpWindow, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation("UDP probe failed: {Message}", ex.Message);
            return [];
        }
    }
}
