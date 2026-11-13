using HarmonyLib;

namespace Sts2LobbyEnhance;

/// <summary>
/// The unit of isolation: one group is one Harmony id and one rollback boundary.
///
/// Grouping follows the seam that fails together, not the feature list. When a repair in a group fails,
/// only that group is unpatched (<see cref="Entry.HarmonyIdFor"/> gives its id, for example
/// <c>sts2_lobby_enhance.join</c>); every other group keeps its patches. That is what makes a host-mod
/// update or a game hotfix cost the affected feature instead of the whole mod.
///
/// A group also decides when patches are installed: repairs that must live or die together belong in the
/// same group, repairs that can fail independently get their own.
/// </summary>
internal enum FixGroup
{
    /// <summary>join-flow + join-screen-modes: the join takeover and the screen dispatch that replaces it.</summary>
    Join,

    /// <summary>host-wire: the join-response wire layout.</summary>
    Wire,

    /// <summary>tail-error-paths: the tail runtime's abort/reject call sites (IL rewrite).</summary>
    Tail,

    /// <summary>abandon-run: abandoning a multiplayer run.</summary>
    Gameplay,

    /// <summary>mid-run-restart: restarting the hosted run from the pause panel.</summary>
    Restart,

    /// <summary>new-run-button: the game-over screen's new-run button.</summary>
    NewRun,

    /// <summary>extra-game-modes: the functional extra lobby modes (Blitz/Dread).</summary>
    Modes,

    /// <summary>room-card-pill: the room card's mode label. Pure decoration, best effort.</summary>
    Cosmetic,

    /// <summary>unlock-extra-modes: reporting the extra epochs as unlocked.</summary>
    Unlock
}

/// <summary>
/// Independent observations of the running game build.
///
/// Each flag answers one question about one member's shape. They are deliberately *not* combined into a
/// single "does this build need a patch" predicate: the six changes came from one release, but a later
/// hotfix can restore or reshape any one of them, and an AND over six flags turns any single surprise into
/// "the whole mod does nothing". Every repair therefore reads only the flags it actually depends on, in
/// <see cref="ICompatFix.Applies"/>.
///
/// <see cref="GenerateRelicFightGainedParameter"/> is diagnostic only: no repair consumes it (the
/// breakpoint it describes has no repair yet). It stays in <see cref="Describe"/> so the log still shows
/// the full picture.
/// </summary>
internal readonly record struct CompatSnapshot(
    bool InitialGameInfoLostGameMode,
    bool JoinRequestLostMaxAscensionUnlocked,
    bool DisconnectClientGainedParameter,
    bool ConnectedPeersChangedType,
    bool GenerateRelicFightGainedParameter,
    bool UpdateProgressGainedReturnValue)
{
    /// <summary>One line for the startup log. Diagnostic only — never feed this back into a gate.</summary>
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
/// One isolated repair, installed into the Harmony instance of its <see cref="FixGroup"/>.
///
/// Contract, in order of importance:
///
/// <list type="number">
/// <item>
/// <b>Resolve, then patch.</b> <see cref="Apply"/> must resolve <em>every</em> anchor it needs — types,
/// methods, fields, properties, and every shape it relies on (parameter types, arity, staticness) —
/// <em>before</em> the first <c>harmony.Patch</c> call. Never interleave: patch one seam, then look up the
/// next, and so on. A lookup that fails after a patch has been installed leaves a half-installed repair
/// whose only remaining symptom is an exception deep inside the game, with nothing in the log pointing at
/// the repair that caused it. Resolve everything, then patch everything, then log.
/// </item>
/// <item>
/// <b>Report what is missing, do not die on the first miss.</b> Collect missing anchors while resolving
/// (<see cref="AnchorProbe"/> exists for exactly this) and either answer <c>false</c> from
/// <see cref="Applies"/> with them as the reason, or throw one exception naming all of them. "missing:
/// LanConnectLobbyOverlay.GameModeFromCreateId, GameMode.Blitz" is actionable; a
/// <c>NullReferenceException</c> from the middle of a lookup is not.
/// </item>
/// <item>
/// <b>Fail alone.</b> Throwing is allowed and expected: the host catches it, records this repair as
/// failed and unpatches this repair's group only. Do not try to compensate for another repair, and do not
/// swallow exceptions that mean "my anchors are gone" — a repair that silently does nothing is worse than
/// one that reports itself as broken, because the log then claims the feature is there.
/// </item>
/// <item>
/// <b>Answer honestly.</b> <see cref="Applies"/> is a question about the running build, not about
/// whether the feature would be nice to have. Returning <c>true</c> when the anchors are missing turns a
/// clean "skipped, here is why" into a failure.
/// </item>
/// </list>
/// </summary>
internal interface ICompatFix
{
    /// <summary>Short stable identifier used in logs, in <see cref="StartupReport"/> and by release tooling.</summary>
    string Id { get; }

    /// <summary>Which Harmony id and which rollback boundary this repair belongs to.</summary>
    FixGroup Group { get; }

    /// <summary>
    /// Whether this specific repair is needed and possible for the current build.
    ///
    /// <paramref name="reason"/> must be non-null. When the answer is <c>false</c> it is shown in the
    /// startup log next to the repair id, so write a sentence a user can act on ("host mod has no
    /// LanConnectLobbyOverlay.GameModeFromCreateId"), not a status code. When the answer is <c>true</c>,
    /// use <see cref="string.Empty"/>.
    ///
    /// Throwing from here is treated as a failure of this repair alone (it is recorded and its group is
    /// unpatched); prefer answering <c>false</c> with a reason, which is not an error.
    /// </summary>
    bool Applies(in CompatSnapshot snapshot, out string reason);

    /// <summary>
    /// Install the repair. Resolve every anchor first, then patch — see the interface contract.
    /// Throwing is treated as "this repair failed" by the host and rolls back this repair's group only.
    /// </summary>
    void Apply(Harmony harmony, in CompatSnapshot snapshot);
}
