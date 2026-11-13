using IotStarter.App.Core.Devices;

namespace IotStarter.App.Core.Demo;

/// <summary>
/// The simulated world lives in memory, while demo devices added in earlier sessions are saved like
/// real ones. On start-up this puts those devices back "on the network" in the simulator, with
/// their saved token and address, so demo mode survives an app restart.
/// </summary>
public sealed class DemoSession(DemoWorld world, IDeviceStore store, ITokenStore tokens)
{
    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        foreach (DeviceRecord record in await store.GetAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!record.IsDemo)
            {
                continue;
            }

            DemoDevice? device = world.Devices.FirstOrDefault(d => string.Equals(d.DeviceId, record.Id, StringComparison.OrdinalIgnoreCase));
            string? token = await tokens.GetAsync(record.Id).ConfigureAwait(false);
            if (device is null || token is null || device.Provisioned)
            {
                continue;
            }

            world.Provision(device, token);
            device.Name = record.Name;
            if (record.LastIPv4 is not null)
            {
                device.IPv4 = record.LastIPv4;
            }
        }
    }
}
