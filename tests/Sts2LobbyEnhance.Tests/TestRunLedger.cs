using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Sts2LobbyEnhance.Tests;

/// <summary>
/// Counts how many game-dependent tests actually reached their assertions in *this* process.
///
/// Why this exists: 22 of the suite's cases need the game DLL and/or the official 0.6.3 DLL, and each of
/// them calls <c>Assert.Skip</c> when its input is absent. A skip is reported as "skipped", and
/// <c>dotnet test</c> still exits 0 — so a green run on a machine without the game proved nothing about
/// the six documented breaks. That is the test-layer twin of "the log says it worked but it did not".
/// <see cref="GameDependentCoverageTests"/> turns that into a failure.
///
/// The counter is fed by the helpers every game-dependent test goes through, not by the tests
/// themselves, so a test cannot forget to report:
///   * <see cref="OfficialModScan.Require064"/> — the 12 BreakpointRegressionTests cases;
///   * <see cref="GameApiShapeTests"/>'s OpenGame() — the 9 game-API cases;
///   * <see cref="BuiltPatchTests"/>'s RequireGameDirectory() — the 1 patch-scan case;
///   * <see cref="TailRosterContractTests"/>'s RequireHostType() — the 4 roster-contract cases;
///   * <see cref="RosterLab.Open"/> — the 4 roster-epoch regression cases.
/// </summary>
internal static class TestRunLedger
{
    /// <summary>
    /// How many game-dependent cases the suite is expected to execute.
    ///
    /// 22 come from the 2026-11-13 infrastructure audit ("完全依赖游戏/官方 DLL 的 22 个用例"):
    /// 12 in BreakpointRegressionTests + 9 in GameApiShapeTests + 1 in BuiltPatchTests.
    /// 4 more were added afterwards and skip under exactly the same conditions:
    /// TailRosterContractTests (needs the game directory *and* the official 0.6.3 DLL).
    /// 4 more came with the P0 offline reproduction: RosterEpochRegressionTests (same two inputs, and it
    /// additionally loads the real game serializers).
    /// Total 30.
    ///
    /// Hard-coded on purpose: adding or deleting a game-dependent case has to be a deliberate edit here,
    /// and <see cref="GameDependentCoverageTests"/> fails when the count drifts upward.
    /// </summary>
    internal const int ExpectedGameDependentTestCount = 30;

    private static readonly ConcurrentDictionary<string, byte> Executed = new(StringComparer.Ordinal);

    /// <summary>Number of distinct game-dependent tests that got past their skip guards.</summary>
    internal static int ExecutedCount => Executed.Count;

    internal static IReadOnlyList<string> ExecutedNames =>
        Executed.Keys.Order(StringComparer.Ordinal).ToList();

    /// <summary>Called by a skip-guard helper once the guard has been passed.</summary>
    internal static void Record([CallerMemberName] string testName = "")
    {
        if (!string.IsNullOrEmpty(testName))
        {
            Executed.TryAdd(testName, 0);
        }
    }
}
