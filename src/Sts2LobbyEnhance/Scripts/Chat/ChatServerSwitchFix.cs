using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;

namespace Sts2LobbyEnhance;

    /// <summary>
    /// Puts <c>‹</c> <c>›</c> next to the lobby server chat panel's title and moves the public channel to another
    /// server's channel with them. It does not switch the lobby server, does not leave the room and does not write
    /// config.json - only which server's public chat this client is attached to changes.
    ///
    /// Why the class under patch is the panel, not the overlay: the title row is <c>LanConnectBasicChatPanel</c>'s
    /// own <c>HBoxContainer</c> (<c>LanConnectBasicChatPanel.cs:860-889</c>), and upstream rewrites
    /// <c>ChatChannelTitle</c>'s text from two places, both inside that class (<c>:2288-2295</c>,
    /// <c>:2448-2453</c>). A postfix on <c>UpdateAvailability()</c> runs after both, and a postfix on
    /// <c>_Process()</c> gives the row its per-frame refresh, so nothing has to be re-found or cached across the
    /// panel's rebuilds.
    ///
    /// Why the switch goes through <c>LanConnectRotatingServerChatPort.ConnectAsync</c> and not
    /// <c>LanConnectLobbyRuntime.ConnectServerChatAsync</c>: the runtime method reconnects the same
    /// <c>LanConnectServerChatClient</c>, whose <c>_connectStarted</c> latch only ever fires once
    /// (<c>LanConnectServerChatClient.cs:254-257</c>), and whose <c>MarkPermanentStop(Unsupported)</c>
    /// (<c>:181-189</c>, never retried at <c>:1048-1059</c>) would poison every later attempt. The rotating port
    /// builds a new client and a new coordinator per call and disposes the old one
    /// (<c>LanConnectChatRuntimePorts.cs:777-838</c>), so the overlay's own
    /// <c>EnsureCurrentLobbyServerChatBinding</c> (<c>LanConnectLobbyOverlay.cs:2185-2201</c>) sees a new State
    /// object at the next frame (<c>:729</c>) and rebinds the panel by itself.
    ///
    /// Every Godot call lives in a patch body. <see cref="Applies"/> and <see cref="Apply"/> are reflection and
    /// <c>harmony.Patch</c> only, because the smoke test runs both in a plain .NET process with no engine.
    /// </summary>
internal sealed class ChatServerSwitchFix : ICompatFix
{
    private const string PanelTypeName = "Sts2LanConnect.Scripts.LanConnectBasicChatPanel";

    private const string RuntimeTypeName = "Sts2LanConnect.Scripts.LanConnectLobbyRuntime";

    private const string ConfigTypeName = "Sts2LanConnect.Scripts.LanConnectConfig";

    private const string UpdateAvailabilityMethodName = "UpdateAvailability";

    private const string ProcessMethodName = "_Process";

    private const string CreateButtonMethodName = "CreateButton";

    private const string ChatStylePropertyName = "ChatVisualStyle";

    private const string MutedColorPropertyName = "TextMutedColor";

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

    /// <summary>The node upstream gives the sidebar panel (<c>LanConnectLobbyOverlay.cs:1762</c>).</summary>
    private const string LobbyPanelNodeName = "LobbyServerChatPanel";

    /// <summary>Style value of the sidebar panel (<c>LanConnectLobbyOverlay.cs:1763</c>).</summary>
    private const string LobbyStyleName = "LobbySidebar";

    private const string SwitchRowNodeName = "LobbyEnhanceChatServerSwitch";

    private const string NameLabelNodeName = "LobbyEnhanceChatServerName";

    /// <summary>Short name limit in the row; the panel is 200 px wide before layout gives it more.</summary>
    private const int MaxNameChars = 8;

    private const float ArrowSize = 24f;

    private const float ArrowFontSize = 16f;

    private const int RowSeparation = 4;

    /// <summary>
    /// Below this the row goes compact: the status label (ShrinkEnd, clipped to zero) is hidden and so is the
    /// middle name label, leaving two 24 px arrows. At 200 px - the sidebar's minimum
    /// (<c>LanConnectLobbyOverlay.cs:1748</c>) - the title, an icon, two arrows and a name cannot all fit, and
    /// squeezing the title is exactly what the user called "奇形怪状". The target name then lives in the arrows'
    /// tooltips.
    /// </summary>
    private const float CompactRowWidth = 260f;

    private const string LocalServerLabel = "本服";

    private const int MaxAttempts = 5;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(12d);

