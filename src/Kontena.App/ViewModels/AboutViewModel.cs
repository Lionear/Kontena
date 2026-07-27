using System.Reflection;
using CommunityToolkit.Mvvm.Input;
using Kontena.App.Services;

namespace Kontena.App.ViewModels;

/// <summary>
/// The About page (KON-135). Its own screen rather than a category inside Settings: this is the one
/// place the brand gets room, and a page reached through a settings sub-nav is a page nobody opens.
/// </summary>
public sealed partial class AboutViewModel : ViewModelBase
{
    private const string RepositoryUrl = "https://github.com/Lionear/Kontena";
    private const string WebsiteUrl = "https://kontena.app";

    private readonly Action? _showActivity;

    /// <param name="secrets">Keychain probe for the credentials note; defaults to this platform's.</param>
    /// <param name="showActivity">How the page asks the shell to open Activity. Null in design-time
    /// and tests, and then the quick action is not offered at all.</param>
    public AboutViewModel(ISecretStore? secrets = null, Action? showActivity = null)
    {
        _showActivity = showActivity;

        // Says whether the guarantee in CONTRIBUTING actually holds on this session (KON-52).
        KeychainStatus = (secrets ?? SecretStore.Create()).IsAvailable
            ? "Credentials are stored in your system keychain, never in Kontena's own files. You can inspect and revoke them there."
            : "No system keychain is reachable on this session, so Kontena cannot store credentials. It will not write them anywhere else instead.";
    }

    public string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>The repository, without the scheme — it is a label here, not something to type.</summary>
    public string RepositoryLabel { get; } = "github.com/Lionear/Kontena";

    public string KeychainStatus { get; }

    /// <summary>
    /// Whether the Activity quick action is shown. Only the shell can navigate, so without it the
    /// row would do nothing — and a dead button is worse than a missing one (KON-117).
    /// </summary>
    public bool HasActivity => _showActivity is not null;

    [RelayCommand]
    private static void OpenRepository() => Browser.OpenUrl(RepositoryUrl);

    [RelayCommand]
    private static void OpenReleases() => Browser.OpenUrl($"{RepositoryUrl}/releases");

    [RelayCommand]
    private static void OpenIssues() => Browser.OpenUrl($"{RepositoryUrl}/issues");

    [RelayCommand]
    private static void OpenWebsite() => Browser.OpenUrl(WebsiteUrl);

    [RelayCommand]
    private void ShowActivity() => _showActivity?.Invoke();
}
