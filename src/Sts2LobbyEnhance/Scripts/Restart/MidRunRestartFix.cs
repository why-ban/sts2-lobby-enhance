using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace Sts2LobbyEnhance;

/// <summary>
/// Mid-run restart ("SL"): while a run is still in progress, put the whole room back at the beginning of the
/// current floor without re-hosting, without re-joining, and without any confirmation step.
///
/// Why the save already means "start of this floor": the game writes <c>current_run_mp.save</c> inside
/// <c>RunManager.EnterMapPointInternal</c> *before* the room is created (RunManager.cs:978-981 vs :996), together
/// with the whole RNG state, and only overwrites it once the room is finished (CombatManager.cs:1418-1419,
/// EventRoom.cs:141). Loading it therefore replays the current room from its start — exactly what singleplayer
/// "SL" does (NMainMenu.OnContinueButtonPressedAsync, NMainMenu.cs:699-722).
///
/// Why the confirmation can be skipped: <c>LoadRunLobby.SetReady(bool)</c> is public and
/// <c>IsAboutToBeginGame()</c> starts the run as soon as every present player is ready (LoadRunLobby.cs:324-372).
/// The patch readies the host and every returning client programmatically, so the native "continue" screen never
/// appears. The only prompt that survives is the game's own "N players are missing" confirmation, which is kept on
/// purpose: starting with an absent player leaves their character uncontrolled.
///
/// Everything runs on the existing <c>NetService</c> (the same trick the new-run button uses: suppress the one
/// <c>Disconnect</c> issued by <c>RunManager.CleanUp</c>), so the room never disappears and the protocol profile is
/// untouched. The save file and the room binding are never deleted — unlike the new-run button, this feature only
/// ever reads the save.
/// </summary>
internal sealed class MidRunRestartFix : ICompatFix
{
    private const string RuntimeTypeName = "Sts2LanConnect.Scripts.LanConnectLobbyRuntime";
    private const string TailRuntimeTypeName = "Sts2LanConnect.Scripts.LanConnectTailMessageRuntime";
    private const string BindingTypeName = "Sts2LanConnect.Scripts.LanConnectMultiplayerSaveRoomBinding";
    private const string PanelTypeName = "Sts2LanConnect.Scripts.LanConnectRoomManagementPanel";
    private const string ModIdPrefix = "sts2_lobby_enhance";
    private const string SaveRelativePath = "saves/current_run_mp.save";

    /// <summary>
    /// How long the client waits for the host's <c>ClientLoadJoinResponseMessage</c>.
    ///
    /// The host's own deadline for the join request it is answering is 10000 ms
    /// (<c>LoadRunLobby.ClientResponseTimeout</c>, LoadRunLobby.cs:81 / StartRunLobby.cs:104). This side waits
    /// longer on purpose so the host's deadline is always the one that fires first and the log names the side that
    /// actually gave up. The old comment here claimed "the host gives us ten seconds" while the constant was 15 s —
    /// the two numbers were never the same thing.
    /// </summary>
    private const int JoinResponseTimeoutMs = 15000;

    /// <summary>
    /// Safety cap for the host's wait for the load lobby to begin the run — not a service deadline.
    ///
    /// The run can only begin once every present player is ready (<c>LoadRunLobby.IsAboutToBeginGame</c>,
    /// LoadRunLobby.cs:353-364) and <see cref="RestartListener.ShouldAllowRunToBegin"/> may be sitting on the native
    /// "N players are missing" popup waiting for a human to click. A short deadline therefore turned "the user has
    /// not decided yet" into "the restart failed". The wait now ends early only for a reason that proves no begin can
    /// arrive (see <see cref="AwaitHostBeginAsync"/>); this cap only catches a genuine stall.
    /// </summary>
    private const int HostBeginTimeoutMs = 150000;

    /// <summary>
    /// How long the client waits for the host to begin the reloaded run.
    ///
    /// Strictly larger than <see cref="HostBeginTimeoutMs"/>, because the failure ordering is what breaks the room:
    /// when the client gives up first it disconnects (<see cref="RollbackClient"/> → <c>Disconnect(Quit)</c>), the
    /// host's <c>Players</c> drops to 1, <c>IsAboutToBeginGame()</c> becomes permanently false
    /// (LoadRunLobby.cs:359-362) and the host is guaranteed to fail too. One client timeout used to kill the whole
    /// room's restart.
    /// </summary>
    private const int ClientBeginTimeoutMs = 180000;

    /// <summary>How often the client says it is still waiting, so the log separates "waiting" from "stuck".</summary>
    private const int BeginWaitProgressIntervalMs = 10000;

    private const int MainMenuWaitFrames = 180;

    private static Shape? _shape;
    private static PanelShape? _panelShape;

    // ---- session teardown suppression (one call, one service) -------------------------------------------

    private static INetGameService? _suppressedService;
    private static bool _suppressDisconnect;

    // ---- host side ---------------------------------------------------------------------------------------

    private static bool _hostRestartInProgress;
    private static LoadRunLobby? _hostLobby;
    private static bool _hostBeginRequested;
    private static Task<bool>? _hostBeginTask;
    private static bool _hostAbandoned;

    /// <summary>
    /// Set when the load lobby asked whether it may begin and the answer was no for a reason that cannot change:
    /// the missing-player confirmation could not be shown, or the player declined it. <c>LoadRunLobby</c> un-readies
    /// the host on that answer (LoadRunLobby.cs:303-308), so <c>IsAboutToBeginGame()</c> can never become true again
    /// and no begin message will ever arrive — waiting for one is a guaranteed stall.
    /// </summary>
    private static bool _hostBeginRefused;

    // ---- client side -------------------------------------------------------------------------------------

    /// <summary>
    /// The client session whose reassemble handlers are registered, used only as a de-duplication key.
    ///
    /// It used to double as "the current session" for the handlers, which was a real defect: after a reconnect the
    /// slot points at the new session while the old session's handlers are still registered on the old one. The
    /// handlers no longer read this field — each one is bound to the session it was registered on (see
    /// <see cref="HandshakeSucceededPostfix"/>) — so a stale value can only cause a duplicate registration, never an
    /// answer sent on the wrong session.
    /// </summary>
    private static INetGameService? _handlersRegisteredFor;
    private static bool _clientRestartInProgress;
    private static TaskCompletionSource<ClientLoadJoinResponseMessage>? _loadResponse;
    private static LoadRunLobby? _clientLobby;
    private static bool _clientBeginRequested;
    private static Task<bool>? _clientBeginTask;
    private static ClientLoadJoinResponseMessage? _clientFallbackResponse;

    public string Id => "mid-run-restart";

    public bool Applies(in CompatSnapshot snapshot) => snapshot.RequiresPatch;

    public void Apply(Harmony harmony, in CompatSnapshot snapshot)
    {
        Assembly host = Reflect.FindAssembly(Entry.HostModAssemblyName)
            ?? throw new InvalidOperationException("Host mod assembly is not loaded.");

        _shape = Shape.Capture(host);

        // Entry point: the room management panel's own "restart" button calls this. Taking over the async stub
        // changes every existing entry point at once (no second button, no divergent behaviour), and skipping the
        // state machine means the mod's "tear the room down and reconnect everyone" flow never runs.
        Type runtimeType = Reflect.RequireType(host, RuntimeTypeName);
        MethodInfo startHostedRestart = Reflect.RequireInstanceMethod(runtimeType, "StartHostedRunRestartAsync");
        harmony.Patch(
            startHostedRestart,
            prefix: new HarmonyMethod(typeof(MidRunRestartFix), nameof(StartHostedRunRestartPrefix))
            {
                priority = Priority.First
            });

        // The only caller of that method is the room-management panel, and it writes our result into a Label that
        // this very flow has already destroyed. Replace its flow so the write cannot reach a freed Godot object.
        InstallPanelRestartFlowPatch(harmony, host, startHostedRestart);

        // Keeping the session alive across the teardown needs the same one-shot suppression the new-run button
        // uses; both sides call RunManager.CleanUp, so both services are covered.
        harmony.Patch(
            Reflect.RequireInstanceMethod(typeof(NetHostGameService), "Disconnect", typeof(NetError), typeof(bool)),
            prefix: new HarmonyMethod(typeof(MidRunRestartFix), nameof(HostDisconnectPrefix))
            {
                priority = Priority.First
            });

        harmony.Patch(
            Reflect.RequireInstanceMethod(typeof(NetClientGameService), "Disconnect", typeof(NetError), typeof(bool)),
            prefix: new HarmonyMethod(typeof(MidRunRestartFix), nameof(ClientDisconnectPrefix))
            {
                priority = Priority.First
            });

        harmony.Patch(
            Reflect.RequireInstanceMethod(
                typeof(NetClientGameService),
                "HandshakeSucceeded",
                typeof(ulong),
                typeof(PeerVersionInfo)),
            postfix: new HarmonyMethod(typeof(MidRunRestartFix), nameof(HandshakeSucceededPostfix))
            {
                priority = Priority.First
            });

        Entry.Info(
            "mid-run-restart: restart entry patched (same-session floor reload), session teardown kept alive, " +
            "client reassemble handlers registered.");
    }

