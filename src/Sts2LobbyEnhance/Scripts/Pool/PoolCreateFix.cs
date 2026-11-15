using System.Reflection;
using System.Runtime.ExceptionServices;
using HarmonyLib;

namespace Sts2LobbyEnhance;

/// <summary>
/// Picks the server a new room is published to: the lowest-latency server that currently allows room creation,
/// preferring the one the player is already on.
///
/// Why this exists: a pooled lobby makes the server behind the room list invisible, so hosting would otherwise
/// land on whatever server happened to be current - possibly a node that is near its bandwidth guard
/// (createRoomGuardStatus, LanConnectLobbyModel's LobbyHealthResponse :110-124) or one that is far away.
///
/// What it does NOT do: it never touches config.json itself. Switching writes the address, that is the host mod's
/// own semantics (LanConnectServerSwitchCoordinator.cs:1171-1175) and is accepted as such. It also refuses to
/// switch when any room session is live, because the switch would leave that room (:1160-1163) - the same reason
/// LanConnectLobbyOverlay.CreateRoomAsync already refuses to run with a hosted room (:4450-4455).
///
/// The threshold is a deliberate small deviation from "switch to the lowest-latency server": switching is not free
/// (chat reconnect, a config write, a new TCP path), so a server that is one millisecond closer is not worth it.
/// Only a real gain - or a current server that is offline or at its guard - moves the player.
/// </summary>
internal sealed class PoolCreateFix : ICompatFix
{
    internal const string FixId = "server-pool-create";

    /// <summary>How much faster a server must be before switching is worth the churn.</summary>
    private const int SwitchGainMs = 30;

    /// <summary>At most this many servers are tried before the create runs on whatever is current.</summary>
    private const int MaxSwitchAttempts = 3;

    private const int ProbeTimeoutMs = 2000;

    /// <summary>A switch stops the public chat, reconnects it and writes the config, so it gets its own budget and
    /// never shares the probe's.</summary>
    private const int SwitchTimeoutMs = 15_000;

    /// <summary>Re-entry gate for the reflective call into the original CreateRoomAsync.</summary>
    [ThreadStatic]
    private static bool _reentering;

    private static PoolAnchors? _anchors;

    /// <summary>
    /// Blocks a second click while the server choice is still running. Upstream only sets _actionInFlight inside
    /// CreateRoomAsync (:4408, :4490), which is the method this prefix has not reached yet, so two fast clicks would
    /// otherwise start two switches and the coordinator cancels the superseded one (LanConnectChatRuntimePorts.cs:1154).
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
            PoolAnchors? anchors = PoolAnchors.TryCapture(host, PoolAnchorScope.Create, out reason);
            if (anchors == null)
            {
                return false;
            }

            if (!anchors.CanSwitch)
            {
                reason = "missing: " + PoolAnchors.RuntimeTypeName +
                         ".SwitchLobbyServerWithContextAsync / .Instance (hosting cannot change server)";
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
            anchors.CreateRoom,
            prefix: new HarmonyMethod(typeof(PoolCreateFix), nameof(CreateRoomPrefix))
            {
                priority = Priority.First
            });

