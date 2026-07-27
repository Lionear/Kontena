using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.App.Services;
using Kontena.Core.Models;
using Kontena.Engines;

namespace Kontena.App.ViewModels;

/// <summary>
/// Settings › Registries (KON-114) and the keychain probe behind it (KON-52).
/// </summary>
public partial class SettingsViewModel
{
    // ── Credentials (KON-52) ────────────────────────────────────────────────

    private readonly ISecretStore _secrets;

    /// <summary>
    /// Whether the OS keychain can be reached. Worth stating before anyone types a password: the answer
    /// decides whether Kontena is able to keep one at all, and it is not something a user can otherwise
    /// find out except by trying.
    /// </summary>
    public bool HasKeychain => _secrets.IsAvailable;

    // ── Registries (KON-114) ────────────────────────────────────────────────

    private readonly RegistryCredentials? _registries;
    private readonly Func<IContainerEngine?>? _engineForVerify;

    /// <summary>Whether the category is offered: it needs a keychain to put a new login in.</summary>
    public bool HasRegistries => _registries is not null && HasKeychain;

    public ObservableCollection<RegistryRow> Registries { get; } = [];

    [ObservableProperty] private string _registryHostInput = string.Empty;
    [ObservableProperty] private string _registryUsername = string.Empty;
    [ObservableProperty] private string _registrySecret = string.Empty;
    [ObservableProperty] private bool _isRegistryBusy;
    [ObservableProperty] private string? _registryError;
    [ObservableProperty] private string? _registryNotice;

    public bool CanLogIn =>
        !IsRegistryBusy
        && !string.IsNullOrWhiteSpace(RegistryHostInput)
        && !string.IsNullOrWhiteSpace(RegistryUsername)
        && !string.IsNullOrWhiteSpace(RegistrySecret);

    partial void OnRegistryHostInputChanged(string value) => OnLoginFieldChanged();
    partial void OnRegistryUsernameChanged(string value) => OnLoginFieldChanged();
    partial void OnRegistrySecretChanged(string value) => OnLoginFieldChanged();
    partial void OnIsRegistryBusyChanged(bool value) => OnPropertyChanged(nameof(CanLogIn));

    private void OnLoginFieldChanged()
    {
        OnPropertyChanged(nameof(CanLogIn));
        RegistryError = null;
        RegistryNotice = null;
    }

    private void RefreshRegistries()
    {
        if (_registries is null)
            return;

        Registries.Clear();
        foreach (var login in _registries.List())
        {
            Registries.Add(new RegistryRow(
                login.Host, login.Username, login.Source == RegistryCredentialSource.EngineConfig));
        }
    }

    /// <summary>
    /// Verifies the login against the registry, and only then stores it. Storing an unverified credential
    /// would look configured and fail later at a pull, with an error naming the image rather than the
    /// account.
    /// </summary>
    [RelayCommand]
    private async Task LogInAsync()
    {
        if (_registries is null || !CanLogIn)
            return;

        var host = RegistryHost.Canonical(RegistryHostInput);
        var credential = new RegistryCredential(host, RegistryUsername.Trim(), RegistrySecret);

        RegistryError = null;
        RegistryNotice = null;
        IsRegistryBusy = true;
        try
        {
            if (_engineForVerify?.Invoke() is { } engine)
                await engine.VerifyRegistryLoginAsync(credential);

            if (!await _secrets.SetAsync(SecretKeys.Registry(host), credential.Secret))
            {
                RegistryError = "The login was accepted but could not be saved to your keychain, so it has not been kept.";
                return;
            }

            _settings = _store.Update(s => s with
            {
                Registries =
                [
                    .. s.Registries.Where(r => !RegistryHost.SameHost(r.Host, host)),
                    new RegistryLogin(host, credential.Username, RegistryCredentialSource.Kontena),
                ],
            });

            // Cleared immediately: there is no reason for the secret to stay in a text box, and the row
            // below is the confirmation that it landed.
            RegistrySecret = string.Empty;
            RegistryUsername = string.Empty;
            RegistryHostInput = string.Empty;
            RegistryNotice = $"Signed in to {host}.";
            RefreshRegistries();
        }
        catch (Exception ex)
        {
            RegistryError = ex.Message;
        }
        finally
        {
            IsRegistryBusy = false;
        }
    }

    /// <summary>Removes a login Kontena stored, secret included.</summary>
    [RelayCommand]
    private void SignOut(RegistryRow? row)
    {
        if (row is null || row.IsInherited || _registries is null)
            return;

        // Worth confirming — a sign-out you did not mean breaks the next pull — but no image or data is
        // touched, so the wording says that rather than warning about loss (KON-126).
        Confirm(
            "Sign out of registry",
            $"Sign out of {row.Host}? The stored login is removed from your keychain. Images you already" +
            " pulled stay; pulling or pushing private ones needs a new sign-in.",
            "Sign out",
            () => SignOutAsync(row),
            destructive: false);
    }

    private async Task SignOutAsync(RegistryRow row)
    {
        // The secret goes first: an entry left in the keychain after the row is gone is a credential
        // nobody can see and nobody will remove.
        await _secrets.DeleteAsync(SecretKeys.Registry(row.Host));

        _settings = _store.Update(s => s with
        {
            Registries = [.. s.Registries.Where(r => !RegistryHost.SameHost(r.Host, row.Host))],
        });

        RegistryNotice = $"Signed out of {row.Host}.";
        RefreshRegistries();
    }
}
