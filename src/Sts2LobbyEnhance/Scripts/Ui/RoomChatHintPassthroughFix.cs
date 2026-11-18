using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;

namespace Sts2LobbyEnhance;

/// <summary>
/// The in-run chat overlay lives in the top-right corner, on top of the game's own buttons, and two of its parts keep taking
/// clicks that were meant for those buttons.
///
/// The faded hint ("按 Enter 打开聊天") is the small one: the label itself is <c>Ignore</c> upstream, but its row, the stack
/// above it and the overlay root were not, so the hint's own rectangle swallowed the press. They are set to
/// <c>Ignore</c> here - the bubble and the panel are separate subtrees and keep their filters.
///
/// The panel is the large one. Collapsing it does not remove it: the whole subtree stays in the tree with real filters, and
/// its control strip is even <c>Stop</c>, so while the panel is invisible its rectangle still eats presses. This repair reads
/// the overlay's own <c>RoomOverlayOpen</c> flag after each refresh, and on a collapse saves every Control's filter inside the
/// panel subtree and makes them <c>Ignore</c>; on an expand it writes exactly the saved values back.
///
/// The bubble's wrapper is the third. The wrapper is a plain <c>Control</c> that stays visible with <c>Pass</c> even when the
/// button inside it is hidden, and <c>Pass</c> is not <c>Ignore</c>: the viewport's hit test returns such a node, so the press
/// is delivered into the overlay and consumed there instead of reaching the game button underneath. A hidden button therefore
/// has to silence its wrapper subtree - visibility of the button itself is the signal, which is independent of whether the
/// panel is open. When the button is visible again the saved filters are restored node by node, so the bubble stays clickable
/// and draggable.
///
/// Both save/restore pairs work on transitions only, each with its own list, and only ever write the values they captured.
/// </summary>
internal sealed class RoomChatHintPassthroughFix : ICompatFix
{
    private const string OverlayTypeName = "Sts2LanConnect.Scripts.LanConnectRoomChatOverlay";

    private const string BuildUiMethodName = "BuildUi";

    private const string RefreshMethodName = "RefreshFromSource";

    private const string ResolveChatMethodName = "ResolveChat";

    private const string OpenPropertyName = "RoomOverlayOpen";

    private const string RootFieldName = "_root";

    private const string HintLabelName = "RoomChatFadeHint";

    private const string PanelFadeName = "RoomChatPanelFadeContainer";

    private const string ToggleButtonName = "RoomChatToggleButton";

    private const string VoidTypeName = "System.Void";

    private static readonly ConditionalWeakTable<object, PanelState> States = new();

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

        harmony.Patch(
            shape.Refresh,
            postfix: new HarmonyMethod(typeof(RoomChatHintPassthroughFix), nameof(RefreshPostfix))
            {
                priority = Priority.Last
            });

