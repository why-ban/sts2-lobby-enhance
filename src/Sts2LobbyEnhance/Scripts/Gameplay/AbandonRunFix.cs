using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Daily;
using MegaCrit.Sts2.Core.Entities.Save;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace Sts2LobbyEnhance;

/// <summary>
/// Repair #6: LAN Connect's "back up and abandon the multiplayer save" flow.
///
/// The broken call is inside the async state machine
/// <c>LanConnectMultiplayerSaveCompatibility.&lt;AbandonCurrentRunAsync&gt;d__14.MoveNext</c>:
/// <c>SaveManager.Instance.UpdateProgressWithRunData(run, victory: false)</c> was compiled against a
/// <c>void</c> return, and 0.113 changed it to <c>PostRunProgressUpdateResult</c>. The return type is part of the
/// member reference signature, so the token no longer resolves — <c>Module.ResolveMember</c> throws
/// <c>MissingMethodException</c>, which means the state machine cannot be JITted and Harmony cannot read (let alone
/// patch) its body.
///
/// The seam is the async stub: <c>AbandonCurrentRunAsync</c> itself is plain IL with no removed members, so it can
/// be patched. Returning <c>false</c> from a prefix keeps <c>AsyncTaskMethodBuilder.Start</c> from ever running, so
/// the broken state machine is never compiled, and we hand back a faithful re-implementation of the same flow
/// instead. Nothing is skipped: the confirmation dialog, the safe save read, the backup, the progress write, the run
/// history entry, the daily score upload, the save deletion, the room-binding cleanup, the button refresh and both
/// popups all still happen, in the same order, with the same failure branches — every helper is the host mod's own,
/// reached by reflection, so the semantics stay the host mod's rather than being re-derived here.
///
/// The only functional difference is the new <c>PostRunProgressUpdateResult</c> return value: 0.6.3 had nothing to
/// inspect, so the result is still discarded (the flow continues and deletes the save exactly as before), but a
/// failed progress write is now logged instead of being invisible.
///
/// One diagnostic difference on top of that: when the save is simply gone (<c>no_multiplayer_run_save</c>, the state
/// a successful abandon leaves behind) the refusal is still upstream's, but it is reported at <c>Warn</c> instead of
/// <c>Error</c> — under this patch an <c>Error</c> claims the patch failed, which is not what happened.
///
/// Everything the flow touches is resolved up front by <see cref="Shape.TryCapture"/>; a missing anchor now means
/// "this repair stays out and says why" instead of an exception that used to take every other repair with it.
/// </summary>
internal sealed class AbandonRunFix : ICompatFix
{
    private const string SaveCompatibilityTypeName = "Sts2LanConnect.Scripts.LanConnectMultiplayerSaveCompatibility";
    private const string SaveBackupTypeName = "Sts2LanConnect.Scripts.LanConnectSaveBackup";
    private const string PathsTypeName = "Sts2LanConnect.Scripts.LanConnectPaths";
    private const string ConfigTypeName = "Sts2LanConnect.Scripts.LanConnectConfig";
    private const string SaveRoomBindingTypeName = "Sts2LanConnect.Scripts.LanConnectMultiplayerSaveRoomBinding";
    private const string PopupTypeName = "Sts2LanConnect.Scripts.LanConnectPopupUtil";
    private const string StubMethodName = "AbandonCurrentRunAsync";
    private const string SaveFileName = "current_run_mp.save";

    // ---------------------------------------------------------------- game-side type names (string only)
    private const string GameAssemblyName = "sts2";
    private const string SaveManagerTypeName = "MegaCrit.Sts2.Core.Saves.SaveManager";
    private const string SubmenuTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMultiplayerSubmenu";
    // MegaCrit.Sts2.Core.Saves, not .Runs: the type is declared at
    // decompiled-v0.113.0/MegaCrit/sts2/Core/Saves/SerializableRun.cs:16. The old constant named a namespace
    // that does not exist, so three anchors resolved to nothing and the whole repair was skipped.
    private const string SerializableRunTypeName = "MegaCrit.Sts2.Core.Saves.SerializableRun";
    private const string PostRunProgressUpdateResultTypeName =
        "MegaCrit.Sts2.Core.Entities.Save.PostRunProgressUpdateResult";
    private const string TaskTypeName = "System.Threading.Tasks.Task";
    private const string StringTypeName = "System.String";
    private const string IntTypeName = "System.Int32";
    private const string DateTimeOffsetTypeName = "System.DateTimeOffset";

