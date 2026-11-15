using System.Reflection;
using System.Runtime.ExceptionServices;
using Godot;
using HarmonyLib;

namespace Sts2LobbyEnhance;

/// <summary>
/// Cross-server join: a room card can belong to any server in the pool, so clicking it has to move the game to
/// that server first.
///
/// The shape is the host mod's own invite flow, LanConnectInviteServerJoinCoordinator.cs:63-150: switch with a
/// context lease, re-read that server's room list, find the room by id, then hand the lease to the join so the
/// lease outlives the switch. Two differences, both from the brief:
///
/// - No confirmation dialog. By the time a room card is clicked the character-select session is already gone, so
///   there is no live room to lose.
/// - Every failure path switches the player back. Ending up on a stranger's server with no room is the one
///   outcome that is worse than a failed join, and LanConnectServerSwitchCoordinator.LeaveActiveRoomAsync
///   (:1160-1163) means a switch is not a no-op once anything is running.
///
/// A lease already present on the call (the invite path) means upstream is mid-flow and is left alone.
/// </summary>
internal sealed class PoolJoinFix : ICompatFix
{
    internal const string FixId = "server-pool-join";

    /// <summary>Re-entry gate for the reflective call into the original BeginJoinRoomAsync.</summary>
    [ThreadStatic]
    private static bool _reentering;

    /// <summary>Ceiling for the forward switch, matching PoolServerSwitch's switch-back budget.</summary>
    private const int SwitchTimeoutMs = 10_000;

    private static PoolAnchors? _anchors;

    /// <summary>
    /// Blocks a second join while a cross-server switch is in flight. The switch coordinator cancels the superseded
    /// generation (LanConnectChatRuntimePorts.cs:1154), so two clicks would abort the first join instead of the second.
    /// </summary>
    private static int _inFlight;

    public string Id => FixId;

    public FixGroup Group => FixGroup.Pool;

    public bool Applies(out string reason)
    {
        _anchors = null;
        Assembly? host = Reflect.FindAssembly(Entry.HostModAssemblyName);
        if (host == null)
        {
            reason = "host mod '" + Entry.HostModAssemblyName + "' is not loaded";
            return false;
        }

        try
        {
            PoolAnchors? anchors = PoolAnchors.TryCapture(host, PoolAnchorScope.Join, out reason);
            if (anchors == null)
            {
                return false;
            }

            if (!anchors.CanSwitch)
            {
                reason = "missing: " + PoolAnchors.RuntimeTypeName +
                         ".SwitchLobbyServerWithContextAsync / .Instance (the join has no way to change server)";
                return false;
            }

            _anchors = anchors;
            reason = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            reason = "missing: anchor capture threw " + exception.GetType().Name + ": " + exception.Message;
            return false;
        }
    }

    public void Apply(Harmony harmony)
    {
        PoolAnchors anchors = _anchors
            ?? throw new InvalidOperationException(
                "server-pool: Apply ran without a captured anchor set; Applies must be consulted first.");

        harmony.Patch(
            anchors.BeginJoin,
            prefix: new HarmonyMethod(typeof(PoolJoinFix), nameof(BeginJoinPrefix))
            {
                priority = Priority.First
            });

        Entry.Info(
            "[server-pool] joining a room that belongs to another server now switches to that server first; " +
            "patched " + PoolAnchors.OverlayTypeName + ".BeginJoinRoomAsync (LanConnectLobbyOverlay.cs:4731).");
    }

