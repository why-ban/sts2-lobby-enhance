using System.Collections;
using System.Reflection;
using Godot;
using HarmonyLib;

namespace Sts2LobbyEnhance;

/// <summary>
/// Adds a 「版本」 section with one chip 「相同游戏版本」 to the lobby's room-filter dialog and, while it is on,
/// narrows the room list to the rooms whose advertised game version equals this machine's.
///
/// Only the game version is compared (<c>LobbyRoomSummary.Version</c>, LanConnectLobbyModels.cs:25). The mod
/// version and the protocol profile stay out of the predicate on purpose: a room on a different protocol profile
/// is still joinable, and mixing the three would hide rooms the player can actually enter.
///
/// The chip is built by the same helpers the other six chips use — <c>CreateBodyLabel</c> (:7980) for the
/// section label, <c>ApplyFilterChipStyle</c> (:8248) for the visuals, 44 px like
/// <c>CreateFilterToggleButton</c> (:2801-2811) — and toggling it replays the two calls upstream's own chips make
/// (<c>SyncFilterDialogButtons</c> then <c>ApplyRoomFilterState</c>, :2743-2796), so paging, the scroll offset,
/// the 「筛选」 button accent and the summary line all move the way they do for every other filter.
///
/// The dialog is a fixed 360 px box (:3164-3170). A further label + chip row does not fit in it, so the postfix
/// grows the box by <see cref="DialogGrowth"/> px on both sides; without that the new section spills out of the
/// panel, which is exactly the kind of broken layout this project refuses to ship.
///
/// Everything here is opt-in and reversible: the state starts off (「默认不勾」), the chip is disabled when the
/// game version cannot be read at all, and <c>ClearRoomFiltersAndSearch</c> clears it with every other filter.
/// </summary>
internal sealed class RoomVersionFilterFix : ICompatFix
{
    private const string OverlayTypeName = "Sts2LanConnect.Scripts.LanConnectLobbyOverlay";
    private const string RoomSummaryTypeName = "Sts2LanConnect.Scripts.LobbyRoomSummary";
    private const string BuildInfoTypeName = "Sts2LanConnect.Scripts.LanConnectBuildInfo";

    /// <summary>Node name of the injected chip row: both the idempotency probe and the log line use it.</summary>
    private const string RowNodeName = "LobbyEnhanceVersionFilterRow";

    private const string SectionText = "版本";
    private const string ChipText = "相同游戏版本";
    private const string ChipTooltip = "只看与当前游戏版本相同的房间";
    private const string UnavailableTooltip = "无法读取本机游戏版本";

    /// <summary>Token appended to <c>DescribeRoomFilterState</c>'s result (:7320-7381).</summary>
    private const string FilterStateToken = "同版本";

    /// <summary>What <c>DescribeRoomFilterState</c> returns when nothing is filtered (:7380).</summary>
    private const string AllRoomsSummary = "全部";

    /// <summary>The build-info fallback when no version source answers (LanConnectBuildInfo.cs:30).</summary>
    private const string UnknownGameVersion = "UNKNOWN";

    /// <summary>
    /// Half of the height the new section needs. <c>CreateDialogShell</c> pins the filter dialog to
    /// <c>OffsetTop = -180 / OffsetBottom = 180</c> (:3166-3169), i.e. a 360 px box; the extra 16 px label,
    /// 44 px chip row and two 14 px separations do not fit, so both offsets move outwards by this much.
    /// </summary>
    private const float DialogGrowth = 43f;

    /// <summary>
    /// The user's choice. Deliberately process-wide rather than per overlay instance: <c>RebuildForThemeChange</c>
    /// (:3045) replaces the whole node, and a filter that silently came back on after a theme switch — while the
    /// chip shown in the rebuilt dialog said the opposite — would be a lie. The chip is always rendered from this
    /// value, so the two can never disagree.
    /// </summary>
    private static bool _sameVersionOnly;

    /// <summary>The chip of the most recently built dialog; validity is re-checked before every use.</summary>
    private static Button? _chip;

    /// <summary>This machine's game version, normalized; empty until it could be read at least once.</summary>
    private static string _localVersion = string.Empty;

    private static Shape? _shape;

    private static PatchTargets _targets;

    public string Id => "room-version-filter";

