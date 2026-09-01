using Kontena.Sdk.Tooling;

namespace Kontena.Plugins.ManifestStudio.Git;

public sealed record GitCommandResult
{
    public string Output { get; private init; } = string.Empty;
    public string? Error { get; private init; }
    public bool Ok => Error is null;

    public static GitCommandResult Succeeded(string output) => new() { Output = output };
    public static GitCommandResult Failed(string error) => new() { Error = error };
}

public sealed record GitStatusResult
{
    public GitStatus? Status { get; private init; }
    public string? Error { get; private init; }
    public bool Ok => Error is null;

    public static GitStatusResult Succeeded(GitStatus status) => new() { Status = status };
    public static GitStatusResult Failed(string error) => new() { Error = error };
}

/// <summary>
/// Clone, status, diff, commit, push, pull, branch switching — via the <c>git</c> CLI, not LibGit2Sharp
/// (Plan §11 point 2). Kontena has no native dependency anywhere in the codebase; LibGit2Sharp would
/// have been the first, with its own per-platform native binaries to sign (KON-53). This drives the
/// same <c>Kontena.Sdk.Tooling</c> seam (<see cref="ExternalTool"/>, <see cref="IToolRunner"/>) that
/// <c>KnownTools.Kind</c>/<c>Minikube</c> already use. <c>git</c> is still not in <c>KnownTools</c> —
/// it is not a tool the core app also needs, it belongs to this plugin — but that no longer costs it the
/// host's detection, version check and Tools page: since KON-438 the manifest declares the tools an
/// extension drives and the host lists them beside its own. The description is <see cref="GitTool"/>,
/// declared on <c>ManifestStudioPlugin.Manifest</c> and named in <c>plugin.json</c>.
/// <para>
/// Deliberately out: merge, rebase, conflict resolution, history rewriting, per-hunk staging (Plan
/// §7) — a second Git client is a product, not a feature, and the failure mode is someone else's work.
/// <see cref="CommitAsync"/> stages everything (<c>git add -A</c>) rather than offering partial staging.
/// </para>
/// <para>
/// Credentials are not handled here: a push against a remote that needs auth uses whatever credential
/// helper the user's own <c>git</c> is already configured with. Routing that through the host's
/// credential store instead (Plan §7's "de plugin vraagt de host om te pushen") is plugin-host work
/// (KON-296) — there is no host to ask yet.
/// </para>
/// </summary>
public sealed class GitCli(IToolRunner? runner = null)
{
    private static readonly ExternalTool Git = GitTool.Definition;

    private readonly IToolRunner _runner = runner ?? new ToolRunner();

    public async ValueTask<GitStatusResult> StatusAsync(string repositoryPath, CancellationToken ct = default)
    {
        var result = await RunAsync(repositoryPath, ["status", "--porcelain", "--branch"], ct).ConfigureAwait(false);
        return result.Ok ? GitStatusResult.Succeeded(GitStatusParser.Parse(result.Output)) : GitStatusResult.Failed(result.Error!);
    }

    public ValueTask<GitCommandResult> DiffAsync(string repositoryPath, CancellationToken ct = default) =>
        RunAsync(repositoryPath, ["diff", "HEAD"], ct);

    public async ValueTask<GitCommandResult> CommitAsync(string repositoryPath, string message, CancellationToken ct = default)
    {
        var add = await RunAsync(repositoryPath, ["add", "-A"], ct).ConfigureAwait(false);
        return add.Ok ? await RunAsync(repositoryPath, ["commit", "-m", message], ct).ConfigureAwait(false) : add;
    }

    public async ValueTask<GitCommandResult> CommitAndPushAsync(
        string repositoryPath, string message, CancellationToken ct = default)
    {
        var commit = await CommitAsync(repositoryPath, message, ct).ConfigureAwait(false);
        return commit.Ok ? await PushAsync(repositoryPath, ct).ConfigureAwait(false) : commit;
    }

    public ValueTask<GitCommandResult> PushAsync(string repositoryPath, CancellationToken ct = default) =>
        RunAsync(repositoryPath, ["push"], ct);

    public ValueTask<GitCommandResult> PullAsync(string repositoryPath, CancellationToken ct = default) =>
        RunAsync(repositoryPath, ["pull"], ct);

    /// <summary>
    /// Clones <paramref name="url"/> into <paramref name="targetPath"/> (KON-436), which git creates.
    /// Whether the target is usable — missing, empty, or already holding someone else's files — is
    /// git's judgement, not a pre-check here: it is the one that knows, and it says so in words worth
    /// putting on screen.
    /// <para>
    /// The one command in this class that streams instead of buffering. A clone of a real repository
    /// takes long enough that buffered output is indistinguishable from a hang (see
    /// <see cref="IToolRunner.StreamAsync"/>), and <paramref name="progress"/> hands on git's own
    /// counting — nothing here knows how big the repository is until git says so.
    /// </para>
    /// </summary>
    /// <param name="progress">Called with each line git writes, latest last. Deliberately not
    /// <c>ConfigureAwait(false)</c>-ed below, unlike everything else here: this lands in a bound
    /// property, and resuming on a pool thread would make every progress line an off-thread update.</param>
    public async ValueTask<GitCommandResult> CloneAsync(
        string url, string targetPath, Action<string>? progress = null, CancellationToken ct = default)
    {
        if (!(await _runner.FindAsync(Git, ct).ConfigureAwait(false)).Found)
            return GitCommandResult.Failed("git was not found on PATH.");

        var full = Path.GetFullPath(targetPath);
        var invocation = new ToolInvocation(Git, ["clone", "--progress", "--", url, full])
        {
            // The parent, because the target itself is what git is about to create.
            WorkingDirectory = Path.GetDirectoryName(full),

            // No timeout: a large repository over a slow line is not a hang.
            Timeout = null,
        };

        try
        {
            await foreach (var line in _runner.StreamAsync(invocation, ct))
                if (line.Text.Trim() is { Length: > 0 } text)
                    progress?.Invoke(text);

            return GitCommandResult.Succeeded(full);
        }
        catch (ToolFailedException exception)
        {
            return GitCommandResult.Failed(exception.Complaint);
        }
        catch (ToolNotFoundException exception)
        {
            return GitCommandResult.Failed(exception.Message);
        }
    }

    public ValueTask<GitCommandResult> SwitchBranchAsync(
        string repositoryPath, string branch, CancellationToken ct = default) =>
        RunAsync(repositoryPath, ["switch", branch], ct);

    public async ValueTask<IReadOnlyList<string>> ListBranchesAsync(string repositoryPath, CancellationToken ct = default)
    {
        var result = await RunAsync(repositoryPath, ["branch", "--format=%(refname:short)"], ct).ConfigureAwait(false);
        return result.Ok
            ? [.. result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
            : [];
    }

    private async ValueTask<GitCommandResult> RunAsync(
        string repositoryPath, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        if (!(await _runner.FindAsync(Git, ct).ConfigureAwait(false)).Found)
            return GitCommandResult.Failed("git was not found on PATH.");

        try
        {
            var result = await _runner
                .RunAsync(new ToolInvocation(Git, arguments) { WorkingDirectory = repositoryPath }, ct)
                .ConfigureAwait(false);

            return result.Ok ? GitCommandResult.Succeeded(result.StandardOutput) : GitCommandResult.Failed(result.Complaint);
        }
        catch (ToolNotFoundException ex)
        {
            return GitCommandResult.Failed(ex.Message);
        }
    }
}
