using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Timeline;
using MegaCrit.Sts2.Core.Timeline.Epochs;
using MegaCrit.Sts2.Core.Unlocks;

namespace Sts2LobbyEnhance;

    /// <summary>
    /// Feature, not a 0.113 compatibility break: make Blitz (极速模式) and Dread (恐惧模式) usable as soon as this
    /// patch is installed
    ///
    /// The game gates both modes on two timeline epochs, <c>BLITZ_EPOCH</c> and <c>DREAD_EPOCH</c>, in two
    /// independent places, so this needs two read-only seams:
    ///
    /// <list type="number">
    /// <item><b>Reported unlock state.</b> <c>StartRunLobby.HandleClientLobbyJoinRequestMessage</c> disconnects a
    /// joining client with <c>BlitzNotUnlocked</c>/<c>DreadNotUnlocked</c> unless the client's own
    /// <c>SerializableUnlockState.UnlockedEpochs</c> holds the matching id. That state comes from
    /// <see cref="SaveManager.GenerateUnlockStateFromProgress"/>, so a postfix there adds the two ids.</item>
    /// <item><b>UI gates.</b> The menus never call it: <c>NSingleplayerSubmenu.RefreshButtons</c> and
    /// <c>NMultiplayerHostSubmenu.RefreshButtons</c> ask <c>SaveManager.IsEpochRevealed&lt;BlitzEpoch&gt;()</c>,
    /// which forwards to <c>ProgressState.IsEpochRevealed(string)</c>, so a postfix there unblocks the buttons.
    /// One seam is not enough for the user-visible request.</item>
    /// </list>
    ///
    /// Both seams are pure queries and nothing here writes to the save file: we change what a read returns and
    /// what we report over the wire. The timeline still shows both epochs as not obtained, and
    /// <c>ProgressState.TotalUnlocks</c> is unaffected because it counts <c>IsEpochObtained</c>, which is unpatched
    ///
    /// Both seams are resolved up front by <see cref="Shape.TryCapture"/>, and the two epoch types by name (never
    /// <c>typeof</c>), so a build that drops Blitz or Dread is reported in the startup log while the runtime path
    /// degrades to "that mode stays locked" on its own (<see cref="ExtraModes"/>)
    /// </summary>
internal sealed class UnlockExtraModesFix : ICompatFix
{
    private const string GameAssemblyName = "sts2";
    private const string SaveManagerTypeName = "MegaCrit.Sts2.Core.Saves.SaveManager";
    private const string ProgressStateTypeName = "MegaCrit.Sts2.Core.Saves.ProgressState";
    private const string UnlockStateTypeName = "MegaCrit.Sts2.Core.Unlocks.UnlockState";
    private const string BlitzEpochTypeName = "MegaCrit.Sts2.Core.Timeline.Epochs.BlitzEpoch";
    private const string DreadEpochTypeName = "MegaCrit.Sts2.Core.Timeline.Epochs.DreadEpoch";
    private const string BooleanTypeName = "System.Boolean";
    private const string StringTypeName = "System.String";

