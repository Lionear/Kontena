using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.ViewModels;

/// <summary>
/// Edit one object's live manifest and put it back (KON-252).
/// <para>
/// Kontena could already fetch a manifest and apply a bundle, and nowhere were the two one act
/// except on pod detail — which grew its own copy of the flow. This is that flow, extracted, so the
/// detail pages and the config/secret editor share one implementation rather than three that drift.
/// </para>
/// <para>
/// <b>YAML rather than a field editor</b>, and most deliberately for a Secret: an editor that
/// base64-encodes behind your back is pleasanter and riskier, because you cannot see what is
/// actually going to the cluster. The manifest is what the cluster stores.
/// </para>
/// </summary>
public partial class ManifestEditorViewModel : ViewModelBase
{
    private readonly IClusterEngine _cluster;
    private readonly ResourceRef _reference;
    private string _original = string.Empty;

    public ManifestEditorViewModel(IClusterEngine cluster, ResourceRef reference)
    {
        _cluster = cluster;
        _reference = reference;
        _ = LoadAsync();
    }

    /// <summary>What is being edited, for a dialog that needs a title.</summary>
    public string Title => $"{_reference.Kind.Kind} {_reference.Name}";

    public string Subtitle => _reference.Namespace is { Length: > 0 } ns ? ns : "cluster-scoped";

    [ObservableProperty] private string _text = string.Empty;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isBusy;

    /// <summary>The result of the last check or apply, in the cluster's words where there are any.</summary>
    [ObservableProperty] private string? _status;

    [ObservableProperty] private bool _statusIsError;

    /// <summary>Whether the object can be written at all — false hides both buttons.</summary>
    public bool CanWrite => _cluster.Capabilities.Apply;

    partial void OnTextChanged(string value) => Recompute();

    partial void OnIsBusyChanged(bool value) => Recompute();

    private void Recompute()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanApply));
    }

    /// <summary>Whether the editor holds something other than what was fetched.</summary>
    public bool IsDirty => !string.Equals(Text, _original, StringComparison.Ordinal);

    public bool CanApply => CanWrite && IsDirty && !IsBusy && !IsLoading;

    public IBrush StatusBrush =>
        new SolidColorBrush(Color.Parse(StatusIsError ? "#F87171" : "#34D399"));

    partial void OnStatusIsErrorChanged(bool value) => OnPropertyChanged(nameof(StatusBrush));

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            _original = await _cluster.GetManifestAsync(_reference);
            Text = _original;
            Status = null;
        }
        catch (Exception failure)
        {
            StatusIsError = true;
            Status = failure.Message;
            Text = "# Could not fetch the manifest.";
        }
        finally
        {
            IsLoading = false;
            Recompute();
        }
    }

    /// <summary>Throw away the edits and go back to what the cluster holds.</summary>
    [RelayCommand]
    private void Revert()
    {
        Text = _original;
        Status = null;
        StatusIsError = false;
    }

    /// <summary>
    /// Ask the cluster what this would do, without doing it.
    /// <para>
    /// Server-side dry-run, so it is the apiserver's own validation and defaulting rather than a
    /// guess — and on a Secret, "what exactly am I about to change" is not a luxury. Offered as its
    /// own button rather than run automatically before every apply: a check that always happens is a
    /// check nobody reads, and it costs a round trip on every keystroke's worth of confidence.
    /// </para>
    /// </summary>
    [RelayCommand]
    private Task CheckAsync() => SendAsync(dryRun: true);

    [RelayCommand]
    private Task ApplyAsync() => SendAsync(dryRun: false);

    private async Task SendAsync(bool dryRun)
    {
        if (!CanApply)
            return;

        IsBusy = true;
        Status = null;
        StatusIsError = false;

        try
        {
            var results = new List<ApplyProgress>();
            await foreach (var step in _cluster.ApplyAsync(
                new ManifestBundle { Yaml = Text, Source = "editor", DryRun = dryRun }))
            {
                results.Add(step);
            }

            if (results.Find(r => r.Action == ApplyAction.Failed) is { } failed)
            {
                // The apiserver's own message. It names the field it rejected, and summarising that
                // into "apply failed" sends someone to a terminal to find out which.
                StatusIsError = true;
                Status = failed.Error ?? "The cluster refused it.";
                return;
            }

            if (results.TrueForAll(r => r.Action == ApplyAction.Unchanged))
            {
                Status = dryRun
                    ? "No change — this manifest already matches what the cluster holds."
                    : "No change — the manifest already matched.";
                return;
            }

            var what = string.Join(", ", results.Select(
                r => $"{r.Resource.Kind.Kind}/{r.Resource.Name} {r.Action.ToString().ToLowerInvariant()}"));

            if (dryRun)
            {
                // Said in the future tense, because nothing has happened. A dry-run that reports
                // "configured" reads as done, and then the Apply button looks redundant.
                Status = $"Would change · {what}";
                return;
            }

            // Re-read rather than assume: defaulting, admission webhooks and other controllers all
            // get a say, so what the cluster now holds is not simply what was typed.
            //
            // And set the message *after*, not before: LoadAsync clears the status, which is right
            // when you open the editor and wrong here — it wiped the only confirmation that the
            // apply had worked, leaving a page that looked as though nothing had happened.
            await LoadAsync();
            Status = $"Applied · {what}";
        }
        catch (Exception failure)
        {
            StatusIsError = true;
            Status = failure.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
