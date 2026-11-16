using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Save;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Blitz;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using MegaCrit.Sts2.Core.Nodes.Screens.DailyRun;
using MegaCrit.Sts2.Core.Nodes.Screens.Dread;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;
using MegaCrit.Sts2.addons.mega_text;

namespace Sts2LobbyEnhance;

/// <summary>
/// New feature: a "start a new run" button on the game-over screen that brings the whole room back to the
/// character-select screen without re-hosting and without anyone re-joining.
///
/// Why it is possible at all: the session is only torn down when someone presses the main-menu button
/// (<c>NGameOverScreen.OnMainMenuButtonPressed</c> → <c>NetService.Disconnect(QuitGameOver)</c>). While the
/// game-over screen is up, <c>RunManager.State</c> is still set and the <c>NetService</c> is still connected, so
/// the finished run can be replaced by a fresh <c>StartRunLobby</c> on the very same connection.
///
/// The flow (host side):
/// <list type="number">
/// <item>tear the finished run down exactly like the main-menu button would — except that the single
/// <c>NetService.Disconnect</c> issued by <c>RunManager.CleanUp</c> is suppressed for that one call;</item>
/// <item>back on the main menu, build a new <c>StartRunLobby</c> through the game's own host path for the finished
/// run's mode (standard / daily / custom / blitz / dread, see <see cref="PushHostScreen"/>) and push that
/// screen;</item>
/// <item>admit every still-connected peer that also runs this patch, so the game's lobby protocol drives them
/// back to their own lobby screen (see <see cref="AdmitPatchedPeers"/>).</item>
/// </list>
///
/// The flow (client side): on the host's <c>InitialGameInfoMessage</c>, answer with
/// <c>ClientLobbyJoinRequestMessage</c> immediately (the host only waits ten seconds), tear the finished run down
/// the same way, and push the character-select screen once the host's <c>ClientLobbyJoinResponseMessage</c>
/// arrives.
///
/// Everything in between is the game's native lobby protocol: no custom frames, no changes to the mod's wire
/// layer, and no change to the frozen protocol profile of the room.
/// </summary>
internal sealed class NewRunFromGameOverFix : ICompatFix
{
    /// <summary>
    /// Prefix of our own entry in <c>PeerVersionInfo.otherMods</c> (<c>id-version</c>, see
    /// <c>ModManager.GetNonGameplayRelevantModNameList</c>). Matched without the version so a client running a
    /// different build of the patch is still recognised.
    /// </summary>
    private const string ModIdPrefix = "sts2_lobby_enhance";

    private const string CompatibilityTypeName = "Sts2LanConnect.Scripts.LanConnectMultiplayerCompatibility";
    private const string ConfigTypeName = "Sts2LanConnect.Scripts.LanConnectConfig";
    private const string BindingTypeName = "Sts2LanConnect.Scripts.LanConnectMultiplayerSaveRoomBinding";

    private const string ButtonNodeName = "LobbyEnhanceNewRunButton";
    private const string CaptionNodeName = "LobbyEnhanceNewRunCaption";
    private const string HintNodeName = "LobbyEnhanceReassembleHint";

    private const string NewRunLabel = "新开一局";

    /// <summary>
    /// <c>NReturnToMainMenuButton._Ready()</c> moves the node 140px to the left and stores the moved position as
    /// the target of its animate-in tween. The injection mirrors that so the tween lands on the layout rectangle.
    /// </summary>
    private const float SlideInDistance = 140f;

    /// <summary>
    /// Ten seconds, the budget <c>StartRunLobby.ClientResponseTimeout</c> gives a client. The player waits on the
    /// main menu for the host's answer, so a longer deadline buys nothing
    /// </summary>
    private const int JoinResponseTimeoutMs = 10000;

    private const int MainMenuWaitFrames = 180;

    private const int DailyAdmitFilterWindowMs = 60000;

    private static Shape? _shape;

    private static PatchTargets _targets;

    // Kept so the button can be withdrawn the moment the session it depends on is gone. A visible button that
    // silently does nothing is the exact failure the 2026-11-13 field report described ("我点了没反应").

    private static NReturnToMainMenuButton? _injectedButton;
    private static Label? _injectedCaption;
    private static Label? _injectedHint;
    private static INetGameService? _watchedService;

    private const string DisconnectedCaption = "联机会话已断开，无法新开一局";

    private const string DisconnectedMessage = "联机会话已断开，无法新开一局。请点「主菜单」返回主菜单后重新建房。";

    private static INetGameService? _suppressedService;
    private static bool _suppressDisconnect;

    private static bool _gameOverScreenVisible;

    /// <summary>
    /// The client session whose reassemble handlers are registered, used only as a de-duplication key.
    ///
    /// It used to double as "the current session" for the handlers, which was a real defect: after a reconnect the
    /// slot points at the new session while the old session's handlers are still registered on the old one, so a
    /// message arriving there was answered on the wrong session. The handlers no longer read this field (each one is
    /// bound to the session it was registered on, see <see cref="RegisterClientHandlers"/>), so a stale value can
    /// only cause a duplicate registration, never an answer sent on the wrong session.
    /// </summary>
    private static INetGameService? _handlersRegisteredFor;
    private static bool? _hostHasPatch;
    private static bool _reassemblePending;
    private static TaskCompletionSource<ClientLobbyJoinResponseMessage>? _joinResponse;

    private static bool _hostRestartInProgress;
    private static SerializableRun? _finishedRun;
    private static ScreenKind _finishedScreenKind = ScreenKind.Unsupported;
    private static GameMode? _finishedRunMode;
    private static bool _finishedModeSupported;

    /// <summary>
    /// Daily builds its own lobby asynchronously (after asking the time server) and then admits every connected
    /// peer unconditionally. This one-shot flag routes that single admission through the same "only patched peers"
    /// filter the other modes use, so an unpatched client is never put on the ten-second lobby-join timeout.
    /// </summary>
    private static bool _filterDailyAdmit;
    private static INetGameService? _filterDailyAdmitService;
    private static long _filterDailyAdmitDeadline;

    public string Id => "new-run-button";

    /// <summary>
    /// Which lobby screen a finished run maps onto. Mirrors the game's own join dispatch
    /// (<c>NJoinFriendScreen.JoinGameAsync</c>): custom runs first, then the game mode.
    /// </summary>
    private enum ScreenKind
    {
        Unsupported,
        CharacterSelect,
        Daily,
        Custom,
        Blitz,
        Dread
    }

    public FixGroup Group => FixGroup.NewRun;

    /// <summary>
    /// Answers "can this repair run on the build that is actually loaded", and resolves every anchor on the way.
    ///
    /// Nothing version-specific gates this: a game-over "start a new run" button rides the vanilla
    /// <c>StartRunLobby</c> protocol and only touches the game-over screen, so its only preconditions are the
    /// anchors resolved below.
    ///
    /// Resolving here rather than in <see cref="Apply"/> is what keeps the startup report honest: a missing anchor
    /// becomes "skipped, here is why" instead of a repair that reports itself as applied and then does nothing.
    /// </summary>
    public bool Applies(out string reason)
    {
        Assembly? host = Reflect.FindAssembly(Entry.HostModAssemblyName);
        if (host == null)
        {
            reason = $"host mod '{Entry.HostModAssemblyName}' is not loaded";
            return false;
        }

        if (!TryCapture(host, out Shape? shape, out PatchTargets targets, out string missing))
        {
            reason = "missing: " + missing;
            return false;
        }

        _shape = shape;
        _targets = targets;
        reason = string.Empty;
        return true;
    }

    public void Apply(Harmony harmony)
    {
        // Applies() resolved and cached these; reaching Apply without them is a host-contract violation, not a build
        // problem, so it must not be silently tolerated.
        PatchTargets targets = _targets;
        Shape shape = _shape
            ?? throw new InvalidOperationException(
                "new-run-button: Apply ran without a captured shape; Applies must be consulted first.");

        // _Ready still owns the *client* hint (the client must know whether to wait for the host while it sits
        // on the first page of the screen).
        harmony.Patch(
            targets.GameOverReady,
            postfix: new HarmonyMethod(typeof(NewRunFromGameOverFix), nameof(GameOverReadyPostfix))
            {
                priority = Priority.First
            });

        // The host's "新开一局" button is deliberately *not* injected in _Ready: the player asked for it to
        // appear only after pressing "继续" and entering the run summary, so the first page shows exactly what
        // the game shows. OpenSummaryScreen is that moment, and it runs at most once per screen because the
        // continue button it disables is only re-enabled by the screen's own intro animation.
        harmony.Patch(
            targets.OpenSummary,
            postfix: new HarmonyMethod(typeof(NewRunFromGameOverFix), nameof(SummaryOpenedPostfix))
            {
                priority = Priority.First
            });

        // Leaving the summary hides the whole summary container, so the injected nodes have to leave with it:
        // they are siblings of the container, not children, and would otherwise stay on top of the leaderboard
        // that opens straight after the fade-out.
        harmony.Patch(
            targets.HideSummary,
            postfix: new HarmonyMethod(typeof(NewRunFromGameOverFix), nameof(SummaryClosedPostfix))
            {
                priority = Priority.First
            });

        // The teardown of a finished run is the only place we must keep the socket alive. Both sides need it:
        // the host's RunManager.CleanUp and the client's RunManager.CleanUp each call Disconnect().
        harmony.Patch(
            targets.HostDisconnect,
            prefix: new HarmonyMethod(typeof(NewRunFromGameOverFix), nameof(HostDisconnectPrefix))
            {
                priority = Priority.First
            });

        harmony.Patch(
            targets.ClientDisconnect,
            prefix: new HarmonyMethod(typeof(NewRunFromGameOverFix), nameof(ClientDisconnectPrefix))
            {
                priority = Priority.First
            });

        // The client discards the host's PeerVersionInfo after the handshake, which is the only place that knows
        // whether the host runs this patch. Capture it (and register the reassemble handlers) here.
        harmony.Patch(
            targets.HandshakeSucceeded,
            postfix: new HarmonyMethod(typeof(NewRunFromGameOverFix), nameof(HandshakeSucceededPostfix))
            {
                priority = Priority.First
            });

        // The daily lobby screen builds its own StartRunLobby (after asking the time server) and then admits every
        // connected peer. That single admission has to be filtered while we restart into a daily run, otherwise an
        // unpatched client would be put on the ten-second ClientResponseTimeout and kicked.
        harmony.Patch(
            targets.AdmitClientsConnected,
            prefix: new HarmonyMethod(typeof(NewRunFromGameOverFix), nameof(AdmitConnectedPeersPrefix))
            {
                priority = Priority.First
            });

        Entry.Info(
            "new-run-button: game-over screen patched (host button / client hint), session teardown kept alive, " +
            "handshake mod list captured.");

        if (shape.SetSelectedGameMode == null)
        {
            Entry.Warn(
                $"new-run-button: {CustomRunScreenTypeName}._gameModeDropdown / SetSelectedGameMode did not " +
                "resolve, so a rebuilt custom lobby starts in the dropdown's own default mode.");
        }
    }

