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
/// Lets a client enter a Blitz or Dread session.
///
/// The host mod's post-join screen dispatch (<c>LanConnectLobbyJoinFlow.PushJoinedScreen</c>) was written
/// against 0.111, where <c>GameMode</c> was <c>{ None, Standard, Daily, Custom }</c>. It switches on
/// <c>gameMode - 1</c> and ends with <c>throw new ArgumentOutOfRangeException(... "Unhandled multiplayer
/// game mode.")</c>. 0.113 renumbered the enum to <c>{ None, Standard, Daily, Dread, Blitz }</c>, so against
/// the shipped binary that switch is wrong in three separate ways:
/// <list type="bullet">
/// <item><c>case 2</c> used to mean Custom and now means <b>Dread</b>, so a Dread client was silently sent
/// to <c>NCustomRunScreen</c> instead of <c>NDreadScreen</c>.</item>
/// <item><c>case 3</c> has no body at all, so a <b>Blitz</b> client fell through to the throw — this is the
/// <c>ArgumentOutOfRangeException: Unhandled multiplayer game mode.</c> seen in a real session.</item>
/// <item>Custom is no longer a <c>GameMode</c> member, only the separate <c>JoinResult.isCustom</c> flag, so
/// custom runs stopped reaching <c>NCustomRunScreen</c> and started landing on whichever screen their base
/// mode maps to.</item>
/// </list>
///
/// The replacement is a line-for-line copy of the game's own dispatch in
/// <c>NJoinFriendScreen.JoinGameAsync</c> (0.113), which is the authority on where each mode belongs. It
/// covers the three session states that method has a branch for:
/// <list type="bullet">
/// <item><c>InLobby</c> — joining a room that has not started yet
/// (<c>NJoinFriendScreen.cs:301-338</c>);</item>
/// <item><c>InLoadedLobby</c> — joining a room that is resuming a save
/// (<c>NJoinFriendScreen.cs:339-362</c>);</item>
/// <item><c>Running</c> — the host's run is already under way, so the game refuses the join with
/// <c>NetError.RunInProgress</c> and disconnects (<c>NJoinFriendScreen.cs:363-371</c>). This state is
/// reachable: <c>JoinFlow.Begin</c> builds a <c>JoinResult</c> for it via <c>AttemptRejoin</c>
/// (<c>JoinFlow.cs:120-131</c>). The shipped host mod has no branch for it and falls through to its
/// <c>throw new ArgumentOutOfRangeException(... "Unhandled multiplayer game mode.")</c>
/// (<c>dec-official/LanConnectLobbyJoinFlow.cs:488</c>), which is what this fix replaces.</item>
/// </list>
///
/// <c>RunSessionState.None</c> has no game-side branch at all — <c>JoinGameAsync</c> simply falls through
/// its <c>if</c> chain and clears the loading overlay — and it cannot reach here either: <c>JoinFlow.Begin</c>
/// answers the <c>default:</c> arm of its state switch by disconnecting with <c>NetError.InternalError</c>
/// and throwing (<c>JoinFlow.cs:132-135</c>), so <c>PushJoinedScreen</c> is never called with
/// <c>None</c>. This fix therefore leaves <c>None</c> on the host mod's own path instead of inventing a
/// silent no-op for a state the game never produces.
///
/// Anything this method does not recognise returns <c>true</c> so the host mod's own method still runs and
/// still throws exactly what it threw before; the fix never invents behaviour for states it does not model.
///
/// Note this is the *join* side only. Creating such a room is handled separately by
/// <see cref="ExtraGameModesFix"/>; that fix routes the host into the right screen, but it never runs on a
/// client, which is why the two gaps are independent.
/// </summary>
internal sealed class JoinScreenDispatchFix : ICompatFix
{
    private const string JoinFlowTypeName = "Sts2LanConnect.Scripts.LanConnectLobbyJoinFlow";
    private const string DispatchMethodName = "PushJoinedScreen";

    public string Id => "join-screen-modes";

    /// <summary>
    /// Gated on the same 0.113 game-side fact the rest of the fixes use. The break this repair works around
    /// is a *game* change (0.113 renumbered <c>GameMode</c> to <c>{None, Standard, Daily, Dread, Blitz}</c>,
    /// so the shipped mod's <c>switch(gameMode - 1)</c> no longer lines up), and that renumbering shipped in
    /// the same release that removed <c>InitialGameInfoMessage.gameMode</c> — the flag below. With the
    /// game back on 0.111 the flag goes false and this fix stops installing, which is the self-disabling
    /// promise made in <c>Core/CompatContract.cs:8-12</c>.
    ///
    /// Known limit, stated rather than hidden: this flag describes the game, not the host mod. If upstream
    /// ever ships a 0.113-compiled <c>PushJoinedScreen</c> while the game stays 0.113, this prefix still
    /// installs and replaces it with an equivalent implementation (same screens for the same states). The
    /// only way to tell those apart would be to inspect the host mod's IL for the 0.111 <c>switch</c> shape,
    /// which is not something this patch should be doing on every start.
    /// </summary>
    public bool Applies(in CompatSnapshot snapshot) => snapshot.InitialGameInfoLostGameMode;

