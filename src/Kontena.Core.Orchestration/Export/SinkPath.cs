using System.Text;

namespace Kontena.Core.Orchestration.Export;

/// <summary>
/// Path handling for the sinks. They write into somebody's GitOps repository, so "where does this
/// actually land" has to be answered before anything is written rather than explained afterwards.
/// </summary>
internal static class SinkPath
{
    /// <summary>
    /// How paths compare. Linux is the only one of the three that is reliably case-sensitive; on
    /// Windows and macOS treating <c>Alerts</c> and <c>alerts</c> as different directories would let
    /// a containment check pass for a path that is in fact the same place.
    /// </summary>
    private static readonly StringComparison Comparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>Suffixes a source label may already carry, so ".yaml" is not appended twice.</summary>
    private static readonly string[] YamlExtensions = [".yaml", ".yml"];

    /// <summary>
    /// The absolute path with every symlinked component resolved.
    /// <para>
    /// Containment is decided on real paths, never on the ones that were typed: a directory that is
    /// a link to somewhere else entirely is the difference between writing inside the repository and
    /// writing over whatever the link points at. It also cuts the other way — a macOS temp directory
    /// reaches its files through <c>/var</c> → <c>/private/var</c>, so two paths for one place must
    /// resolve to one string or an honest export gets refused.
    /// </para>
    /// </summary>
    /// <remarks>
    /// .NET has no <c>realpath</c>: <see cref="FileSystemInfo.ResolveLinkTarget"/> resolves the final
    /// component only, so walk the components and resolve each one that is a link. A link that cannot
    /// be resolved at all — a cycle — throws <see cref="IOException"/>, which is the right answer:
    /// containment cannot be established, so nothing may be written.
    /// </remarks>
    public static string Real(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var root = System.IO.Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
            return full;

        var current = root;
        foreach (var part in full[root.Length..]
                     .Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = System.IO.Path.Combine(current, part);
            if (IsLink(current))
                current = Follow(current);
        }

        return current;
    }

    /// <summary>
    /// Whether this component is a link, as opposed to an ordinary entry or nothing at all — the
    /// directory being written to usually does not exist yet, and "not there" is not a link.
    /// </summary>
    /// <remarks><see cref="File.GetAttributes(string)"/> reports on the link rather than on what it
    /// points at, which is the only way to tell a dangling link from a plain missing directory.</remarks>
    private static bool IsLink(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Where a link really leads. A link that leads nowhere is an error rather than a path: treating
    /// it as the name it was written under would let a dangling link into somebody's home directory
    /// pass a containment check and then be created by the write that follows.
    /// </summary>
    private static string Follow(string path)
        => new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName
            ?? throw new IOException($"'{path}' is a link that leads nowhere.");

    /// <summary>True when <paramref name="child"/> is <paramref name="parent"/> itself or sits below it.</summary>
    public static bool Contains(string parent, string child)
    {
        var root = parent.TrimEnd(System.IO.Path.DirectorySeparatorChar);

        return child.Equals(root, Comparison)
            || child.StartsWith(root + System.IO.Path.DirectorySeparatorChar, Comparison);
    }

    /// <summary>
    /// A file name for a bundle's source label.
    /// <para>
    /// <c>ManifestBundle.Source</c> is a label — "editor", a file name, an alert rule's name — and
    /// some of those come from a cluster, so it is untrusted input on its way into a path. Anything
    /// that is not a plain ASCII name character becomes a dash: no separator, no drive letter and no
    /// <c>..</c> can survive that, which is why the sinks can combine the result with their directory
    /// without a second escape check. ASCII only because the file lands in a repository that gets
    /// checked out on three operating systems, two of which disagree about how to normalise the rest.
    /// </para>
    /// <para>Empty when nothing usable is left, which the caller reports rather than papers over.</para>
    /// </summary>
    public static string FileName(string source)
    {
        var text = source.Trim();
        foreach (var extension in YamlExtensions)
        {
            if (text.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                text = text[..^extension.Length];
                break;
            }
        }

        var name = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
                name.Append(character);
            else if (name.Length > 0 && name[^1] != '-')
                name.Append('-');
        }

        // Leading and trailing dots and dashes go: they are what turns a name into `..`, into a
        // hidden file, or into something a shell reads as an option.
        return name.ToString().Trim('-', '.');
    }
}
