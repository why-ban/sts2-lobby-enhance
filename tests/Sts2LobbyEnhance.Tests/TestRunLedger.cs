using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Xunit;

namespace Sts2LobbyEnhance.Tests;

/// <summary>
/// Records which game-dependent cases actually reached their assertions in this process, and holds the list of
/// cases that are supposed to reach them.
///
/// The suite skips a case when the game DLL or the official mod DLL is missing, and a skipped case still
/// exits 0, so a green run on a machine without the game proves nothing. `GameDependentCoverageTests`
/// turns that into a failure by comparing this list against what really ran.
///
/// The name comes from the stack: every skip guard calls `Record()` from a helper, so
/// `[CallerMemberName]` would record the helper's name and collapse whole classes into one entry.
/// </summary>
internal static class TestRunLedger
{
    /// <summary>
    /// The game-dependent cases the suite executes when every input is present, by the name the runner reports (a
    /// theory row counts on its own). Listed instead of counted: a bare number can say that something is missing but
    /// not which case, and it cannot tell a new case from a skipped one. Adding or removing such a case has to be a
    /// deliberate edit here.
    /// </summary>
    internal static readonly string[] ExpectedGameDependentTests =
    [
        "Sts2LobbyEnhance.Tests.BreakpointRegressionTests.EachHistoricalBreakIsResolvedNow(typeName: \"ClientLobbyJoinRequestMessage\", memberName: \"maxAscensionUnlocked\")",
        "Sts2LobbyEnhance.Tests.BreakpointRegressionTests.EachHistoricalBreakIsResolvedNow(typeName: \"InitialGameInfoMessage\", memberName: \"gameMode\")",
        "Sts2LobbyEnhance.Tests.BreakpointRegressionTests.EachHistoricalBreakIsResolvedNow(typeName: \"NetHostGameService\", memberName: \"DisconnectClient\")",
        "Sts2LobbyEnhance.Tests.BreakpointRegressionTests.EachHistoricalBreakIsResolvedNow(typeName: \"NetHostGameService\", memberName: \"get_ConnectedPeers\")",
        "Sts2LobbyEnhance.Tests.BreakpointRegressionTests.EachHistoricalBreakIsResolvedNow(typeName: \"RelicPickingResult\", memberName: \"GenerateRelicFight\")",
        "Sts2LobbyEnhance.Tests.BreakpointRegressionTests.EachHistoricalBreakIsResolvedNow(typeName: \"SaveManager\", memberName: \"UpdateProgressWithRunData\")",
        "Sts2LobbyEnhance.Tests.BreakpointRegressionTests.EveryHistoricallyBrokenTypeStillExistsInTheGame",
        "Sts2LobbyEnhance.Tests.BreakpointRegressionTests.EveryUnresolvedReferenceIsExplainedOrAbsent",
        "Sts2LobbyEnhance.Tests.BreakpointRegressionTests.NoBreakAppearsThatTheProjectDoesNotDocument",
        "Sts2LobbyEnhance.Tests.BreakpointRegressionTests.NoReferenceIsUnresolvableAgainstTheGame",
        "Sts2LobbyEnhance.Tests.BreakpointRegressionTests.PinnedOfficialBuildIsTheOneUnderTest",
        "Sts2LobbyEnhance.Tests.BreakpointRegressionTests.ScanCoversEveryMetadataTableThatCanCarryABrokenSignature",
        "Sts2LobbyEnhance.Tests.BuiltPatchTests.PatchAssemblyResolvesCompletelyAgainstTheInstalledGame",
        "Sts2LobbyEnhance.Tests.GameApiShapeTests.ClientLobbyJoinRequestMessageSplitItsAscensionField",
        "Sts2LobbyEnhance.Tests.GameApiShapeTests.InitialGameInfoMessageLostItsGameModeField",
        "Sts2LobbyEnhance.Tests.GameApiShapeTests.JoinFlowBeginTakesASceneTree",
        "Sts2LobbyEnhance.Tests.GameApiShapeTests.JoinResponseMessageCarriesGameModeAndIsCustom",
        "Sts2LobbyEnhance.Tests.GameApiShapeTests.NetHostGameServiceConnectedPeersBecameReadOnly",
        "Sts2LobbyEnhance.Tests.GameApiShapeTests.NetHostGameServiceDisconnectClientGainedADebugReason",
        "Sts2LobbyEnhance.Tests.GameApiShapeTests.PostRunProgressUpdateResultTypeExists",
        "Sts2LobbyEnhance.Tests.GameApiShapeTests.RelicPickingResultGenerateRelicFightNeedsARelicIndex",
        "Sts2LobbyEnhance.Tests.GameApiShapeTests.SaveManagerUpdateProgressWithRunDataReturnsAResult",
        "Sts2LobbyEnhance.Tests.RosterEpochRegressionTests.T1_AnAuthorityThatAcceptedTheOldEpochRejectsTheNewEpochAsCurrentState",
        "Sts2LobbyEnhance.Tests.RosterEpochRegressionTests.T2_ResetMakesTheNextEpochAcceptableAsABootstrap",
        "Sts2LobbyEnhance.Tests.RosterEpochRegressionTests.T3_RepeatingTheAcceptedBytesAsCurrentStateStillPasses",
        "Sts2LobbyEnhance.Tests.RosterEpochRegressionTests.T4_ResetKeepsTheHostPeerIdSoTheAuthorityStillValidates",
        "Sts2LobbyEnhance.Tests.TailRosterContractTests.BindingDictionaryCarriesTheRosterAuthority",
        "Sts2LobbyEnhance.Tests.TailRosterContractTests.RosterAuthorityStateExposesAnInstanceReset",
        "Sts2LobbyEnhance.Tests.TailRosterContractTests.TailRuntimeExposesTheMessageBusResolver",
        "Sts2LobbyEnhance.Tests.TailRosterContractTests.TailRuntimeExposesTheSharedSingleton"
    ];

