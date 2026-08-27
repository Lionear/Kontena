using System.Text.Json;
using Kontena.Engines.Plugins;

namespace Kontena.Engines.Tests;

/// <summary>
/// The loader is the one place that turns files on disk into running code, so its failure modes are
/// the tests: nothing loads without consent, nothing loads that claims a newer SDK, and nothing that
/// goes wrong in one directory reaches the caller.
/// </summary>
public sealed class PluginLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kontena-plugin-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// The fixture assembly, built by <c>tests/Kontena.TestPlugin</c> into this project's output. It
    /// carries its own copy of Kontena.Sdk.dll, which is the whole point: the loader must ignore it.
    /// </summary>
    private static string FixtureDirectory =>
        Path.Combine(AppContext.BaseDirectory, "plugin-fixture");

    /// <summary>
    /// The hostile fixture, built by <c>tests/Kontena.HostilePlugin</c> into its own subfolder of this
    /// project's output — a separate assembly from <see cref="FixtureDirectory"/>'s, so which
    /// <c>IEnginePlugin</c> the loader finds never depends on type ordering within one assembly.
    /// </summary>
    private static string HostileFixtureDirectory =>
        Path.Combine(AppContext.BaseDirectory, "hostile-plugin-fixture");

    /// <summary>
    /// The UI-only fixture, built by <c>tests/Kontena.UiTestPlugin</c>: an assembly with an
    /// <c>IUiPlugin</c> and no <c>IEnginePlugin</c> at all (KON-331).
    /// </summary>
    private static string UiFixtureDirectory =>
        Path.Combine(AppContext.BaseDirectory, "ui-plugin-fixture");

    /// <summary>
    /// A directory outside <see cref="_root"/> entirely — sibling, not nested — so an escape test that
    /// points a manifest at it is not also scanned by <c>Discover(_root, …)</c> as a plugin directory of
    /// its own.
    /// </summary>
    private readonly string _outside = Path.Combine(
        Path.GetTempPath(), "kontena-plugin-tests-outside-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_outside))
            Directory.Delete(_outside, recursive: true);
    }

    /// <summary>
    /// Copy the built fixture into the plugins root and give it a manifest.
    /// <para>
    /// The contribution declaration defaults to what <c>TestPlugin</c> genuinely is — one engine
    /// backend and a page — because the loader holds a manifest to its assembly (KON-280), so a
    /// fixture that under-declares is rejected before it reaches whatever a test is actually about.
    /// </para>
    /// </summary>
    private string InstallFixture(
        string version = "1.0.0",
        string minSdk = "0.1.0",
        string? id = null,
        object[]? platforms = null,
        string[]? backends = null,
        bool contributesUi = true)
    {
        id ??= "com.kontena.test";
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);

        foreach (var file in Directory.GetFiles(FixtureDirectory))
            File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);

        File.WriteAllText(Path.Combine(dir, "plugin.json"), JsonSerializer.Serialize(new
        {
            id,
            name = "Test Plugin",
            version,
            author = "Kontena",
            description = "A plugin.",
            minSdkVersion = minSdk,
            assembly = "Kontena.TestPlugin.dll",
            platforms = platforms ?? [],
            backends = backends ?? EngineOnly,
            contributesUi,
        }));

        return dir;
    }

    /// <summary>The one backend kind the engine fixtures contribute, as plugin.json spells it.</summary>
    private static readonly string[] EngineOnly = ["engine"];

    /// <summary>The operating system this test run is on, named the way a manifest names it.</summary>
    private static string ThisOs =>
        OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsMacOS() ? "macos"
        : "linux";

    /// <summary>Copy the built hostile fixture into the plugins root and give it a manifest.</summary>
    private string InstallHostileFixture(string id = "com.kontena.hostile")
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);

        foreach (var file in Directory.GetFiles(HostileFixtureDirectory))
            File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);

        File.WriteAllText(Path.Combine(dir, "plugin.json"), JsonSerializer.Serialize(new
        {
            id,
            name = "Hostile Plugin",
            version = "1.0.0",
            author = "Kontena",
            description = "A plugin whose provider throws.",
            minSdkVersion = "0.1.0",
            assembly = "Kontena.HostilePlugin.dll",
            backends = EngineOnly,
        }));

        return dir;
    }

    /// <summary>Copy the built UI-only fixture into the plugins root and give it a manifest.</summary>
    private string InstallUiFixture(string id = "com.kontena.uitest")
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);

        foreach (var file in Directory.GetFiles(UiFixtureDirectory))
            File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);

        File.WriteAllText(Path.Combine(dir, "plugin.json"), JsonSerializer.Serialize(new
        {
            id,
            name = "UI Test Plugin",
            version = "1.0.0",
            author = "Kontena",
            description = "A plugin that contributes pages.",
            minSdkVersion = "0.1.0",
            assembly = "Kontena.UiTestPlugin.dll",
            contributesUi = true,
        }));

        return dir;
    }

    /// <summary>
    /// Write a plugin directory: a manifest, and a stand-in file where it says the assembly is.
    /// <para>
    /// The stand-in is not a loadable assembly and is not meant to be — these tests are about the
    /// decisions taken before anything loads. It has to exist, though: since KON-362 a directory whose
    /// assembly is absent is rejected outright rather than left waiting for consent, because there is
    /// nothing to give consent about. Pass <paramref name="writeAssembly"/> false for the tests that
    /// want that rejection.
    /// </para>
    /// </summary>
    private string WriteManifest(
        string id,
        string version = "1.0.0",
        string minSdk = "0.1.0",
        string assembly = "Nothing.dll",
        bool writeAssembly = true)
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), JsonSerializer.Serialize(new
        {
            id,
            name = "Test",
            version,
            author = "Kontena",
            description = "A plugin.",
            minSdkVersion = minSdk,
            assembly,
        }));

        if (writeAssembly && !Path.IsPathRooted(assembly))
            File.WriteAllText(Path.Combine(dir, assembly), "not a real assembly");

        return dir;
    }

    [Fact]
    public void A_missing_root_yields_nothing()
    {
        Assert.Empty(PluginLoader.Discover(Path.Combine(_root, "does-not-exist"), _ => true));
    }

    [Fact]
    public void An_empty_directory_is_rejected_with_a_reason()
    {
        Directory.CreateDirectory(Path.Combine(_root, "empty"));

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));

        Assert.Equal(PluginStatus.Rejected, found.Status);
        Assert.NotNull(found.Reason);
    }

    [Fact]
    public void An_unreadable_manifest_is_rejected_with_a_reason()
    {
        var dir = Path.Combine(_root, "broken");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), "{ this is not json");

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));

        Assert.Equal(PluginStatus.Rejected, found.Status);
        Assert.NotNull(found.Reason);
    }

    [Fact]
    public void A_manifest_missing_a_required_field_is_rejected_with_a_reason()
    {
        var dir = Path.Combine(_root, "incomplete");
        Directory.CreateDirectory(dir);
        // Valid JSON, but no id and no assembly: System.Text.Json throws on the required members, and
        // that has to land as a rejection like any other unreadable manifest.
        File.WriteAllText(Path.Combine(dir, "plugin.json"), """{ "name": "Half a plugin" }""");

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));

        Assert.Equal(PluginStatus.Rejected, found.Status);
        Assert.NotNull(found.Reason);
    }

    [Fact]
    public void A_plugin_without_consent_awaits_it_and_is_not_loaded()
    {
        WriteManifest("com.kontena.test");

        var found = Assert.Single(PluginLoader.Discover(_root, _ => false));

        Assert.Equal(PluginStatus.AwaitingConsent, found.Status);
        Assert.Equal("com.kontena.test", found.Manifest!.Id);
        Assert.Empty(found.Providers);
    }

    [Fact]
    public void Consent_is_asked_per_id_and_version()
    {
        WriteManifest("com.kontena.test", version: "2.0.0");

        var found = Assert.Single(PluginLoader.Discover(
            _root, c => c.Manifest.Id == "com.kontena.test" && c.Manifest.Version == "1.0.0"));

        Assert.Equal(PluginStatus.AwaitingConsent, found.Status);
    }

    [Fact]
    public void An_assembly_that_changed_under_an_answer_is_asked_about_again()
    {
        // The hole KON-362 closes: an answer recorded for one dll used to cover any dll that kept the
        // same id and version, because plugin.json is a text file beside the code it describes.
        //
        // Deliberately not the loadable fixture: nothing here needs the file to be real, because the
        // digest is taken before anything decides whether to load it.
        var directory = WriteManifest("com.kontena.test", assembly: "Plugin.dll");
        var assembly = Path.Combine(directory, "Plugin.dll");
        File.WriteAllText(assembly, "the build the user saw");

        var agreed = Sha256Of(assembly);

        // What settings hold after the user answers: the id and version they recognised it by, and the
        // digest of what they were answering about.
        bool Allowed(PluginCandidate c) =>
            c.Manifest.Id == "com.kontena.test"
            && c.Manifest.Version == "1.0.0"
            && c.Sha256 == agreed;

        // Past the consent gate — this stand-in is not loadable, so it is rejected further along, and
        // what matters here is only that it was not stopped at the question.
        // (That the digest travels out on a plugin that does load is
        // A_loaded_plugin_reports_the_digest_it_was_allowed_on.)
        var before = Assert.Single(PluginLoader.Discover(_root, Allowed));
        Assert.NotEqual(PluginStatus.AwaitingConsent, before.Status);

        // Same directory, same manifest, same id, same version — different code.
        File.WriteAllText(assembly, "something else entirely");

        var after = Assert.Single(PluginLoader.Discover(_root, Allowed));

        Assert.Equal(PluginStatus.AwaitingConsent, after.Status);
        Assert.NotEqual(agreed, after.Sha256);
        Assert.Empty(after.Providers);
    }

    [Fact]
    public void A_loaded_plugin_reports_the_digest_it_was_allowed_on()
    {
        // The prompt records what the scan hashed rather than hashing again on confirm, so the value
        // has to travel out on the result.
        var directory = InstallFixture();

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));

        Assert.Equal(PluginStatus.Loaded, found.Status);
        Assert.Equal(Sha256Of(Path.Combine(directory, "Kontena.TestPlugin.dll")), found.Sha256);
    }

    private static string Sha256Of(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(file));
    }

    [Fact]
    public void A_missing_assembly_is_rejected_rather_than_thrown()
    {
        WriteManifest("com.kontena.test", assembly: "NotThere.dll", writeAssembly: false);

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));

        Assert.Equal(PluginStatus.Rejected, found.Status);
        Assert.NotNull(found.Reason);
    }

    [Fact]
    public void A_missing_assembly_is_rejected_rather_than_left_waiting_for_consent()
    {
        // Even with nothing agreed to: the prompt asks whether to run an assembly, and there is none
        // here to run. Awaiting consent would mean the same unanswerable question every launch.
        WriteManifest("com.kontena.test", assembly: "NotThere.dll", writeAssembly: false);

        var found = Assert.Single(PluginLoader.Discover(_root, _ => false));

        Assert.Equal(PluginStatus.Rejected, found.Status);
        Assert.Equal(string.Empty, found.Sha256);
    }

    [Fact]
    public void An_absolute_assembly_path_cannot_escape_the_plugin_directory()
    {
        // A directory that reused a previously approved id@version could otherwise point the loader at
        // any assembly on disk, since consent is keyed on id@version alone. A real, loadable assembly
        // outside the plugin's own directory to point at — a fake file would fail to load for its own
        // reason regardless of the fix, proving nothing:
        Directory.CreateDirectory(_outside);
        var escaped = Path.Combine(_outside, "Kontena.TestPlugin.dll");
        File.Copy(Path.Combine(FixtureDirectory, "Kontena.TestPlugin.dll"), escaped, overwrite: true);

        WriteManifest("com.kontena.test", assembly: escaped);

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));

        // The absolute path is stripped to its filename before being combined with the plugin's own
        // directory, so this looks for "Kontena.TestPlugin.dll" right there — finds nothing, since it
        // was never copied into the plugin's own directory — and is rejected rather than loading the
        // real assembly sitting outside.
        Assert.Equal(PluginStatus.Rejected, found.Status);
        Assert.NotNull(found.Reason);
        Assert.Empty(found.Providers);
    }

    [Fact]
    public void A_relative_assembly_path_cannot_escape_the_plugin_directory_with_dot_dot()
    {
        Directory.CreateDirectory(_outside);
        foreach (var file in Directory.GetFiles(FixtureDirectory))
            File.Copy(file, Path.Combine(_outside, Path.GetFileName(file)), overwrite: true);

        // Two levels up: one out of the plugin's own directory (_root/com.kontena.test), one more out
        // of _root itself, to reach _outside — its sibling in the temp directory.
        var relative = Path.Combine("..", "..", Path.GetFileName(_outside), "Kontena.TestPlugin.dll");
        WriteManifest("com.kontena.test", assembly: relative);

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));

        // The ".." is stripped away along with the rest of the path, leaving "Kontena.TestPlugin.dll"
        // combined with the plugin's own directory, where the real fixture never got copied — so this
        // must reject rather than load the one sitting next to _root.
        Assert.Equal(PluginStatus.Rejected, found.Status);
        Assert.NotNull(found.Reason);
        Assert.Empty(found.Providers);
    }

    [Fact]
    public void One_broken_directory_does_not_hide_a_healthy_one()
    {
        Directory.CreateDirectory(Path.Combine(_root, "empty"));
        WriteManifest("com.kontena.test");

        var found = PluginLoader.Discover(_root, _ => false);

        Assert.Equal(2, found.Count);
        Assert.Contains(found, p => p.Status == PluginStatus.AwaitingConsent);
        Assert.Contains(found, p => p.Status == PluginStatus.Rejected);
    }

    [Fact]
    public void The_fixture_ships_its_own_sdk_copy()
    {
        // Guards the guard: if this stops being true, the test below proves nothing.
        Assert.True(File.Exists(Path.Combine(FixtureDirectory, "Kontena.Sdk.dll")));
    }

    [Fact]
    public void An_allowed_plugin_loads_and_contributes_its_providers()
    {
        InstallFixture();

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));

        Assert.Equal(PluginStatus.Loaded, found.Status);
        Assert.Null(found.Reason);
        var provider = Assert.Single(found.Providers);
        Assert.Equal("testplugin", provider.Backend);
    }

    [Fact]
    public void The_sdk_comes_from_the_host_even_though_the_plugin_ships_one()
    {
        InstallFixture();

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));
        var provider = Assert.Single(found.Providers);

        // The cast in the loader already proves type identity, but say so out loud: this is the
        // failure that produces no exception and no backend, only silence.
        Assert.Same(
            typeof(Kontena.Sdk.IBackendProvider),
            provider.GetType().GetInterface("Kontena.Sdk.IBackendProvider"));
    }

    [Fact]
    public void A_plugin_that_contributes_nothing_useful_is_rejected_rather_than_thrown()
    {
        // A directory whose assembly holds no IEnginePlugin: point the manifest at the SDK itself.
        var dir = InstallFixture();
        File.WriteAllText(Path.Combine(dir, "plugin.json"), JsonSerializer.Serialize(new
        {
            id = "com.kontena.test",
            name = "Test Plugin",
            version = "1.0.0",
            author = "Kontena",
            description = "A plugin.",
            minSdkVersion = "0.1.0",
            assembly = "Kontena.Sdk.dll",
        }));

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));

        Assert.Equal(PluginStatus.Rejected, found.Status);
        Assert.NotNull(found.Reason);
    }

    [Fact]
    public void A_plugin_that_needs_a_newer_sdk_is_rejected_with_a_reason()
    {
        InstallFixture(minSdk: "99.0.0");

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));

        Assert.Equal(PluginStatus.Rejected, found.Status);
        Assert.Contains("99.0.0", found.Reason);
        Assert.Empty(found.Providers);
    }

    [Fact]
    public void A_plugin_that_needs_exactly_this_sdk_loads()
    {
        var host = typeof(Kontena.Sdk.IEnginePlugin).Assembly.GetName().Version!;
        InstallFixture(minSdk: $"{host.Major}.{host.Minor}.{host.Build}");

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));

        Assert.Equal(PluginStatus.Loaded, found.Status);
    }

    [Fact]
    public void A_plugin_without_a_minimum_sdk_loads()
    {
        InstallFixture(minSdk: "");

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));

        Assert.Equal(PluginStatus.Loaded, found.Status);
    }

    [Fact]
    public void An_unparseable_minimum_sdk_is_rejected_rather_than_ignored()
    {
        InstallFixture(minSdk: "banana");

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));

        Assert.Equal(PluginStatus.Rejected, found.Status);
        Assert.NotNull(found.Reason);
    }

    [Fact]
    public void A_plugin_whose_code_disagrees_with_its_manifest_is_rejected()
    {
        // The fixture's own manifest says 1.0.0; the file on disk claims 9.9.9. Consent was given for
        // what the file said, so the code may not turn out to be something else.
        InstallFixture(version: "9.9.9");

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));

        Assert.Equal(PluginStatus.Rejected, found.Status);
        Assert.NotNull(found.Reason);
    }

    [Fact]
    public void A_provider_whose_identity_getter_throws_is_rejected_rather_than_taking_down_startup()
    {
        // The host reads Backend/DisplayName/Chip/Kind/ChipStyle while building the very first switcher,
        // outside any try and before there is a window to report a failure in (KON-279 final review,
        // finding 1). A getter that throws here has to cost this one plugin its place in the list, not
        // the launch.
        InstallHostileFixture();

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));

        Assert.Equal(PluginStatus.Rejected, found.Status);
        Assert.NotNull(found.Reason);
        Assert.Empty(found.Providers);
    }

    [Fact]
    public void A_plugin_that_contributes_only_pages_loads()
    {
        // Manifest Studio's shape (KON-331): no IEnginePlugin anywhere in the assembly. That used to be
        // the loader's rejection reason, so this is the test that says a plugin need not be an engine.
        InstallUiFixture();

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));

        Assert.Equal(PluginStatus.Loaded, found.Status);
        Assert.Empty(found.Providers);
        var page = Assert.Single(found.Pages);
        Assert.Equal("editor", page.Key);
        Assert.Equal("Editor", page.Label);
    }

    [Fact]
    public void A_loaded_plugin_leaves_its_directory_deletable()
    {
        // What KON-405 was: loading by path keeps the assembly open for the life of a context that is
        // never collectible, so on Windows the file could not be deleted or replaced afterwards — a
        // plugin impossible to uninstall or update without closing Kontena, and every test in this
        // class failing in Dispose() on windows-latest while passing on the other two runners.
        //
        // This assertion is only ever load-bearing on Windows: Linux and macOS let an open file be
        // unlinked, so it would pass there with the handle still held. That is fine — CI runs all
        // three, and the runner that can tell the difference is the one that regressed.
        var directory = InstallFixture();

        Assert.Equal(PluginStatus.Loaded, Assert.Single(PluginLoader.Discover(_root, _ => true)).Status);

        Directory.Delete(directory, recursive: true);

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void A_plugin_that_contributes_both_registers_both()
    {
        InstallFixture();

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));

        Assert.Equal(PluginStatus.Loaded, found.Status);
        Assert.Single(found.Providers);
        Assert.Single(found.Pages);
    }

    // ---- Platform requirements (KON-280) ----------------------------------------------------------

    [Fact]
    public void A_plugin_for_another_platform_is_rejected_with_what_it_needs()
    {
        InstallFixture(platforms: [new { os = "haiku" }]);

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));

        Assert.Equal(PluginStatus.Rejected, found.Status);
        Assert.Contains("haiku", found.Reason);
        Assert.Empty(found.Providers);
    }

    [Fact]
    public void A_plugin_for_this_platform_loads()
    {
        InstallFixture(platforms: [new { os = ThisOs }]);

        Assert.Equal(PluginStatus.Loaded, Assert.Single(PluginLoader.Discover(_root, _ => true)).Status);
    }

    [Fact]
    public void A_plugin_that_names_no_platform_loads_anywhere()
    {
        InstallFixture(platforms: []);

        Assert.Equal(PluginStatus.Loaded, Assert.Single(PluginLoader.Discover(_root, _ => true)).Status);
    }

    /// <summary>Apple's <c>container</c> wanting macOS 26 is the case the version part exists for.</summary>
    [Fact]
    public void A_plugin_that_needs_a_newer_os_than_this_one_is_rejected()
    {
        var above = Environment.OSVersion.Version.Major + 1;

        InstallFixture(platforms: [new { os = ThisOs, minVersion = $"{above}.0" }]);

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));

        Assert.Equal(PluginStatus.Rejected, found.Status);
        Assert.Contains($"{above}.0", found.Reason);
    }

    /// <summary>
    /// Before the consent question, not after: a plugin that cannot run here is not something to ask
    /// the user about, and the prompt would come back every launch with no answer that helps.
    /// </summary>
    [Fact]
    public void The_platform_is_checked_before_consent_is_asked()
    {
        InstallFixture(platforms: [new { os = "haiku" }]);

        var asked = false;

        var found = Assert.Single(PluginLoader.Discover(_root, _ =>
        {
            asked = true;
            return true;
        }));

        Assert.False(asked);
        Assert.Equal(PluginStatus.Rejected, found.Status);
    }

    // ---- The contribution declaration, held to the assembly (KON-280) -----------------------------

    [Fact]
    public void A_plugin_that_contributes_a_backend_kind_it_did_not_declare_is_rejected()
    {
        InstallFixture(backends: []);

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));

        Assert.Equal(PluginStatus.Rejected, found.Status);
        Assert.Contains("does not declare", found.Reason);
        Assert.Empty(found.Providers);
    }

    [Fact]
    public void A_plugin_that_contributes_pages_it_did_not_declare_is_rejected()
    {
        InstallFixture(contributesUi: false);

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));

        Assert.Equal(PluginStatus.Rejected, found.Status);
        Assert.Empty(found.Pages);
    }

    [Fact]
    public void A_plugin_that_declares_a_ui_it_does_not_have_is_rejected()
    {
        InstallHostileFixture();

        // The hostile fixture is an IEnginePlugin and nothing else; claiming pages is the lie here.
        var dir = Path.Combine(_root, "com.kontena.hostile");
        var manifest = File.ReadAllText(Path.Combine(dir, "plugin.json"));
        File.WriteAllText(
            Path.Combine(dir, "plugin.json"),
            manifest.Replace("}", ",\"contributesUi\":true}", StringComparison.Ordinal));

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));

        Assert.Equal(PluginStatus.Rejected, found.Status);
        Assert.Contains("no IUiPlugin", found.Reason);
    }

    /// <summary>
    /// Under-delivery is not a lie. nerdctl declares an engine backend and contributes one provider per
    /// containerd namespace, so a machine without nerdctl gives it none — and that plugin still loaded
    /// correctly, it just has nothing to offer here.
    /// </summary>
    [Fact]
    public void A_plugin_that_declares_more_than_it_contributes_here_still_loads()
    {
        InstallFixture(backends: ["engine", "cluster"]);

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));

        Assert.Equal(PluginStatus.Loaded, found.Status);
        Assert.Single(found.Providers);
    }

    // ---- MinSdkVersion is major.minor.patch, and nothing else (KON-280) ---------------------------

    [Theory]
    [InlineData("0.1")]
    [InlineData("0.1.0.0")]
    [InlineData("0.1.0-beta")]
    [InlineData("v0.1.0")]
    public void A_minimum_sdk_that_is_not_major_minor_patch_is_rejected(string minSdk)
    {
        InstallFixture(minSdk: minSdk);

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));

        Assert.Equal(PluginStatus.Rejected, found.Status);
        Assert.Contains("major.minor.patch", found.Reason);
    }
}