    public void Apply(Harmony harmony, in CompatSnapshot snapshot)
    {
        System.Reflection.Assembly host = Reflect.FindAssembly(Entry.HostModAssemblyName)
            ?? throw new InvalidOperationException("Host mod assembly is not loaded.");

        Type joinFlow = Reflect.RequireType(host, JoinFlowTypeName);
        System.Reflection.MethodInfo dispatch = Reflect.RequireMethodByName(joinFlow, DispatchMethodName, 3);

        System.Reflection.ParameterInfo[] parameters = dispatch.GetParameters();
        if (parameters[0].ParameterType != typeof(NSubmenuStack)
            || parameters[1].ParameterType != typeof(NetClientGameService)
            || parameters[2].ParameterType != typeof(JoinResult))
        {
            throw new MissingMethodException(
                joinFlow.FullName,
                $"{DispatchMethodName}(NSubmenuStack, NetClientGameService, JoinResult) — found " +
                $"({parameters[0].ParameterType.Name}, {parameters[1].ParameterType.Name}, {parameters[2].ParameterType.Name})");
        }

        harmony.Patch(
            dispatch,
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
    /// Returns false (skip the original) only for the states handled here. Everything else falls through to
    /// the host mod's method, so an unexpected mode still produces the upstream exception rather than a
    /// silently wrong screen.
    /// </summary>
    // ReSharper disable once UnusedParameter.Local — Harmony binds by position.
    private static bool PushJoinedScreenPrefix(
        NSubmenuStack stack,
        NetClientGameService netService,
        JoinResult joinResult)
    {
        return !TryPushJoinedScreen(stack, netService, joinResult);
    }

    /// <summary>Returns true when a screen was pushed.</summary>
    private static bool TryPushJoinedScreen(
        NSubmenuStack stack,
        NetClientGameService netService,
        JoinResult joinResult)
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
            // NJoinFriendScreen.cs:363-371, in the game's own order: build the RunInProgress popup, add it to
            // the modal container, then disconnect. `NetError.RunInProgress` is 101 (NetError.cs:45).
            //
            // One deliberate difference: the game dereferences `NModalContainer.Instance` unconditionally,
            // which would NRE if the container is not in the tree yet. We use `?.` so a missing container
            // costs the popup instead of the whole join path; the disconnect below still happens either way.
            NErrorPopup? popup = NErrorPopup.Create(new NetErrorInfo(NetError.RunInProgress, selfInitiated: false));
            if (popup != null)
            {
                NModalContainer.Instance?.Add(popup);
            }

            netService.Disconnect(NetError.RunInProgress);

            // The caller (dec-official/LanConnectLobbyJoinFlow.cs:114-116) continues to mark the attempt as
            // "Joined" once this method returns normally; that is the host mod's own control flow and is out
            // of scope for a prefix on this seam. What matters here is that the player now sees the game's
            // RunInProgress popup instead of "Unhandled multiplayer game mode."
            Entry.Info(
                $"join-screen-modes: the host's run is already in progress (mode={joinResult.gameMode}); " +
                "showed the RunInProgress popup and disconnected, exactly like NJoinFriendScreen does.");
            return true;
        }

        // RunSessionState.None: no game-side branch exists and JoinFlow.Begin cannot return it (JoinFlow.cs:132-135
        // disconnects and throws on the default arm), so the host mod's own handling is left untouched.
        return false;
    }

    /// <summary>Joining a room that has not started its run yet. Order copied from NJoinFriendScreen.</summary>
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
    /// Joining a room that is resuming a save. Order copied from NJoinFriendScreen, which is the only place
    /// that records where a resumed Blitz or Dread run belongs: <c>NMultiplayerLoadGameScreen</c>, the same
    /// screen Standard uses. There is no <c>NBlitzLoadScreen</c> or <c>NDreadLoadScreen</c> in 0.113 — the
    /// Blitz and Dread screens only expose <c>InitializeMultiplayerAsClient</c>, i.e. the not-yet-started
    /// lobby, so they cannot be reused for a resume.
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