    /// <summary>
    /// <c>TryLoadSafeCurrentRun</c>'s reason for "there is no multiplayer save to load" — i.e.
    /// <c>ReadSaveStatus.FileNotFound</c>, which is the state a *successful* abandon leaves behind
    /// (<c>dec-official/LanConnectMultiplayerSaveRoomBinding.cs:34</c>; <c>ReadSaveStatus.cs:11-19</c>).
    /// </summary>
    private const string NoMultiplayerSaveReason = "no_multiplayer_run_save";

    private static Shape? _shape;

    public string Id => "abandon-run";

    public FixGroup Group => FixGroup.Gameplay;

    /// <summary>
    /// Applicable when the host mod is present and every anchor the flow needs resolves — including the
    /// one that actually decides this repair: <c>SaveManager.UpdateProgressWithRunData</c> returning
    /// <em>exactly</em> <c>PostRunProgressUpdateResult</c>.
    ///
    /// <see cref="CompatSnapshot.UpdateProgressGainedReturnValue"/> is deliberately not read here. It only
    /// answers "returns something that is not void", which is also true for <c>ValueTask</c> or
    /// <c>Task&lt;T&gt;</c>; patching on that answer installs a repair whose body then cannot assign the
    /// result. The exact return type is the only correct prerequisite, and it is checked in
    /// <see cref="Shape.TryCapture"/>.
    /// </summary>
    public bool Applies(in CompatSnapshot snapshot, out string reason)
    {
        Assembly? host = Reflect.FindAssembly(Entry.HostModAssemblyName);
        if (host == null)
        {
            reason = "host mod 'sts2_lan_connect' is not loaded";
            return false;
        }

        Shape? shape = Shape.TryCapture(host, out reason);
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
            "abandon-run: Applies() did not capture the host shape; refusing to patch blind.");

        harmony.Patch(
            shape.Stub,
            prefix: new HarmonyMethod(typeof(AbandonRunFix), nameof(AbandonPrefix))
            {
                priority = Priority.First
            });

