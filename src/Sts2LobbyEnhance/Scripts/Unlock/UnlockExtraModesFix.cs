using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Timeline;
using MegaCrit.Sts2.Core.Timeline.Epochs;
using MegaCrit.Sts2.Core.Unlocks;

namespace Sts2LobbyEnhance;

/// <summary>
/// Feature (not a 0.113 compatibility break): make Blitz (极速模式) and Dread (恐惧模式) usable as soon
/// as this patch is installed.
///
/// The game gates both modes on two timeline epochs — <c>BLITZ_EPOCH</c> and <c>DREAD_EPOCH</c> — in two
/// independent places, so this needs two read-only seams:
///
/// <list type="number">
/// <item><b>Reported unlock state.</b> <c>StartRunLobby.HandleClientLobbyJoinRequestMessage</c> (host
/// side) disconnects a joining client with <c>BlitzNotUnlocked</c>/<c>DreadNotUnlocked</c> unless the
/// client's own <c>SerializableUnlockState.UnlockedEpochs</c> contains the matching id. That state comes
/// from <see cref="SaveManager.GenerateUnlockStateFromProgress"/> (<c>JoinFlow.AttemptJoin</c>), so a
/// postfix there adds the two ids to what we report.</item>
/// <item><b>UI gates.</b> The game's own menus never call <c>GenerateUnlockStateFromProgress</c>: both
/// <c>NSingleplayerSubmenu.RefreshButtons</c> and <c>NMultiplayerHostSubmenu.RefreshButtons</c> ask
/// <c>SaveManager.IsEpochRevealed&lt;BlitzEpoch&gt;()</c>, which forwards to
/// <c>ProgressState.IsEpochRevealed(string)</c>. A postfix there makes the two buttons (and Dread's
/// visibility) behave as unlocked. This is why one seam is not enough for the user-visible request.</item>
/// </list>
///
/// Both seams are pure queries. Nothing here writes to the save file: we never touch
/// <c>ProgressState.Epochs</c>, we only change what a read returns and what we report over the wire.
/// The timeline still shows the two epochs as not obtained, and <c>ProgressState.TotalUnlocks</c> is
/// unaffected because it counts <c>IsEpochObtained</c>, which we do not patch.
/// </summary>
internal sealed class UnlockExtraModesFix : ICompatFix
{
    private static int _reported;
    private static int _warned;

    public string Id => "unlock-extra-modes";

    /// <summary>
    /// Not tied to any single 0.113 break: this is a feature the user asked for, and
    /// <see cref="Entry"/> already refuses to run anything at all when the game build needs no patch.
    /// </summary>
    public bool Applies(in CompatSnapshot snapshot) => true;

    public void Apply(Harmony harmony, in CompatSnapshot snapshot)
    {
        MethodInfo generate = Reflect.RequireInstanceMethod(typeof(SaveManager), "GenerateUnlockStateFromProgress");
        MethodInfo isRevealed = Reflect.RequireInstanceMethod(typeof(ProgressState), "IsEpochRevealed", typeof(string));

        harmony.Patch(
            generate,
            postfix: new HarmonyMethod(typeof(UnlockExtraModesFix), nameof(GenerateUnlockStatePostfix))
            {
                priority = Priority.Low
            });

        harmony.Patch(
            isRevealed,
            postfix: new HarmonyMethod(typeof(UnlockExtraModesFix), nameof(IsEpochRevealedPostfix))
            {
                priority = Priority.Low
            });

        Entry.Info(
            "unlock-extra-modes: patched SaveManager.GenerateUnlockStateFromProgress (reported unlock state) " +
            "and ProgressState.IsEpochRevealed (menu gates) to treat Blitz and Dread as unlocked.");
    }

