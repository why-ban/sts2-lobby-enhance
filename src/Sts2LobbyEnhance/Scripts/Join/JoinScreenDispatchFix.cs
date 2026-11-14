using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.Blitz;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using MegaCrit.Sts2.Core.Nodes.Screens.DailyRun;
using MegaCrit.Sts2.Core.Nodes.Screens.Dread;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2LobbyEnhance;

    /// <summary>
    /// Lets a client enter a Blitz or Dread session
    ///
    /// The host mod's post-join dispatch (<c>LanConnectLobbyJoinFlow.PushJoinedScreen</c>) was written against 0.111,
    /// where <c>GameMode</c> was <c>{ None, Standard, Daily, Custom }</c>. It switches on <c>gameMode - 1</c> and ends
    /// with <c>throw new ArgumentOutOfRangeException(..., "Unhandled multiplayer game mode.")</c>. 0.113 renumbered
    /// the enum to <c>{ None, Standard, Daily, Dread, Blitz }</c>, so against the shipped binary that switch is wrong
    /// three separate ways:
    /// <list type="bullet">
    /// <item><c>case 2</c> meant Custom and now means <b>Dread</b>, so a Dread client was sent to
    /// <c>NCustomRunScreen</c> instead of <c>NDreadScreen</c>.</item>
    /// <item><c>case 3</c> has no body, so a <b>Blitz</b> client fell through to the throw. That is the
    /// <c>ArgumentOutOfRangeException: Unhandled multiplayer game mode.</c> seen in a real session.</item>
    /// <item>Custom is no longer a <c>GameMode</c> member, only the separate <c>JoinResult.isCustom</c> flag, so
    /// custom runs stopped reaching <c>NCustomRunScreen</c>.</item>
    /// </list>
    ///
    /// The replacement copies the game's own dispatch in <c>NJoinFriendScreen.JoinGameAsync</c> (0.113), which has a
    /// branch for three states: <c>InLobby</c> (<c>NJoinFriendScreen.cs:301-338</c>), <c>InLoadedLobby</c> (:339-362)
    /// and <c>Running</c> (:363-371), where the game refuses with <c>NetError.RunInProgress</c> and
    /// <c>JoinFlow.Begin</c> does build a <c>JoinResult</c> via <c>AttemptRejoin</c> (<c>JoinFlow.cs:120-131</c>)
    /// while the shipped mod falls through to its throw (<c>dec-official/LanConnectLobbyJoinFlow.cs:488</c>).
    /// <c>RunSessionState.None</c> is left on the host mod's path: <c>JoinFlow.Begin</c> disconnects and throws
    /// before <c>PushJoinedScreen</c> is called (<c>JoinFlow.cs:132-135</c>)
    ///
    /// Anything unrecognised returns <c>true</c> so the host mod's own method still runs and still throws what it
    /// threw before. Creating such a room is handled separately by <see cref="ExtraGameModesFix"/>, which never
    /// runs on a client
    /// </summary>
internal sealed class JoinScreenDispatchFix : ICompatFix
{
    private const string JoinFlowTypeName = "Sts2LanConnect.Scripts.LanConnectLobbyJoinFlow";
    private const string DispatchMethodName = "PushJoinedScreen";

    // Probe targets. Strings, never typeof: a probe that mentions a renamed game type cannot be JITted, and
    // surviving exactly that is the whole point of the probe (2026-11-14 gating audit).
    private const string SubmenuStackTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NSubmenuStack";
    private const string NetClientServiceTypeName = "MegaCrit.Sts2.Core.Multiplayer.NetClientGameService";
    private const string JoinResultTypeName = "MegaCrit.Sts2.Core.Multiplayer.Game.JoinResult";

    public string Id => "join-screen-modes";

    /// <summary>
    /// Gated on the same 0.113 game-side fact the rest of the fixes use: the break is a *game* change, and the
    /// renumbering shipped in the release that removed <c>InitialGameInfoMessage.gameMode</c>
    ///
    /// Known limit, stated rather than hidden: the flag describes the game, not the host mod. A 0.113-compiled
    /// <c>PushJoinedScreen</c> on a 0.113 game would still be replaced by an equivalent implementation, because
    /// telling the two apart would mean inspecting the host mod's IL on every start
    /// </summary>
    public FixGroup Group => FixGroup.Join;

    /// <summary>
    /// Needed when this build has the 0.113 <c>GameMode</c> renumbering. That flag is this repair's precondition,
    /// not a global "is this 0.113" gate
    /// </summary>
    public bool Applies(in CompatSnapshot snapshot, out string reason)
    {
        if (!snapshot.InitialGameInfoLostGameMode)
        {
            reason = "this build does not show the 0.113 GameMode renumbering this dispatch repairs";
            return false;
        }

        Assembly? host = Reflect.FindAssembly(Entry.HostModAssemblyName);
        if (host == null)
        {
            reason = "host mod '" + Entry.HostModAssemblyName + "' is not loaded";
            return false;
        }

        if (!TryCapture(host, out Shape shape, out string missing))
        {
            reason = "missing: " + missing;
            return false;
        }

        _shape = shape;
        reason = string.Empty;
        return true;
    }

