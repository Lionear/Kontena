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
public partial class ClusterConfigMapsViewModel : ClusterListPageViewModel<ConfigObjectRow>
{
    private readonly IClusterEngine _cluster;
    private readonly string? _namespace;

    public ClusterConfigMapsViewModel(IClusterEngine cluster, string? @namespace)
        : base(cluster, GroupVersionKind.ConfigMap, @namespace)
    {
        _cluster = cluster;
        _namespace = @namespace;
        _ = LoadAsync();
        StartWatching();
    }

    /// <summary>Delete, always confirmed (KON-253). Shared with the secrets page below.</summary>
    private void ConfirmDelete(ConfigObjectRow row) => ConfigDelete.Confirm(this, _cluster, row, LoadAsync);

    /// <summary>Opens the manifest editor; the shell owns the modal (KON-252).</summary>
    public Action<ResourceRef>? RequestEdit { get; set; }

    private void Edit(ConfigObjectRow row) => RequestEdit?.Invoke(row.Reference);

    /// <summary>Opens the detail in the drawer; the shell owns that too (KON-330).</summary>
    public Action<ConfigObjectRow>? RequestOpenDetail { get; set; }

    private void Open(ConfigObjectRow row) => RequestOpenDetail?.Invoke(row);

    public override string SearchPlaceholder => "Search config maps…";

    protected override async Task<IReadOnlyList<ConfigObjectRow>> LoadRowsAsync(CancellationToken ct) =>
    [
        .. (await _cluster.ListConfigMapsAsync(_namespace, ct))
            .Select(c => new ConfigObjectRow(
                new ResourceRef(GroupVersionKind.ConfigMap, c.Namespace, c.Name),
                type: null, c.Keys, c.Age, _cluster.GetConfigDataAsync, secret: false,
                onDelete: ConfirmDelete, onEdit: Edit, onOpen: Open)),
    ];

    // The key names too: "which config map holds nginx.conf" is a question the name alone cannot
    // answer, and it is the one you actually have.
    protected override bool Matches(ConfigObjectRow row, string term) =>
        Contains(row.Name, term) || Contains(row.Namespace, term) || row.MatchesKey(term);
}

/// <summary>Secrets — keys and sizes, with the values behind a deliberate act.</summary>
public partial class ClusterSecretsViewModel : ClusterListPageViewModel<ConfigObjectRow>
{
    private readonly IClusterEngine _cluster;
    private readonly string? _namespace;

    // Following secrets moves no values: the watch carries the same metadata the listing does, and a
    // reload rebuilds rows of key names and sizes. The page's rule — a value only leaves the cluster
    // when asked for, one key at a time — is untouched by being live.
    public ClusterSecretsViewModel(IClusterEngine cluster, string? @namespace)
        : base(cluster, GroupVersionKind.Secret, @namespace)
    {
        _cluster = cluster;
        _namespace = @namespace;
        _ = LoadAsync();
        StartWatching();
    }

    private void ConfirmDelete(ConfigObjectRow row) => ConfigDelete.Confirm(this, _cluster, row, LoadAsync);

    /// <summary>Opens the manifest editor; the shell owns the modal (KON-252).</summary>
    public Action<ResourceRef>? RequestEdit { get; set; }

    private void Edit(ConfigObjectRow row) => RequestEdit?.Invoke(row.Reference);

    /// <summary>Opens the detail in the drawer; the shell owns that too (KON-330).</summary>
    public Action<ConfigObjectRow>? RequestOpenDetail { get; set; }

    private void Open(ConfigObjectRow row) => RequestOpenDetail?.Invoke(row);

    public override string SearchPlaceholder => "Search secrets…";

    protected override async Task<IReadOnlyList<ConfigObjectRow>> LoadRowsAsync(CancellationToken ct) =>
    [
        .. (await _cluster.ListSecretsAsync(_namespace, ct))
            .Select(s => new ConfigObjectRow(
                new ResourceRef(GroupVersionKind.Secret, s.Namespace, s.Name),
                s.Type, s.Keys, s.Age, _cluster.GetConfigDataAsync, secret: true,
                onDelete: ConfirmDelete, onEdit: Edit, onOpen: Open)),
    ];

    protected override bool Matches(ConfigObjectRow row, string term) =>
        Contains(row.Name, term) || Contains(row.Namespace, term)
        || Contains(row.Type, term) || row.MatchesKey(term);
}

/// <summary>One ConfigMap or Secret in the list: what it is, and the way in to its detail.</summary>
public sealed partial class ConfigObjectRow : ObservableObject
{
    private readonly Func<ResourceRef, CancellationToken, ValueTask<IReadOnlyList<ConfigEntry>>> _fetch;

    private readonly Action<ConfigObjectRow>? _onDelete;
    private readonly Action<ConfigObjectRow>? _onEdit;
    private readonly Action<ConfigObjectRow>? _onOpen;

