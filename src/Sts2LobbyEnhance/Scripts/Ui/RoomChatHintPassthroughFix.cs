using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;

namespace Sts2LobbyEnhance;

    /// <summary>
    /// Keeps the collapsed in-run chat hint from eating clicks meant for the game's own top-right buttons, and
    /// keeps it from sitting on top of them.
    ///
    /// Two separate problems, two separate fixes:
    ///
    /// 1. **Clicks.** The hint itself is `MouseFilter = Ignore` (<c>LanConnectRoomChatOverlay.cs:498</c>), but its
    ///    parents are not: `topRow` (<c>:489</c>) and `stack` (<c>:484-485</c>, explicitly `Stop`) cover the
    ///    same rectangle and swallow the click. When the panel is collapsed those two - and the
    ///    `MarginContainer` root - are switched to `Ignore`, so the click reaches whatever is behind them.
    ///    The toggle bubble keeps working: Godot hit-tests children before the node itself, so a parent set to
    ///    `Ignore` does not hide its children. Evidence is the engine's own picking routine,
    ///    `Viewport::_gui_find_control_at_pos` (<c>work/lead/_tmp_godot/viewport.cpp:1835-1874</c>): it recurses into
    ///    every visible child first (<c>:1848-1860</c>) and only then decides about the node itself, where
    ///    `MOUSE_FILTER_IGNORE` makes it return <c>nullptr</c> for *that node only* (<c>:1862-1864</c>).
    ///
    /// 2. **Overlap.** The hint is a sibling of the bubble inside a right-aligned row, so it covers the strip to
    ///    the left of the bubble - which is where the game's top bar sits. The hint is moved out of that row into
    ///    the column below it, right-aligned, so the collapsed overlay occupies the same columns as the bubble
    ///    and the row above it stays free.
    ///
    /// Collapsed/expanded is read from <c>_panelFrame.Visible</c> - the flag upstream itself writes from
    /// <c>chat.RoomOverlayOpen</c> (<c>:650</c>) - never guessed.
    /// </summary>
internal sealed class RoomChatHintPassthroughFix : ICompatFix
{
    private const string OverlayTypeName = "Sts2LanConnect.Scripts.LanConnectRoomChatOverlay";

    private const string RefreshMethodName = "RefreshFromSource";

    private static Shape? _shape;

    private static int _warned;

    private static readonly ConditionalWeakTable<Control, RowState> States = new();

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

        // RefreshFromSource runs every frame (its own comment says so, :608-611), so it is the natural place to
        // keep a two-state property in sync without polling anything ourselves.
        harmony.Patch(
            shape.Refresh,
            postfix: new HarmonyMethod(typeof(RoomChatHintPassthroughFix), nameof(RefreshPostfix))
            {
                priority = Priority.Last
            });

        Entry.Info(
            "room-chat-hint-passthrough: while the in-run chat is collapsed, the hint row and its containers " +
            "no longer take mouse clicks and the hint sits below the bubble instead of over the top bar.");
    }

    // ReSharper disable once UnusedParameter.Local — Harmony binds by name.
    private static void RefreshPostfix(object __instance)
    {
        Shape? shape = _shape;
        if (shape == null || __instance is not Control overlay)
        {
            return;
        }

        if (!GodotObject.IsInstanceValid(overlay) || !overlay.IsInsideTree())
        {
            return;
        }

        try
        {
            Sync(shape, overlay);
        }
        catch (Exception exception)
        {
            // Cosmetic: the hint keeping its original behaviour is never worse than a broken overlay.
            WarnOnce("could not adjust the collapsed chat hint: " +
                     exception.GetType().Name + ": " + exception.Message);
        }
    }

    private static void Sync(Shape shape, Control overlay)
    {
        if (shape.Hint.GetValue(overlay) is not Label hint || !GodotObject.IsInstanceValid(hint))
        {
            return;
        }

        if (shape.PanelFrame.GetValue(overlay) is not Control frame || !GodotObject.IsInstanceValid(frame))
        {
            return;
        }

        RowState? state = States.GetValue(overlay, _ => new RowState());

        // Answer the "is it collapsed" question with the flag upstream itself maintains (:650 writes
        // _panelFrame.Visible from chat.RoomOverlayOpen), not with a guess.
        bool collapsed = !frame.Visible;

        EnsureBelowBubble(hint, state);

        if (collapsed && !state.FirstCollapseLogged)
        {
            state.FirstCollapseLogged = true;
            Entry.Info(
                "room-chat-hint-passthrough: collapsed hint is click-through (root/stack/row = Ignore) and " +
                "parked below the bubble; the expanded state restores upstream's own filters.");
        }

        state.Collapsed = collapsed;

        // The three containers between the click and the game: the overlay root, the column and the bubble's row.
        if (hint.GetParent() is not Control column || column.GetParent() is not Control stack)
        {
            return;
        }

        if (!state.Captured)
        {
            // Remember what upstream chose for the expanded state, so restoring it is exact rather than "Stop".
            state.RootFilter = overlay.MouseFilter;
            state.ColumnFilter = column.MouseFilter;
            state.RowFilter = stack.MouseFilter;
            state.Captured = true;
        }

        overlay.MouseFilter = collapsed ? Control.MouseFilterEnum.Ignore : state.RootFilter;
        column.MouseFilter = collapsed ? Control.MouseFilterEnum.Ignore : state.ColumnFilter;
        stack.MouseFilter = collapsed ? Control.MouseFilterEnum.Ignore : state.RowFilter;
    }

    /// <summary>
    /// Moves the hint out of the bubble's row and into the column below it, once per overlay, right-aligned. After
    /// this the collapsed overlay occupies exactly the bubble's columns, so the hint cannot sit on the top bar.
    /// </summary>
    private static void EnsureBelowBubble(Label hint, RowState state)
    {
        if (state.Moved || hint.GetParent() is not HBoxContainer row)
        {
            return;
        }

        if (row.GetParent() is not VBoxContainer stack)
        {
            return;
        }

        row.RemoveChild(hint);
        stack.AddChild(hint);
        stack.MoveChild(hint, row.GetIndex() + 1);

        hint.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        hint.HorizontalAlignment = HorizontalAlignment.Right;
        state.Moved = true;
    }

    private static void WarnOnce(string message)
    {
        if (Interlocked.Exchange(ref _warned, 1) != 0)
        {
            return;
        }

        Entry.Warn("room-chat-hint-passthrough: " + message);
    }

    /// <summary>Per-overlay bookkeeping: the structural move happens once, the log line once.</summary>
    private sealed class RowState
    {
        internal bool Moved;

        internal bool Collapsed;

        internal bool FirstCollapseLogged;

        internal bool Captured;

        /// <summary>Upstream's filters for the expanded state, restored verbatim.</summary>
        internal Control.MouseFilterEnum RootFilter;

        internal Control.MouseFilterEnum ColumnFilter;

        internal Control.MouseFilterEnum RowFilter;
    }

    /// <summary>
    /// Every member this repair touches, resolved once in <see cref="Applies"/> and never again.
    /// </summary>
    internal sealed class Shape
    {
        internal MethodInfo Refresh = null!;

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
