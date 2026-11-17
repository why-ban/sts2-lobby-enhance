using System.Reflection;
using Godot;
using HarmonyLib;

namespace Sts2LobbyEnhance;

/// <summary>
/// Turns the lobby's right-hand column into one draggable surface.
///
/// On a phone that column is taller than the window and the chat composer sits below the bottom edge. Upstream wraps the
/// column in a <c>ScrollContainer</c> in its compact layout, but the press that would start that scroll is taken by the
/// controls the column is made of, so a vertical swipe over the cards moves nothing and the composer stays unreachable.
///
/// Driving the scroll from <c>GuiInput</c> on those controls is not enough: only the control the press was delivered to
/// sees the gesture, and a container passes the press up while a button swallows it. This repair therefore reads the
/// input stream itself (<see cref="Node._Input"/>, which runs before the GUI and cannot be consumed) and moves the column
/// when a press that began inside its rectangle turns into a vertical drag. Presses that begin on a control which needs
/// the gesture for itself - a button, the message list, the composer - are left alone, so no click is invented and the
/// chat log still scrolls as its own list.
/// </summary>
internal sealed class SidebarScrollFix : ICompatFix
{
    private const string OverlayTypeName = "Sts2LanConnect.Scripts.LanConnectLobbyOverlay";

    private const string RebuildMethodName = "RebuildMainContentLayout";

    private const string ReadyMethodName = "_Ready";

    private const string SidebarFieldName = "_sidebarContainer";

    private const string ObserverName = "LobbyEnhanceSidebarScrollWatcher";

    private const string Tag = "sidebar-scroll: ";

    private static Shape? _shape;

    private static int _warned;

    private static int _reported;

    public string Id => "sidebar-scroll";

    public FixGroup Group => FixGroup.Layout;

