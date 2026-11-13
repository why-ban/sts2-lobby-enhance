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

    /// <summary>
    /// <c>TryLoadSafeCurrentRun</c>'s reason for "there is no multiplayer save to load" — i.e.
    /// <c>ReadSaveStatus.FileNotFound</c>, which is the state a *successful* abandon leaves behind
    /// (<c>dec-official/LanConnectMultiplayerSaveRoomBinding.cs:34</c>; <c>ReadSaveStatus.cs:11-19</c>).
    /// </summary>
    private const string NoMultiplayerSaveReason = "no_multiplayer_run_save";

    private static HostApi? _api;

    public string Id => "abandon-run";

    public bool Applies(in CompatSnapshot snapshot) => snapshot.UpdateProgressGainedReturnValue;

    public void Apply(Harmony harmony, in CompatSnapshot snapshot)
    {
        Assembly host = Reflect.FindAssembly(Entry.HostModAssemblyName)
            ?? throw new InvalidOperationException("Host mod assembly is not loaded.");

        Type compatibility = Reflect.RequireType(host, SaveCompatibilityTypeName);
        MethodInfo stub = FindStub(compatibility);

        _api = HostApi.Capture(host, compatibility);

        harmony.Patch(
            stub,
            prefix: new HarmonyMethod(typeof(AbandonRunFix), nameof(AbandonPrefix))
            {
                priority = Priority.First
            });

        Entry.Info(
            "abandon-run: patched " + compatibility.Name + "." + StubMethodName +
            " (state machine bypassed, 0.113 UpdateProgressWithRunData signature used).");
    }

    /// <summary>
    /// The stub is the only <c>AbandonCurrentRunAsync</c> on the type; the shape is still asserted so a future
    /// host mod that adds an overload cannot silently get the wrong one patched.
    /// </summary>
    private static MethodInfo FindStub(Type compatibility)
    {
        MethodInfo[] candidates = compatibility
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => string.Equals(method.Name, StubMethodName, StringComparison.Ordinal))
            .ToArray();

        MethodInfo? stub = candidates.FirstOrDefault(method =>
        {
            ParameterInfo[] parameters = method.GetParameters();
            return parameters.Length == 1
                   && parameters[0].ParameterType == typeof(NMultiplayerSubmenu)
                   && method.ReturnType == typeof(Task);
        });

        return stub
               ?? throw new MissingMethodException(
                   compatibility.FullName,
                   $"{StubMethodName}(NMultiplayerSubmenu) -> Task " +
                   $"[found: {string.Join(" | ", candidates.Select(method => method.ToString()))}]");
    }

    // ReSharper disable once UnusedParameter.Local — names are Harmony's binding contract.
    private static bool AbandonPrefix(NMultiplayerSubmenu submenu, ref Task __result)
    {
        HostApi api = _api ?? throw new InvalidOperationException("abandon-run: host API was not captured.");
        __result = AbandonAsync(api, submenu);
        return false;
    }

    /// <summary>
    /// Transliteration of 0.6.3's <c>AbandonCurrentRunAsync</c> body. Comments mark the source line each block
    /// reproduces so a future reader can diff it against the upstream file without re-deriving the intent.
    /// </summary>
    private static async Task AbandonAsync(HostApi api, NMultiplayerSubmenu submenu)
    {
        // 0.6.3 source 285-288.
        if (!await api.ConfirmPermanentAbandonAsync(submenu))
        {
            return;
        }

        // 0.6.3 source 290-296 (deployed binary: LanConnectMultiplayerSaveCompatibility.cs:243-249). The refusal
        // itself is upstream's and is kept verbatim: no save is deleted when the save cannot be read.
        if (!api.TryLoadSafeCurrentRun(out SerializableRun? run, out string failureReason) || run == null)
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
                api.ShowInfo(
                    "没有可放弃的多人存档：存档已在本次会话更早的一次放弃里删除（或本来就不存在），" +
                    "因此没有执行删除。\n这是上游的既有行为，不是失败，无需处理。");
                return;
            }

            Entry.Error($"ERROR: Refusing to delete unreadable multiplayer save: {failureReason}");
            Entry.Info(
                $"sts2_lan_connect save_compat: abandon blocked because save load failed reason={failureReason}");
            api.ShowInfo("无法安全读取当前多人存档，因此没有执行删除。请先复制调试报告并联系开发者。");
            return;
        }

        // 0.6.3 source 298-300.
        string sourceSavePath = ProjectSettings.GlobalizePath(
            SaveManager.Instance.GetProfileScopedPath(Path.Combine("saves", SaveFileName)));
        string backupRoot = Path.Combine(api.ResolveWritableDataDirectory(), "save-backups");

        // 0.6.3 source 301-313.
        if (!api.TryCreateBackup(
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
            api.ShowInfo($"删除前备份失败，因此没有删除存档。\n原因：{backupError}");
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
            api.ShowInfo($"存档删除失败；备份仍保留在：\n{backupPath}");
            return;
        }

        // 0.6.3 source 342-347.
        string saveKey = api.BuildSaveKey(run);
        bool removedBinding = api.RemoveSaveRoomBinding(saveKey);
        Entry.Info(
            $"sts2_lan_connect save_compat: abandon completed removedBinding={removedBinding}, " +
            $"saveKey={saveKey}, backupPath={backupPath}");

        api.RefreshButtons(submenu);
        api.ShowInfo($"多人存档已放弃。删除前备份保存在：\n{backupPath}");
    }

    /// <summary>
    /// Every host-mod helper the abandon flow touches, resolved once during <see cref="Apply"/>. Anything missing
    /// throws with the member name, which the host turns into "this repair failed" — the flow never runs half-wired.
    /// </summary>
    private sealed class HostApi
    {
        private HostApi(
            MethodInfo confirmPermanentAbandon,
            MethodInfo tryLoadSafeCurrentRun,
            MethodInfo tryCreateBackup,
            MethodInfo resolveWritableDataDirectory,
            MethodInfo buildSaveKey,
            MethodInfo removeSaveRoomBinding,
            MethodInfo showInfo,
            MethodInfo updateButtons)
        {
            ConfirmPermanentAbandon = confirmPermanentAbandon;
            TryLoadSafeCurrentRunMethod = tryLoadSafeCurrentRun;
            TryCreateBackupMethod = tryCreateBackup;
            ResolveWritableDataDirectoryMethod = resolveWritableDataDirectory;
            BuildSaveKeyMethod = buildSaveKey;
            RemoveSaveRoomBindingMethod = removeSaveRoomBinding;
            ShowInfoMethod = showInfo;
            UpdateButtonsMethod = updateButtons;
        }

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

        internal static HostApi Capture(Assembly host, Type compatibility)
        {
            MethodInfo confirm = Reflect.RequireStaticMethod(
                compatibility, "ConfirmPermanentAbandonAsync", typeof(NMultiplayerSubmenu));

            MethodInfo load = Reflect.RequireStaticMethod(
                compatibility,
                "TryLoadSafeCurrentRun",
                typeof(SerializableRun).MakeByRefType(),
                typeof(string).MakeByRefType());

            Type backup = Reflect.RequireType(host, SaveBackupTypeName);
            MethodInfo tryCreate = Reflect.RequireStaticMethod(
                backup,
                "TryCreate",
                typeof(string),
                typeof(string),
                typeof(int),
                typeof(DateTimeOffset),
                typeof(string).MakeByRefType(),
                typeof(string).MakeByRefType());

            Type paths = Reflect.RequireType(host, PathsTypeName);
            MethodInfo resolveDataDirectory = Reflect.RequireStaticMethod(paths, "ResolveWritableDataDirectory");

            Type binding = Reflect.RequireType(host, SaveRoomBindingTypeName);
            MethodInfo buildSaveKey = Reflect.RequireStaticMethod(binding, "BuildSaveKey", typeof(SerializableRun));

            Type config = Reflect.RequireType(host, ConfigTypeName);
            MethodInfo removeBinding = Reflect.RequireStaticMethod(config, "RemoveSaveRoomBinding", typeof(string));

            Type popup = Reflect.RequireType(host, PopupTypeName);
            MethodInfo showInfo = Reflect.RequireStaticMethod(popup, "ShowInfo", typeof(string));

            MethodInfo updateButtons = Reflect.RequireInstanceMethod(typeof(NMultiplayerSubmenu), "UpdateButtons");

            return new HostApi(
                confirm,
                load,
                tryCreate,
                resolveDataDirectory,
                buildSaveKey,
                removeBinding,
                showInfo,
                updateButtons);
        }
    }
}