    /// <summary>Whole-walk budget; also the longest the arrows stay disabled.</summary>
    private static readonly TimeSpan WalkBudget = TimeSpan.FromSeconds(45d);

    private static readonly TimeSpan UsableStateInterval = TimeSpan.FromMilliseconds(500d);

    private const int UsableStateSamples = 6;

    private static Shape? _shape;

    private static readonly ConditionalWeakTable<Control, RowWidgets> Rows = new();

    private static SwitchState _state = SwitchState.Initial;

    private static int _busy;

    private static string? _observedConfigUrl;

    private static int _warned;

    private static int _nameLookupWarned;

    /// <summary>Separate from <see cref="_warned"/> so a shape notice cannot swallow the first real failure.</summary>
    private static int _shapeNoticed;

    public string Id => "chat-server-switch";

    /// <summary>
    /// The public channel's attachment point. A shape change in the chat panel rolls back this repair and the
    /// touch fixes only; the room list and the announcement carousel keep their own groups.
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
            "channel after a /probe preflight (capabilities.serverChatVersion == 1, cached 5 min). The lobby " +
            "server, the room and config.json are untouched.");
    }

    /// <summary>
    /// Runs after upstream has just rewritten the title and the status text, so the row is refreshed on the same
    /// pass instead of a frame later.
    /// </summary>
    // ReSharper disable once UnusedParameter.Local — Harmony binds by name.
    private static void UpdateAvailabilityPostfix(object __instance)
    {
        Sync(__instance);
    }

    /// <summary>
    /// Per-frame tick. Upstream refreshes the title from state events only, so a switch that completes on a
    /// worker thread has no other place to become visible in the same frame.
    /// </summary>
    // ReSharper disable once UnusedParameter.Local — Harmony binds by name.
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
            // A missing row must never break the chat panel. Leave upstream's title row as it is and say so once.
            WarnOnce("could not update the public channel switch row: " +
                     exception.GetType().Name + ": " + exception.Message);
        }
    }

    private static void SyncCore(Shape shape, Control panel)
    {
        if (!IsLobbySidebarPanel(shape, panel))
        {
            return;
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
            Volatile.Write(ref _state, SwitchState.Initial);
            ChatServerCatalog.Invalidate();
        }

        float width = panel.Size.X;
        bool compact = width > 0f && width < CompactRowWidth;

        Label? status = shape.StatusLabel.GetValue(panel) as Label;
        if (status != null && GodotObject.IsInstanceValid(status))
        {
            status.Visible = !compact;
        }

        SwitchState state = Volatile.Read(ref _state);
        widgets.Name.Visible = !compact;

        string localName = ResolveLocalName(configUrl);
        (string text, string tooltip) = Describe(state, localName, configUrl);
        if (!string.Equals(widgets.Name.Text, text, StringComparison.Ordinal))
        {
            widgets.Name.Text = text;
        }

        widgets.Name.TooltipText = tooltip;
        bool busy = state.Phase is SwitchPhase.Probing or SwitchPhase.Connecting;
        string target = CurrentTargetLabel(state, localName);
        widgets.Previous.Disabled = busy;
        widgets.Next.Disabled = busy;
        widgets.Previous.TooltipText = busy ? "正在切换，请稍候" : "切换到上一台服务器的公共频道 · 当前 " + target;
        widgets.Next.TooltipText = busy ? "正在切换，请稍候" : "切换到下一台服务器的公共频道 · 当前 " + target;
    }

    /// <summary>Where the public channel points right now; shown in the row and in the arrows' tooltips.</summary>
    private static string CurrentTargetLabel(SwitchState state, string localName)
    {
        if (state.Phase == SwitchPhase.Unavailable)
        {
            return "频道暂不可用";
        }

        return state.TargetAddress == null
            ? localName
            : ChatServerCatalog.ShortName(state.TargetName, state.TargetAddress, MaxNameChars);
    }

    private static (string Text, string Tooltip) Describe(SwitchState state, string localName, string configUrl)
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

        string text = CurrentTargetLabel(state, localName);
        string detail = state.TargetAddress == null
            ? $"当前：本服 {configUrl}。点 ‹ › 切换其它服务器的公共频道"
            : $"当前：{state.TargetName}（{state.TargetAddress}）。点 ‹ › 切换其它服务器的公共频道";

        // A refused switch keeps the existing connection, so the reason belongs in the tooltip instead of the row.
        if (!string.IsNullOrEmpty(state.Problem))
        {
            detail += "\n上次尝试：" + state.Problem;
        }

        return (text, detail);
    }

    private static string ResolveLocalName(string configUrl)
    {
        try
        {
            return ChatServerCatalog.TryResolveDisplayName(configUrl, out string displayName)
                ? ChatServerCatalog.ShortName(displayName, configUrl, MaxNameChars)
                : ChatServerCatalog.ShortName(null, configUrl, MaxNameChars);
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _nameLookupWarned, 1) == 0)
            {
                Entry.Warn(
                    "chat-server-switch: the current server's name is unavailable, showing the address instead: " +
                    exception.GetType().Name + ": " + exception.Message);
            }

            return ChatServerCatalog.ShortName(null, configUrl, MaxNameChars);
        }
    }

    /// <summary>
    /// True only for the lobby sidebar's server panel. The room chat overlay builds the same class
    /// (<c>LanConnectRoomChatOverlay.cs:594</c>), and a server switch there would point at a channel that panel
    /// does not show.
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

        // The title row is an HBoxContainer (<c>LanConnectBasicChatPanel.cs:860</c>); anything else means the
        // layout was reworked and guessing where the row belongs would produce the "奇形怪状" this mod avoids.
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

        Label name = new()
        {
            Name = NameLabelNodeName,
            ClipText = true,
            VerticalAlignment = VerticalAlignment.Center,
            CustomMinimumSize = Vector2.Zero,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        name.AddThemeFontSizeOverride("font_size", 12);
        if (shape.MutedColor.GetValue(panel) is Color muted)
        {
            name.AddThemeColorOverride("font_color", muted);
        }

        previous.Connect(Button.SignalName.Pressed, Callable.From(() => BeginStep(-1)));
        next.Connect(Button.SignalName.Pressed, Callable.From(() => BeginStep(1)));

        var built = new HBoxContainer { Name = SwitchRowNodeName };
        built.AddThemeConstantOverride("separation", RowSeparation);
        built.AddChild(previous);
        built.AddChild(name);
        built.AddChild(next);

        titleRow.AddChild(built);
        if (shape.StatusLabel.GetValue(panel) is Label status &&
            GodotObject.IsInstanceValid(status) &&
            ReferenceEquals(status.GetParent(), titleRow))
        {
            // Title, switch row, then the status label: the status keeps shrinking to zero instead of the row
            // being pushed off the panel.
            titleRow.MoveChild(built, status.GetIndex());
        }

        var widgets = new RowWidgets(previous, name, next);
        Rows.Add(built, widgets);
        return widgets;
    }

    /// <summary>
    /// Reuses the panel's own button factory so the arrows carry exactly the sidebar's button styling instead of
    /// a second, slightly different look.
    /// </summary>
    private static Button CreateArrowButton(Shape shape, Control panel, string glyph, string tooltip)
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
    /// Starts one walk. Runs on the main thread because <c>ResolveCurrentPlayerNetId</c> may reach
    /// <c>PlatformUtil.GetLocalPlayerId</c> (<c>LanConnectLobbyRuntime.cs:3406</c>), which is a game API; the
    /// string it returns is handed to the worker.
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
                SetState(new SwitchState(
                    previous.TargetAddress, previous.TargetName, SwitchPhase.Idle, "大厅运行时还没就绪"));
                Interlocked.Exchange(ref _busy, 0);
                return;
            }

            string netId = shape.ResolveNetId.Invoke(runtime, null) as string ?? string.Empty;
            string playerName = shape.DisplayName.Invoke(null, null) as string ?? "玩家";
            string configUrl = ReadConfigBaseUrl(shape);
            if (string.IsNullOrWhiteSpace(netId))
            {
                SetState(new SwitchState(
                    previous.TargetAddress, previous.TargetName, SwitchPhase.Idle, "读不到本机 NetId，无法申请频道票据"));
                Interlocked.Exchange(ref _busy, 0);
                return;
            }

            // Upstream's candidate gatherer rewrites the known-peers cache file, so it is called here, on the main
            // thread, and the worker only does the network half.
            List<(string Address, string Name)> seeds = ChatServerCatalog.ReadInitialCandidates();

            SetState(new SwitchState(previous.TargetAddress, previous.TargetName, SwitchPhase.Probing, null));
            Task ignored = Task.Run(() => StepAsync(shape, runtime, direction, netId, playerName, configUrl, seeds));
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _busy, 0);
            SwitchState previous = Volatile.Read(ref _state);
            SetState(new SwitchState(
                previous.TargetAddress,
                previous.TargetName,
                SwitchPhase.Idle,
                exception.GetType().Name + ": " + exception.Message));
            WarnOnce("could not start the channel switch: " + exception.GetType().Name + ": " + exception.Message);
        }
    }

    /// <summary>
    /// Worker-thread walk. Never touches a node and never throws: the only channel back to the UI is
    /// <see cref="SetState"/>, which the panel's own <c>_Process</c> postfix picks up on the main thread.
    /// </summary>
    private static async Task StepAsync(
        Shape shape,
        object runtime,
        int direction,
        string netId,
        string playerName,
        string configUrl,
        List<(string Address, string Name)> seeds)
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
                // Nothing to switch to. The channel that is up stays up, so this is not "unavailable", it is
                // "there is only one" - and the tooltip is where that belongs.
                SetState(new SwitchState(previous.TargetAddress, previous.TargetName, SwitchPhase.Idle,
                    "没有探测到其它支持公共频道协议 v1 的服务器"));
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

                SetState(new SwitchState(candidate.Address, candidate.DisplayName, SwitchPhase.Connecting, null));

                // From here on the rotating port replaces the coordinator before it connects, so a failure means
                // the previous channel is already gone - that is the only case where "频道暂不可用" is true.
                replacedConnection = true;
                string? rejected = await TryConnectAsync(shape, runtime, candidate, netId, playerName)
                    .ConfigureAwait(false);
                if (rejected != null)
                {
                    problem = rejected;
                    continue;
                }

                string? unusable = await AwaitUsableStateAsync(shape, runtime).ConfigureAwait(false);
                if (unusable != null)
                {
                    // The client latched MarkPermanentStop for this target: remember it so the next walk skips
                    // the same dead end.
                    ChatServerCatalog.MarkUnsupported(candidate.Address);
                    problem = unusable;
                    continue;
                }

                SetState(new SwitchState(candidate.Address, candidate.DisplayName, SwitchPhase.Idle, null));
                return;
            }

            SetState(new SwitchState(
                previous.TargetAddress,
                previous.TargetName,
                replacedConnection ? SwitchPhase.Unavailable : SwitchPhase.Idle,
                problem ?? "没有找到可用的公共频道服务器"));
        }
        catch (Exception exception)
        {
            SetState(new SwitchState(
                previous.TargetAddress,
                previous.TargetName,
                replacedConnection ? SwitchPhase.Unavailable : SwitchPhase.Idle,
                exception.GetType().Name + ": " + exception.Message));
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private static async Task<string?> TryConnectAsync(
        Shape shape,
        object runtime,
        CatalogServer candidate,
        string netId,
        string playerName)
    {
        object? port = shape.ChatOwner.GetValue(runtime);
        if (port == null)
        {
            return "公共频道端口还没建立";
        }

        if (!Uri.TryCreate(candidate.Address, UriKind.Absolute, out Uri? uri))
        {
            return "服务器地址无法解析：" + candidate.Address;
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

    private static string ReadConfigBaseUrl(Shape shape) => shape.ConfigBaseUrl.GetValue(null) as string ?? string.Empty;

    private static void SetState(SwitchState next) => Volatile.Write(ref _state, next);

    private static bool IsAlive(RowWidgets widgets) =>
        GodotObject.IsInstanceValid(widgets.Previous) &&
        GodotObject.IsInstanceValid(widgets.Name) &&
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

    private sealed class RowWidgets(Button previous, Label name, Button next)
    {
        internal Button Previous { get; } = previous;

        internal Label Name { get; } = name;

        internal Button Next { get; } = next;
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

        internal MethodInfo CreateButton = null!;

        internal PropertyInfo MutedColor = null!;

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
                MutedColor = ReflectScope.PropertyOf(panel, MutedColorPropertyName, problems)!,
                RuntimeInstance = ReflectScope.StaticProperty(problems, runtime, RuntimeInstancePropertyName)!,
                ResolveNetId = ReflectScope.Exact(
                    runtime, ResolveNetIdMethodName, requireStatic: false, Array.Empty<string>(), "System.String",
                    problems)!,
                RuntimeChat = ReflectScope.PropertyOf(runtime, RuntimeChatPropertyName, problems)!,
                ConfigBaseUrl = ReflectScope.StaticProperty(problems, config, ConfigBaseUrlPropertyName)!,
                DisplayName = ReflectScope.Exact(
                    config, DisplayNameMethodName, requireStatic: true, Array.Empty<string>(), "System.String",
                    problems)!
            };

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

            // The candidate list is not optional: without it the arrows would have nowhere to go.
            ChatServerCatalog.TryCapture(host, problems);

            return shape;
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