    /// <summary>
    /// Adds the two epoch ids to the unlock state the client reports to a host. Rebuilds the state instead
    /// of mutating it: <see cref="UnlockState"/> keeps a private <c>HashSet</c>, while
    /// <see cref="UnlockState.ToSerializable"/> already exposes everything the public constructor needs, so
    /// the round trip preserves the encounter set and the run count exactly.
    /// </summary>
    private static void GenerateUnlockStatePostfix(ref UnlockState __result)
    {
        try
        {
            IReadOnlyList<string> ids = ExtraModes.Ids;
            if (ids.Count == 0 || __result == null)
            {
                return;
            }

            SerializableUnlockState serializable = __result.ToSerializable();
            bool added = false;

            foreach (string id in ids)
            {
                if (serializable.UnlockedEpochs.Contains(id, StringComparer.Ordinal))
                {
                    continue;
                }

                serializable.UnlockedEpochs.Add(id);
                added = true;
            }

            if (!added)
            {
                return;
            }

            __result = new UnlockState(serializable.UnlockedEpochs, serializable.EncountersSeen, serializable.NumberOfRuns);
            ReportOnce(ids);
        }
        catch (Exception exception)
        {
            WarnOnce("could not add the extra modes to the reported unlock state: " + exception);
        }
    }

    /// <summary>
    /// Answers the menu gates. Only ever turns <c>false</c> into <c>true</c>, and only for the two epoch
    /// ids this feature owns, so every other unlock query keeps its real answer.
    /// </summary>
    // ReSharper disable once UnusedParameter.Local — Harmony binds by position.
    private static void IsEpochRevealedPostfix(string __0, ref bool __result)
    {
        if (__result)
        {
            return;
        }

        try
        {
            if (ExtraModes.Contains(__0))
            {
                __result = true;
            }
        }
        catch
        {
            // A menu gate must never be able to throw.
        }
    }

    private static void ReportOnce(IReadOnlyList<string> ids)
    {
        if (Interlocked.Exchange(ref _reported, 1) != 0)
        {
            return;
        }

        try
        {
            Entry.Info(
                "unlock-extra-modes: injected Blitz/Dread epoch ids into the local unlock state (writeToDisk=false) " +
                $"[{string.Join(", ", ids)}]");
        }
        catch
        {
            // Diagnostics must never be able to break a join.
        }
    }

    private static void WarnOnce(string message)
    {
        if (Interlocked.Exchange(ref _warned, 1) != 0)
        {
            return;
        }

        try
        {
            Entry.Warn("unlock-extra-modes: " + message);
        }
        catch
        {
            // Diagnostics must never be able to break a join.
        }
    }

    /// <summary>
    /// The two epoch ids, resolved once from the game's own id table so they can never drift from the
    /// host-side check in <c>StartRunLobby</c> (which calls the very same <see cref="EpochModel.GetId{T}"/>).
    /// </summary>
    private static class ExtraModes
    {
        private static readonly Lazy<IReadOnlyList<string>> LazyIds =
            new(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);

        private static readonly Lazy<HashSet<string>> LazySet =
            new(() => LazyIds.Value.ToHashSet(StringComparer.Ordinal), LazyThreadSafetyMode.ExecutionAndPublication);

        internal static IReadOnlyList<string> Ids => LazyIds.Value;

        internal static bool Contains(string? epochId) => epochId != null && LazySet.Value.Contains(epochId);

        private static IReadOnlyList<string> Resolve()
        {
            List<string> ids = new(2);
            TryAdd(ids, nameof(BlitzEpoch), static () => EpochModel.GetId<BlitzEpoch>());
            TryAdd(ids, nameof(DreadEpoch), static () => EpochModel.GetId<DreadEpoch>());
            return ids;
        }

        /// <summary>Never throws: a build without the epoch simply leaves that mode locked.</summary>
        private static void TryAdd(List<string> ids, string epochName, Func<string> getId)
        {
            string id;

            try
            {
                id = getId();
            }
            catch (Exception exception)
            {
                WarnOnce($"cannot resolve the {epochName} id ({exception.GetType().Name}: {exception.Message}); that mode stays locked.");
                return;
            }

            if (!EpochModel.IsValid(id))
            {
                WarnOnce($"'{id}' ({epochName}) is not a valid epoch in this build; that mode stays locked.");
                return;
            }

            if (!ids.Contains(id, StringComparer.Ordinal))
            {
                ids.Add(id);
            }
        }
    }
}