        Entry.Info(
            "abandon-run: patched " + SaveCompatibilityTypeName + "." + StubMethodName +
            " (state machine bypassed, 0.113 UpdateProgressWithRunData signature used).");
    }

    // ReSharper disable once UnusedParameter.Local — names are Harmony's binding contract.
    private static bool AbandonPrefix(NMultiplayerSubmenu submenu, ref Task __result)
    {
        Shape shape = _shape ?? throw new InvalidOperationException("abandon-run: shape was not captured.");
        __result = AbandonAsync(shape, submenu);
        return false;
    }

    /// <summary>
    /// Transliteration of 0.6.3's <c>AbandonCurrentRunAsync</c> body. Comments mark the source line each block
    /// reproduces so a future reader can diff it against the upstream file without re-deriving the intent.
    /// </summary>
    private static async Task AbandonAsync(Shape shape, NMultiplayerSubmenu submenu)
    {
        // 0.6.3 source 285-288.
        if (!await shape.ConfirmPermanentAbandonAsync(submenu))
        {
            return;
        }

        // 0.6.3 source 290-296 (deployed binary: LanConnectMultiplayerSaveCompatibility.cs:243-249). The refusal
        // itself is upstream's and is kept verbatim: no save is deleted when the save cannot be read.
        if (!shape.TryLoadSafeCurrentRun(out SerializableRun? run, out string failureReason) || run == null)
        {
            if (string.Equals(failureReason, NoMultiplayerSaveReason, StringComparison.Ordinal))
            {
                // The benign case, and by far the common one: an earlier abandon in this same session already
                // deleted current_run_mp.save, so there is simply nothing left to abandon (PC godot.log:994-996).
                // Upstream logs this at Error level, but once this flow runs under [lobby-enhance] an ERROR reads as
                // "the patch broke", which is exactly what it is not. Warn keeps it visible without that claim.
                Entry.Warn(
                    "abandon-run: nothing to abandon — the multiplayer save was already deleted by an earlier " +
                    "abandon in this session (or never existed). The host mod refuses in this case too; that is " +
                    $"upstream's existing behaviour, not a failure. reason={failureReason}");
                Entry.Info(
                    $"sts2_lan_connect save_compat: abandon blocked because save load failed reason={failureReason}");
                shape.ShowInfo(
                    "没有可放弃的多人存档：存档已在本次会话更早的一次放弃里删除（或本来就不存在），" +
                    "因此没有执行删除。\n这是上游的既有行为，不是失败，无需处理。");
                return;
            }

            Entry.Error($"ERROR: Refusing to delete unreadable multiplayer save: {failureReason}");
            Entry.Info(
                $"sts2_lan_connect save_compat: abandon blocked because save load failed reason={failureReason}");
            shape.ShowInfo("无法安全读取当前多人存档，因此没有执行删除。请先复制调试报告并联系开发者。");
            return;
        }

        // 0.6.3 source 298-300.
        string sourceSavePath = ProjectSettings.GlobalizePath(
            SaveManager.Instance.GetProfileScopedPath(Path.Combine("saves", SaveFileName)));
        string backupRoot = Path.Combine(shape.ResolveWritableDataDirectory(), "save-backups");

        // 0.6.3 source 301-313.
        if (!shape.TryCreateBackup(
                sourceSavePath,
                backupRoot,
                SaveManager.Instance.CurrentProfileId,
                DateTimeOffset.UtcNow,
                out string backupPath,
                out string backupError))
        {
            Entry.Error($"ERROR: Refusing to delete multiplayer save because backup failed: {backupError}");
            Entry.Info(
                $"sts2_lan_connect save_compat: abandon blocked because backup failed source={sourceSavePath}, " +
                $"reason={backupError}");
            shape.ShowInfo($"删除前备份失败，因此没有删除存档。\n原因：{backupError}");
            return;
        }

        // 0.6.3 source 315.
        Entry.Info($"sts2_lan_connect save_compat: abandon backup created path={backupPath}");

        // 0.6.3 source 318-329. The return value did not exist on 0.111; see the class remarks.
        try
        {
            PostRunProgressUpdateResult progress = SaveManager.Instance.UpdateProgressWithRunData(run, victory: false);
            if (!progress.success)
            {
                // 0.6.3 could not observe this at all (the method returned void). The flow still continues, exactly
                // as before; only the diagnosis is new.
                Entry.Warn(
                    "abandon-run: UpdateProgressWithRunData reported success=false; " +
                    "run history will still be written and the save will still be deleted.");
            }

            RunHistoryUtilities.CreateRunHistoryEntry(run, victory: false, isAbandoned: true, run.PlatformType);
            if (run.DailyTime.HasValue)
            {
                int score = ScoreUtility.CalculateScore(run, won: false);
                _ = TaskHelper.RunSafely(DailyRunUtility.UploadScore(run.DailyTime.Value, score, run.Players));
            }
        }
        catch (Exception exception)
        {
            Entry.Error($"ERROR: Failed to upload run history/metrics: {exception}");
        }

        // 0.6.3 source 331-340.
        try
        {
            SaveManager.Instance.DeleteCurrentMultiplayerRun();
        }
        catch (Exception exception)
        {
            Entry.Error($"ERROR: Failed to delete multiplayer save after backup: {exception}");
            shape.ShowInfo($"存档删除失败；备份仍保留在：\n{backupPath}");
            return;
        }

        // 0.6.3 source 342-347.
        //
        // Everything from here on runs AFTER the save was physically deleted, so none of it may be allowed to
        // throw: an escaping exception would leave the player with a deleted save, a stale room binding, an
        // un-refreshed menu and no message at all — "nothing happened", except it did. Each step degrades on its
        // own and the final message always states where the backup is.
        string saveKey;
        try
        {
            saveKey = shape.BuildSaveKey(run);
        }
        catch (Exception exception)
        {
            Entry.Warn(
                "abandon-run: the save was deleted but its room-binding key could not be computed (" +
                Describe(exception) + "); the stale binding will be cleared on the next launch.");
            SafeShowInfo(shape, "多人存档已放弃。删除前备份保存在：\n" + backupPath);
            return;
        }

        bool removedBinding;
        try
        {
            removedBinding = shape.RemoveSaveRoomBinding(saveKey);
        }
        catch (Exception exception)
        {
            Entry.Warn("abandon-run: the save was deleted but its room binding was not removed (" + Describe(exception) + ").");
            removedBinding = false;
        }

        Entry.Info(
            $"sts2_lan_connect save_compat: abandon completed removedBinding={removedBinding}, " +
            $"saveKey={saveKey}, backupPath={backupPath}");

        try
        {
            shape.RefreshButtons(submenu);
        }
        catch (Exception exception)
        {
            Entry.Warn("abandon-run: the save was deleted but the menu could not be refreshed (" + Describe(exception) + ").");
        }

        SafeShowInfo(shape, "多人存档已放弃。删除前备份保存在：\n" + backupPath);
    }

    /// <summary>Splits <see cref="TargetInvocationException"/> so the log names the real cause.</summary>
    private static string Describe(Exception exception) =>
        exception is TargetInvocationException { InnerException: { } inner }
            ? inner.GetType().Name + ": " + inner.Message
            : exception.GetType().Name + ": " + exception.Message;

    /// <summary>
    /// Tells the player the outcome. Failing to display it must never take the flow down with it: by the time
    /// this runs the save is already gone, so the message is the only thing left to get right.
    /// </summary>
    private static void SafeShowInfo(Shape shape, string message)
    {
        try
        {
            shape.ShowInfo(message);
        }
        catch (Exception exception)
        {
            Entry.Error("abandon-run: could not show the result popup (" + Describe(exception) + "). Message was: " + message);
        }
    }

    /// <summary>
    /// Every host-mod helper and game-side member the abandon flow touches, resolved once during
    /// <see cref="Apply"/> and never again.
    ///
    /// <see cref="TryCapture"/> collects *all* the missing anchors instead of throwing on the first one, so a
    /// host or game update produces one actionable log line rather than a stack trace — and, because a throw
    /// here used to roll back every other repair, a missing anchor no longer disables the mod.
    /// </summary>
    private sealed class Shape
    {
        private Shape(
            MethodInfo stub,
            MethodInfo confirmPermanentAbandon,
            MethodInfo tryLoadSafeCurrentRun,
            MethodInfo tryCreateBackup,
            MethodInfo resolveWritableDataDirectory,
            MethodInfo buildSaveKey,
            MethodInfo removeSaveRoomBinding,
            MethodInfo showInfo,
            MethodInfo updateButtons)
        {
            Stub = stub;
            ConfirmPermanentAbandon = confirmPermanentAbandon;
            TryLoadSafeCurrentRunMethod = tryLoadSafeCurrentRun;
            TryCreateBackupMethod = tryCreateBackup;
            ResolveWritableDataDirectoryMethod = resolveWritableDataDirectory;
            BuildSaveKeyMethod = buildSaveKey;
            RemoveSaveRoomBindingMethod = removeSaveRoomBinding;
            ShowInfoMethod = showInfo;
            UpdateButtonsMethod = updateButtons;
        }

        internal MethodInfo Stub { get; }

        private MethodInfo ConfirmPermanentAbandon { get; }

        private MethodInfo TryLoadSafeCurrentRunMethod { get; }

        private MethodInfo TryCreateBackupMethod { get; }

        private MethodInfo ResolveWritableDataDirectoryMethod { get; }

        private MethodInfo BuildSaveKeyMethod { get; }

        private MethodInfo RemoveSaveRoomBindingMethod { get; }

        private MethodInfo ShowInfoMethod { get; }

        private MethodInfo UpdateButtonsMethod { get; }

        internal async Task<bool> ConfirmPermanentAbandonAsync(NMultiplayerSubmenu submenu)
        {
            object? invoked = ConfirmPermanentAbandon.Invoke(null, new object?[] { submenu });
            if (invoked is not Task<bool> task)
            {
                throw new InvalidOperationException(
                    $"{SaveCompatibilityTypeName}.ConfirmPermanentAbandonAsync did not return Task<bool>.");
            }

            return await task;
        }

        internal bool TryLoadSafeCurrentRun(out SerializableRun? run, out string failureReason)
        {
            object?[] arguments = { null, null };
            bool success = TryLoadSafeCurrentRunMethod.Invoke(null, arguments) is true;
            run = arguments[0] as SerializableRun;
            failureReason = arguments[1] as string ?? string.Empty;
            return success;
        }

        internal bool TryCreateBackup(
            string sourcePath,
            string backupRoot,
            int profileId,
            DateTimeOffset timestamp,
            out string backupPath,
            out string error)
        {
            object?[] arguments = { sourcePath, backupRoot, profileId, timestamp, null, null };
            bool success = TryCreateBackupMethod.Invoke(null, arguments) is true;
            backupPath = arguments[4] as string ?? string.Empty;
            error = arguments[5] as string ?? string.Empty;
            return success;
        }

        internal string ResolveWritableDataDirectory() =>
            ResolveWritableDataDirectoryMethod.Invoke(null, null) as string
            ?? throw new InvalidOperationException($"{PathsTypeName}.ResolveWritableDataDirectory() returned null.");

        internal string BuildSaveKey(SerializableRun run) =>
            BuildSaveKeyMethod.Invoke(null, new object?[] { run }) as string
            ?? throw new InvalidOperationException($"{SaveRoomBindingTypeName}.BuildSaveKey returned null.");

        internal bool RemoveSaveRoomBinding(string saveKey) =>
            RemoveSaveRoomBindingMethod.Invoke(null, new object?[] { saveKey }) is true;

        internal void ShowInfo(string body) => ShowInfoMethod.Invoke(null, new object?[] { body });

        /// <summary>
        /// 0.6.3 called <c>submenu.Call(NMultiplayerSubmenu.MethodName.UpdateButtons)</c>. Going through the private
        /// method directly reaches the same body without touching the generated <c>StringName</c> cache.
        /// </summary>
        internal void RefreshButtons(NMultiplayerSubmenu submenu) =>
            UpdateButtonsMethod.Invoke(submenu, null);

        internal static Shape? TryCapture(Assembly host, out string reason)
        {
            List<string> missing = new();

            Type? compatibility = host.GetType(SaveCompatibilityTypeName, throwOnError: false, ignoreCase: false);
            Type? backup = host.GetType(SaveBackupTypeName, throwOnError: false, ignoreCase: false);
            Type? paths = host.GetType(PathsTypeName, throwOnError: false, ignoreCase: false);
            Type? config = host.GetType(ConfigTypeName, throwOnError: false, ignoreCase: false);
            Type? binding = host.GetType(SaveRoomBindingTypeName, throwOnError: false, ignoreCase: false);
            Type? popup = host.GetType(PopupTypeName, throwOnError: false, ignoreCase: false);

            if (compatibility == null) missing.Add(SaveCompatibilityTypeName);
            if (backup == null) missing.Add(SaveBackupTypeName);
            if (paths == null) missing.Add(PathsTypeName);
            if (config == null) missing.Add(ConfigTypeName);
            if (binding == null) missing.Add(SaveRoomBindingTypeName);
            if (popup == null) missing.Add(PopupTypeName);

            // Name first (no typeof), falling back to the assembly this file already uses at runtime through
            // SaveManager: the fallback adds no coupling that the flow does not already have.
            Assembly game = Reflect.FindAssembly(GameAssemblyName) ?? typeof(SaveManager).Assembly;
            Type? saveManager = game.GetType(SaveManagerTypeName, throwOnError: false, ignoreCase: false);
            Type? submenu = game.GetType(SubmenuTypeName, throwOnError: false, ignoreCase: false);

            if (saveManager == null) missing.Add(SaveManagerTypeName);
            if (submenu == null) missing.Add(SubmenuTypeName);

            // The stub: static, one NMultiplayerSubmenu parameter, returns Task.
            MethodInfo? stub = ReflectScope.Exact(
                compatibility,
                StubMethodName,
                requireStatic: true,
                new[] { SubmenuTypeName },
                TaskTypeName,
                missing);

            MethodInfo? confirm = ReflectScope.Exact(
                compatibility,
                "ConfirmPermanentAbandonAsync",
                requireStatic: true,
                new[] { SubmenuTypeName },
                null,
                missing);

            MethodInfo? load = ReflectScope.Exact(
                compatibility,
                "TryLoadSafeCurrentRun",
                requireStatic: true,
                new[] { SerializableRunTypeName + "&", StringTypeName + "&" },
                null,
                missing);

            MethodInfo? tryCreate = ReflectScope.Exact(
                backup,
                "TryCreate",
                requireStatic: true,
                new[]
                {
                    StringTypeName, StringTypeName, IntTypeName, DateTimeOffsetTypeName,
                    StringTypeName + "&", StringTypeName + "&"
                },
                null,
                missing);

            MethodInfo? resolveDataDirectory = ReflectScope.Exact(
                paths,
                "ResolveWritableDataDirectory",
                requireStatic: true,
                Array.Empty<string>(),
                null,
                missing);

            MethodInfo? buildSaveKey = ReflectScope.Exact(
                binding,
                "BuildSaveKey",
                requireStatic: true,
                new[] { SerializableRunTypeName },
                null,
                missing);

            MethodInfo? removeBinding = ReflectScope.Exact(
                config,
                "RemoveSaveRoomBinding",
                requireStatic: true,
                new[] { StringTypeName },
                null,
                missing);

            MethodInfo? showInfo = ReflectScope.Exact(
                popup,
                "ShowInfo",
                requireStatic: true,
                new[] { StringTypeName },
                null,
                missing);

            MethodInfo? updateButtons = ReflectScope.Exact(
                submenu,
                "UpdateButtons",
                requireStatic: false,
                Array.Empty<string>(),
                null,
                missing);

            // The game-side break itself: UpdateProgressWithRunData must return exactly PostRunProgressUpdateResult.
            // "Returns something that is not void" is not enough — a ValueTask or Task<T> return would satisfy that
            // check and then fail inside AbandonAsync, which assigns the result to PostRunProgressUpdateResult.
            MethodInfo? updateProgress = ReflectScope.Exact(
                saveManager,
                "UpdateProgressWithRunData",
                requireStatic: false,
                new[] { SerializableRunTypeName, "System.Boolean" },
                PostRunProgressUpdateResultTypeName,
                missing);

            if (updateProgress == null)
            {
                // ReflectScope.Exact() already recorded the mismatch; add the reason in the operator's language.
                missing.Add(
                    "SaveManager.UpdateProgressWithRunData must return " + PostRunProgressUpdateResultTypeName +
                    " for this repair to be applicable");
            }

            if (missing.Count > 0)
            {
                reason = "missing: " + string.Join(", ", missing);
                return null;
            }

            reason = string.Empty;
            return new Shape(
                stub!,
                confirm!,
                load!,
                tryCreate!,
                resolveDataDirectory!,
                buildSaveKey!,
                removeBinding!,
                showInfo!,
                updateButtons!);
        }

    }
}
