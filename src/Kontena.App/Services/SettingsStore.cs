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
