using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;

namespace Sts2LobbyEnhance;

/// <summary>
/// Desktop only: while the in-run chat is collapsed, its hint strip stops swallowing the mouse clicks that were
/// meant for the game's own top-right buttons.
///
/// The hint label is not the culprit - upstream already ships it as <c>MouseFilter = Ignore</c>. What takes the click
/// is the chain of containers it lives in: the column holding its row is <c>Stop</c>, and the row plus the root
/// <c>MarginContainer</c> are <c>Pass</c>, because Godot gives every <c>Container</c> <c>MOUSE_FILTER_PASS</c> by
/// default. <c>Pass</c> is not a hole: picking returns the topmost control at that point whose filter is not
/// <c>Ignore</c>, and the input walk then stops at the first <c>Stop</c> ancestor. The click dies inside the overlay
/// and the button behind it is never offered the event.
///
/// So all three go to <c>Ignore</c> while collapsed - dropping only the <c>Stop</c> would merely move the pick up to
/// the row - and back to exactly the values upstream chose when the panel is open. Picking recurses into children
/// before deciding about a node itself, so the toggle bubble keeps its drags under <c>Ignore</c> parents.
///
/// Collapsed/expanded is read from <c>_panelFrame.Visible</c>, the flag upstream writes from
/// <c>chat.RoomOverlayOpen</c>. The overlay itself is a <c>CanvasLayer</c>, so the clickable chain starts at
/// <c>_root</c>, its first <c>Control</c>.
///
/// Mobile keeps upstream's filters, and that is a product decision rather than a technical one: the obstruction was
/// reported on desktop, where the collapsed chat has no bubble to grab (upstream shows it only in touch pointer mode)
/// and the strip covers the game's buttons with nothing interactive of its own. On a phone the collapsed chat is the
/// bubble, and the player can drag the bubble out of the way.
/// </summary>
internal sealed class RoomChatHintPassthroughFix : ICompatFix
{
    private const string OverlayTypeName = "Sts2LanConnect.Scripts.LanConnectRoomChatOverlay";

    private const string RefreshMethodName = "RefreshFromSource";

    private static Shape? _shape;

    private static int _warned;

    /// <summary>-1 unknown, 0 desktop, 1 mobile.</summary>
    private static int _mobile = -1;

    private static readonly ConditionalWeakTable<object, FilterState> States = new();

    public string Id => "room-chat-hint-passthrough";