    private static void GameOverReadyPostfix(NGameOverScreen __instance)
    {
        try
        {
            // A new game-over screen means the previous one is gone and its injected nodes died with it. Dropping
            // the references here keeps a later summary open / leaderboard close from touching a freed node, and
            // keeps one game's label from being "reused" onto another game's screen.
            _injectedButton = null;
            _injectedCaption = null;
            _injectedHint = null;

            Shape shape = _shape ?? throw new InvalidOperationException("new-run-button: shape was not captured.");
            RunManager manager = RunManager.Instance;
            INetGameService? service = manager.NetService;
            if (service == null || !manager.HasRunEnded)
            {
                return;
            }

            if (service.Type == NetGameType.Host)
            {
                // Nothing to do on the first page: the host's button and caption belong to the run summary and
                // are injected by SummaryOpenedPostfix. Waiting loses nothing — the finished run, the peer counts
                // and the session liveness are all still readable from this screen when the summary opens.
                Entry.Info("new-run-button: host game-over screen ready; the restart button waits for the summary.");
                return;
            }

            if (service.Type == NetGameType.Client)
            {
                InjectClientHint(shape, __instance, service);
            }
        }
        catch (Exception exception)
        {
            Entry.Error($"new-run-button: game-over UI injection failed: {exception}");
        }
    }

    /// <summary>
    /// The run summary is on screen: this is where the host's "新开一局" button and its caption appear.
    ///
    /// The hook is <c>NGameOverScreen.OpenSummaryScreen(NButton)</c>, the method the "继续" button is wired to.
    /// It is private, but Harmony patches private instance
    /// methods as long as the signature matches; <see cref="TryCapture"/> resolves it by name and exact parameter
    /// types before anything is patched.
    /// </summary>
    private static void SummaryOpenedPostfix(NGameOverScreen __instance)
    {
        try
        {
            Shape shape = _shape ?? throw new InvalidOperationException("new-run-button: shape was not captured.");
            RunManager manager = RunManager.Instance;
            INetGameService? service = manager.NetService;
            if (service == null || !manager.HasRunEnded)
            {
                return;
            }

            // The client's hint stays on the first page (injected from _Ready); only the host has a button here.
            if (service.Type != NetGameType.Host)
            {
                return;
            }

            InjectHostControls(shape, __instance, service);
        }
        catch (Exception exception)
        {
            Entry.Error($"new-run-button: injecting the host controls on the summary failed: {exception}");
        }
    }

    /// <summary>
    /// The summary is being hidden: take the injected nodes with it.
    ///
    /// The nodes are hidden, not freed: <c>OpenSummaryScreen</c> can be invoked again through the screen's own
    /// method dispatcher, and a second injection has to reuse the nodes it created the first time instead of
    /// stacking a second button and a second label on top of them.
    /// </summary>
    private static void SummaryClosedPostfix(NGameOverScreen __instance)
    {
        HideHostControls($"the summary closed on {__instance.Name}");
    }

    /// <summary>
    /// Hides the injected host controls while keeping the nodes alive for the next summary open. Unlike
    /// <see cref="WithdrawHostControls"/> this does not rewrite the caption: nothing is wrong with the session,
    /// the summary was only covered by the leaderboard that opens after its fade-out, or hidden by the screen's
    /// own method dispatcher.
    /// </summary>
    private static void HideHostControls(string why)
    {
        bool hidden = false;

        if (_injectedButton is { } button)
        {
            try
            {
                if (GodotObject.IsInstanceValid(button))
                {
                    // OnDisable sets Visible = false, and OnEnable replays the game's own fade-in when we come
                    // back.
                    button.Disable();
                    hidden = true;
                }
            }
            catch (Exception exception)
            {
                Entry.Warn($"new-run-button: could not hide the restart button: {exception.Message}");
            }
        }

        if (_injectedCaption is { } caption)
        {
            try
            {
                if (GodotObject.IsInstanceValid(caption))
                {
                    caption.Visible = false;
                    hidden = true;
                }
            }
            catch (Exception exception)
            {
                Entry.Warn($"new-run-button: could not hide the caption: {exception.Message}");
            }
        }

        if (hidden)
        {
            Entry.Info($"new-run-button: host controls hidden ({why}).");
        }
    }

    private static void InjectHostControls(Shape shape, NGameOverScreen screen, INetGameService service)
    {
        NReturnToMainMenuButton mainMenu = shape.MainMenuButton.GetValue(screen) as NReturnToMainMenuButton
            ?? throw new InvalidOperationException("new-run-button: the main-menu button is missing from the screen.");

        // The screen dies with the run, so the finished run has to be captured now: the stale room binding is
        // keyed by a hash of it and can only be cleaned up before the teardown.
        if (shape.ResultField.GetValue(screen) is PostRunProgressUpdateResult result)
        {
            _finishedRun = result.run;
        }

        PeerCounts counts = CountPeers(service);
        RunState? runState = shape.RunStateField.GetValue(screen) as RunState;
        ScreenKind screenKind = ResolveScreenKind(runState);
        bool supported = screenKind != ScreenKind.Unsupported;
        _finishedScreenKind = screenKind;
        _finishedRunMode = runState?.GameMode;
        _finishedModeSupported = supported;
        // The session can already be gone by the time the screen appears: "abandon run" stops the host and only
        // afterwards shows the game-over screen (field report 2026-11-13: host log 1806-1807 stops the host, 1929
        // injects the button). Offering a button that cannot possibly work is worse than not offering it, so
        // liveness is part of the decision here, not only of the click handler.
        bool sessionAlive = service.IsConnected;
        bool canRestart = supported && sessionAlive && counts.Patched > 0;

        Control anchor = ResolveLayoutAnchor(shape, screen);
        GameOverSlots slots = ComputeSlots(mainMenu, anchor, withRestartButton: canRestart);

        if (canRestart)
        {
            _injectedButton = ShowRestartButton(shape, mainMenu, anchor, slots.Button);
        }
        else
        {
            // Nothing to offer this time (session already gone, or no patched peer). A button left over from an
            // earlier open of the summary must not stay clickable.
            HideHostControls("the restart button is not offered");
        }

        // Without a button the caption takes the slot the button would have used, so it still sits right under
        // the main-menu button instead of leaving a hole. The label is reused across opens for the same reason
        // the button is (see ShowInfoLabel).
        LayoutRect captionRect = canRestart ? slots.Caption : slots.Hint;
        _injectedCaption = ShowInfoLabel(
            _injectedCaption,
            CaptionNodeName,
            BuildHostCaption(supported, sessionAlive, counts),
            slots,
            captionRect,
            anchor);

        WatchSession(service);

        Entry.Info(
            $"new-run-button: host controls injected (supported={supported}, sessionAlive={sessionAlive}, " +
            $"patched={counts.Patched}, unknown={counts.Unknown}, total={counts.Total}, anchor={anchor.Name}, " +
            $"placement={(slots.PlacedBelow ? "below" : "above")}, font={slots.FontSize}, gap={slots.Gap:0}, " +
            $"menu={Describe(slots.Menu)}, button={Describe(slots.Button)}, caption={Describe(captionRect)}).");
    }

    /// <summary>
    /// Shows the "新开一局" button, creating it the first time and bringing the same node back afterwards. It is
    /// a duplicate of the screen's own "主菜单" button so it keeps the exact visual style (same scene, same
    /// shader, same animate-in) without touching the game's .tscn.
    ///
    /// Reuse is not an optimisation: <c>OpenSummaryScreen</c> is reachable more than once through the screen's
    /// method dispatcher, and every extra <c>AddChild</c> would leave another button and another caption stacked
    /// on the same rectangle.
    /// </summary>
    private static NReturnToMainMenuButton? ShowRestartButton(
        Shape shape,
        NReturnToMainMenuButton mainMenu,
        Control anchor,
        LayoutRect rect)
    {
        Vector2 target = new(rect.X, rect.Y);

        if (_injectedButton is { } existing && GodotObject.IsInstanceValid(existing))
        {
            if (ReferenceEquals(existing.GetParent(), anchor))
            {
                // Same node, same parent: only the rectangle and the enable state can have changed.
                shape.ShowPosition.SetValue(existing, target);
                existing.Position = target;
                existing.Disable();
                existing.Enable();

                // Enable() runs NButton.OnEnable, which calls RegisterHotkeys() and pushes MegaInput.select
                // again. That is exactly what took the confirm key away from "主菜单" the first time (see
                // ReleaseDuplicatedHotkey), so it has to be dropped again on every re-enable.
                ReleaseDuplicatedHotkey(shape, existing);
                return existing;
            }

            // Left over from a different anchor: it would stay visible behind the button created below.
            if (!existing.IsQueuedForDeletion())
            {
                existing.QueueFree();
            }
        }

        NReturnToMainMenuButton button = (NReturnToMainMenuButton)mainMenu.Duplicate();
        button.Name = ButtonNodeName;
        button.Position = target;
        anchor.AddChildSafely(button);

        if (!button.IsInsideTree())
        {
            // AddChildSafely only defers when the parent is not ready; the screen has been in the tree since
            // _Ready and the summary only opens after that, so this should never happen. Leave the node where
            // the scene put it instead of guessing.
            Entry.Warn("new-run-button: the restart button was added deferred; its position may be wrong.");
            return button;
        }

        // _Ready() has just run on the duplicate: it moved the node 140px to the left and stored *that* as the
        // target of its animate-in tween. Overwrite the target so the tween lands exactly on the layout
        // rectangle instead of 140px to the side of it.
        shape.ShowPosition.SetValue(button, target);
        button.Position = target + new Vector2(-SlideInDistance, 0f);

        button.GetNode<MegaLabel>("Label").SetTextAutoSize(NewRunLabel);
        button.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(OnNewRunPressed));

        // The duplicate inherits the original's enabled=false state (the screen disables the button in _Ready),
        // so Enable() replays the game's own animate-in: Visible = true, modulate transparentBlack -> white,
        // and the position tween set up above.
        button.Disable();
        button.Enable();

