using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.ViewModels;

// ConfigMaps and Secrets (KON-249). Both were browsable through the generic resource browser
// already — as raw YAML, which for a Secret means base64: unreadable and fully exposed at the same
// time, the worst of both. These two pages exist to undo exactly that pairing.

/// <summary>ConfigMaps — the same shape as the secrets page, minus every reason for the masking.</summary>
public partial class ClusterConfigMapsViewModel : ListPageViewModel<ConfigObjectRow>
{
    private readonly IClusterEngine _cluster;
    private readonly string? _namespace;

    public ClusterConfigMapsViewModel(IClusterEngine cluster, string? @namespace)
    {
        _cluster = cluster;
        _namespace = @namespace;
        _ = LoadAsync();
    }

    public override string SearchPlaceholder => "Search config maps…";

    protected override async Task<IReadOnlyList<ConfigObjectRow>> LoadRowsAsync() =>
    [
        .. (await _cluster.ListConfigMapsAsync(_namespace))
            .Select(c => new ConfigObjectRow(
                new ResourceRef(GroupVersionKind.ConfigMap, c.Namespace, c.Name),
                type: null, c.Keys, c.Age, _cluster.GetConfigDataAsync, secret: false)),
    ];

    // The key names too: "which config map holds nginx.conf" is a question the name alone cannot
    // answer, and it is the one you actually have.
    protected override bool Matches(ConfigObjectRow row, string term) =>
        Contains(row.Name, term) || Contains(row.Namespace, term) || row.MatchesKey(term);
}

/// <summary>Secrets — keys and sizes, with the values behind a deliberate act.</summary>
public partial class ClusterSecretsViewModel : ListPageViewModel<ConfigObjectRow>
{
    private readonly IClusterEngine _cluster;
    private readonly string? _namespace;

    public ClusterSecretsViewModel(IClusterEngine cluster, string? @namespace)
    {
        _cluster = cluster;
        _namespace = @namespace;
        _ = LoadAsync();
    }

    public override string SearchPlaceholder => "Search secrets…";

    protected override async Task<IReadOnlyList<ConfigObjectRow>> LoadRowsAsync() =>
    [
        .. (await _cluster.ListSecretsAsync(_namespace))
            .Select(s => new ConfigObjectRow(
                new ResourceRef(GroupVersionKind.Secret, s.Namespace, s.Name),
                s.Type, s.Keys, s.Age, _cluster.GetConfigDataAsync, secret: true)),
    ];

    protected override bool Matches(ConfigObjectRow row, string term) =>
        Contains(row.Name, term) || Contains(row.Namespace, term)
        || Contains(row.Type, term) || row.MatchesKey(term);
}

/// <summary>One ConfigMap or Secret: a row that opens to show its keys.</summary>
public sealed partial class ConfigObjectRow : ObservableObject
{
    private readonly Func<ResourceRef, CancellationToken, ValueTask<IReadOnlyList<ConfigEntry>>> _fetch;

    public ConfigObjectRow(
        ResourceRef reference, string? type, IReadOnlyList<ConfigKey> keys, TimeSpan age,
        Func<ResourceRef, CancellationToken, ValueTask<IReadOnlyList<ConfigEntry>>> fetch,
        bool secret)
    {
        ArgumentNullException.ThrowIfNull(keys);

        Reference = reference;
        _fetch = fetch;
        IsSecret = secret;

        Name = reference.Name;
        Namespace = reference.Namespace ?? "default";
        Type = string.IsNullOrEmpty(type) ? "—" : type;
        Age = Format.Duration(age);

        KeyCount = keys.Count switch
        {
            0 => "no keys",
            1 => "1 key",
            var n => $"{n} keys",
        };

        Keys = [.. keys.Select(k => new ConfigKeyRow(k, ResolveAsync, secret))];
    }

    public ResourceRef Reference { get; }
    public string Name { get; }
    public string Namespace { get; }
    public string Type { get; }
    public string Age { get; }
    public string KeyCount { get; }
    public bool IsSecret { get; }

    /// <summary>Whether the object's type column applies at all — ConfigMaps have none.</summary>
    public bool HasType => IsSecret;

    public ObservableCollection<ConfigKeyRow> Keys { get; }

    /// <summary>An object with no keys at all: valid, and worth seeing rather than an empty drawer.</summary>
    public bool HasKeys => Keys.Count > 0;

