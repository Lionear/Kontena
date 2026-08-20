using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Kontena.App.ViewModels;

// KON-418 — DESIGN PREVIEW, not wired to anything yet.
//
// Editing a Secret today opens the live manifest in a YAML editor (KON-252), which for a Secret puts
// every value on screen as base64: unreadable and fully exposed at once — the exact pairing the
// Secrets page was built to undo (KON-249). This is what that editor looks like as fields.
//
// The data below is held in memory rather than fetched, because the shape is what is up for review.
// The real one has nothing extra to fetch: EditManifestDialogViewModel already pulls the whole
// manifest, values included, so a field view is a second rendering of a document that is in the
// process either way — and a masking one, where the YAML view shows the same bytes in the clear.

/// <summary>
/// The structured Secret editor (KON-418): one row per key, values masked, YAML one toggle away.
/// </summary>
public partial class SecretEditDialogViewModel : ViewModelBase
{
    public SecretEditDialogViewModel(
        string name, string @namespace, string type, string age,
        IEnumerable<SecretFieldRow> keys, string yaml)
    {
        Title = $"Secret {name}";
        Subtitle = @namespace;
        TypeText = type;
        AgeText = age;
        Keys = [.. keys];
        Yaml = yaml;

        foreach (var row in Keys)
            Watch(row);
    }

    public string Title { get; }
    public string Subtitle { get; }
    public string TypeText { get; }
    public string AgeText { get; }

    public ObservableCollection<SecretFieldRow> Keys { get; }

    public string KeyCountText => Keys.Count == 1 ? "1 key" : $"{Keys.Count} keys";

    /// <summary>
    /// The same document the fields are a view of. Kept as the escape hatch rather than replaced:
    /// type, labels, annotations and binary data have no field here, and the objection recorded on
    /// <see cref="ManifestEditorViewModel"/> — that an editor which base64-encodes behind your back
    /// hides what actually goes to the cluster — is answered by being able to look.
    /// </summary>
    [ObservableProperty] private string _yaml = string.Empty;

    [ObservableProperty] private bool _isYamlSelected;

    public bool IsFieldsSelected => !IsYamlSelected;

    partial void OnIsYamlSelectedChanged(bool value) => OnPropertyChanged(nameof(IsFieldsSelected));

    [RelayCommand]
    private void SelectTab(string tab) => IsYamlSelected = tab == "yaml";

    /// <summary>The result of the last check or apply, in the cluster's words where there are any.</summary>
    [ObservableProperty] private string? _status;

    [ObservableProperty] private bool _statusIsError;

    public IBrush StatusBrush =>
        new SolidColorBrush(Color.Parse(StatusIsError ? "#F87171" : "#34D399"));

    partial void OnStatusIsErrorChanged(bool value) => OnPropertyChanged(nameof(StatusBrush));

    /// <summary>Whether anything here differs from what the cluster holds.</summary>
    public bool IsDirty => Keys.Any(k => k.IsChanged || k.IsNew);

    [RelayCommand]
    private void AddKey()
    {
        // No size: a key that is not in the cluster yet has none, and "0 B" reads as a value that
        // is there and empty.
        var row = new SecretFieldRow(string.Empty, string.Empty, string.Empty) { IsNew = true, IsRevealed = true };
        Watch(row);
        Keys.Add(row);
        Changed();
    }

    private void Watch(SecretFieldRow row)
    {
        row.Removed = Remove;
        row.PropertyChanged += (_, _) => Changed();
    }

    private void Remove(SecretFieldRow row)
    {
        Keys.Remove(row);
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(KeyCountText));
        OnPropertyChanged(nameof(IsDirty));
    }
}

/// <summary>
/// One key of the Secret: its name, its value, and whether the value is on screen.
/// <para>
/// The value is masked, not absent — the manifest that was fetched to open the editor carries it
/// either way, so hiding it is about the screen, not about the process. That is a different promise
/// from the detail page's, where a value is fetched per key and dropped again, and it is the one
/// worth confirming before this gets built.
/// </para>
/// </summary>
public sealed partial class SecretFieldRow : ObservableObject
{
    private readonly string _originalName;
    private readonly string _originalValue;

    public SecretFieldRow(string name, string size, string value, bool binary = false)
    {
        _originalName = name;
        _originalValue = value;
        Name = name;
        Size = size;
        Value = value;
        IsBinary = binary;
    }

    /// <summary>Set by the dialog so a row can take itself off the list.</summary>
    public Action<SecretFieldRow>? Removed { get; set; }

    [ObservableProperty] private string _name;

    [ObservableProperty] private string _value;

    public string Size { get; }

    /// <summary>Bytes rather than text — a certificate, a keystore, an archive.</summary>
    public bool IsBinary { get; }

    /// <summary>
    /// Never drawn as characters, and never editable as characters either: half a TLS key in a text
    /// box is not what the value is, and one keystroke would corrupt the whole of it.
    /// </summary>
    public string BinaryNotice => $"{Size} of binary data — edit this key in the YAML view.";

    [ObservableProperty] private bool _isNew;

    partial void OnNameChanged(string value) => Recompute();

    partial void OnValueChanged(string value) => Recompute();

    private void Recompute()
    {
        OnPropertyChanged(nameof(IsChanged));
        OnPropertyChanged(nameof(RevealTip));
        OnPropertyChanged(nameof(RemoveTip));
    }

    public bool IsChanged =>
        !IsNew && (!string.Equals(Name, _originalName, StringComparison.Ordinal)
                   || !string.Equals(Value, _originalValue, StringComparison.Ordinal));

    [ObservableProperty] private bool _isRevealed;

    partial void OnIsRevealedChanged(bool value) => OnPropertyChanged(nameof(RevealTip));

    /// <summary>
    /// The tooltip of an icon-only button, which is also its accessible name (KON-56) — so it says
    /// which of the two pressing it does, and which row it belongs to. A dialog full of buttons all
    /// called "Show the value" tells a screen reader nothing (KON-416).
    /// </summary>
    public string RevealTip =>
        Name is { Length: > 0 } key
            ? IsRevealed ? $"Hide the value of {key}" : $"Show the value of {key}"
            : IsRevealed ? "Hide the value" : "Show the value";

    /// <summary>Same rule as <see cref="RevealTip"/>: an icon-only button says which row it is on.</summary>
    public string RemoveTip =>
        Name is { Length: > 0 } key ? $"Remove the key {key}" : "Remove this key";

    [RelayCommand]
    private void Toggle() => IsRevealed = !IsRevealed;

    /// <summary>Back to what the cluster holds, for this row alone.</summary>
    [RelayCommand]
    private void Undo()
    {
        Name = _originalName;
        Value = _originalValue;
    }

    [RelayCommand]
    private void Remove() => Removed?.Invoke(this);
}