        ReleaseDuplicatedHotkey(shape, button);
        CopyButtonMaterial(shape, mainMenu, button);
        return button;
    }

    /// <summary>
    /// Withdraws the injected controls. Called when the session the button depends on ends, and again when the
    /// player presses a button that has already become stale — the field report was 29 presses of a button that
    /// logged a warning and did nothing the player could see.
    ///
    /// <paramref name="captionText"/> is why the button is gone. It goes into the caption because that label is
    /// the only thing left on the screen once the button disappears
    /// </summary>
    private static void WithdrawHostControls(string captionText)
    {
        NReturnToMainMenuButton? button = _injectedButton;
        _injectedButton = null;
        if (button != null)
        {
            try
            {
                if (GodotObject.IsInstanceValid(button))
                {
                    // NReturnToMainMenuButton.OnDisable sets Visible = false, so the button really disappears
                    // instead of staying clickable.
                    button.Disable();
                }
            }
            catch (Exception exception)
            {
                Entry.Warn($"new-run-button: could not withdraw the restart button: {exception.Message}");
            }
        }

        Label? caption = _injectedCaption;
        _injectedCaption = null;
        if (caption != null)
        {
            try
            {
                if (GodotObject.IsInstanceValid(caption))
                {
                    // The caption stays visible on purpose: it is the only thing that can tell the player why
                    // the button vanished.
                    caption.Text = captionText;
                }
            }
            catch (Exception exception)
            {
                Entry.Warn($"new-run-button: could not update the caption: {exception.Message}");
            }
        }
    }

    /// <summary>
    /// Subscribes to the session's own disconnect event so the button disappears the moment the session does —
    /// not on the next click. One subscription per service; a new session replaces it.
    /// </summary>
    private static void WatchSession(INetGameService service)
    {
        if (ReferenceEquals(_watchedService, service))
        {
            return;
        }

        UnwatchSession();
        try
        {
            service.Disconnected += OnSessionDisconnected;
            _watchedService = service;
        }
        catch (Exception exception)
        {
            Entry.Warn($"new-run-button: could not watch the session: {exception.Message}");
        }
    }

    private static void UnwatchSession()
    {
        INetGameService? previous = _watchedService;
        _watchedService = null;
        if (previous == null)
        {
            return;
        }

        try
        {
            previous.Disconnected -= OnSessionDisconnected;
        }
        catch (Exception exception)
        {
            Entry.Warn($"new-run-button: could not stop watching the session: {exception.Message}");
        }
    }

    /// <summary>
    /// One handler for both sides, because <see cref="WatchSession"/> is the only subscription either side
    /// makes. On the host it withdraws the button; on the client it re-words the hint, which otherwise keeps
    /// claiming the host can still start a new run (phone log 566731 → 566744).
    /// </summary>
    private static void OnSessionDisconnected(NetErrorInfo info)
    {
        Entry.Info(
            $"new-run-button: the session ended ({info.GetReason()}); withdrawing the restart button and " +
            "re-wording the client hint.");
        WithdrawHostControls(DisconnectedCaption);

        // No-op when the screen has no hint label (host screen, or the client hint was never injected).
        SetHintText(BuildClientHint(sessionAlive: false));
    }

    /// <summary>
    /// A duplicate also duplicates <c>NReturnToMainMenuButton.Hotkeys</c> (<c>MegaInput.select</c>).
    /// <c>NHotkeyManager</c> resolves a hotkey by insertion order, so a second registration would take the
    /// confirm key away from "继续" (during the intermission) and from "主菜单" (on the summary page) and hand
    /// it to "新开一局" — a single key press would silently start a new run. Our button is mouse-only on
    /// purpose; dropping its binding is the smallest fix that leaves the game's own bindings untouched.
    /// </summary>
    private static void ReleaseDuplicatedHotkey(Shape shape, NReturnToMainMenuButton button)
    {
        try
        {
            shape.UnregisterHotkeys.Invoke(button, null);
        }
        catch (Exception exception)
        {
            Entry.Warn($"new-run-button: could not release the duplicated button's hotkey: {exception.Message}");
        }
    }

    /// <summary>
    /// <c>Duplicate()</c> shares the hover shader material with the original, so hovering either button would
    /// brighten both. Give the copy its own material.
    /// </summary>
    private static void CopyButtonMaterial(
        Shape shape,
        NReturnToMainMenuButton source,
        NReturnToMainMenuButton copy)
    {
        try
        {
            TextureRect? sourceImage = source.GetNodeOrNull<TextureRect>("Image");
            TextureRect? copyImage = copy.GetNodeOrNull<TextureRect>("Image");
            if (sourceImage?.Material is not ShaderMaterial shared || copyImage == null)
            {
                return;
            }

            if (shared.Duplicate() is not ShaderMaterial own)
            {
                return;
            }

            copyImage.Material = own;
            shape.ShaderMaterial.SetValue(copy, own);
        }
        catch (Exception exception)
        {
            Entry.Warn($"new-run-button: could not give the restart button its own shader material: {exception.Message}");
        }
    }

    private static void InjectClientHint(Shape shape, NGameOverScreen screen, INetGameService service)
    {
        // Every mode this patch can restart now has a target screen, so the hint no longer depends on the mode.
        // A run whose mode we cannot map still gets the hint: the wording only says what the host may do.
        if (shape.RunStateField.GetValue(screen) is not RunState)
        {
            return;
        }

        NReturnToMainMenuButton mainMenu = shape.MainMenuButton.GetValue(screen) as NReturnToMainMenuButton
            ?? throw new InvalidOperationException("new-run-button: the main-menu button is missing from the screen.");

        _gameOverScreenVisible = true;

        // The hint talks about what the *host* can still do, so it is only true while our own session is alive.
        // IsConnected is the game's own answer to "can this service still send messages", and a client clears
        // it before the Disconnected event fires. The host path uses the same flag for the same reason
        // (InjectHostControls).
        bool sessionAlive = service.IsConnected;

        Control anchor = ResolveLayoutAnchor(shape, screen);
        GameOverSlots slots = ComputeSlots(mainMenu, anchor, withRestartButton: false);

        _injectedHint = ShowInfoLabel(
            _injectedHint,
            HintNodeName,
            BuildClientHint(sessionAlive),
            slots,
            slots.Hint,
            anchor);

        // A disconnect can also happen *after* this line; the label is then stale until someone updates it, so
        // the session's own event is the hook (see OnSessionDisconnected).
        WatchSession(service);

        Entry.Info(
            $"new-run-button: client hint injected (hostHasPatch={_hostHasPatch?.ToString() ?? "unknown"}, " +
            $"sessionAlive={sessionAlive}, anchor={anchor.Name}, " +
            $"placement={(slots.PlacedBelow ? "below" : "above")}, font={slots.FontSize}, " +
            $"menu={Describe(slots.Menu)}, hint={Describe(slots.Hint)}).");
    }

    /// <summary>
    /// Picks the coordinate space (and parent) for the injected nodes.
    ///
    /// The screen's own buttons are absolutely positioned — <c>NReturnToMainMenuButton._Ready()</c> and
    /// <c>NGameOverContinueButton.OnEnable()</c> both tween their own <c>position</c>, which only works when no
    /// Container re-lays them out — and <c>NGameOverScreen</c> derives from <c>NClickableControl</c>, i.e. a
    /// plain <c>Control</c>. So the parent is a plain Control as well, and setting <c>Position</c> is final.
    ///
    /// The UI root the screen fades in (<c>%Ui</c>) is preferred over the screen itself because being its child
    /// makes the injected nodes participate in the game's own fade-in instead of popping in fully opaque on top
    /// of a backstop that is still fading. It is only used when it really is a full-screen, untranslated Control,
    /// so the coordinates measured against it are the same ones the screen itself would report.
    /// </summary>
    private static Control ResolveLayoutAnchor(Shape shape, NGameOverScreen screen)
    {
        if (shape.UiNode.GetValue(screen) is Control ui
            && GodotObject.IsInstanceValid(ui)
            && ui.GlobalPosition.IsEqualApprox(Vector2.Zero)
            && ui.Size.X >= screen.Size.X - 1f
            && ui.Size.Y >= screen.Size.Y - 1f)
        {
            return ui;
        }

        return screen;
    }

    private static GameOverSlots ComputeSlots(
        NReturnToMainMenuButton mainMenu,
        Control anchor,
        bool withRestartButton)
    {
        LayoutRect menu = ToAnchorLocal(mainMenu, anchor);
        Vector2 viewport = ResolveViewportSize(anchor);
        return GameOverLayout.ComputeLayout(menu, viewport.X, viewport.Y, withRestartButton);
    }

    /// <summary>Measures a node in <paramref name="anchor"/>'s local space, so the layout never assumes the
    /// anchor sits at the origin.</summary>
    private static LayoutRect ToAnchorLocal(Control node, Control anchor)
    {
        Vector2 local = anchor.GetGlobalTransform().AffineInverse() * node.GlobalPosition;
        return new LayoutRect(local.X, local.Y, node.Size.X, node.Size.Y);
    }

    private static Vector2 ResolveViewportSize(Control anchor)
    {
        if (anchor.Size.X >= 1f && anchor.Size.Y >= 1f)
        {
            return anchor.Size;
        }

        Vector2 size = anchor.GetViewportRect().Size;
        if (size.X < 1f || size.Y < 1f)
        {
            size = new Vector2(1920f, 1080f);
            Entry.Warn($"new-run-button: the layout anchor reports no size; assuming {size}.");
        }

        return size;
    }

    /// <summary>
    /// Builds one of the two info labels (host caption / client hint). The size comes from the layout so the
    /// text is centred inside the exact rectangle the tests assert on; wrapping keeps the longest wording inside
    /// that box, and the dark outline keeps it readable over both the dark backstop and the brighter summary.
    /// </summary>
    private static Label CreateInfoLabel(string text, in GameOverSlots slots, in LayoutRect rect)
    {
        Label label = new()
        {
            Text = text,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Position = new Vector2(rect.X, rect.Y),
            Size = new Vector2(rect.Width, rect.Height)
        };
        ApplyLabelTheme(label, slots);
        return label;
    }

    /// <summary>
    /// Shows one of the two info labels, reusing the node when it already exists under the same parent — the
    /// same reason <see cref="ShowRestartButton"/> reuses its button: a second <c>AddChild</c> would leave a
    /// second copy of the text stacked on the same rectangle.
    /// </summary>
    private static Label ShowInfoLabel(
        Label? existing,
        string nodeName,
        string text,
        in GameOverSlots slots,
        in LayoutRect rect,
        Control anchor)
    {
        if (existing != null && GodotObject.IsInstanceValid(existing))
        {
            if (ReferenceEquals(existing.GetParent(), anchor))
            {
                existing.Text = text;
                existing.Position = new Vector2(rect.X, rect.Y);
                existing.Size = new Vector2(rect.Width, rect.Height);
                existing.Visible = true;
                ApplyLabelTheme(existing, slots);
                return existing;
            }

            // Left over from a different anchor: it would keep showing the old text behind the new label.
            if (!existing.IsQueuedForDeletion())
            {
                existing.QueueFree();
            }
        }

        Label label = CreateInfoLabel(text, slots, rect);
        label.Name = nodeName;
        anchor.AddChildSafely(label);
        return label;
    }

    /// <summary>
    /// The theme overrides both info labels share. Applied on reuse as well, because the font size follows the
    /// viewport height (<c>GameOverLayout.ComputeLayout</c>) and a resized window must not keep the old size.
    /// </summary>
    private static void ApplyLabelTheme(Label label, in GameOverSlots slots)
    {
        label.AddThemeFontSizeOverride("font_size", slots.FontSize);
        label.AddThemeColorOverride("font_color", new Color(0.95f, 0.95f, 0.95f));
        label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.85f));
        label.AddThemeConstantOverride("outline_size", Math.Max(4, slots.FontSize / 5));
    }

    private static string Describe(in LayoutRect rect) =>
        rect.IsEmpty ? "<none>" : $"({rect.X:0},{rect.Y:0} {rect.Width:0}x{rect.Height:0})";

    private static string BuildHostCaption(bool supported, bool sessionAlive, PeerCounts counts)
    {
        if (!sessionAlive)
        {
            return DisconnectedCaption;
        }

        if (!supported)
        {
            return "当前模式不支持一键新开，请点「主菜单」后重新建房";
        }

        if (counts.Total == 0)
        {
            return "房间里没有其他玩家，无法重开";
        }

        if (counts.Patched == 0)
        {
            return "队友均未安装兼容补丁，无法重开";
        }

        if (counts.Patched == counts.Total)
        {
            return "队友会一起回到准备页";
        }

        string text = $"{counts.Patched} 名队友将一起返回准备页";
        if (counts.Unknown > 0)
        {
            text += $"，{counts.Unknown} 人补丁状态未知";
        }

        int unpatched = counts.Total - counts.Patched - counts.Unknown;
        if (unpatched > 0)
        {
            text += $"，{unpatched} 人未安装补丁需手动重连";
        }

        return text;
    }

    /// <summary>
    /// The client's one-line hint. Two inputs decide it: whether our own session is still alive, and whether the
    /// host runs this patch.
    ///
    /// Every wording below has to fit the label <see cref="CreateInfoLabel"/> builds. <c>GameOverLayout</c>
    /// gives that label <c>min(maxMenuWidth, max(2 * menuWidth, 420))</c> px, so 520 px with the shipped
    /// main-menu button and never less than 420 px, and the 1080p font is the same 24 px the layout's
    /// <c>MinFontSize</c> pins. A full-width CJK glyph therefore costs 24 px, so the narrowest budget is 17
    /// glyphs (420 / 24 = 17.5). The glyph count of each wording is noted next to it;
    /// <c>AutowrapMode.WordSmart</c> is the safety net, not the plan.
    /// </summary>
    private static string BuildClientHint(bool sessionAlive)
    {
        // 16 glyphs ≈ 384 px. Nothing the host does matters once our session is gone, and this was the field
        // bug: the phone logged "Disconnected. Reason: HostAbandoned" (phone log 566731) and the hint injected
        // two lines later still told the player to wait for the host (566744).
        if (!sessionAlive)
        {
            return "房主已结束联机，请退出后重新加入";
        }

        return _hostHasPatch switch
        {
            // 16 glyphs ≈ 384 px. The user's own wording.
            true => "房主可以新开一局，不着急退出游戏",
            // 16 glyphs ≈ 384 px.
            false => "房主未装补丁，本局结束需退出重进",
            // 17 glyphs ≈ 408 px.
            _ => "房主补丁未知，可能直接新开或需重进"
        };
    }

    private static void OnNewRunPressed(NButton _)
    {
        try
        {
            if (_hostRestartInProgress)
            {
                return;
            }

            Shape shape = _shape ?? throw new InvalidOperationException("new-run-button: shape was not captured.");
            RunManager manager = RunManager.Instance;

            // Pre-flight only. Everything that can fail here leaves the host exactly where they are: on the
            // game-over screen, with the session untouched. Every rejection must be *visible*: the 2026-11-13
            // report was a player pressing this button 29 times against a log-only warning.
            if (!manager.HasRunEnded)
            {
                Entry.Warn("new-run-button: the run has not ended yet; ignoring the request.");
                RejectRestart("本局尚未结束，无法新开一局。");
                return;
            }

            INetGameService? service = manager.NetService;
            if (service is not NetHostGameService host)
            {
                Entry.Warn(
                    $"new-run-button: the session is no longer a host session " +
                    $"(service={service?.GetType().Name ?? "<null>"}); refusing to restart.");
                RejectRestart(DisconnectedMessage);
                return;
            }

            if (!host.IsConnected)
            {
                Entry.Warn(
                    $"new-run-button: the host session is closed (service={host.GetType().Name}, " +
                    $"isConnected={host.IsConnected}, peers={host.ConnectedPeers.Count()}); refusing to restart.");
                RejectRestart(DisconnectedMessage);
                return;
            }

            if (!_finishedModeSupported || _finishedScreenKind == ScreenKind.Unsupported)
            {
                Entry.Warn("new-run-button: this run's mode has no known lobby screen; ignoring the request.");
                RejectRestart("当前模式不支持一键新开，请点「主菜单」后重新建房。");
                return;
            }

            if (NGame.Instance == null)
            {
                Entry.Warn("new-run-button: NGame is unavailable; ignoring the request.");
                RejectRestart("游戏尚未就绪，无法新开一局。请稍后重试。");
                return;
            }

            PeerCounts counts = CountPeers(host);
            if (counts.Patched == 0)
            {
                Entry.Warn("new-run-button: no connected peer runs this patch; refusing to restart.");
                RejectRestart("队友均未安装兼容补丁，无法新开一局。请点「主菜单」后重新建房。");
                return;
            }

            _hostRestartInProgress = true;
            string? staleSaveKey = TryBuildFinishedSaveKey(shape);
            TaskHelper.RunSafely(HostRestartAsync(shape, host, _finishedScreenKind, staleSaveKey));
        }
        catch (Exception exception)
        {
            _hostRestartInProgress = false;
            Entry.Error($"new-run-button: could not start the restart: {exception}");
        }
    }

    private static async Task HostRestartAsync(
        Shape shape,
        NetHostGameService host,
        ScreenKind screenKind,
        string? staleSaveKey)
    {
        NSubmenu? screen = null;
        try
        {
            await TeardownToMainMenuAsync(host);

            NMainMenu menu = await WaitForMainMenuAsync();
            int maxPlayers = GetEffectiveMaxPlayers(shape);

            // Arm the daily filter *before* pushing: the daily screen starts its lobby setup synchronously inside
            // OnSubmenuOpened, and if the time-server task ever completes inline the admission would happen before
            // the next line of this method.
            if (screenKind == ScreenKind.Daily)
            {
                _filterDailyAdmit = true;
                _filterDailyAdmitService = host;
                _filterDailyAdmitDeadline = System.Environment.TickCount64 + DailyAdmitFilterWindowMs;
            }

            screen = PushHostScreen(menu, screenKind, host, maxPlayers);
            Entry.Info($"new-run-button: pushed {screen.GetType().Name} for {screenKind}.");

            if (screenKind != ScreenKind.Daily)
            {
                // Every screen except Daily creates its lobby inside InitializeMultiplayerAsHost; the Daily one
                // builds it later, after the time server answers, and admits peers itself — that admission is the
                // one the filter armed above replaces.
                StartRunLobby? lobby = FindLobby(screen);
                if (lobby == null)
                {
                    throw new InvalidOperationException(
                        $"{screen.GetType().Name} did not create a lobby; cannot admit the room.");
                }

                int admitted = AdmitPatchedPeers(shape, lobby, host);
                Entry.Info($"new-run-button: lobby rebuilt on the existing session (admitted={admitted}).");
            }

            RemoveStaleBinding(shape, staleSaveKey);

            Entry.Info(
                $"new-run-button: host lobby rebuilt on the existing session " +
                $"(mode={screenKind}, maxPlayers={maxPlayers}).");
        }
        catch (Exception exception)
        {
            Entry.Error($"new-run-button: host restart failed: {exception}");
            DisarmDailyFilter();
            // IsInProgress flips to false inside RunManager.CleanUp's finally. If it is still true the run was never
            // torn down, so the host is still on the game-over screen with a live session and the right recovery is
            // to leave everything alone; otherwise the only honest state is the one the main-menu button produces.
            RollbackHost(host, screen, tornDown: !RunManager.Instance.IsInProgress);
        }
        finally
        {
            _hostRestartInProgress = false;
            _finishedRun = null;
        }
    }

    /// <summary>
    /// Pushes the lobby screen that recreates the finished run, using the same calls the game's own host flows use.
    /// The Daily screen is the odd one out: it takes no player count and creates its lobby itself once the time
    /// server answers (see <c>NDailyRunScreen.SetupLobbyForHostOrSingleplayer</c>), so there is nothing to pass.
    /// </summary>
    private static NSubmenu PushHostScreen(
        NMainMenu menu,
        ScreenKind screenKind,
        NetHostGameService host,
        int maxPlayers)
    {
        NSubmenuStack stack = menu.SubmenuStack;
        switch (screenKind)
        {
            case ScreenKind.Custom:
            {
                NCustomRunScreen screen = stack.GetSubmenuType<NCustomRunScreen>();
                TryPreselectCustomGameMode(screen, _finishedRunMode);
                screen.InitializeMultiplayerAsHost(host, maxPlayers);
                stack.Push(screen);
                return screen;
            }
            case ScreenKind.Daily:
            {
                NDailyRunScreen screen = stack.GetSubmenuType<NDailyRunScreen>();
                screen.InitializeMultiplayerAsHost(host);
                stack.Push(screen);
                return screen;
            }
            case ScreenKind.Blitz:
            {
                NBlitzScreen screen = stack.GetSubmenuType<NBlitzScreen>();
                screen.InitializeMultiplayerAsHost(host, maxPlayers);
                stack.Push(screen);
                return screen;
            }
            case ScreenKind.Dread:
            {
                NDreadScreen screen = stack.GetSubmenuType<NDreadScreen>();
                screen.InitializeMultiplayerAsHost(host, maxPlayers);
                stack.Push(screen);
                return screen;
            }
            case ScreenKind.CharacterSelect:
            {
                NCharacterSelectScreen screen = stack.GetSubmenuType<NCharacterSelectScreen>();
                screen.InitializeMultiplayerAsHost(host, maxPlayers);
                stack.Push(screen);
                return screen;
            }
            default:
                throw new InvalidOperationException($"No lobby screen for {screenKind}.");
        }
    }

    /// <summary>
    /// Best effort: preselect the base game mode of a custom run so the rebuilt custom lobby starts in the same
    /// mode. The dropdown only needs to be set before <c>InitializeMultiplayerAsHost</c> reads it, and the host
    /// can still pick the mode in the screen, so a failure here must never abort. The two anchors are optional
    /// (a miss is reported once in <see cref="Apply"/>), and the success line is only written when the setter
    /// really ran.
    /// </summary>
    private static void TryPreselectCustomGameMode(NCustomRunScreen screen, GameMode? gameMode)
    {
        if (gameMode is not { } mode || mode == GameMode.None)
        {
            return;
        }

        FieldInfo? dropdownField = _shape?.GameModeDropdown;
        MethodInfo? setter = _shape?.SetSelectedGameMode;
        if (dropdownField == null || setter == null)
        {
            return;
        }

        try
        {
            if (dropdownField.GetValue(screen) is not { } dropdown)
            {
                Entry.Warn(
                    "new-run-button: the custom run screen has no game-mode dropdown instance, so the rebuilt " +
                    "lobby keeps the dropdown's default mode.");
                return;
            }

            setter.Invoke(dropdown, new object?[] { mode });
            Entry.Info($"new-run-button: custom lobby preselected game mode {mode}.");
        }
        catch (Exception exception)
        {
            Entry.Warn($"new-run-button: could not preselect the custom game mode: {exception.Message}");
        }
    }

    private static StartRunLobby? FindLobby(NSubmenu screen)
    {
        FieldInfo? field = screen.GetType().GetField("_lobby", BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(screen) as StartRunLobby;
    }

    /// <summary>
    /// One-shot replacement for the daily lobby's "admit everyone that connected before this lobby existed" step.
    /// It is only armed for the single restart that asked for it, and only for the lobby of the session that
    /// restart created; any other call (a normal daily host, another mode, a daily lobby built later, the native
    /// path taken by <see cref="AdmitPatchedPeers"/>) falls straight through to the game's own implementation.
    ///
    /// This is a Harmony *prefix*, so it must never throw: <c>StartRunLobby.AdmitClientsConnectedBeforeLobbyExisted</c>
    /// is a plain <c>void</c> the daily screen calls while it builds its lobby, and
    /// upstream does not catch. An escaping exception would therefore not "fail one admission" — it would abort
    /// the whole daily host flow. Every failure here degrades to <c>true</c>, i.e. the game's own admission,
    /// because admitting an unpatched peer (who then times out) is strictly better than a dead lobby.
    /// </summary>
    private static bool AdmitConnectedPeersPrefix(StartRunLobby __instance)
    {
        if (!_filterDailyAdmit)
        {
            return true;
        }

        if (System.Environment.TickCount64 > _filterDailyAdmitDeadline)
        {
            DisarmDailyFilter();
            Entry.Warn(
                "new-run-button: the daily admission filter expired; falling back to the game's own admission.");
            return true;
        }

        // The flag belongs to the one daily screen the restart pushed. Any other lobby (the player left that
        // screen before the time server answered, then hosted again) keeps the game's own admission.
        if (!ReferenceEquals(__instance.NetService, _filterDailyAdmitService))
        {
            DisarmDailyFilter();
            Entry.Info(
                "new-run-button: the daily admission filter belonged to another session; the game's own " +
                "admission runs.");
            return true;
        }

        // Disarm before filtering: AdmitPatchedPeers may call this very method for the all-patched case.
        DisarmDailyFilter();

        try
        {
            if (__instance.NetService is not NetHostGameService host)
            {
                return true;
            }

            Shape shape = _shape
                ?? throw new InvalidOperationException("new-run-button: shape was not captured.");
            int admitted = AdmitPatchedPeers(shape, __instance, host);
            Entry.Info(
                $"new-run-button: daily lobby admitted {admitted} patched peer(s); the rest stay on their own screen.");
            return false;
        }
        catch (Exception exception)
        {
            // Keep the filter disarmed (already done above) so a later call cannot re-enter the same failure,
            // and let the game admit everyone: that is what it would have done without this patch.
            DisarmDailyFilter();
            Entry.Error(
                "new-run-button: the filtered daily admission failed, falling back to the game's own " +
                $"admission: {Describe(exception)}");
            return true;
        }
    }

    private static void DisarmDailyFilter()
    {
        _filterDailyAdmit = false;
        _filterDailyAdmitService = null;
    }

    /// <summary>
    /// Admits the peers that can come back on their own.
    ///
    /// The game's <c>AdmitClientsConnectedBeforeLobbyExisted()</c> is public but admits **every** connected peer
    /// unconditionally, which would put an unpatched client on the ten-second
    /// <c>ClientResponseTimeout</c> and end with <c>DisconnectClient(LobbyJoinTimeout)</c> — a surprise kick.
    /// Filtering therefore has to go through the per-peer private <c>OnConnectedToClientAsHost</c>. When every
    /// peer is patched there is nothing to filter, so the native method is used.
    /// </summary>
    private static int AdmitPatchedPeers(Shape shape, StartRunLobby lobby, NetHostGameService host)
    {
        List<ulong> patched = new();
        int total = 0;

        foreach (var peer in host.ConnectedPeers)
        {
            total++;
            PeerVersionInfo? info = host.GetVersionInfoForPeer(peer.peerId);
            if (info == null || !HasOurMod(info.Value))
            {
                continue;
            }

            patched.Add(peer.peerId);
        }

        if (patched.Count == 0)
        {
            return 0;
        }

        if (patched.Count == total)
        {
            lobby.AdmitClientsConnectedBeforeLobbyExisted();
            return patched.Count;
        }

        int admitted = 0;
        int failed = 0;
        foreach (ulong peerId in patched)
        {
            // The call goes through MethodInfo.Invoke, so failures arrive wrapped in a
            // TargetInvocationException; unwrap it or the log only ever shows the wrapper. A failure is
            // counted instead of being swallowed, because this method's return value is what the caller
            // reports as "admitted N" — a count that includes a peer that was never admitted would be a
            // log that claims success without effect.
            try
            {
                shape.AdmitPeer.Invoke(lobby, new object?[] { peerId });
                admitted++;
            }
            catch (Exception exception)
            {
                failed++;
                Entry.Warn($"new-run-button: admitting peer {peerId} failed: {Describe(exception)}");
            }
        }

        Entry.Info(
            $"new-run-button: admitted {admitted}/{total} peers individually (failed={failed}); the rest " +
            "stay on their game-over screen by design.");
        return admitted;
    }

    /// <summary>
    /// Renders an exception for the log, unwrapping the <see cref="TargetInvocationException"/> that every
    /// <see cref="MethodInfo.Invoke(object?, object?[])"/> call adds. Without this the log only ever says
    /// "Exception has been thrown by the target of an invocation" and the real cause is lost — which is exactly
    /// how the field log lost the ENet send failure behind the 2026-11-13 admission errors.
    /// </summary>
    private static string Describe(Exception exception) =>
        exception is TargetInvocationException { InnerException: { } inner }
            ? $"{inner.GetType().Name}: {inner.Message}"
            : $"{exception.GetType().Name}: {exception.Message}";

    /// <summary>
    /// Restores the "main-menu button was pressed" state. The game-over screen is already gone at this point (the
    /// teardown cleared the overlay stack), so the only honest rollback is the one the player would have gotten by
    /// pressing the main-menu button: a clean disconnect, which also detaches the mod's host origin and lets the
    /// room disappear from the lobby list the normal way.
    /// </summary>
    private static void RollbackHost(NetHostGameService host, NSubmenu? screen, bool tornDown)
    {
        if (!tornDown)
        {
            // The run is still loaded: the host keeps the game-over screen and can press the main-menu button.
            Entry.Warn("new-run-button: restart aborted before the teardown; session and screen are untouched.");
            ShowInfo("新开一局失败，本局界面已保留。可以点「主菜单」正常退出。");
            return;
        }

        bool cleanedUp = false;
        try
        {
            if (screen != null && GodotObject.IsInstanceValid(screen))
            {
                NMainMenu? menu = NGame.Instance?.MainMenu;
                if (menu != null && screen.IsInsideTree() && ReferenceEquals(menu.SubmenuStack.Peek(), screen))
                {
                    // Every lobby screen overrides OnSubmenuClosed to call CleanUpLobby(disconnectSession: true)
                    // (NCharacterSelectScreen:669, NDailyRunScreen:366, NCustomRunScreen:529, NBlitzScreen:604,
                    // NDreadScreen:718), so Pop is exactly the back-button path for all of them. Doing the lobby
                    // cleanup by hand as well would double dispose the lobby's input synchronizer.
                    menu.SubmenuStack.Pop();
                    cleanedUp = true;
                }
            }
        }
        catch (Exception exception)
        {
            Entry.Warn($"new-run-button: rollback cleanup failed: {exception.Message}");
        }

        if (!cleanedUp)
        {
            try
            {
                if (host.IsConnected)
                {
                    host.Disconnect(NetError.Quit);
                }
            }
            catch (Exception exception)
            {
                Entry.Warn($"new-run-button: rollback disconnect failed: {exception.Message}");
            }
        }

        ShowInfo("新开一局失败，已退出本局。请重新建房。");
    }

    private static string? TryBuildFinishedSaveKey(Shape shape)
    {
        if (_finishedRun == null)
        {
            return null;
        }

        try
        {
            return shape.BuildSaveKey.Invoke(null, new object?[] { _finishedRun }) as string;
        }
        catch (Exception exception)
        {
            Entry.Warn($"new-run-button: could not rebuild the finished run's save key: {exception.Message}");
            return null;
        }
    }

    /// <summary>
    /// Drops the room binding of the finished run. The save itself is already gone (<c>RunManager.OnEnded</c>
    /// deletes it), so the binding is dead weight that would otherwise linger in the mod's config forever.
    /// Best effort on purpose: a stale entry must never block starting a new run.
    /// </summary>
    private static void RemoveStaleBinding(Shape shape, string? staleSaveKey)
    {
        if (string.IsNullOrWhiteSpace(staleSaveKey))
        {
            return;
        }

        try
        {
            object? removed = shape.RemoveSaveRoomBinding.Invoke(null, new object?[] { staleSaveKey });
            Entry.Info($"new-run-button: stale room binding removed={removed} saveKey={staleSaveKey}");
        }
        catch (Exception exception)
        {
            Entry.Warn($"new-run-button: could not remove the stale room binding: {exception.Message}");
        }
    }

    private static int GetEffectiveMaxPlayers(Shape shape)
    {
        // A missing or renamed host helper must not turn "how many players may join" into a failed restart: the whole
        // rebuild is a best-effort operation and 4 is the game's own default. Before this was wrapped, a throwing
        // Invoke escalated a degradable value into RollbackHost (which disconnects the room).
        try
        {
            object? value = shape.GetEffectiveMaxPlayers.Invoke(null, null);
            int resolved = value is int number ? number : 4;
            // LanConnectConstants.ProtocolMinPlayers .. ProtocolMaxPlayers; the host flow clamps the same way.
            return Math.Clamp(resolved, 2, 8);
        }
        catch (Exception exception)
        {
            Entry.Warn(
                $"new-run-button: could not read the host mod's effective max players ({Describe(exception)}); " +
                "using 4.");
            return 4;
        }
    }

    private static void HandshakeSucceededPostfix(NetClientGameService __instance, ulong peerId, PeerVersionInfo versionInfo)
    {
        try
        {
            bool hasPatch = HasOurMod(versionInfo);
            _hostHasPatch = hasPatch;
            RegisterClientHandlers(__instance);
            Entry.Info(
                $"new-run-button: host mod list captured (hostHasPatch={hasPatch}, " +
                $"otherMods=[{string.Join(",", versionInfo.otherMods ?? new List<string>())}]).");
        }
        catch (Exception exception)
        {
            Entry.Warn($"new-run-button: could not capture the host's mod list: {exception.Message}");
        }
    }

    private static void RegisterClientHandlers(INetGameService service)
    {
        if (ReferenceEquals(_handlersRegisteredFor, service))
        {
            return;
        }

        // Bind the handler to the session that owns it instead of letting it read the static slot back.
        //
        // The slot is a single field that is also the de-duplication key, so after a reconnect it points at the new
        // session while the previous session's handlers are still registered on the previous session: a message
        // arriving there used to be answered on the *new* session. Capturing the instance removes the shared mutable
        // state — the handler can only ever answer on the session that received the message. Same rule as this
        // fix's own <c>HandshakeSucceededPostfix</c>: it receives <c>__instance</c> as a parameter and passes that
        // instance to <c>RegisterClientHandlers</c>, never the static slot.
        INetGameService session = service;
        service.RegisterMessageHandler<InitialGameInfoMessage>(
            (message, sender) => OnInitialGameInfoMessage(session, message, sender));
        service.RegisterMessageHandler<ClientLobbyJoinResponseMessage>(OnJoinResponseMessage);
        _handlersRegisteredFor = service;
    }

    /// <summary>
    /// Handles the host's "the room is reassembling into a new lobby" announcement.
    ///
    /// <paramref name="service"/> is the session this handler was registered on — passed in, never read back from a
    /// static field, so the answer always goes out on the session that actually received the message.
    /// </summary>
    private static void OnInitialGameInfoMessage(
        INetGameService service,
        InitialGameInfoMessage message,
        ulong senderId)
    {
        try
        {
            if (message.sessionState != RunSessionState.InLobby)
            {
                return;
            }

            // Guard #1: only a client that is sitting on a finished multiplayer run's game-over screen may react.
            // Anything else (a normal join, a running game, a stale screen) is ignored with one info line — never
            // an error, because the vanilla join flow registers its own handler for this very message.
            if (!IsAwaitingHostReassemble())
            {
                Entry.Info("new-run-button: ignoring InitialGameInfoMessage (not waiting for a reassemble).");
                return;
            }

            BeginClientReassemble(service);
        }
        catch (Exception exception)
        {
            Entry.Error($"new-run-button: failed to handle the reassemble request: {exception}");
        }
    }

    private static void OnJoinResponseMessage(ClientLobbyJoinResponseMessage message, ulong senderId)
    {
        if (!_reassemblePending)
        {
            Entry.Info("new-run-button: ignoring ClientLobbyJoinResponseMessage (no reassemble pending).");
            return;
        }

        _joinResponse?.TrySetResult(message);
    }

    private static bool IsAwaitingHostReassemble()
    {
        if (_reassemblePending || !_gameOverScreenVisible)
        {
            return false;
        }

        RunManager manager = RunManager.Instance;
        if (!manager.IsInProgress || !manager.HasRunEnded)
        {
            // The screen is gone (or the run was already cleaned up): drop the flag so a later session cannot
            // accidentally match it.
            _gameOverScreenVisible = false;
            return false;
        }

        return manager.NetService?.Type == NetGameType.Client;
    }

    private static void BeginClientReassemble(INetGameService service)
    {
        _reassemblePending = true;
        _joinResponse = new TaskCompletionSource<ClientLobbyJoinResponseMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // Answer before tearing anything down: StartRunLobby.ClientResponseTimeout is ten seconds and a teardown
        // plus a main-menu load can take a couple of them. The 0.113 message carries the three fields below —
        // the removed 0.111 maxAscensionUnlocked must not be used here.
        UnlockState unlockState = SaveManager.Instance.GenerateUnlockStateFromProgress();

        // This reassemble reuses the session, so the Tail binding (and its roster authority) survives the room
        // rebuild. The host's answer to the request below is a ClientLobbyJoinResponseMessage, which
        // RestoreStartRunResponse turns into AcceptRoster(..., Bootstrap), but AcceptRoster ignores the hint
        // once an epoch is accepted and validates as CurrentState instead, demanding a byte-for-byte repeat of
        // the finished run's StartRunLobbyPlayer snapshot. Drop the epoch first, strictly before the send.
        // Non-fatal by design: a failed reset must never abort the reassemble.
        if (TailRoster.Reset(service, "before the reassemble join request"))
        {
            Entry.Info(
                "new-run-button: dropped the accepted Tail roster epoch on this session " +
                "(before the reassemble join request).");
        }

        service.SendMessage(new ClientLobbyJoinRequestMessage
        {
            unlockState = unlockState.ToSerializable(),
            maxStandardAscension = SaveManager.Instance.Progress.MaxMultiplayerAscension,
            maxBlitzAscension = SaveManager.Instance.Progress.MaxBlitzAscension
        });

        Entry.Info("new-run-button: host asked the room to reassemble; join request sent, tearing the run down.");
        TaskHelper.RunSafely(ClientReassembleAsync(service));
    }

    private static async Task ClientReassembleAsync(INetGameService service)
    {
        try
        {
            await TeardownToMainMenuAsync(service);

            ClientLobbyJoinResponseMessage response = await AwaitJoinResponseAsync(service);

            NMainMenu menu = await WaitForMainMenuAsync();
            PushClientScreen(menu, response, service);

            Entry.Info(
                $"new-run-button: client returned to the host's lobby (mode={response.gameMode}, " +
                $"isCustom={response.isCustom}).");
        }
        catch (Exception exception)
        {
            Entry.Error($"new-run-button: client reassemble failed: {exception}");

            // Everything below this point goes through reflection, so the interesting failure can arrive
            // wrapped in a TargetInvocationException whose own message is the useless
            // "Exception has been thrown by the target of an invocation" (2026-11-13 field log). Print the
            // real cause separately instead of leaving it inside the wrapper.
            if (exception is TargetInvocationException { InnerException: { } inner })
            {
                Entry.Error(
                    "new-run-button: client reassemble failed, real cause: " +
                    $"{inner.GetType().Name}: {inner.Message}");
            }

            RollbackClient(service, tornDown: !RunManager.Instance.IsInProgress);
        }
        finally
        {
            _reassemblePending = false;
            _joinResponse = null;
            _gameOverScreenVisible = false;
        }
    }

    private static async Task<ClientLobbyJoinResponseMessage> AwaitJoinResponseAsync(INetGameService service)
    {
        TaskCompletionSource<ClientLobbyJoinResponseMessage> completion = _joinResponse
            ?? throw new InvalidOperationException("new-run-button: no join response is pending.");
        Task<ClientLobbyJoinResponseMessage> pending = completion.Task;
        SceneTree? tree = Engine.GetMainLoop() as SceneTree;
        long deadline = System.Environment.TickCount64 + JoinResponseTimeoutMs;

        while (!pending.IsCompleted)
        {
            if (System.Environment.TickCount64 > deadline)
            {
                throw new TimeoutException(
                    $"the host did not answer the reassemble join request within {JoinResponseTimeoutMs} ms.");
            }

            try
            {
                // Nobody else pumps the session while we sit on the main menu waiting for the lobby response.
                service.Update();
            }
            catch (Exception exception)
            {
                Entry.Warn($"new-run-button: session update failed while waiting for the lobby: {exception.Message}");
            }

            if (tree != null)
            {
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }
            else
            {
                await Task.Delay(16);
            }
        }

        return await pending;
    }

    /// <summary>
    /// Mirrors <c>NJoinFriendScreen.JoinGameAsync</c>'s lobby dispatch. The host's
    /// <c>ClientLobbyJoinResponseMessage</c> is the authoritative description of the lobby — custom first, then the
    /// game mode — so the client never guesses from its own (already torn down) run state and can never disagree
    /// with the host about which screen to show.
    /// </summary>
    private static void PushClientScreen(
        NMainMenu menu,
        ClientLobbyJoinResponseMessage response,
        INetGameService service)
    {
        NSubmenuStack stack = menu.SubmenuStack;

        if (response.isCustom)
        {
            NCustomRunScreen custom = stack.GetSubmenuType<NCustomRunScreen>();
            custom.InitializeMultiplayerAsClient(service, response);
            stack.Push(custom);
            return;
        }

        switch (response.gameMode)
        {
            case GameMode.Standard:
            {
                NCharacterSelectScreen screen = stack.GetSubmenuType<NCharacterSelectScreen>();
                screen.InitializeMultiplayerAsClient(service, response);
                stack.Push(screen);
                return;
            }
            case GameMode.Daily:
            {
                NDailyRunScreen screen = stack.GetSubmenuType<NDailyRunScreen>();
                screen.InitializeMultiplayerAsClient(service, response);
                stack.Push(screen);
                return;
            }
            case GameMode.Blitz:
            {
                NBlitzScreen screen = stack.GetSubmenuType<NBlitzScreen>();
                screen.InitializeMultiplayerAsClient(service, response);
                stack.Push(screen);
                return;
            }
            case GameMode.Dread:
            {
                NDreadScreen screen = stack.GetSubmenuType<NDreadScreen>();
                screen.InitializeMultiplayerAsClient(service, response);
                stack.Push(screen);
                return;
            }
            default:
                // The game throws ArgumentOutOfRangeException here. A readable failure plus the normal rollback is
                // better than crashing the client, and it keeps the "unknown mode" promise of the button side.
                throw new NotSupportedException(
                    $"the host's lobby reports an unknown game mode ({response.gameMode}).");
        }
    }

    private static void RollbackClient(INetGameService service, bool tornDown)
    {
        if (!tornDown)
        {
            Entry.Warn("new-run-button: reassemble aborted before the teardown; the session is untouched.");
            ShowInfo("返回准备页失败，本局界面已保留。可以点「主菜单」正常退出。");
            return;
        }

        try
        {
            if (service.IsConnected)
            {
                service.Disconnect(NetError.Quit);
            }
        }
        catch (Exception exception)
        {
            Entry.Warn($"new-run-button: client rollback disconnect failed: {exception.Message}");
        }

        ShowInfo("返回准备页失败，已退出本局。请重新加入房间。");
    }

    /// <summary>
    /// Tears the finished run down the way the main-menu button does, but keeps the socket: the one
    /// <c>NetService.Disconnect</c> issued by <c>RunManager.CleanUp</c> is suppressed for exactly this call.
    /// The flag is cleared in <c>finally</c>, so a failed teardown cannot leave the suppression behind.
    /// </summary>
    private static async Task TeardownToMainMenuAsync(INetGameService service)
    {
        NGame game = NGame.Instance ?? throw new InvalidOperationException("NGame instance is unavailable.");

        _suppressedService = service;
        _suppressDisconnect = true;
        try
        {
            // ReturnToMainMenu = FadeOut -> LoadCommonAndMainMenuAssets -> RunManager.CleanUp -> LoadMainMenu.
            await game.ReturnToMainMenu();
        }
        finally
        {
            _suppressDisconnect = false;
            _suppressedService = null;
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

    private static void SetHintText(string text)
    {
        try
        {
            SceneTree? tree = Engine.GetMainLoop() as SceneTree;
            if (tree?.Root?.FindChild(HintNodeName, recursive: true, owned: false) is Label label)
            {
                label.Text = text;
            }
        }
        catch (Exception exception)
        {
            Entry.Warn($"new-run-button: could not update the hint label: {exception.Message}");
        }
    }

    /// <summary>
    /// Refuses a restart in a way the player can see. A withdrawn button plus a popup that names the reason is the
    /// whole point: the previous build logged a warning and left the player staring at an unchanged screen.
    /// </summary>
    private static void RejectRestart(string message)
    {
        // Same reason in both places: a stale caption next to a popup that contradicts it is how the screen
        // ends up telling the player two different stories.
        WithdrawHostControls(message);
        ShowInfo(message);
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
            Entry.Warn($"new-run-button: could not show the result popup: {exception.Message}");
        }
    }

    /// <summary>
    /// Maps a finished run onto the lobby screen that can recreate it, using the authoritative run state:
    /// custom runs are decided by <c>IsCustomGame</c> first, then by <c>GameMode</c>. Only an unknown mode
    /// (<c>GameMode.None</c>) is unsupported; it keeps the button hidden instead of pushing a wrong screen.
    ///
    /// Note for Dread: at the end of a run <c>RunState.GameMode</c> is the real <c>GameMode.Dread</c>, because
    /// <c>NDreadScreen</c> builds its run with that value itself — the lobby-service sentinel used when hosting
    /// never reaches the run state, so matching on the enum is correct here.
    /// </summary>
    private static ScreenKind ResolveScreenKind(RunState? state)
    {
        if (state == null)
        {
            return ScreenKind.Unsupported;
        }

        if (state.IsCustomGame)
        {
            return ScreenKind.Custom;
        }

        return state.GameMode switch
        {
            GameMode.Standard => ScreenKind.CharacterSelect,
            GameMode.Daily => ScreenKind.Daily,
            GameMode.Blitz => ScreenKind.Blitz,
            GameMode.Dread => ScreenKind.Dread,
            _ => ScreenKind.Unsupported
        };
    }

    private static PeerCounts CountPeers(INetGameService service)
    {
        if (service is not NetHostGameService host)
        {
            return default;
        }

        int total = 0;
        int patched = 0;
        int unknown = 0;

        foreach (var peer in host.ConnectedPeers)
        {
            total++;
            PeerVersionInfo? info = host.GetVersionInfoForPeer(peer.peerId);
            if (info == null)
            {
                // Never guess: a peer whose handshake data is missing counts as unknown, not as patched.
                unknown++;
                continue;
            }

            if (HasOurMod(info.Value))
            {
                patched++;
            }
        }

        return new PeerCounts(total, patched, unknown);
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
    /// Drops exactly one <c>NetService.Disconnect</c> — the one issued by <c>RunManager.CleanUp</c> while we are
    /// deliberately keeping the session. Everything else (a real quit, a kick, the rollback) passes through.
    ///
    /// The <c>NetError</c> filter is load-bearing, not defensive. While the teardown runs, the host mod's Tail runtime
    /// can reject an inbound frame on this very service and disconnect with <c>ModMismatch</c>. Swallowing that one
    /// leaves the session half torn down: the Tail binding
    /// is still registered, ENet is still up, and nothing ever reports the rejection. Only the teardown's own
    /// <c>Quit</c> may be dropped. Field log 2026-11-13 566639 → 566660/566679 is the sequence that taught it.
    ///
    /// Both prefixes therefore take the reason: the game's signature is
    /// <c>Disconnect(NetError reason, bool now = false)</c>, and <see cref="Apply"/> binds it by exact parameter types.
    /// </summary>
    private static bool HostDisconnectPrefix(NetHostGameService __instance, NetError reason) =>
        ShouldKeepSession(__instance, reason);

    private static bool ClientDisconnectPrefix(NetClientGameService __instance, NetError reason) =>
        ShouldKeepSession(__instance, reason);

    private static bool ShouldKeepSession(object instance, NetError reason)
    {
        if (!_suppressDisconnect || !ReferenceEquals(instance, _suppressedService))
        {
            return true;
        }

        if (reason != NetError.Quit)
        {
            Entry.Warn(
                $"new-run-button: letting the teardown's {reason} disconnect through instead of suppressing it.");
            return true;
        }

        Entry.Info("new-run-button: suppressed the finished run's teardown disconnect; the session stays up.");
        return false;
    }

    private readonly record struct PeerCounts(int Total, int Patched, int Unknown);

    private const string GameOverScreenTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen.NGameOverScreen";
    private const string ReturnToMainMenuButtonTypeName =
        "MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen.NReturnToMainMenuButton";
    private const string ButtonTypeName = "MegaCrit.Sts2.Core.Nodes.GodotExtensions.NButton";
    private const string ClickableControlTypeName = "MegaCrit.Sts2.Core.Nodes.GodotExtensions.NClickableControl";
    private const string StartRunLobbyTypeName = "MegaCrit.Sts2.Core.Multiplayer.Game.Lobby.StartRunLobby";
    private const string NetHostGameServiceTypeName = "MegaCrit.Sts2.Core.Multiplayer.NetHostGameService";
    private const string NetClientGameServiceTypeName = "MegaCrit.Sts2.Core.Multiplayer.NetClientGameService";
    private const string PostRunResultTypeName = "MegaCrit.Sts2.Core.Entities.Save.PostRunProgressUpdateResult";
    private const string RunStateTypeName = "MegaCrit.Sts2.Core.Runs.RunState";
    private const string GameModeTypeName = "MegaCrit.Sts2.Core.Runs.GameMode";
    private const string MegaLabelTypeName = "MegaCrit.Sts2.addons.mega_text.MegaLabel";
    private const string NSubmenuTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NSubmenu";
    private const string NMainMenuTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu";
    private const string NSubmenuStackTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NSubmenuStack";
    private const string CharacterSelectScreenTypeName =
        "MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectScreen";
    private const string DailyRunScreenTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.DailyRun.NDailyRunScreen";
    private const string CustomRunScreenTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.CustomRun.NCustomRunScreen";
    private const string BlitzScreenTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.Blitz.NBlitzScreen";
    private const string DreadScreenTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.Dread.NDreadScreen";
    private const string ErrorPopupTypeName = "MegaCrit.Sts2.Core.Nodes.CommonUi.NErrorPopup";
    private const string ModalContainerTypeName = "MegaCrit.Sts2.Core.Nodes.CommonUi.NModalContainer";
    private const string ControlTypeName = "Godot.Control";

    /// <summary>
    /// Decides whether this repair can be installed at all, without throwing and without touching the engine.
    ///
    /// Rules this method obeys, each for a concrete reason:
    /// <list type="bullet">
    /// <item>Every game type is looked up by *name* (<see cref="FindType"/>), never with <c>typeof</c>. A
    /// <c>typeof</c> of a type that a future game build renamed makes this very method fail to JIT, which would turn
    /// "one anchor moved" into "the probe itself is broken".</item>
    /// <item>No <c>Invoke</c>, no <c>Activator</c>, no Godot node, no file system. Resolving a
    /// <see cref="MethodInfo"/> does not JIT the target, so probing is safe before the game has created anything.</item>
    /// <item>Every missing anchor is collected, not just the first one: one log line has to say what to fix.</item>
    /// <item>Nothing is thrown to the caller. The host treats an exception as "this repair failed" and rolls
    /// back this repair's group, which for the new-run group means both of its repairs: a missing anchor must
    /// not cost that.</item>
    /// </list>
    /// </summary>
    private static bool TryCapture(
        Assembly host,
        out Shape? shape,
        out PatchTargets targets,
        out string missing)
    {
        List<string> problems = new();

        Type? compatibility = ReflectScope.HostType(host, CompatibilityTypeName, problems);
        MethodInfo? getMaxPlayers = ReflectScope.StaticMethod(problems, compatibility, "GetEffectiveMaxPlayers");

        Type? bindingType = ReflectScope.HostType(host, BindingTypeName, problems);
        MethodInfo? buildSaveKey = ReflectScope.StaticMethod(
            problems,
            bindingType,
            "BuildSaveKey",
            "MegaCrit.Sts2.Core.Saves.SerializableRun");

        Type? configType = ReflectScope.HostType(host, ConfigTypeName, problems);
        MethodInfo? removeBinding = ReflectScope.StaticMethod(problems, configType, "RemoveSaveRoomBinding", "System.String");

        Type? screenType = ReflectScope.GameType(GameOverScreenTypeName, problems);
        FieldInfo? mainMenuButton = ReflectScope.Field(problems, screenType, "_mainMenuButton");
        FieldInfo? resultField = ReflectScope.Field(problems, screenType, "_result");
        FieldInfo? runStateField = ReflectScope.Field(problems, screenType, "_runState");
        FieldInfo? uiNode = ReflectScope.Field(problems, screenType, "_uiNode");

        if (mainMenuButton != null && mainMenuButton.FieldType.FullName != ReturnToMainMenuButtonTypeName)
        {
            problems.Add(
                $"{GameOverScreenTypeName}._mainMenuButton is {mainMenuButton.FieldType.Name}, expected " +
                "NReturnToMainMenuButton");
        }

        if (resultField != null && resultField.FieldType.FullName != PostRunResultTypeName)
        {
            problems.Add(
                $"{GameOverScreenTypeName}._result is {resultField.FieldType.Name}, expected " +
                "PostRunProgressUpdateResult");
        }

        if (runStateField != null && runStateField.FieldType.FullName != RunStateTypeName)
        {
            problems.Add($"{GameOverScreenTypeName}._runState is {runStateField.FieldType.Name}, expected RunState");
        }

        if (uiNode != null && !InheritsFrom(uiNode.FieldType, ControlTypeName))
        {
            problems.Add($"{GameOverScreenTypeName}._uiNode is {uiNode.FieldType.Name}, expected a Control");
        }

        MethodInfo? ready = ReflectScope.InstanceMethod(problems, screenType, "_Ready");
        MethodInfo? openSummary = ReflectScope.InstanceMethod(
            problems,
            screenType,
            "OpenSummaryScreen",
            "MegaCrit.Sts2.Core.Nodes.GodotExtensions.NButton");
        MethodInfo? hideSummary = ReflectScope.InstanceMethod(problems, screenType, "HideSummary");

        Type? buttonType = ReflectScope.GameType(ReturnToMainMenuButtonTypeName, problems);
        FieldInfo? showPosition = ReflectScope.Field(problems, buttonType, "_showPosition");
        FieldInfo? shaderMaterial = ReflectScope.Field(problems, buttonType, "_hsv");

        if (showPosition != null && showPosition.FieldType.FullName != "Godot.Vector2")
        {
            problems.Add(
                $"{ReturnToMainMenuButtonTypeName}._showPosition is {showPosition.FieldType.Name}, expected Vector2");
        }

        if (shaderMaterial != null && shaderMaterial.FieldType.FullName != "Godot.ShaderMaterial")
        {
            problems.Add(
                $"{ReturnToMainMenuButtonTypeName}._hsv is {shaderMaterial.FieldType.Name}, expected ShaderMaterial");
        }

        Type? nButtonType = ReflectScope.GameType(ButtonTypeName, problems);
        MethodInfo? unregisterHotkeys = ReflectScope.InstanceMethod(problems, nButtonType, "UnregisterHotkeys");

        Type? lobbyType = ReflectScope.GameType(StartRunLobbyTypeName, problems);
        MethodInfo? admitPeer = ReflectScope.InstanceMethod(problems, lobbyType, "OnConnectedToClientAsHost", "System.UInt64");
        MethodInfo? admitClients = ReflectScope.InstanceMethod(
            problems,
            lobbyType,
            "AdmitClientsConnectedBeforeLobbyExisted");

        if (admitPeer != null && admitPeer.ReturnType.FullName != "System.Void")
        {
            problems.Add($"{StartRunLobbyTypeName}.OnConnectedToClientAsHost no longer returns void");
        }

        Type? hostService = ReflectScope.GameType(NetHostGameServiceTypeName, problems);
        MethodInfo? hostDisconnect = ReflectScope.InstanceMethod(
            problems,
            hostService,
            "Disconnect",
            "MegaCrit.Sts2.Core.Entities.Multiplayer.NetError",
            "System.Boolean");

        Type? clientService = ReflectScope.GameType(NetClientGameServiceTypeName, problems);
        MethodInfo? clientDisconnect = ReflectScope.InstanceMethod(
            problems,
            clientService,
            "Disconnect",
            "MegaCrit.Sts2.Core.Entities.Multiplayer.NetError",
            "System.Boolean");
        MethodInfo? handshake = ReflectScope.InstanceMethod(
            problems,
            clientService,
            "HandshakeSucceeded",
            "System.UInt64",
            "MegaCrit.Sts2.Core.Multiplayer.PeerVersionInfo");

        // The prefixes/postfixes below take these parameters by name; a rename would break the binding silently.
        ReflectScope.RequireParameterNames(problems, hostDisconnect, "reason");
        ReflectScope.RequireParameterNames(problems, clientDisconnect, "reason");
        ReflectScope.RequireParameterNames(problems, handshake, "peerId", "versionInfo");

        //
        // These are the ones no "install-time" check would otherwise catch: they appear in handler bodies, so a rename
        // surfaces as a TypeLoadException inside the game's own method the first time the handler runs, where a
        // try/catch inside the handler cannot help (parameter types are resolved when the method is JITted).
        // FindLobby reads the private _lobby field off whichever screen the host is on. A rename there would not
        // throw — FindLobby returns null, the repair throws "did not create a lobby", and the failure path tears
        // the whole room down. That is a far worse outcome than the feature being absent, so the field is part of
        // the shape: if it is gone we simply do not install.
        RequireLobbyField(ReflectScope.GameType(CharacterSelectScreenTypeName, problems), problems);
        RequireLobbyField(ReflectScope.GameType(DailyRunScreenTypeName, problems), problems);
        Type? customScreen = ReflectScope.GameType(CustomRunScreenTypeName, problems);
        RequireLobbyField(customScreen, problems);
        RequireLobbyField(ReflectScope.GameType(BlitzScreenTypeName, problems), problems);
        RequireLobbyField(ReflectScope.GameType(DreadScreenTypeName, problems), problems);
        ReflectScope.GameType(MegaLabelTypeName, problems);
        ReflectScope.GameType(NSubmenuTypeName, problems);
        ReflectScope.GameType(NMainMenuTypeName, problems);
        ReflectScope.GameType(NSubmenuStackTypeName, problems);
        ReflectScope.GameType(PostRunResultTypeName, problems);
        ReflectScope.GameType(RunStateTypeName, problems);
        ReflectScope.GameType(ErrorPopupTypeName, problems);
        ReflectScope.GameType(ModalContainerTypeName, problems);

        Type? clickable = ReflectScope.GameType(ClickableControlTypeName, problems);
        if (clickable != null)
        {
            Type? signalNames = clickable.GetNestedType("SignalName", BindingFlags.Public | BindingFlags.NonPublic);
            if (signalNames == null)
            {
                problems.Add($"{ClickableControlTypeName}.SignalName (nested type)");
            }
            else
            {
                ReflectScope.StaticField(problems, signalNames, "Released");
            }
        }

        Type? gameMode = ReflectScope.GameType(GameModeTypeName, problems);
        if (gameMode != null)
        {
            // Enum.GetNames throws on a non-enum, so the kind check comes first: the probe must never throw.
            if (!gameMode.IsEnum)
            {
                problems.Add($"{GameModeTypeName} is no longer an enum");
            }
            else if (!HasEnumNames(gameMode, "None", "Standard", "Daily", "Blitz", "Dread"))
            {
                problems.Add($"{GameModeTypeName} lost one of None/Standard/Daily/Blitz/Dread");
            }
        }

        // Optional: preselecting the finished custom run's mode. Losing either anchor costs that preselection
        // only, so neither joins the required set; Apply reports the loss once.
        var preselectProblems = new List<string>();
        FieldInfo? gameModeDropdown = ReflectScope.Field(preselectProblems, customScreen, "_gameModeDropdown");
        MethodInfo? setSelectedGameMode = gameModeDropdown == null
            ? null
            : ReflectScope.InstanceMethod(
                preselectProblems,
                gameModeDropdown.FieldType,
                "SetSelectedGameMode",
                GameModeTypeName);
        if (setSelectedGameMode == null)
        {
            gameModeDropdown = null;
        }

        if (problems.Count > 0
            || getMaxPlayers == null
            || buildSaveKey == null
            || removeBinding == null
            || mainMenuButton == null
            || resultField == null
            || runStateField == null
            || uiNode == null
            || ready == null
            || openSummary == null
            || hideSummary == null
            || showPosition == null
            || shaderMaterial == null
            || unregisterHotkeys == null
            || admitPeer == null
            || admitClients == null
            || hostDisconnect == null
            || clientDisconnect == null
            || handshake == null)
        {
            shape = null;
            targets = default;
            missing = problems.Count > 0 ? string.Join("; ", problems) : "an anchor resolved to null";
            return false;
        }

        shape = new Shape(
            mainMenuButton,
            resultField,
            runStateField,
            uiNode,
            showPosition,
            shaderMaterial,
            admitPeer,
            getMaxPlayers,
            buildSaveKey,
            removeBinding,
            unregisterHotkeys,
            gameModeDropdown,
            setSelectedGameMode);

        targets = new PatchTargets(
            ready,
            openSummary,
            hideSummary,
            hostDisconnect,
            clientDisconnect,
            handshake,
            admitClients);
        missing = string.Empty;
        return true;
    }

    /// <summary>Methods this repair patches, install-time only</summary>
    private readonly record struct PatchTargets(
        MethodInfo GameOverReady,
        MethodInfo OpenSummary,
        MethodInfo HideSummary,
        MethodInfo HostDisconnect,
        MethodInfo ClientDisconnect,
        MethodInfo HandshakeSucceeded,
        MethodInfo AdmitClientsConnected);

    /// <summary>
    /// Verifies a lobby screen still declares the private <c>_lobby</c> field <see cref="FindLobby"/> reads.
    /// A renamed field is silent at read time (FindLobby returns null), and the failure path for a missing lobby
    /// tears the whole room down — so the field is part of the shape, not an implementation detail.
    /// </summary>
    private static void RequireLobbyField(Type? screen, List<string> problems)
    {
        if (screen == null)
        {
            return;
        }

        if (screen.GetField("_lobby", BindingFlags.Instance | BindingFlags.NonPublic) == null)
        {
            problems.Add($"{(screen.FullName ?? screen.Name)}._lobby (StartRunLobby field)");
        }
    }

    private static bool InheritsFrom(Type? type, string baseTypeFullName)
    {
        for (Type? current = type; current != null; current = current.BaseType)
        {
            if (string.Equals(current.FullName, baseTypeFullName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasEnumNames(Type enumType, params string[] required)
    {
        HashSet<string> names = new(Enum.GetNames(enumType), StringComparer.Ordinal);
        foreach (string name in required)
        {
            if (!names.Contains(name))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Every reflection anchor the runtime path uses, filled in by <see cref="TryCapture"/> only after all of them
    /// resolved. Nothing here is null once <see cref="Apply"/> installed the repair, except the two custom-run
    /// anchors that only cost the mode preselection.
    /// </summary>
    private sealed class Shape
    {
        // internal, not private: TryCapture lives in a sibling nested type, and a private
        // constructor of a nested class is only reachable from its own containing type.
        internal Shape(
            FieldInfo mainMenuButton,
            FieldInfo resultField,
            FieldInfo runStateField,
            FieldInfo uiNode,
            FieldInfo showPosition,
            FieldInfo shaderMaterial,
            MethodInfo admitPeer,
            MethodInfo getEffectiveMaxPlayers,
            MethodInfo buildSaveKey,
            MethodInfo removeSaveRoomBinding,
            MethodInfo unregisterHotkeys,
            FieldInfo? gameModeDropdown,
            MethodInfo? setSelectedGameMode)
        {
            MainMenuButton = mainMenuButton;
            ResultField = resultField;
            RunStateField = runStateField;
            UiNode = uiNode;
            ShowPosition = showPosition;
            ShaderMaterial = shaderMaterial;
            AdmitPeer = admitPeer;
            GetEffectiveMaxPlayers = getEffectiveMaxPlayers;
            BuildSaveKey = buildSaveKey;
            RemoveSaveRoomBinding = removeSaveRoomBinding;
            UnregisterHotkeys = unregisterHotkeys;
            GameModeDropdown = gameModeDropdown;
            SetSelectedGameMode = setSelectedGameMode;
        }

        internal FieldInfo MainMenuButton { get; }

        internal FieldInfo ResultField { get; }

        internal FieldInfo RunStateField { get; }

        internal FieldInfo UiNode { get; }

        internal FieldInfo ShowPosition { get; }

        internal FieldInfo ShaderMaterial { get; }

        internal MethodInfo AdmitPeer { get; }

        internal MethodInfo GetEffectiveMaxPlayers { get; }

        internal MethodInfo BuildSaveKey { get; }

        internal MethodInfo RemoveSaveRoomBinding { get; }

    /// <summary><c>NButton.UnregisterHotkeys()</c>; drops the hotkey a duplicated button re-registered</summary>
        internal MethodInfo UnregisterHotkeys { get; }

        /// <summary><c>NCustomRunScreen._gameModeDropdown</c>; optional, a miss only costs the mode preselection</summary>
        internal FieldInfo? GameModeDropdown { get; }

        /// <summary><c>NCustomGameModeDropdown.SetSelectedGameMode(GameMode)</c>; optional, same as above</summary>
        internal MethodInfo? SetSelectedGameMode { get; }

        // Resolution lives in TryCapture: it collects every missing anchor instead of throwing on the first one, and
        // it resolves game types by name so a rename cannot break the probe itself.
    }
}
