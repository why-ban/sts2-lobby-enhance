using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;

namespace Sts2LobbyEnhance;

/// <summary>
/// Lets the player publish a new room to one chosen lobby server. 自动（延迟最低） is both the default and the behaviour
/// without this repair; a picked server is a constraint only create honours, and it is never written to config.json
/// </summary>
internal sealed class CreateServerPickerFix : ICompatFix
{
    internal const string FixId = "server-pool-create-picker";

    internal const int ProbeBudgetMs = 2000;

    private static PoolAnchors? _anchors;

    private static readonly ConditionalWeakTable<object, CreateServerPickerSection> Sections = new();

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
            PoolAnchors? anchors = PoolAnchors.TryCapture(host, PoolAnchorScope.CreatePicker, out reason);
            if (anchors == null)
            {
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
            anchors.BuildCreateDialog,
            postfix: new HarmonyMethod(typeof(CreateServerPickerFix), nameof(BuildCreateDialogPostfix))
            {
                priority = Priority.Last
            });

        harmony.Patch(
            anchors.OpenCreateDialog,
            postfix: new HarmonyMethod(typeof(CreateServerPickerFix), nameof(OpenCreateDialogPostfix))
            {
                priority = Priority.Last
            });

        Entry.Info(
            "[server-pool] the create dialog now offers a collapsible server picker (default 自动（延迟最低）), and every " +
            "open of the dialog starts at that default again; patched " + PoolAnchors.OverlayTypeName +
            ".BuildCreateDialog (LanConnectLobbyOverlay.cs:2480) and .OpenCreateDialogInternal (:4973).");
    }

    // ReSharper disable once InconsistentNaming - Harmony binds these by their exact names.
    public static void BuildCreateDialogPostfix(object? __instance, object? __result)
    {
        try
        {
            PoolAnchors? anchors = _anchors;
            if (anchors == null || __instance == null || __result is not Control shell)
            {
                return;
            }

            if (anchors.CreateDialogBody?.GetValue(__instance) is not VBoxContainer body)
            {
                return;
            }

            if (!shell.IsAncestorOf(body))
            {
                return;
            }

            Control? errorLabel = anchors.CreateDialogErrorLabel?.GetValue(__instance) as Control;
            CreateServerPickerSection section = Sections.GetValue(
                __instance,
                overlay => new CreateServerPickerSection(anchors, overlay, body, errorLabel));
            section.Attach();
        }
        catch (Exception exception)
        {
            PoolRefreshFix.WarnOnce(
                "create-picker",
                "the server picker could not be added to the create dialog: " + PoolRoomClient.Describe(exception));
        }
    }

    // ReSharper disable once InconsistentNaming - Harmony binds these by their exact names.
    public static void OpenCreateDialogPostfix(object? __instance)
    {
        try
        {
            if (__instance == null)
            {
                return;
            }

            // A manual choice belongs to the room being created, not to the session: the dialog is built once, so
            // without this the previous room's server would still be pinned the next time the dialog opens.
            CreateServerPickerState.Reset(__instance);
            if (Sections.TryGetValue(__instance, out CreateServerPickerSection? section) && section != null)
            {
                section.ResetToDefault();
            }
        }
        catch (Exception exception)
        {
            PoolRefreshFix.WarnOnce(
                "create-picker-reset",
                "the picker could not be reset for this open: " + PoolRoomClient.Describe(exception));
        }
    }

    internal static int ProbeBudget => ProbeBudgetMs;
}

/// <summary>
/// The per-overlay remembered choice: <see cref="BaseUrl"/> null means 自动（延迟最低）, anything else is the one authority
/// the create repair may switch to before publishing
/// </summary>
internal sealed class CreateServerPickerState
{
    internal string? BaseUrl;

    internal bool Expanded;

    private static readonly ConditionalWeakTable<object, CreateServerPickerState> States = new();

