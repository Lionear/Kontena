namespace Kontena.Core.Tooling;

/// <summary>Renders an invocation the way a person would type it — for display, logs and copying.</summary>
public static class ToolCommand
{
    /// <summary>
    /// Note this is for *reading*, not for executing: Kontena never builds a command string and hands
    /// it to a shell. Everything runs from an argument list, so there is nothing to quote wrong and
    /// nothing to inject.
    /// </summary>
    public static string Describe(string executable, IReadOnlyList<string> arguments)
    {
        var text = new System.Text.StringBuilder(Path.GetFileNameWithoutExtension(executable));
        foreach (var argument in arguments)
        {
            text.Append(' ');
            text.Append(NeedsQuotes(argument) ? $"\"{argument}\"" : argument);
        }

        return text.ToString();
    }

    private static bool NeedsQuotes(string argument)
        => argument.Length == 0 || argument.Any(char.IsWhiteSpace) || argument.Contains('"');
}