    internal sealed class Shape
    {
        internal MethodInfo Dispatch = null!;
    }

    internal static Shape? _shape;

    /// <summary>
    /// Resolves the host-mod seam. Never throws and never invokes anything. The game-side screen types this dispatch
    /// touches are compile-time references and are deliberately not probed here; the runtime guard in
    /// <see cref="TryPushJoinedScreen"/> covers them
    /// </summary>
    private static bool TryCapture(Assembly host, out Shape shape, out string reason)
    {
        shape = new Shape();
        var missing = new List<string>();

        Type? joinFlow = host.GetType(JoinFlowTypeName, throwOnError: false, ignoreCase: false);
        if (joinFlow == null)
        {
            missing.Add("type " + JoinFlowTypeName);
        }
        else
        {
            MethodInfo? dispatch = joinFlow
                .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    string.Equals(method.Name, DispatchMethodName, StringComparison.Ordinal)
                    && method.GetParameters() is { Length: 3 } parameters
                    && string.Equals(parameters[0].ParameterType.FullName, SubmenuStackTypeName, StringComparison.Ordinal)
                    && string.Equals(parameters[1].ParameterType.FullName, NetClientServiceTypeName, StringComparison.Ordinal)
                    && string.Equals(parameters[2].ParameterType.FullName, JoinResultTypeName, StringComparison.Ordinal));

            if (dispatch == null)
            {
                missing.Add(
                    $"{JoinFlowTypeName}.{DispatchMethodName}({SubmenuStackTypeName}, {NetClientServiceTypeName}, " +
                    $"{JoinResultTypeName})");
            }
            else
            {
                shape.Dispatch = dispatch;
            }
        }

