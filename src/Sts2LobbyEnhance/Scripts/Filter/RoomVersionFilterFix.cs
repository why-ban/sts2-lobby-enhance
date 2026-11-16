using System.Collections;
using System.Reflection;
using Godot;
using HarmonyLib;

namespace Sts2LobbyEnhance;

/// <summary>
/// Adds a 「版本」 section with one chip 「相同游戏版本」 to the lobby's room-filter dialog and, while it is on,
/// narrows the room list to the rooms whose advertised game version equals this machine's.
///
/// Only the game version is compared. The mod version and the protocol profile stay out of the predicate: two rooms
/// whose profiles differ can still be joinable, and the one case where a profile difference blocks a join is rejected
/// by upstream's own join check with an explanation of its own, so filtering on it would hide rooms the player can
/// enter.
///
/// The chip is built by the same helpers the other six chips use and toggling it replays the two calls upstream's own
/// chips make (<c>SyncFilterDialogButtons</c> then <c>ApplyRoomFilterState</c>), so paging, the scroll offset, the
/// 「筛选」 button accent and the summary line all move the way they do for every other filter.
///
/// The dialog sizes itself to its content: <c>CreateDialogShell</c> puts the card in a <c>CenterContainer</c>,
/// which lays it out at its own combined minimum size, so the extra label and chip row grow the box on their own
/// and no offset has to be touched.
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

    /// <summary>Token appended to <c>DescribeRoomFilterState</c>'s result.</summary>
    private const string FilterStateToken = "同版本";

    /// <summary>What <c>DescribeRoomFilterState</c> returns when nothing is filtered.</summary>
    private const string AllRoomsSummary = "全部";

    /// <summary>The build-info fallback when no version source answers.</summary>
    private const string UnknownGameVersion = "UNKNOWN";

    /// <summary>
    /// The user's choice, deliberately process-wide rather than per overlay instance: a theme switch replaces the
    /// whole node, and a filter that came back on after one, while the rebuilt dialog's chip said the opposite, would
    /// be a lie. The chip is rendered from this value, so the two cannot disagree.
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
    /// The room-list filter row is its own seam: a shape change in that dialog rolls back this repair and nothing else.
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
        // Applies() resolved and cached these; reaching Apply without them is a host-contract violation, not a build
        // problem, so it is not tolerated silently.
        Shape shape = _shape ?? throw new InvalidOperationException(
            "room-version-filter: Applies() did not capture the filter-dialog anchors; refusing to patch blind.");
        PatchTargets targets = _targets;

        // The section is added by a postfix, so upstream's dialog is built exactly as before and we only append to it.
        // A missing body leaves the dialog untouched (see BuildFilterDialogPostfix).
        harmony.Patch(
            targets.BuildFilterDialog,
            postfix: new HarmonyMethod(typeof(RoomVersionFilterFix), nameof(BuildFilterDialogPostfix)));

        // No state of its own to install: this only repaints the chip, so a failure here costs a colour.
        harmony.Patch(
            targets.SyncFilterDialogButtons,
            postfix: new HarmonyMethod(typeof(RoomVersionFilterFix), nameof(SyncFilterDialogButtonsPostfix)));

        // The predicate itself. Postfix + ref object: the list instance is the caller's own and the entries are removed
        // from it in place, so every call site - paging, stage and the pool refresh - sees the same list.
        harmony.Patch(
            targets.GetFilteredRooms,
            postfix: new HarmonyMethod(typeof(RoomVersionFilterFix), nameof(GetFilteredRoomsPostfix)));

        // Without this the 「筛选」 button stays unlit and the no-match hint claims the room list has nothing rather
        // than that a filter is on.
        harmony.Patch(
            targets.HasRoomSearchOrFilter,
            postfix: new HarmonyMethod(typeof(RoomVersionFilterFix), nameof(HasRoomSearchOrFilterPostfix)));

        // The summary line the player reads has to name this filter like any other.
        harmony.Patch(
            targets.DescribeRoomFilterState,
            postfix: new HarmonyMethod(typeof(RoomVersionFilterFix), nameof(DescribeRoomFilterStatePostfix)));

        // 「清空」 clears this filter together with the upstream ones. Prefix, not postfix: upstream's own reset runs
        // either way, and clearing ours first means the stage rebuild that its room-search reset triggers already
        // leaves every room visible.
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

    /// <summary>
    /// Appends 「版本」 + the chip row to the dialog body upstream just built.
    ///
    /// The body is located as the first <see cref="VBoxContainer"/> in the shell rather than by walking shell →
    /// CenterContainer → MarginContainer → PanelContainer → body by hand, so a theme or layout change that inserts a
    /// wrapper does not break this. Missing parts are reported once and skipped, leaving upstream's dialog as it was.
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

            // Upstream's own factory, so the label carries exactly the style of 「房间类型」/「游戏模式」.
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

            // Upstream's own chip painter: same styleboxes, font sizes and disabled look.
            shape.ApplyChipStyle.Invoke(null, new object[] { chip, _sameVersionOnly });
            chip.Connect(BaseButton.SignalName.Pressed, Callable.From(() => OnChipPressed(__instance, chip)));

            row.AddChild(chip);
            body.AddChild(sectionLabel);
            body.AddChild(row);

            _chip = chip;

            Entry.Info(
                "room-version-filter: 「" + SectionText + "」 section added to the filter dialog (rows=" +
                body.GetChildCount() + ", localVersion='" + localVersion + "', chipUsable=" + usable +
                ", sameVersionOnly=" + _sameVersionOnly + ").");
        }
        catch (Exception exception)
        {
            // A failure here costs the section, not the dialog: upstream's dialog stays as it is, and one line says why.
            WarnOnce("room-version-filter: could not add the 「" + SectionText + "」 section: " + Describe(exception));
        }
    }

    /// <summary>
    /// Repaints the chip after upstream synced the other six, from every chip toggle and from the places that open the
    /// dialog or relight the 「筛选」 button. Upstream cannot know our chip, so without this it keeps whatever style it
    /// was given the last time.
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

            // ApplyFilterChipStyle also defines the disabled stylebox, so a disabled chip is painted here like any
            // other; it only never receives the press.
            shape.ApplyChipStyle.Invoke(null, new object[] { chip, _sameVersionOnly });
        }
        catch (Exception exception)
        {
            WarnOnce("room-version-filter: could not repaint the chip: " + Describe(exception));
        }
    }

    /// <summary>
    /// What upstream's own chips do on press: flip the flag, sync the chips, then let <c>ApplyRoomFilterState</c> reset
    /// the page, the scroll offset and the room stage. Replaying those two calls instead of re-implementing them is
    /// what keeps pagination and the 「筛选」 accent correct.
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

    /// <summary>
    /// Drops every room whose advertised game version differs from this machine's.
    ///
    /// The entries are removed from the list upstream returned, in place, rather than replaced: each call site either
    /// takes that list straight into <c>List&lt;LobbyRoomSummary&gt;</c> or reads its <c>Count</c>, so they all see the
    /// same object. Declared as <c>ref object</c> because <c>LobbyRoomSummary</c> is a host type that is deliberately
    /// not referenced at compile time.
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

            int before = rooms.Count;
            for (int index = rooms.Count - 1; index >= 0; index--)
            {
                if (!IsSameVersion(shape, rooms[index], localVersion))
                {
                    rooms.RemoveAt(index);
                }
            }

            LogNarrowed(before, rooms.Count, localVersion);
        }
        catch (Exception exception)
        {
            // This runs inside the room-list rebuild, so it does not rethrow: leaving the list untouched shows more
            // rooms than asked for, which is a far smaller problem than an exception in the lobby stage.
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
            // Upstream returns 「全部」 when nothing is selected. Keeping that word and appending would read
            // 「全部 · 同版本」, which contradicts itself.
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
    /// 「清空」 resets upstream's six flags; ours is cleared first, so the stage rebuild that upstream's own room-search
    /// reset triggers already shows every room again.
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

    private static string _narrowedLog = string.Empty;

    /// <summary>
    /// One line per distinct outcome, so a "I ticked it and the list is still full" report can be told apart from a
    /// filter that never ran. The room list refreshes on a timer, and the same outcome on every tick is not news.
    /// </summary>
    private static void LogNarrowed(int before, int after, string localVersion)
    {
        string line = "room-version-filter: version filter " + before + " -> " + after + " rooms (local " +
                      localVersion + ")";
        if (string.Equals(_narrowedLog, line, StringComparison.Ordinal))
        {
            return;
        }

        _narrowedLog = line;
        Entry.Info(line);
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
    /// This machine's game version through upstream's own resolver, which reads <c>ReleaseInfoManager</c>, then the git
    /// commit, then the game assembly version.
    ///
    /// Cached only once it produced something usable: the resolver answers <c>UNKNOWN</c> while the release info is
    /// still loading, and caching that would keep the chip disabled for the rest of the session.
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
    /// Trim, then drop one leading <c>v</c>/<c>V</c>. Upstream normalises the same way before it compares, so a room
    /// only reaches this comparison when its shape already matches; comparing case-insensitively here is the
    /// deliberate difference, because <c>V0.113.0</c> and <c>v0.113.0</c> are the same build. Returns
    /// <see cref="string.Empty"/> for anything unusable (empty, whitespace-only, <see cref="UnknownGameVersion"/>).
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

    /// <summary>
    /// The dialog body: first <see cref="VBoxContainer"/> in child order. Nothing else on the way to the body is one,
    /// and the pixel-border panel adds no child containers of its own, only styleboxes.
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

    /// <summary>Methods this repair patches, install-time only</summary>
    internal readonly record struct PatchTargets(
        MethodInfo BuildFilterDialog,
        MethodInfo SyncFilterDialogButtons,
        MethodInfo GetFilteredRooms,
        MethodInfo HasRoomSearchOrFilter,
        MethodInfo DescribeRoomFilterState,
        MethodInfo ClearRoomFiltersAndSearch);

    /// <summary>
    /// Every reflection anchor the runtime path uses, filled in by <see cref="Shape.TryCapture"/> once all of them
    /// resolved.
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

        internal MethodInfo CreateBodyLabel { get; }

        internal MethodInfo ApplyChipStyle { get; }

        internal MethodInfo SyncButtons { get; }

        internal MethodInfo ApplyFilterState { get; }

        internal PropertyInfo RoomVersion { get; }

        internal MethodInfo GetGameVersion { get; }

        /// <summary>
        /// Resolves everything by name and reports every miss at once. Metadata only: no <c>Invoke</c>, no
        /// <c>Activator</c>, no Godot node, no <c>typeof</c> on a host type, so it also runs in the headless harness
        /// where no Godot runtime sits behind the loaded assembly.
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

            // The postfix removes entries from the list in place, so the result has to stay the exact
            // List<LobbyRoomSummary> upstream returns.
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