    /// <summary>
    /// The room-list filter row is its own seam: a shape change in that dialog rolls back this repair and nothing
    /// else
    /// </summary>
    public FixGroup Group => FixGroup.Filter;

    public bool Applies(out string reason)
    {
        Assembly? host = Reflect.FindAssembly(Entry.HostModAssemblyName);
        if (host == null)
        {
            reason = "host mod '" + Entry.HostModAssemblyName + "' is not loaded";
            return false;
        }

        if (!Shape.TryCapture(host, out Shape? shape, out PatchTargets targets, out string missing))
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
        // Applies() resolved and cached these; reaching Apply without them is a host-contract violation, not a
        // build problem, so it must not be tolerated silently.
        Shape shape = _shape ?? throw new InvalidOperationException(
            "room-version-filter: Applies() did not capture the filter-dialog anchors; refusing to patch blind.");
        PatchTargets targets = _targets;

        // The section is added by a postfix, so upstream's dialog is built exactly as it always was and we only
        // append to it. A missing body leaves the dialog untouched (see BuildFilterDialogPostfix).
        harmony.Patch(
            targets.BuildFilterDialog,
            postfix: new HarmonyMethod(typeof(RoomVersionFilterFix), nameof(BuildFilterDialogPostfix)));

        // No state of its own to install: this only repaints the chip, so a failure here costs a colour.
        harmony.Patch(
            targets.SyncFilterDialogButtons,
            postfix: new HarmonyMethod(typeof(RoomVersionFilterFix), nameof(SyncFilterDialogButtonsPostfix)));

        // The predicate itself. Postfix + ref object: the list instance is the caller's own, and the entries are
        // removed from it in place, so every call site (paging :3428, :5884 and the stage :3562) agrees.
        harmony.Patch(
            targets.GetFilteredRooms,
            postfix: new HarmonyMethod(typeof(RoomVersionFilterFix), nameof(GetFilteredRoomsPostfix)));

        // Without this the 「筛选」 button stays unlit and the no-match hint claims the room list has nothing
        // rather than that a filter is on (:3568-3573, :8217-8218).
        harmony.Patch(
            targets.HasRoomSearchOrFilter,
            postfix: new HarmonyMethod(typeof(RoomVersionFilterFix), nameof(HasRoomSearchOrFilterPostfix)));

        // The summary line the player reads (:3569-3573, :3627) has to name this filter like any other.
        harmony.Patch(
            targets.DescribeRoomFilterState,
            postfix: new HarmonyMethod(typeof(RoomVersionFilterFix), nameof(DescribeRoomFilterStatePostfix)));

        // 「清空」 clears this filter together with the upstream ones. Prefix, not postfix: upstream's own reset
        // runs either way, and doing ours first keeps the room stage upstream rebuilds at :5947 consistent.
        harmony.Patch(
            targets.ClearRoomFiltersAndSearch,
            prefix: new HarmonyMethod(typeof(RoomVersionFilterFix), nameof(ClearRoomFiltersAndSearchPrefix)));

        // Naming the bound members is what makes a later "the chip is gone" report diagnosable from one log line.
        Entry.Info(
            "room-version-filter: patched 6 seams on " + OverlayTypeName + " (BuildFilterDialog / " +
            "SyncFilterDialogButtons / GetFilteredRooms / HasRoomSearchOrFilter / DescribeRoomFilterState / " +
            "ClearRoomFiltersAndSearch); the filter dialog now offers 「" + ChipText + "」 off by default and " +
            "compares only " + RoomSummaryTypeName + "." + shape.RoomVersion.Name + " against " +
            BuildInfoTypeName + "." + shape.GetGameVersion.Name + "().");
    }

    // =====================================================================================================
    // The section itself
    // =====================================================================================================

