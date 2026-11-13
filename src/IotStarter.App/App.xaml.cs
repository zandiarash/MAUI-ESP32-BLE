using IotStarter.App.Services;

namespace IotStarter.App;

public partial class App : Application
{
    private readonly AppLifecycle _lifecycle;

    public App(AppLifecycle lifecycle)
    {
        InitializeComponent();
        _lifecycle = lifecycle;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new MainPage()) { Title = "IoT Starter" };

        // Discovery runs on start, on resume and on network changes (see AppLifecycle).
        window.Created += (_, _) => _lifecycle.Start();
        window.Resumed += (_, _) => _lifecycle.OnResumed();
        return window;
    }
}
