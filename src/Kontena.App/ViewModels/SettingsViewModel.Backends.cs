using System.Collections.ObjectModel;
using Kontena.Adapters.Docker;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.App.Services;
using Kontena.Sdk.Errors;
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

    /// <summary>A private key to use instead of whatever the agent offers (KON-261).</summary>
    [ObservableProperty] private string _remoteKeyFile = string.Empty;

    /// <summary>Authenticate with a stored password rather than a key (KON-259).</summary>
    [ObservableProperty] private bool _remoteUsePassword;

    /// <summary>
    /// The password as it is being typed. Never persisted with the engine — it goes to the keychain on
    /// save and this field is emptied, so a settings file cannot end up holding it.
    /// </summary>
    [ObservableProperty] private string _remotePassword = string.Empty;

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

    /// <summary>
    /// Whether to offer the password option at all (KON-259). Without a keychain there is nowhere to
    /// put it, and Kontena has no fallback to a file — so the option is absent rather than present and
    /// broken, the same rule the registry logins follow.
    /// </summary>
    public bool ShowPasswordOption => RemoteIsSsh && HasKeychain;

    /// <summary>The key file box, hidden once a password is what will be used.</summary>
    public bool ShowKeyFile => RemoteIsSsh && !RemoteUsePassword;

    public bool ShowPasswordBox => ShowPasswordOption && RemoteUsePassword;

    /// <summary>Shown for TCP only, and only until certificates are given.</summary>
    public bool ShowInsecureWarning => RemoteIsTcp && string.IsNullOrWhiteSpace(RemoteCertificateDirectory);

    public bool CanAddRemote => !IsRemoteBusy && Draft().Problem is null;

    /// <summary>
    /// The connection whose host key is not trusted yet, or null (KON-260). Set only by a test that
    /// failed for that one reason, so the button below appears exactly when it has something to offer
    /// — and never after a key that <i>changed</i>, which is not a question the user should be
    /// invited to answer with a click.
    /// </summary>
    [ObservableProperty] private RemoteEngine? _untrustedHost;

    public bool CanReviewHostKey => UntrustedHost is not null && !IsRemoteBusy;

    partial void OnUntrustedHostChanged(RemoteEngine? value) => OnPropertyChanged(nameof(CanReviewHostKey));

    /// <summary>
    /// Fetches the host's fingerprint and asks about it. Confirming writes it to <c>known_hosts</c> and
    /// tests again, so the user ends where they were going rather than back at a form.
    /// </summary>
    [RelayCommand]
    private async Task ReviewHostKeyAsync()
    {
        if (UntrustedHost is not { } remote)
            return;

        IsRemoteBusy = true;
        try
        {
            var request = await SshHostKeyTrust.AskAsync(remote, async () =>
            {
                UntrustedHost = null;
                await TestRemoteAsync();
            });

            RequestConfirm?.Invoke(request);
        }
        catch (Exception ex)
        {
            // The host never got as far as offering a key. ssh's own message says why; there is
            // nothing to confirm, so show that rather than an empty dialog.
            RemoteError = ex.Message;
        }
        finally
        {
            IsRemoteBusy = false;
        }
    }

    [RelayCommand]
    private void SetRemoteTransport(string transport) => RemoteIsSsh = transport != "tcp";

    partial void OnRemoteIsSshChanged(bool value)
    {
        OnPropertyChanged(nameof(RemoteIsTcp));
        OnPropertyChanged(nameof(ShowInsecureWarning));
        NotifyAuthVisibility();
        OnRemoteFieldChanged();
    }

    private void NotifyAuthVisibility()
    {
        OnPropertyChanged(nameof(ShowPasswordOption));
        OnPropertyChanged(nameof(ShowKeyFile));
        OnPropertyChanged(nameof(ShowPasswordBox));
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
    partial void OnIsRemoteBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanAddRemote));
        OnPropertyChanged(nameof(CanReviewHostKey));
    }

    // These two had no handler at all, so the submit button did not re-evaluate while they were being
    // typed — invisible while the only rule was about certificates, and wrong the moment the user and
    // the socket path carry a rule of their own.
    partial void OnRemoteUserChanged(string value) => OnRemoteFieldChanged();
    partial void OnRemoteSocketPathChanged(string value) => OnRemoteFieldChanged();
    partial void OnRemoteKeyFileChanged(string value) => OnRemoteFieldChanged();

    partial void OnRemoteUsePasswordChanged(bool value)
    {
        // A password and a key file are alternatives, not a pair (the model refuses both). Clearing the
        // other one is what makes the choice visible rather than leaving a field that silently no
        // longer applies.
        if (value)
            RemoteKeyFile = string.Empty;
        else
            RemotePassword = string.Empty;

        NotifyAuthVisibility();
        OnRemoteFieldChanged();
    }

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

        // The offer belongs to the host that was tested. Once the form describes a different one it is
        // an offer to trust a machine nobody asked about.
        UntrustedHost = null;
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
        KeyFile = RemoteKeyFile,
        UsePassword = RemoteUsePassword && HasKeychain,
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

    // ── Retrying a backend that did not answer (KON-328) ────────────────────

    private readonly Func<string, Task>? _retryBackend;

    /// <summary>
    /// Ask one backend again from the row that shows it as unreachable.
    /// <para>
    /// The reason this exists is the failure only a person can clear: an engine that has to be started,
    /// a VPN that has to come up, an SSH agent waiting on a fingerprint in 1Password. Those attempts
    /// succeed on the second try by definition — and until now the second try meant restarting Kontena.
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task RetryBackendAsync(string? backend)
    {
        if (backend is null || _retryBackend is null || _retrying is not null)
            return;

        _retrying = backend;
        MarkRetrying(backend, true);
        try
        {
            // The shell re-probes and calls SetBackendConnected back with the answer — one probe cache,
            // written in one place, whichever row asked.
            await _retryBackend(backend);
        }
        finally
        {
            _retrying = null;
            MarkRetrying(backend, false);
        }
    }

    /// <summary>The backend being retried, so a click on another row cannot start a second probe.</summary>
    private string? _retrying;

    /// <summary>
    /// Fold a fresh probe result into the rows already on screen (KON-328). In place rather than by
    /// rebuilding the page, following <see cref="Relabel"/>: a rebuild would empty a remote form that is
    /// halfway typed, and the user retrying a connection is often exactly the user filling one in.
    /// </summary>
    internal void SetBackendConnected(string backend, bool connected, string detail)
    {
        for (var i = 0; i < _backends.Count; i++)
        {
            if (_backends[i].Backend == backend)
                _backends[i] = _backends[i] with { Connected = connected, Detail = detail };
        }

        for (var i = 0; i < Engines.Count; i++)
        {
            if (Engines[i].Backend == backend)
                Engines[i] = Engines[i] with { Connected = connected, Detail = detail };
        }

        for (var i = 0; i < RemoteEngines.Count; i++)
        {
            if (RemoteEngines[i].Remote.Backend == backend)
                RemoteEngines[i] = RemoteEngines[i] with { Connected = connected };
        }
    }

    private void MarkRetrying(string backend, bool retrying)
    {
        for (var i = 0; i < Engines.Count; i++)
        {
            if (Engines[i].Backend == backend)
                Engines[i] = Engines[i] with { Retrying = retrying };
        }

        for (var i = 0; i < RemoteEngines.Count; i++)
        {
            if (RemoteEngines[i].Remote.Backend == backend)
                RemoteEngines[i] = RemoteEngines[i] with { Retrying = retrying };
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
                var backend = new RemoteDockerEngineProvider(draft, SshPasswordPrompt.For(draft)).CreateBackend();
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
            UntrustedHost = null;
        }
        catch (SshHostKeyException ex) when (ex.Problem == SshHostKeyProblem.Unknown)
        {
            // The one failure that is a question rather than a fault (KON-260). Offering the fingerprint
            // here is what stops this being "go and use a terminal, then come back".
            RemoteError = ex.Message;
            UntrustedHost = draft;
        }
        catch (Exception ex)
        {
            // ssh's and the daemon's own words. "Permission denied (publickey)" says exactly what to fix,
            // and nothing written here would say it better. A changed host key lands here too, on
            // purpose: it names the file and line to undo, and Kontena will not offer to undo it.
            RemoteError = ex.Message;
            UntrustedHost = null;
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
        RemoteKeyFile = remote.KeyFile ?? string.Empty;
        RemoteUsePassword = remote.UsePassword;

        // Left empty on purpose: the stored password is not read back into a box. Typing a new one
        // replaces it, and leaving it alone keeps what the keychain already has.
        RemotePassword = string.Empty;

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
        RemoteKeyFile = string.Empty;
        RemoteUsePassword = false;
        RemotePassword = string.Empty;
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

        // Before the engine is written, not after: a stored engine whose password never made it to the
        // keychain looks configured and fails at connect time with ssh blamed for it.
        if (draft.UsePassword && RemotePassword.Length > 0
            && !await _secrets.SetAsync(SecretKeys.Engine(draft.Id), RemotePassword))
        {
            RemoteError = "The keychain refused to store the password, so nothing was saved.";
            return;
        }

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