    /// <summary>
    /// Appends 「版本」 + the chip row to the dialog body upstream just built.
    ///
    /// The body is located as the first <see cref="VBoxContainer"/> in the shell rather than by walking
    /// <c>shell → CenterContainer → MarginContainer → PanelContainer → VBoxContainer</c> by hand
    /// (<c>CreateDialogShell</c> :3158-3175), so a theme or layout change that inserts a wrapper does not
    /// break this. Missing parts are reported once and skipped — upstream's dialog is left exactly as it was.
    /// </summary>
    private static void BuildFilterDialogPostfix(object? __instance, Control? __result)
    {
        try
        {
            if (_shape is not { } shape || __instance == null || __result == null)
            {
                return;
            }

            // Re-entering this method for the same shell must not stack a second section: the chip would then
            // be a duplicate, and the dialog would grow twice.
            if (FindByName(__result, RowNodeName) is HBoxContainer existingRow)
            {
                _chip = FindChip(existingRow);
                return;
            }

            if (FirstVBox(__result) is not { } body)
            {
                WarnOnce("room-version-filter: the filter dialog has no VBoxContainer body; the 「" + SectionText +
                         "」 section was not added and upstream's dialog is unchanged.");
                return;
            }

            // Upstream's own factory, so the label is byte-for-byte the style of 「房间类型」/「游戏模式」
            // (UiText + TextStrong + 16 px, :7980-7989).
            if (shape.CreateBodyLabel.Invoke(null, new object[] { SectionText }) is not Label sectionLabel)
            {
                WarnOnce("room-version-filter: " + OverlayTypeName + ".CreateBodyLabel returned no Label; the 「" +
                         SectionText + "」 section was not added.");
                return;
            }

            string localVersion = LocalGameVersion(shape);
            bool usable = localVersion.Length > 0;
            if (!usable)
            {
                // Only reachable when the build-info chain fell through to UNKNOWN. A filter that cannot tell
                // what "same" means must not be left armed: it would silently hide every room.
                _sameVersionOnly = false;
            }

            HBoxContainer row = new()
            {
                Name = RowNodeName,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            row.AddThemeConstantOverride("separation", 10);

            Button chip = new()
            {
                Text = ChipText,
                CustomMinimumSize = new Vector2(0f, 44f),
                TooltipText = usable ? ChipTooltip : UnavailableTooltip,
                Disabled = !usable
            };

            // Upstream's own chip painter: same styleboxes, same font sizes, same disabled look (:8248-8267).
            shape.ApplyChipStyle.Invoke(null, new object[] { chip, _sameVersionOnly });
            chip.Connect(BaseButton.SignalName.Pressed, Callable.From(() => OnChipPressed(__instance, chip)));

            row.AddChild(chip);
            body.AddChild(sectionLabel);
            body.AddChild(row);

            WidenDialog(body);
            _chip = chip;

            Entry.Info(
                "room-version-filter: 「" + SectionText + "」 section added to the filter dialog (rows=" +
                body.GetChildCount() + ", localVersion='" + localVersion + "', chipUsable=" + usable +
                ", sameVersionOnly=" + _sameVersionOnly + ").");
        }
        catch (Exception exception)
        {
            // An optional filter must never break the filter dialog. Upstream's dialog stays as it is; say so once.
            WarnOnce("room-version-filter: could not add the 「" + SectionText + "」 section: " + Describe(exception));
        }
    }

    /// <summary>
    /// Repaints the chip after upstream synced the other six (:2813-2826, reached from every chip toggle and from
    /// <c>OpenFilterDialog</c> :2832 and <c>UpdateRoomFilterButtons</c> :8224). Upstream cannot know our chip, so
    /// without this it keeps whatever style it was given the last time.
    /// </summary>
    private static void SyncFilterDialogButtonsPostfix()
    {
        try
        {
            if (_shape is not { } shape)
            {
                return;
            }

            if (_chip is not { } chip || !GodotObject.IsInstanceValid(chip))
            {
                return;
            }

            // ApplyFilterChipStyle also defines the disabled stylebox (:8258, :8265), so a disabled chip is
            // painted here like any other — it only never receives the press.
            shape.ApplyChipStyle.Invoke(null, new object[] { chip, _sameVersionOnly });
        }
        catch (Exception exception)
        {
            WarnOnce("room-version-filter: could not repaint the chip: " + Describe(exception));
        }
    }

    /// <summary>
    /// What upstream's own chips do on press (:2743-2796, :7294-7295): flip the flag, sync the chips, then let
    /// <c>ApplyRoomFilterState</c> reset the page, the scroll offset and the room stage (:7298-7307). Replaying
    /// those two calls instead of re-implementing them is what keeps pagination and the 「筛选」 accent correct.
    /// </summary>
    private static void OnChipPressed(object overlay, Button chip)
    {
        try
        {
            if (_shape is not { } shape || overlay == null || chip == null)
            {
                return;
            }

            if (!GodotObject.IsInstanceValid(chip) || chip.Disabled)
            {
                return;
            }

            if (overlay is not GodotObject target || !GodotObject.IsInstanceValid(target))
            {
                return;
            }

            _sameVersionOnly = !_sameVersionOnly;

            shape.SyncButtons.Invoke(overlay, null);
            shape.ApplyFilterState.Invoke(overlay, new object[] { "dialog_version" });

            Entry.Info("room-version-filter: sameVersionOnly=" + _sameVersionOnly + " (chip pressed).");
        }
        catch (Exception exception)
        {
            WarnOnce("room-version-filter: could not toggle the version filter: " + Describe(exception));
        }
    }

    // =====================================================================================================
    // The predicate
    // =====================================================================================================

    /// <summary>
    /// Drops every room whose advertised game version differs from this machine's.
    ///
    /// The entries are removed from the list upstream returned, in place, rather than replaced: the call sites
    /// assign <c>GetFilteredRooms()</c> straight into <c>List&lt;LobbyRoomSummary&gt;</c> (:3562), so handing back
    /// a different list instance would throw an <c>InvalidCastException</c> inside the game's own code.
    ///
    /// Declared as <c>ref object</c> because <c>LobbyRoomSummary</c> is a host type that is deliberately not
    /// referenced at compile time; Harmony binds the by-ref result to any assignable declared type (verified
    /// against the shipped 0Harmony 2.4.2 in this session).
    /// </summary>
    private static void GetFilteredRoomsPostfix(ref object? __result)
    {
        try
        {
            if (_shape is not { } shape || !_sameVersionOnly)
            {
                return;
            }

            if (__result is not IList rooms || rooms.Count == 0)
            {
                return;
            }

            string localVersion = LocalGameVersion(shape);
            if (localVersion.Length == 0)
            {
                // Cannot tell what "same" means right now. Showing everything is the honest choice; the chip is
                // disabled in this state anyway (see BuildFilterDialogPostfix).
                return;
            }

            for (int index = rooms.Count - 1; index >= 0; index--)
            {
                if (!IsSameVersion(shape, rooms[index], localVersion))
                {
                    rooms.RemoveAt(index);
                }
            }
        }
        catch (Exception exception)
        {
            // Never rethrow: this runs inside the room-list rebuild. Leaving the list untouched shows more rooms
            // than asked for, which is a far smaller problem than an exception in the lobby stage.
            WarnOnce("room-version-filter: could not narrow the room list: " + Describe(exception));
        }
    }

    private static void HasRoomSearchOrFilterPostfix(ref bool __result)
    {
        try
        {
            if (_sameVersionOnly)
            {
                __result = true;
            }
        }
        catch (Exception exception)
        {
            WarnOnce("room-version-filter: could not report the filter as active: " + Describe(exception));
        }
    }

    private static void DescribeRoomFilterStatePostfix(ref string? __result)
    {
        try
        {
            if (!_sameVersionOnly)
            {
                return;
            }

            string current = __result ?? string.Empty;
            // Upstream returns 「全部」 when nothing is selected (:7380). Keeping that word and appending would
            // read 「全部 · 同版本」, which contradicts itself.
            __result = current.Length == 0 || string.Equals(current, AllRoomsSummary, StringComparison.Ordinal)
                ? FilterStateToken
                : current + " · " + FilterStateToken;
        }
        catch (Exception exception)
        {
            WarnOnce("room-version-filter: could not describe the filter state: " + Describe(exception));
        }
    }

    /// <summary>
    /// 「清空」 resets upstream's six flags (:5938-5948); ours is cleared first so the rebuild it triggers at
    /// :5947 already shows every room again.
    /// </summary>
    private static void ClearRoomFiltersAndSearchPrefix()
    {
        try
        {
            _sameVersionOnly = false;
        }
        catch (Exception exception)
        {
            WarnOnce("room-version-filter: could not clear the version filter: " + Describe(exception));
        }
    }

    private static bool IsSameVersion(Shape shape, object? room, string localVersion)
    {
        if (room == null)
        {
            return false;
        }

        // An empty version is "unknown", never "equal": a room that does not say which game version it runs was
        // not verified to match, and pretending otherwise would put the player in a version-mismatch join.
        string roomVersion = NormalizeVersion(shape.RoomVersion.GetValue(room) as string);
        return roomVersion.Length > 0
               && string.Equals(roomVersion, localVersion, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// This machine's game version through upstream's own resolver (<c>LanConnectBuildInfo.GetGameVersion</c>
    /// :25-31), which reads <c>ReleaseInfoManager</c>, then the git commit, then the game assembly version.
    ///
    /// Cached only once it produced something usable: the resolver answers <c>UNKNOWN</c> while the release info
    /// is still loading, and caching that would keep the chip disabled for the rest of the session.
    /// </summary>
    private static string LocalGameVersion(Shape shape)
    {
        if (_localVersion.Length > 0)
        {
            return _localVersion;
        }

        string detected = NormalizeVersion(shape.GetGameVersion.Invoke(null, null) as string);
        if (detected.Length > 0)
        {
            _localVersion = detected;
        }

        return detected;
    }

    /// <summary>
    /// Trim, then drop one leading <c>v</c>/<c>V</c>, then compare case-insensitively. Upstream compares the same
    /// two strings with <c>Ordinal</c> after the same trim/strip (LanConnectLobbyManagedJoinFlow.cs:406-420), so a
    /// room only reaches this comparison when its shape already matches; the case-insensitive comparison here is
    /// the deliberate difference — <c>V0.113.0</c> and <c>v0.113.0</c> are the same build, not two.
    /// Returns <see cref="string.Empty"/> for anything unusable (empty, whitespace-only,
    /// <see cref="UnknownGameVersion"/>).
    /// </summary>
    private static string NormalizeVersion(string? value)
    {
        string normalized = (value ?? string.Empty).Trim();
        if (normalized.Length > 0 && (normalized[0] == 'v' || normalized[0] == 'V'))
        {
            normalized = normalized[1..].Trim();
        }

        if (normalized.Length == 0
            || string.Equals(normalized, UnknownGameVersion, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return normalized;
    }

    // =====================================================================================================
    // Layout
    // =====================================================================================================

    /// <summary>
    /// Grows the dialog box by <see cref="DialogGrowth"/> on both sides.
    ///
    /// <c>CreateDialogShell</c> sizes the filter dialog by anchors, not by content: <c>OffsetTop = -180</c> and
    /// <c>OffsetBottom = 180</c> (:3166-3169). The section added above is a 16 px label, a 44 px chip row and two
    /// 14 px body separations (:3116), so the box has to grow or the content overflows the panel.
    ///
    /// The margin is found as the nearest <see cref="MarginContainer"/> ancestor of the body instead of by index,
    /// so a wrapper added by a future theme does not silently disable this.
    /// </summary>
    private static void WidenDialog(Node body)
    {
        if (NearestMargin(body) is not { } margin)
        {
            WarnOnce("room-version-filter: the filter dialog has no MarginContainer ancestor; its height was left " +
                     "at 360 px and the new section may sit outside the panel.");
            return;
        }

        margin.OffsetTop -= DialogGrowth;
        margin.OffsetBottom += DialogGrowth;
    }

    private static MarginContainer? NearestMargin(Node node)
    {
        for (Node? current = node.GetParent(); current != null; current = current.GetParent())
        {
            if (current is MarginContainer margin)
            {
                return margin;
            }
        }

        return null;
    }

    /// <summary>
    /// The dialog body: first <see cref="VBoxContainer"/> in child order. <c>CreateDialogShell</c> puts nothing
    /// else that is one on the way to the body (shell → veil / CenterContainer → MarginContainer →
    /// PanelContainer → body, :3158-3175), and the pixel-border panel adds no child containers (only
    /// styleboxes).
    /// </summary>
    private static VBoxContainer? FirstVBox(Node root)
    {
        foreach (Node child in root.GetChildren())
        {
            if (child is VBoxContainer found)
            {
                return found;
            }

            if (FirstVBox(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static Node? FindByName(Node root, string name)
    {
        foreach (Node child in root.GetChildren())
        {
            if (string.Equals(child.Name.ToString(), name, StringComparison.Ordinal))
            {
                return child;
            }

            if (FindByName(child, name) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static Button? FindChip(Node row)
    {
        foreach (Node child in row.GetChildren())
        {
            if (child is Button button)
            {
                return button;
            }
        }

        return null;
    }

    private static string Describe(Exception exception) =>
        exception.GetType().Name + ": " + exception.Message;

    // =====================================================================================================
    // Logging
    // =====================================================================================================

    private const int MaxWarnedMessages = 8;

    /// <summary>
    /// One line per distinct problem, never two. A repaint that fails runs on every chip sync, so an unguarded
    /// <c>Entry.Warn</c> there would flood the log; keying by message keeps a second, different failure visible
    /// instead of hiding it behind the first.
    /// </summary>
    private static readonly HashSet<string> Warned = new(StringComparer.Ordinal);

    private static void WarnOnce(string message)
    {
        lock (Warned)
        {
            if (Warned.Count >= MaxWarnedMessages || !Warned.Add(message))
            {
                return;
            }
        }

        Entry.Warn(message);
    }

    // =====================================================================================================
    // Anchors
    // =====================================================================================================

    /// <summary>Methods this repair patches, install-time only</summary>
    internal readonly record struct PatchTargets(
        MethodInfo BuildFilterDialog,
        MethodInfo SyncFilterDialogButtons,
        MethodInfo GetFilteredRooms,
        MethodInfo HasRoomSearchOrFilter,
        MethodInfo DescribeRoomFilterState,
        MethodInfo ClearRoomFiltersAndSearch);

    /// <summary>
    /// Every reflection anchor the runtime path uses, filled in by <see cref="Shape.TryCapture"/> only after all of
    /// them resolved. Nothing here can be null once <see cref="Apply"/> installed the repair.
    /// </summary>
    internal sealed class Shape
    {
        private Shape(
            MethodInfo createBodyLabel,
            MethodInfo applyChipStyle,
            MethodInfo syncButtons,
            MethodInfo applyFilterState,
            PropertyInfo roomVersion,
            MethodInfo getGameVersion)
        {
            CreateBodyLabel = createBodyLabel;
            ApplyChipStyle = applyChipStyle;
            SyncButtons = syncButtons;
            ApplyFilterState = applyFilterState;
            RoomVersion = roomVersion;
            GetGameVersion = getGameVersion;
        }

        /// <summary><c>CreateBodyLabel(string)</c>, static private (:7980)</summary>
        internal MethodInfo CreateBodyLabel { get; }

        /// <summary><c>ApplyFilterChipStyle(Button, bool)</c>, static private (:8248)</summary>
        internal MethodInfo ApplyChipStyle { get; }

        /// <summary><c>SyncFilterDialogButtons()</c> (:2813)</summary>
        internal MethodInfo SyncButtons { get; }

        /// <summary><c>ApplyRoomFilterState(string)</c> (:7298)</summary>
        internal MethodInfo ApplyFilterState { get; }

        /// <summary><c>LobbyRoomSummary.Version</c> (LanConnectLobbyModels.cs:25)</summary>
        internal PropertyInfo RoomVersion { get; }

        /// <summary><c>LanConnectBuildInfo.GetGameVersion()</c> (:25)</summary>
        internal MethodInfo GetGameVersion { get; }

        /// <summary>
        /// Resolves everything by name and reports every miss at once. Metadata only: no <c>Invoke</c>, no
        /// <c>Activator</c>, no Godot node, no <c>typeof</c> on a host type — so this is safe in the headless
        /// harness, where there is no Godot runtime behind the loaded assembly.
        /// </summary>
        internal static bool TryCapture(Assembly host, out Shape? shape, out PatchTargets targets, out string missing)
        {
            List<string> problems = new();

            Type? overlay = ReflectScope.HostType(host, OverlayTypeName, problems);

            MethodInfo? buildDialog = ReflectScope.InstanceMethod(problems, overlay, "BuildFilterDialog");
            MethodInfo? syncButtons = ReflectScope.InstanceMethod(problems, overlay, "SyncFilterDialogButtons");
            MethodInfo? getFiltered = ReflectScope.InstanceMethod(problems, overlay, "GetFilteredRooms");
            MethodInfo? hasFilter = ReflectScope.InstanceMethod(problems, overlay, "HasRoomSearchOrFilter");
            MethodInfo? describe = ReflectScope.InstanceMethod(problems, overlay, "DescribeRoomFilterState");
            MethodInfo? clearFilters = ReflectScope.InstanceMethod(problems, overlay, "ClearRoomFiltersAndSearch");
            MethodInfo? applyFilterState = ReflectScope.InstanceMethod(
                problems,
                overlay,
                "ApplyRoomFilterState",
                "System.String");
            MethodInfo? applyChipStyle = ReflectScope.StaticMethod(
                problems,
                overlay,
                "ApplyFilterChipStyle",
                "Godot.Button",
                "System.Boolean");
            MethodInfo? createBodyLabel = ReflectScope.StaticMethod(
                problems,
                overlay,
                "CreateBodyLabel",
                "System.String");

            // The signatures the patch bodies depend on. A postfix binds to the result of whatever the method
            // returns, so a changed return type has to be a missing anchor rather than a silent no-op.
            RequireReturn(buildDialog, "Godot.Control", problems);
            RequireReturn(hasFilter, "System.Boolean", problems);
            RequireReturn(describe, "System.String", problems);
            RequireReturn(clearFilters, "System.Void", problems);
            RequireReturn(applyFilterState, "System.Void", problems);
            RequireReturn(applyChipStyle, "System.Void", problems);
            RequireReturn(createBodyLabel, "Godot.Label", problems);

            // The postfix removes entries from the list in place; that only works while the result is still the
            // exact List<LobbyRoomSummary> the call sites cast it to (:3562).
            if (getFiltered != null && !ReturnsListOf(getFiltered.ReturnType, RoomSummaryTypeName))
            {
                problems.Add(
                    OverlayTypeName + ".GetFilteredRooms returns " + DescribeType(getFiltered.ReturnType) +
                    ", expected List<LobbyRoomSummary>");
            }

            Type? roomSummary = ReflectScope.HostType(host, RoomSummaryTypeName, problems);
            PropertyInfo? roomVersion = ReflectScope.Property(problems, roomSummary, "Version");
            if (roomVersion != null && roomVersion.PropertyType.FullName != "System.String")
            {
                problems.Add(
                    RoomSummaryTypeName + ".Version is " + roomVersion.PropertyType.Name + ", expected String");
            }

            Type? buildInfo = ReflectScope.HostType(host, BuildInfoTypeName, problems);
            MethodInfo? getGameVersion = ReflectScope.StaticMethod(problems, buildInfo, "GetGameVersion");
            RequireReturn(getGameVersion, "System.String", problems);

            if (problems.Count > 0
                || buildDialog == null
                || syncButtons == null
                || getFiltered == null
                || hasFilter == null
                || describe == null
                || clearFilters == null
                || applyFilterState == null
                || applyChipStyle == null
                || createBodyLabel == null
                || roomVersion == null
                || getGameVersion == null)
            {
                shape = null;
                targets = default;
                missing = problems.Count > 0 ? string.Join("; ", problems) : "an anchor resolved to null";
                return false;
            }

            shape = new Shape(
                createBodyLabel,
                applyChipStyle,
                syncButtons,
                applyFilterState,
                roomVersion,
                getGameVersion);

            targets = new PatchTargets(
                buildDialog,
                syncButtons,
                getFiltered,
                hasFilter,
                describe,
                clearFilters);

            missing = string.Empty;
            return true;
        }

        private static void RequireReturn(MethodInfo? method, string expectedReturnTypeName, List<string> problems)
        {
            if (method == null)
            {
                // Already reported by the lookup that missed it.
                return;
            }

            if (method.ReturnType.FullName != expectedReturnTypeName)
            {
                problems.Add(method.Name + " returns " + DescribeType(method.ReturnType) + ", expected " +
                             expectedReturnTypeName);
            }
        }

        private static bool ReturnsListOf(Type returnType, string elementTypeName)
        {
            if (!returnType.IsGenericType
                || returnType.GetGenericTypeDefinition().FullName != "System.Collections.Generic.List`1")
            {
                return false;
            }

            return string.Equals(
                returnType.GetGenericArguments()[0].FullName,
                elementTypeName,
                StringComparison.Ordinal);
        }

        /// <summary>Short, log-safe name: a full generic argument list would bury the reason.</summary>
        private static string DescribeType(Type type) =>
            type.IsGenericType ? type.Name : type.FullName ?? type.Name;
    }
}