    internal static CreateServerPickerState For(object overlay) => States.GetValue(overlay, _ => new CreateServerPickerState());

    internal static string? SelectedBaseUrl(object overlay) => For(overlay).BaseUrl;

    /// <summary>Back to 自动（延迟最低） and collapsed: what each open of the create dialog starts from</summary>
    internal static void Reset(object overlay)
    {
        CreateServerPickerState state = For(overlay);
        state.BaseUrl = null;
        state.Expanded = false;
    }
}

/// <summary>
/// Pure UI: it never switches servers itself, and every node write tolerates a dialog that closed while a probe was
/// still running
/// </summary>
internal sealed class CreateServerPickerSection
{
    private const string RootNodeName = "LobbyEnhanceCreateServerPicker";

    private const string CollapsedMark = "  ▸";

    private const string ExpandedMark = "  ▾";

    private const string SelectedBullet = "● ";

    private const string UnselectedBullet = "○ ";

    private const string RowBaseMeta = "pool_picker_base";

    private const string RowLabelMeta = "pool_picker_label";

    /// <summary>Set once a row has been styled, so the first restyle cannot be skipped as "unchanged".</summary>
    private const string RowStyledMeta = "pool_picker_styled";

    /// <summary>The selection state the host styling call was last applied with.</summary>
    private const string RowSelectedMeta = "pool_picker_selected";

    /// <summary>Share of the viewport height the expanded list may take on a phone before it scrolls on its own</summary>
    private const float ListHeightFraction = 0.38f;

    private const float MinListHeight = 160f;

    private const float FallbackRowHeight = 44f;

    private readonly PoolAnchors _anchors;

    private readonly object _overlay;

    private readonly VBoxContainer _body;

    private readonly Control? _errorLabel;

    private readonly VBoxContainer _root;

    private readonly Button _header;

    private readonly VBoxContainer _rows;

    /// <summary>The scrolling shell around the rows on a phone, absent on a desktop where the dialog scrolls instead</summary>
    private readonly ScrollContainer? _listScroll;

    /// <summary>Whichever node carries "the list is expanded": the scroll when there is one, else the rows</summary>
    private readonly Control _listHost;

    private readonly Label? _hint;

    private int _refreshing;

