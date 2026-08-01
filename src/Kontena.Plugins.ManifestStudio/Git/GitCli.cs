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
/// Status, diff, commit, push, pull, branch switching — via the <c>git</c> CLI, not LibGit2Sharp
/// (Plan §11 point 2). Kontena has no native dependency anywhere in the codebase; LibGit2Sharp would
/// have been the first, with its own per-platform native binaries to sign (KON-53). This drives the
/// same <c>Kontena.Sdk.Tooling</c> seam (<see cref="ExternalTool"/>, <see cref="IToolRunner"/>) that
/// <c>KnownTools.Kind</c>/<c>Minikube</c> already use — <c>git</c> is not in <c>KnownTools</c> itself
/// because, unlike those, it is not a tool the core app also needs; it belongs to this plugin.
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
    private static readonly ExternalTool Git = new("git", "git", ["--version"], []);

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
