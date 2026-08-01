using CommunityToolkit.Mvvm.ComponentModel;

namespace Kontena.Plugins.ManifestStudio.Workspace;

/// <summary>
/// One open tab. Dirty is computed against what was last read from or written to disk — not a flag
/// that has to be remembered to flip — so an edit that happens to reproduce the saved text is not
/// dirty either.
/// </summary>
public sealed partial class OpenDocument : ObservableObject
{
    private string _savedText;

    private OpenDocument(string path, string text)
    {
        Path = path;
        _savedText = text;
        _text = text;
    }

    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _text;

    public bool IsDirty => Text != _savedText;

    public static OpenDocument Load(string path) => new(path, File.ReadAllText(path));

    public void Save()
    {
        File.WriteAllText(Path, Text);
        _savedText = Text;
        OnPropertyChanged(nameof(IsDirty));
    }
}
