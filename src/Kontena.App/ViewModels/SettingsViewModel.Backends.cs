using System.Collections.ObjectModel;
using Kontena.Adapters.Docker;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.App.Services;
using Kontena.Sdk.Models;
using Kontena.Core.Models;

namespace Kontena.App.ViewModels;

/// <summary>
/// Settings › Engines: what each backend is called (KON-119) and the remote engines you added
/// yourself (KON-46).
/// </summary>
public partial class SettingsViewModel
{
    // ── Names in the switcher (KON-119) ─────────────────────────────────────

    private readonly Action? _onNamesChanged;

    /// <summary>Every backend, with the name the user chose for it.</summary>
    public ObservableCollection<BackendNameRow> BackendNames { get; } = [];

    public bool HasBackendNames => BackendNames.Count > 0;

    private void RefreshBackendNames()
    {
        BackendNames.Clear();
        foreach (var backend in _backends)
        {
            var source = backend.SourceName is { Length: > 0 } s ? s : backend.Name;
            _settings.BackendNames.TryGetValue(backend.Backend, out var chosen);
            BackendNames.Add(new BackendNameRow(backend.Backend, source, backend.Chip, chosen, Rename));
        }

        OnPropertyChanged(nameof(HasBackendNames));
    }

    /// <summary>
    /// Stores the name, or clears it when the field is emptied. Written straight through the store so a
    /// rename cannot be lost to another part of the app saving its own copy.
    /// </summary>
    private void Rename(string backend, string? name)
    {
        var source = BackendNames.FirstOrDefault(r => r.Backend == backend)?.SourceName ?? string.Empty;
        _settings = _store.Update(s => s.WithBackendName(backend, name, source));

        Relabel(backend, _settings.NameFor(backend, source));
        _onNamesChanged?.Invoke();
    }

    /// <summary>
    /// Carries a new name into the lists this page is already showing — the detected engines and the
    /// launch dropdown. Rebuilding the whole page instead would throw away the field being typed in.
    /// </summary>
    private void Relabel(string backend, string name)
    {
        for (var i = 0; i < _backends.Count; i++)
        {
            if (_backends[i].Backend == backend)
                _backends[i] = _backends[i] with { Name = name };
        }

        for (var i = 0; i < Engines.Count; i++)
        {
            if (Engines[i].Backend == backend)
                Engines[i] = Engines[i] with { Name = name };
        }

        _relabelling = true;
        try
        {
            var wasPinned = _pinnedBackend == backend;

            StartupOptions.Clear();
            StartupOptions.Add(LastUsedOption);
            StartupOptions.Add(FirstConnectedOption);
            foreach (var item in _backends)
                StartupOptions.Add(item.Name);

            // Clearing the list drops the selection, so put it back — by id, not by the old name.
            SelectedStartup = wasPinned
                ? name
                : _pinnedBackend is { Length: > 0 } id
                    ? _backends.FirstOrDefault(e => e.Backend == id)?.Name ?? SelectedStartup
                    : SelectedStartup;
        }
        finally
        {
            _relabelling = false;
        }

        // The hint repeats the chosen name in a sentence, and Save — which normally refreshes it — is
        // deliberately not called here: a rename is not a change of what Kontena opens on launch.
        OnPropertyChanged(nameof(StartupHint));
    }

    // ── Remote engines (KON-46) ─────────────────────────────────────────────

    private readonly Func<Task>? _onRemotesChanged;

    public ObservableCollection<RemoteEngineRow> RemoteEngines { get; } = [];

    [ObservableProperty] private string _remoteName = string.Empty;
    [ObservableProperty] private string _remoteHost = string.Empty;
    [ObservableProperty] private string _remoteUser = string.Empty;
    [ObservableProperty] private string _remotePort = string.Empty;
    [ObservableProperty] private string _remoteSocketPath = string.Empty;
    [ObservableProperty] private string _remoteCertificateDirectory = string.Empty;
    [ObservableProperty] private bool _remoteAllowInsecure;
    [ObservableProperty] private bool _remoteIsSsh = true;
    [ObservableProperty] private bool _isRemoteBusy;
    [ObservableProperty] private string? _remoteError;
    [ObservableProperty] private string? _remoteNotice;

    /// <summary>
    /// The remote being edited, or null while the form is adding a new one (KON-125).
    /// <para>
    /// Kept as an id rather than a copy of the row, because that id is the whole point of editing: the
    /// name the user gave it, its keychain entry, its remembered port forwards and a launch pin all
    /// hang off it. Remove-and-re-add loses every one of them silently.
    /// </para>
    /// </summary>
    [ObservableProperty] private string? _editingRemoteId;

    public bool IsEditingRemote => EditingRemoteId is not null;

