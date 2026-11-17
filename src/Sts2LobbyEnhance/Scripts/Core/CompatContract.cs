using HarmonyLib;

namespace Sts2LobbyEnhance;

/// <summary>
/// The unit of isolation: one group is one Harmony id and one rollback boundary.
///
/// Grouping follows the seam that fails together, not the feature list, so a host-mod update costs the
/// affected feature instead of the whole mod
/// </summary>
internal enum FixGroup
{
    NewRun,

    Modes,

    /// <summary>Roster re-baselining for LobbyBeginRun (load-join-order is separate: that one re-sorts one message list)</summary>
    Roster,

    /// <summary>Load-join player ordering (independent of Roster: it runs on every save load, that one on every start)</summary>
    LoadOrder,

    /// <summary>Room-list narrowing by game version. Answers only to the filter row, which is a different seam</summary>
    Filter,

    /// <summary>Server pool: fan-out room collection, origin badges and the server switch a cross-server join needs</summary>
    Pool,

    /// <summary>
    /// Touch scrolling and long-press selection in the chat panel. Platform-conditioned behaviour sits in three
    /// groups: this one, <see cref="Chat"/> (the hint click-through is desktop-only) and <see cref="Pool"/> (the
    /// create dialog's list scrolls by touch only), so grouping follows the seam rather than the platform
    /// </summary>
    Touch,

    /// <summary>
    /// The lobby's own layout on a phone: the sidebar has to scroll as one surface. Separate from <see cref="Touch"/>,
    /// which is about the chat widget's input handling, because these two break for different reasons
    /// </summary>
    Layout,

    /// <summary>Switching which server's public channel the chat panel is attached to</summary>
    Chat,

    /// <summary>
    /// Announcement fetching. Separate from <see cref="Pool"/> so a change to the announcement client costs the
    /// announcement fetch, not the room list
    /// </summary>
    Announce
}

/// <summary>
/// One isolated repair, installed into the Harmony instance of its <see cref="FixGroup"/>.
///
/// Resolve every anchor before the first <c>harmony.Patch</c> call. A lookup that fails after a patch has
/// been installed leaves a half-installed repair whose only symptom is an exception deep inside the game,
/// with nothing in the log pointing at the repair that caused it.
///
/// Collect every missing anchor rather than dying on the first miss (<see cref="ReflectScope"/>).
/// Throwing is expected and safe: the host catches it, records this repair as failed and unpatches this
/// repair's group only, so a repair that silently does nothing is worse than one that reports itself
/// broken.
///
/// <see cref="Applies"/> asks whether the running build allows the repair, not whether the feature would
/// be nice to have. Answering <c>true</c> with missing anchors turns a clean "skipped, here is why"
/// into a failure.
///
/// Since 2.0.0 neither method takes a game-version snapshot: the six 0.113 breaks that used to be probed
/// are adapted upstream (0.6.4), so no repair gates itself on a version any more. The room-version filter
/// still reads the local version string, but as feature input, not as a gate
/// </summary>
internal interface ICompatFix
{
    /// <summary>Stable: tests, release tooling and the upload kit pin these strings</summary>
    string Id { get; }

    /// <summary>Which Harmony id and which rollback boundary this repair belongs to.</summary>
    FixGroup Group { get; }

    /// <summary>
    /// When <c>false</c>, <paramref name="reason"/> is shown in the startup log next to the repair id, so write
    /// a sentence a user can act on rather than a status code, and use <see cref="string.Empty"/> when
    /// <c>true</c>. A throw also takes this repair's whole group down (see <c>Entry.Install</c>), so prefer
    /// answering <c>false</c>
    /// </summary>
    bool Applies(out string reason);

    /// <summary>Resolve every anchor first; throwing rolls back this repair's group only</summary>
    void Apply(Harmony harmony);
}
