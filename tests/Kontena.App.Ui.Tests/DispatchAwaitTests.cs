using System.Runtime.CompilerServices;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// No test hands xUnit the outer task of a <c>Dispatch(async …)</c> (KON-417).
/// <para>
/// This class of mistake is silent by construction, which is why it is worth a guard rather than a
/// review note. <c>HeadlessUnitTestSession</c> has no <c>Func&lt;Task&gt;</c> overload, so an async
/// lambda binds to <c>Dispatch&lt;T&gt;(Func&lt;T&gt;, CancellationToken)</c> with <c>T = Task</c> and
/// yields a <c>Task&lt;Task&gt;</c>. xUnit awaits the outer one, which completes the moment the lambda
/// hands back its inner task — so everything after the first <c>await</c> in the body, failed
/// assertions included, lands in a task nobody awaits and the test passes whatever it found. An
/// <c>Assert.Fail</c> on line one of such a lambda reports <c>Passed!</c>.
/// </para>
/// <para>
/// Two of these had been green over real failures long enough that nobody knew: a migrate dialog
/// assertion on the wrong property, and a config section counting rows that were never materialised.
/// <c>.Unwrap()</c> is the fix, and it is one call easy to leave off the next time.
/// </para>
/// <para>
/// Read from source rather than from IL: the outer task is a perfectly ordinary <c>Task&lt;Task&gt;</c>
/// and there is nothing in the compiled test to tell it apart from one somebody meant to write.
/// </para>
/// </summary>
public sealed class DispatchAwaitTests
{
    private const string Call = "Dispatch(async";

    /// <summary>This file, which names the pattern in its prose and would otherwise report itself.</summary>
    private static readonly string OwnFile = NameOfOwnFile();

    [Fact]
    public void Every_async_Dispatch_is_unwrapped()
    {
        var sites = 0;
        var bare = new List<string>();

        foreach (var file in Directory.EnumerateFiles(TestsDirectory(), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;

            if (string.Equals(Path.GetFileName(file), OwnFile, StringComparison.Ordinal))
                continue;

            var source = File.ReadAllText(file);

            for (var at = source.IndexOf(Call, StringComparison.Ordinal); at >= 0;
                 at = source.IndexOf(Call, at + Call.Length, StringComparison.Ordinal))
            {
                sites++;

                var close = CloseOfCall(source, at + Call.Length - "async".Length - 1);

                if (!source.AsSpan(close + 1).TrimStart().StartsWith(".Unwrap()"))
                    bare.Add($"{Path.GetFileName(file)}:{source.Take(at).Count(c => c == '\n') + 1}");
            }
        }

        // A scan that matched nothing would make this test pass for the wrong reason.
        Assert.True(sites >= 5, $"Only found {sites} async Dispatch calls — the scan is broken.");

        Assert.True(bare.Count == 0,
            "Dispatch(async …) without .Unwrap() — the assertions inside never reach xUnit: "
            + string.Join(", ", bare));
    }

    /// <summary>
    /// The index of the <c>)</c> that closes the call whose <c>(</c> is at <paramref name="open"/>.
    /// Parentheses inside string literals are not discounted; a lambda body that carried an unbalanced
    /// one would send this off the end and fail the test, which is the direction this guard should
    /// fail in.
    /// </summary>
    private static int CloseOfCall(string source, int open)
    {
        var depth = 0;

        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '(')
                depth++;
            else if (source[i] == ')' && --depth == 0)
                return i;
        }

        throw new InvalidOperationException($"Unbalanced parentheses after offset {open}.");
    }

    private static string NameOfOwnFile([CallerFilePath] string path = "") => Path.GetFileName(path);

    private static string TestsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            var tests = Path.Combine(dir.FullName, "tests");
            if (Directory.Exists(Path.Combine(tests, "Kontena.App.Ui.Tests")))
                return tests;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not find tests/ from the test output directory.");
    }
}
