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
            "[server-pool] hosting now starts on the lowest-latency server that can host the dialog's protocol " +
            "profile and whose create guard allows it; a server whose /probe cannot confirm the profile is never " +
            "chosen. Patched " + PoolAnchors.OverlayTypeName + ".CreateRoomAsync (LanConnectLobbyOverlay.cs:4401).");
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

            // A live room session would be destroyed by the switch (LanConnectChatRuntimePorts.cs:1160-1163) and a
            // switch already in flight owns the context; both mean "run upstream untouched".
            if (PoolServerSwitch.HasActiveRoomSession(anchors) || PoolServerSwitch.SwitchInProgress(anchors))
            {
                return true;
            }

            // Upstream rejects the create for free in these three cases before it ever contacts a server
            // (LanConnectLobbyOverlay.cs:4403-4455). Switching first would take the player off their server - leaving
            // the room, dropping the public channel and rewriting config.json - for a create that is then refused
            // anyway. Let upstream report its own error instead.
            if (!CheapCreateChecksPass(anchors, __instance))
            {
                return true;
            }

            if (Interlocked.Exchange(ref _inFlight, 1) != 0)
            {
                // Already choosing a server: the second click is a no-op rather than a second switch, said out loud.
                PoolRefreshFix.SetStatus(__instance, "正在选择大厅服务器，请稍候…");
                __result = Task.CompletedTask;
                return false;
            }

            // The dialog's picker (CreateServerPickerFix) records a pinned server; null means 自动（延迟最低）.
            string? pinned = CreateServerPickerState.SelectedBaseUrl(__instance);
            __result = pinned == null
                ? CreateOnBestServerAsync(anchors, __instance)
                : CreateOnChosenServerAsync(anchors, __instance, pinned);
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

    /// <summary>
    /// The three refusals upstream makes before it talks to any server (LanConnectLobbyOverlay.cs:4403-4408 empty room
    /// name, :4443-4448 a multiplayer continue save, :4450-4455 an already hosted room). All three are reads; a missing
    /// anchor means "cannot tell" and the pre-check passes, because blocking a legitimate create is worse than an
    /// unnecessary switch.
    /// </summary>
    private static bool CheapCreateChecksPass(PoolAnchors anchors, object overlay)
    {
        try
        {
            if (anchors.RoomNameInput?.GetValue(overlay) is Godot.LineEdit nameInput
                && string.IsNullOrWhiteSpace(nameInput.Text))
            {
                return false;
            }

            if (anchors.SaveManagerInstance?.GetValue(null) is { } saves
                && anchors.SaveManagerHasMultiplayerRun?.GetValue(saves) is true)
            {
                return false;
            }

            if (PoolServerSwitch.HasActiveHostedRoom(anchors))
            {
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            WarnOnce("create-precheck", "the create pre-checks could not be read: " + PoolRoomClient.Describe(exception));
            return true;
        }
    }

    private static async Task CreateOnBestServerAsync(PoolAnchors anchors, object overlay)
    {
        try
        {
            try
            {
                PoolRefreshFix.SetStatus(overlay, "正在选择延迟最低、支持该协议且可建房的大厅服务器…");
                await SwitchToBestServerAsync(anchors, overlay).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                WarnOnce("create-switch", "choosing a server for the room failed: " + PoolRoomClient.Describe(exception));
            }

            // The original always runs: a room hosted on the wrong (but working) server beats no room at all.
            await InvokeCreateAsync(anchors, overlay).ConfigureAwait(true);

            // Diagnostic only. A create that ends without a live room on a server whose capabilities said it could
            // host is the one case the pre-flight cannot decide on its own (the frozen selection is validated inside
            // the host flow), so it is worth one log line with the server's name attached.
            if (!PoolServerSwitch.HasActiveRoomSession(anchors))
            {
                Entry.Info(
                    "[server-pool] create finished without a live room; server=" +
                    PoolServerCatalog.CurrentBaseUrl(anchors) + ".");
            }
        }
        finally
        {
            // Held until the original create is done, because a click during hosting must not switch the server the
            // host flow is publishing to.
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }

    /// <summary>
    /// The player pinned a server in the create dialog. "Only create on that server" means exactly that: switch to it,
    /// and if the switch does not land, publish nothing and say why in the dialog's own error line. Creating on a
    /// different server than the one chosen would be the one outcome the feature exists to prevent.
    /// </summary>
    private static async Task CreateOnChosenServerAsync(PoolAnchors anchors, object overlay, string chosenBaseUrl)
    {
        try
        {
            string current = PoolServerCatalog.CurrentBaseUrl(anchors);
            if (!PoolAddress.Same(chosenBaseUrl, current))
            {
                PoolRefreshFix.SetStatus(overlay, "正在切换到所选服务器：" + ShortName(chosenBaseUrl) + "…");
                bool switched = await SwitchToServerAsync(anchors, overlay, chosenBaseUrl).ConfigureAwait(true);
                if (!switched)
                {
                    ShowCreateError(
                        anchors,
                        overlay,
                        "无法切换到所选服务器（" + ShortName(chosenBaseUrl) + "），房间没有创建。" +
                        "可以改成「自动（延迟最低）」，或换一台服务器再试。");
                    PoolRefreshFix.SetStatus(overlay, "未能切换到所选服务器，已取消建房。");
                    WarnOnce(
                        "create-pinned-switch",
                        "the pinned server " + chosenBaseUrl + " could not be switched to, so no room was created");
                    return;
                }
            }

            await InvokeCreateAsync(anchors, overlay).ConfigureAwait(true);

            if (!PoolServerSwitch.HasActiveRoomSession(anchors))
            {
                Entry.Info(
                    "[server-pool] create finished without a live room; server=" +
                    PoolServerCatalog.CurrentBaseUrl(anchors) + " (pinned by the player).");
            }
        }
        finally
        {
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }

    /// <summary>Switches to one server and reports whether the config really ended up pointing at it.</summary>
    private static async Task<bool> SwitchToServerAsync(PoolAnchors anchors, object overlay, string baseUrl)
    {
        try
        {
            using var budget = new CancellationTokenSource(SwitchTimeoutMs);
            object? lease = await PoolServerSwitch.SwitchAsync(anchors, baseUrl, budget.Token).ConfigureAwait(true);
            PoolServerSwitch.DisposeLease(lease);
            PoolServerSwitch.RefreshServerUi(anchors, overlay);
        }
        catch (Exception exception)
        {
            WarnOnce(
                "create-pinned-switch",
                "switching to the pinned server " + baseUrl + " failed: " + PoolRoomClient.Describe(exception));
        }

        return PoolAddress.Same(PoolServerCatalog.CurrentBaseUrl(anchors), baseUrl);
    }

    /// <summary>Writes the dialog's own error line, preferring the host mod's helper so styling stays upstream's.</summary>
    private static void ShowCreateError(PoolAnchors anchors, object overlay, string message)
    {
        try
        {
            if (anchors.ShowCreateDialogError != null)
            {
                anchors.ShowCreateDialogError.Invoke(overlay, new object?[] { message, true });
                return;
            }

            if (anchors.CreateDialogErrorLabel?.GetValue(overlay) is Godot.Label label)
            {
                label.Text = message;
                label.Visible = true;
            }
        }
        catch (Exception exception)
        {
            WarnOnce("create-error", "the dialog error line could not be written: " + PoolRoomClient.Describe(exception));
        }
    }

    private static string ShortName(string url) => PoolAddress.ShortName(null, url, PoolText.SourceNameMaxLength);

    private static async Task SwitchToBestServerAsync(PoolAnchors anchors, object overlay)
    {
        IReadOnlyList<PoolServer> servers = PoolServerCatalog.GetServers(anchors);
        if (servers.Count == 0)
        {
            return;
        }

        // Probing both the metrics and the protocol capabilities: the second one is what keeps us off a server
        // that will freeze a protocol the client cannot accept.
        using var probeBudget = new CancellationTokenSource(ProbeTimeoutMs + 800);
        await ProbeStaleAsync(anchors, servers, probeBudget.Token).ConfigureAwait(true);

        (string? profile, int version) = ResolveOwnProfile(anchors, overlay);
        if (profile == null)
        {
            // Without knowing which profile the dialog is about to request there is nothing to verify against, and
            // switching blind is what produced the mobile "LAN 协议版本不匹配" reports.
            WarnOnce(
                "profile",
                "the create dialog's protocol profile could not be resolved, so the room stays on the current server");
            return;
        }

        List<PoolServer> allowed = servers
            .Where(server => server.LatencyMs.HasValue)
            .Where(server => string.Equals(server.GuardStatus, "allow", StringComparison.OrdinalIgnoreCase))
            .Where(server => PoolServerCatalog.SupportsProfile(server, profile, version))
            .OrderBy(server => server.LatencyMs!.Value)
            .ToList();

        string current = PoolServerCatalog.CurrentBaseUrl(anchors);
        PoolServer? currentServer = servers.FirstOrDefault(server => PoolAddress.Same(server.BaseUrl, current));
        if (currentServer == null)
        {
            // The current address is not one of the servers the pool knows about, which in practice means the player
            // configured it by hand. Leaving it alone is the only defensible choice.
            return;
        }

        bool currentCapable = PoolServerCatalog.SupportsProfile(currentServer, profile, version);
        Entry.Info(
            "[server-pool] create profile=" + profile + " v" + version + "; " + allowed.Count + " of " +
            servers.Count + " servers can host it (current " + currentServer.BaseUrl + " " +
            (currentCapable ? "can" : "cannot") + ").");

        if (allowed.Count == 0)
        {
            // Nothing can host this profile: stay where the player is. A room that fails to publish is worse than a
            // room on a slow server, so no switch is the answer, not the least-bad switch.
            WarnOnce(
                "no-capable-server",
                "no known server advertises the protocol profile this room needs, so it is created on the current " +
                "server instead of switching");
            return;
        }

        if (currentCapable)
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

                // The switch reported success but the config still points elsewhere: treat it as "we are now on that
                // server" for the rest of the loop instead of comparing every later candidate against a stale value.
                current = PoolServerCatalog.CurrentBaseUrl(anchors);
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
    /// The canonical profile this create is going to ask for, derived the way upstream derives it: the dialog's
    /// selected protocol id (LanConnectLobbyOverlay.cs:6664-6669) into LanConnectProtocolProfile
    /// (:6780-6787), then to its canonical wire string. The LAN protocol version is the same value
    /// LanConnectProtocolOffer.CreateCurrent() puts in the offer: LanConnectConstants.TailLanProtocolVersion when the
    /// tail runtime is available, 0 otherwise (LanConnectProtocolOffer.cs:13-51).
    ///
    /// Nothing here creates a protocol offer: CreateCurrent() reads the game's message registry and native bus id and
    /// has no business running in a patch body before the create does.
    /// </summary>
    private static (string? Profile, int Version) ResolveOwnProfile(PoolAnchors anchors, object overlay)
    {
        try
        {
            if (anchors.GetSelectedCreateProtocolId == null
                || anchors.ProtocolProfileFromCreateId == null
                || anchors.ProfileToCanonical == null)
            {
                return (null, 0);
            }

            if (anchors.GetSelectedCreateProtocolId.Invoke(overlay, null) is not int protocolId)
            {
                return (null, 0);
            }

            object? profile = anchors.ProtocolProfileFromCreateId.Invoke(null, new object?[] { protocolId });
            if (profile == null)
            {
                return (null, 0);
            }

            string? canonical = anchors.ProfileToCanonical.Invoke(null, new object?[] { profile }) as string;
            if (string.IsNullOrWhiteSpace(canonical))
            {
                return (null, 0);
            }

            int version = 0;
            if (string.Equals(canonical, PoolServerCatalog.TailProfileCanonical, StringComparison.OrdinalIgnoreCase)
                && anchors.TailRuntimeAvailable?.GetValue(null) is true)
            {
                version = anchors.TailLanProtocolVersion;
            }

            return (canonical, version);
        }
        catch (Exception exception)
        {
            WarnOnce("profile", "resolving the create dialog's protocol profile failed: " + PoolRoomClient.Describe(exception));
            return (null, 0);
        }
    }

    /// <summary>
    /// Probes the servers whose latency/guard/protocol cache is stale, all at once and without letting one slow node
    /// hold the dialog. A server that does not answer keeps a null latency and simply drops out of the candidates.
    /// </summary>
    private static async Task ProbeStaleAsync(
        PoolAnchors anchors,
        IReadOnlyList<PoolServer> servers,
        CancellationToken cancellationToken)
    {
        List<Task> pending = servers
            // A node in failure cooldown is not a candidate anyway and asking it would cost the dialog its budget.
            .Where(server => !PoolServerCatalog.IsCoolingDown(server))
            .Where(PoolServerCatalog.NeedsProbe)
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