    [ObservableProperty] private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanExpand));
        OnPropertyChanged(nameof(CanCollapse));
    }

    /// <summary>
    /// Which way the chevron points, and whether there is one at all. An object with no keys has
    /// nothing to open, and a control that opens onto nothing is a dead button (KON-117).
    /// </summary>
    public bool CanExpand => HasKeys && !IsExpanded;

    public bool CanCollapse => HasKeys && IsExpanded;

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    public bool MatchesKey(string term) =>
        Keys.Any(k => k.Name.Contains(term, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Fetch one key's value, now.
    /// <para>
    /// Nothing is cached between requests, which is the point: hiding a value drops it, and showing
    /// it again asks the cluster again. A cache would mean a secret sitting in this process for as
    /// long as the page is open, having been shown once — and that is the state the whole design of
    /// this page exists to avoid.
    /// </para>
    /// </summary>
    private async Task<ConfigEntry?> ResolveAsync(string key)
    {
        var entries = await _fetch(Reference, CancellationToken.None);
        return entries.FirstOrDefault(e => e.Key == key);
    }
}

/// <summary>One key of a ConfigMap or Secret, and whether its value is on screen.</summary>
public sealed partial class ConfigKeyRow : ObservableObject
{
    private readonly Func<string, Task<ConfigEntry?>> _resolve;

    public ConfigKeyRow(ConfigKey key, Func<string, Task<ConfigEntry?>> resolve, bool secret)
    {
        _resolve = resolve;

        Name = key.Name;
        Size = Format.Size(key.SizeBytes);
        IsSecret = secret;

        // A ConfigMap has nothing to protect, so its values are simply there. Making the user press
        // Reveal on a LOG_LEVEL of "info" would teach them to press it without reading.
        if (!secret)
            _ = ShowAsync();
    }

    public string Name { get; }
    public string Size { get; }
    public bool IsSecret { get; }

    /// <summary>The value, when it is on screen. Null is both "not asked for" and "hidden again".</summary>
    [ObservableProperty] private string? _value;

    [ObservableProperty] private bool _isRevealed;
    [ObservableProperty] private bool _isBusy;

    /// <summary>Set when the value is bytes rather than text — a certificate, a keystore, an archive.</summary>
    [ObservableProperty] private bool _isBinary;

    /// <summary>What went wrong asking for the value; usually RBAC saying no.</summary>
    [ObservableProperty] private string? _error;

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>
    /// Binary values are never rendered as characters. Half of a TLS key drawn as text is noise, it
    /// can put a terminal into a state nobody asked for, and it is not what the value is anyway.
    /// </summary>
    public string BinaryNotice => $"{Size} of binary data — copy takes it as base64.";

    [RelayCommand]
    private async Task Toggle()
    {
        if (IsRevealed)
        {
            // Dropped, not merely hidden.
            Value = null;
            IsRevealed = false;
            IsBinary = false;
            Error = null;
            return;
        }

        await ShowAsync();
    }

    private async Task ShowAsync()
    {
        IsBusy = true;
        Error = null;

        try
        {
            var entry = await _resolve(Name);

            if (entry is null)
            {
                // The key was in the listing and is not in the object any more. Rare, and better
                // said than shown as an empty value.
                Error = "That key is not there any more — the object has changed since this page loaded.";
                return;
            }

            IsBinary = entry.IsBinary;
            Value = entry.Text;
            IsRevealed = true;
        }
        catch (Exception failure)
        {
            // Reading a secret is its own RBAC verb, and being allowed to list them does not mean
            // being allowed to read one. Saying so beats an empty box.
            Error = failure.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// The value for the clipboard, fetched on the spot.
    /// <para>
    /// Copying and revealing are separate acts, and wanting one without the other is the normal
    /// case: a password goes into a terminal far more often than it goes onto a screen someone else
    /// can see. So this never sets <see cref="Value"/> — nothing it returns is bound to anything.
    /// </para>
    /// </summary>
    public async Task<string?> ForClipboardAsync()
    {
        try
        {
            var entry = await _resolve(Name);

            // Binary goes out as base64: the form the cluster stores it in, the form every other
            // tool takes it back in, and the only one that survives a clipboard whole.
            return entry is null ? null : entry.Text ?? entry.Base64;
        }
        catch (Exception failure)
        {
            Error = failure.Message;
            return null;
        }
    }
}
