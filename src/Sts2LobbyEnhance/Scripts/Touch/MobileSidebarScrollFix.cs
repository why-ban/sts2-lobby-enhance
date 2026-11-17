using System.Reflection;
using Godot;
using HarmonyLib;

namespace Sts2LobbyEnhance;

/// <summary>
/// Phone only: lets the lobby's right-hand column scroll as one surface, so the channel chat composer at the bottom of it
/// can be reached. The column itself is never moved and never re-parented - its anchors, offsets and size flags stay
/// upstream's - only its contents go into a scroller of ours that fills the column.
///
/// The scroller goes <em>inside</em> the column, not around it, and that is the whole point of this shape. A
/// <c>ScrollContainer</c> dropped into a plain <c>Control</c> parent is sized by anchors alone, and the column's own
/// anchors then resolve against the scroller instead of against the lobby: an earlier attempt wrapped the column this way
/// and the wrapper measured 0x0, which put the column at its 200 px minimum over the room list and cut its right edge.
/// A <c>VBoxContainer</c> parent lays the scroller out itself, so the column keeps its exact slot by construction.
///
/// The scrollbar is hidden (<c>ShowNever</c>), so nothing is reserved and the column looks and measures as before; the
/// drag comes from <see cref="MobileSidebarScrollWatcher"/>, which reads the input stream instead of the GUI because the
/// controls inside the column stop the pointer before the scroller ever sees it.
/// </summary>
internal sealed class MobileSidebarScrollFix : ICompatFix
{
    private const string OverlayTypeName = "Sts2LanConnect.Scripts.LanConnectLobbyOverlay";

    private const string RebuildMethodName = "RebuildMainContentLayout";

    private const string SidebarFieldName = "_sidebarContainer";

    private const string ScrollerName = "LobbyEnhanceSidebarScroll";

    private const string ContentName = "LobbyEnhanceSidebarScrollContent";

    private const string WatcherName = "LobbyEnhanceSidebarScrollWatcher";

    /// <summary>Upstream's own separation inside the column; only used when the column carries no override</summary>
    private const int DefaultSeparation = 12;

    private const string Tag = "mobile-sidebar-scroll: ";

    private static Shape? _shape;

    private static int _warned;

    private static int _reported;

    public string Id => "mobile-sidebar-scroll";

    public FixGroup Group => FixGroup.Layout;

    public bool Applies(out string reason)
    {
        Assembly? host = Reflect.FindAssembly(Entry.HostModAssemblyName);
        if (host == null)
        {
            reason = $"host mod '{Entry.HostModAssemblyName}' is not loaded";
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
            $"{Tag}on a phone the lobby's right-hand column scrolls as one surface, so the channel chat at the bottom of " +
            "it can be reached, without moving the column or resizing it.");
    }

    /// <summary>
    /// Runs after every rebuild of the main content. A rebuild re-parents the column into a fresh wrapper but leaves its
    /// children alone, so the scroller travels with the column; both steps below are idempotent and cheap on every call.
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

            if (!TouchChatScrollFix.Platform.IsMobile)
            {
                return;
            }

            if (shape.Sidebar.GetValue(__instance) is not Control column || !GodotObject.IsInstanceValid(column))
            {
                return;
            }

            ScrollContainer? scroll = EnsureScroller(column);
            if (scroll == null || !GodotObject.IsInstanceValid(scroll))
            {
                return;
            }

            EnsureWatcher(overlay).Follow(column, scroll);

            if (Interlocked.Exchange(ref _reported, 1) == 0)
            {
                Entry.Info(
                    $"{Tag}column={column.GetGlobalRect()}, scroller={scroll.GetGlobalRect()}, " +
                    $"scroll range=0..{Range(scroll):0}, page={scroll.GetVScrollBar().Page:0}.");
            }
        }
        catch (Exception exception)
        {
            WarnOnce("the right-hand column could not be made scrollable: " +
                     exception.GetType().Name + ": " + exception.Message);
        }
    }

    /// <summary>
    /// Returns the scroller the column's contents live in, creating it inside the column the first time. Upstream's
    /// compact layout already put the column inside one, and nesting two would fight over the same finger, so that one is
    /// used as it is.
    /// </summary>
    private static ScrollContainer? EnsureScroller(Control column)
    {
        if (column.GetParent() is ScrollContainer outer && GodotObject.IsInstanceValid(outer))
        {
            return outer;
        }

        if (column.FindChild(ScrollerName, recursive: false, owned: false) is ScrollContainer existing
            && GodotObject.IsInstanceValid(existing) && ReferenceEquals(existing.GetParent(), column))
        {
            MoveContents(column, existing);
            return existing;
        }

        var content = new VBoxContainer
        {
            Name = ContentName,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        content.AddThemeConstantOverride("separation", Separation(column));

        var created = new ScrollContainer
        {
            Name = ScrollerName,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.ShowNever,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            FollowFocus = false
        };
        created.AddChild(content);
        column.AddChild(created);
        MoveContents(column, created);
        return created;
    }

    /// <summary>
    /// Moves the column's own children into the scroller's content box, keeping their order. The content box keeps the
    /// column's separation and lets its children expand exactly as they did inside the column, so with the same amount of
    /// space the panels get the same sizes they had before.
    /// </summary>
    private static void MoveContents(Control column, ScrollContainer scroller)
    {
        if (scroller.GetChildCount() == 0 || scroller.GetChild(0) is not VBoxContainer content)
        {
            return;
        }

        var moving = new List<Node>();
        foreach (Node child in column.GetChildren())
        {
            if (!ReferenceEquals(child, scroller))
            {
                moving.Add(child);
            }
        }

        foreach (Node child in moving)
        {
            column.RemoveChild(child);
            content.AddChild(child);
        }
    }

    private static int Separation(Control column)
    {
        int separation = column.GetThemeConstant("separation");
        return separation >= 0 ? separation : DefaultSeparation;
    }

    private static MobileSidebarScrollWatcher EnsureWatcher(Node overlay)
    {
        if (overlay.FindChild(WatcherName, recursive: false, owned: false) is MobileSidebarScrollWatcher existing
            && GodotObject.IsInstanceValid(existing))
        {
            return existing;
        }

        var created = new MobileSidebarScrollWatcher { Name = WatcherName };
        overlay.AddChild(created);
        return created;
    }

    private static float Range(ScrollContainer scroll)
    {
        ScrollBar bar = scroll.GetVScrollBar();
        return Mathf.Max((float)bar.MaxValue - (float)bar.Page, 0f);
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
                problems.Add($"{OverlayTypeName}.{SidebarFieldName} is {sidebar.FieldType.FullName}, expected a Control");
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
