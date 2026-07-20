using System.Text.Json;
using System.Text.Json.Serialization;
using Kontena.Core.Models;
using Xunit;

namespace Kontena.Core.Tests;

public class KontenaSettingsTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void Defaults_are_dark_and_auto_detecting()
    {
        var settings = new KontenaSettings();
        Assert.Equal(ThemePreference.Dark, settings.Theme);
        Assert.True(settings.AutoDetectEngines);
        Assert.Null(settings.DefaultEngine);
    }

    [Fact]
    public void Round_trips_through_json()
    {
        var original = new KontenaSettings
        {
            Theme = ThemePreference.System,
            AutoDetectEngines = false,
            DefaultEngine = "podman",
            LaunchAtLogin = true,
            SendUsageStats = true,
        };

        var json = JsonSerializer.Serialize(original, Options);
        var restored = JsonSerializer.Deserialize<KontenaSettings>(json, Options);

        Assert.Equal(original, restored);
    }

    [Fact]
    public void Theme_serializes_as_a_name_not_a_number()
    {
        var json = JsonSerializer.Serialize(new KontenaSettings { Theme = ThemePreference.Light }, Options);
        Assert.Contains("\"Light\"", json);
    }
}
