using System.Globalization;
using IotStarter.App.Core.Network;
using IotStarter.Shared;
using Microsoft.Extensions.Logging;
using Zeroconf;

namespace IotStarter.App.Services;

/// <summary>
/// DNS-SD browse for _iotstarter._tcp with Zeroconf. On iOS the package's iOS build uses the system
/// Bonjour API (the service type must be listed in NSBonjourServices); on Android a Wi-Fi multicast
/// lock is held during the browse so multicast replies are delivered.
/// </summary>
public sealed class ZeroconfMdnsBrowser(ILogger<ZeroconfMdnsBrowser> logger) : IMdnsBrowser
{
    private const string Protocol = ProtocolConstants.MdnsServiceType + ".local.";

    public async Task<IReadOnlyList<DiscoveryHit>> BrowseAsync(TimeSpan window, CancellationToken cancellationToken)
    {
        using IDisposable multicastLock = AcquireMulticastLock();
        IReadOnlyList<IZeroconfHost> hosts = await ZeroconfResolver.ResolveAsync(
            Protocol,
            scanTime: window,
            retries: 1,
            retryDelayMilliseconds: 500,
            callback: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var hits = new List<DiscoveryHit>();
        foreach (IZeroconfHost host in hosts)
        {
            if (string.IsNullOrEmpty(host.IPAddress))
            {
                continue;
            }

            IService? service = host.Services.Values.FirstOrDefault();
            int port = service?.Port is > 0 and <= 65535 ? service.Port : ProtocolConstants.HttpPort;
            string? id = null;
            int? model = null;
            foreach (IReadOnlyDictionary<string, string> properties in service?.Properties ?? [])
            {
                if (properties.TryGetValue(HttpContract.TxtId, out string? txtId) && DeviceIdentity.ParseDeviceId(txtId) is not null)
                {
                    id = txtId.ToUpperInvariant();
                }

                if (properties.TryGetValue(HttpContract.TxtModel, out string? txtModel)
                    && int.TryParse(txtModel, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    model = parsed;
                }
            }

            hits.Add(new DiscoveryHit(id, new DeviceEndpoint(host.IPAddress, port), model, DiscoverySource.Mdns));
        }

        logger.LogDebug("mDNS browse found {Count} device(s)", hits.Count);
        return hits;
    }

    private static IDisposable AcquireMulticastLock()
    {
#if ANDROID
        var wifi = (Android.Net.Wifi.WifiManager?)Android.App.Application.Context.GetSystemService(Android.Content.Context.WifiService);
        Android.Net.Wifi.WifiManager.MulticastLock? multicastLock = wifi?.CreateMulticastLock("iotstarter-mdns");
        if (multicastLock is null)
        {
            return new Releaser(null);
        }

        multicastLock.SetReferenceCounted(false);
        multicastLock.Acquire();
        return new Releaser(multicastLock.Release);
#else
        return new Releaser(null);
#endif
    }

    private sealed class Releaser(Action? release) : IDisposable
    {
        public void Dispose() => release?.Invoke();
    }
}
