using IotStarter.App.Core.Demo;
using IotStarter.App.Core.Devices;
using IotStarter.App.Core.Network;
using IotStarter.App.Core.Platform;
using IotStarter.App.Core.State;
using IotStarter.Shared;

namespace IotStarter.App.Core.Tests.State;

public class DeviceManagerTests
{
    private const string Token = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";

    [Fact]
    public async Task Refresh_finds_a_device_whose_address_changed()
    {
        await using var host = new TestHost();
        DemoDevice device = await ProvisionedDeviceAsync(host);
        string oldAddress = device.IPv4!;
        device.IPv4 = host.World.NextAddress();

        await host.Devices.LoadAsync(TestContext.Current.CancellationToken);
        await host.Devices.RefreshAsync(TestContext.Current.CancellationToken);

        DeviceView view = Assert.Single(host.State.Devices);
        Assert.Equal(DeviceConnectivity.Online, view.Connectivity);
        Assert.NotEqual(oldAddress, view.Record.LastIPv4);
        Assert.Equal(device.IPv4, view.Record.LastIPv4);
        Assert.Equal(DiscoverySource.Mdns, view.FoundVia);

        // The new address is persisted for next time.
        DeviceRecord stored = Assert.Single(await host.Store.GetAllAsync(TestContext.Current.CancellationToken));
        Assert.Equal(device.IPv4, stored.LastIPv4);
    }

    [Fact]
    public async Task Switching_updates_optimistically_and_confirms()
    {
        await using var host = new TestHost();
        DemoDevice device = await ProvisionedDeviceAsync(host);
        await host.Devices.LoadAsync(TestContext.Current.CancellationToken);
        await host.Devices.RefreshAsync(TestContext.Current.CancellationToken);

        DeviceActionResult result = await host.Devices.SetOutputAsync(device.DeviceId, 2, true, TestContext.Current.CancellationToken);

        Assert.Equal(DeviceActionResult.Ok, result);
        Assert.True(device.Outputs[1]);
        OutputView output = host.State.Devices[0].Outputs.Single(o => o.Id == 2);
        Assert.True(output.On);
        Assert.False(output.Pending);
    }

    [Fact]
    public async Task Failed_switch_rolls_back_and_publishes_a_notice()
    {
        await using var host = new TestHost();
        DemoDevice device = await ProvisionedDeviceAsync(host);
        await host.Devices.LoadAsync(TestContext.Current.CancellationToken);
        await host.Devices.RefreshAsync(TestContext.Current.CancellationToken);
        var notices = new List<AppNotice>();
        host.State.Notice += notices.Add;

        device.Online = false;
        DeviceActionResult result = await host.Devices.SetOutputAsync(device.DeviceId, 1, true, TestContext.Current.CancellationToken);

        Assert.Equal(DeviceActionResult.Unreachable, result);
        OutputView output = host.State.Find(device.DeviceId)!.Outputs.Single(o => o.Id == 1);
        Assert.False(output.On);
        Assert.False(output.Pending);
        Assert.Contains(notices, n => n.Kind == NoticeKind.SwitchFailed);
    }

    [Fact]
    public async Task Wrong_token_shows_the_device_as_unauthorized()
    {
        await using var host = new TestHost();
        DemoDevice device = await ProvisionedDeviceAsync(host);
        device.TokenHex = new string('f', 64);

        await host.Devices.LoadAsync(TestContext.Current.CancellationToken);
        await host.Devices.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DeviceConnectivity.Unauthorized, Assert.Single(host.State.Devices).Connectivity);
    }

    [Fact]
    public async Task Not_on_a_local_network_marks_devices_offline_without_probing()
    {
        await using var host = new TestHost(demoMode: false);
        await host.Store.UpsertAsync(new DeviceRecord("A1B2C3D4E5F6", 1, "Lamp", "10.0.0.5", 80, null, IsDemo: false), TestContext.Current.CancellationToken);
        host.Network.State = LocalNetworkState.CellularOnly;
        var notices = new List<AppNotice>();
        host.State.Notice += notices.Add;

        await host.Devices.LoadAsync(TestContext.Current.CancellationToken);
        await host.Devices.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DeviceConnectivity.Offline, Assert.Single(host.State.Devices).Connectivity);
        Assert.Contains(notices, n => n.Kind == NoticeKind.NotOnLocalNetwork);
    }

    [Fact]
    public async Task Forget_removes_device_and_token()
    {
        await using var host = new TestHost();
        DemoDevice device = await ProvisionedDeviceAsync(host);
        await host.Devices.LoadAsync(TestContext.Current.CancellationToken);

        await host.Devices.ForgetAsync(device.DeviceId, TestContext.Current.CancellationToken);

        Assert.Empty(host.State.Devices);
        Assert.Empty(await host.Store.GetAllAsync(TestContext.Current.CancellationToken));
        Assert.Null(await host.Tokens.GetAsync(device.DeviceId));
    }

    [Fact]
    public async Task Demo_and_real_devices_are_kept_apart()
    {
        await using var host = new TestHost();
        await ProvisionedDeviceAsync(host);
        await host.Store.UpsertAsync(new DeviceRecord("0011223344AA", 1, "Real lamp", "10.0.0.5", 80, null, IsDemo: false), TestContext.Current.CancellationToken);

        await host.Devices.LoadAsync(TestContext.Current.CancellationToken);
        Assert.All(host.State.Devices, d => Assert.True(d.Record.IsDemo));

        host.Settings.DemoMode = false;
        await host.Devices.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Real lamp", Assert.Single(host.State.Devices).Record.Name);
    }

    /// <summary>A demo device already on the network, saved in the app with a matching token.</summary>
    private static async Task<DemoDevice> ProvisionedDeviceAsync(TestHost host)
    {
        DemoDevice device = host.World.Devices.First(d => d.ModelId == ModelCatalog.FourChannelSwitchId);
        host.World.Provision(device, Token);
        await host.Tokens.SetAsync(device.DeviceId, Token);
        await host.Store.UpsertAsync(
            new DeviceRecord(device.DeviceId, device.ModelId, "Lamp", device.IPv4, 80, null, IsDemo: true),
            TestContext.Current.CancellationToken);
        return device;
    }
}
