using IotStarter.App.Core.Demo;
using IotStarter.App.Core.Platform;
using IotStarter.App.Core.Provisioning;
using IotStarter.App.Core.State;
using IotStarter.Shared;

namespace IotStarter.App.Core.Tests.Provisioning;

/// <summary>
/// The wizard state machine end to end: real ProvisioningSession, framing and codec against the
/// simulated device (which uses the same shared codec as the firmware).
/// </summary>
public class AddDeviceWizardTests
{
    [Fact]
    public async Task Happy_path_provisions_hands_over_and_names_the_device()
    {
        await using var host = new TestHost();
        await using AddDeviceWizard wizard = host.NewWizard();

        WizardChoosingNetwork step = await ReachNetworkStepAsync(wizard);
        Assert.Contains(step.Networks, n => n.Ssid == DemoWorld.HomeSsid);
        Assert.Equal(ModelCatalog.FourChannelSwitchId, step.Info.ModelId);

        CredentialProblem problem = await wizard.SubmitCredentialsAsync(DemoWorld.HomeSsid, DemoWorld.HomePassword, isHidden: false);
        Assert.Equal(CredentialProblem.None, problem);

        var naming = (WizardNaming)(await TestHost.WaitForAsync(wizard, s => s is WizardNaming or WizardFailed)).Value!;
        await wizard.FinishAsync("Kitchen");
        await TestHost.WaitForAsync(wizard, s => s is WizardDone);

        DeviceView device = Assert.Single(host.State.Devices);
        Assert.Equal(naming.Record.Id, device.Id);
        Assert.Equal("Kitchen", device.Record.Name);
        Assert.Equal(DeviceConnectivity.Online, device.Connectivity);
        Assert.Equal(4, device.Outputs.Count);
        Assert.True(device.Record.IsDemo);

        // The token the device stored is the one this phone keeps in secure storage.
        DemoDevice simulated = host.World.Devices.Single(d => d.DeviceId == device.Id);
        Assert.Equal(simulated.TokenHex, await host.Tokens.GetAsync(device.Id));
        Assert.Equal("Kitchen", simulated.Name);
    }

    [Fact]
    public async Task Wrong_password_is_reported_specifically_and_retry_keeps_the_input()
    {
        await using var host = new TestHost();
        await using AddDeviceWizard wizard = host.NewWizard();
        await ReachNetworkStepAsync(wizard);

        await wizard.SubmitCredentialsAsync(DemoWorld.HomeSsid, "wrong-password", isHidden: false);
        var failed = (WizardFailed)(await TestHost.WaitForAsync(wizard, s => s is WizardFailed or WizardNaming)).Value!;

        Assert.Equal(FailureKind.WrongPassword, failed.Failure.Kind);
        Assert.Equal(RetryTarget.Credentials, failed.Failure.Retry);

        await wizard.RetryAsync();
        var step = (WizardChoosingNetwork)(await TestHost.WaitForAsync(wizard, s => s is WizardChoosingNetwork)).Value!;
        Assert.Equal(DemoWorld.HomeSsid, step.Draft.Ssid);
        Assert.Equal("wrong-password", step.Draft.Password);

        // Same BLE session, no restart of device or app.
        await wizard.SubmitCredentialsAsync(DemoWorld.HomeSsid, DemoWorld.HomePassword, isHidden: false);
        await TestHost.WaitForAsync(wizard, s => s is WizardNaming);
    }

    [Fact]
    public async Task Unknown_network_is_reported_as_not_found()
    {
        await using var host = new TestHost();
        await using AddDeviceWizard wizard = host.NewWizard();
        await ReachNetworkStepAsync(wizard);

        await wizard.SubmitCredentialsAsync("Hidden-5G-only", "password123", isHidden: true);
        var failed = (WizardFailed)(await TestHost.WaitForAsync(wizard, s => s is WizardFailed)).Value!;

        Assert.Equal(FailureKind.NetworkNotFound, failed.Failure.Kind);
    }