        reason = string.Join(", ", missing);
        return missing.Count == 0;
    }

    public void Apply(Harmony harmony, in CompatSnapshot snapshot)
    {
        Shape shape = _shape
            ?? throw new InvalidOperationException(
                "join-screen-modes: Apply ran without a captured shape; Applies must be consulted first.");

        harmony.Patch(
            shape.Dispatch,
            prefix: new HarmonyMethod(typeof(JoinScreenDispatchFix), nameof(PushJoinedScreenPrefix))
            {
                priority = Priority.First
            });

        Entry.Info(
            "join-screen-modes: patched LanConnectLobbyJoinFlow.PushJoinedScreen to mirror the game's own " +
            "NJoinFriendScreen dispatch (Blitz/Dread in both the lobby and the resumed-save state, custom " +
            "decided by isCustom instead of the removed GameMode.Custom).");
    }

    /// <summary>
    /// Returns false (skip the original) only for the states handled here. Anything else falls through to the host
    /// mod's method, so an unexpected mode still throws upstream's exception instead of reaching a wrong screen
    /// </summary>
    private static bool PushJoinedScreenPrefix(
        NSubmenuStack stack,
        NetClientGameService netService,
        JoinResult joinResult)
    {
        return !TryPushJoinedScreen(stack, netService, joinResult);
    }

    /// <summary>
    /// Returns true when a screen was pushed
    ///
    /// Runtime guard: the twelve game types this dispatch touches are compile-time references, so a rename can only
    /// surface as a JIT failure on the first join. Catching turns that into "the host mod's own dispatch runs".
    /// Every branch pushes last and logs after, so a throw can only happen with nothing pushed yet
    /// </summary>
    private static bool TryPushJoinedScreen(
        NSubmenuStack stack,
        NetClientGameService netService,
        JoinResult joinResult)
    {
        try
        {
            if (joinResult.sessionState == RunSessionState.InLobby)
            {
                return TryPushLobbyScreen(stack, netService, joinResult);
            }

            if (joinResult.sessionState == RunSessionState.InLoadedLobby)
            {
                return TryPushLoadScreen(stack, netService, joinResult);
            }

            if (joinResult.sessionState == RunSessionState.Running)
            {
    // NJoinFriendScreen.cs:363-371, in the game's own order: build the RunInProgress popup, add it to the modal
    // container, then disconnect. `NetError.RunInProgress` is 101 (NetError.cs:45).
    //
    // One deliberate difference: the game dereferences `NModalContainer.Instance` unconditionally, which NREs if
    // the container is not in the tree yet. `?.` costs the popup instead of the join; the disconnect still happens
                NErrorPopup? popup = NErrorPopup.Create(new NetErrorInfo(NetError.RunInProgress, selfInitiated: false));
                if (popup != null)
                {
                    NModalContainer.Instance?.Add(popup);
                }

                netService.Disconnect(NetError.RunInProgress);

    // The caller (dec-official/LanConnectLobbyJoinFlow.cs:114-116) still marks the attempt "Joined" once this
    // returns normally, which is the host mod's own control flow. What matters here is the popup the player sees
                Entry.Info(
                    $"join-screen-modes: the host's run is already in progress (mode={joinResult.gameMode}); " +
                    "showed the RunInProgress popup and disconnected, exactly like NJoinFriendScreen does.");
                return true;
            }

    // RunSessionState.None has no game-side branch and JoinFlow.Begin cannot return it, so the host mod keeps it
            return false;
        }
        catch (Exception exception)
        {
            WarnGuardTripped(exception);
            return false;
        }
    }

    private static int _guardTripped;

    private static void WarnGuardTripped(Exception exception)
    {
        if (Interlocked.Exchange(ref _guardTripped, 1) != 0)
        {
            return;
        }

        Entry.Warn(
            "join-screen-modes: the game's screen API no longer matches this repair " +
            $"({exception.GetType().Name}: {exception.Message}). Falling back to the host mod's own dispatch for " +
            "the rest of this session; Blitz/Dread/custom rooms may land on the wrong screen.");
    }

    private static bool TryPushLobbyScreen(
        NSubmenuStack stack,
        NetClientGameService netService,
        JoinResult joinResult)
    {
        ClientLobbyJoinResponseMessage response = joinResult.joinResponse
            ?? throw new InvalidOperationException(
                $"a lobby join result for {joinResult.gameMode} carried no {nameof(ClientLobbyJoinResponseMessage)}.");

        // Custom is decided first, exactly like the game does: in 0.113 it is a flag, not a GameMode member,
        // so checking the enum alone would send a custom run to its base mode's screen.
        if (joinResult.isCustom)
        {
            NCustomRunScreen screen = stack.GetSubmenuType<NCustomRunScreen>();
            screen.InitializeMultiplayerAsClient(netService, response);
            stack.Push(screen);
            Entry.Info("join-screen-modes: client entered a custom lobby.");
            return true;
        }

        switch (joinResult.gameMode)
        {
            case GameMode.Standard:
            {
                NCharacterSelectScreen screen = stack.GetSubmenuType<NCharacterSelectScreen>();
                screen.InitializeMultiplayerAsClient(netService, response);
                stack.Push(screen);
                Entry.Info("join-screen-modes: client entered a Standard lobby.");
                return true;
            }

            case GameMode.Daily:
            {
                NDailyRunScreen screen = stack.GetSubmenuType<NDailyRunScreen>();
                screen.InitializeMultiplayerAsClient(netService, response);
                stack.Push(screen);
                Entry.Info("join-screen-modes: client entered a Daily lobby.");
                return true;
            }

            case GameMode.Blitz:
            {
                NBlitzScreen screen = stack.GetSubmenuType<NBlitzScreen>();
                screen.InitializeMultiplayerAsClient(netService, response);
                stack.Push(screen);
                Entry.Info("join-screen-modes: client entered a Blitz lobby.");
                return true;
            }

            case GameMode.Dread:
            {
                NDreadScreen screen = stack.GetSubmenuType<NDreadScreen>();
                screen.InitializeMultiplayerAsClient(netService, response);
                stack.Push(screen);
                Entry.Info("join-screen-modes: client entered a Dread lobby.");
                return true;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// Joining a room that is resuming a save. Order copied from NJoinFriendScreen, the only place recording where a
    /// resumed Blitz or Dread run belongs: <c>NMultiplayerLoadGameScreen</c>, the same screen Standard uses. There is
    /// no <c>NBlitzLoadScreen</c> or <c>NDreadLoadScreen</c> in 0.113, those screens only expose
    /// <c>InitializeMultiplayerAsClient</c> (the not-yet-started lobby), so they cannot be reused for a resume
    /// </summary>
    private static bool TryPushLoadScreen(
        NSubmenuStack stack,
        NetClientGameService netService,
        JoinResult joinResult)
    {
        ClientLoadJoinResponseMessage response = joinResult.loadJoinResponse
            ?? throw new InvalidOperationException(
                $"a load join result for {joinResult.gameMode} carried no {nameof(ClientLoadJoinResponseMessage)}.");

        if (joinResult.isCustom)
        {
            NCustomRunLoadScreen screen = stack.GetSubmenuType<NCustomRunLoadScreen>();
            screen.InitializeAsClient(netService, response);
            stack.Push(screen);
            Entry.Info("join-screen-modes: client resumed a custom run.");
            return true;
        }

        switch (joinResult.gameMode)
        {
            case GameMode.Standard:
            case GameMode.Blitz:
            case GameMode.Dread:
            {
                NMultiplayerLoadGameScreen screen = stack.GetSubmenuType<NMultiplayerLoadGameScreen>();
                screen.InitializeAsClient(netService, response);
                stack.Push(screen);
                Entry.Info($"join-screen-modes: client resumed a {joinResult.gameMode} run.");
                return true;
            }

            case GameMode.Daily:
            {
                NDailyRunLoadScreen screen = stack.GetSubmenuType<NDailyRunLoadScreen>();
                screen.InitializeAsClient(netService, response);
                stack.Push(screen);
                Entry.Info("join-screen-modes: client resumed a Daily run.");
                return true;
            }

            default:
                return false;
        }
    }
}