    private static int _reported;
    private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);

    private static readonly object ReportedGate = new();
    private static Shape? _shape;

    public string Id => "unlock-extra-modes";

    public FixGroup Group => FixGroup.Unlock;

    /// <summary>
    /// Applicable when the two game seams resolve and at least one epoch type exists. Both missing means the
    /// repair would install and do nothing, the "log says applied, feature is not there" outcome, so that
    /// answers <c>false</c>
    ///
    /// <see cref="CompatSnapshot"/> is not read: no wire or signature breakpoint decides whether the local client
    /// may report an epoch as unlocked. The host mod is deliberately not checked either, because this repair
    /// touches only game APIs and a claimed dependency would be fake
    /// </summary>
    public bool Applies(in CompatSnapshot snapshot, out string reason)
    {
        Shape? shape = Shape.TryCapture(out reason);
        if (shape == null)
        {
            return false;
        }

        _shape = shape;
        reason = string.Empty;
        return true;
    }

    public void Apply(Harmony harmony, in CompatSnapshot snapshot)
    {
        Shape shape = _shape ?? throw new InvalidOperationException(
            "unlock-extra-modes: Applies() did not capture the seam shape; refusing to patch blind.");

        harmony.Patch(
            shape.Generate,
            postfix: new HarmonyMethod(typeof(UnlockExtraModesFix), nameof(GenerateUnlockStatePostfix))
            {
                priority = Priority.Low
            });

        harmony.Patch(
            shape.IsEpochRevealed,
            postfix: new HarmonyMethod(typeof(UnlockExtraModesFix), nameof(IsEpochRevealedPostfix))
            {
                priority = Priority.Low
            });

        if (shape.MissingEpochTypes.Count > 0)
        {
    // Not a failure: the runtime path already leaves an unresolvable epoch locked, and saying so here explains
    // in the startup log why one of the two modes will not appear
            Entry.Warn(
                "unlock-extra-modes: this build has no " + string.Join(", ", shape.MissingEpochTypes) +
                " type; that mode stays locked (the other seam still runs).");
        }

        Entry.Info(
            "unlock-extra-modes: patched SaveManager.GenerateUnlockStateFromProgress (reported unlock state) " +
            "and ProgressState.IsEpochRevealed (menu gates) to treat Blitz and Dread as unlocked.");
    }

    /// <summary>
    /// Rebuilds the state rather than mutating it: <see cref="UnlockState"/> keeps a private <c>HashSet</c>, while
    /// <see cref="UnlockState.ToSerializable"/> exposes everything the public constructor needs, so the round trip
    /// preserves the encounter set and the run count
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
            WarnOnce("inject-failed", "could not add the extra modes to the reported unlock state: " + exception);
        }
    }

    /// <summary>
    /// Only ever turns <c>false</c> into <c>true</c>, and only for the two epoch ids this feature owns
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

    /// <summary>
    /// Logs one message per distinct <paramref name="reason"/>. A single process-wide flag here let whichever
    /// failure fired first silence the one that matters, so keying by reason keeps each kind reporting once and
    /// none able to silence another
    /// </summary>
    private static void WarnOnce(string reason, string message)
    {
        lock (ReportedGate)
        {
            if (!Reported.Add(reason))
            {
                return;
            }
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
    /// Resolved from the game's own id table so they can never drift from the host-side check in
    /// <c>StartRunLobby</c>, which calls the same <see cref="EpochModel.GetId{T}"/>
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
                WarnOnce("resolve-" + epochName, $"cannot resolve the {epochName} id ({exception.GetType().Name}: {exception.Message}); that mode stays locked.");
                return;
            }

            if (!EpochModel.IsValid(id))
            {
                WarnOnce("invalid-" + epochName, $"'{id}' ({epochName}) is not a valid epoch in this build; that mode stays locked.");
                return;
            }

            if (!ids.Contains(id, StringComparer.Ordinal))
            {
                ids.Add(id);
            }
        }
    }

    /// <summary>
    /// Resolved before anything is patched, metadata only: no <c>typeof</c>, no <c>Invoke</c>, no Godot
    /// </summary>
    private sealed class Shape
    {
        private Shape(MethodInfo generate, MethodInfo isEpochRevealed, IReadOnlyList<string> missingEpochTypes)
        {
            Generate = generate;
            IsEpochRevealed = isEpochRevealed;
            MissingEpochTypes = missingEpochTypes;
        }

        internal MethodInfo Generate { get; }

        internal MethodInfo IsEpochRevealed { get; }

        /// <summary>Epoch types this build does not have. Informational: the runtime path already degrades.</summary>
        internal IReadOnlyList<string> MissingEpochTypes { get; }

        internal static Shape? TryCapture(out string reason)
        {
            List<string> missing = new();

            // Name first (no typeof), falling back to the assembly this file already references through
            // SaveManager/ProgressState: the fallback adds no coupling the seams do not already have.
            Assembly game = Reflect.FindAssembly(GameAssemblyName) ?? typeof(SaveManager).Assembly;

            Type? saveManager = game.GetType(SaveManagerTypeName, throwOnError: false, ignoreCase: false);
            Type? progress = game.GetType(ProgressStateTypeName, throwOnError: false, ignoreCase: false);

            if (saveManager == null) missing.Add(SaveManagerTypeName);
            if (progress == null) missing.Add(ProgressStateTypeName);

            MethodInfo? generate = ReflectScope.Exact(
                saveManager,
                "GenerateUnlockStateFromProgress",
                requireStatic: false,
                Array.Empty<string>(),
                UnlockStateTypeName,
                missing);

            MethodInfo? isEpochRevealed = ReflectScope.Exact(
                progress,
                "IsEpochRevealed",
                requireStatic: false,
                new[] { StringTypeName },
                BooleanTypeName,
                missing);

            if (missing.Count > 0)
            {
                reason = "missing: " + string.Join(", ", missing);
                return null;
            }

            string[] epochTypeNames = { BlitzEpochTypeName, DreadEpochTypeName };
            List<string> missingEpochs = new();
            foreach (string epochTypeName in epochTypeNames)
            {
                if (game.GetType(epochTypeName, throwOnError: false, ignoreCase: false) == null)
                {
                    missingEpochs.Add(epochTypeName);
                }
            }

            if (missingEpochs.Count == epochTypeNames.Length)
            {
                // Both gone: the seams would install and then inject nothing, which is the "applied but
                // does nothing" shape this redesign exists to prevent.
                reason = "missing: " + string.Join(", ", missingEpochs) + " (nothing to report as unlocked)";
                return null;
            }

            reason = string.Empty;
            return new Shape(generate!, isEpochRevealed!, missingEpochs);
        }

    }
}
