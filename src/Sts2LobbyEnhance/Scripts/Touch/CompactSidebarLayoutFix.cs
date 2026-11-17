using System.Reflection;
using Godot;
using HarmonyLib;

namespace Sts2LobbyEnhance;

/// <summary>
/// Phone only: makes room for the channel chat by hiding the one block in the lobby's right-hand column that only displays
/// information - the ROOM DETAILS card (its title, its separator and the 房间 / 房主 / 人数 / 模式 / 版本 / 协议 / 状态
/// lines).
///
/// That card repeats what the room cards on the left already show, and it is what pushes the chat composer below the
/// bottom edge on a phone. With it hidden the column holds the ACTIONS card plus the chat frame, and the chat's own
/// minimum - title, the 140 px message floor and the composer - fits with slack to spare. Nothing is re-parented and no
/// anchor is touched: the column keeps upstream's slot, one of its children is hidden and one label changes owner.
///
/// The status line cannot go with the card. It is the only feedback for a refused join or a failed create, so it is moved
/// into the ACTIONS card, which stays on screen.
/// </summary>
internal sealed class CompactSidebarLayoutFix : ICompatFix
{
    private const string OverlayTypeName = "Sts2LanConnect.Scripts.LanConnectLobbyOverlay";

    private const string BuildDetailsMethodName = "BuildSelectedRoomDetailPanel";

    private const string StatusLabelFieldName = "_statusLabel";

    private const string AppliedMeta = "sts2le_compact_sidebar_layout";

    private const string Tag = "compact-sidebar-layout: ";

    private static Shape? _shape;

    private static int _warned;

    public string Id => "compact-sidebar-layout";

    public FixGroup Group => FixGroup.Touch;

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
                "compact-sidebar-layout: Apply ran without a captured shape; Applies must be consulted first.");

        harmony.Patch(
            shape.BuildDetails,
            postfix: new HarmonyMethod(typeof(CompactSidebarLayoutFix), nameof(DetailsPostfix))
            {
                priority = Priority.Last
            });

        Entry.Info(
            $"{Tag}on a phone the lobby's right-hand column hides the read-only ROOM DETAILS card, so the channel chat and " +
            "its composer fit; the status line moves into ACTIONS and stays visible.");
    }

    /// <summary>
    /// Runs once per sidebar build. Everything downstream keeps its handles: the hidden card is only hidden (the room
    /// detail labels are still written to, they are just not on screen) and the status label is moved, not replaced.
    /// </summary>
    // ReSharper disable once InconsistentNaming
    public static void DetailsPostfix(object? __instance, Control? __result)
    {
        try
        {
            Shape? shape = _shape;
            if (shape == null || __instance is not Node overlay || !GodotObject.IsInstanceValid(overlay)
                || __result is not Control section || !GodotObject.IsInstanceValid(section))
            {
                return;
            }

            if (!TouchChatScrollFix.Platform.IsMobile || section.HasMeta(AppliedMeta))
            {
                return;
            }

            if (section.GetChildCount() < 2
                || section.GetChild(0) is not Control details
                || section.GetChild(1) is not Control actions)
            {
                return;
            }

            // Shape guard: only hide a card without a button, and only when the survivor really is the action card
            if (HasButton(details) || !HasButton(actions))
            {
                return;
            }

            if (shape.StatusLabel.GetValue(__instance) is not Label status
                || !GodotObject.IsInstanceValid(status) || !details.IsAncestorOf(status))
            {
                return;
            }

            // The action card's body is its first child; falling back to the card itself still keeps the label on screen
            Node body = actions.GetChildCount() > 0 && actions.GetChild(0) is VBoxContainer box ? box : actions;

            status.GetParent()?.RemoveChild(status);
            body.AddChild(status);
            details.Visible = false;
            section.SetMeta(AppliedMeta, true);
        }
        catch (Exception exception)
        {
            WarnOnce("the phone column could not be trimmed: " + exception.GetType().Name + ": " + exception.Message);
        }
    }

    private static bool HasButton(Node node)
    {
        if (node is Button)
        {
            return true;
        }

        foreach (Node child in node.GetChildren())
        {
            if (HasButton(child))
            {
                return true;
            }
        }

        return false;
    }

    private static void WarnOnce(string message) =>
        Entry.WarnOnce(ref _warned, "compact-sidebar-layout", message);

    /// <summary>Every anchor this repair needs, resolved once in <see cref="Applies"/>.</summary>
    internal sealed class Shape
    {
        internal MethodInfo BuildDetails = null!;

        internal FieldInfo StatusLabel = null!;

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

            MethodInfo? buildDetails = ReflectScope.Exact(
                overlay,
                BuildDetailsMethodName,
                requireStatic: false,
                parameterTypeNames: Array.Empty<string>(),
                returnTypeName: "Godot.Control",
                problems);

            FieldInfo? status = ReflectScope.Field(problems, overlay, StatusLabelFieldName);
            if (status != null && !typeof(Label).IsAssignableFrom(status.FieldType))
            {
                problems.Add($"{OverlayTypeName}.{StatusLabelFieldName} is {status.FieldType.FullName}, expected a Label");
                status = null;
            }

            if (buildDetails == null || status == null || problems.Count > 0)
            {
                shape = new Shape();
                missing = string.Join(", ", problems);
                return false;
            }

            shape.BuildDetails = buildDetails;
            shape.StatusLabel = status;
            missing = string.Empty;
            return true;
        }
    }
}
