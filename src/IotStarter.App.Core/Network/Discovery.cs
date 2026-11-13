using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using IotStarter.Shared;
using Microsoft.Extensions.Logging;

namespace IotStarter.App.Core.Network;

/// <summary>How a device was found on the network.</summary>
public enum DiscoverySource
{
    LastKnownAddress,
    Mdns,
    UdpProbe
}

/// <summary>A device that answered discovery. <see cref="DeviceId"/> may be null for an mDNS hit without TXT.</summary>
public sealed record DiscoveryHit(string? DeviceId, DeviceEndpoint Endpoint, int? ModelId, DiscoverySource Source);

/// <summary>DNS-SD browsing (Bonjour on iOS, a multicast responder elsewhere).</summary>
public interface IMdnsBrowser
{
    Task<IReadOnlyList<DiscoveryHit>> BrowseAsync(TimeSpan window, CancellationToken cancellationToken);
}

/// <summary>The UDP broadcast probe.</summary>
public interface IUdpProber
{
    Task<IReadOnlyList<DiscoveryHit>> ProbeAsync(TimeSpan window, CancellationToken cancellationToken);
}

/// <summary>
/// Sends "IOTS?" + version to 255.255.255.255:48731 and collects unicast replies.
/// On iOS broadcast needs Apple's multicast entitlement; without it the send fails and this
/// returns an empty list (mDNS then covers discovery).
/// </summary>
public sealed class UdpProber(ILogger<UdpProber> logger) : IUdpProber
{
    public async Task<IReadOnlyList<DiscoveryHit>> ProbeAsync(TimeSpan window, CancellationToken cancellationToken)
    {
        var hits = new Dictionary<string, DiscoveryHit>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var client = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
            client.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

            byte[] probe = [.. Encoding.ASCII.GetBytes(ProtocolConstants.DiscoveryProbeMagic), ProtocolConstants.ProtocolVersion];
            var target = new IPEndPoint(IPAddress.Broadcast, ProtocolConstants.DiscoveryPort);

            // Two probes, half a window apart: UDP may drop either.
            await client.SendAsync(probe, target, cancellationToken).ConfigureAwait(false);

            using var windowSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            windowSource.CancelAfter(window);
            bool resent = false;
            long started = Environment.TickCount64;

            while (!windowSource.IsCancellationRequested)
            {
                if (!resent && Environment.TickCount64 - started > window.TotalMilliseconds / 2)
                {
                    resent = true;
                    await client.SendAsync(probe, target, cancellationToken).ConfigureAwait(false);
                }

                UdpReceiveResult result;
                try
                {
                    using var slice = CancellationTokenSource.CreateLinkedTokenSource(windowSource.Token);
                    slice.CancelAfter(TimeSpan.FromMilliseconds(250));
                    result = await client.ReceiveAsync(slice.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    continue;
                }

                DiscoveryHit? hit = Parse(result.Buffer, result.RemoteEndPoint.Address);
                if (hit?.DeviceId is { } id)
                {
                    hits[id] = hit;
                }
            }
        }
        catch (SocketException ex)
        {
            logger.LogInformation("UDP discovery unavailable here: {Message}", ex.Message);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return [.. hits.Values];
    }

    internal static DiscoveryHit? Parse(byte[] buffer, IPAddress sender)
    {
        try
        {
            DiscoveryReply? reply = JsonSerializer.Deserialize(buffer, ApiJsonContext.Default.DiscoveryReply);
            if (reply is null || DeviceIdentity.ParseDeviceId(reply.Id) is null || reply.Port is <= 0 or > 65535)
            {
                return null;
            }

            return new DiscoveryHit(reply.Id.ToUpperInvariant(), new DeviceEndpoint(sender.ToString(), reply.Port), reply.Model, DiscoverySource.UdpProbe);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
