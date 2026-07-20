using Avalonia;

namespace Kontena.App.Services;

/// <summary>
/// Applies the list-row density to the running application by swapping the shared
/// <c>RowMargin</c> resource, which every list-row grid binds via DynamicResource.
/// </summary>
public static class DensityApplier
{
    public static void Apply(bool compact)
    {
        var app = Application.Current;
        if (app is null)
            return;

        app.Resources["RowMargin"] = new Thickness(18, compact ? 6 : 11);
    }
}
