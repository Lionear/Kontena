using System.Xml;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace Kontena.Plugins.ManifestStudio.Views;

/// <summary>
/// The YAML syntax definition AvaloniaEdit does not ship (KON-427), plus the one thing an <c>.xshd</c>
/// file cannot express: a palette that follows the app's theme.
/// <para>
/// The editor surface is <c>CodeSurface</c>, which is <c>#141519</c> in dark and <b>white</b> in light
/// (KON-196 — a manifest is a document, not a terminal). A single hard-coded set of code colours would
/// therefore be unreadable in one of the two themes, so each named colour carries a value per theme and
/// <see cref="Apply"/> swaps them. One definition instance is shared by every editor because the whole
/// app has one theme at a time; re-parsing the xshd per tab would buy nothing.
/// </para>
/// </summary>
internal static class YamlHighlighting
{
    /// <summary>Dark on <c>#141519</c>, light on white. Both sets are the same hues at different
    /// lightness, so a document does not change character when the theme does.
    /// <para>
    /// The comment grey is deliberately lighter than the mockup's <c>#6b7686</c>: that measures 3.4:1
    /// on this surface, and a comment in a manifest is prose someone has to read, not chrome.
    /// </para></summary>
    private static readonly (string Name, string Dark, string Light)[] Palette =
    [
        ("Comment", "#8B949E", "#6E7781"),
        ("Key", "#79C0FF", "#0550AE"),
        ("String", "#A5D6FF", "#0A3069"),
        ("Number", "#F0B072", "#953800"),
        ("Constant", "#D2A8FF", "#8250DF"),
        ("Directive", "#7CC4FF", "#0550AE"),
    ];

    private static readonly Lazy<IHighlightingDefinition> Definition = new(Load);

    public static IHighlightingDefinition For(ThemeVariant theme)
    {
        var definition = Definition.Value;
        Apply(definition, theme);
        return definition;
    }

    private static void Apply(IHighlightingDefinition definition, ThemeVariant theme)
    {
        var light = theme == ThemeVariant.Light;

        foreach (var (name, dark, lightColour) in Palette)
        {
            if (definition.GetNamedColor(name) is { } colour)
                colour.Foreground = new SimpleHighlightingBrush(Color.Parse(light ? lightColour : dark));
        }
    }

    private static IHighlightingDefinition Load()
    {
        // Embedded rather than a file beside the dll: the plugin is unzipped by hand into a plugins
        // folder (Plans/plugin-store.md §0), and a definition that can go missing is a definition that
        // will.
        using var stream = typeof(YamlHighlighting).Assembly
            .GetManifestResourceStream("Kontena.Plugins.ManifestStudio.Resources.Yaml.xshd")
            ?? throw new InvalidOperationException("Yaml.xshd is not embedded in this assembly.");

        using var reader = XmlReader.Create(stream);
        return HighlightingLoader.Load(HighlightingLoader.LoadXshd(reader), HighlightingManager.Instance);
    }
}
