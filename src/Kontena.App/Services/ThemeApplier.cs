using Avalonia;
using Avalonia.Styling;
using Kontena.Core.Models;

namespace Kontena.App.Services;

/// <summary>Applies a <see cref="ThemePreference"/> to the running application.</summary>
public static class ThemeApplier
{
    public static void Apply(ThemePreference preference)
    {
        var app = Application.Current;
        if (app is null)
            return;

        app.RequestedThemeVariant = preference switch
        {
            ThemePreference.Light => ThemeVariant.Light,
            ThemePreference.Dark => ThemeVariant.Dark,
            _ => null, // System → follow the OS
        };
    }
}