    public bool Applies(out string reason)
    {
        Assembly? host = Reflect.FindAssembly(Entry.HostModAssemblyName);
        if (host == null)
        {
            reason = "host mod '" + Entry.HostModAssemblyName + "' is not loaded";
            return false;
        }

        try
        {
            if (!Shape.TryCapture(host, out Shape shape, out string missing))
            {
                reason = "missing: " + missing;
                return false;
            }

            _shape = shape;
            reason = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            reason = $"missing: anchor capture threw {exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    public void Apply(Harmony harmony)
    {
        Shape shape = _shape
            ?? throw new InvalidOperationException(
                "sidebar-scroll: Apply ran without a captured shape; Applies must be consulted first.");

        // Two seams into the same setup: the rebuild runs whenever the layout changes, _Ready runs once when the
        // overlay opens. Either one alone leaves a window in which the column has no watcher.
        var postfix = new HarmonyMethod(typeof(SidebarScrollFix), nameof(RebuildPostfix))
        {
            priority = Priority.Last
        };

        harmony.Patch(shape.Rebuild, postfix: postfix);
        if (shape.Ready != null)
        {
            harmony.Patch(shape.Ready, postfix: postfix);
        }

        Entry.Info(
            $"{Tag}the lobby's right-hand column is draggable, so the channel chat at the bottom of it can be reached " +
            "on a phone.");
    }

    /// <summary>
    /// Runs after every rebuild of the main content: a rebuild re-parents the column into a fresh wrapper and drops
    /// whatever scroller used to hold it, so the scroller is re-established and the watcher is pointed at the new
    /// column. Both steps are idempotent.
    /// </summary>
    // ReSharper disable once InconsistentNaming
    public static void RebuildPostfix(object? __instance)
    {
        try
        {
            Shape? shape = _shape;
            if (shape == null || __instance is not Node overlay || !GodotObject.IsInstanceValid(overlay))
            {
                return;
            }

            if (shape.Sidebar.GetValue(__instance) is not Control sidebar || !GodotObject.IsInstanceValid(sidebar))
            {
                return;
            }

            ScrollContainer? scroll = sidebar.GetParent() as ScrollContainer;
            if (scroll == null)
            {
                scroll = Wrap(sidebar);
            }

            if (scroll == null)
            {
                return;
            }

            SidebarDragWatcher watcher = Watcher(overlay);
            watcher.Follow(sidebar, scroll);

            if (Interlocked.Exchange(ref _reported, 1) == 0)
            {
                Entry.Info(
                    $"{Tag}column rect={sidebar.GetGlobalRect()}, scroller={scroll.GetGlobalRect()}, " +
                    $"scroll={scroll.ScrollVertical:0}/{Mathf.Max((float)scroll.GetVScrollBar().MaxValue - (float)scroll.GetVScrollBar().Page, 0f):0}.");
            }
        }
        catch (Exception exception)
        {
            WarnOnce("the right-hand column could not be made draggable: " +
                     exception.GetType().Name + ": " + exception.Message);
        }
    }

    /// <summary>
    /// Puts the column inside a scroller that occupies the column's slot. Upstream only does this for its compact layout,
    /// which leaves a window wide enough for the non-compact layout with a column that simply overflows.
    /// </summary>
    private static ScrollContainer? Wrap(Control sidebar)
    {
        Node? parent = sidebar.GetParent();
        if (parent == null)
        {
            return null;
        }

        int index = sidebar.GetIndex();
        float left = sidebar.AnchorLeft;
        float right = sidebar.AnchorRight;
        float top = sidebar.AnchorTop;
        float bottom = sidebar.AnchorBottom;
        float offsetLeft = sidebar.OffsetLeft;
        float offsetRight = sidebar.OffsetRight;
        float offsetTop = sidebar.OffsetTop;
        float offsetBottom = sidebar.OffsetBottom;

        parent.RemoveChild(sidebar);

        var scroll = new ScrollContainer
        {
            Name = "LobbyEnhanceSidebarScroll",
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            FollowFocus = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };

        scroll.AnchorLeft = left;
        scroll.AnchorRight = right;
        scroll.AnchorTop = top;
        scroll.AnchorBottom = bottom;
        scroll.OffsetLeft = offsetLeft;
        scroll.OffsetRight = offsetRight;
        scroll.OffsetTop = offsetTop;
        scroll.OffsetBottom = offsetBottom;

        parent.AddChild(scroll);
        parent.MoveChild(scroll, index);
        scroll.AddChild(sidebar);
        return scroll;
    }

    private static SidebarDragWatcher Watcher(Node overlay)
    {
        if (overlay.FindChild(ObserverName, recursive: false, owned: false) is SidebarDragWatcher existing
            && GodotObject.IsInstanceValid(existing))
        {
            return existing;
        }

        var created = new SidebarDragWatcher { Name = ObserverName };
        overlay.AddChild(created);
        return created;
    }

    private static void WarnOnce(string message) => Entry.WarnOnce(ref _warned, "sidebar-scroll", message);

    /// <summary>Every anchor this repair needs, resolved once in <see cref="Applies"/>.</summary>
    internal sealed class Shape
    {
        internal MethodInfo? Ready;

        internal MethodInfo Rebuild = null!;

        internal FieldInfo Sidebar = null!;

        internal static bool TryCapture(Assembly host, out Shape shape, out string missing)
        {
            shape = new Shape();
            var problems = new List<string>();

            Type? overlay = ReflectScope.HostType(host, OverlayTypeName, problems);
            if (overlay == null)
            {
                missing = string.Join(", ", problems);
                return false;
            }

            // Optional, and only when the overlay declares its own _Ready: the inherited Control._Ready would patch
            // every control in the game, so an inherited method is treated as "no seam" rather than as a target.
            var readyProblems = new List<string>();
            MethodInfo? ready = ReflectScope.Exact(
                overlay,
                ReadyMethodName,
                requireStatic: false,
                parameterTypeNames: Array.Empty<string>(),
                returnTypeName: "System.Void",
                readyProblems);
            if (ready != null && ready.DeclaringType != overlay)
            {
                ready = null;
            }

            MethodInfo? rebuild = ReflectScope.Exact(
                overlay,
                RebuildMethodName,
                requireStatic: false,
                parameterTypeNames: Array.Empty<string>(),
                returnTypeName: "System.Void",
                problems);

            FieldInfo? sidebar = ReflectScope.Field(problems, overlay, SidebarFieldName);
            if (sidebar != null && !typeof(Control).IsAssignableFrom(sidebar.FieldType))
            {
                problems.Add($"{OverlayTypeName}.{SidebarFieldName} is {sidebar.FieldType.FullName}, expected a Control");
                sidebar = null;
            }

            if (rebuild == null || sidebar == null || problems.Count > 0)
            {
                shape = new Shape();
                missing = string.Join(", ", problems);
                return false;
            }

            shape.Ready = ready;
            shape.Rebuild = rebuild;
            shape.Sidebar = sidebar;
            missing = string.Empty;
            return true;
        }
    }
}