    // ReSharper disable once InconsistentNaming - Harmony binds these by their exact names.
    public static bool BeginJoinPrefix(object? __instance, object?[]? __args, ref Task __result)
    {
        try
        {
            PoolAnchors? anchors = _anchors;
            if (anchors == null || __instance == null || __args is not { Length: >= 5 } || _reentering)
            {
                return true;
            }

            // A lease on the argument list means the invite coordinator already switched and owns the context.
            if (__args[3] != null)
            {
                return true;
            }

            object? room = __args[0];
            if (room == null)
            {
                return true;
            }

            PoolOrigin? origin = PoolRoomStore.Instance.OriginOf(room);
            if (origin == null)
            {
                // Not a pooled room (an invite, a saved-run resume): upstream's own server is the right one.
                return true;
            }

            string current = PoolServerCatalog.CurrentBaseUrl(anchors);
            if (current.Length == 0 || PoolAddress.Same(origin.BaseUrl, current))
            {
                return true;
            }

            if (Interlocked.Exchange(ref _inFlight, 1) != 0)
            {
                // Already switching for a previous click: this click is a no-op, said out loud instead of silently
                // swallowing it.
                PoolRefreshFix.SetStatus(__instance, "正在切换大厅服务器，请稍候…");
                __result = Task.CompletedTask;
                return false;
            }

            CancellationToken cancellationToken = __args[2] is CancellationToken token ? token : CancellationToken.None;
            __result = JoinAcrossServersAsync(
                anchors,
                __instance,
                origin,
                room,
                __args[1] as string,
                cancellationToken,
                __args[4] as string);
            return false;
        }
        catch (Exception exception)
        {
            WarnOnce(
                "join",
                "the cross-server join could not start, so the room is joined against the current server: " +
                PoolRoomClient.Describe(exception));
            return true;
        }
    }

