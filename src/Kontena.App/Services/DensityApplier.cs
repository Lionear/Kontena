using Avalonia;

namespace Kontena.App.Services;

/// <summary>
/// Applies the list-row density to the running application by swapping the shared
/// <c>RowMargin</c> and <c>RowPadding</c> resources, which every list row binds via
/// DynamicResource. Engine rows carry the spacing as a margin on their grid, cluster rows as
/// padding on their border — same vertical numbers, different horizontal inset (KON-441).
/// </summary>
public static class DensityApplier
{
    public static void Apply(bool compact)
    {
        var app = Application.Current;
        if (app is null)
            return;

        var vertical = compact ? 6 : 11;
        app.Resources["RowMargin"] = new Thickness(18, vertical);
        app.Resources["RowPadding"] = new Thickness(16, vertical);
    }
}
