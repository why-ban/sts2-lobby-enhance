using System.Diagnostics;
using Xunit;

namespace Sts2LobbyEnhance.Tests;

/// <summary>
/// The anti-false-green gate: a green <c>dotnet test</c> must mean the game-dependent cases actually ran.
///
/// 32 of the suite's cases skip themselves when the game DLL, an official DLL or the built patch is missing
/// (<see cref="TestRunLedger"/>). Without this file a green run on a machine without Slay the Spire 2 would mean
/// those 32 never asserted anything.
///
///   1. <b>Input probe</b>. Every input that makes one of those cases skip is checked here.
///   2. <b>Execution set</b>. The cases that recorded themselves must be exactly the expected list. This fact may run
///      before the cases it counts, so it waits for them (bounded) instead of asserting whatever order the runner
///      happened to pick.
///
/// Set <c>STS2_ALLOW_MISSING_GAME_INPUTS=1</c> to downgrade a missing input to a skip (for a developer
/// without the game). The skip message lists every missing input, so the exception is never silent.
/// </summary>
public sealed class GameDependentCoverageTests
{
    private const string AllowMissingInputsVariable = "STS2_ALLOW_MISSING_GAME_INPUTS";

    private readonly ITestOutputHelper _output;

    public GameDependentCoverageTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void EveryGameDependentTestActuallyRan()
    {
        int expected = TestRunLedger.ExpectedGameDependentTestCount;
        int executed = TestRunLedger.ExecutedCount;
        List<string> missingInputs = MissingInputs();

        _output.WriteLine($"game-dependent cases recorded in this process: {executed}/{expected}");
        foreach (string name in TestRunLedger.ExecutedNames)
        {
            _output.WriteLine("  ran: " + name);
        }

        if (missingInputs.Count > 0)
        {
            int wouldSkip = Math.Max(0, expected - executed);
            string detail =
                $"{wouldSkip} 个游戏依赖型测试没有真正执行断言（本次运行只记录了 {executed}/{expected} 个）。" +
                Environment.NewLine +
                "原因（这些输入缺失时对应用例走 Assert.Skip，dotnet test 仍然退出 0）：" +
                Environment.NewLine +
                string.Join(Environment.NewLine, missingInputs.Select(reason => "  - " + reason));

            if (string.Equals(
                    Environment.GetEnvironmentVariable(AllowMissingInputsVariable),
                    "1",
                    StringComparison.Ordinal))
            {
                Assert.Skip(
                    $"Skipped on request ({AllowMissingInputsVariable}=1). " + detail);
            }

            Assert.Fail(
                detail + Environment.NewLine +
                "这是「测试全绿但其实没跑」：发布闸门不允许在这种状态下通过。" + Environment.NewLine +
                $"确实要在没有游戏/构建产物的机器上跑，请设置 {AllowMissingInputsVariable}=1（skip 消息会列出全部缺失项）。");
        }

        // Every input is present, so none of them may skip. This fact can still run before the cases it counts, so wait
        // for the rest of the run (bounded). Waiting on the *set* rather than the count is what lets the failure name
        // the cases: a count alone also passes when a new case replaces a skipped one.
        var clock = Stopwatch.StartNew();
        while (TestRunLedger.MissingTests().Count > 0 && clock.Elapsed < TimeSpan.FromSeconds(60))
        {
            Thread.Sleep(50);
        }

        List<string> missingCases = TestRunLedger.MissingTests().ToList();
        List<string> unexpectedCases = TestRunLedger.UnexpectedTests().ToList();
        List<string> ran = TestRunLedger.ExecutedNames.ToList();
        _output.WriteLine($"game-dependent cases executed: {ran.Count}/{expected}");
        foreach (string name in missingCases)
        {
            _output.WriteLine("  missing: " + name);
        }

        foreach (string name in unexpectedCases)
        {
            _output.WriteLine("  unexpected: " + name);
        }

        if (missingCases.Count == 0 && unexpectedCases.Count == 0)
        {
            return;
        }

        List<string> lines = [$"期望 {expected} 个游戏依赖型用例，实际执行 {ran.Count} 个。"];
        if (missingCases.Count > 0)
        {
            lines.Add($"有输入存在却仍有用例跳过，没有执行断言的是（{missingCases.Count} 个）：");
            lines.AddRange(missingCases.Select(name => "  - " + name));
        }

        if (unexpectedCases.Count > 0)
        {
            lines.Add(
                $"这 {unexpectedCases.Count} 个用例不在期望名单里（新增、改名或阈值过期，请同步 " +
                "tests/Sts2LobbyEnhance.Tests/TestRunLedger.cs 的 ExpectedGameDependentTests）：");
            lines.AddRange(unexpectedCases.Select(name => "  - " + name));
        }

        Assert.Fail(string.Join(Environment.NewLine, lines));
    }

