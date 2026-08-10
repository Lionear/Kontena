using System.Runtime.CompilerServices;
using Kontena.Sdk;
using Kontena.Sdk.Errors;
using Kontena.Sdk.Models;

namespace Kontena.Core.Migration;

/// <summary>
/// Executes a confirmed <see cref="MigrationPlan"/> against two engines. It speaks only CEAL, so it
/// does not know which engines these are.
/// <para>
/// Two rules hold throughout. The source is only ever stopped — never removed, never changed. And
/// nothing created on the target is cleaned up when a run fails: cleaning up is removing, and
/// removing asks first. What is left behind is named, so the next attempt meets it as an ordinary
/// "this already exists, overwrite?" question.
/// </para>
/// </summary>
public sealed class ContainerMigrationRunner(
    IContainerEngine source, IContainerEngine target, string stagingRoot)
{
    /// <summary>Runs the plan, one step at a time, in the order the steps have to happen.</summary>
    public async IAsyncEnumerable<MigrationProgress> RunAsync(
        MigrationPlan plan,
        ContainerInspect container,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!plan.CanRun)
        {
            throw new InvalidOperationException(
                "This plan is blocked and must not be run. The dialog has no button for it; reaching "
                + "here means a caller skipped the check.");
        }

        var staging = CreateStaging(stagingRoot);

        try
        {
            // Copying a volume out of a running container gives a torn copy — a database halfway
            // through a write. The stop is what makes the copy worth having.
            if (container.State is ContainerState.Running or ContainerState.Restarting)
            {
                await source.StopContainerAsync(container.Id, ct).ConfigureAwait(false);
                yield return new MigrationProgress("Stopping source",
                    $"Stopped '{container.Name}'. It is kept, not removed.");
            }

            yield return await EnsureImageAsync(plan.Request.Image, ct).ConfigureAwait(false);

            foreach (var volume in plan.Volumes)
            {
                if (!volume.WillCopy)
                {
                    yield return new MigrationProgress($"Volume '{volume.Name}'",
                        "Left alone: it already exists on the target and holds data.");
                    continue;
                }

                await CopyVolumeAsync(volume, staging, ct).ConfigureAwait(false);
                yield return new MigrationProgress($"Volume '{volume.Name}'", "Contents copied.");
            }

            var id = await target.CreateContainerAsync(plan.Request, ct).ConfigureAwait(false);

            yield return new MigrationProgress("Created",
                $"'{plan.Request.Name}' exists on the target engine and is stopped. Start it to see "
                + "whether it does what it did before.", id);
        }
        finally
        {
            // The staging is ours and it is a copy of data that still exists on both sides, so it is
            // the one thing that goes whatever happened.
            try
            {
                if (Directory.Exists(staging))
                    Directory.Delete(staging, recursive: true);
            }
            catch (IOException)
            {
                // A temp directory that outlives the run is untidy, not harmful, and must not turn a
                // finished migration into an error.
            }
        }
    }

    /// <summary>
    /// The staging directory, readable only by its owner (KON-364).
    /// <para>
    /// What passes through here is the contents of the container's volumes — a database, a config, the
    /// keys it was given — and the root it is made under is a temp path, which on Unix every user on the
    /// machine can read. Same reasoning as the shell session directory in <c>HostShellLauncher</c>.
    /// </para>
    /// </summary>
    private static string CreateStaging(string root) =>
        (OperatingSystem.IsWindows()
            ? Directory.CreateDirectory(root)
            : Directory.CreateDirectory(
                root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute))
        .FullName;

    private async ValueTask<MigrationProgress> EnsureImageAsync(string image, CancellationToken ct)
    {
        if (await target.InspectImageAsync(image, ct).ConfigureAwait(false) is not null)
            return new MigrationProgress("Image", $"'{image}' is already on the target engine.");

        try
        {
            await foreach (var _ in target.PullImageAsync(image, credential: null, ct).ConfigureAwait(false))
            {
                // Progress lines belong to the pull dialog, not here: this step is one line in a list
                // of steps, and a migration that scrolls is harder to read than one that does not.
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new EngineException(
                $"'{image}' could not be pulled onto the target engine: {error.Message}\n\n"
                + "If this image was built locally or lives in a private registry, move it by hand "
                + $"first:\n    docker save {image} -o image.tar\n    container image load -i image.tar\n"
                + "(That pairing is untested — `container image load` wants an OCI archive.)", error);
        }

        return new MigrationProgress("Image", $"Pulled '{image}'.");
    }

    private async ValueTask CopyVolumeAsync(VolumePlan volume, string staging, CancellationToken ct)
    {
        if (!volume.ExistsOnTarget)
        {
            await target
                .CreateVolumeAsync(new CreateVolumeRequest { Name = volume.Name }, ct)
                .ConfigureAwait(false);
        }

        var archive = Path.Combine(staging, $"{volume.Name}.tar");

        try
        {
            await source.ExportVolumeAsync(volume.Name, archive, ct).ConfigureAwait(false);
            await target.ImportVolumeAsync(volume.Name, archive, ct).ConfigureAwait(false);
        }
        finally
        {
            // One volume's archive at a time: a machine with room for the biggest volume can migrate
            // a container with ten of them.
            if (File.Exists(archive))
                File.Delete(archive);
        }
    }
}
