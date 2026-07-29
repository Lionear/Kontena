using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kontena.Core.Models;

namespace Kontena.App.Services;

/// <summary>Loads and saves <see cref="KontenaSettings"/> as JSON under the user's config dir.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public SettingsStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Lionear", "Kontena");
        _path = Path.Combine(dir, "settings.json");
    }

    /// <summary>A store over a specific file. For tests, which must not touch the real settings.</summary>
    internal SettingsStore(string path) => _path = path;

    public KontenaSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<KontenaSettings>(json, Options) ?? new KontenaSettings();
            }
        }
        catch
        {
            // Corrupt or unreadable file — fall back to defaults rather than crash.
        }

        return new KontenaSettings();
    }

    /// <summary>
    /// Changes settings on top of what is on disk right now, and returns the result.
    /// <para>
    /// The only supported way to write. Settings are one file but several owners — the shell remembers the
    /// open backend and window geometry, the Settings page owns preferences, registries and remotes — and
    /// each holds its own copy. Saving such a copy writes every field, so it silently reverts whatever
    /// another owner changed after that copy was taken. That is how a list of configured remote engines
    /// disappears on the next backend switch. Re-reading first means a writer can only affect the fields it
    /// actually touches.
    /// </para>
    /// </summary>
    public KontenaSettings Update(Func<KontenaSettings, KontenaSettings> change)
    {
        var updated = change(Load());
        Save(updated);
        return updated;
    }

    /// <summary>
    /// Writes settings as given, replacing the file. Prefer <see cref="Update"/>: this overwrites fields
    /// the caller may not know about. Public for the first write of a fresh object and for tools.
    /// </summary>
    public void Save(KontenaSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path)!;

            // Only a directory this write created gets its mode set: the path can be pointed elsewhere
            // (tests, tools), and narrowing a directory somebody else owns is not this method's call.
            var created = !Directory.Exists(directory);
            Directory.CreateDirectory(directory);
            if (created)
                RestrictToOwner(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            // The mode is set before the content exists, so the fields below are never briefly
            // world-readable — and a file written by an older version is narrowed on its next save.
            if (!File.Exists(_path))
                File.Create(_path).Dispose();

            RestrictToOwner(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            File.WriteAllText(_path, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
            // Best-effort persistence; a failed write must not take the app down.
        }
    }

    /// <summary>
    /// Keeps <paramref name="path"/> to its owner on Unix (KON-187). No secret is in here — those live
    /// in the keychain (KON-52) — but remote engine hosts and users, registry usernames and the
    /// kubeconfig paths Kontena reads are reconnaissance for anyone else with an account on the machine.
    /// Windows inherits the user profile's ACL and needs nothing.
    /// </summary>
    private static void RestrictToOwner(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception)
        {
            // A filesystem that cannot express this (a mounted share, a container volume) is not a
            // reason to lose the settings.
        }
    }
}
