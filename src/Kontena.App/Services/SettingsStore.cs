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
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
            // Best-effort persistence; a failed write must not take the app down.
        }
    }
}
