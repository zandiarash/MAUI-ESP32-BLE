namespace IotStarter.App.Core.Platform;

/// <summary>User settings: theme, language and demo mode.</summary>
public interface IAppSettings
{
    ThemePreference Theme { get; set; }

    /// <summary>BCP-47 culture name, or null to follow the system.</summary>
    string? Language { get; set; }

    /// <summary>When on, simulated BLE and network devices replace the real ones.</summary>
    bool DemoMode { get; set; }

    event EventHandler? Changed;
}

public sealed class AppSettings(IKeyValueStore store) : IAppSettings
{
    private const string ThemeKey = "settings.theme";
    private const string LanguageKey = "settings.language";
    private const string DemoKey = "settings.demo";

    public event EventHandler? Changed;

    public ThemePreference Theme
    {
        get => Enum.TryParse(store.Get(ThemeKey), out ThemePreference theme) ? theme : ThemePreference.System;
        set => Update(ThemeKey, value.ToString(), Theme.ToString());
    }

    public string? Language
    {
        get => store.Get(LanguageKey) is { Length: > 0 } language ? language : null;
        set => Update(LanguageKey, value ?? string.Empty, Language ?? string.Empty);
    }

    public bool DemoMode
    {
        get => store.Get(DemoKey) == bool.TrueString;
        set => Update(DemoKey, value ? bool.TrueString : bool.FalseString, DemoMode ? bool.TrueString : bool.FalseString);
    }

    private void Update(string key, string value, string current)
    {
        if (value == current)
        {
            return;
        }

        store.Set(key, value);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
