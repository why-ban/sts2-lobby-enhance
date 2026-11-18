using System.Reflection;
using Godot;
using HarmonyLib;

namespace Sts2LobbyEnhance;

/// <summary>
/// The in-run chat overlay's faded hint ("按 Enter 打开聊天") sits over the game's own top-right buttons. The label itself
/// is already <c>MouseFilter.Ignore</c> upstream, but it is wrapped in a row and a stack that are not, and one of them is
/// explicitly <c>Stop</c>, so a click on the hint was consumed before the buttons under it could see it.
///
/// This repair walks the hint's own parent chain - the hint, its row, the stack and the overlay root - and hands each node
/// back as <c>Ignore</c>. Nothing else is touched: the engine descends into a node's children before it checks that node's
/// own filter, so the bubble button, the panel and its input keep receiving clicks even though their ancestors are
/// transparent (the same rule makes the hint's own area fall through to whatever is painted underneath).
/// </summary>
internal sealed class RoomChatHintPassthroughFix : ICompatFix
{
    private const string OverlayTypeName = "Sts2LanConnect.Scripts.LanConnectRoomChatOverlay";

    private const string BuildUiMethodName = "BuildUi";

    private const string RootFieldName = "_root";

    private const string HintLabelName = "RoomChatFadeHint";

    private const string ControlTypeName = "Godot.Control";

    private const string VoidTypeName = "System.Void";

    private static Shape? _shape;

    private static int _warned;

    public string Id => "room-chat-hint-passthrough";

    /// <summary>One overlay, one hint: a shape change here rolls back this repair and nothing else</summary>
    public FixGroup Group => FixGroup.Chat;

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
        Shape shape = _shape ?? throw new InvalidOperationException(
            "room-chat-hint-passthrough: Apply ran without a captured shape; Applies must be consulted first.");

        harmony.Patch(
            shape.BuildUi,
            postfix: new HarmonyMethod(typeof(RoomChatHintPassthroughFix), nameof(BuildUiPostfix))
            {
                priority = Priority.Last
            });

        Entry.Info(
            "room-chat-hint-passthrough: patched " + OverlayTypeName + "." + BuildUiMethodName + ". The faded hint's own " +
            "chain is set to MouseFilter.Ignore, so the line stays readable but lets every click through to the buttons " +
            "under it; the bubble, the panel and the input keep their own filters and stay clickable.");
    }

    /// <summary>
    /// Runs after upstream built the overlay's tree, i.e. after every filter in it was assigned once. Only the chain
    /// between the hint label and the overlay root is touched.
    /// </summary>
    // ReSharper disable once InconsistentNaming
    private static void BuildUiPostfix(object? __instance)
    {
        try
        {
            Shape? shape = _shape;
            if (shape == null || __instance is not Node overlay || !GodotObject.IsInstanceValid(overlay))
            {
                return;
            }

            if (shape.Root.GetValue(__instance) is not Control root || !GodotObject.IsInstanceValid(root))
            {
                return;
            }

            Control? hint = FindByName(root, HintLabelName);
            if (hint == null)
            {
                // A renamed hint is a shape change, not a guess: leave the tree exactly as upstream built it
                return;
            }

            for (Node? node = hint; node != null; node = node.GetParent())
            {
                if (node is Control control)
                {
                    control.MouseFilter = Control.MouseFilterEnum.Ignore;
                }

                if (ReferenceEquals(node, root))
                {
                    break;
                }
            }
        }
        catch (Exception exception)
        {
            WarnOnce("could not make the chat hint transparent: " + exception.GetType().Name + ": " + exception.Message);
        }
    }

    private static Control? FindByName(Node root, string name)
    {
        if (root is Control control && string.Equals(root.Name.ToString(), name, StringComparison.Ordinal))
        {
            return control;
        }

        foreach (Node child in root.GetChildren())
        {
            Control? found = FindByName(child, name);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private static void WarnOnce(string message) =>
        Entry.WarnOnce(ref _warned, "room-chat-hint-passthrough", message);

    /// <summary>Every anchor this repair needs, resolved once in <see cref="Applies"/>.</summary>
    internal sealed class Shape
    {
        private Shape(MethodInfo buildUi, FieldInfo root)
        {
            BuildUi = buildUi;
            Root = root;
        }

        internal MethodInfo BuildUi { get; }

        internal FieldInfo Root { get; }

        internal static bool TryCapture(Assembly host, out Shape shape, out string missing)
        {
            shape = null!;
            var problems = new List<string>();

            Type? overlay = ReflectScope.HostType(host, OverlayTypeName, problems);

            MethodInfo? buildUi = overlay?.GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .FirstOrDefault(m => m.Name == BuildUiMethodName
                                     && m.GetParameters().Length == 0
                                     && m.ReturnType.FullName == VoidTypeName);

            FieldInfo? root = ReflectScope.Field(problems, overlay, RootFieldName);
            if (root != null && !typeof(Control).IsAssignableFrom(root.FieldType))
            {
                problems.Add(OverlayTypeName + "." + RootFieldName + " is " + root.FieldType.Name + ", expected a Control");
                root = null;
            }

            if (buildUi == null)
            {
                problems.Add(OverlayTypeName + "." + BuildUiMethodName + "() declared by the type itself not found");
            }

            if (problems.Count > 0)
            {
                missing = string.Join(", ", problems);
                return false;
            }

            shape = new Shape(buildUi!, root!);
            missing = string.Empty;
            return true;
        }
    }
}