    /// <summary>The form's primary action names what it will do, not what the section is called.</summary>
    public string RemoteSubmitLabel => IsEditingRemote ? "Save changes" : "Add engine";

    partial void OnEditingRemoteIdChanged(string? value)
    {
        OnPropertyChanged(nameof(IsEditingRemote));
        OnPropertyChanged(nameof(RemoteSubmitLabel));
    }

    public bool RemoteIsTcp => !RemoteIsSsh;

    /// <summary>Shown for TCP only, and only until certificates are given.</summary>
    public bool ShowInsecureWarning => RemoteIsTcp && string.IsNullOrWhiteSpace(RemoteCertificateDirectory);

    public bool CanAddRemote => !IsRemoteBusy && Draft().Problem is null;

    [RelayCommand]
    private void SetRemoteTransport(string transport) => RemoteIsSsh = transport != "tcp";

    partial void OnRemoteIsSshChanged(bool value)
    {
        OnPropertyChanged(nameof(RemoteIsTcp));
        OnPropertyChanged(nameof(ShowInsecureWarning));
        OnRemoteFieldChanged();
    }

    /// <summary>
    /// A value that <c>ssh</c> would read as one of its own options, or null (KON-181). Shown as it is
    /// typed rather than on submit, so the disabled Add button says why it is disabled instead of
    /// simply being grey (the KON-117 lesson).
    /// <para>
    /// Only this rule: TCP without certificates has its own warning above, with the acknowledgement
    /// checkbox that goes with it, and saying it twice in two tones is worse than saying it once.
    /// </para>
    /// </summary>
    public string? RemoteProblem =>
        RemoteEngine.ArgumentProblem(RemoteHost.Trim(), RemoteUser.Trim(), RemoteSocketPath.Trim());

    partial void OnRemoteNameChanged(string value) => OnRemoteFieldChanged();
    partial void OnRemoteHostChanged(string value) => OnRemoteFieldChanged();
    partial void OnRemotePortChanged(string value) => OnRemoteFieldChanged();
    partial void OnRemoteAllowInsecureChanged(bool value) => OnRemoteFieldChanged();
    partial void OnIsRemoteBusyChanged(bool value) => OnPropertyChanged(nameof(CanAddRemote));

    // These two had no handler at all, so the submit button did not re-evaluate while they were being
    // typed — invisible while the only rule was about certificates, and wrong the moment the user and
    // the socket path carry a rule of their own.
    partial void OnRemoteUserChanged(string value) => OnRemoteFieldChanged();
    partial void OnRemoteSocketPathChanged(string value) => OnRemoteFieldChanged();

    partial void OnRemoteCertificateDirectoryChanged(string value)
    {
        OnPropertyChanged(nameof(ShowInsecureWarning));
        OnRemoteFieldChanged();
    }

    private void OnRemoteFieldChanged()
    {
        OnPropertyChanged(nameof(CanAddRemote));
        OnPropertyChanged(nameof(RemoteProblem));
        RemoteError = null;
        RemoteNotice = null;
    }

    /// <summary>
    /// The connection the form currently describes. Built rather than validated field by field, so the one
    /// rule that matters — TCP without certificates is refused — lives in the model and not in the view.
    /// </summary>
    /// <summary>
    /// The connection the form describes. While editing, the existing id is reused unless one is given
    /// — an edit must not mint a new backend (KON-125).
    /// </summary>
    private RemoteEngine Draft(string? id = null) => new RemoteEngineDraft
    {
        Name = RemoteName,
        Host = RemoteHost,
        User = RemoteUser,
        Port = RemotePort,
        SocketPath = RemoteSocketPath,
        CertificateDirectory = RemoteCertificateDirectory,
        AllowInsecure = RemoteAllowInsecure,
        IsSsh = RemoteIsSsh,
    }.Build(id ?? EditingRemoteId);

    private void RefreshRemotes()
    {
        RemoteEngines.Clear();
        foreach (var remote in _settings.RemoteEngines)
        {
            var connected = _backends.Any(b => b.Backend == remote.Backend && b.Connected);
            RemoteEngines.Add(new RemoteEngineRow(remote, connected));
        }
    }