    // =====================================================================================================
    // Room-management panel (host mod UI)
    // =====================================================================================================

    /// <summary>
    /// Takes over <c>LanConnectRoomManagementPanel.StartRestartFlowAsync</c> — the handler behind the host mod's own
    /// "restart" button — so its failure text cannot be written into a destroyed node.
    ///
    /// Upstream awaits <c>StartHostedRunRestartAsync()</c> and, when that returns false, writes
    /// <c>_statusLabel.Text</c> after checking only the C# reference for null
    /// (LanConnectRoomManagementPanel.cs:646, written at :648). But the restart flow itself ends with
    /// <c>NGame.ReturnToMainMenu</c> — this patch's path, and the host mod's at LanConnectLobbyRuntime.cs:2060 — which
    /// frees the panel (:661-666). Touching a member of a freed Godot object throws
    /// <c>ObjectDisposedException</c>, so the one message the player was supposed to see never appeared (field log
    /// 2026-11-13 PC 1803). The very same method's `finally` already guards the button with
    /// <c>GodotObject.IsInstanceValid</c> (:654); only the label write forgot it.
    ///
    /// This is the only patch here that touches a *UI* type, so it is deliberately optional: if the type or one of
    /// its private fields is renamed, the repair must still install and only this takeover is skipped.
    /// </summary>
    private static void InstallPanelRestartFlowPatch(
        Harmony harmony,
        Assembly host,
        MethodInfo startHostedRestart)
    {
        try
        {
            Type? panelType = host.GetType(PanelTypeName, throwOnError: false, ignoreCase: false);
            if (panelType == null)
            {
                Entry.Warn(
                    $"mid-run-restart: {PanelTypeName} is not present in the host mod, so its restart flow keeps its " +
                    "own implementation (a failed restart may then log ObjectDisposedException instead of showing " +
                    "the failure text).");
                return;
            }

            MethodInfo flow = Reflect.RequireMethodByName(panelType, "StartRestartFlowAsync", 1);
            if (flow.ReturnType != typeof(Task))
            {
                throw new MissingMemberException(
                    panelType.FullName,
                    $"StartRestartFlowAsync returns {flow.ReturnType.Name}, expected Task");
            }

            _panelShape = PanelShape.Capture(panelType, startHostedRestart);
            harmony.Patch(
                flow,
                prefix: new HarmonyMethod(typeof(MidRunRestartFix), nameof(StartRestartFlowPrefix))
                {
                    priority = Priority.First
                });

            Entry.Info(
                "mid-run-restart: room-management panel restart flow taken over; its status label is only written " +
                "while the node is still alive.");
        }
        catch (Exception exception)
        {
            Entry.Warn(
                "mid-run-restart: could not take over the room panel's restart flow; the host mod keeps its own " +
                $"implementation ({exception.GetType().Name}: {exception.Message}).");
        }
    }

    /// <summary>
    /// Harmony prefix for the panel flow. Replaces it wholesale (<c>return false</c>) with
    /// <see cref="RunPanelRestartFlowAsync"/>, which awaits the very same <c>StartHostedRunRestartAsync</c> call the
    /// original makes — so the takeover in <see cref="Apply"/> still decides what that call does.
    /// </summary>
    private static bool StartRestartFlowPrefix(object __instance, object __0, ref Task __result)
    {
        PanelShape? panel = _panelShape;
        if (panel == null)
        {
            return true;
        }

        __result = RunPanelRestartFlowAsync(__instance, __0, panel);
        return false;
    }

    /// <summary>
    /// The replacement body of <c>LanConnectRoomManagementPanel.StartRestartFlowAsync</c>
    /// (LanConnectRoomManagementPanel.cs:642-659): await the restart, write the failure text only into a live label,
    /// then always clear the in-flight flag and re-enable the button exactly as the original's `finally` does.
    ///
    /// Skipping the label write loses nothing: every false return either showed this patch's <c>NErrorPopup</c> or the
    /// host mod's <c>LanConnectPopupUtil.ShowInfo</c> (LanConnectLobbyRuntime.cs:2012/2017/2022/2027/2068), so the
    /// player is told either way.
    /// </summary>
    private static async Task RunPanelRestartFlowAsync(object panel, object? runtime, PanelShape shape)
    {
        bool started = false;

        try
        {
            started = await InvokeHostRestartAsync(runtime, shape);
            if (!started)
            {
                WritePanelStatus(panel, shape, "重开启动失败，请查看提示信息。");
            }
        }
        catch (Exception exception)
        {
            // MethodInfo.Invoke wraps whatever the target throws; report the real cause, not the wrapper.
            Exception root = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
            Entry.Error(
                $"mid-run-restart: the room panel's restart flow failed: {root.GetType().Name}: {root.Message}");
            WritePanelStatus(panel, shape, "重开启动失败，请查看提示信息。");
        }
        finally
        {
            ResetPanelRestartFlow(panel, shape);

            // The panel is a CanvasLayer overlay, not part of the run or the pause menu, so returning to the main
            // menu does not free it — it survives the whole reload and keeps sitting on top of the freshly loaded
            // run, still saying "正在准备重开并通知队友...". A successful restart therefore has to dismiss it, and
            // the panel's own "关闭" button is the exact path to use (ClosePanel, LanConnectRoomManagementPanel.cs:661).
            // Done after ResetPanelRestartFlow so the flag/button writes above still land on a live node.
            if (started)
            {
                ClosePanel(panel, shape);
            }
        }
    }

