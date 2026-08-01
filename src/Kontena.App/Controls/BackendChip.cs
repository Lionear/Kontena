using Avalonia;
using Avalonia.Controls;

using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Kontena.Core.Models;
using Kontena.Core.Orchestration;

namespace Kontena.App.Controls;

/// <summary>
/// The square badge that says which backend something belongs to (KON-80): the product's own mark on a
/// wash of its colour, or a letter when no mark was declared.
/// <para>
/// One control rather than the six copies it replaces. Each of those hard-coded the plate
/// (<c>DockerChip</c>) and the ink (Docker's blue) around a bound letter, so a Podman chip was a violet
/// plate with a Docker-blue P on it — the bug that came with duplicating a chip five times instead of
/// naming it once.
/// </para>
/// </summary>
public sealed class BackendChip : UserControl
{
    /// <summary>Parsed once per mark: the same path data is drawn on every row of a list.</summary>
    private static readonly Dictionary<string, Geometry> Glyphs = new(StringComparer.Ordinal);

    /// <summary>What to draw. Null renders nothing rather than an empty plate.</summary>
    public static readonly StyledProperty<BackendChipInfo?> ChipProperty =
        AvaloniaProperty.Register<BackendChip, BackendChipInfo?>(nameof(Chip));

    /// <summary>Edge length in pixels; the corner radius and the mark scale with it.</summary>
    public static readonly StyledProperty<double> ChipSizeProperty =
        AvaloniaProperty.Register<BackendChip, double>(nameof(ChipSize), 26d);

    public BackendChipInfo? Chip
    {
        get => GetValue(ChipProperty);
        set => SetValue(ChipProperty, value);
    }

    public double ChipSize
    {
        get => GetValue(ChipSizeProperty);
        set => SetValue(ChipSizeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ChipProperty || change.Property == ChipSizeProperty)
            Rebuild();
    }

    /// <summary>
    /// The brand ink is measured against the theme, so switching theme has to recompute it — a
    /// DynamicResource cannot, because the colour comes from a provider rather than the palette.
    /// </summary>
    public BackendChip() => ActualThemeVariantChanged += (_, _) => Rebuild();

    private void Rebuild()
    {
        if (Chip is not { } chip)
        {
            Content = null;
            return;
        }

        var size = ChipSize;
        var plate = new Border
        {
            Width = size,
            Height = size,
            // 7 at 26px, which is what every hand-written chip used; kept proportional so a 16px row
            // badge and a 38px settings badge are the same shape.
            CornerRadius = new CornerRadius(Math.Round(size * 0.27)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        if (chip.HasGlyph)
        {
            var dark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
            var ink = BackendChipInk.For(chip.Accent!, dark);

            plate.Background = new SolidColorBrush(Color.Parse(chip.Accent!))
            {
                Opacity = BackendChipInk.PlateOpacity,
            };
            plate.Child = new Avalonia.Controls.Shapes.Path
            {
                Data = Glyph(chip.Glyph!),
                Fill = new SolidColorBrush(ink),
                Width = Math.Round(size * 0.6),
                Height = Math.Round(size * 0.6),
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        else
        {
            // No mark: a letter on the palette's own info wash. It used to be Docker's blue on Docker's
            // wash for every backend, which said "Docker" about the demo cluster and the remote engine.
            plate[!BackgroundProperty] = new DynamicResourceExtension("InfoSoft");

            var letter = new TextBlock
            {
                Text = chip.Letter,
                FontWeight = FontWeight.Bold,
                FontSize = Math.Round(size * 0.42),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            letter[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Info");
            letter[!TextBlock.FontFamilyProperty] = new DynamicResourceExtension("Mono");

            plate.Child = letter;
        }

        Content = plate;
    }

    private static Geometry Glyph(string data)
    {
        if (Glyphs.TryGetValue(data, out var cached))
            return cached;

        var parsed = Geometry.Parse(data);
        Glyphs[data] = parsed;
        return parsed;
    }
}