    public ConfigObjectRow(
        ResourceRef reference, string? type, IReadOnlyList<ConfigKey> keys, TimeSpan age,
        Func<ResourceRef, CancellationToken, ValueTask<IReadOnlyList<ConfigEntry>>> fetch,
        bool secret, Action<ConfigObjectRow>? onDelete = null, Action<ConfigObjectRow>? onEdit = null,
        Action<ConfigObjectRow>? onOpen = null)
    {
        ArgumentNullException.ThrowIfNull(keys);

        Reference = reference;
        _fetch = fetch;
        _onDelete = onDelete;
        _onEdit = onEdit;
        _onOpen = onOpen;
        IsSecret = secret;
        CanDelete = onDelete is not null;
        CanEdit = onEdit is not null;
        CanOpen = onOpen is not null;

        Name = reference.Name;
        Namespace = reference.Namespace ?? "default";
        Type = string.IsNullOrEmpty(type) ? "—" : type;
        Age = Format.Duration(age);
        Keys = keys;

        KeyCount = keys.Count switch
        {
            0 => "no keys",
            1 => "1 key",
            var n => $"{n} keys",
        };
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

    /// <summary>
    /// The key names and sizes that came with the listing. Names only — the list holds no values at
    /// all, which is what lets a page of fifty secrets exist without any of them being anywhere in
    /// this process. Asking for one is the detail's job (KON-330).
    /// </summary>
    public IReadOnlyList<ConfigKey> Keys { get; }

    /// <summary>Whether the page wired a delete handler (KON-253).</summary>
    public bool CanDelete { get; }

    [RelayCommand]
    private void Delete() => _onDelete?.Invoke(this);

    /// <summary>Whether the page wired the manifest editor (KON-252).</summary>
    public bool CanEdit { get; }

    [RelayCommand]
    private void Edit() => _onEdit?.Invoke(this);

    /// <summary>Whether the page wired the detail (KON-330).</summary>
    public bool CanOpen { get; }

    [RelayCommand]
    private void Open() => _onOpen?.Invoke(this);

    public bool MatchesKey(string term) =>
        Keys.Any(k => k.Name.Contains(term, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The key rows for this object's detail, each able to fetch its own value.
    /// <para>
    /// Built on request rather than held on the row: a list page would otherwise carry a fetcher per
    /// key for every object on screen, and for ConfigMaps — whose values are shown without asking —
    /// that meant the list itself pulling every value of every object it listed.
    /// </para>
    /// </summary>
    public IReadOnlyList<ConfigKeyRow> BuildKeyRows() =>
        [.. Keys.Select(k => new ConfigKeyRow(k, ResolveAsync, IsSecret))];

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
    public bool IsSecret { get; }

    /// <summary>
    /// The size of the value. Seeded from the listing, and corrected from the value itself once one
    /// has been fetched — a row built without a listing behind it (the pod page's environment
    /// section, KON-416) starts out not knowing it.
    /// </summary>
    [ObservableProperty] private string _size = string.Empty;

    partial void OnSizeChanged(string value) => OnPropertyChanged(nameof(BinaryNotice));

    /// <summary>The value, when it is on screen. Null is both "not asked for" and "hidden again".</summary>
    [ObservableProperty] private string? _value;

    [ObservableProperty] private bool _isRevealed;
    [ObservableProperty] private bool _isBusy;

    partial void OnIsRevealedChanged(bool value) => OnPropertyChanged(nameof(RevealTip));

    /// <summary>
    /// The tooltip of an icon-only reveal button, which is also its accessible name (KON-56) — so it
    /// has to say which of the two pressing it does, not what the row is showing (KON-390).
    /// </summary>
    public string RevealTip => IsRevealed ? "Hide the value" : "Show the value";

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
            Size = Format.Size(entry.SizeBytes);
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

/// <summary>
/// The confirm text for deleting a ConfigMap or a Secret (KON-253).
/// <para>
/// Written once because the two pages must say the same thing about the same act, and pulled out of
/// both view-models because the wording is the whole feature: the delete itself is one call.
/// </para>
/// </summary>
internal static class ConfigDelete
{
    /// <summary>
    /// The words alone, for the caller that raises the confirm itself — the detail page's delete goes
    /// through the shell so the drawer can close behind it (KON-334).
    /// </summary>
    public static (string Title, string Message) Words(ConfigObjectRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var kind = row.IsSecret ? "secret" : "config map";

        // The consequence is delayed, and that is the part worth saying. A running pod holds what it
        // mounted at start; it keeps running, and fails the next time it is recreated — which may be
        // days later and will not look connected to this by then.
        var mounted = row.IsSecret
            ? "Pods already running keep the values they started with, so nothing breaks now. The next"
              + " pod that tries to mount it will not start."
            : "Pods already running keep the values they started with, so nothing breaks now. The next"
              + " pod that tries to mount it, or read it as environment, will not start.";

        return ($"Delete {kind}",
            $"Delete {kind} \"{row.Name}\" in {row.Namespace}? This cannot be undone — Kontena does not"
            + $" keep a copy. {mounted}");
    }

    public static void Confirm(
        ViewModelBase page, IClusterEngine cluster, ConfigObjectRow row, Func<Task> reload)
    {
        ArgumentNullException.ThrowIfNull(row);

        var (title, message) = Words(row);

        page.ConfirmDelete(title, message, async () =>
        {
            await cluster.DeleteAsync(row.Reference);
            await reload();
        });
    }
}