        Entry.Info(
            "room-chat-hint-passthrough: patched " + OverlayTypeName + "." + BuildUiMethodName + " and " +
            OverlayTypeName + "." + RefreshMethodName + ". While the panel is collapsed the hint's own chain and the whole " +
            "panel subtree are MouseFilter.Ignore, so nothing of the overlay can eat a click meant for the game; expanding " +
            "restores every saved filter, so the bubble, the panel and its controls stay clickable.");
    }

    /// <summary>Runs once after upstream built the tree; only the hint label's own chain up to the root is touched.</summary>
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

    /// <summary>
    /// Runs after upstream refreshed the tree, so <c>RoomOverlayOpen</c> is read, never inferred from rectangles or alpha.
    /// Work happens only when that flag flips: collapse saves-and-silences, expand restores the saved values.
    /// </summary>
    // ReSharper disable once InconsistentNaming
    private static void RefreshPostfix(object? __instance)
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

            PanelState state = States.GetValue(overlay, _ => new PanelState());
            ApplyToggleState(shape, root, state);

            bool open = ReadOpen(shape, overlay);
            if (state.Open == open)
            {
                return;
            }

            state.Open = open;
            if (state.Fade == null || !GodotObject.IsInstanceValid(state.Fade))
            {
                state.Fade = FindByName(root, PanelFadeName);
                state.Saved.Clear();
            }

            if (state.Fade == null)
            {
                return;
            }

            if (!open)
            {
                state.Saved.Clear();
                Collect(state.Fade, state.Saved);
                foreach ((Control control, Control.MouseFilterEnum _) in state.Saved)
                {
                    if (GodotObject.IsInstanceValid(control))
                    {
                        control.MouseFilter = Control.MouseFilterEnum.Ignore;
                    }
                }

                return;
            }

            foreach ((Control control, Control.MouseFilterEnum filter) in state.Saved)
            {
                if (GodotObject.IsInstanceValid(control))
                {
                    control.MouseFilter = filter;
                }
            }

            state.Saved.Clear();
        }
        catch (Exception exception)
        {
            WarnOnce("could not keep the chat window from eating clicks: " + exception.GetType().Name + ": " +
                     exception.Message);
        }
    }

    /// <summary>
    /// The bubble's wrapper is a Pass Control that stays visible on desktop even though the button inside it is hidden, so
    /// it is the topmost hit at that 56x56 square and eats the click meant for the game's own button under it. Visibility
    /// of the button itself is the signal here: hidden wrapper subtree is silenced, visible wrapper is restored exactly.
    /// </summary>
    private static void ApplyToggleState(Shape shape, Control root, PanelState state)
    {
        if (state.ToggleSilenced)
        {
            // Read the button again: the stored value is the one from the collapse, so trusting it would keep the wrapper
            // ignored for ever after the first collapse, and the bubble would stop being clickable
            Control? now = FindByName(root, ToggleButtonName);
            bool nowVisible = now != null && now.Visible;
            if (nowVisible)
            {
                Restore(state.ToggleSaved);
                state.ToggleSaved.Clear();
                state.ToggleSilenced = false;
            }

            return;
        }

        Control? toggle = FindByName(root, ToggleButtonName);
        if (toggle == null)
        {
            return;
        }

        bool visible = toggle.Visible;
        if (visible)
        {
            return;
        }

        if (toggle.GetParent() is not Control wrapper || !GodotObject.IsInstanceValid(wrapper))
        {
            return;
        }

        state.ToggleSaved.Clear();
        Collect(wrapper, state.ToggleSaved);
        foreach ((Control control, Control.MouseFilterEnum _) in state.ToggleSaved)
        {
            if (GodotObject.IsInstanceValid(control))
            {
                control.MouseFilter = Control.MouseFilterEnum.Ignore;
            }
        }

        state.ToggleSilenced = true;
    }

    private static void Restore(List<(Control Control, Control.MouseFilterEnum Filter)> saved)
    {
        foreach ((Control control, Control.MouseFilterEnum filter) in saved)
        {
            if (GodotObject.IsInstanceValid(control))
            {
                control.MouseFilter = filter;
            }
        }
    }

    private static bool ReadOpen(Shape shape, Node overlay)
    {
        object? chat = shape.ResolveChat.Invoke(overlay, null);
        return chat != null && shape.Open.GetValue(chat) is true;
    }

    /// <summary>Saves every Control in the panel subtree, itself included, in a stable order.</summary>
    private static void Collect(Node root, List<(Control Control, Control.MouseFilterEnum Filter)> into)
    {
        if (root is Control control)
        {
            into.Add((control, control.MouseFilter));
        }

        foreach (Node child in root.GetChildren())
        {
            Collect(child, into);
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

    /// <summary>Per overlay: the last seen open state and the filters saved on the last collapse.</summary>
    private sealed class PanelState
    {
        internal bool? Open;

        internal Control? Fade;

        internal bool ToggleSilenced;

        internal List<(Control Control, Control.MouseFilterEnum Filter)> Saved { get; } = new();

        internal List<(Control Control, Control.MouseFilterEnum Filter)> ToggleSaved { get; } = new();
    }

    /// <summary>Every anchor this repair needs, resolved once in <see cref="Applies"/>.</summary>
    internal sealed class Shape
    {
        private Shape(MethodInfo buildUi, MethodInfo refresh, MethodInfo resolveChat, PropertyInfo open, FieldInfo root)
        {
            BuildUi = buildUi;
            Refresh = refresh;
            ResolveChat = resolveChat;
            Open = open;
            Root = root;
        }

        internal MethodInfo BuildUi { get; }

        internal MethodInfo Refresh { get; }

        internal MethodInfo ResolveChat { get; }

        internal PropertyInfo Open { get; }

        internal FieldInfo Root { get; }

        internal static bool TryCapture(Assembly host, out Shape shape, out string missing)
        {
            shape = null!;
            var problems = new List<string>();

            Type? overlay = ReflectScope.HostType(host, OverlayTypeName, problems);

            MethodInfo? buildUi = Declared(overlay, BuildUiMethodName, problems, out _);
            MethodInfo? refresh = Declared(overlay, RefreshMethodName, problems, out _);
            MethodInfo? resolveChat = Declared(overlay, ResolveChatMethodName, problems, out Type? chatType);

            PropertyInfo? open = chatType?.GetProperty(
                OpenPropertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (chatType != null && (open == null || open.PropertyType != typeof(bool)))
            {
                problems.Add((chatType.FullName ?? chatType.Name) + "." + OpenPropertyName + " is not a bool property");
                open = null;
            }

            FieldInfo? root = ReflectScope.Field(problems, overlay, RootFieldName);
            if (root != null && !typeof(Control).IsAssignableFrom(root.FieldType))
            {
                problems.Add(OverlayTypeName + "." + RootFieldName + " is " + root.FieldType.Name + ", expected a Control");
                root = null;
            }

            if (problems.Count > 0)
            {
                missing = string.Join(", ", problems);
                return false;
            }

            shape = new Shape(buildUi!, refresh!, resolveChat!, open!, root!);
            missing = string.Empty;
            return true;
        }

        private static MethodInfo? Declared(Type? overlay, string name, List<string> problems, out Type? returnType)
        {
            returnType = null;
            MethodInfo? method = overlay?.GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == 0);

            if (method == null)
            {
                problems.Add(OverlayTypeName + "." + name + "() declared by the type itself not found");
                return null;
            }

            if (method.ReturnType.FullName == VoidTypeName)
            {
                if (name != ResolveChatMethodName)
                {
                    return method;
                }

                problems.Add(OverlayTypeName + "." + name + "() returns void");
                return null;
            }

            returnType = method.ReturnType;
            return method;
        }
    }
}