    private static async Task JoinAcrossServersAsync(
        PoolAnchors anchors,
        object overlay,
        PoolOrigin origin,
        object room,
        string? password,
        CancellationToken cancellationToken,
        string? expectedServer)
    {
        string originalServer = PoolServerCatalog.CurrentBaseUrl(anchors);
        object? lease = null;
        bool switched = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            PoolRefreshFix.SetStatus(overlay, "正在切换到房间所在的服务器：" + origin.ShortName + "…");
            // Own budget, not the caller's token: a click passes CancellationToken.None, and a switch that parks on the
            // coordinator's gate (LanConnectChatRuntimePorts.cs:1156) would otherwise never return and leave _inFlight
            // set, turning every later cross-server click into a silent no-op.
            using var switchBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            switchBudget.CancelAfter(SwitchTimeoutMs);
            lease = await PoolServerSwitch.SwitchAsync(anchors, origin.BaseUrl, switchBudget.Token).ConfigureAwait(true);
            switched = true;

            if (lease != null && PoolServerSwitch.IsLeaseCancelled(anchors, lease))
            {
                PoolRefreshFix.SetStatus(overlay, "服务器上下文已变化，请重试。");
                await PoolServerSwitch.ReturnToAsync(anchors, originalServer, overlay).ConfigureAwait(true);
                return;
            }

            string roomId = PoolRoomClient.RoomIdOf(anchors, room);
            (object? fresh, bool verified) = await FindRoomAsync(anchors, origin.BaseUrl, roomId, cancellationToken)
                .ConfigureAwait(true);

            if (verified && fresh == null)
            {
                PoolRefreshFix.SetStatus(overlay, "房间里找不到该房间了（大厅列表刚刚变化），已切回原服务器。");
                await PoolServerSwitch.ReturnToAsync(anchors, originalServer, overlay).ConfigureAwait(true);
                return;
            }

            // The lease is handed over but kept in scope: the original disposes it in its own finally
            // (LanConnectLobbyOverlay.cs:4786-4789), and disposing a lease twice is a no-op
            // (LanConnectServerContextLease.Dispose uses Interlocked.Exchange), so our finally is the safety net for
            // the path where the reflective call fails before the original's body ever ran.
            object targetRoom = fresh ?? room;
            await InvokeJoinAsync(
                    anchors,
                    overlay,
                    targetRoom,
                    password,
                    cancellationToken,
                    lease,
                    // No trailing slash: upstream compares this against NormalizeAuthority(...) with OrdinalIgnoreCase
                    // (LanConnectLobbyOverlay.cs:5358-5365), and ".../" never equals that.
                    expectedServer ?? origin.BaseUrl)
                .ConfigureAwait(true);

            if (!PoolServerSwitch.HasActiveRoomSession(anchors))
            {
                // A saved-run room with more than one free slot opens the slot picker and returns WITHOUT joining
                // (:4749-4762). Switching back there would cancel the superseded server generation and void the lease
                // the player is about to pick a slot with, so this path keeps the context and leaves the address where
                // it is; the picker's own callback joins through the ordinary flow.
                if (HasSavedRun(anchors, targetRoom))
                {
                    return;
                }

                await PoolServerSwitch.ReturnToAsync(anchors, originalServer, overlay).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            if (switched)
            {
                await PoolServerSwitch.ReturnToAsync(anchors, originalServer, overlay).ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            WarnOnce("join", "the cross-server join failed: " + PoolRoomClient.Describe(exception));
            if (switched)
            {
                await PoolServerSwitch.ReturnToAsync(anchors, originalServer, overlay).ConfigureAwait(true);
            }
        }
        finally
        {
            PoolServerSwitch.DisposeLease(lease);
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }

    /// <summary>
    /// Whether the room carries a saved run (LanConnectLobbyModels.cs:41). Those are the rooms whose join can legally
    /// stop at a dialog instead of connecting, which is why the caller must not treat "no session yet" as a failure
    /// for them.
    /// </summary>
    private static bool HasSavedRun(PoolAnchors anchors, object room)
    {
        try
        {
            return anchors.RoomSavedRun?.GetValue(room) != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Re-reads the target server's rooms and picks the room by id, the same way the invite coordinator verifies an
    /// invite (:117-133). A failed re-read is not a missing room: verified stays false and the caller proceeds with
    /// the pooled room object, so a flaky list request cannot fake a deletion.
    /// </summary>
    private static async Task<(object? Room, bool Verified)> FindRoomAsync(
        PoolAnchors anchors,
        string baseUrl,
        string roomId,
        CancellationToken cancellationToken)
    {
        if (roomId.Length == 0)
        {
            return (null, false);
        }

        try
        {
            List<object> rooms = await PoolRoomClient
                .GetRoomsAsync(anchors, baseUrl, PoolRoomClient.VerifyTimeoutMs, cancellationToken)
                .ConfigureAwait(true);
            foreach (object candidate in rooms)
            {
                if (string.Equals(PoolRoomClient.RoomIdOf(anchors, candidate), roomId, StringComparison.Ordinal))
                {
                    return (candidate, true);
                }
            }

            return (null, true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            WarnOnce("verify", "the room list of the target server could not be re-read: " + PoolRoomClient.Describe(exception));
            return (null, false);
        }
    }

    private static async Task InvokeJoinAsync(
        PoolAnchors anchors,
        object overlay,
        object room,
        string? password,
        CancellationToken cancellationToken,
        object? lease,
        string expectedServer)
    {
        Task? join;
        bool previous = _reentering;
        _reentering = true;
        try
        {
            join = anchors.BeginJoin.Invoke(
                overlay,
                new object?[] { room, password, cancellationToken, lease, expectedServer }) as Task;
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            return;
        }
        finally
        {
            _reentering = previous;
        }

        if (join != null)
        {
            await join.ConfigureAwait(true);
        }
    }

    private static void WarnOnce(string signature, string message) =>
        PoolRefreshFix.WarnOnce(signature, message);
}

/// <summary>
/// Server switching, shared by the join and the create-room repairs. Both need the same three steps: switch, know
/// whether the switch landed, and get back to where the player started.
///
/// Switching is never free: LanConnectServerSwitchCoordinator.SwitchCoreAsync leaves an active room (:1160-1163),
/// stops and reconnects the public chat (:1166, :1178) and persists the new address into the config (:1174). The
/// callers therefore switch only when they have decided to, and always put the address back when the reason for
/// switching went away.
/// </summary>
internal static class PoolServerSwitch
{
    private const int SwitchBackTimeoutMs = 10_000;

    /// <summary>
    /// Switches and returns the context lease when the host mod offers one. The lease is what keeps the server
    /// context alive across the join; the caller owns it from here and must dispose it.
    /// </summary>
    internal static async Task<object?> SwitchAsync(
        PoolAnchors anchors,
        string baseUrl,
        CancellationToken cancellationToken)
    {
        object runtime = anchors.RuntimeInstance?.GetValue(null)
                         ?? throw new InvalidOperationException("the lobby runtime is not ready");

        if (anchors.SwitchWithContext != null)
        {
            object? invocation = PoolRoomClient.Invoke(anchors.SwitchWithContext, runtime, new object?[]
            {
                PoolAddress.ToBaseUrl(baseUrl),
                cancellationToken
            });
            Task task = invocation as Task
                        ?? throw new InvalidOperationException("SwitchLobbyServerWithContextAsync no longer returns a Task");
            await task.ConfigureAwait(true);
            return task.GetType().GetProperty("Result")?.GetValue(task);
        }

        if (anchors.SwitchPlain != null)
        {
            object? invocation = PoolRoomClient.Invoke(anchors.SwitchPlain, runtime, new object?[]
            {
                PoolAddress.ToBaseUrl(baseUrl),
                cancellationToken
            });
            if (invocation is Task task)
            {
                await task.ConfigureAwait(true);
            }

            return null;
        }

        throw new InvalidOperationException("the host mod has no server switch method");
    }

    /// <summary>Puts the player back on the server they started from. Never throws and never cancelled by the caller.</summary>
    internal static async Task ReturnToAsync(PoolAnchors anchors, string baseUrl, object overlay)
    {
        if (PoolAddress.TryNormalize(baseUrl) == null)
        {
            return;
        }

        if (PoolAddress.Same(baseUrl, PoolServerCatalog.CurrentBaseUrl(anchors)))
        {
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(SwitchBackTimeoutMs);
            object? lease = await SwitchAsync(anchors, baseUrl, cts.Token).ConfigureAwait(true);
            DisposeLease(lease);
            RefreshServerUi(anchors, overlay);
            PoolRefreshFix.SetStatus(overlay, "已切回原服务器：" + PoolAddress.ShortName(null, baseUrl, PoolText.SourceNameMaxLength));
        }
        catch (Exception exception)
        {
            PoolRefreshFix.WarnOnce(
                "switch-back",
                "switching back to the original server failed, so the player stays on the last server until they " +
                "pick one: " + PoolRoomClient.Describe(exception));
        }
    }

    /// <summary>
    /// Mirrors what the host mod does after its own switch (LanConnectLobbyOverlay.cs:6392-6399): repaint the
    /// server field and the network summary, otherwise the dialog keeps advertising the previous address.
    /// </summary>
    internal static void RefreshServerUi(PoolAnchors anchors, object overlay)
    {
        try
        {
            if (anchors.ServerBaseUrlInput?.GetValue(overlay) is LineEdit input)
            {
                // The box mirrors the OVERRIDE, not the resolved address (upstream :6395 writes
                // LobbyServerBaseUrlOverride). Writing the resolved value there would let the player persist it as an
                // override by pressing the box's own apply button.
                input.Text = (anchors.ConfigBaseUrlOverride?.GetValue(null) as string)
                             ?? anchors.ConfigBaseUrl.GetValue(null) as string
                             ?? string.Empty;
            }

            anchors.UpdateNetworkSummary?.Invoke(overlay, null);
            anchors.UpdateActionButtons?.Invoke(overlay, null);
        }
        catch (Exception)
        {
            // Cosmetic only.
        }
    }

    internal static bool HasActiveRoomSession(PoolAnchors anchors)
    {
        try
        {
            object? runtime = anchors.RuntimeInstance?.GetValue(null);
            return runtime != null && anchors.RuntimeActiveRoomSession?.GetValue(runtime) is true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The hosted half of "a room session is live" (upstream :267, :4450-4455).</summary>
    internal static bool HasActiveHostedRoom(PoolAnchors anchors)
    {
        try
        {
            object? runtime = anchors.RuntimeInstance?.GetValue(null);
            return runtime != null && anchors.RuntimeHasActiveHostedRoom?.GetValue(runtime) is true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static bool SwitchInProgress(PoolAnchors anchors)
    {
        try
        {
            object? runtime = anchors.RuntimeInstance?.GetValue(null);
            return runtime != null && anchors.RuntimeSwitchInProgress?.GetValue(runtime) is true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static bool IsLeaseCancelled(PoolAnchors anchors, object lease)
    {
        try
        {
            return anchors.LeaseToken?.GetValue(lease) is CancellationToken token && token.IsCancellationRequested;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static void DisposeLease(object? lease)
    {
        try
        {
            (lease as IDisposable)?.Dispose();
        }
        catch (Exception)
        {
            // Disposing twice is a no-op in the host mod; a throwing dispose is not worth a warning.
        }
    }
}
