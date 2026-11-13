using System.Globalization;
using IotStarter.App.Core.Platform;

namespace IotStarter.App.Services;

/// <summary>
/// Chooses the UI culture and text direction. Only English ships today; a translation becomes
/// active by adding Strings.&lt;culture&gt;.resx and listing the culture in <see cref="SupportedCultures"/>.
/// Right-to-left cultures switch the whole layout to RTL. "RTL preview" renders English in RTL so
/// layouts can be checked before a translation exists.
/// </summary>
public sealed class UiCulture(IAppSettings settings)
{
    /// <summary>Settings value for the right-to-left layout preview.</summary>
    public const string RtlPreview = "rtl-preview";

    /// <summary>Cultures with a resource file (neutral names).</summary>
    public static readonly string[] SupportedCultures = ["en"];

    private readonly CultureInfo _system = CultureInfo.CurrentUICulture;

    public CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en");

    public bool IsRightToLeft { get; private set; }

    public string Direction => IsRightToLeft ? "rtl" : "ltr";

    /// <summary>Applies the saved language choice to the current and future threads.</summary>
    public void Apply()
    {
        string? choice = settings.Language;
        bool preview = choice == RtlPreview;
        CultureInfo culture = Resolve(preview ? null : choice);

        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        Culture = culture;
        IsRightToLeft = preview || culture.TextInfo.IsRightToLeft;
    }

    private CultureInfo Resolve(string? choice)
    {
        foreach (string? candidate in new[] { choice, _system.Name, _system.TwoLetterISOLanguageName })
        {
            if (string.IsNullOrEmpty(candidate))
            {
                continue;
            }

            CultureInfo culture;
            try
            {
                culture = CultureInfo.GetCultureInfo(candidate);
            }
            catch (CultureNotFoundException)
            {
                continue;
            }

            if (SupportedCultures.Contains(culture.TwoLetterISOLanguageName))
            {
                // Use the system's regional variant (number/date formats) when the language matches.
                return choice is null && culture.TwoLetterISOLanguageName == _system.TwoLetterISOLanguageName ? _system : culture;
            }
        }

        return CultureInfo.GetCultureInfo("en");
    }
}
