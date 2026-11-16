using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;

namespace Sts2LobbyEnhance;

    /// <summary>
    /// Puts ‹ › and a ⟳ button in the lobby chat panel's title row; they reattach the public channel to another
    /// server's channel. The lobby server, the room and config.json are untouched - only which server's public chat
    /// this client is attached to changes. Also attaches that channel once when the sidebar first becomes visible,
    /// because upstream only ever connects it from the picker's switch.
    /// </summary>
internal sealed class ChatServerSwitchFix : ICompatFix
{
    private const string PanelTypeName = "Sts2LanConnect.Scripts.LanConnectBasicChatPanel";

    private const string OverlayTypeName = "Sts2LanConnect.Scripts.LanConnectLobbyOverlay";

    private const string AutoRefreshFieldName = "_timeUntilAutoRefresh";

    private const string RuntimeTypeName = "Sts2LanConnect.Scripts.LanConnectLobbyRuntime";

    private const string ConfigTypeName = "Sts2LanConnect.Scripts.LanConnectConfig";

    private const string UpdateAvailabilityMethodName = "UpdateAvailability";

    private const string ProcessMethodName = "_Process";

    private const string CreateButtonMethodName = "CreateButton";

    private const string ChatStylePropertyName = "ChatVisualStyle";

    private const string TextStrongColorPropertyName = "TextStrongColor";

    private const string IconsFieldName = "_icons";

    private const string IconsGetMethodName = "Get";

    /// <summary>Lucide's "refresh-cw", the same four paths the lobby's REFRESH button draws.</summary>
    private const string ResetIconName = "refresh-cw";

    private const string ResetFallbackGlyph = "⟳";

    private const int ResetFallbackFontSize = 16;

    private const string RuntimeInstancePropertyName = "Instance";

    private const string ChatOwnerFieldName = "_chatOwner";

    private const string ResolveNetIdMethodName = "ResolveCurrentPlayerNetId";

    private const string RuntimeChatPropertyName = "Chat";

    private const string PortConnectMethodName = "ConnectAsync";

    private const string ConfigBaseUrlPropertyName = "LobbyServerBaseUrl";

    private const string DisplayNameMethodName = "GetEffectivePlayerDisplayName";

    private const string DualServerPropertyName = "Server";

    private const string PresentationPropertyName = "Presentation";

    private const string ChatEnabledPropertyName = "ChatEnabled";

    private const string PortCurrentPropertyName = "Current";

    private const string CoordinatorClientFieldName = "_client";

    private const string ClientTypeName = "Sts2LanConnect.Scripts.LanConnectServerChatClient";

    private const string ClientBaseUriFieldName = "_lobbyBaseUri";

    /// <summary>Upstream's node name for the sidebar chat panel.</summary>
    private const string LobbyPanelNodeName = "LobbyServerChatPanel";

    /// <summary>The value of upstream's <c>LanConnectConstants.LobbyOverlayName</c>.</summary>
    private const string OverlayNodeName = "LanConnectLobbyOverlay";

    /// <summary>The sidebar panel's <c>ChatVisualStyle</c> value.</summary>
    private const string LobbyStyleName = "LobbySidebar";

    private const string SwitchRowNodeName = "LobbyEnhanceChatServerSwitch";

    /// <summary>Per-label meta key holding the suffix this repair appended, so the next pass can strip it.</summary>
    private const string TitleSuffixMetaKey = "sts2le_chat_title_suffix";

    private const string TitleSeparator = " · ";

    /// <summary>Short name limit in the lobby sidebar; it is 200 px wide before layout gives it more.</summary>
    private const int MaxNameChars = 8;

    private const float ArrowSize = 24f;

    private const float ArrowFontSize = 16f;

    private const int RowSeparation = 4;

    /// <summary>
    /// Below this the status label (ShrinkEnd, clipped to zero) is hidden so the title still has room for
    /// "频道聊天 · 魔仙堡" plus the arrows. The server name lives inside the title text, so it never competes with
    /// the row for space - only the status does.
    /// </summary>
    private const float CompactRowWidth = 260f;

    private const string LocalServerLabel = "本服";

    /// <summary>What <see cref="ChatServerCatalog.ShortName"/> says when there is neither a name nor an address.</summary>
    private const string UnknownServerLabel = "未知服务器";

    private const int MaxAttempts = 5;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(12d);

    /// <summary>Whole-walk budget, checked between hops only.</summary>
    private static readonly TimeSpan WalkBudget = TimeSpan.FromSeconds(45d);

    private static readonly TimeSpan UsableStateInterval = TimeSpan.FromMilliseconds(500d);

    private const int UsableStateSamples = 6;

    private static Shape? _shape;

    private static readonly ConditionalWeakTable<Control, RowWidgets> Rows = new();

    private static SwitchState _state = SwitchState.Initial;

    private static int _busy;

    private static string? _observedConfigUrl;

    /// <summary>One automatic first connect per process: after it, the button is the only way back to a channel.</summary>
    private static int _firstConnectAttempted;



    private static int _warned;

    private static int _nameLookupWarned;

    /// <summary>Separate from <see cref="_warned"/> so a shape notice cannot swallow the first real failure.</summary>
    private static int _shapeNoticed;

    /// <summary>Set when a switch settles; consumed once on the next main-thread tick.</summary>
    private static int _refreshNudgePending;

    /// <summary>
    /// Bumped by every walk and by every lobby-server change. A walk that is no longer current must not write a
    /// settled state, or the label would keep pointing at a server the channel never reached.
    /// </summary>
    private static int _stepGeneration;

    /// <summary>
    /// Lines produced on a worker thread and logged from the next main-thread tick. The game's logger is not ours
    /// to call from a pool thread.
    /// </summary>
    private static readonly Queue<string> PendingInfo = new();

    /// <summary>One line per rendering path, so a real-machine log says which one the button actually used.</summary>
    private static int _iconLoggedTexture;

    private static int _iconLoggedFallback;

    private static int _nameLogged;

    private static int _configWarned;

    public string Id => "chat-server-switch";

    /// <summary>
    /// A shape change in the chat panel rolls back this repair and room-chat-hint-passthrough, the other member of
    /// group Chat. The touch, pool and announce repairs are separate groups.
    /// </summary>
    public FixGroup Group => FixGroup.Chat;

    public bool Applies(out string reason)
    {
        Assembly? host = Reflect.FindAssembly(Entry.HostModAssemblyName);
        if (host == null)
        {
            reason = "host mod '" + Entry.HostModAssemblyName + "' is not loaded";
            return false;
        }

        var problems = new List<string>();
        Shape? shape = null;
        try
        {
            shape = Shape.TryCapture(host, problems);
        }
        catch (Exception exception)
        {
            problems.Add(exception.GetType().Name + ": " + exception.Message);
        }

        if (shape == null || problems.Count > 0)
        {
            reason = "missing: " + string.Join(", ", problems);
            return false;
        }

        _shape = shape;
        reason = string.Empty;
        return true;
    }

    public void Apply(Harmony harmony)
    {
        Shape shape = _shape ?? throw new InvalidOperationException(
            "chat-server-switch: Applies() did not capture the chat panel shape; refusing to patch blind.");

        // The rotating port builds a new chat client and coordinator per call, so the overlay rebinds the panel by
        // itself. The runtime's own ConnectServerChatAsync cannot do this: it reconnects the same client, whose
        // connect latch fires once and whose permanent stop is never retried.
        harmony.Patch(
            shape.PortConnect,
            postfix: new HarmonyMethod(typeof(ChatServerSwitchFix), nameof(PortConnectPostfix))
            {
                priority = Priority.Last
            });

        harmony.Patch(
            shape.UpdateAvailability,
            postfix: new HarmonyMethod(typeof(ChatServerSwitchFix), nameof(UpdateAvailabilityPostfix))
            {
                priority = Priority.Last
            });

        harmony.Patch(
            shape.Process,
            postfix: new HarmonyMethod(typeof(ChatServerSwitchFix), nameof(ProcessPostfix))
            {
                priority = Priority.Last
            });

        Entry.Info(
            "chat-server-switch: the lobby public channel row now offers ‹ › to attach to another server's " +
            "channel after a /probe preflight (capabilities.serverChatVersion == 1, cached 5 min). The first " +
            "time the sidebar becomes visible the channel is also attached to the configured server once, because " +
            "upstream only ever connects it from the pick a server dialog the lobby entry skips. The lobby " +
            "server, the room and config.json are untouched. The in-run chat panel is not touched at all: " +
            "switching the channel from a run used to cost the room history, so that entry point is gone.");
    }