    [Theory]
    [InlineData("", "password1", CredentialProblem.SsidMissing)]
    [InlineData("Home", "short", CredentialProblem.PasswordTooShort)]
    [InlineData("Home", "pässwört-long", CredentialProblem.PasswordInvalidCharacters)]
    [InlineData("an-ssid-that-is-way-too-long-for-wifi", "password1", CredentialProblem.SsidTooLong)]
    public async Task Invalid_input_is_refused_before_anything_is_sent(string ssid, string password, CredentialProblem expected)
    {
        await using var host = new TestHost();
        await using AddDeviceWizard wizard = host.NewWizard();
        await ReachNetworkStepAsync(wizard);

        CredentialProblem problem = await wizard.SubmitCredentialsAsync(ssid, password, isHidden: true);

        Assert.Equal(expected, problem);
        Assert.True(wizard.State.Value is WizardChoosingNetwork);
    }

    [Fact]
    public async Task Unknown_model_is_listed_and_marked()
    {
        await using var host = new TestHost();
        await using AddDeviceWizard wizard = host.NewWizard();
        await wizard.StartAsync();

        var scanning = (WizardScanning)(await TestHost.WaitForAsync(wizard,
            s => s is WizardScanning sc && sc.Devices.Any(d => d.ModelId == DemoWorld.UnknownModelId))).Value!;

        Assert.True(scanning.Devices.Single(d => d.ModelId == DemoWorld.UnknownModelId).IsUnknownModel);
    }

    [Theory]
    [InlineData(PermissionState.Denied, BlockReason.PermissionDenied)]
    [InlineData(PermissionState.DeniedPermanently, BlockReason.PermissionDeniedPermanently)]
    public async Task Denied_permission_blocks_with_the_reason(PermissionState permission, BlockReason expected)
    {
        await using var host = new TestHost(demoMode: false, realPermissions: new FixedPermissions(permission));
        await using AddDeviceWizard wizard = host.NewWizard();

        await wizard.StartAsync();

        var blocked = (WizardBlocked)wizard.State.Value!;
        Assert.Equal(expected, blocked.Reason);
    }

    [Fact]
    public async Task Bluetooth_off_blocks_the_wizard()
    {
        await using var host = new TestHost(demoMode: false, realPermissions: new FixedPermissions(PermissionState.Granted));
        await using AddDeviceWizard wizard = host.NewWizard();

        await wizard.StartAsync();

        Assert.Equal(BlockReason.BluetoothOff, ((WizardBlocked)wizard.State.Value!).Reason);
    }

    [Fact]
    public void Device_errors_map_to_specific_failures()
    {
        Assert.Equal(FailureKind.WrongPassword, AddDeviceWizard.MapError(ErrorCode.WrongPassword));
        Assert.Equal(FailureKind.NetworkNotFound, AddDeviceWizard.MapError(ErrorCode.NetworkNotFound));
        Assert.Equal(FailureKind.NoIpAddress, AddDeviceWizard.MapError(ErrorCode.NoIpAddress));
        Assert.Equal(FailureKind.WifiTimeout, AddDeviceWizard.MapError(ErrorCode.Timeout));
        Assert.Equal(FailureKind.NotAuthorized, AddDeviceWizard.MapError(ErrorCode.NotAuthorized));
        Assert.Equal(FailureKind.DeviceError, AddDeviceWizard.MapError(ErrorCode.Internal));
    }

    private static async Task<WizardChoosingNetwork> ReachNetworkStepAsync(AddDeviceWizard wizard)
    {
        await wizard.StartAsync();
        var scanning = (WizardScanning)(await TestHost.WaitForAsync(wizard,
            s => s is WizardScanning sc && sc.Devices.Any(d => d.Model is not null))).Value!;

        await wizard.SelectDeviceAsync(scanning.Devices.First(d => d.Model is not null).PeripheralId);
        return (WizardChoosingNetwork)(await TestHost.WaitForAsync(wizard,
            s => s is WizardChoosingNetwork { ScanStatus: NetworkScanStatus.Done } or WizardFailed)).Value!;
    }
}
