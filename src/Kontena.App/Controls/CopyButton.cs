using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;

namespace Kontena.App.Controls;

/// <summary>
/// A button that puts <see cref="Text"/> on the clipboard (KON-481) — the resource name beside every
/// detail page's title.
/// <para>
/// The same pill-and-clipboard move the internal hostname (KON-447) and the port forward's address
/// already make, named once instead of an <c>OnCopyClick</c> in the code-behind of each of a dozen
/// detail views. The clipboard hangs off the window, so the copy stays in the view layer rather than
/// dragging a TopLevel into a view model. Styled as a plain <see cref="Button"/>, so <c>pill</c> and
/// friends apply.
/// </para>
/// </summary>
public sealed class CopyButton : Button
{
    /// <summary>What a click copies. Null or empty copies nothing.</summary>
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<CopyButton, string?>(nameof(Text));

    public CopyButton() => Content = "Copy";

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(Button);

    protected override async void OnClick()
    {
        base.OnClick();
        if (!string.IsNullOrEmpty(Text) && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetValueAsync(DataFormat.Text, Text);
    }
}