    /// <summary>
    /// Every input whose absence makes a case call <c>Assert.Skip</c> somewhere in this suite. One entry
    /// per skip site, so the list doubles as documentation of what "the machine can run the suite" means.
    /// </summary>
    private static List<string> MissingInputs()
    {
        List<string> missing = [];

        if (TestPaths.GameDirectory is null)
        {
            missing.Add($"game data directory not found (sts2.dll). {TestPaths.GameDirectoryHint}");
        }
        else if (TestPaths.GameAssembly is null || !File.Exists(TestPaths.GameAssembly))
        {
            missing.Add($"sts2.dll is missing from {TestPaths.GameDirectory}.");
        }

        if (TestPaths.OfficialModDll is null)
        {
            missing.Add($"official sts2_lan_connect DLL not found. {TestPaths.OfficialModHint}");
        }

        if (TestPaths.OfficialMod064Dll is null)
        {
            missing.Add($"official sts2_lan_connect 0.6.4 DLL not found. {TestPaths.OfficialMod064Hint}");
        }

        if (TestPaths.BuiltPatchAssembly is null)
        {
            missing.Add($"the patch assembly has not been built. {TestPaths.BuildHint}");
        }

        string contentDirectory = Path.Combine(TestPaths.PackagingDirectory, "content");
        foreach (string name in new[] { "sts2_lobby_enhance.dll", "LICENSE", "THIRD_PARTY_NOTICES" })
        {
            string path = Path.Combine(contentDirectory, name);
            if (!File.Exists(path))
            {
                missing.Add($"packaging/content/{name} is missing; run packaging/build-content.ps1.");
            }
        }

        string image = Path.Combine(TestPaths.PackagingDirectory, "image.png");
        if (!File.Exists(image))
        {
            missing.Add("packaging/image.png is missing; run python packaging/make-image.py.");
        }

        foreach (string name in new[] { "LICENSE", "THIRD_PARTY_NOTICES" })
        {
            string path = TestPaths.DocumentationSource(name);
            if (!File.Exists(path))
            {
                missing.Add($"{name} is missing from {Path.GetDirectoryName(path)}.");
            }
        }

        // The release-package test skips when dist/ is empty or absent, and the gate runs pack-release before
        // dotnet test precisely so it never has to. Listing it here turns "silently skipped" into a named
        // missing input instead.
        if (ReleasePackageTests.ReleaseDirectory() is not { } releaseDirectory)
        {
            missing.Add($"dist/ is missing ({ReleasePackageTests.ExpectedReleaseDirectory()}); run tools/pack-release/pack-release.ps1 before dotnet test.");
        }
        else if (Directory.GetFiles(releaseDirectory, "sts2-lobby-enhance-v*.zip").Length == 0)
        {
            missing.Add($"no sts2-lobby-enhance-v*.zip in {releaseDirectory}; run tools/pack-release/pack-release.ps1 before dotnet test.");
        }

        string? workDirectory = Path.GetDirectoryName(TestPaths.CompanionRoot);
        string? upstreamLicense = workDirectory is null
            ? null
            : Path.Combine(workDirectory, "lobby", "STS2-Game-Lobby", "LICENSE");

        if (upstreamLicense is null || !File.Exists(upstreamLicense))
        {
            missing.Add("the upstream repository licence (work/lobby/STS2-Game-Lobby/LICENSE) is not on this machine.");
        }

        return missing;
    }
}
