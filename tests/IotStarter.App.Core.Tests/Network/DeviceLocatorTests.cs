using System.Net;
using System.Text;
using IotStarter.App.Core.Network;
using Microsoft.Extensions.Logging.Abstractions;

namespace IotStarter.App.Core.Tests.Network;

public class DeviceLocatorTests
{
    private const string DeviceA = "A1B2C3D4E5F6";
    private const string DeviceB = "0011223344AA";

    [Fact]
    public async Task Last_known_address_is_used_without_discovery()
    {
        var api = new FakeApi { [new DeviceEndpoint("10.0.0.5")] = DeviceA };
        var mdns = new FakeMdns();
        var udp = new FakeUdp();
        var locator = new DeviceLocator(api, mdns, udp, NullLogger<DeviceLocator>.Instance);

        var found = await locator.LocateAsync([new LocateRequest(DeviceA, new DeviceEndpoint("10.0.0.5"))], TestContext.Current.CancellationToken);

        Assert.Equal(DiscoverySource.LastKnownAddress, found[DeviceA].Source);
        Assert.Equal(0, mdns.Calls);
        Assert.Equal(0, udp.Calls);
    }

    [Fact]
    public async Task Address_taken_by_another_device_falls_back_to_mdns()
    {
        // DHCP gave the old address to device B; device A moved to .9.
        var api = new FakeApi
        {
            [new DeviceEndpoint("10.0.0.5")] = DeviceB,
            [new DeviceEndpoint("10.0.0.9")] = DeviceA
        };
        var mdns = new FakeMdns(new DiscoveryHit(DeviceA, new DeviceEndpoint("10.0.0.9"), 1, DiscoverySource.Mdns));
        var udp = new FakeUdp();
        var locator = new DeviceLocator(api, mdns, udp, NullLogger<DeviceLocator>.Instance);

        var found = await locator.LocateAsync([new LocateRequest(DeviceA, new DeviceEndpoint("10.0.0.5"))], TestContext.Current.CancellationToken);

        Assert.Equal("10.0.0.9", found[DeviceA].Endpoint.IPv4);
        Assert.Equal(DiscoverySource.Mdns, found[DeviceA].Source);
        Assert.Equal(0, udp.Calls);
    }

    [Fact]
    public async Task Udp_probe_is_tried_when_mdns_finds_nothing()
    {
        var api = new FakeApi { [new DeviceEndpoint("10.0.0.7")] = DeviceA };
        var mdns = new FakeMdns { Throws = true };
        var udp = new FakeUdp(new DiscoveryHit(DeviceA, new DeviceEndpoint("10.0.0.7"), 1, DiscoverySource.UdpProbe));
        var locator = new DeviceLocator(api, mdns, udp, NullLogger<DeviceLocator>.Instance);

        var found = await locator.LocateAsync([new LocateRequest(DeviceA, null)], TestContext.Current.CancellationToken);

        Assert.Equal(DiscoverySource.UdpProbe, found[DeviceA].Source);
    }

    [Fact]
    public async Task Discovery_hit_with_wrong_identity_is_not_accepted()
    {
        // mDNS claims device A at .8, but /info there says device B.
        var api = new FakeApi { [new DeviceEndpoint("10.0.0.8")] = DeviceB };
        var mdns = new FakeMdns(new DiscoveryHit(DeviceA, new DeviceEndpoint("10.0.0.8"), 1, DiscoverySource.Mdns));
        var locator = new DeviceLocator(api, mdns, new FakeUdp(), NullLogger<DeviceLocator>.Instance);

        var found = await locator.LocateAsync([new LocateRequest(DeviceA, null)], TestContext.Current.CancellationToken);

        Assert.Empty(found);
    }

    [Fact]
    public async Task Missing_devices_are_absent_from_the_result()
    {
        var locator = new DeviceLocator(new FakeApi(), new FakeMdns(), new FakeUdp(), NullLogger<DeviceLocator>.Instance);

        var found = await locator.LocateAsync([new LocateRequest(DeviceA, new DeviceEndpoint("10.0.0.5"))], TestContext.Current.CancellationToken);

        Assert.Empty(found);
    }

    [Fact]
    public void Udp_reply_is_parsed_and_validated()
    {
        byte[] good = Encoding.UTF8.GetBytes("{\"id\":\"a1b2c3d4e5f6\",\"model\":1,\"port\":80,\"api\":1}");
        byte[] badId = Encoding.UTF8.GetBytes("{\"id\":\"nothex\",\"model\":1,\"port\":80,\"api\":1}");
        byte[] badPort = Encoding.UTF8.GetBytes("{\"id\":\"A1B2C3D4E5F6\",\"model\":1,\"port\":0,\"api\":1}");

        DiscoveryHit? hit = UdpProber.Parse(good, IPAddress.Parse("10.0.0.3"));

        Assert.NotNull(hit);
        Assert.Equal(DeviceA, hit.DeviceId);
        Assert.Equal("10.0.0.3", hit.Endpoint.IPv4);
        Assert.Null(UdpProber.Parse(badId, IPAddress.Loopback));
        Assert.Null(UdpProber.Parse(badPort, IPAddress.Loopback));
        Assert.Null(UdpProber.Parse(Encoding.UTF8.GetBytes("not json"), IPAddress.Loopback));
    }

    private sealed class FakeApi : Dictionary<DeviceEndpoint, string>, IDeviceApiClient
    {
        public Task<DeviceInfoResponse> GetInfoAsync(DeviceEndpoint endpoint, TimeSpan timeout, CancellationToken cancellationToken) =>
            TryGetValue(endpoint, out string? id)
                ? Task.FromResult(new DeviceInfoResponse(id, 1, "Device", "1.0.0", 1))
                : throw new DeviceApiException(DeviceApiError.Unreachable, "no answer");

        public Task<IReadOnlyList<OutputResponse>> GetOutputsAsync(DeviceEndpoint endpoint, string token, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OutputResponse> SetOutputAsync(DeviceEndpoint endpoint, string token, int outputId, bool on, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> SetNameAsync(DeviceEndpoint endpoint, string token, string name, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task IdentifyAsync(DeviceEndpoint endpoint, string token, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RebootAsync(DeviceEndpoint endpoint, string token, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task FactoryResetAsync(DeviceEndpoint endpoint, string token, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeMdns(params DiscoveryHit[] hits) : IMdnsBrowser
    {
        public int Calls { get; private set; }

        public bool Throws { get; init; }

        public Task<IReadOnlyList<DiscoveryHit>> BrowseAsync(TimeSpan window, CancellationToken cancellationToken)
        {
            Calls++;
            return Throws ? throw new InvalidOperationException("multicast blocked") : Task.FromResult<IReadOnlyList<DiscoveryHit>>(hits);
        }
    }

    private sealed class FakeUdp(params DiscoveryHit[] hits) : IUdpProber
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<DiscoveryHit>> ProbeAsync(TimeSpan window, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<DiscoveryHit>>(hits);
        }
    }
}
