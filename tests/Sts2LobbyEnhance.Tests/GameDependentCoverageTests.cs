using Xunit;

namespace Sts2LobbyEnhance.Tests;

/// <summary>
/// The anti-false-green gate: a green <c>dotnet test</c> must mean the game-dependent cases actually ran.
///
/// 22 of the suite's cases skip themselves when the game DLL, the official DLL, the built patch or the
/// packaging workspace is missing (<see cref="TestRunLedger"/>). Without this file, "47 passed" on a
/// machine without Slay the Spire 2 means 22 of them never asserted anything.
///
/// The gate has two halves, and only the second one depends on test ordering:
///   1. <b>Input probe</b> (order-independent). Every input that makes one of those cases skip is checked
///      here. If all of them are present, none of the 22 can take its skip branch, whatever order the
///      runner picks.
///   2. <b>Execution count</b> (order-dependent). <see cref="TestRunLedger.ExecutedCount"/> is compared
///      with the hard-coded threshold. When this fact happens to run last it reports the full number;
///      when it runs first the count is legitimately small, so it is reported and not asserted — the
///      probe above is what carries the guarantee in that case.
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
        List<string> missing = MissingInputs();

        _output.WriteLine($"game-dependent cases recorded in this process: {executed}/{expected}");
        foreach (string name in TestRunLedger.ExecutedNames)
        {
            _output.WriteLine("  ran: " + name);
        }

        if (missing.Count > 0)
        {
            int wouldSkip = Math.Max(0, expected - executed);
            string detail =
                $"{wouldSkip} 个游戏依赖型测试没有真正执行断言（本次运行只记录了 {executed}/{expected} 个）。" +
                Environment.NewLine +
                "原因（这些输入缺失时对应用例走 Assert.Skip，dotnet test 仍然退出 0）：" +
                Environment.NewLine +
                string.Join(Environment.NewLine, missing.Select(reason => "  - " + reason));

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

        // All inputs are present, so nothing in the 22 can skip. The count can then only be *below* the
        // threshold because this fact ran before the others; it must never be above it. An increase means
        // a game-dependent case was added without bumping ExpectedGameDependentTestCount.
        Assert.True(
            executed <= expected,
            $"记录到 {executed} 个游戏依赖型用例，但阈值 ExpectedGameDependentTestCount 只有 {expected}。" +
            "新增了游戏依赖型用例就必须同步 tests/.../TestRunLedger.cs 的阈值，否则「22 个都跑过」这句话就不成立了。");

        _output.WriteLine($"all inputs present; the {expected} game-dependent cases cannot skip in this run.");
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
            missing.Add($"official sts2_lan_connect 0.6.3 DLL not found. {TestPaths.OfficialModHint}");
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
            string path = Path.Combine(TestPaths.DocsDirectory, name);
            if (!File.Exists(path))
            {
                missing.Add($"docs/{name} is missing.");
            }
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
