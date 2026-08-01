using System.Globalization;
using System.Text.RegularExpressions;

namespace Kontena.Plugins.ManifestStudio.Git;

/// <summary>Reads <c>git status --porcelain --branch</c> — the stable, script-oriented format `git`
/// itself promises never to change shape, as opposed to the human-facing plain output.</summary>
public static partial class GitStatusParser
{
    public static GitStatus Parse(string porcelain)
    {
        var branch = string.Empty;
        var ahead = 0;
        var behind = 0;
        var changes = new List<GitFileChange>();

        foreach (var line in porcelain.Split('\n'))
        {
            if (line.Length == 0)
                continue;

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                var header = line[3..];
                var branchEnd = header.IndexOfAny(['.', ' ']);
                branch = branchEnd < 0 ? header : header[..branchEnd];

                if (AheadPattern().Match(header) is { Success: true } aheadMatch)
                    ahead = int.Parse(aheadMatch.Groups[1].Value, CultureInfo.InvariantCulture);

                if (BehindPattern().Match(header) is { Success: true } behindMatch)
                    behind = int.Parse(behindMatch.Groups[1].Value, CultureInfo.InvariantCulture);

                continue;
            }

            if (line.Length < 3)
                continue;

            var xy = line[..2];
            var rest = line[3..];
            var path = rest.Contains(" -> ", StringComparison.Ordinal) ? rest.Split(" -> ")[^1] : rest;
            changes.Add(new GitFileChange(path, StatusLabel(xy)));
        }

        return new GitStatus(branch, ahead, behind, changes);
    }

    private static string StatusLabel(string xy) => xy switch
    {
        "??" => "Untracked",
        "!!" => "Ignored",
        _ when xy.Contains('D') => "Deleted",
        _ when xy.Contains('A') => "Added",
        _ when xy.Contains('R') => "Renamed",
        _ when xy.Contains('M') => "Modified",
        _ => "Changed",
    };

    [GeneratedRegex(@"ahead (\d+)")]
    private static partial Regex AheadPattern();

    [GeneratedRegex(@"behind (\d+)")]
    private static partial Regex BehindPattern();
}
