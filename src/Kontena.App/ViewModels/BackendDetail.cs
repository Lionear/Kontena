namespace Kontena.App.ViewModels;

/// <summary>A probe's detail line, <c>"{version} · {address}"</c>, taken apart in one place.</summary>
public static class BackendDetail
{
    private const string Separator = " · ";

    /// <summary>What stands in for an address the user chose to hide (KON-410). Fixed length, so it
    /// does not give away how long the address is.</summary>
    public const string Mask = "••••••••";

    /// <summary>The version and the address; the address is empty when the line has none.</summary>
    public static (string Version, string Address) Split(string detail)
    {
        var at = detail.IndexOf(Separator, StringComparison.Ordinal);
        return at < 0 ? (detail, string.Empty) : (detail[..at], detail[(at + Separator.Length)..]);
    }
}
