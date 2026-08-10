using Kontena.Sdk.Errors;
using Kontena.Sdk.Models;
using Xunit;

namespace Kontena.Adapters.Docker.Tests;

/// <summary>
/// The volume transfer, against a real daemon — there is nothing to assert on a command line here,
/// because this adapter talks to the Engine API rather than a CLI. Skips cleanly without Docker.
/// </summary>
[Collection(DockerCollection.Name)]
public sealed class DockerVolumeTransferTests
{
    private const string Source = "kon350-src";
    private const string Destination = "kon350-dst";
    private const string Image = "alpine:3.20";

    /// <summary>
    /// Round-trips a volume through a tar and checks that the ownership came back. That is the whole
    /// reason the staging is an archive instead of a directory: unpacked onto the host, these files
    /// would arrive owned by whoever is logged in, and a database volume of uid 999 would not start.
    /// </summary>
    [SkippableFact]
    public async Task Volume_contents_survive_an_export_and_import_with_their_owner()
    {
        using var engine = await DockerEngineTests.ConnectOrSkipAsync();
        Skip.If(!await HasImageAsync(engine), $"{Image} is not present on this host.");

        var archive = Path.Combine(Path.GetTempPath(), $"kon350-{Guid.NewGuid():N}.tar");

        await engine.CreateVolumeAsync(new CreateVolumeRequest { Name = Source });
        await engine.CreateVolumeAsync(new CreateVolumeRequest { Name = Destination });

        try
        {
            await RunToCompletionAsync(
                engine, "kon350-seed", Source,
                "mkdir -p /data/sub && echo hi > /data/sub/f && chown -R 999:999 /data/sub");

            await engine.ExportVolumeAsync(Source, archive);
            await engine.ImportVolumeAsync(Destination, archive);

            var listing = await engine.BrowseVolumeAsync(Destination, "/sub");
            Assert.Contains(listing.Entries, e => e.Name == "f");

            // Browsing reports no owner, so the point of the whole exercise is read back the only way
            // it can be: from inside a container that can see the restored file.
            var owner = await RunToCompletionAsync(
                engine, "kon350-verify", Destination, "stat -c %u:%g /data/sub/f");

            Assert.Contains("999:999", owner, StringComparison.Ordinal);
        }
        finally
        {
            // Named on purpose: a prune here would take whatever else is on this machine.
            await SafeRemoveAsync(engine, ["kon350-seed", "kon350-verify"], [Source, Destination], archive);
        }
    }

    private static async Task<bool> HasImageAsync(DockerEngine engine) =>
        (await engine.ListImagesAsync()).Any(i => $"{i.Repository}:{i.Tag}" == Image);

    /// <summary>
    /// Runs one short command with the volume bound at <c>/data</c>, waits for it to stop, and hands
    /// back what it printed. Waiting matters: exporting a volume a seed container is still filling
    /// would copy whatever happened to be there.
    /// </summary>
    private static async Task<string> RunToCompletionAsync(
        DockerEngine engine, string name, string volume, string script)
    {
        var id = await engine.CreateContainerAsync(new CreateContainerRequest
        {
            Image = Image,
            Name = name,
            Mounts = [new MountSpec(MountSpec.Volume, volume, "/data")],
            Entrypoint = ["/bin/sh"],
            Command = ["-c", script],
            Start = true,
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while ((await engine.InspectContainerAsync(id, timeout.Token)).State == ContainerState.Running)
            await Task.Delay(100, timeout.Token);

        var output = new List<string>();
        await foreach (var entry in engine.StreamLogsAsync(id, follow: false, timeout.Token))
            output.Add(entry.Message);

        return string.Join("\n", output);
    }

    private static async Task SafeRemoveAsync(
        DockerEngine engine, string[] containers, string[] volumes, string archive)
    {
        foreach (var container in containers)
        {
            try
            {
                await engine.RemoveContainerAsync(container, force: true);
            }
            catch (ResourceNotFoundException)
            {
                // Never created — the test failed before this one existed.
            }
        }

        foreach (var volume in volumes)
        {
            try
            {
                await engine.RemoveVolumeAsync(volume, force: true);
            }
            catch (ResourceNotFoundException)
            {
                // Same.
            }
        }

        File.Delete(archive);
    }
}