    /// <summary>
    /// Runs after upstream has just rewritten the title and the status text, so the row is refreshed on the same
    /// pass instead of a frame later.
    /// </summary>
    // ReSharper disable once UnusedParameter.Local (Harmony binds by name).
    private static void UpdateAvailabilityPostfix(object __instance)
    {
        Sync(__instance);
    }

    /// <summary>
    /// Per-frame tick. Upstream refreshes the title from state events only, so a switch that completes on a
    /// worker thread has no other place to become visible in the same frame.
    /// </summary>
    // ReSharper disable once UnusedParameter.Local (Harmony binds by name).
    private static void ProcessPostfix(object __instance)
    {
        Sync(__instance);
    }

    private static void Sync(object? instance)
    {
        Shape? shape = _shape;
        if (shape == null || instance is not Control panel)
        {
            return;
        }

        if (!GodotObject.IsInstanceValid(panel) || !panel.IsInsideTree())
        {
            return;
        }

        try
        {
            SyncCore(shape, panel);
        }
        catch (Exception exception)
        {
            // A missing row costs the switch row only: leave upstream's title row as it is and say so once.
            WarnOnce("could not update the public channel switch row: " +
                     exception.GetType().Name + ": " + exception.Message);
        }
    }

    private static void SyncCore(Shape shape, Control panel)
    {
        if (!IsLobbySidebarPanel(shape, panel))
        {
            // Only the lobby sidebar carries the switch row. The in-run overlay is upstream's business: it has its
            // own channel tabs and a channel switch there cost the room history, so this repair stays out of it.
            return;
        }

        if (Interlocked.Exchange(ref _refreshNudgePending, 0) != 0)
        {
            // Main thread (a Harmony postfix on the panel), so reaching into the scene tree is safe here. Only the
            // timer is touched: the lobby's own refresh then re-fetches rooms and announcements from the server
            // the channel is now attached to.
            NudgeOverlayAutoRefresh(shape);
        }

        // Before Bind() the panel has no state and its title still says the localized default: nothing to report.
        if (shape.ChatState.GetValue(panel) == null)
        {
            return;
        }

        RowWidgets? widgets = EnsureWidgets(shape, panel);
        if (widgets == null)
        {
            return;
        }

        string configUrl = ReadConfigBaseUrl(shape);
        if (_observedConfigUrl == null)
        {
            _observedConfigUrl = configUrl;
        }
        else if (!string.Equals(_observedConfigUrl, configUrl, StringComparison.OrdinalIgnoreCase))
        {
            // The player switched lobby servers through upstream's own picker, so "本服" moved with them.
            _observedConfigUrl = configUrl;
            // Any walk still in flight belongs to the server the player just left: void it before it can write.
            Interlocked.Increment(ref _stepGeneration);
            Volatile.Write(ref _state, SwitchState.Initial);
            ChatServerCatalog.Invalidate();
            Interlocked.Exchange(ref _refreshNudgePending, 1);
        }

        SwitchState state = Volatile.Read(ref _state);

        // The name is recomputed from what the channel is actually attached to, not from what the last step asked
        // for: a step can outlive the lobby server it started under, and reading the live client every frame heals
        // that race on the next frame.
        string localName = ResolveLocalName(configUrl);
        string? actual = ReadLiveTargetAddress(shape) ?? state.TargetAddress;
        ConnectFirstChannelOnce(shape, panel, configUrl, actual, state);
        bool busy = state.Phase is SwitchPhase.Probing or SwitchPhase.Connecting;
        string target = CurrentTargetLabel(state, localName, actual, configUrl);
        (string text, string tooltip) = Describe(state, target, configUrl, actual);

        // The title is written before anything else in this method can fail: the name rides on the title text itself
        // ("频道聊天 · 魔仙堡", arrows furthest right), so a later throw still leaves the row named.
        if (shape.TitleLabel.GetValue(panel) is Label title && GodotObject.IsInstanceValid(title))
        {
            ApplyTitle(title, text, tooltip, widgets);
        }

        float width = panel.Size.X;
        bool compact = width > 0f && width < CompactRowWidth;

        Label? status = shape.StatusLabel.GetValue(panel) as Label;
        if (status != null && GodotObject.IsInstanceValid(status))
        {
            bool visible = !compact;
            if (status.Visible != visible)
            {
                status.Visible = visible;
            }
        }

        // This runs every frame, and none of the setters below compares before it acts (Godot's tooltip setter
        // refreshes the control's configuration warnings on every write), so only a changed row is written.
        if (widgets.Busy != busy || !string.Equals(widgets.Target, target, StringComparison.Ordinal))
        {
            widgets.Busy = busy;
            widgets.Target = target;
            if (widgets.Reset.Disabled != busy)
            {
                widgets.Reset.Disabled = busy;
                widgets.Previous.Disabled = busy;
                widgets.Next.Disabled = busy;
            }

            widgets.Previous.TooltipText = busy ? "正在切换，请稍候" : "切换到上一台服务器的公共频道 · 当前 " + target;
            widgets.Next.TooltipText = busy ? "正在切换，请稍候" : "切换到下一台服务器的公共频道 · 当前 " + target;
        }

        ConsumePendingInfo();
    }

    /// <summary>
    /// Rewrites the title to the panel's own text plus this repair's suffix without accumulating suffixes: upstream
    /// rewrites the title from inside the panel on every state change, and the per-frame tick also runs on frames
    /// where upstream did not, so whatever this repair appended last is stripped first.
    /// </summary>
    private static void ApplyTitle(Label title, string name, string tooltip, RowWidgets widgets)
    {
        if (!title.ClipText)
        {
            title.ClipText = true;
        }

        if (title.TextOverrunBehavior != TextServer.OverrunBehavior.TrimEllipsis)
        {
            title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        }

        // Remembered on the label: on a frame where upstream did not rewrite the text, last frame's suffix is still
        // in it and has to come off before this frame's goes on.
        string applied = ReadTitleSuffix(title);
        string baseText = title.Text;
        if (applied.Length > 0 && baseText.EndsWith(applied, StringComparison.Ordinal))
        {
            baseText = baseText[..^applied.Length];
        }

        // A blank name would render as a dangling " · ".
        string shown = string.IsNullOrWhiteSpace(name) ? LocalServerLabel : name.Trim();
        string suffix = TitleSeparator + shown;
        string next = baseText + suffix;
        if (!string.Equals(title.Text, next, StringComparison.Ordinal))
        {
            title.Text = next;
        }

        if (!string.Equals(applied, suffix, StringComparison.Ordinal))
        {
            title.SetMeta(TitleSuffixMetaKey, suffix);
        }

        // Upstream only ever writes this label's Text, so the tooltip it shows is the one written here last.
        if (!ReferenceEquals(widgets.TitleLabel, title) ||
            !string.Equals(widgets.TitleTooltip, tooltip, StringComparison.Ordinal))
        {
            widgets.TitleLabel = title;
            widgets.TitleTooltip = tooltip;
            title.TooltipText = tooltip;
        }
    }

    private static string ReadTitleSuffix(Label title) =>
        title.HasMeta(TitleSuffixMetaKey) ? title.GetMeta(TitleSuffixMetaKey).AsString() : string.Empty;

    /// <summary>
    /// True while this walk still owns the state. A lobby-server change bumps the generation, and a voided walk
    /// publishing a target is the race a player triggers by switching the chat server and the lobby server in quick
    /// succession.
    /// </summary>
    private static bool IsCurrentGeneration(int generation) =>
        generation == Volatile.Read(ref _stepGeneration);

