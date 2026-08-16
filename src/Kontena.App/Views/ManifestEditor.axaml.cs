using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Threading;
using AvaloniaEdit.Document;

namespace Kontena.App.Views;

/// <summary>
/// A manifest, in an editor that only lays out the lines on screen (KON-382).
/// <para>
/// An Avalonia <c>TextBox</c> lays out every line it is given, and <c>kube-prometheus-stack</c>
/// renders 5.2 MB across 82,000 lines — four fifths of it CRD schema. That measured at close to six
/// seconds of frozen window, which KON-380 had to cap at 512 KB to make the page usable at all.
/// <c>TextEditor</c> virtualises its visual lines, so the whole bundle goes in and there is nothing
/// left to cap.
/// </para>
/// <para>
/// Wrapped rather than used directly so a view-model keeps handing the page a plain string, the same
/// arrangement <c>Kontena.Plugins.ManifestStudio</c> uses for its own editor.
/// </para>
/// </summary>
public partial class ManifestEditor : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<ManifestEditor, string>(
            nameof(Text), defaultValue: string.Empty, defaultBindingMode: BindingMode.TwoWay);

    /// <summary>
    /// Above this many characters the document is built on a background thread. Parsing is the
    /// expensive half of showing a big bundle, and it is the half that does not have to happen on
    /// the thread that draws — the lesson KON-381 paid for on the apply itself. Below it the thread
    /// hop costs more than the parse, and buys a frame of the previous document instead.
    /// </summary>
    private const int OffThreadFrom = 256 * 1024;

    // Guards the two-way sync from re-entering itself: loading a document raises TextChanged, which
    // would otherwise write Text straight back and start over.
    private bool _syncing;

    // A big document is parsed while the next bundle may already be arriving. Only the newest wins.
    private int _generation;

    public ManifestEditor()
    {
        InitializeComponent();

        Editor.TextChanged += (_, _) =>
        {
            if (_syncing)
                return;

            _syncing = true;
            SetCurrentValue(TextProperty, Editor.Document.Text);
            _syncing = false;
        };

        Load(Text);
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TextProperty && !_syncing)
            Load(Text);
    }

    private void Load(string text)
    {
        var generation = ++_generation;

        if (text.Length < OffThreadFrom)
        {
            Show(new TextDocument(text), generation);
            return;
        }

        _ = LoadOffThreadAsync(text, generation);
    }

    private async Task LoadOffThreadAsync(string text, int generation)
    {
        // A TextDocument pins itself to the thread that built it, so ownership is released here and
        // claimed again on the UI thread in Show.
        var document = await Task.Run(() =>
        {
            var built = new TextDocument(text);
            built.SetOwnerThread(null);
            return built;
        });

        await Dispatcher.UIThread.InvokeAsync(() => Show(document, generation));
    }

    private void Show(TextDocument document, int generation)
    {
        // A newer bundle arrived while this one was being parsed; that one is the one to show.
        if (generation != _generation)
            return;

        document.SetOwnerThread(Thread.CurrentThread);

        _syncing = true;
        Editor.Document = document;
        _syncing = false;
    }
}