        Entry.Info(
            "[server-pool] hosting now starts on the lowest-latency server whose create guard allows it; patched " +
            PoolAnchors.OverlayTypeName + ".CreateRoomAsync (LanConnectLobbyOverlay.cs:4401).");
    }

    // ReSharper disable once InconsistentNaming - Harmony binds these by their exact names.
    public static bool CreateRoomPrefix(object? __instance, ref Task __result)
    {
        try
        {
            PoolAnchors? anchors = _anchors;
            if (anchors == null || __instance == null || _reentering)
            {
                return true;
            }

            // A live room session would be destroyed by the switch (:1160-1163) and a switch already in flight
            // owns the context; both mean "run upstream untouched".
            if (PoolServerSwitch.HasActiveRoomSession(anchors) || PoolServerSwitch.SwitchInProgress(anchors))
            {
                return true;
            }

            if (Interlocked.Exchange(ref _inFlight, 1) != 0)
            {
                // Already choosing a server: the second click is a no-op rather than a second switch.
                __result = Task.CompletedTask;
                return false;
            }

            __result = CreateOnBestServerAsync(anchors, __instance);
            return false;
        }
        catch (Exception exception)
        {
            WarnOnce(
                "create",
                "the server choice could not start, so the room is created on the current server: " +
                PoolRoomClient.Describe(exception));
            return true;
        }
    }

    private static async Task CreateOnBestServerAsync(PoolAnchors anchors, object overlay)
    {
        try
        {
            try
            {
                PoolRefreshFix.SetStatus(overlay, "正在选择延迟最低且可建房的服务器…");
                await SwitchToBestServerAsync(anchors, overlay).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                WarnOnce("create-switch", "choosing a server for the room failed: " + PoolRoomClient.Describe(exception));
            }

            // The original always runs: a room hosted on the wrong (but working) server beats no room at all.
            await InvokeCreateAsync(anchors, overlay).ConfigureAwait(true);
        }
        finally
        {
            // Held until the original create is done, because a click during hosting must not switch the server the
            // host flow is publishing to.
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }

    private static async Task SwitchToBestServerAsync(PoolAnchors anchors, object overlay)
    {
        IReadOnlyList<PoolServer> servers = PoolServerCatalog.GetServers(anchors);
        if (servers.Count == 0)
        {
            return;
        }

        using var probeBudget = new CancellationTokenSource(ProbeTimeoutMs + 500);
        await ProbeStaleAsync(anchors, servers, probeBudget.Token).ConfigureAwait(true);

        List<PoolServer> allowed = servers
            .Where(server => server.LatencyMs.HasValue)
            .Where(server => string.Equals(server.GuardStatus, "allow", StringComparison.OrdinalIgnoreCase))
            .OrderBy(server => server.LatencyMs!.Value)
            .ToList();
        if (allowed.Count == 0)
        {
            return;
        }

        string current = PoolServerCatalog.CurrentBaseUrl(anchors);
        PoolServer? currentServer = servers.FirstOrDefault(server => PoolAddress.Same(server.BaseUrl, current));
        if (currentServer == null)
        {
            // The current address is not one of the servers the pool knows about, which in practice means the player
            // configured it by hand. Leaving it alone is the only defensible choice.
            return;
        }

        if (string.Equals(currentServer.GuardStatus, "allow", StringComparison.OrdinalIgnoreCase))
        {
            if (!currentServer.LatencyMs.HasValue)
            {
                // The player is on a server we could not measure; moving them on a guess is not an improvement.
                return;
            }

            if (allowed[0].LatencyMs!.Value + SwitchGainMs >= currentServer.LatencyMs.Value)
            {
                return;
            }
        }

        foreach (PoolServer candidate in allowed.Take(MaxSwitchAttempts))
        {
            if (PoolAddress.Same(candidate.BaseUrl, current))
            {
                return;
            }

            try
            {
                using var switchBudget = new CancellationTokenSource(SwitchTimeoutMs);
                object? lease = await PoolServerSwitch
                    .SwitchAsync(anchors, candidate.BaseUrl, switchBudget.Token)
                    .ConfigureAwait(true);
                PoolServerSwitch.DisposeLease(lease);
                PoolServerSwitch.RefreshServerUi(anchors, overlay);
                if (PoolAddress.Same(PoolServerCatalog.CurrentBaseUrl(anchors), candidate.BaseUrl))
                {
                    Entry.Info(
                        "[server-pool] hosting on " + candidate.ShortName(PoolText.SourceNameMaxLength) +
                        " (latency " + candidate.LatencyMs + " ms, create guard " + candidate.GuardStatus + ").");
                    return;
                }
            }
            catch (Exception exception)
            {
                WarnOnce(
                    "create-switch",
                    "switching to " + candidate.BaseUrl + " failed, trying the next candidate: " +
                    PoolRoomClient.Describe(exception));
            }
        }
    }

    /// <summary>
    /// Probes the servers whose latency/guard cache is stale, all at once and without letting one slow node hold the
    /// dialog. A server that does not answer keeps a null latency and simply drops out of the candidate list.
    /// </summary>
    private static async Task ProbeStaleAsync(
        PoolAnchors anchors,
        IReadOnlyList<PoolServer> servers,
        CancellationToken cancellationToken)
    {
        List<Task> pending = servers
            .Where(server => !PoolServerCatalog.IsProbeFresh(server))
            .Select(server => PoolServerCatalog.ProbeAsync(anchors, server, ProbeTimeoutMs, cancellationToken))
            .ToList();
        if (pending.Count == 0)
        {
            return;
        }

        Task all = Task.WhenAll(pending);
        Task budget = Task.Delay(ProbeTimeoutMs, cancellationToken);
        Task finished = await Task.WhenAny(all, budget).ConfigureAwait(true);
        if (ReferenceEquals(finished, all))
        {
            try
            {
                await all.ConfigureAwait(true);
            }
            catch (Exception)
            {
                // Individual probes already fold their own failures into the cache.
            }
        }
    }

    private static async Task InvokeCreateAsync(PoolAnchors anchors, object overlay)
    {
        Task? create;
        bool previous = _reentering;
        _reentering = true;
        try
        {
            create = anchors.CreateRoom.Invoke(overlay, null) as Task;
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

        if (create != null)
        {
            await create.ConfigureAwait(true);
        }
    }

    private static void WarnOnce(string signature, string message) =>
        PoolRefreshFix.WarnOnce(signature, message);
}