    /// <summary>
    /// One normalisation for every address in this file: the walk's target, the live client's URI and the
    /// directory's entries are compared through the catalogue's own <c>Normalize</c>, so a trailing slash or a
    /// case difference can never make the same server look like two.
    /// </summary>
    private static bool SameAddress(string? left, string? right) =>
        string.Equals(
            ChatServerCatalog.Normalize(left),
            ChatServerCatalog.Normalize(right),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What the public channel is attached to right now, read from the live client: the port's current coordinator,
    /// that coordinator's client, and the client's base URI.
    ///
    /// This is the only value that cannot lie: our own state records what a step asked for, and a step can outlive
    /// the lobby server it was started under. A missing link answers null, which the label reads as "fall back to
    /// the configured server".
    /// </summary>
    private static string? ReadLiveTargetAddress(Shape shape)
    {
        try
        {
            object? runtime = shape.RuntimeInstance.GetValue(null);
            if (runtime == null)
            {
                return null;
            }

            object? port = shape.ChatOwner.GetValue(runtime);
            if (port == null)
            {
                return null;
            }

            object? coordinator = shape.PortCurrent.GetValue(port);
            if (coordinator == null)
            {
                return null;
            }

            object? client = shape.CoordinatorClient.GetValue(coordinator);
            if (client == null || !shape.ClientBaseUri.DeclaringType!.IsInstanceOfType(client))
            {
                return null;
            }

            return shape.ClientBaseUri.GetValue(client) is Uri uri ? uri.ToString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Upstream connects the public channel only as part of a lobby-server switch, and that switch has one caller:
    /// the pick a server dialog hands back. The lobby entry skips the dialog, so on a fresh start nothing ever
    /// called the port and the sidebar stays empty until the player presses ⟳.
    /// </summary>
    private static void ConnectFirstChannelOnce(
        Shape shape,
        Control panel,
        string configUrl,
        string? actual,
        SwitchState state)
    {
        if (Volatile.Read(ref _firstConnectAttempted) != 0 || !string.IsNullOrWhiteSpace(actual))
        {
            return;
        }

        // Only once the sidebar is on screen: at startup the overlay is hidden and nothing asked for a channel yet.
        if (!panel.IsVisibleInTree() ||
            state.Phase is SwitchPhase.Probing or SwitchPhase.Connecting ||
            Volatile.Read(ref _busy) != 0 ||
            !Uri.TryCreate(configUrl, UriKind.Absolute, out _))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _firstConnectAttempted, 1, 0) != 0)
        {
            return;
        }

        try
        {
            object? runtime = shape.RuntimeInstance.GetValue(null);
            string netId = runtime == null
                ? string.Empty
                : shape.ResolveNetId.Invoke(runtime, null) as string ?? string.Empty;
            string playerName = shape.DisplayName.Invoke(null, null) as string ?? "玩家";
            if (runtime == null || string.IsNullOrWhiteSpace(netId))
            {
                // The runtime is not up yet; leave the attempt for a later frame.
                Interlocked.Exchange(ref _firstConnectAttempted, 0);
                return;
            }

            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                Interlocked.Exchange(ref _firstConnectAttempted, 0);
                return;
            }

            int generation = Interlocked.Increment(ref _stepGeneration);
            SetState(new SwitchState(null, null, SwitchPhase.Probing, null), nudgeRefresh: false);
            Entry.Info("chat-server-switch: 大厅首次打开，公共频道从未连过，自动连一次本服 " + configUrl + "。");
            Task ignored = Task.Run(
                () => FirstConnectAsync(shape, runtime, configUrl, netId, playerName, generation));
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _busy, 0);
            WarnOnce("could not start the first channel connect: " +
                     exception.GetType().Name + ": " + exception.Message);
        }
    }

    /// <summary>
    /// Connects to the configured server only after it advertises public channel v1, because the port replaces the
    /// coordinator before it connects: a server without the protocol would leave the sidebar on upstream's
    /// "unsupported" presentation instead of an empty list.
    /// </summary>
    private static async Task FirstConnectAsync(
        Shape shape,
        object runtime,
        string address,
        string netId,
        string playerName,
        int generation)
    {
        SwitchState previous = Volatile.Read(ref _state);
        try
        {
            ProbeOutcome probe = await ChatServerCatalog.ProbeAsync(address).ConfigureAwait(false);
            if (!IsCurrentGeneration(generation))
            {
                return;
            }

            if (!probe.Reachable)
            {
                SetState(FailureState(previous, replacedConnection: false, "本服探测不到"), nudgeRefresh: false);
                DeferInfo("首次进入：本服 " + address + " 探测不到，公共频道没有连接，可点 ⟳ 重试。");
                return;
            }

            if (!probe.Supported)
            {
                SetState(FailureState(previous, replacedConnection: false, "本服没有公共频道 v1"), nudgeRefresh: false);
                DeferInfo("首次进入：本服 " + address + " 没有声明公共频道协议 v1，公共频道保持未连接。");
                return;
            }

            SetState(new SwitchState(address, null, SwitchPhase.Connecting, null), nudgeRefresh: false);
            string? rejected = await ConnectAndVerifyAsync(shape, runtime, address, netId, playerName)
                .ConfigureAwait(false);
            if (!IsCurrentGeneration(generation))
            {
                return;
            }

            if (rejected != null)
            {
                SetState(FailureState(previous, replacedConnection: true, rejected), nudgeRefresh: false);
                DeferInfo("首次进入：连接本服的公共频道失败：" + rejected);
                return;
            }

            SetState(new SwitchState(address, null, SwitchPhase.Idle, null), nudgeRefresh: false);
            DeferInfo("首次进入：公共频道已连上本服 " + address + "。");
        }
        catch (Exception exception)
        {
            if (IsCurrentGeneration(generation))
            {
                SetState(
                    FailureState(previous, replacedConnection: true,
                        exception.GetType().Name + ": " + exception.Message),
                    nudgeRefresh: false);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>
    /// Emits the deferred "target has no public channel" line on the main thread. The switch itself runs on a
    /// worker, where touching the game's logger is not ours to do.
    /// </summary>
    private static void DeferInfo(string message)
    {
        lock (PendingInfo)
        {
            if (PendingInfo.Count < 8)
            {
                PendingInfo.Enqueue(message);
            }
        }
    }

    private static void ConsumePendingInfo()
    {
        while (true)
        {
            string message;
            lock (PendingInfo)
            {
                if (!PendingInfo.TryDequeue(out message!))
                {
                    return;
                }
            }

            Entry.Info("chat-server-switch: " + message);
        }
    }

    /// <summary>
    /// Swallows exactly one exception type out of the port's final connect step.
    ///
    /// Upstream's switch persists the new lobby address and only then awaits the chat connect, so a target without
    /// public channel v1 throws <see cref="NotSupportedException"/> out of the whole switch after the address is
    /// already saved. Any other exception still faults the task.
    /// </summary>
    private static async Task SwallowCoreAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            DeferInfo(
                "目标服务器没有公共频道 v1，已放行大厅切服（LanConnectChatRuntimePorts.cs:837）；聊天面板会显示不支持。");
        }
    }

    /// <summary>
    /// A postfix on an <c>async</c> method is the only honest place to do this: every exception of such a method
    /// is captured in its returned task, so replacing the task is what decides whether the caller sees it.
    /// </summary>
    private static void PortConnectPostfix(ref object __result)
    {
        try
        {
            if (__result is Task task)
            {
                __result = SwallowCoreAsync(task);
            }
        }
        catch (Exception exception)
        {
            WarnOnce("could not soften the channel connect result: " +
                     exception.GetType().Name + ": " + exception.Message);
        }
    }

    /// <summary>
    /// Zeroes the lobby overlay's own auto-refresh countdown so the next frame runs one ordinary refresh instead of
    /// waiting out the interval. Nothing is fetched from here and no timer of our own is created.
    /// </summary>
    private static void NudgeOverlayAutoRefresh(Shape shape)
    {
        try
        {
            SceneTree? tree = Engine.GetMainLoop() as SceneTree;
            Node? root = tree?.Root;
            if (root == null)
            {
                return;
            }

            Node? overlay = root.FindChild(OverlayNodeName, recursive: true, owned: false);
            if (overlay == null || !shape.OverlayType.IsInstanceOfType(overlay))
            {
                return;
            }

            shape.AutoRefresh.SetValue(overlay, 0d);
        }
        catch (Exception exception)
        {
            WarnOnce("could not request an immediate lobby refresh: " +
                     exception.GetType().Name + ": " + exception.Message);
        }
    }

    /// <summary>
    /// Where the public channel is attached right now; shown in the title and in the arrows' tooltips.
    /// </summary>
    /// <param name="actual">
    /// The address read from the live client this frame, or the last settled target when that read failed. It is
    /// the authority: the state may describe a step that a newer lobby-server change has voided.
    /// </param>
    private static string CurrentTargetLabel(
        SwitchState state,
        string localName,
        string? actual,
        string configUrl)
    {
        if (state.Phase == SwitchPhase.Unavailable)
        {
            return "频道暂不可用";
        }

        if (string.IsNullOrWhiteSpace(actual))
        {
            return localName;
        }

        // 1. The directory owns names: that is where the operator's display names arrive from.
        if (ChatServerCatalog.TryLookupDisplayName(actual, configUrl, out string fromDirectory) &&
            !IsUnknownName(fromDirectory))
        {
            return NonEmpty(ChatServerCatalog.ShortName(fromDirectory, actual, MaxNameChars), actual);
        }

        // 2. On the configured lobby server, the name ResolveLocalName already asked the same gatherer for. The
        //    directory above can miss this address while its snapshot is still being built, and falling through
        //    would print the IP even though the name is known.
        if (SameAddress(actual, configUrl))
        {
            return localName;
        }

        // 3. The name the walk carried, but only for the address it was picked for.
        if (SameAddress(actual, state.TargetAddress) &&
            !string.IsNullOrWhiteSpace(state.TargetName) &&
            !IsUnknownName(state.TargetName))
        {
            return NonEmpty(ChatServerCatalog.ShortName(state.TargetName, actual, MaxNameChars), actual);
        }

        // 4. Nothing but the address is left. Say so once per address: a label stuck on an IP is a directory
        //    problem, and this line is the only way to tell it apart from a name that simply is not set.
        LogLabelFallback(actual);
        return NonEmpty(ChatServerCatalog.ShortName(null, actual, MaxNameChars), actual);
    }

    private static bool IsUnknownName(string? name) =>
        string.IsNullOrWhiteSpace(name) ||
        string.Equals(name.Trim(), UnknownServerLabel, StringComparison.Ordinal);

    private static string NonEmpty(string candidate, string address)
    {
        if (!string.IsNullOrWhiteSpace(candidate) && !IsUnknownName(candidate))
        {
            return candidate;
        }

        string host = ChatServerCatalog.HostOf(address);
        return host.Length > 0 ? ChatServerCatalog.ShortName(host, address, MaxNameChars) : LocalServerLabel;
    }

    private static readonly HashSet<string> LabelFallbackLogged = new(StringComparer.OrdinalIgnoreCase);

    private static void LogLabelFallback(string address)
    {
        string key = ChatServerCatalog.Normalize(address);
        lock (LabelFallbackLogged)
        {
            if (LabelFallbackLogged.Count > 32 || !LabelFallbackLogged.Add(key))
            {
                return;
            }
        }

        Entry.Info("chat-server-switch: label fallback: no display name for " + address);
    }

    private static (string Text, string Tooltip) Describe(
        SwitchState state,
        string target,
        string configUrl,
        string? actual)
    {
        switch (state.Phase)
        {
            case SwitchPhase.Probing:
                return ("探测中…", "正在探测可切换的大厅服务器（GET /probe）");
            case SwitchPhase.Connecting:
                return ("连接中…", "正在连接目标服务器的公共频道");
            case SwitchPhase.Unavailable:
                return ("频道暂不可用", state.Problem ?? "没有探测到支持公共频道协议 v1 的服务器");
            default:
                break;
        }

        string text = target;
        string detail = string.IsNullOrWhiteSpace(actual) || SameAddress(actual, configUrl)
            ? $"当前：本服 {configUrl}。点 ‹ › 切换其它服务器的公共频道"
            : $"当前：{actual}。点 ‹ › 切换其它服务器的公共频道";

        // A refused switch keeps the existing connection, so the reason belongs in the tooltip instead of the row.
        if (!string.IsNullOrEmpty(state.Problem))
        {
            detail += "\n上次尝试：" + state.Problem;
        }

        return (text, detail);
    }

    /// <summary>
    /// The short name of the server the panel is attached to, for the title's tail: the operator name the server
    /// list carries, else the address host, else "本服". The last one is what keeps the tail non-empty, because a
    /// blank tail reads worse than an imprecise one.
    /// </summary>
    private static string ResolveLocalName(string configUrl)
    {
        string name;
        string source;
        try
        {
            if (ChatServerCatalog.TryResolveDisplayName(configUrl, out string displayName) &&
                !string.IsNullOrWhiteSpace(displayName))
            {
                name = ChatServerCatalog.ShortName(displayName, configUrl, MaxNameChars);
                source = "display name";
            }
            else
            {
                name = ChatServerCatalog.ShortName(null, configUrl, MaxNameChars);
                source = name.Length == 0 ? "fallback" : "address host";
            }
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _nameLookupWarned, 1) == 0)
            {
                Entry.Warn(
                    "chat-server-switch: the current server's name is unavailable: " +
                    exception.GetType().Name + ": " + exception.Message);
            }

            name = string.Empty;
            source = "exception";
        }

        // "未知服务器" is ShortName's answer for "no name and no address"; for the server we are already on, the
        // honest label is "本服".
        if (string.IsNullOrWhiteSpace(name) ||
            string.Equals(name, UnknownServerLabel, StringComparison.Ordinal))
        {
            name = LocalServerLabel;
            source = "local label";
        }

        if (Interlocked.Exchange(ref _nameLogged, 1) == 0)
        {
            Entry.Info(
                "chat-server-switch: public channel label resolved from " + source + ": '" + name +
                "' (config base url: '" + configUrl + "').");
        }

        return name;
    }

    /// <summary>
    /// True only for the lobby sidebar's chat panel. The room overlay builds the same class and is deliberately left
    /// alone: a channel switch there costs the room history.
    /// </summary>
    private static bool IsLobbySidebarPanel(Shape shape, Control panel)
    {
        object? style = shape.ChatStyle.GetValue(panel);
        if (style == null || !string.Equals(style.ToString(), LobbyStyleName, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(panel.Name.ToString(), LobbyPanelNodeName, StringComparison.Ordinal))
        {
            return true;
        }

        if (Interlocked.Exchange(ref _shapeNoticed, 1) == 0)
        {
            Entry.Warn(
                "chat-server-switch: the lobby chat panel is named '" + panel.Name +
                "' instead of '" + LobbyPanelNodeName + "', so the switch row is left out. The panel looks" +
                " otherwise correct; upstream most likely renamed the node.");
        }

        return false;
    }

    private static RowWidgets? EnsureWidgets(Shape shape, Control panel)
    {
        if (shape.TitleLabel.GetValue(panel) is not Label title || !GodotObject.IsInstanceValid(title))
        {
            return null;
        }

        // The title row is an HBoxContainer; anything else means the layout was reworked, and guessing where the row
        // belongs would produce the "奇形怪状" this mod avoids.
        if (title.GetParent() is not HBoxContainer titleRow)
        {
            return null;
        }

        Node? existing = titleRow.FindChild(SwitchRowNodeName, recursive: false, owned: false);
        if (existing is HBoxContainer row)
        {
            if (Rows.TryGetValue(row, out RowWidgets? known) && IsAlive(known))
            {
                return known;
            }

            // Ours, but its children were replaced by something else: drop it and build a clean one.
            Rows.Remove(row);
            row.QueueFree();
        }
        else if (existing != null)
        {
            // A foreign node already holds our name. Do not fight over it.
            return null;
        }

        Button previous = CreateArrowButton(shape, panel, "‹", "切换到上一台服务器的公共频道");
        Button next = CreateArrowButton(shape, panel, "›", "切换到下一台服务器的公共频道");
        Button reset = CreateResetButton(shape, panel);
        previous.Connect(Button.SignalName.Pressed, Callable.From(() => BeginStep(-1)));
        next.Connect(Button.SignalName.Pressed, Callable.From(() => BeginStep(1)));
        // No animation of any kind: a press only starts the switch. Visual feedback is the button's own
        // hover/pressed stylebox.
        reset.Connect(Button.SignalName.Pressed, Callable.From(() => BeginReset(shape)));

        var built = new HBoxContainer { Name = SwitchRowNodeName };
        built.AddThemeConstantOverride("separation", RowSeparation);
        built.AddChild(reset);
        built.AddChild(previous);
        built.AddChild(next);

        // Appended last, so the arrows are the right-most thing in the row. The status label stays where it was
        // (before them) and keeps shrinking to zero on a narrow panel instead of pushing the arrows out.
        titleRow.AddChild(built);

        var widgets = new RowWidgets(reset, previous, next);
        Rows.Add(built, widgets);
        return widgets;
    }

    /// <summary>
    /// The reset button: the arrows' styling and size, with the lobby's REFRESH glyph.
    ///
    /// Two renderings, because the icon texture is not guaranteed: the Lucide loader decodes an embedded SVG, and
    /// when that fails (a preview host without the pck, a decoder miss) a 24 px button with nothing in it is worse
    /// than the same glyph as text.
    /// </summary>
    private static Button CreateResetButton(Shape shape, Control panel)
    {
        object? created = shape.CreateButton.Invoke(panel, new object?[] { string.Empty, false });
        if (created is not Button button)
        {
            throw new InvalidOperationException(
                PanelTypeName + "." + CreateButtonMethodName + " returned no Button for the reset control.");
        }

        float size = ArrowSize;
        button.Text = string.Empty;
        button.CustomMinimumSize = new Vector2(size, size);
        button.FocusMode = Control.FocusModeEnum.None;
        button.MouseFilter = Control.MouseFilterEnum.Stop;

        // The panel's button style carries 10 px of horizontal content margin, right for a text button and fatal for
        // a 24 px one: 20 px of margin leaves about 4 px for the glyph. Style is otherwise untouched, so the control
        // still looks like its neighbours.
        TightenContentMargins(button);
        button.TooltipText = "回到置顶服务器的频道";

        Color strong = shape.TextStrong.GetValue(panel) is Color color ? color : Colors.White;
        Texture2D? icon = TryLoadResetIcon(shape, panel, out string detail);
        if (icon != null)
        {
            button.ExpandIcon = true;
            button.Icon = icon;
            if (Interlocked.Exchange(ref _iconLoggedTexture, 1) == 0)
            {
                Entry.Info("chat-server-switch: reset icon=texture (Lucide '" + ResetIconName + "', " + detail + ").");
            }
        }
        else
        {
            // U+27F3 CLOCKWISE GAPPED CIRCLE ARROW: the same glyph as text, sized and tinted like the icon would be.
            button.Text = ResetFallbackGlyph;
            button.AddThemeFontSizeOverride("font_size", ResetFallbackFontSize);
            button.AddThemeColorOverride("font_color", strong);
            button.AddThemeColorOverride("font_hover_color", strong);
            button.AddThemeColorOverride("font_pressed_color", strong);
            button.AddThemeColorOverride("font_focus_color", strong);
            button.AddThemeColorOverride("font_disabled_color", new Color(strong, 0.6f));
            if (Interlocked.Exchange(ref _iconLoggedFallback, 1) == 0)
            {
                Entry.Info(
                    "chat-server-switch: reset icon=fallback (text '" + ResetFallbackGlyph + "', " +
                    ResetFallbackFontSize + "px) - " + detail + ".");
            }
        }

        return button;
    }

    /// <summary>
    /// Pulls "refresh-cw" out of the panel's own Lucide set, the glyph the lobby's REFRESH button draws.
    ///
    /// "Got a texture" is not "got the glyph": when the SVG decoder fails the loader returns its own placeholder, a
    /// magenta X drawn at <c>pixelSize</c>. A real decode is transparent in the corner and the placeholder is not,
    /// which is what tells them apart here.
    /// </summary>
    private static Texture2D? TryLoadResetIcon(
        Shape shape,
        Control panel,
        out string detail)
    {
        detail = string.Empty;
        try
        {
            object? icons = shape.Icons.GetValue(panel);
            if (icons == null)
            {
                detail = "the panel has no icon loader";
                return null;
            }

            Color color = shape.TextStrong.GetValue(panel) is Color strong ? strong : Colors.White;
            int pixelSize = Math.Max(12, (int)(ArrowSize * 0.7f));
            if (shape.IconsGet.Invoke(icons, new object?[] { ResetIconName, pixelSize, color }) is not Texture2D texture)
            {
                detail = "the loader returned no texture";
                return null;
            }

            if (texture.GetWidth() < 8 || texture.GetHeight() < 8)
            {
                detail = "the texture is only " + texture.GetWidth() + "x" + texture.GetHeight();
                return null;
            }

            if (IsDecoderPlaceholder(texture))
            {
                detail = "the SVG decoder failed and the loader substituted its placeholder";
                return null;
            }

            detail = pixelSize + "px";
            return texture;
        }
        catch (Exception exception)
        {
            detail = "the loader threw " + exception.GetType().Name;
            return null;
        }
    }

    /// <summary>
    /// The loader's failure texture is a magenta X over a transparent field, so its top-left pixel is opaque. A
    /// decoded Lucide glyph is stroke-only and leaves that pixel empty.
    /// </summary>
    private static bool IsDecoderPlaceholder(Texture2D texture)
    {
        try
        {
            Image? image = texture.GetImage();
            if (image == null || image.GetWidth() == 0 || image.GetHeight() == 0)
            {
                return true;
            }

            Color corner = image.GetPixel(0, 0);
            return corner.A > 0.9f &&
                   Math.Abs(corner.R - 1f) < 0.08f &&
                   Math.Abs(corner.G - 0.2f) < 0.08f &&
                   Math.Abs(corner.B - 0.4f) < 0.08f;
        }
        catch (Exception)
        {
            // Cannot tell: trust the texture rather than hiding a glyph that is probably fine.
            return false;
        }
    }

    /// <summary>
    /// Zeroes the content margins of every stylebox state on this one button, so its glyph gets the whole square.
    /// The styleboxes belong to this button alone (the panel's button style builds a fresh one per state).
    /// </summary>
    private static void TightenContentMargins(Button button)
    {
        foreach (string state in new[] { "normal", "hover", "pressed", "focus", "disabled" })
        {
            if (button.GetThemeStylebox(state) is not StyleBoxFlat style)
            {
                continue;
            }

            style.ContentMarginLeft = 0;
            style.ContentMarginTop = 0;
            style.ContentMarginRight = 0;
            style.ContentMarginBottom = 0;
        }
    }

    /// <summary>
    /// Reuses the panel's own button factory so the arrows carry exactly the sidebar's button styling instead of
    /// a second, slightly different look.
    /// </summary>
    private static Button CreateArrowButton(
        Shape shape,
        Control panel,
        string glyph,
        string tooltip)
    {
        object? created = shape.CreateButton.Invoke(panel, new object?[] { glyph, false });
        if (created is not Button button)
        {
            throw new InvalidOperationException(
                PanelTypeName + "." + CreateButtonMethodName + " returned no Button for the switch row.");
        }

        button.Text = glyph;
        button.CustomMinimumSize = new Vector2(ArrowSize, ArrowSize);
        button.FocusMode = Control.FocusModeEnum.None;
        button.MouseFilter = Control.MouseFilterEnum.Stop;
        button.AddThemeFontSizeOverride("font_size", (int)ArrowFontSize);
        button.TooltipText = tooltip;
        return button;
    }

    /// <summary>
    /// Returns the public channel to upstream's pinned server, through the same preflight and the same port as the
    /// arrows.
    /// </summary>
    private static void BeginReset(Shape shape)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return;
        }

        try
        {
            object? runtime = shape.RuntimeInstance.GetValue(null);
            if (runtime == null)
            {
                Interlocked.Exchange(ref _busy, 0);
                DeferInfo("复位时大厅运行时还没就绪，未切换。");
                return;
            }

            string netId = shape.ResolveNetId.Invoke(runtime, null) as string ?? string.Empty;
            if (string.IsNullOrWhiteSpace(netId))
            {
                Interlocked.Exchange(ref _busy, 0);
                DeferInfo("读不到本机 NetId，复位未执行。");
                return;
            }

            string playerName = shape.DisplayName.Invoke(null, null) as string ?? "玩家";
            string configUrl = ReadConfigBaseUrl(shape);
            string target = ChatServerCatalog.FeaturedAddress;
            if (string.IsNullOrWhiteSpace(target))
            {
                Interlocked.Exchange(ref _busy, 0);
                DeferInfo("复位目标地址为空（置顶服），未切换。");
                return;
            }

            // Already there: reconnecting would only shake the channel for no reason.
            string? actual = ReadLiveTargetAddress(shape) ?? Volatile.Read(ref _state).TargetAddress;
            if (SameAddress(target, actual))
            {
                Interlocked.Exchange(ref _busy, 0);
                // A click that changes nothing on screen reads as a broken button, so the reason goes into the tooltip
                // the row already has (Describe prints it as the last attempt).
                SwitchState idled = Volatile.Read(ref _state);
                SetState(new SwitchState(
                    idled.TargetAddress,
                    idled.TargetName,
                    SwitchPhase.Idle,
                    "已经在置顶服务器的频道上，未重连：" + target));
                DeferInfo("已经在目标服务器的频道上，未重连：" + target);
                return;
            }

            // Upstream's candidate gatherer reads and rewrites the known-peers cache file, so it is called here, on
            // the main thread, and the worker only does the network half.
            List<(string Address, string Name)> seeds = ChatServerCatalog.ReadInitialCandidates();

            int generation = Interlocked.Increment(ref _stepGeneration);
            SwitchState previous = Volatile.Read(ref _state);
            SetState(new SwitchState(previous.TargetAddress, previous.TargetName, SwitchPhase.Probing, null));
            Task ignored = Task.Run(
                () => ResetAsync(shape, runtime, target, netId, playerName, configUrl, seeds, generation));
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _busy, 0);
            WarnOnce("could not start the channel reset: " +
                     exception.GetType().Name + ": " + exception.Message);
        }
    }

    /// <summary>
    /// Worker half of the reset. When the pinned server does not speak v1 the reset degrades to the first
    /// chat-capable candidate; with no candidate the current channel stays.
    /// </summary>
    private static async Task ResetAsync(
        Shape shape,
        object runtime,
        string target,
        string netId,
        string playerName,
        string configUrl,
        List<(string Address, string Name)> seeds,
        int generation)
    {
        string address = target;
        string? name = null;
        SwitchState previous = Volatile.Read(ref _state);
        try
        {
            ProbeOutcome probe = await ChatServerCatalog.ProbeAsync(address).ConfigureAwait(false);
            if (!probe.Reachable || !probe.Supported)
            {
                CatalogSnapshot? snapshot = await ChatServerCatalog
                    .GetSnapshotAsync(configUrl, seeds)
                    .ConfigureAwait(false);
                CatalogServer? fallback = snapshot?.Servers
                    .FirstOrDefault(server => !server.IsCurrent && server.ChatSupported);
                if (fallback == null)
                {
                    DeferInfo("置顶服务器不支持公共频道协议 v1，且没有其它可用候选，保持当前频道。");
                    if (IsCurrentGeneration(generation))
                    {
                        SetState(FailureState(previous, replacedConnection: false,
                            "置顶服务器与其它候选都没有公共频道 v1"));
                    }

                    return;
                }

                DeferInfo("置顶服务器不支持公共频道协议 v1，改连第一台支持频道的候选：" + fallback.Address);
                address = fallback.Address;
                name = fallback.DisplayName;
            }

            if (!IsCurrentGeneration(generation))
            {
                return;
            }

            SetState(new SwitchState(address, name, SwitchPhase.Connecting, null));
            string? rejected = await ConnectAndVerifyAsync(shape, runtime, address, netId, playerName)
                .ConfigureAwait(false);
            if (!IsCurrentGeneration(generation))
            {
                return;
            }

            if (rejected != null)
            {
                // The port already replaced the coordinator, so the old channel is gone: say so.
                SetState(FailureState(previous, replacedConnection: true, rejected));
                return;
            }

            SetState(new SwitchState(address, name, SwitchPhase.Idle, null));
        }
        catch (Exception exception)
        {
            if (IsCurrentGeneration(generation))
            {
                SetState(FailureState(previous, replacedConnection: true,
                    exception.GetType().Name + ": " + exception.Message));
            }
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private static async Task<string?> ConnectAndVerifyAsync(
        Shape shape,
        object runtime,
        string address,
        string netId,
        string playerName)
    {
        string? rejected = await TryConnectAsync(shape, runtime, address, netId, playerName).ConfigureAwait(false);
        if (rejected != null)
        {
            return rejected;
        }

        string? unusable = await AwaitUsableStateAsync(shape, runtime).ConfigureAwait(false);
        if (unusable != null)
        {
            ChatServerCatalog.MarkUnsupported(address);
            return unusable;
        }

        return null;
    }

    /// <summary>
    /// Starts one walk. Runs on the main thread because <c>ResolveCurrentPlayerNetId</c> may reach the game's own
    /// <c>PlatformUtil.GetLocalPlayerId</c>; the string it returns is handed to the worker.
    /// </summary>
    private static void BeginStep(int direction)
    {
        Shape? shape = _shape;
        if (shape == null)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return;
        }

        try
        {
            SwitchState previous = Volatile.Read(ref _state);

            object? runtime = shape.RuntimeInstance.GetValue(null);
            if (runtime == null)
            {
                SetState(FailureState(previous, replacedConnection: false, "大厅运行时还没就绪"));
                Interlocked.Exchange(ref _busy, 0);
                return;
            }

            string netId = shape.ResolveNetId.Invoke(runtime, null) as string ?? string.Empty;
            string playerName = shape.DisplayName.Invoke(null, null) as string ?? "玩家";
            string configUrl = ReadConfigBaseUrl(shape);
            if (string.IsNullOrWhiteSpace(netId))
            {
                SetState(FailureState(previous, replacedConnection: false, "读不到本机 NetId，无法申请频道票据"));
                Interlocked.Exchange(ref _busy, 0);
                return;
            }

            // Upstream's candidate gatherer rewrites the known-peers cache file, so it is called here, on the main
            // thread, and the worker only does the network half.
            List<(string Address, string Name)> seeds = ChatServerCatalog.ReadInitialCandidates();

            int generation = Interlocked.Increment(ref _stepGeneration);
            SetState(new SwitchState(previous.TargetAddress, previous.TargetName, SwitchPhase.Probing, null));
            Task ignored = Task.Run(
                () => StepAsync(shape, runtime, direction, netId, playerName, configUrl, seeds, generation));
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _busy, 0);
            SwitchState previous = Volatile.Read(ref _state);
            SetState(FailureState(previous, replacedConnection: false, exception.GetType().Name + ": " + exception.Message));
            WarnOnce("could not start the channel switch: " + exception.GetType().Name + ": " + exception.Message);
        }
    }

    /// <summary>
    /// Worker-thread walk. It touches no node; the only channel back to the UI is <see cref="SetState"/>, which the
    /// panel's own <c>_Process</c> postfix picks up on the main thread.
    /// </summary>
    private static async Task StepAsync(
        Shape shape,
        object runtime,
        int direction,
        string netId,
        string playerName,
        string configUrl,
        List<(string Address, string Name)> seeds,
        int generation)
    {
        string? problem = null;
        bool replacedConnection = false;
        SwitchState previous = Volatile.Read(ref _state);
        var deadline = Stopwatch.StartNew();
        try
        {
            CatalogSnapshot? snapshot = await ChatServerCatalog.GetSnapshotAsync(configUrl, seeds).ConfigureAwait(false);
            IReadOnlyList<CatalogServer> servers = snapshot?.Servers ?? Array.Empty<CatalogServer>();
            if (servers.Count <= 1)
            {
                // Every candidate whose /probe did not advertise serverChatVersion == 1 was dropped while the
                // catalog was built, so "one entry" means "only this server speaks the protocol". The channel
                // that is up stays up and the row reads 本服.
                if (IsCurrentGeneration(generation))
                {
                    SetState(FailureState(previous, replacedConnection: false,
                        "没有探测到其它支持公共频道协议 v1 的服务器"));
                }

                return;
            }

            int index = IndexOfTarget(servers);
            int attempts = 0;
            for (int hop = 0; hop < servers.Count && attempts < MaxAttempts; hop++)
            {
                // Arrows are disabled while this runs, so the walk bounds itself: a chain of unreachable servers
                // must not keep the row locked for minutes.
                if (deadline.Elapsed > WalkBudget)
                {
                    problem = "尝试超时，已放弃本次切换";
                    break;
                }

                index = Wrap(index + direction, servers.Count);
                CatalogServer candidate = servers[index];
                attempts++;

                ProbeOutcome probe = await ChatServerCatalog.ProbeAsync(candidate.Address).ConfigureAwait(false);
                if (!probe.Reachable || !probe.Supported)
                {
                    problem = ChatServerCatalog.ShortName(candidate.DisplayName, candidate.Address, MaxNameChars) +
                              " 没有声明公共频道协议 v1";
                    continue;
                }

                if (!IsCurrentGeneration(generation))
                {
                    // The lobby server changed while this walk was running: it no longer describes reality.
                    return;
                }

                SetState(new SwitchState(candidate.Address, candidate.DisplayName, SwitchPhase.Connecting, null));

                // From here on the rotating port replaces the coordinator before it connects, so a failure means
                // the previous channel is already gone - that is the only case where "频道暂不可用" is true.
                replacedConnection = true;
                string? rejected = await ConnectAndVerifyAsync(shape, runtime, candidate.Address, netId, playerName)
                    .ConfigureAwait(false);
                if (rejected != null)
                {
                    // A target that lied about v1 was already marked by the helper; either way the walk moves on.
                    problem = rejected;
                    continue;
                }

                if (!IsCurrentGeneration(generation))
                {
                    // The connect already happened, but the player left this server's context meanwhile: the
                    // next frame reads the live client and shows what is actually attached.
                    return;
                }

                SetState(new SwitchState(candidate.Address, candidate.DisplayName, SwitchPhase.Idle, null));
                return;
            }

            // Nothing was switched and (unless a connection was already replaced) nothing was lost, so the row
            // goes back to "本服" and only the tooltip carries the reason.
            if (IsCurrentGeneration(generation))
            {
                SetState(FailureState(previous, replacedConnection, problem ?? "没有找到可用的公共频道服务器"));
            }
        }
        catch (Exception exception)
        {
            if (IsCurrentGeneration(generation))
            {
                SetState(FailureState(
                    previous, replacedConnection, exception.GetType().Name + ": " + exception.Message));
            }
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private static async Task<string?> TryConnectAsync(
        Shape shape,
        object runtime,
        string address,
        string netId,
        string playerName)
    {
        object? port = shape.ChatOwner.GetValue(runtime);
        if (port == null)
        {
            return "公共频道端口还没建立";
        }

        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri))
        {
            return "服务器地址无法解析：" + address;
        }

        using var timeout = new CancellationTokenSource(ConnectTimeout);
        try
        {
            object? returned = shape.PortConnect.Invoke(port, new object?[] { uri, netId, playerName, timeout.Token });
            if (returned is Task task)
            {
                await task.ConfigureAwait(false);
            }

            return null;
        }
        catch (Exception exception)
        {
            Exception reported = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
            return "连接失败：" + reported.GetType().Name + ": " + reported.Message;
        }
    }

    /// <summary>
    /// Waits for the freshly created channel to say something usable. A server that answers the probe but is
    /// configured with chat off only shows that here, after the ticket round trip; continuing to the next
    /// candidate is what the spec asks for, and returning null on "not proven broken" keeps a slow-but-fine
    /// server from being skipped.
    /// </summary>
    private static async Task<string?> AwaitUsableStateAsync(Shape shape, object runtime)
    {
        for (int sample = 0; sample < UsableStateSamples; sample++)
        {
            await Task.Delay(UsableStateInterval).ConfigureAwait(false);
            object? state = ReadChannelState(shape, runtime);
            if (state == null)
            {
                continue;
            }

            string presentation = shape.StatePresentation.GetValue(state)?.ToString() ?? string.Empty;
            bool enabled = shape.StateChatEnabled.GetValue(state) is bool value && value;
            if (string.Equals(presentation, "Unsupported", StringComparison.Ordinal))
            {
                return "该服务器未声明公共频道协议 v1";
            }

            if (string.Equals(presentation, "Disabled", StringComparison.Ordinal) && !enabled)
            {
                return "该服务器的公共频道已关闭";
            }

            if (string.Equals(presentation, "Ready", StringComparison.Ordinal))
            {
                return null;
            }
        }

        return null;
    }

    private static object? ReadChannelState(Shape shape, object runtime)
    {
        try
        {
            object? dual = shape.RuntimeChat.GetValue(runtime);
            return dual == null ? null : shape.DualServer.GetValue(dual);
        }
        catch (Exception)
        {
            // LanConnectLobbyRuntime.Chat throws before the chat owner exists; "cannot tell yet" is null.
            return null;
        }
    }

    /// <summary>
    /// End state of a walk that did not switch anything. When the previous channel is provably still up (no
    /// connect was attempted, so the rotating port never replaced the coordinator) the row falls back to 本服;
    /// only a replaced-and-failed connection earns the "频道暂不可用" wording.
    /// </summary>
    private static SwitchState FailureState(SwitchState previous, bool replacedConnection, string problem) =>
        replacedConnection
            ? new SwitchState(previous.TargetAddress, previous.TargetName, SwitchPhase.Unavailable, problem)
            : new SwitchState(null, null, SwitchPhase.Idle, problem);

    private static int IndexOfTarget(IReadOnlyList<CatalogServer> servers)
    {
        SwitchState state = Volatile.Read(ref _state);
        if (state.TargetAddress == null)
        {
            return 0;
        }

        for (int index = 0; index < servers.Count; index++)
        {
            if (string.Equals(servers[index].Address, state.TargetAddress, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return 0;
    }

    private static int Wrap(int index, int count)
    {
        if (count <= 0)
        {
            return 0;
        }

        int wrapped = index % count;
        return wrapped < 0 ? wrapped + count : wrapped;
    }

    /// <summary>
    /// The configured lobby base URL, or "" when the getter cannot answer. That getter resolves the packaged
    /// defaults file, so it can throw: a missing URL then costs the name its address fallback and nothing else.
    /// </summary>
    private static string ReadConfigBaseUrl(Shape shape)
    {
        try
        {
            return shape.ConfigBaseUrl.GetValue(null) as string ?? string.Empty;
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _configWarned, 1) == 0)
            {
                Entry.Warn(
                    "chat-server-switch: reading the configured lobby server failed, the channel label falls back " +
                    "to 本服: " + exception.GetType().Name + ": " + exception.Message);
            }

            return string.Empty;
        }
    }

    /// <summary>
    /// The address the public channel is currently attached to, or null when it is on the configured lobby
    /// server. Read by the announcement repair so the announcement bar can follow the same server instead of
    /// resolving a second, divergent idea of "current".
    /// </summary>
    internal static string? CurrentChatTargetAddress => Volatile.Read(ref _state).TargetAddress;

    private static void SetState(SwitchState next, bool nudgeRefresh = true)
    {
        Volatile.Write(ref _state, next);
        if (nudgeRefresh && next.Phase == SwitchPhase.Idle)
        {
            // AnnouncementSourceFix reads the settled target from CurrentChatTargetAddress; this flag only asks
            // the overlay to run one refresh right away instead of waiting out its 8 s timer.
            Interlocked.Exchange(ref _refreshNudgePending, 1);
        }
    }

    private static bool IsAlive(RowWidgets widgets) =>
        GodotObject.IsInstanceValid(widgets.Reset) &&
        GodotObject.IsInstanceValid(widgets.Previous) &&
        GodotObject.IsInstanceValid(widgets.Next);

    private static void WarnOnce(string message)
    {
        if (Interlocked.Exchange(ref _warned, 1) != 0)
        {
            return;
        }

        Entry.Warn("chat-server-switch: " + message);
    }

    private enum SwitchPhase
    {
        Idle,

        Probing,

        Connecting,

        Unavailable
    }

    private sealed record SwitchState(string? TargetAddress, string? TargetName, SwitchPhase Phase, string? Problem)
    {
        internal static SwitchState Initial { get; } = new(null, null, SwitchPhase.Idle, null);
    }

    private sealed class RowWidgets(Button reset, Button previous, Button next)
    {
        internal Button Reset { get; } = reset;

        internal Button Previous { get; } = previous;

        internal Button Next { get; } = next;

        /// <summary>The row this instance last painted, so an unchanged frame writes nothing.</summary>
        internal bool Busy;

        internal string Target = string.Empty;

        internal Label? TitleLabel;

        internal string TitleTooltip = string.Empty;
    }

    /// <summary>
    /// Every member this repair touches, resolved once in <see cref="Applies"/> and never again. Metadata only:
    /// no <c>typeof</c> on a host type, and nothing invoked while probing.
    /// </summary>
    internal sealed class Shape
    {
        internal MethodInfo UpdateAvailability = null!;

        internal MethodInfo Process = null!;

        internal FieldInfo TitleLabel = null!;

        internal FieldInfo StatusLabel = null!;

        internal FieldInfo ChatState = null!;

        internal PropertyInfo ChatStyle = null!;

        /// <summary>The panel's own Lucide loader: the same glyph set the lobby's REFRESH button uses.</summary>
        internal FieldInfo Icons = null!;

        internal MethodInfo IconsGet = null!;

        internal PropertyInfo TextStrong = null!;

        internal MethodInfo CreateButton = null!;

        internal PropertyInfo RuntimeInstance = null!;

        internal FieldInfo ChatOwner = null!;

        internal MethodInfo ResolveNetId = null!;

        internal PropertyInfo RuntimeChat = null!;

        internal MethodInfo PortConnect = null!;

        internal PropertyInfo ConfigBaseUrl = null!;

        internal MethodInfo DisplayName = null!;

        internal PropertyInfo DualServer = null!;

        internal PropertyInfo StatePresentation = null!;

        internal PropertyInfo StateChatEnabled = null!;

        /// <summary>Port → coordinator → client → base URI: what the channel is <em>actually</em> attached to.</summary>
        internal PropertyInfo PortCurrent = null!;

        internal FieldInfo CoordinatorClient = null!;

        internal FieldInfo ClientBaseUri = null!;

        /// <summary>The overlay, reached by node name; only its refresh timer is touched.</summary>
        internal Type OverlayType = null!;

        internal FieldInfo AutoRefresh = null!;

        internal static Shape? TryCapture(Assembly host, List<string> problems)
        {
            Type? panel = ReflectScope.HostType(host, PanelTypeName, problems);
            Type? runtime = ReflectScope.HostType(host, RuntimeTypeName, problems);
            Type? config = ReflectScope.HostType(host, ConfigTypeName, problems);

            if (panel == null || runtime == null || config == null)
            {
                return null;
            }

            var shape = new Shape
            {
                UpdateAvailability = ReflectScope.Exact(
                    panel, UpdateAvailabilityMethodName, requireStatic: false, Array.Empty<string>(), "System.Void",
                    problems)!,
                Process = ReflectScope.Exact(
                    panel, ProcessMethodName, requireStatic: false, new[] { "System.Double" }, "System.Void",
                    problems)!,
                CreateButton = ReflectScope.MethodOf(panel, CreateButtonMethodName, 2, problems)!,
                ChatStyle = ReflectScope.PropertyOf(panel, ChatStylePropertyName, problems)!,
                TextStrong = ReflectScope.PropertyOf(panel, TextStrongColorPropertyName, problems)!,
                Icons = RequireField(problems, panel, IconsFieldName, "LanConnectLucideIconLoader")!,
                RuntimeInstance = ReflectScope.StaticProperty(problems, runtime, RuntimeInstancePropertyName)!,
                ResolveNetId = ReflectScope.Exact(
                    runtime, ResolveNetIdMethodName, requireStatic: false, Array.Empty<string>(), "System.String",
                    problems)!,
                RuntimeChat = ReflectScope.PropertyOf(runtime, RuntimeChatPropertyName, problems)!,
                ConfigBaseUrl = RequireStringStatic(problems, config, ConfigBaseUrlPropertyName)!,
                DisplayName = ReflectScope.Exact(
                    config, DisplayNameMethodName, requireStatic: true, Array.Empty<string>(), "System.String",
                    problems)!
            };

            if (shape.Icons != null)
            {
                shape.IconsGet = ReflectScope.MethodOf(shape.Icons.FieldType, IconsGetMethodName, 3, problems)!;
            }

            shape.TitleLabel = RequireField(problems, panel, "_titleLabel", "Label")!;
            shape.StatusLabel = RequireField(problems, panel, "_statusLabel", "Label")!;

            FieldInfo? chatState = RequireField(problems, panel, "_state", "LanConnectChatChannelState");
            shape.ChatState = chatState!;
            if (chatState != null)
            {
                shape.StatePresentation = ReflectScope.PropertyOf(chatState.FieldType, PresentationPropertyName, problems)!;
                shape.StateChatEnabled = ReflectScope.PropertyOf(chatState.FieldType, ChatEnabledPropertyName, problems)!;
                if (shape.StateChatEnabled != null && shape.StateChatEnabled.PropertyType != typeof(bool))
                {
                    problems.Add(ChatEnabledPropertyName + " is " + shape.StateChatEnabled.PropertyType.FullName +
                                 ", expected System.Boolean");
                }
            }

            FieldInfo? chatOwner = ReflectScope.Field(problems, runtime, ChatOwnerFieldName);
            if (chatOwner != null)
            {
                // Resolved through the field's own type: the port is internal, so its name is the only anchor
                // worth pinning, and the field type is what a rename would have to keep working.
                if (!string.Equals(chatOwner.FieldType.Name, "LanConnectRotatingServerChatPort", StringComparison.Ordinal))
                {
                    problems.Add(ChatOwnerFieldName + " is " + chatOwner.FieldType.FullName +
                                 ", expected LanConnectRotatingServerChatPort");
                }
                else
                {
                    shape.ChatOwner = chatOwner;
                    shape.PortConnect = ReflectScope.Exact(
                        chatOwner.FieldType,
                        PortConnectMethodName,
                        requireStatic: false,
                        new[] { "System.Uri", "System.String", "System.String", "System.Threading.CancellationToken" },
                        null,
                        problems)!;

                    // The live-target chain, resolved from the field types rather than by name: port.Current ->
                    // coordinator._client -> LanConnectServerChatClient._lobbyBaseUri.
                    PropertyInfo? current = ReflectScope.PropertyOf(chatOwner.FieldType, PortCurrentPropertyName, problems);
                    if (current != null)
                    {
                        shape.PortCurrent = current;
                        FieldInfo? client = ReflectScope.Field(problems, current.PropertyType, CoordinatorClientFieldName);
                        if (client != null)
                        {
                            shape.CoordinatorClient = client;
                            Type? concrete = host.GetType(ClientTypeName, throwOnError: false, ignoreCase: false);
                            if (concrete == null)
                            {
                                problems.Add(ClientTypeName + " (host type)");
                            }
                            else
                            {
                                FieldInfo? uri = ReflectScope.Field(problems, concrete, ClientBaseUriFieldName);
                                if (uri != null && uri.FieldType != typeof(Uri))
                                {
                                    problems.Add(ClientBaseUriFieldName + " is " + uri.FieldType.FullName +
                                                 ", expected System.Uri");
                                }
                                else
                                {
                                    shape.ClientBaseUri = uri!;
                                }
                            }
                        }
                    }
                }
            }


            if (shape.RuntimeChat != null)
            {
                if (shape.RuntimeChat.PropertyType == runtime)
                {
                    problems.Add(RuntimeChatPropertyName + " returns the runtime itself");
                }
                else
                {
                    shape.DualServer = ReflectScope.PropertyOf(
                        shape.RuntimeChat.PropertyType, DualServerPropertyName, problems)!;
                }
            }

            if (shape.RuntimeInstance != null && shape.RuntimeInstance.PropertyType != runtime)
            {
                problems.Add(RuntimeInstancePropertyName + " is " + shape.RuntimeInstance.PropertyType.FullName +
                             ", expected " + RuntimeTypeName);
            }

            Type? overlay = ReflectScope.HostType(host, OverlayTypeName, problems);
            if (overlay != null)
            {
                shape.OverlayType = overlay;
                FieldInfo? autoRefresh = ReflectScope.Field(problems, overlay, AutoRefreshFieldName);
                if (autoRefresh != null)
                {
                    if (autoRefresh.FieldType != typeof(double))
                    {
                        problems.Add(AutoRefreshFieldName + " is " + autoRefresh.FieldType.FullName +
                                     ", expected System.Double");
                    }
                    else
                    {
                        shape.AutoRefresh = autoRefresh;
                    }
                }
            }

            // The candidate list is not optional: without it the arrows would have nowhere to go.
            ChatServerCatalog.TryCapture(host, problems);

            return shape;
        }

        /// <summary>A static string property; anything else is a shape change worth reporting.</summary>
        private static PropertyInfo? RequireStringStatic(List<string> problems, Type owner, string name)
        {
            PropertyInfo? property = ReflectScope.StaticProperty(problems, owner, name);
            if (property != null && property.PropertyType != typeof(string))
            {
                problems.Add(name + " is " + property.PropertyType.FullName + ", expected System.String");
                return null;
            }

            return property;
        }

        private static FieldInfo? RequireField(
            List<string> problems,
            Type owner,
            string name,
            string expectedTypeName)
        {
            FieldInfo? field = ReflectScope.Field(problems, owner, name);
            if (field == null)
            {
                return null;
            }

            if (!string.Equals(field.FieldType.Name, expectedTypeName, StringComparison.Ordinal))
            {
                problems.Add(name + " is " + field.FieldType.FullName + ", expected " + expectedTypeName);
                return null;
            }

            return field;
        }
    }
}
