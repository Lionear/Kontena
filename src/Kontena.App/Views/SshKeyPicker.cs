using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Kontena.App.Views;

/// <summary>
/// The file picker for an SSH private key (KON-261). Shared, because both places that configure a
/// remote engine need the same one and a second copy would drift.
/// </summary>
internal static class SshKeyPicker
{
    /// <summary>The chosen path, or null when the picker was dismissed or is unavailable.</summary>
    public static async Task<string?> PickAsync(TopLevel? topLevel)
    {
        if (topLevel?.StorageProvider is not { } storage)
            return null;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "SSH private key",
            AllowMultiple = false,

            // Where keys live, so the picker opens on them rather than on Documents. ~/.ssh is hidden
            // on Unix, and a picker that starts elsewhere makes the user go looking for a folder they
            // cannot see.
            SuggestedStartLocation = await SshFolderAsync(storage),

            // An aid, not a gate. A private key usually has no extension at all — id_ed25519, id_rsa —
            // so filtering to a pattern set would hide the common case rather than help find it.
            FileTypeFilter =
            [
                new FilePickerFileType("SSH keys") { Patterns = ["id_*", "*.pem", "*.key"] },
                FilePickerFileTypes.All,
            ],
        });

        return files.Count > 0 && files[0].TryGetLocalPath() is { Length: > 0 } path ? path : null;
    }

    private static async Task<IStorageFolder?> SshFolderAsync(IStorageProvider storage)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

            return Directory.Exists(path) ? await storage.TryGetFolderFromPathAsync(path) : null;
        }
        catch (Exception)
        {
            // No ~/.ssh, or a provider that will not resolve it. The picker opens wherever it likes,
            // which is the behaviour without this at all.
            return null;
        }
    }
}
