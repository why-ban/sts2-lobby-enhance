using HarmonyLib;

namespace Sts2LobbyEnhance;

/// <summary>
/// Which of the six wire/API breaks that 0.113 introduced are actually present in the running game.
///
/// The shipped LAN Connect 0.6.3 binary was compiled against 0.111. Every one of its six broken
/// member references is a *signature* change rather than a removal, so the breakage can be detected
/// purely by reflecting over the game assembly — no IL parsing and no side effects. This keeps the
/// patch self-disabling: once upstream ships a build compiled for 0.113 (or the game reverts), every
/// flag goes false and this mod does nothing at all.
/// </summary>
internal readonly record struct CompatSnapshot(
    bool InitialGameInfoLostGameMode,
    bool JoinRequestLostMaxAscensionUnlocked,
    bool DisconnectClientGainedParameter,
    bool ConnectedPeersChangedType,
    bool GenerateRelicFightGainedParameter,
    bool UpdateProgressGainedReturnValue)
{
    /// <summary>
    /// True when the game is at least 0.113: every break is present at once because they all come
    /// from the same release. Used as the single gate for "is this patch needed at all".
    /// </summary>
    internal bool RequiresPatch =>
        InitialGameInfoLostGameMode
        && JoinRequestLostMaxAscensionUnlocked
        && DisconnectClientGainedParameter
        && ConnectedPeersChangedType
        && GenerateRelicFightGainedParameter
        && UpdateProgressGainedReturnValue;

    internal string Describe() =>
        "breaks[" +
        $"gameModeGone={InitialGameInfoLostGameMode}," +
        $"maxAscensionUnlockedGone={JoinRequestLostMaxAscensionUnlocked}," +
        $"disconnectClient4={DisconnectClientGainedParameter}," +
        $"connectedPeersIReadOnly={ConnectedPeersChangedType}," +
        $"generateRelicFight4={GenerateRelicFightGainedParameter}," +
        $"updateProgressReturns={UpdateProgressGainedReturnValue}]";
}

/// <summary>
/// One isolated repair. Implementations must be individually safe: a failure in one patch is logged
/// and skipped, never allowed to take down the rest (and never allowed to leave a half-patched
/// session behind — <see cref="Apply"/> must roll itself back or refuse to start).
/// </summary>
internal interface ICompatFix
{
    /// <summary>Short stable identifier used in logs and diagnostics.</summary>
    string Id { get; }

    /// <summary>Whether this specific repair is needed for the current game build.</summary>
    bool Applies(in CompatSnapshot snapshot);

    /// <summary>Install the repair. Throwing here is treated as "this fix failed" by the host.</summary>
    void Apply(Harmony harmony, in CompatSnapshot snapshot);
}