    /// <summary>
    /// Grouped with the chat UI: a shape change in the room overlay costs the hint's click-through only.
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
            "room-chat-hint-passthrough: Applies() did not capture the overlay shape; refusing to patch blind.");

        // RefreshFromSource runs every frame and its own body sets _panelFrame.Visible first, so a postfix sees
        // this frame's collapsed state and not the previous one's.
        harmony.Patch(
            shape.Refresh,
            postfix: new HarmonyMethod(typeof(RoomChatHintPassthroughFix), nameof(RefreshPostfix)));

        Entry.Info(
            "room-chat-hint-passthrough: while the in-run chat is collapsed its hint strip is click-through " +
            "(root/column/row = Ignore); opening the panel restores upstream's own filters. Mobile untouched.");
    }

    // ReSharper disable once UnusedParameter.Local (Harmony binds by name).
    private static void RefreshPostfix(object? __instance)
    {
        Shape? shape = _shape;
        if (shape == null || __instance is not Node overlay)
        {
            return;
        }

        if (MobilePlatform() || !GodotObject.IsInstanceValid(overlay) || !overlay.IsInsideTree())
        {
            return;
        }

        try
        {
            Sync(shape, overlay);
        }
        catch (Exception exception)
        {
            // Cosmetic: leaving the hint as upstream ships it is never worse than a broken overlay.
            WarnOnce("could not free the collapsed chat hint: " +
                     exception.GetType().Name + ": " + exception.Message);
        }
    }

    private static void Sync(Shape shape, Node overlay)
    {
        if (shape.PanelFrame.GetValue(overlay) is not Control frame || !GodotObject.IsInstanceValid(frame))
        {
            return;
        }

        FilterState state = States.GetValue(overlay, _ => new FilterState());
        bool collapsed = !frame.Visible;

        // The collapsed flag is the only input, and upstream never writes these three filters: re-applying the same
        // values on every frame of the overlay's _Process is work with nothing to show for it.
        if (state.Applied == collapsed)
        {
            return;
        }

        if (shape.Root.GetValue(overlay) is not Control root || !GodotObject.IsInstanceValid(root))
        {
            return;
        }

        if (shape.Hint.GetValue(overlay) is not Control hint || !GodotObject.IsInstanceValid(hint))
        {
            return;
        }

        // hint -> topRow -> stack -> _root. If upstream ever reshapes this chain, the guard below leaves its
        // behaviour completely alone instead of half-patching it.
        if (hint.GetParent() is not Control row || row.GetParent() is not Control column ||
            !ReferenceEquals(column.GetParent(), root))
        {
            return;
        }

        if (!state.Captured)
        {
            // Upstream does not write these three, so the first frame we see carries the values it chose.
            state.Root = root.MouseFilter;
            state.Column = column.MouseFilter;
            state.Row = row.MouseFilter;
            state.Captured = true;
        }

        root.MouseFilter = collapsed ? Control.MouseFilterEnum.Ignore : state.Root;
        column.MouseFilter = collapsed ? Control.MouseFilterEnum.Ignore : state.Column;
        row.MouseFilter = collapsed ? Control.MouseFilterEnum.Ignore : state.Row;
        state.Applied = collapsed;

        if (collapsed && !state.Logged)
        {
            state.Logged = true;
            Entry.Info(
                "room-chat-hint-passthrough: collapsed - the hint strip no longer blocks clicks " +
                "(root/column/row = Ignore, restored to " + state.Root + "/" + state.Column + "/" + state.Row +
                " when the panel is open).");
        }
    }

    /// <summary>
    /// The only platform read here, cached because it crosses into the engine. Unknown resolves to mobile: then the
    /// fix simply never runs and upstream behaviour stands unchanged.
    /// </summary>
    private static bool MobilePlatform()
    {
        int cached = Volatile.Read(ref _mobile);
        if (cached >= 0)
        {
            return cached == 1;
        }

        int resolved;
        try
        {
            resolved = OS.HasFeature("mobile") ? 1 : 0;
        }
        catch
        {
            resolved = 1;
        }

        Volatile.Write(ref _mobile, resolved);
        return resolved == 1;
    }

    private static void WarnOnce(string message)
    {
        if (Interlocked.Exchange(ref _warned, 1) != 0)
        {
            return;
        }

        Entry.Warn("room-chat-hint-passthrough: " + message);
    }

    /// <summary>Per-overlay bookkeeping: the captured filters and the one log line.</summary>
    private sealed class FilterState
    {
        internal bool Captured;

        internal bool Logged;

        /// <summary>The last collapsed state that was written into the filters; null until the first write.</summary>
        internal bool? Applied;

        /// <summary>Upstream's filters for the expanded state, restored verbatim.</summary>
        internal Control.MouseFilterEnum Root;

        internal Control.MouseFilterEnum Column;

        internal Control.MouseFilterEnum Row;
    }

    /// <summary>
    /// Every member this repair touches, resolved once in <see cref="Applies"/> and never again.
    /// </summary>
    internal sealed class Shape
    {
        internal MethodInfo Refresh = null!;

        internal FieldInfo Root = null!;

        internal FieldInfo Hint = null!;

        internal FieldInfo PanelFrame = null!;

        internal static Shape? TryCapture(Assembly host, List<string> problems)
        {
            Type? overlay = ReflectScope.HostType(host, OverlayTypeName, problems);
            if (overlay == null)
            {
                return null;
            }

            var shape = new Shape
            {
                Refresh = ReflectScope.Exact(
                    overlay,
                    RefreshMethodName,
                    requireStatic: false,
                    Array.Empty<string>(),
                    "System.Void",
                    problems)!,
                Root = RequireField(problems, overlay, "_root", "MarginContainer")!,
                Hint = RequireField(problems, overlay, "_fadeHint", "Label")!,
                PanelFrame = RequireField(problems, overlay, "_panelFrame", "PanelContainer")!
            };

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
