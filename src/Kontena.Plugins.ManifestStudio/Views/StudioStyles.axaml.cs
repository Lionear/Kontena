using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Kontena.Plugins.ManifestStudio.Views;

/// <summary>The studio pages' shared style vocabulary — see <c>StudioStyles.axaml</c> for what is in it
/// and why it is a compiled <see cref="Styles"/> rather than a <c>StyleInclude</c>.</summary>
public sealed class StudioStyles : Styles
{
    public StudioStyles() => AvaloniaXamlLoader.Load(this);
}
