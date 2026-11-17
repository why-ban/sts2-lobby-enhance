using System.Reflection;
using Godot;
using HarmonyLib;

namespace Sts2LobbyEnhance;

/// <summary>
/// Phone only: lets the lobby's right-hand column scroll as one surface.
///
/// In the compact layout the sidebar already sits inside a <c>ScrollContainer</c>, but its children stop the pointer:
/// the room-details card, the action card and the chat frame are containers that either swallow the press or hand it to
/// a nested scroller, so a finger drag over that column never reaches the sidebar's own scroll. On a phone the column
/// is taller than the viewport, which puts the chat composer below the bottom edge with no way to reach it - the
/// symptom is "the channel chat cannot be used on a phone".
///
/// The repair gives the sidebar and each of its direct children their own drag handler, pointed at whichever
/// <c>ScrollContainer</c> encloses them at press time, which is the same surface the dialog's server rows use. A drag
/// moves the column; the nested message list keeps winning inside its own rect, so scrolling the chat log still
/// scrolls the log.
///
/// Desktop is untouched: the mouse wheel already moves that column, and widening the drag surface there would fight
/// text selection.
/// </summary>
internal sealed class MobileSidebarScrollFix : ICompatFix
{
    private const string OverlayTypeName = "Sts2LanConnect.Scripts.LanConnectLobbyOverlay";

    private const string RebuildMethodName = "RebuildMainContentLayout";

    private const string SidebarFieldName = "_sidebarContainer";

    private const string Tag = "mobile-sidebar-scroll: ";

    private const string AttachedMeta = "sts2le_sidebar_drag";

    private const string WrapName = "LobbyEnhanceSidebarScroll";

    private static Shape? _shape;

    private static int _warned;

    public string Id => "mobile-sidebar-scroll";

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
                "mobile-sidebar-scroll: Apply ran without a captured shape; Applies must be consulted first.");

        harmony.Patch(
            shape.Rebuild,
            postfix: new HarmonyMethod(typeof(MobileSidebarScrollFix), nameof(RebuildPostfix))
            {
                priority = Priority.Last
            });

        Entry.Info(
            $"{Tag}on a phone the lobby's right-hand column scrolls as one surface, so the channel chat composer " +
            "below the fold can be reached.");
    }

    /// <summary>
    /// Runs after every rebuild of the main content, because a rebuild re-parents the sidebar into a fresh wrapper and
    /// drops whatever scroller used to hold it. Both steps below are idempotent: the wrapper is only added when the
    /// sidebar is not already inside a scroller, and each control is marked once.
    /// </summary>
    // ReSharper disable once InconsistentNaming
    public static void RebuildPostfix(object? __instance)
    {
        try
        {
            Shape? shape = _shape;
            if (shape == null || __instance == null || !TouchChatScrollFix.Platform.IsMobile)
            {
                return;
            }

            if (shape.Sidebar.GetValue(__instance) is not Control sidebar || !GodotObject.IsInstanceValid(sidebar))
            {
                return;
            }

            if (!sidebar.IsInsideTree())
            {
                return;
            }

            if (sidebar.GetParent() is not ScrollContainer)
            {
                Wrap(sidebar);
            }

            Attach(sidebar);

            foreach (Node child in sidebar.GetChildren())
            {
                if (child is Control control && GodotObject.IsInstanceValid(control))
                {
                    Attach(control);
                }
            }
        }
        catch (Exception exception)
        {
            WarnOnce("the right-hand column could not be made scrollable on this device: " +
                     exception.GetType().Name + ": " + exception.Message);
        }
    }

    /// <summary>
    /// Puts the sidebar inside a scroller that occupies the sidebar's slot. Only reached on a touch device whose window
    /// is large enough for the non-compact layout, where upstream leaves the column unscrolled.
    /// </summary>
    private static void Wrap(Control sidebar)
    {
        Node? parent = sidebar.GetParent();
        if (parent == null)
        {
            return;
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
            Name = WrapName,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            FollowFocus = true,
            MouseFilter = Control.MouseFilterEnum.Pass,
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

        Entry.Info(Tag + "the right-hand column had no scroller on this device, so one was added around it.");
    }

    /// <summary>
    /// One drag handler per control, pointed at whatever <c>ScrollContainer</c> encloses it when the finger lands: that
    /// is what makes the handler survive a rebuild that swaps the scroller underneath it.
    /// </summary>
    private static void Attach(Control control)
    {
        if (control.HasMeta(AttachedMeta))
        {
            return;
        }

        control.SetMeta(AttachedMeta, true);

        // A container passes the press up by default, and a drag only reaches this handler if the control owns it.
        control.MouseFilter = Control.MouseFilterEnum.Stop;
        TouchDragScroll? drag = TouchDragScroll.Attach(control);
        if (drag == null)
        {
            control.RemoveMeta(AttachedMeta);
        }
    }

    private static void WarnOnce(string message) => Entry.WarnOnce(ref _warned, "mobile-sidebar-scroll", message);

    /// <summary>Every anchor this repair needs, resolved once in <see cref="Applies"/>.</summary>
    internal sealed class Shape
    {
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
                problems.Add($"${OverlayTypeName}.${SidebarFieldName} is ${sidebar.FieldType.FullName}, expected a Control");
                sidebar = null;
            }

            if (rebuild == null || sidebar == null || problems.Count > 0)
            {
                shape = new Shape();
                missing = string.Join(", ", problems);
                return false;
            }

            shape.Rebuild = rebuild;
            shape.Sidebar = sidebar;
            missing = string.Empty;
            return true;
        }
    }
}