    internal CreateServerPickerSection(
        PoolAnchors anchors,
        object overlay,
        VBoxContainer body,
        Control? errorLabel)
    {
        _anchors = anchors;
        _overlay = overlay;
        _body = body;
        _errorLabel = errorLabel;

        CreateServerPickerState state = CreateServerPickerState.For(overlay);

        _root = new VBoxContainer
        {
            Name = RootNodeName,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        _root.AddThemeConstantOverride("separation", 8);

        _header = BuildHeader(state);
        _root.AddChild(_header);

        _rows = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        _rows.AddThemeConstantOverride("separation", 6);

        if (TouchChatScrollFix.Platform.IsMobile)
        {
            _listScroll = new ScrollContainer
            {
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                Visible = state.Expanded
            };
            _listScroll.AddChild(_rows);
            _root.AddChild(_listScroll);
            _listHost = _listScroll;
        }
        else
        {
            _rows.Visible = state.Expanded;
            _root.AddChild(_rows);
            _listHost = _rows;
        }

        _hint = BuildHint();
        _root.AddChild(_hint);

        UpdateHeader();
    }

    internal void Attach()
    {
        try
        {
            foreach (Node existing in _body.GetChildren())
            {
                if (existing is not null && existing.Name == RootNodeName && !ReferenceEquals(existing, _root))
                {
                    _body.RemoveChild(existing);
                    existing.QueueFree();
                }
            }

            if (_root.GetParent() != null)
            {
                return;
            }

            _body.AddChild(_root);
            if (_errorLabel != null && ReferenceEquals(_errorLabel.GetParent(), _body))
            {
                _body.MoveChild(_root, _errorLabel.GetIndex());
            }

            // The ctor's UpdateHeader ran while the section was still parentless, where every node write is skipped on
            // purpose (IsInsideTree guard). Now that it is in the dialog, paint the real text.
            UpdateHeader();
            UpdateHint(CreateServerPickerState.For(_overlay));
            if (CreateServerPickerState.For(_overlay).Expanded)
            {
                RebuildRows();
            }
        }
        catch (Exception exception)
        {
            PoolRefreshFix.WarnOnce("create-picker-attach", "the picker could not be placed: " + PoolRoomClient.Describe(exception));
        }
    }

    private Button BuildHeader(CreateServerPickerState state)
    {
        TouchDragScroll? drag = null;
        Action toggle = () =>
        {
            if (drag?.ConsumeActivation() == true)
            {
                return;
            }

            Toggle();
        };

        Button? button = null;
        if (_anchors.CreateActionButton is { } factory)
        {
            ParameterInfo[] parameters = factory.GetParameters();
            object? glyph = parameters.Length >= 6 ? Enum.ToObject(parameters[5].ParameterType, 0) : null;
            button = factory.Invoke(_overlay, new object?[]
            {
                string.Empty,
                "选择房间发布到哪一台大厅服务器。默认自动：延迟最低且允许建房的那台。",
                toggle,
                false,
                false,
                glyph
            }) as Button;
        }

        if (button == null)
        {
            button = new Button { CustomMinimumSize = new Vector2(0f, 44f) };
            button.Pressed += toggle;
        }

        button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        // A drag over the header is an attempt to scroll the dialog, not a request to fold the section
        if (TouchChatScrollFix.Platform.IsMobile)
        {
            drag = TouchDragScroll.Attach(button);
        }

        return button;
    }

    private Label? BuildHint()
    {
        Label? label = _anchors.CreateBodyLabel?.Invoke(null, new object?[] { string.Empty }) as Label;
        if (label == null)
        {
            return null;
        }

        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.AddThemeFontSizeOverride("font_size", 13);
        label.AddThemeColorOverride("font_color", MutedColor());
        label.Visible = false;
        return label;
    }

    private void Toggle()
    {
        try
        {
            CreateServerPickerState state = CreateServerPickerState.For(_overlay);
            state.Expanded = !state.Expanded;
            if (IsLive(_listHost))
            {
                _listHost.Visible = state.Expanded;
            }

            UpdateHeader();
            if (!state.Expanded)
            {
                return;
            }

            RebuildRows();
            _ = RefreshAsync();
        }
        catch (Exception exception)
        {
            PoolRefreshFix.WarnOnce("create-picker-toggle", "the picker could not be expanded: " + PoolRoomClient.Describe(exception));
        }
    }

    /// <summary>No network here: the latency column is only what the cache already knows</summary>
    private void RebuildRows()
    {
        try
        {
            if (!IsLive(_rows))
            {
                return;
            }

            foreach (Node child in _rows.GetChildren())
            {
                _rows.RemoveChild(child);
                child.QueueFree();
            }

            CreateServerPickerState state = CreateServerPickerState.For(_overlay);

            AddRow(null, "自动（延迟最低）", "按延迟升序挑一台允许建房的服务器。", state.BaseUrl == null);

            foreach (PoolServer server in CandidateServers(state.BaseUrl))
            {
                bool selected = state.BaseUrl != null && PoolAddress.Same(server.BaseUrl, state.BaseUrl);
                AddRow(server.BaseUrl, server.ShortName(16) + " · " + LatencyLabel(server), server.BaseUrl, selected);
            }

            ApplyListHeight();
            UpdateHint(state);
        }
        catch (Exception exception)
        {
            PoolRefreshFix.WarnOnce("create-picker-rows", "the server list could not be drawn: " + PoolRoomClient.Describe(exception));
        }
    }

    /// <summary>
    /// Gives the list a height of its own on a phone, as tall as its content but never more than a share of the screen.
    /// The dialog rebuilt nothing when the section expanded - its card is sized once from the collapsed body - so without
    /// a cap the only way to the rest of the dialog is the dialog's own scrollbar
    /// </summary>
    private void ApplyListHeight()
    {
        try
        {
            ScrollContainer? scroll = _listScroll;
            if (scroll == null || !GodotObject.IsInstanceValid(scroll) || !scroll.IsInsideTree())
            {
                return;
            }

            float cap = Mathf.Max(MinListHeight, scroll.GetViewportRect().Size.Y * ListHeightFraction);
            scroll.CustomMinimumSize = new Vector2(0f, Mathf.Min(ListContentHeight(), cap));
        }
        catch (Exception exception)
        {
            PoolRefreshFix.WarnOnce(
                "create-picker-height",
                "the server list could not be given a height: " + PoolRoomClient.Describe(exception));
        }
    }

    private float ListContentHeight()
    {
        if (!IsLive(_rows))
        {
            return 0f;
        }

        float height = _rows.GetCombinedMinimumSize().Y;
        return height > 0f ? height : Mathf.Max(1, _rows.GetChildCount()) * FallbackRowHeight;
    }

    /// <summary>
    /// One choice. The selected one borrows upstream's "primary" button styling (the same emphasis the CREATE button
    /// uses) plus a filled bullet, so the state is visible without a toggle's pressed colour taking over the dialog.
    /// </summary>
    private void AddRow(string? baseUrl, string label, string tooltip, bool selected)
    {
        var row = new Button
        {
            TooltipText = Display(tooltip),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Alignment = HorizontalAlignment.Left
        };

        row.SetMeta(RowBaseMeta, baseUrl ?? string.Empty);
        row.SetMeta(RowLabelMeta, label);
        ApplyRowStyle(row, label, selected);

        // The row stops the pointer, so it is also the row that has to move the list and then refuse the activation a
        // swipe would otherwise produce
        TouchDragScroll? drag = TouchChatScrollFix.Platform.IsMobile ? TouchDragScroll.Attach(row, _listScroll) : null;
        row.Pressed += () =>
        {
            if (drag?.ConsumeActivation() == true)
            {
                return;
            }

            Select(baseUrl);
        };

        _rows.AddChild(row);
    }

    private void ApplyRowStyle(Button row, string label, bool selected)
    {
        // Upstream's styling call runs on every restyle of every row; skipping it when nothing changed keeps a click
        // from restyling rows that keep their state (and from re-running whatever the host helper does internally)
        bool known = row.HasMeta(RowStyledMeta) && row.GetMeta(RowStyledMeta).AsBool();
        bool wasSelected = known && row.HasMeta(RowSelectedMeta) && row.GetMeta(RowSelectedMeta).AsBool();
        if (known && wasSelected == selected && string.Equals(row.Text, Display((selected ? SelectedBullet : UnselectedBullet) + label), StringComparison.Ordinal))
        {
            return;
        }

        row.Text = Display((selected ? SelectedBullet : UnselectedBullet) + label);
        _anchors.ApplyButtonStyle?.Invoke(null, new object?[] { row, selected, false });
        row.SetMeta(RowStyledMeta, true);
        row.SetMeta(RowSelectedMeta, selected);
    }

    private void RestyleRows(string? selectedBaseUrl)
    {
        if (!IsLive(_rows))
        {
            return;
        }

        foreach (Node child in _rows.GetChildren())
        {
            if (child is not Button row || !row.HasMeta(RowBaseMeta) || !row.HasMeta(RowLabelMeta))
            {
                continue;
            }

            string rowBase = row.GetMeta(RowBaseMeta).AsString();
            string label = row.GetMeta(RowLabelMeta).AsString();
            bool selected = string.IsNullOrEmpty(rowBase)
                ? selectedBaseUrl == null
                : selectedBaseUrl != null && PoolAddress.Same(rowBase, selectedBaseUrl);
            ApplyRowStyle(row, label, selected);
        }
    }

    private void Select(string? baseUrl)
    {
        try
        {
            CreateServerPickerState state = CreateServerPickerState.For(_overlay);

            // Picking a server answers the question this section asks, so the list folds and the header carries the
            // choice; the selection itself outlives the fold and is what the create path reads
            state.BaseUrl = baseUrl;
            state.Expanded = false;
            if (IsLive(_listHost))
            {
                _listHost.Visible = false;
            }

            RestyleRows(baseUrl);
            UpdateHeader();
            UpdateHint(state);
        }
        catch (Exception exception)
        {
            PoolRefreshFix.WarnOnce("create-picker-select", "the selection could not be stored: " + PoolRoomClient.Describe(exception));
        }
    }

    private void UpdateHeader()
    {
        try
        {
            if (!IsLive(_header))
            {
                return;
            }

            CreateServerPickerState state = CreateServerPickerState.For(_overlay);
            string current = state.BaseUrl == null
                ? "自动（延迟最低）"
                : PoolAddress.ShortName(null, state.BaseUrl, 16);
            _header.Text = Display("服务器 · " + current + (state.Expanded ? ExpandedMark : CollapsedMark));
        }
        catch (Exception exception)
        {
            PoolRefreshFix.WarnOnce("create-picker-header", "the picker header could not be updated: " + PoolRoomClient.Describe(exception));
        }
    }

    /// <summary>
    /// One muted line, only when the capability data for the pinned server actually exists: it says the server cannot
    /// host this dialog's protocol profile. It is advisory - the create path still tries, because only the service's
    /// own answer is authoritative.
    /// </summary>
    private void UpdateHint(CreateServerPickerState state)
    {
        try
        {
            Label? hint = _hint;
            if (!IsLive(hint))
            {
                return;
            }

            string text = string.Empty;
            if (state.BaseUrl != null)
            {
                PoolServer? selected = CandidateServers(state.BaseUrl)
                    .FirstOrDefault(server => PoolAddress.Same(server.BaseUrl, state.BaseUrl));
                (string? profile, int version) = ResolveOwnProfile();
                if (selected != null && profile != null && PoolServerCatalog.IsCapabilityFresh(selected))
                {
                    text = PoolServerCatalog.SupportsProfile(selected, profile, version)
                        ? string.Empty
                        : "该服务器的协议能力与当前档位（" + profile + "）不匹配，可能无法建房；仍会按你的选择尝试。";
                }
            }

            hint!.Text = Display(text);
            hint.Visible = text.Length > 0;
        }
        catch (Exception exception)
        {
            PoolRefreshFix.WarnOnce("create-picker-hint", "the protocol hint could not be updated: " + PoolRoomClient.Describe(exception));
        }
    }

    private async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) != 0)
        {
            return;
        }

        try
        {
            IReadOnlyList<PoolServer> servers = PoolServerCatalog.GetServers(_anchors);
            List<Task> pending = servers
                .Where(server => !PoolServerCatalog.IsCoolingDown(server))
                .Where(PoolServerCatalog.NeedsProbe)
                .Select(server => PoolServerCatalog.ProbeAsync(
                    _anchors,
                    server,
                    CreateServerPickerFix.ProbeBudget,
                    CancellationToken.None))
                .ToList();
            if (pending.Count == 0)
            {
                return;
            }

            Task all = Task.WhenAll(pending);
            Task budget = Task.Delay(CreateServerPickerFix.ProbeBudget);
            Task finished = await Task.WhenAny(all, budget).ConfigureAwait(true);
            if (ReferenceEquals(finished, all))
            {
                await all.ConfigureAwait(true);
            }

            // Redraw either way: the budget expiring is not a failure, it just means the slower nodes keep "未测" until
            // the next expand. Without this the panel could stay blank even though the fast servers had already answered.
            RebuildRows();
        }
        catch (Exception exception)
        {
            PoolRefreshFix.WarnOnce("create-picker-probe", "refreshing the latency column failed: " + PoolRoomClient.Describe(exception));
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    /// <summary>Pool order, plus the pinned server when the catalog no longer lists it (never silently drop a choice).</summary>
    private IReadOnlyList<PoolServer> CandidateServers(string? selectedBaseUrl)
    {
        List<PoolServer> servers = PoolServerCatalog.GetServers(_anchors).ToList();
        if (!string.IsNullOrWhiteSpace(selectedBaseUrl)
            && !servers.Exists(server => PoolAddress.Same(server.BaseUrl, selectedBaseUrl)))
        {
            servers.Add(new PoolServer(selectedBaseUrl));
        }

        return servers;
    }

    private string LatencyLabel(PoolServer server) =>
        server.LatencyMs is { } latency and > 0 and < PoolServerFetch.UnknownLatency
            ? latency + " ms"
            : "未测";

    private (string? Profile, int Version) ResolveOwnProfile()
    {
        try
        {
            if (_anchors.GetSelectedCreateProtocolId == null
                || _anchors.ProtocolProfileFromCreateId == null
                || _anchors.ProfileToCanonical == null)
            {
                return (null, 0);
            }

            if (_anchors.GetSelectedCreateProtocolId.Invoke(_overlay, null) is not int protocolId)
            {
                return (null, 0);
            }

            object? profile = _anchors.ProtocolProfileFromCreateId.Invoke(null, new object?[] { protocolId });
            if (profile == null)
            {
                return (null, 0);
            }

            string? canonical = _anchors.ProfileToCanonical.Invoke(null, new object?[] { profile }) as string;
            if (string.IsNullOrWhiteSpace(canonical))
            {
                return (null, 0);
            }

            int version = 0;
            if (string.Equals(canonical, PoolServerCatalog.TailProfileCanonical, StringComparison.OrdinalIgnoreCase)
                && _anchors.TailRuntimeAvailable?.GetValue(null) is true)
            {
                version = _anchors.TailLanProtocolVersion;
            }

            return (canonical, version);
        }
        catch (Exception)
        {
            return (null, 0);
        }
    }

    private Color MutedColor()
    {
        try
        {
            if (_anchors.TextMutedColor?.GetValue(null) is Color color)
            {
                return color;
            }
        }
        catch (Exception)
        {
            // Fall through to the literal.
        }

        return new Color(0.66f, 0.68f, 0.72f);
    }

    /// <summary>
    /// Validity, not IsInsideTree: BuildCreateDialog's return value is added to the overlay *after* the method returns,
    /// so every node of this section is still detached while it is being built. Text and visibility work detached; only
    /// a freed node must not be touched.
    /// </summary>
    private static bool IsLive(Node? node) => node != null && GodotObject.IsInstanceValid(node);

    /// <summary>
    /// Every string this panel prints goes through the host mod's own glyph fallback, the call its buttons and labels
    /// already use. The panel is full of the punctuation and bullet glyphs that fallback exists for, so printing them
    /// raw is the half-applied state: correct on a desktop, tofu on the Android builds that need it.
    /// </summary>
    private string Display(string text)
    {
        try
        {
            return _anchors.NormalizeForDisplay?.Invoke(null, new object?[] { text }) as string ?? text;
        }
        catch (Exception)
        {
            return text;
        }
    }

    /// <summary>Repaints after the choice was reset, so the header stops showing the previous room's server.</summary>
    internal void ResetToDefault()
    {
        try
        {
            CreateServerPickerState state = CreateServerPickerState.For(_overlay);
            if (IsLive(_listHost))
            {
                _listHost.Visible = state.Expanded;
            }

            UpdateHeader();
            UpdateHint(state);
        }
        catch (Exception exception)
        {
            PoolRefreshFix.WarnOnce(
                "create-picker-repaint",
                "the picker could not be repainted for this open: " + PoolRoomClient.Describe(exception));
        }
    }
}
