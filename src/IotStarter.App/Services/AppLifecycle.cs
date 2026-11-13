using IotStarter.App.Core.Platform;
using IotStarter.App.Core.State;
using Microsoft.Extensions.Logging;

namespace IotStarter.App.Services;

/// <summary>
/// Re-runs discovery when the app starts or resumes, when the phone changes network and when demo
/// mode is toggled (pull-to-refresh calls <see cref="DeviceManager.RefreshAsync"/> directly).
/// </summary>
public sealed class AppLifecycle(
    DeviceManager devices,
    IotStarter.App.Core.Demo.DemoSession demo,
    INetworkStatus network,
    IAppSettings settings,
    ILogger<AppLifecycle> logger)
{
    private bool _started;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        network.Changed += (_, _) => Run(refreshOnly: true);
        settings.Changed += OnSettingsChanged;
        Run(refreshOnly: false);
    }

    public void OnResumed() => Run(refreshOnly: true);

    private bool _lastDemoMode;

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (settings.DemoMode != _lastDemoMode)
        {
            Run(refreshOnly: false);
        }
    }

    private void Run(bool refreshOnly) => _ = RunAsync(refreshOnly);

    private async Task RunAsync(bool refreshOnly)
    {
        try
        {
            if (!refreshOnly)
            {
                _lastDemoMode = settings.DemoMode;
                if (settings.DemoMode)
                {
                    await demo.RestoreAsync(CancellationToken.None).ConfigureAwait(false);
                }

                await devices.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await devices.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Refreshing devices failed");
        }
    }
}