    internal static int ExpectedGameDependentTestCount => ExpectedGameDependentTests.Length;

    private static readonly ConcurrentDictionary<string, byte> Executed = new(StringComparer.Ordinal);

    /// <summary>Number of distinct game-dependent cases that got past their skip guards.</summary>
    internal static int ExecutedCount => Executed.Count;

    internal static IReadOnlyList<string> ExecutedNames =>
        Executed.Keys.Order(StringComparer.Ordinal).ToList();

    /// <summary>Expected cases that never reached their assertions in this process.</summary>
    internal static IReadOnlyList<string> MissingTests() =>
        ExpectedGameDependentTests
            .Except(ExecutedNames, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>Cases that ran without being expected: a new case, or one whose displayed name changed.</summary>
    internal static IReadOnlyList<string> UnexpectedTests() =>
        ExecutedNames
            .Except(ExpectedGameDependentTests, StringComparer.Ordinal)
            .ToList();

    /// <summary>Called by a skip guard once the guard has been passed.</summary>
    internal static void Record()
    {
        string? name = CurrentCaseName();
        if (name != null)
        {
            Executed.TryAdd(name, 0);
        }
    }

    private static string? CurrentCaseName()
    {
        // xUnit reports the case (a theory row counts separately), which is the granularity the expected list is
        // expressed in. The stack is only a fallback for a run without an ambient test context.
        string? display = Xunit.TestContext.Current.Test?.TestDisplayName;
        if (!string.IsNullOrEmpty(display))
        {
            return display;
        }

        return CurrentTestName();
    }

    private static string? CurrentTestName()
    {
        // Skip Record itself and its caller so a helper frame is never mistaken for the case.
        foreach (StackFrame frame in new StackTrace(1, fNeedFileInfo: false).GetFrames())
        {
            MethodBase? method = frame.GetMethod();
            if (method == null)
            {
                continue;
            }

            if (method.GetCustomAttributes(typeof(FactAttribute), inherit: true).Length == 0 &&
                method.GetCustomAttributes(typeof(TheoryAttribute), inherit: true).Length == 0)
            {
                continue;
            }

            return method.DeclaringType?.Name + "." + method.Name;
        }

        return null;
    }
}