    /// <summary>
    /// Actually connects, before anything is saved. For SSH that means opening the tunnel and asking the
    /// daemon through it — the only way to tell "the host is reachable" from "the engine answers", which are
    /// different problems with different fixes.
    /// </summary>
    [RelayCommand]
    private async Task TestRemoteAsync()
    {
        var draft = Draft();
        if (draft.Problem is { } problem)
        {
            RemoteError = problem;
            return;
        }

        RemoteError = null;
        RemoteNotice = null;
        IsRemoteBusy = true;
        try
        {
            var info = await Task.Run(async () =>
            {
                var backend = new RemoteDockerEngineProvider(draft).CreateBackend();
                try
                {
                    await backend.PingAsync();
                    return await backend.GetInfoAsync();
                }
                finally
                {
                    // Disposing takes the tunnel with it: a test must not leave a connection behind.
                    (backend as IDisposable)?.Dispose();
                }
            });

            RemoteNotice = $"Connected — {info.DisplayName} {info.Version}.".Replace("  ", " ", StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            // ssh's and the daemon's own words. "Permission denied (publickey)" and "Host key verification
            // failed" say exactly what to fix, and nothing written here would say it better.
            RemoteError = ex.Message;
        }
        finally
        {
            IsRemoteBusy = false;
        }
    }

    /// <summary>Loads a stored remote into the form so it can be changed in place (KON-125).</summary>
    [RelayCommand]
    private void EditRemote(RemoteEngineRow? row)
    {
        if (row is null)
            return;

        var remote = row.Remote;

        EditingRemoteId = remote.Id;
        RemoteIsSsh = remote.Transport == RemoteEngineTransport.Ssh;
        RemoteName = remote.Name;
        RemoteHost = remote.Host;
        RemoteUser = remote.User ?? string.Empty;
        RemotePort = remote.Port?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        RemoteSocketPath = remote.SocketPath ?? string.Empty;
        RemoteCertificateDirectory = remote.CertificateDirectory ?? string.Empty;
        RemoteAllowInsecure = remote.AllowInsecureTcp;

        RemoteError = null;
        RemoteNotice = null;
    }

    /// <summary>Leaves edit mode without writing anything, and empties the form.</summary>
    [RelayCommand]
    private void CancelEditRemote() => ClearRemoteForm();

    private void ClearRemoteForm()
    {
        EditingRemoteId = null;
        RemoteIsSsh = true;
        RemoteName = string.Empty;
        RemoteHost = string.Empty;
        RemoteUser = string.Empty;
        RemotePort = string.Empty;
        RemoteSocketPath = string.Empty;
        RemoteCertificateDirectory = string.Empty;
        RemoteAllowInsecure = false;
        RemoteError = null;
    }

    /// <summary>
    /// Stores the form: a new remote, or the one being edited under its existing id. Same command for
    /// both, so there is one path that validates and one that writes.
    /// </summary>
    [RelayCommand]
    private async Task AddRemoteAsync()
    {
        var draft = Draft();
        if (draft.Problem is { } problem)
        {
            RemoteError = problem;
            return;
        }

        var editing = EditingRemoteId;

        _settings = _store.Update(s => editing is null
            ? s with { RemoteEngines = [.. s.RemoteEngines, draft] }

            // Replaced in place, keeping its position in the list: the switcher reads this order, and a
            // saved edit that jumped an entry to the bottom would read as something else having changed.
            : s with
            {
                RemoteEngines = [.. s.RemoteEngines.Select(r => r.Id == editing ? draft : r)],
            });

        RemoteNotice = editing is null ? $"Added {draft.Name}." : $"Saved {draft.Name}.";
        ClearRemoteForm();
        RefreshRemotes();

        // The switcher is built from the provider list, so it has to be rebuilt for the change to show.
        if (_onRemotesChanged is not null)
            await _onRemotesChanged();
    }

    [RelayCommand]
    private void RemoveRemote(RemoteEngineRow? row)
    {
        if (row is null)
            return;

        // A removed remote cannot be undone from inside Kontena — the connection details and its stored
        // secret are both gone, and re-adding means typing them again (KON-126, and KON-116 is exactly
        // what losing these silently looks like).
        Confirm(
            "Remove remote engine",
            $"Remove \"{row.Name}\"? Kontena forgets how to reach it, along with the password or key it" +
            " kept for it — you would have to enter those again. The host itself is untouched.",
            "Remove",
            () => RemoveRemoteAsync(row));
    }

    private async Task RemoveRemoteAsync(RemoteEngineRow row)
    {
        _settings = _store.Update(s => s with
        {
            RemoteEngines = [.. s.RemoteEngines.Where(r => r.Id != row.Remote.Id)],
        });

        // Anything kept in the keychain for this remote goes with it, so a re-add cannot inherit an old
        // secret belonging to a host that is no longer configured.
        await _secrets.DeleteAsync(SecretKeys.Engine(row.Remote.Id));

        // Editing something that no longer exists would save it back on the next click.
        if (EditingRemoteId == row.Remote.Id)
            ClearRemoteForm();

        RemoteNotice = $"Removed {row.Name}.";
        RefreshRemotes();

        if (_onRemotesChanged is not null)
            await _onRemotesChanged();
    }
}
