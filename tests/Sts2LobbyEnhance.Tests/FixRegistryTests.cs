using System.Text.RegularExpressions;
using Xunit;

namespace Sts2LobbyEnhance.Tests;

/// <summary>
/// Pins down the *inventory* of repairs.
///
/// Why this file exists: nothing else in the suite asserts how many repairs the patch installs, so
/// deleting one line from <c>Entry.cs</c>'s <c>CompatFixes.Build()</c> would leave every case green.
///
/// Scope, stated honestly:
///   * These assertions read the **source** of the patch. They are not a build and not a runtime test;
///     they cannot prove the patch behaves correctly in a game. They do prove that a repair cannot be
///     added, removed, renamed or reordered without someone editing an expectation on purpose.
///   * Runtime evidence that each repair actually installs its Harmony seams is asserted by
///     <c>tools/smoke-test</c>, which loads the shipped <c>packaging/content</c> DLL ("one real seam per
///     fix"). The two layers are complementary: this one cannot be skipped on a machine without the
///     game, and that one cannot be fooled by a stale build.
/// </summary>
public sealed class FixRegistryTests
{
    /// <summary>
    /// The exact repair ids <c>CompatFixes.Build()</c> must yield, in order.
    ///
    /// This list must stay identical to:
    ///   * <c>src/Sts2LobbyEnhance/Scripts/Entry.cs</c> — <c>CompatFixes.Build()</c>;
    ///   * <c>tools/smoke-test/Program.cs</c> — <c>expectedFixIds</c>.
    /// Three copies is deliberate: the source list, the shipped-DLL list and the human-facing list would
    /// all have to be edited together for a repair to disappear unnoticed.
    /// </summary>
    private static readonly string[] ExpectedFixIds =
    [
        "roster-rebaseline",
        "load-join-order",
        "new-run-button",
        "new-run-save-binding",
        "new-protocol-only"
    ];

    private static string ScriptsDirectory =>
        Path.Combine(TestPaths.CompanionRoot, "src", "Sts2LobbyEnhance", "Scripts");

    [Fact]
    public void CompatFixesAreExactlyTheExpectedIdsInOrder()
    {
        string entry = ReadScript("Entry.cs");
        string body = ExtractBuildBody(entry);

        List<string> classNames = Regex
            .Matches(body, @"yield\s+return\s+new\s+(?<type>\w+)\s*\(\s*\)\s*;")
            .Select(match => match.Groups["type"].Value)
            .ToList();

        Assert.False(
            classNames.Count == 0,
            "CompatFixes.Build() yielded no fixes; the extraction regex no longer matches Entry.cs.");

        List<string> actualIds = classNames.Select(ResolveFixId).ToList();

        Assert.True(
            ExpectedFixIds.SequenceEqual(actualIds, StringComparer.Ordinal),
            "CompatFixes.Build() does not yield the expected repairs." + Environment.NewLine +
            "  expected: [" + string.Join(", ", ExpectedFixIds) + "]" + Environment.NewLine +
            "  actual  : [" + string.Join(", ", actualIds) + "]" + Environment.NewLine +
            "  classes : [" + string.Join(", ", classNames) + "]" + Environment.NewLine +
            "Update this list, tools/smoke-test/Program.cs expectedFixIds and the documentation together.");

        // A rename inside a fix must not be able to slip through as "still the same number of entries".
        Assert.Equal(classNames.Count, classNames.Distinct(StringComparer.Ordinal).Count());
    }

    // ---------------------------------------------------------------- helpers

    private static string ReadScript(string relativePath)
    {
        string path = Path.Combine(ScriptsDirectory, relativePath);
        Assert.True(File.Exists(path), $"Missing source file: {path}");
        return File.ReadAllText(path);
    }

    /// <summary>Body of <c>CompatFixes.Build()</c> in Entry.cs.</summary>
    private static string ExtractBuildBody(string entrySource)
    {
        Match match = Regex.Match(
            entrySource,
            @"internal\s+static\s+IEnumerable<ICompatFix>\s+Build\s*\(\s*\)\s*\{(?<body>.*?)\r?\n\s{4}\}",
            RegexOptions.Singleline);

        Assert.True(match.Success, "Could not locate CompatFixes.Build() in Entry.cs.");
        return match.Groups["body"].Value;
    }

    /// <summary>Reads <c>public string Id =&gt; "..."</c> out of the fix's own file.</summary>
    private static string ResolveFixId(string className)
    {
        string[] candidates = Directory.GetFiles(ScriptsDirectory, className + ".cs", SearchOption.AllDirectories);
        Assert.True(
            candidates.Length == 1,
            $"Expected exactly one source file for {className}, found {candidates.Length}.");

        Match match = Regex.Match(File.ReadAllText(candidates[0]), @"public\s+string\s+Id\s*=>\s*""(?<id>[^""]+)"";");
        Assert.True(match.Success, $"Could not find 'public string Id => ...' in {candidates[0]}.");
        return match.Groups["id"].Value;
    }
}