    /// <summary>
    /// Dismisses the room-management panel the way its own "关闭" button does, but only while the node is alive.
    /// </summary>
    private static void ClosePanel(object panel, PanelShape shape)
    {
        try
        {
            if (panel is not GodotObject node || !GodotObject.IsInstanceValid(node))
            {
                return;
            }

            shape.ClosePanel.Invoke(panel, null);
            Entry.Info("mid-run-restart: the restart succeeded, so the room-management panel was closed.");
        }
        catch (Exception exception)
        {
            Entry.Warn(
                $"mid-run-restart: the restart succeeded but the room-management panel could not be closed: " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>
    /// Calls <c>LanConnectLobbyRuntime.StartHostedRunRestartAsync()</c> and waits for the real answer.
    ///
    /// The call goes through the Harmony-patched <see cref="MethodInfo"/>, so it re-enters
    /// <see cref="StartHostedRunRestartPrefix"/> and honours whatever that decided. Awaiting the returned task — not
    /// the <c>Invoke</c> call — is what makes the bool meaningful: <c>Invoke</c> only hands back the task, so reading
    /// its result eagerly would report success before anything had run.
    /// </summary>
    private static async Task<bool> InvokeHostRestartAsync(object? runtime, PanelShape shape)
    {
        if (runtime == null)
        {
            Entry.Warn("mid-run-restart: the room panel called the restart flow without a runtime; reporting failure.");
            return false;
        }

        object? pending = shape.HostRestart.Invoke(runtime, null);
        if (pending is Task<bool> typed)
        {
            return await typed;
        }

        if (pending is Task plain)
        {
            await plain;
            return true;
        }

        Entry.Warn(
            $"mid-run-restart: {shape.HostRestart.Name} returned {pending?.GetType().Name ?? "null"}, not a Task; " +
            "treating it as a failure.");
        return false;
    }

    /// <summary>
    /// Writes the panel's status label, but only while the node is still alive.
    ///
    /// <c>GodotObject.IsInstanceValid</c> is the only check that answers "can I still touch this?" — a non-null C#
    /// reference to a freed node still throws <c>ObjectDisposedException</c> on any member access.
    /// </summary>
    private static void WritePanelStatus(object panel, PanelShape shape, string text)
    {
        try
        {
            object? label = shape.StatusLabel.GetValue(panel);
            if (label is not GodotObject node || !GodotObject.IsInstanceValid(node))
            {
                Entry.Warn(
                    "mid-run-restart: the room panel's status label is already gone, so the failure text was not " +
                    "written into it (the player was told through the popup instead).");
                return;
            }

            // PanelShape.Capture refuses to build a shape without a writable Label.Text, so this cannot be the
            // reason the message is lost.
            shape.LabelText.SetValue(label, text);
        }
        catch (Exception exception)
        {
            Entry.Warn(
                $"mid-run-restart: could not write the room panel status label: " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>
    /// The original's `finally` (LanConnectRoomManagementPanel.cs:651-658): clear the in-flight flag and re-enable the
    /// restart button. A missed flag reset would leave the panel permanently refusing to restart, so that one is
    /// reported as an error instead of swallowed.
    /// </summary>
    private static void ResetPanelRestartFlow(object panel, PanelShape shape)
    {
        try
        {
            shape.RestartInFlight.SetValue(panel, false);
        }
        catch (Exception exception)
        {
            Entry.Error(
                "mid-run-restart: could not clear the room panel's in-flight flag, so its restart button may stay " +
                $"disabled until the panel is reopened: {exception.GetType().Name}: {exception.Message}");
        }

        try
        {
            object? button = shape.RestartButton.GetValue(panel);
            if (button is GodotObject node && GodotObject.IsInstanceValid(node) && shape.ButtonDisabled != null)
            {
                shape.ButtonDisabled.SetValue(button, false);
            }
        }
        catch (Exception exception)
        {
            Entry.Warn(
                $"mid-run-restart: could not re-enable the room panel's restart button: " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    // =====================================================================================================
    // Entry point
    // =====================================================================================================

    /// <summary>
    /// Takes over only for the case this repair can actually handle: we are the host, a multiplayer run is in
    /// progress, and the multiplayer save exists. Everything else (singleplayer, no save, already in the menu)
    /// returns true so the host mod's own flow keeps working exactly as before.
    /// </summary>
    private static bool StartHostedRunRestartPrefix(object __instance, ref Task<bool> __result)
    {
        if (!ShouldTakeOver())
        {
            return true;
        }

        __result = StartHostRestartAsync();
        return false;
    }

    private static bool ShouldTakeOver()
    {
        RunManager manager = RunManager.Instance;
        if (!manager.IsInProgress)
        {
            return false;
        }

        if (manager.NetService is not NetHostGameService host || !host.IsConnected)
        {
            return false;
        }

        if (NGame.Instance == null)
        {
            return false;
        }

        if (!MultiplayerSaveExists())
        {
            return false;
        }

        // Nobody to bring back: the same-session path cannot do anything the host mod's own flow does not, so leave
        // that flow in charge.
        if (CollectPatchedPeers(host).Count == 0)
        {
            Entry.Info("mid-run-restart: no connected peer runs this patch; leaving the host mod flow in charge.");
            return false;
        }

        return true;
    }

    private static bool MultiplayerSaveExists()
    {
        try
        {
            string path = ProjectSettings.GlobalizePath(
                SaveManager.Instance.GetProfileScopedPath(Path.Combine("saves", "current_run_mp.save")));
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        }
        catch (Exception exception)
        {
            Entry.Warn($"mid-run-restart: could not probe the multiplayer save: {exception.Message}");
            return false;
        }
    }

    // =====================================================================================================
    // Host side
    // =====================================================================================================

    private static async Task<bool> StartHostRestartAsync()
    {
        if (_hostRestartInProgress)
        {
            Entry.Warn("mid-run-restart: a restart is already in progress; ignoring the request.");
            return false;
        }

        _hostRestartInProgress = true;
        SerializableRun? save = null;
        NetHostGameService? host = null;
        List<ulong> patchedPeers = new();

        try
        {
            Shape shape = _shape ?? throw new InvalidOperationException("mid-run-restart: shape was not captured.");

            // The save is only ever read here. Nothing in this file deletes the save or the room binding.
            if (!TryLoadSave(shape, out save, out string failureReason) || save == null)
            {
                Entry.Error($"mid-run-restart: cannot read the multiplayer save ({failureReason}); aborting.");
                ShowInfo("读取多人存档失败，已取消重开。");
                return false;
            }

            host = (NetHostGameService)RunManager.Instance.NetService;
            patchedPeers = CollectPatchedPeers(host);

            string saveKey = TryBuildSaveKey(shape, save) ?? "<unknown>";
            Entry.Info(
                $"mid-run-restart: begin saveKey={saveKey} players={save.Players.Count} " +
                $"patchedPeers={patchedPeers.Count}");

            // Precondition, checked before anything is torn down: without the tail binding the clients cannot
            // receive our LoadJoinResponse extension frame, and the whole room would sit on a 10/15-second timeout
            // for a reason nothing in the log would name.
            TailBindingState hostTail = ProbeTailBinding(host);
            Entry.Info(
                $"mid-run-restart: tail binding (host) bound={hostTail.Bound} " +
                $"writerBindings={hostTail.WriterBindings} bus={hostTail.Bus}");
            if (!hostTail.Bound)
            {
                Entry.Error(
                    "mid-run-restart: the host session has no tail_v1 binding, so the extension frames cannot be " +
                    "sent; aborting before the run is touched.");
                ShowInfo("联机协议未就绪，已取消重开。请重试；若反复失败，请退出房间后重新建房。");
                return false;
            }

            _hostBeginRequested = false;
            _hostBeginRefused = false;
            await TeardownToMainMenuAsync(host);
            NMainMenu menu = await WaitForMainMenuAsync();

            // Rule: never stack lobby handlers on the same NetService — a second restart must not receive
            // duplicates from the previous attempt.
            CleanUpHostLobby();

            LoadRunLobby lobby = new(host, new RestartListener(isHost: true), save);
            _hostLobby = lobby;
            lobby.AddLocalHostPlayer();
            Entry.Info($"mid-run-restart: lobby rebuilt on the existing session (players={lobby.PlayerCount})");

            // Zero-confirmation path: the host readies itself, every returning client readies itself as it joins,
            // and LoadRunLobby begins the run once everybody is ready.
            lobby.SetReady(true);

            int admitted = 0;
            foreach (ulong peerId in patchedPeers)
            {
                if (AdmitPeer(shape, lobby, peerId))
                {
                    admitted++;
                }
            }

            Entry.Info(
                $"mid-run-restart: admitted {admitted}/{patchedPeers.Count} patched peer(s) into the load lobby.");
            if (admitted == 0)
            {
                throw new InvalidOperationException(
                    "none of the returning players is still connected, so there is nobody to reload the floor for.");
            }

            // The main menu does not pump the net service (only the run, the lobby screens and the menu screens do),
            // so the host keeps it alive itself until every client has joined, readied and the run begins.
            await AwaitHostBeginAsync(host);
            return true;
        }
        catch (Exception exception)
        {
            Entry.Error($"mid-run-restart: host restart failed: {exception.GetType().Name}: {exception.Message}");
            FallbackToNativeScreen(save, host, patchedPeers);
            return false;
        }
        finally
        {
            _hostRestartInProgress = false;
            _hostBeginTask = null;
            _hostAbandoned = false;
            _hostBeginRefused = false;
        }
    }

    /// <summary>
    /// Last resort: if the zero-confirmation path could not be set up, fall back to the game's own continue
    /// screen so the host can still press "confirm" and resume manually.
    /// </summary>
    private static void FallbackToNativeScreen(
        SerializableRun? save,
        NetHostGameService? host,
        List<ulong> patchedPeers)
    {
        try
        {
            CleanUpHostLobby();

            NMainMenu? menu = NGame.Instance?.MainMenu;
            if (save == null || host == null || menu == null || !host.IsConnected)
            {
                bool stillInRun = RunManager.Instance.IsInProgress;
                Entry.Error($"mid-run-restart: fallback unavailable (stillInRun={stillInRun}); nothing was changed.");
                ShowInfo(stillInRun ? "重开失败，本局界面已保留。" : "重开失败，已退出本局。请重新加入房间。");
                return;
            }

            Entry.Warn("mid-run-restart: fallback to native confirm screen");
            NMultiplayerLoadGameScreen screen = menu.SubmenuStack.GetSubmenuType<NMultiplayerLoadGameScreen>();
            screen.InitializeAsHost(host, save);
            menu.SubmenuStack.Push(screen);

            LoadRunLobby? nativeLobby = FindLoadLobby(screen);
            if (nativeLobby != null)
            {
                Shape shape = _shape ?? throw new InvalidOperationException("shape was not captured.");
                foreach (ulong peerId in patchedPeers)
                {
                    AdmitPeer(shape, nativeLobby, peerId);
                }
            }

            ShowInfo("自动重开未能完成，已切换到确认界面，请手动确认继续。");
        }
        catch (Exception exception)
        {
            // MethodInfo.Invoke wraps anything the target throws; report the real cause, not the wrapper.
            Exception root = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
            Entry.Error($"mid-run-restart: fallback failed: {root.GetType().Name}: {root.Message}");

            // At this point there is no run, no lobby and no confirmation screen, yet the session (and the room's
            // frozen protocol lease) is still alive. Leaving it up is what makes the next "load saved run" fail with
            // "Protocol selection is already owned" (field log 2026-11-13 1654): the lease is only released when the
            // session ends. Close the room so the host is left in a state it can actually recover from.
            CloseHostSession(host, "the native confirm screen could not be shown");
            ShowInfo("重开失败，房间已关闭。请重新建房或加入房间。");
        }
    }

    /// <summary>
    /// Ends the host session when the restart left nothing usable behind, so the room and its protocol lease are
    /// released instead of pinning the player inside a session with no run and no lobby.
    /// </summary>
    private static void CloseHostSession(NetHostGameService? host, string why)
    {
        if (host == null || !host.IsConnected)
        {
            return;
        }

        try
        {
            host.Disconnect(NetError.InternalError);
            Entry.Warn($"mid-run-restart: closed the host session because {why}.");
        }
        catch (Exception exception)
        {
            Entry.Warn($"mid-run-restart: could not close the host session: {exception.Message}");
        }
    }

    // =====================================================================================================
    // Client side
    // =====================================================================================================

    private static void HandshakeSucceededPostfix(NetClientGameService __instance, ulong peerId, PeerVersionInfo versionInfo)
    {
        try
        {
            if (ReferenceEquals(_handlersRegisteredFor, __instance))
            {
                return;
            }

            // Bind the handler to the session that owns it instead of letting it read the static slot back.
            //
            // The slot is a single field that is also the de-duplication key, so after a reconnect it points at the
            // new session while the previous session's handlers are still registered on the previous session: a
            // message arriving there used to be answered on the *new* session.
            // Capturing the instance removes the shared mutable state — the handler can only ever answer on the
            // session that received the message. The load-response handler never read the slot, so it stays as-is.
            NetClientGameService session = __instance;
            __instance.RegisterMessageHandler<InitialGameInfoMessage>(
                (message, sender) => OnInitialGameInfoMessage(session, message, sender));
            __instance.RegisterMessageHandler<ClientLoadJoinResponseMessage>(OnLoadJoinResponseMessage);
            _handlersRegisteredFor = __instance;
            Entry.Info(
                $"mid-run-restart: client reassemble handlers registered on the session (peerId={peerId}, " +
                $"session={session.NetId}).");
        }
        catch (Exception exception)
        {
            Entry.Warn($"mid-run-restart: could not register client handlers: {exception.Message}");
        }
    }

    /// <summary>
    /// Handles the host's "we are in a load lobby" announcement.
    ///
    /// <paramref name="service"/> is the session this handler was registered on — passed in, never read back from a
    /// static field, so the answer always goes out on the session that actually received the message.
    /// </summary>
    private static void OnInitialGameInfoMessage(
        NetClientGameService service,
        InitialGameInfoMessage message,
        ulong senderId)
    {
        try
        {
            // The host's load lobby announces itself with InLoadedLobby; anything else belongs to other flows
            // (a normal join uses the vanilla JoinFlow, the new-run button uses InLobby).
            if (message.sessionState != RunSessionState.InLoadedLobby)
            {
                return;
            }

            if (!ShouldAcceptClientRestart())
            {
                Entry.Info(
                    $"mid-run-restart: ignoring InitialGameInfoMessage from {senderId} (not a mid-run restart).");
                return;
            }

            // Precondition: our LoadJoinRequest extension frame can only be produced while the tail runtime is
            // bound. Sending the request anyway would make the host hold it for 2000 ms and then disconnect us with
            // ModMismatch, and the log would never say why. Refuse up front instead.
            TailBindingState tail = ProbeTailBinding(service);
            if (!tail.Bound)
            {
                Entry.Error(
                    $"mid-run-restart: this client has no tail_v1 binding (writerBindings={tail.WriterBindings}, " +
                    $"bus={tail.Bus}), so the reload handshake cannot complete; refusing to send the join request " +
                    "instead of waiting for the timeout.");
                ShowInfo("联机协议未就绪，无法回到本层开头。请点「主菜单」后重新加入房间。");
                return;
            }

            _clientRestartInProgress = true;
            _loadResponse = new TaskCompletionSource<ClientLoadJoinResponseMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            // The reload reuses this session, so the roster authority still holds the StartRunLobby epoch from the
            // room we are leaving. Drop it first: the host is about to publish a LoadRunLobby epoch, and a
            // current-state comparison between the two carrier types cannot ever match (see TailRoster).
            // Non-fatal by design: a failed reset only costs us the informative warning, never the restart.
            if (TailRoster.Reset(service, "before the reload join request"))
            {
                Entry.Info(
                    "mid-run-restart: dropped the accepted Tail roster epoch on this session " +
                    "(before the reload join request).");
            }

            // Answer before tearing anything down. The host's own deadline for this request is
            // ClientResponseTimeout = 10000 ms (LoadRunLobby.cs:81, same default in StartRunLobby.cs:104). We send
            // immediately, and the wait that follows is our own, longer one (JoinResponseTimeoutMs), so the host's
            // deadline is the one that fires first.
            service.SendMessage<ClientLoadJoinRequestMessage>(default);
            Entry.Info(
                $"mid-run-restart: host asked to reload the current floor; join request sent " +
                $"(tailBound=True, writerBindings={tail.WriterBindings}).");
            TaskHelper.RunSafely(ClientRestartAsync(service));
        }
        catch (Exception exception)
        {
            Entry.Error($"mid-run-restart: failed to handle the reload request: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void OnLoadJoinResponseMessage(ClientLoadJoinResponseMessage message, ulong senderId)
    {
        if (!_clientRestartInProgress)
        {
            Entry.Info(
                $"mid-run-restart: ignoring ClientLoadJoinResponseMessage from {senderId} (no restart pending).");
            return;
        }

        if (_clientLobby != null)
        {
            // We already built our lobby, so a second response means the host had to rebuild it (its automatic
            // path failed) and is showing the native confirm screen. Wake the waiter and mirror that screen
            // instead of waiting for a begin message that will never come.
            //
            // Completing _loadResponse here too is free: this branch is only reachable after the first response
            // completed that very TaskCompletionSource (the lobby is built immediately after
            // AwaitLoadResponseAsync returns it), so TrySetResult reports false and changes nothing. It is kept
            // because "a response arrived" and "the lobby exists" must not be allowed to drift apart — if the order
            // ever changes, the waiter is still released by the response instead of sitting out the whole timeout.
            Entry.Warn(
                $"mid-run-restart: host rebuilt the load lobby (response from {senderId}); switching to the native " +
                "confirm screen.");
            _clientFallbackResponse = message;
            _clientBeginRequested = true;
            _loadResponse?.TrySetResult(message);
            return;
        }

        _loadResponse?.TrySetResult(message);
    }

    private static bool ShouldAcceptClientRestart()
    {
        if (_clientRestartInProgress)
        {
            return false;
        }

        RunManager manager = RunManager.Instance;
        if (!manager.IsInProgress || manager.HasRunEnded)
        {
            // Not mid-run (or the run is already over — that is the new-run button's territory).
            return false;
        }

        return manager.NetService?.Type == NetGameType.Client;
    }

    private static async Task ClientRestartAsync(INetGameService service)
    {
        try
        {
            await TeardownToMainMenuAsync(service);

            ClientLoadJoinResponseMessage response = await AwaitLoadResponseAsync(service);
            NMainMenu menu = await WaitForMainMenuAsync();

            // A second response means the host already had to rebuild the lobby (its auto path failed) and is
            // falling back to the native confirm screen. Mirror that instead of auto-readying.
            bool reAdmission = _clientLobby != null;
            CleanUpClientLobby();

            if (reAdmission)
            {
                Entry.Warn("mid-run-restart: fallback to native confirm screen (client)");
                PushNativeClientScreen(menu, service, response);
                return;
            }

            LoadRunLobby lobby = new(service, new RestartListener(isHost: false), response);
            _clientLobby = lobby;
            _clientBeginRequested = false;
            Entry.Info($"mid-run-restart: lobby rebuilt on the existing session (client, players={lobby.PlayerCount})");

            lobby.SetReady(true);
            await AwaitBeginAsync(service);

            if (_clientFallbackResponse is { } fallback)
            {
                _clientFallbackResponse = null;
                CleanUpClientLobby();
                PushNativeClientScreen(menu, service, fallback);
            }
        }
        catch (Exception exception)
        {
            Entry.Error($"mid-run-restart: client restart failed: {exception.GetType().Name}: {exception.Message}");
            // A rolled-back restart that says nothing looks exactly like "the button did nothing" — the player is
            // dumped back to the menu with no idea why. Always name the reason on screen.
            ShowInfo(
                exception is TimeoutException
                    ? "回到本层开头失败：房主没有回应。请点「主菜单」后重新加入房间。"
                    : $"回到本层开头失败：{exception.GetType().Name}。请点「主菜单」后重新加入房间。");
            RollbackClient(service);
        }
        finally
        {
            _clientRestartInProgress = false;
            _loadResponse = null;
            _clientBeginRequested = false;
            _clientBeginTask = null;
            _clientFallbackResponse = null;
        }
    }

    private static void PushNativeClientScreen(
        NMainMenu menu,
        INetGameService service,
        ClientLoadJoinResponseMessage response)
    {
        NMultiplayerLoadGameScreen screen = menu.SubmenuStack.GetSubmenuType<NMultiplayerLoadGameScreen>();
        screen.InitializeAsClient(service, response);
        menu.SubmenuStack.Push(screen);
    }

    private static async Task<ClientLoadJoinResponseMessage> AwaitLoadResponseAsync(INetGameService service)
    {
        TaskCompletionSource<ClientLoadJoinResponseMessage> completion = _loadResponse
            ?? throw new InvalidOperationException("mid-run-restart: no load response is pending.");
        Task<ClientLoadJoinResponseMessage> pending = completion.Task;
        SceneTree? tree = Engine.GetMainLoop() as SceneTree;
        long deadline = System.Environment.TickCount64 + JoinResponseTimeoutMs;

        while (!pending.IsCompleted)
        {
            if (System.Environment.TickCount64 > deadline)
            {
                throw new TimeoutException(
                    $"the host did not answer the reload join request within {JoinResponseTimeoutMs} ms " +
                    $"({DescribeTailBinding(service)}). " + TailErrorPathFix.DescribeLastRejection());
            }

            Pump(service, tree);
            await NextFrameAsync(tree);
        }

        return await pending;
    }

    /// <summary>
    /// Waits for the host's load lobby to begin the run.
    ///
    /// There is no service deadline here on purpose. <c>LoadRunLobby</c> only asks
    /// <see cref="RestartListener.ShouldAllowRunToBegin"/> once every present player is ready
    /// (LoadRunLobby.cs:353-372), and that callback may be sitting on the native "N players are missing" popup
    /// waiting for a human to click. A fixed deadline therefore declared the restart failed while the user was still
    /// deciding. The wait ends early only for a reason that proves no begin can arrive:
    ///   (a) every returning peer left (<c>_hostAbandoned</c>) — with one player in the room
    ///       <c>IsAboutToBeginGame()</c> is permanently false (LoadRunLobby.cs:359-362);
    ///   (b) our lobby is gone (<c>_hostLobby</c> null) or refused to begin (<c>_hostBeginRefused</c>);
    ///   (c) the safety cap <see cref="HostBeginTimeoutMs"/> elapsed — a stall worth reporting, not a normal outcome.
    /// </summary>
    private static async Task AwaitHostBeginAsync(INetGameService service)
    {
        SceneTree? tree = Engine.GetMainLoop() as SceneTree;
        long started = System.Environment.TickCount64;
        long deadline = started + HostBeginTimeoutMs;

        while (!_hostBeginRequested)
        {
            if (_hostAbandoned)
            {
                throw new InvalidOperationException(
                    "every returning player left the reload lobby before the floor could be reloaded.");
            }

            if (_hostLobby == null)
            {
                throw new InvalidOperationException(
                    "the reload lobby was torn down before it could begin the run.");
            }

            if (_hostBeginRefused)
            {
                throw new InvalidOperationException(
                    "the reload lobby was not allowed to begin — the missing-player confirmation could not be shown " +
                    "or was declined — so this run can never start from here.");
            }

            if (System.Environment.TickCount64 > deadline)
            {
                throw new TimeoutException(
                    $"the reload lobby did not begin the run within {HostBeginTimeoutMs} ms " +
                    $"(players={_hostLobby.PlayerCount}, " +
                    $"elapsed={(System.Environment.TickCount64 - started) / 1000}s); this is the safety cap, not a " +
                    "service deadline.");
            }

            Pump(service, tree);
            await NextFrameAsync(tree);
        }

        if (_hostBeginTask is { } begin && !await begin)
        {
            throw new InvalidOperationException(
                "the reload lobby reported a begin, but rebuilding the run from the save failed.");
        }
    }

    /// <summary>
    /// Waits for the host to begin the reloaded run.
    ///
    /// The budget is <see cref="ClientBeginTimeoutMs"/>, which is strictly larger than the host's own
    /// <see cref="HostBeginTimeoutMs"/>: a client that gives up first disconnects, which removes the player the host
    /// is waiting for and makes the host's <c>IsAboutToBeginGame()</c> permanently false
    /// (LoadRunLobby.cs:359-362). The client must therefore never be the side that decides.
    ///
    /// A host-side fallback (<c>_clientFallbackResponse</c>) also ends the wait: the host rebuilt the lobby and is
    /// showing its native confirm screen, so no begin message is coming and the caller mirrors that screen.
    /// </summary>
    private static async Task AwaitBeginAsync(INetGameService service)
    {
        SceneTree? tree = Engine.GetMainLoop() as SceneTree;
        long started = System.Environment.TickCount64;
        long deadline = started + ClientBeginTimeoutMs;
        long nextProgress = started + BeginWaitProgressIntervalMs;

        while (!_clientBeginRequested && _clientFallbackResponse == null)
        {
            long now = System.Environment.TickCount64;
            if (now > deadline)
            {
                throw new TimeoutException(
                    $"the host did not begin the reloaded run within {ClientBeginTimeoutMs} ms " +
                    $"(elapsed={(now - started) / 1000}s, {DescribeTailBinding(service)}).");
            }

            if (now >= nextProgress)
            {
                nextProgress = now + BeginWaitProgressIntervalMs;
                Entry.Info(
                    $"mid-run-restart: still waiting for the host to begin the reloaded run " +
                    $"(elapsed={(now - started) / 1000}s, budget={ClientBeginTimeoutMs / 1000}s).");
            }

            Pump(service, tree);
            await NextFrameAsync(tree);
        }

        if (_clientBeginTask is { } begin && !await begin)
        {
            throw new InvalidOperationException(
                "the reload lobby reported a begin, but rebuilding the run from the save failed.");
        }
    }

    private static void Pump(INetGameService service, SceneTree? tree)
    {
        try
        {
            // Nobody else pumps the session while we sit on the main menu.
            service.Update();
        }
        catch (Exception exception)
        {
            Entry.Warn($"mid-run-restart: session update failed while waiting: {exception.Message}");
        }
    }

    private static async Task NextFrameAsync(SceneTree? tree)
    {
        if (tree != null)
        {
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
        else
        {
            await Task.Delay(16);
        }
    }

    private static void RollbackClient(INetGameService service)
    {
        CleanUpClientLobby();

        try
        {
            if (service.IsConnected)
            {
                service.Disconnect(NetError.Quit);
            }
        }
        catch (Exception exception)
        {
            Entry.Warn($"mid-run-restart: client rollback disconnect failed: {exception.Message}");
        }

        ShowInfo("回到本层开头失败，已退出本局。请重新加入房间。");
    }

    // =====================================================================================================
    // Beginning the loaded run (host and client)
    // =====================================================================================================

    /// <summary>
    /// Mirrors <c>NMultiplayerLoadGameScreen.StartRun</c> (NMultiplayerLoadGameScreen.cs:413-434): themed fade,
    /// rebuild the run from the save, load it, then release the load lobby's message handlers while keeping the
    /// session.
    /// </summary>
    private static async Task<bool> BeginRunAsync(bool isHost)
    {
        LoadRunLobby? lobby = isHost ? _hostLobby : _clientLobby;
        INetGameService? service = lobby?.NetService;

        try
        {
            if (lobby == null)
            {
                throw new InvalidOperationException("the load lobby is gone.");
            }

            NGame game = NGame.Instance ?? throw new InvalidOperationException("NGame instance is unavailable.");
            Entry.Info("mid-run-restart: begin run for all players");

            SerializablePlayer? local = lobby.Run.Players
                .FirstOrDefault(player => player.NetId == lobby.NetService.NetId);
            CharacterModel? character = local?.CharacterId is { } characterId
                ? ModelDb.GetById<CharacterModel>(characterId)
                : null;

            if (character != null)
            {
                SfxCmd.Play(character.CharacterTransitionSfx);
                await game.Transition.FadeOut(0.8f, character.CharacterSelectTransitionPath);
            }
            else
            {
                Entry.Warn("mid-run-restart: the local character is unknown; using the default transition.");
                await game.Transition.FadeOut(0.8f);
            }

            RunState state = RunState.FromSerializable(lobby.Run);
            await RunManager.Instance.SetUpSavedMultiplayer(state, lobby);
            await game.LoadRun(state, lobby.Run.PreFinishedRoom);

            if (isHost)
            {
                CleanUpHostLobby();
            }
            else
            {
                CleanUpClientLobby();
            }

            await game.Transition.FadeIn();
            return true;
        }
        catch (Exception exception)
        {
            Entry.Error($"mid-run-restart: begin run failed: {exception.GetType().Name}: {exception.Message}");

            if (isHost)
            {
                CleanUpHostLobby();
            }
            else
            {
                CleanUpClientLobby();
            }

            try
            {
                if (service != null && service.IsConnected)
                {
                    service.Disconnect(NetError.InternalError);
                }
            }
            catch (Exception disconnectException)
            {
                Entry.Warn($"mid-run-restart: rollback disconnect failed: {disconnectException.Message}");
            }

            ShowInfo("回到本层开头失败，已退出本局。请重新加入房间。");
            return false;
        }
    }

    // =====================================================================================================
    // Listener
    // =====================================================================================================

    /// <summary>
    /// Drives <see cref="LoadRunLobby"/> without any screen: logs the reassembly, readies automatically (which is
    /// what removes the confirmation step) and only asks the host when a saved player is missing.
    /// </summary>
    private sealed class RestartListener : ILoadRunLobbyListener
    {
        private readonly bool _isHost;

        internal RestartListener(bool isHost)
        {
            _isHost = isHost;
        }

        public void PlayerConnected(LoadRunLobbyPlayer player)
        {
            Entry.Info($"mid-run-restart: player connected id={player.id} host={_isHost}");
        }

        public void RemotePlayerDisconnected(ulong playerId)
        {
            Entry.Warn($"mid-run-restart: player left the load lobby id={playerId}");

            // The host would otherwise sit on its safety cap waiting for a "begin" that cannot happen:
            // LoadRunLobby.IsAboutToBeginGame() is permanently false while the room holds a single player
            // (LoadRunLobby.cs:359-362). Abort the wait instead of burning the whole budget for nothing.
            if (_isHost && _hostLobby is { } lobby && lobby.PlayerCount <= 1)
            {
                _hostAbandoned = true;
            }
        }

        public void PlayerReadyChanged(ulong playerId)
        {
            LoadRunLobby? lobby = _isHost ? _hostLobby : _clientLobby;
            if (lobby != null && lobby.IsAboutToBeginGame())
            {
                Entry.Info($"mid-run-restart: auto-ready all peers (n={lobby.PlayerCount})");
            }
        }

        public async Task<bool> ShouldAllowRunToBegin()
        {
            LoadRunLobby? lobby = _hostLobby;
            if (lobby == null)
            {
                return RefuseBegin("the load lobby is already gone");
            }

            int expected = lobby.Run.Players.Count;
            if (lobby.PlayerCount >= expected)
            {
                return true;
            }

            // Someone from the save did not come back. This is the one confirmation we keep: starting without them
            // leaves their character uncontrolled. Same popup the game's own continue screen uses
            // (NMultiplayerLoadGameScreen.cs:400-411).
            int missing = expected - lobby.PlayerCount;
            Entry.Warn(
                $"mid-run-restart: missing players present={lobby.PlayerCount} expected={expected}; " +
                "showing the native confirmation");

            LocString body = new("gameplay_ui", "CONFIRM_LOAD_SAVE.body");
            body.Add("MissingCount", missing);
            NGenericPopup? popup = NGenericPopup.Create();
            if (popup == null)
            {
                return RefuseBegin("the confirmation popup could not be created");
            }

            // The popup can only answer once it is on screen, so the container has to be proven usable *before* the
            // await. Three facts, each verified in the decompile:
            //   * NModalContainer.Instance is nullable (NModalContainer.cs:77);
            //   * Add drops the popup while another modal is open, logging a warning and returning
            //     (NModalContainer.cs:100-104);
            //   * WaitForConfirmation's task can only be completed by that popup's own buttons
            //     (NGenericPopup.cs:94-119).
            // So awaiting it without a live container is an await that never returns — and LoadRunLobby._isBeginningRun
            // is already true by then (LoadRunLobby.cs:302), which used to leave that lobby unable to ever begin.
            // Upstream has the same code without the null-conditional (NMultiplayerLoadGameScreen.cs:409) and would
            // throw immediately instead of hanging; refusing to begin is the quiet, recoverable equivalent:
            // LoadRunLobby clears the flag and un-readies the host (LoadRunLobby.cs:303-308) and the host side stops
            // waiting with a named reason instead of sitting out the budget.
            NModalContainer? container = NModalContainer.Instance;
            if (container == null)
            {
                Entry.Error(
                    "mid-run-restart: there is no NModalContainer, so the missing-player confirmation can never be " +
                    "shown and waiting for it would hang this lobby forever; refusing to begin the run.");
                popup.QueueFreeSafely();
                return RefuseBegin("the modal container does not exist");
            }

            if (container.OpenModal != null)
            {
                Entry.Error(
                    "mid-run-restart: another modal is already open, so NModalContainer.Add would drop this popup " +
                    "(NModalContainer.cs:100-104) and the confirmation would never answer; refusing to begin the run.");
                popup.QueueFreeSafely();
                return RefuseBegin("another modal is already open");
            }

            container.Add(popup);
            bool allowed = await popup.WaitForConfirmation(
                body,
                new LocString("gameplay_ui", "CONFIRM_LOAD_SAVE.header"),
                new LocString("gameplay_ui", "CONFIRM_LOAD_SAVE.cancel"),
                new LocString("gameplay_ui", "CONFIRM_LOAD_SAVE.confirm"));
            if (!allowed)
            {
                Entry.Warn(
                    "mid-run-restart: the missing-player confirmation was declined; the floor will not be reloaded.");
                RefuseBegin("the player declined to start with missing players");
            }

            return allowed;
        }

        /// <summary>
        /// Records that no begin message can ever arrive and answers "do not begin".
        ///
        /// The flag matters because returning false is not self-explanatory: <c>LoadRunLobby</c> reacts by un-readying
        /// the host (LoadRunLobby.cs:303-308), so <c>IsAboutToBeginGame()</c> is false from then on and the host side
        /// would otherwise wait out its entire budget for a begin that cannot come.
        /// </summary>
        private static bool RefuseBegin(string why)
        {
            Entry.Warn($"mid-run-restart: the load lobby may not begin ({why}); the restart will end with this reason.");
            _hostBeginRefused = true;
            return false;
        }

        public void BeginRun()
        {
            // Keep the task: the previous version signalled "begun" before the reload had actually happened, so a
            // swallowed failure inside BeginRunAsync still let the caller report success — the exact "the log says
            // it worked but nothing happened" shape this feature must never produce again.
            Task<bool> begin = BeginRunAsync(_isHost);
            if (_isHost)
            {
                _hostBeginRequested = true;
                _hostBeginTask = begin;
            }
            else
            {
                _clientBeginRequested = true;
                _clientBeginTask = begin;
            }
        }

        public void LocalPlayerDisconnected(NetErrorInfo info)
        {
            Entry.Warn($"mid-run-restart: disconnected during the restart ({info.GetReason()}).");
        }
    }

    // =====================================================================================================
    // Shared plumbing
    // =====================================================================================================

    private static bool TryLoadSave(Shape shape, out SerializableRun? save, out string failureReason)
    {
        object?[] arguments = { null, null };
        bool success = shape.TryLoadCurrentMultiplayerRun.Invoke(null, arguments) is true;
        save = arguments[0] as SerializableRun;
        failureReason = arguments[1] as string ?? string.Empty;
        return success && save != null;
    }

    private static string? TryBuildSaveKey(Shape shape, SerializableRun save)
    {
        try
        {
            return shape.BuildSaveKey.Invoke(null, new object?[] { save }) as string;
        }
        catch (Exception exception)
        {
            Entry.Warn($"mid-run-restart: could not build the save key: {exception.Message}");
            return null;
        }
    }

    private static List<ulong> CollectPatchedPeers(NetHostGameService host)
    {
        List<ulong> patched = new();
        foreach (var peer in host.ConnectedPeers)
        {
            PeerVersionInfo? info = host.GetVersionInfoForPeer(peer.peerId);
            if (info == null)
            {
                continue;
            }

            if (HasOurMod(info.Value))
            {
                patched.Add(peer.peerId);
            }
        }

        return patched;
    }

    private static bool HasOurMod(in PeerVersionInfo info) =>
        ContainsOurId(info.otherMods) || ContainsOurId(info.gameplayAffectingMods);

    private static bool ContainsOurId(List<string>? mods)
    {
        if (mods == null)
        {
            return false;
        }

        foreach (string entry in mods)
        {
            if (entry != null && entry.StartsWith(ModIdPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Feeds one returning peer into <see cref="LoadRunLobby"/> the way the transport would.
    ///
    /// Two things are load-bearing. First the peer must still be connected: the returning-peer list is captured
    /// before the run is torn down, so a client that failed on its own side has already left by the time we admit
    /// it. Admitting it anyway makes <c>LoadRunLobby.OnConnectedToClientAsHost</c> send to a closed connection,
    /// which surfaces as "Tried to send message to client ... but no client with that ID is connected", escalates
    /// into a native transport failure, and ends with <c>AbortActiveBinding</c> tearing the whole Tail binding down
    /// for every peer (field log 2026-11-13: 1714 → 1744 → 1779). Second, the call goes through
    /// <see cref="MethodInfo.Invoke(object?, object?[])"/>, so failures arrive wrapped in
    /// <see cref="TargetInvocationException"/>; unwrap it or the caller only ever sees the wrapper.
    /// </summary>
    private static bool AdmitPeer(Shape shape, LoadRunLobby lobby, ulong peerId)
    {
        if (lobby.NetService is NetHostGameService host && !IsPeerConnected(host, peerId))
        {
            Entry.Warn(
                $"mid-run-restart: peer {peerId} left before it could be admitted; skipping it instead of " +
                "sending to a closed connection.");
            return false;
        }

        try
        {
            shape.AdmitLoadPeer.Invoke(lobby, new object?[] { peerId });
            return true;
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            Entry.Warn(
                $"mid-run-restart: admitting peer {peerId} failed: " +
                $"{exception.InnerException.GetType().Name}: {exception.InnerException.Message}");
            return false;
        }
        catch (Exception exception)
        {
            Entry.Warn(
                $"mid-run-restart: admitting peer {peerId} failed: {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    private static bool IsPeerConnected(NetHostGameService host, ulong peerId)
    {
        foreach (NetClientData peer in host.ConnectedPeers)
        {
            if (peer.peerId == peerId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads the private <c>_runLobby</c> field of the native continue screen.
    ///
    /// This is the only place the patch reads a private field of a *game* type, so a missing field must be loud.
    /// <see cref="FallbackToNativeScreen"/> uses the result to admit the returning peers into that screen's lobby;
    /// returning null silently skipped the admission, so the screen came up with no players and nothing in the log
    /// said why. NMultiplayerLoadGameScreen has no public accessor (it assigns <c>_runLobby</c> at
    /// NMultiplayerLoadGameScreen.cs:314 and clears it at :393-398), so reflection stays.
    /// </summary>
    private static LoadRunLobby? FindLoadLobby(NSubmenu screen)
    {
        Type screenType = screen.GetType();
        FieldInfo? field = screenType.GetField("_runLobby", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null)
        {
            Entry.Error(
                $"mid-run-restart: {screenType.FullName} no longer has a private field '_runLobby' (renamed " +
                "upstream?), so the returning players cannot be admitted into the native confirm screen; that screen " +
                "will come up without them. This needs a patch update.");
            return null;
        }

        if (field.GetValue(screen) is not LoadRunLobby lobby)
        {
            Entry.Error(
                $"mid-run-restart: {screenType.FullName}._runLobby is null or not a LoadRunLobby, so the returning " +
                "players cannot be admitted into the native confirm screen; that screen will come up without them.");
            return null;
        }

        return lobby;
    }

    private static void CleanUpHostLobby()
    {
        LoadRunLobby? lobby = _hostLobby;
        _hostLobby = null;
        if (lobby == null)
        {
            return;
        }

        try
        {
            // disconnectSession: false — the whole point is to keep the session.
            lobby.CleanUp(disconnectSession: false);
        }
        catch (Exception exception)
        {
            Entry.Warn($"mid-run-restart: host lobby cleanup failed: {exception.Message}");
        }

        DisposeLobbyInputSynchronizer(lobby, "host");
    }

    private static void CleanUpClientLobby()
    {
        LoadRunLobby? lobby = _clientLobby;
        _clientLobby = null;
        if (lobby == null)
        {
            return;
        }

        try
        {
            lobby.CleanUp(disconnectSession: false);
        }
        catch (Exception exception)
        {
            Entry.Warn($"mid-run-restart: client lobby cleanup failed: {exception.Message}");
        }

        DisposeLobbyInputSynchronizer(lobby, "client");
    }

    /// <summary>
    /// Releases the load lobby's <c>PeerInputSynchronizer</c> handler that <c>CleanUp</c> leaves behind.
    ///
    /// <c>LoadRunLobby.CleanUp</c> disposes it only when <c>disconnectSession</c> is true (LoadRunLobby.cs:135-142),
    /// and every call here passes false, so each discarded lobby kept a <c>PeerInputMessage</c> handler registered on
    /// the still-alive session — the handler is registered in the synchronizer's constructor
    /// (PeerInputSynchronizer.cs:112) and removing it is all <c>Dispose</c> does (PeerInputSynchronizer.cs:116-119).
    /// One more copy per restart, each processing every incoming input message.
    ///
    /// The guard is load-bearing, not defensive: on the success path <c>RunManager.SetUpSavedMultiplayer</c> hands
    /// *this very instance* to the live run (RunManager.cs:445 → :558) and dozens of game UI pieces keep calling it
    /// through <c>RunManager.Instance.InputSynchronizer</c> (ScreenStateTracker.cs:64, NMapScreen.cs:725,
    /// NTargetManager.cs:441, …). Disposing it there would unregister the handler the running game depends on, so only
    /// a synchronizer the run does not own is released.
    /// </summary>
    private static void DisposeLobbyInputSynchronizer(LoadRunLobby lobby, string side)
    {
        try
        {
            PeerInputSynchronizer synchronizer = lobby.InputSynchronizer;
            if (synchronizer == null)
            {
                return;
            }

            if (ReferenceEquals(RunManager.Instance.InputSynchronizer, synchronizer))
            {
                Entry.Info(
                    $"mid-run-restart: keeping the {side} lobby's input synchronizer — the run that just started " +
                    "owns it (RunManager.cs:445/558).");
                return;
            }

            synchronizer.Dispose();
            Entry.Info($"mid-run-restart: released the discarded {side} lobby's input synchronizer handler.");
        }
        catch (Exception exception)
        {
            Entry.Warn(
                $"mid-run-restart: could not release the {side} lobby's input synchronizer: " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>
    /// Tears the run down the way the main-menu button does, but keeps the socket: the one
    /// <c>NetService.Disconnect</c> issued by <c>RunManager.CleanUp</c> is suppressed for exactly this call.
    /// </summary>
    private static async Task TeardownToMainMenuAsync(INetGameService service)
    {
        NGame game = NGame.Instance ?? throw new InvalidOperationException("NGame instance is unavailable.");

        // RunManager.CleanUp disposes every synchronizer of the run except this one — the list at
        // RunManager.cs:1752-1764 stops at RunLobby and never touches InputSynchronizer — and this teardown keeps the
        // session alive, so its PeerInputMessage handler stays registered on a live session and keeps handling every
        // incoming input message. Take it down once the run is gone; Dispose only unregisters the handler
        // (PeerInputSynchronizer.cs:116-119), so anything still holding the object cannot break on it.
        PeerInputSynchronizer? runInput = RunManager.Instance.InputSynchronizer;

        _suppressedService = service;
        _suppressDisconnect = true;
        try
        {
            await game.ReturnToMainMenu();
        }
        finally
        {
            _suppressDisconnect = false;
            _suppressedService = null;
        }

        try
        {
            runInput?.Dispose();
        }
        catch (Exception exception)
        {
            Entry.Warn(
                $"mid-run-restart: could not release the torn-down run's input synchronizer: " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static async Task<NMainMenu> WaitForMainMenuAsync()
    {
        SceneTree? tree = Engine.GetMainLoop() as SceneTree
            ?? throw new InvalidOperationException("SceneTree is unavailable.");

        for (int frame = 0; frame < MainMenuWaitFrames; frame++)
        {
            NMainMenu? menu = NGame.Instance?.MainMenu;
            if (menu != null && GodotObject.IsInstanceValid(menu))
            {
                return menu;
            }

            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }

        throw new InvalidOperationException("the main menu did not appear in time.");
    }

    private static void ShowInfo(string message)
    {
        try
        {
            NErrorPopup? popup = NErrorPopup.Create("LAN Connect 0.113 兼容补丁", message, showReportBugButton: false);
            if (popup != null)
            {
                NModalContainer.Instance?.Add(popup);
            }
        }
        catch (Exception exception)
        {
            Entry.Warn($"mid-run-restart: could not show the popup: {exception.Message}");
        }
    }

    /// <summary>
    /// Drops exactly one <c>NetService.Disconnect</c> — the one issued by <c>RunManager.CleanUp</c> while we are
    /// deliberately keeping the session. Everything else passes through.
    /// </summary>
    private static bool HostDisconnectPrefix(NetHostGameService __instance, NetError reason) =>
        ShouldKeepSession(__instance, reason);

    private static bool ClientDisconnectPrefix(NetClientGameService __instance, NetError reason) =>
        ShouldKeepSession(__instance, reason);

    /// <summary>
    /// Drops the teardown's own <c>Disconnect(NetError.Quit)</c> — and only that one.
    ///
    /// The reason filter is load-bearing, not defensive: while the teardown is running, the Tail runtime can
    /// reject an inbound frame on the very same service and call <c>Disconnect(NetError.ModMismatch)</c>
    /// (LanConnectTailMessageRuntime.cs:1428). Swallowing that one leaves the session half torn down — the binding
    /// is still registered, ENet is still up, and nothing ever reports the rejection. Field log 2026-11-13
    /// 566639 → 566660/566679 is exactly that sequence.
    /// </summary>
    private static bool ShouldKeepSession(object instance, NetError reason)
    {
        if (!_suppressDisconnect || !ReferenceEquals(instance, _suppressedService))
        {
            return true;
        }

        if (reason != NetError.Quit)
        {
            Entry.Warn(
                $"mid-run-restart: letting the teardown's {reason} disconnect through instead of suppressing it.");
            return true;
        }

        Entry.Info("mid-run-restart: suppressed the teardown disconnect; the session stays up.");
        return false;
    }

    // =====================================================================================================
    // tail_v1 precondition
    // =====================================================================================================

    /// <summary>
    /// Reads whether the host mod's tail runtime still has a session binding for this service.
    ///
    /// The reload handshake is carried by the tail's extra extension frames: the host holds our
    /// <c>LoadJoinRequest</c> until our frame arrives (2000 ms, then <c>ModMismatch</c>), and we hold its
    /// <c>LoadJoinResponse</c> until its frame arrives. Without the binding the vanilla messages still flow but
    /// the frames do not, so both sides silently wait and the feature fails with a timeout that says nothing
    /// about the cause. Probing first turns that into one explicit, actionable line.
    ///
    /// The field report that motivated this (2026-11-13) showed the client with <c>writerBindings=0</c> and an
    /// inbound <c>LanConnectNativeBusMessage</c> that had no handler; the root cause was the join flow releasing
    /// the binding at the end of a *successful* join (fixed in <c>JoinTakeoverFix</c>). This probe stays as the
    /// guard that makes any future recurrence obvious instead of a 15-second mystery.
    /// </summary>
    private static TailBindingState ProbeTailBinding(INetGameService service)
    {
        try
        {
            Shape shape = _shape ?? throw new InvalidOperationException("shape was not captured.");
            object shared = Reflect.RequireStaticPropertyValue(shape.TailRuntimeType, "Shared");
            object bus = shape.TailGetMessageBus.Invoke(shared, new object?[] { service })
                ?? throw new InvalidOperationException("the tail runtime could not resolve the message bus.");

            int writerBindings = shape.TailWriterBindings.GetValue(shared) is System.Collections.ICollection writers
                ? writers.Count
                : -1;
            bool bound = shape.TailBindings.GetValue(shared) is System.Collections.IDictionary bindings
                         && bindings.Contains(bus);

            return new TailBindingState(bound, writerBindings, bus.GetType().Name);
        }
        catch (Exception exception)
        {
            return new TailBindingState(
                false,
                -1,
                $"probe failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static string DescribeTailBinding(INetGameService service)
    {
        TailBindingState state = ProbeTailBinding(service);
        return $"tailBound={state.Bound} writerBindings={state.WriterBindings} bus={state.Bus}";
    }

    /// <summary>Session binding state of the host mod's tail runtime for one service.</summary>
    private readonly record struct TailBindingState(bool Bound, int WriterBindings, string Bus);

    /// <summary>
    /// Reflection anchors for <c>LanConnectRoomManagementPanel</c> plus the runtime method its flow awaits.
    ///
    /// Every member is required: the whole point of the takeover is that the label write is guarded, so a partial
    /// shape must refuse to install rather than install a half-guarded flow.
    /// </summary>
    private sealed class PanelShape
    {
        private PanelShape(
            MethodInfo hostRestart,
            FieldInfo statusLabel,
            FieldInfo restartButton,
            FieldInfo restartInFlight,
            PropertyInfo labelText,
            PropertyInfo? buttonDisabled,
            MethodInfo closePanel)
        {
            HostRestart = hostRestart;
            StatusLabel = statusLabel;
            RestartButton = restartButton;
            RestartInFlight = restartInFlight;
            LabelText = labelText;
            ButtonDisabled = buttonDisabled;
            ClosePanel = closePanel;
        }

        /// <summary><c>LanConnectLobbyRuntime.StartHostedRunRestartAsync()</c> — the call the panel flow awaits.</summary>
        internal MethodInfo HostRestart { get; }

        /// <summary><c>_statusLabel</c> (Label), LanConnectRoomManagementPanel.cs:80.</summary>
        internal FieldInfo StatusLabel { get; }

        /// <summary><c>_restartButton</c> (Button), LanConnectRoomManagementPanel.cs:82.</summary>
        internal FieldInfo RestartButton { get; }

        /// <summary><c>_restartInFlight</c> (bool), LanConnectRoomManagementPanel.cs:84.</summary>
        internal FieldInfo RestartInFlight { get; }

        /// <summary><c>Label.Text</c> — the property the original writes at :648.</summary>
        internal PropertyInfo LabelText { get; }

        /// <summary><c>BaseButton.Disabled</c> — the property the original's finally writes at :656.</summary>
        internal PropertyInfo? ButtonDisabled { get; }

        /// <summary>
        /// <c>ClosePanel()</c> (LanConnectRoomManagementPanel.cs:661-671) — the same method the panel's own
        /// "关闭" button is wired to (:412). Frees the node and clears the static <c>_instance</c>.
        /// </summary>
        internal MethodInfo ClosePanel { get; }

        internal static PanelShape Capture(Type panelType, MethodInfo hostRestart)
        {
            FieldInfo statusLabel = Reflect.RequireField(panelType, "_statusLabel");
            FieldInfo restartButton = Reflect.RequireField(panelType, "_restartButton");
            FieldInfo restartInFlight = Reflect.RequireField(panelType, "_restartInFlight");

            if (restartInFlight.FieldType != typeof(bool))
            {
                throw new MissingMemberException(
                    panelType.FullName,
                    $"_restartInFlight is {restartInFlight.FieldType.Name}, expected Boolean");
            }

            // Both properties are resolved from the field's own type, so a widget swap is caught here — at startup,
            // with a name — rather than at the moment the player needs the message.
            PropertyInfo labelText = statusLabel.FieldType.GetProperty(
                    "Text", BindingFlags.Instance | BindingFlags.Public)
                ?? throw new MissingMemberException(statusLabel.FieldType.FullName, "Text");

            if (labelText.PropertyType != typeof(string) || !labelText.CanWrite)
            {
                throw new MissingMemberException(
                    statusLabel.FieldType.FullName,
                    $"Text is {labelText.PropertyType.Name} and CanWrite={labelText.CanWrite}, expected a writable string");
            }

            PropertyInfo? buttonDisabled = restartButton.FieldType.GetProperty(
                "Disabled", BindingFlags.Instance | BindingFlags.Public);

            MethodInfo closePanel = Reflect.RequireInstanceMethod(panelType, "ClosePanel");
            if (closePanel.ReturnType != typeof(void) || closePanel.GetParameters().Length != 0)
            {
                throw new MissingMemberException(
                    panelType.FullName,
                    $"ClosePanel is {closePanel.ReturnType.Name}({closePanel.GetParameters().Length} parameter(s)), " +
                    "expected a parameterless void");
            }

            return new PanelShape(
                hostRestart,
                statusLabel,
                restartButton,
                restartInFlight,
                labelText,
                buttonDisabled,
                closePanel);
        }
    }

    /// <summary>
    /// Every reflection anchor, captured once during <see cref="Apply"/>. Anything missing throws with the member
    /// name, which the host turns into "this repair failed" — the feature never runs half-wired.
    /// </summary>
    private sealed class Shape
    {
        private Shape(
            MethodInfo tryLoadCurrentMultiplayerRun,
            MethodInfo buildSaveKey,
            MethodInfo admitLoadPeer,
            Type tailRuntimeType,
            MethodInfo tailGetMessageBus,
            FieldInfo tailBindings,
            FieldInfo tailWriterBindings)
        {
            TryLoadCurrentMultiplayerRun = tryLoadCurrentMultiplayerRun;
            BuildSaveKey = buildSaveKey;
            AdmitLoadPeer = admitLoadPeer;
            TailRuntimeType = tailRuntimeType;
            TailGetMessageBus = tailGetMessageBus;
            TailBindings = tailBindings;
            TailWriterBindings = tailWriterBindings;
        }

        internal MethodInfo TryLoadCurrentMultiplayerRun { get; }

        internal MethodInfo BuildSaveKey { get; }

        internal MethodInfo AdmitLoadPeer { get; }

        /// <summary><c>Sts2LanConnect.Scripts.LanConnectTailMessageRuntime</c>.</summary>
        internal Type TailRuntimeType { get; }

        /// <summary><c>LanConnectTailMessageRuntime.GetMessageBus(INetGameService)</c>.</summary>
        internal MethodInfo TailGetMessageBus { get; }

        /// <summary><c>LanConnectTailMessageRuntime._bindings</c> (bus → session binding).</summary>
        internal FieldInfo TailBindings { get; }

        /// <summary><c>LanConnectTailMessageRuntime._writerBindings</c> (PacketWriter → session binding).</summary>
        internal FieldInfo TailWriterBindings { get; }

        internal static Shape Capture(Assembly host)
        {
            Type binding = Reflect.RequireType(host, BindingTypeName);
            MethodInfo load = Reflect.RequireStaticMethod(
                binding,
                "TryLoadCurrentMultiplayerRun",
                typeof(SerializableRun).MakeByRefType(),
                typeof(string).MakeByRefType());

            MethodInfo buildSaveKey = Reflect.RequireStaticMethod(binding, "BuildSaveKey", typeof(SerializableRun));

            MethodInfo admit = Reflect.RequireInstanceMethod(
                typeof(LoadRunLobby),
                "OnConnectedToClientAsHost",
                typeof(ulong));
            if (admit.ReturnType != typeof(void))
            {
                throw new MissingMemberException(typeof(LoadRunLobby).FullName, "OnConnectedToClientAsHost(ulong)");
            }

            Type tailRuntime = Reflect.RequireType(host, TailRuntimeTypeName);
            // GetMessageBus is a *static* helper on the runtime; resolve it by name and arity, then verify the
            // shape we depend on instead of guessing the binding flags.
            MethodInfo tailGetMessageBus = Reflect.RequireMethodByName(tailRuntime, "GetMessageBus", 1);
            if (!tailGetMessageBus.IsStatic
                || tailGetMessageBus.ReturnType != typeof(NetMessageBus)
                || tailGetMessageBus.GetParameters()[0].ParameterType != typeof(INetGameService))
            {
                throw new MissingMemberException(
                    tailRuntime.FullName,
                    $"GetMessageBus(INetGameService) is static={tailGetMessageBus.IsStatic} " +
                    $"and returns {tailGetMessageBus.ReturnType.Name}");
            }

            FieldInfo tailBindings = Reflect.RequireField(tailRuntime, "_bindings");
            FieldInfo tailWriterBindings = Reflect.RequireField(tailRuntime, "_writerBindings");

            // _bindings is Dictionary<NetMessageBus, Binding> and Binding is a nested private type, so the
            // only stable way to reach it is through the field's own generic arguments. ProbeTailBinding only
            // needs the key type, but the shape is pinned here so a reshuffle fails loudly at startup.
            Type[] bindingArguments = tailBindings.FieldType.GetGenericArguments();
            if (bindingArguments.Length != 2 || bindingArguments[0] != typeof(NetMessageBus))
            {
                throw new MissingMemberException(
                    tailRuntime.FullName,
                    $"_bindings is not a NetMessageBus-keyed dictionary (found {tailBindings.FieldType.Name})");
            }

            // The roster epoch itself is reset by the shared TailRoster component (see TailRoster.Reset); it
            // resolves Binding.RequireRoster()/RosterAuthorityState.Reset() on its own so both repairs that
            // keep a session alive share one contract check.

            return new Shape(
                load,
                buildSaveKey,
                admit,
                tailRuntime,
                tailGetMessageBus,
                tailBindings,
                tailWriterBindings);
        }
    }
}
