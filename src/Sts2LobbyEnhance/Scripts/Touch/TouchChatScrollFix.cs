using System.Reflection;
using Godot;
using HarmonyLib;

namespace Sts2LobbyEnhance;

/// <summary>
/// Phone-only: the chat message labels stop capturing the pointer so a drag scrolls, a 400 ms press selects text
/// instead, and a press that selected must not also open the reference preview. Every patch body returns on desktop
/// before it writes a property, so mouse selection and reference clicks stay upstream's own
/// </summary>
internal sealed class TouchChatScrollFix : ICompatFix
{
    private const string ViewTypeName = "Sts2LanConnect.Scripts.LanConnectRichMessageView";
    private const string ViewReadyMethodName = "_Ready";
    private const string ViewLabelFieldName = "_label";
    private const string ReferencePressedMethodName = "RaiseReferencePressed";

    private const string PanelTypeName = "Sts2LanConnect.Scripts.LanConnectBasicChatPanel";
    private const string ScrollBarStyleMethodName = "ApplyLobbyScrollBarStyle";

    /// <summary>
    /// Harmony binds a patch argument to the target parameter by name, so a rename would stop feeding the bar in and the
    /// postfix would throw on every message rather than bind to nothing
    /// </summary>
    private const string ScrollBarParameterName = "scrollBar";

    /// <summary>Types the patch bodies need are resolved by name so no host <c>typeof</c> exists in this file</summary>
    private const string RichTextLabelTypeName = "Godot.RichTextLabel";

    private const string ScrollBarTypeName = "Godot.ScrollBar";

    private const string VoidTypeName = "System.Void";

    private const string StringTypeName = "System.String";

    /// <summary>
    /// Upstream's own minimum touch target, duplicated as a literal rather than reflected because a missing constant is
    /// not worth losing the scroll fix over
    /// </summary>
    private const float ScrollBarTouchWidth = 44f;

    /// <summary>
    /// Marks a view whose gesture is already attached, so a row that reaches <c>_Ready</c> twice cannot end up with two
    /// gestures and copy twice per press
    /// </summary>
    internal const string GestureAttachedMetaName = "lobby_enhance_touch_selection_gesture";

    /// <summary>
    /// Marks a <c>ScrollContainer</c> that already reports its presses to <see cref="TouchSelectionSession"/>. Rows are
    /// rebuilt on every message change, so without it each rebuild would add another connection
    /// </summary>
    private const string SelectionExitHookMetaName = "lobby_enhance_selection_exit_hook";

    /// <summary>Marks a scroll bar that already reports its presses, so the two attach paths cannot double-connect it</summary>
    private const string ScrollBarHookMetaName = "lobby_enhance_selection_exit_bar";

    private static Shape? _shape;

    private static int _viewWarned;
    private static int _scrollBarWarned;
    private static int _referenceWarned;
    private static int _attachWarned;
    private static int _pressDropWarned;

    public string Id => "touch-chat-scroll";

    /// <summary>
    /// Patches the message rows and the message scroll bar only: a shape change there rolls back this repair and
    /// nothing else
    /// </summary>
    public FixGroup Group => FixGroup.Touch;

    /// <summary>
    /// Which anchors exist is answered by the host build, not by the device, so a desktop startup installs the same
    /// inert patches as a phone one
    /// </summary>
    public bool Applies(out string reason)
    {
        Assembly? host = Reflect.FindAssembly(Entry.HostModAssemblyName);
        if (host == null)
        {
            reason = $"host mod '{Entry.HostModAssemblyName}' is not loaded";
            return false;
        }

        Shape? shape = Shape.TryCapture(host, out reason);
        if (shape == null)
        {
            return false;
        }

        _shape = shape;
        reason = string.Empty;
        return true;
    }

    public void Apply(Harmony harmony)
    {
        Shape shape = _shape ?? throw new InvalidOperationException(
            "touch-chat-scroll: Applies() did not capture the chat shape; refusing to patch blind.");

        harmony.Patch(
            shape.ViewReady,
            postfix: new HarmonyMethod(typeof(TouchChatScrollFix), nameof(ViewReadyPostfix))
            {
                priority = Priority.Last
            });

        harmony.Patch(
            shape.ScrollBarStyle,
            postfix: new HarmonyMethod(typeof(TouchChatScrollFix), nameof(ScrollBarStylePostfix))
            {
                priority = Priority.Last
            });

        // Priority.First so "this press was already consumed by the copy" is answerable before anything else decides to
        // open the preview. The prefix takes no parameters, so it cannot displace upstream's arguments
        harmony.Patch(
            shape.ReferencePressed,
            prefix: new HarmonyMethod(typeof(TouchChatScrollFix), nameof(ReferencePressedPrefix))
            {
                priority = Priority.First
            });

        Entry.Info(
            "touch-chat-scroll: patched " + ViewTypeName + "." + ViewReadyMethodName + ", " + PanelTypeName + "." +
            ScrollBarStyleMethodName + " and " + ViewTypeName + "." + ReferencePressedMethodName + ". On a mobile " +
            "build the message labels stop capturing the pointer (a drag scrolls the log), a 400 ms press puts the row " +
            "into text selection (drag to shape it, lift to copy) and does not open the reference preview it would " +
            "otherwise have opened; on desktop every patch body returns without touching a property, so selection and " +
            "dragging stay as upstream built them.");
    }

    /// <summary>
    /// One freshly inserted message row, after upstream's own <c>_Ready</c> has run so the label holds rendered text, and
    /// nothing is cached because <c>Refresh()</c> frees and rebuilds every row whenever the message list changes
    /// </summary>
    // ReSharper disable once UnusedParameter.Local
    private static void ViewReadyPostfix(object __instance)
    {
        try
        {
            if (!Platform.IsMobile || __instance is not Node view || !GodotObject.IsInstanceValid(view))
            {
                return;
            }

            Shape shape = _shape ?? throw new InvalidOperationException(
                "touch-chat-scroll: the shape was not captured.");

            if (shape.Label.GetValue(__instance) is not RichTextLabel label || !GodotObject.IsInstanceValid(label))
            {
                // A row without its label cannot be made draggable, so leave it as it is rather than guessing
                return;
            }

            // Pass keeps GuiInput (upstream's own reference handling needs it) and stops consuming the event, so the
            // chat ScrollContainer finally sees the drag
            label.MouseFilter = Control.MouseFilterEnum.Pass;

            // Selection off: with emulate_mouse_from_touch the press would otherwise anchor a selection and the drag
            // would extend it, which is the "drag selects text" symptom the players reported
            label.SelectionEnabled = false;

            // Turning selection off also lowers focus_mode to Accessibility; All is what upstream builds this label with
            label.FocusMode = Control.FocusModeEnum.All;

            if (view.HasMeta(GestureAttachedMetaName))
            {
                return;
            }

            view.SetMeta(GestureAttachedMetaName, true);

            // The gesture reads the selected text from the label and pulses the view, and its connection dies with the
            // label
            new TouchSelectionGesture(label, view as CanvasItem).Attach();

            // A press anywhere in the message log has to end a selection the row is showing, not only a press that lands
            // on that same row
            AttachSelectionExitHook(view);
        }
        catch (Exception exception)
        {
            WarnOnce(ref _viewWarned, "could not make the chat text scrollable and selectable: " +
                                      exception.GetType().Name + ": " + exception.Message);
        }
    }

    /// <summary>
    /// Called by upstream while it styles the message scroll bar, inside <c>if (UsesLobbyStyle)</c>: the in-run HUD
    /// style does not come through here, and that is intended. Its bar is hooked from the row path instead, so a press
    /// on either bar ends a selection
    /// </summary>
    // ReSharper disable once InconsistentNaming
    private static void ScrollBarStylePostfix(ScrollBar scrollBar)
    {
        try
        {
            if (!Platform.IsMobile || scrollBar == null || !GodotObject.IsInstanceValid(scrollBar))
            {
                return;
            }

            scrollBar.CustomMinimumSize = new Vector2(ScrollBarTouchWidth, 0f);

            // The bar keeps Control's default MOUSE_FILTER_STOP, so a press on it is stopped at the bar and does not
            // bubble to the ScrollContainer. Without this connection a press on the bar leaves the highlight on screen
            if (!scrollBar.HasMeta(ScrollBarHookMetaName))
            {
                scrollBar.SetMeta(ScrollBarHookMetaName, true);
                scrollBar.GuiInput += OnContainerGuiInput;
            }

            // The bar's parent is the message ScrollContainer
            AttachSelectionExitHook(scrollBar);
        }
        catch (Exception exception)
        {
            WarnOnce(ref _scrollBarWarned, "could not widen the message scroll bar: " + exception.GetType().Name +
                                           ": " + exception.Message);
        }
    }

    /// <summary>
    /// Hooks the nearest enclosing <c>ScrollContainer</c> and its vertical bar: the container sees presses on the log and
    /// the bar its own, and each is guarded by its own meta so the two attach paths cannot connect twice. The handler
    /// only ends a live selection and does not accept the event
    /// </summary>
    private static void AttachSelectionExitHook(Node from)
    {
        try
        {
            for (Node? node = from.GetParent(); node != null; node = node.GetParent())
            {
                if (node is not ScrollContainer container)
                {
                    continue;
                }

                if (!container.HasMeta(SelectionExitHookMetaName))
                {
                    container.SetMeta(SelectionExitHookMetaName, true);
                    container.GuiInput += OnContainerGuiInput;
                }

                // The in-run HUD style never calls ApplyLobbyScrollBarStyle, so its bar is only reachable from here
                ScrollBar? bar = container.GetVScrollBar();
                if (bar != null && GodotObject.IsInstanceValid(bar) && !bar.HasMeta(ScrollBarHookMetaName))
                {
                    bar.SetMeta(ScrollBarHookMetaName, true);
                    bar.GuiInput += OnContainerGuiInput;
                }

                return;
            }
        }
        catch (Exception exception)
        {
            WarnOnce(ref _attachWarned, "could not watch the message scroll container for presses: " +
                                        exception.GetType().Name + ": " + exception.Message);
        }
    }

    /// <summary>
    /// Any new press inside the message log drops a selection that is still on screen, so "tap somewhere else" really
    /// does clear it. Nothing is consumed, so the container still scrolls as before
    /// </summary>
    private static void OnContainerGuiInput(InputEvent input)
    {
        try
        {
            if (input is InputEventMouseButton { Pressed: true } or InputEventScreenTouch { Pressed: true })
            {
                TouchSelectionSession.ExitAny();
            }
        }
        catch (Exception exception)
        {
            WarnOnce(ref _pressDropWarned, "could not drop the text selection on a press: " + exception.GetType().Name +
                                           ": " + exception.Message);
        }
    }

    /// <summary>
    /// Upstream's one funnel for opening the item or combat reference preview on a message, so one guard covers every
    /// activation path. Returns <c>false</c>, skipping the original, for the one activation a selection press would
    /// otherwise have produced. A missing gate, a desktop build or a throwing body falls through to <c>true</c>, which is
    /// upstream's behaviour
    /// </summary>
    private static bool ReferencePressedPrefix()
    {
        try
        {
            if (!Platform.IsMobile)
            {
                return true;
            }

            return !ReferenceActivationGate.TryConsume();
        }
        catch (Exception exception)
        {
            // A broken guard must not swallow a real activation, so fall through to upstream
            WarnOnce(ref _referenceWarned, "could not tell whether a long press consumed the reference click: " +
                                           exception.GetType().Name + ": " + exception.Message);
            return true;
        }
    }

    /// <summary>
    /// One warning per call site. The rows are rebuilt continuously, so an unguarded log here would flood the log file
    /// from a single broken build
    /// </summary>
    private static void WarnOnce(ref int warned, string message) =>
        Entry.WarnOnce(ref warned, "touch-chat-scroll", message);

    /// <summary>
    /// This repair's own platform read, cached because <c>OS.HasFeature</c> crosses into Godot's native layer, which
    /// exists only inside the game, so the patch bodies are the one place it is safe to call. Not a mod-wide answer,
    /// the Chat group reads the platform for its own gate
    /// </summary>
    internal static class Platform
    {
        private static bool _known;
        private static bool _mobile;

        internal static bool IsMobile
        {
            get
            {
                if (_known)
                {
                    return _mobile;
                }

                // Written before the call so a throwing first call cannot leave this re-entrant
                _known = true;
                try
                {
                    _mobile = OS.HasFeature("mobile");
                }
                catch (Exception)
                {
                    // No native layer, as in the smoke test process, so treat it as desktop and change nothing
                    _mobile = false;
                }

                return _mobile;
            }
        }
    }

    /// <summary>
    /// At most one message is in selection mode, and any new press anywhere has to be able to end it. The gesture cannot
    /// see a press that lands on a different row, and a highlight left on screen after the player moved on is worse than
    /// no highlight, so the owner is tracked here instead of inside the label. Single-threaded like the gate below, since
    /// Godot delivers input and runs both sides of this on the main thread
    /// </summary>
    internal static class TouchSelectionSession
    {
        private static TouchSelectionGesture? _active;

        /// <summary>
        /// True while the live selection is still being shaped. Reads main-thread state that only the timer and the input
        /// handlers write, so a scroll surface can consult it from the input stream before the GUI runs
        /// </summary>
        internal static bool IsShaping => _active is { } live && live.IsShaping;

        internal static void Begin(TouchSelectionGesture gesture)
        {
            TouchSelectionGesture? previous = _active;
            _active = gesture;
            if (previous != null && !ReferenceEquals(previous, gesture))
            {
                previous.ForceExit();
            }
        }

        internal static void End(TouchSelectionGesture gesture)
        {
            if (ReferenceEquals(_active, gesture))
            {
                _active = null;
            }
        }

        internal static void ExitAny()
        {
            TouchSelectionGesture? active = _active;
            _active = null;
            active?.ForceExit();
        }
    }

    /// <summary>
    /// "The long press that just fired already put this message into selection mode and will copy it, so the reference
    /// preview it would have opened must not open." A flag rather than a timestamp because the press boundary is what
    /// makes the state unambiguous: the gesture arms it when selection mode starts, before the release that could
    /// activate, and the next press clears it before it can activate anything. Single-threaded by construction, since
    /// Godot delivers input and runs both sides of this on the main thread
    /// </summary>
    internal static class ReferenceActivationGate
    {
        private static bool _armed;

        internal static void Arm() => _armed = true;

        internal static void Disarm() => _armed = false;

        /// <summary>True once per armed press, for the caller that gets there first</summary>
        internal static bool TryConsume()
        {
            if (!_armed)
            {
                return false;
            }

            _armed = false;
            return true;
        }
    }

    /// <summary>
    /// Every member this repair touches, resolved once in <see cref="Applies"/> and not probed again. Metadata only: no
    /// <c>typeof</c> on a host type, nothing invoked while probing, no Godot call
    /// </summary>
    internal sealed class Shape
    {
        private Shape(
            MethodInfo viewReady,
            FieldInfo label,
            MethodInfo scrollBarStyle,
            MethodInfo referencePressed)
        {
            ViewReady = viewReady;
            Label = label;
            ScrollBarStyle = scrollBarStyle;
            ReferencePressed = referencePressed;
        }

        internal MethodInfo ViewReady { get; }

        internal FieldInfo Label { get; }

        internal MethodInfo ScrollBarStyle { get; }

        internal MethodInfo ReferencePressed { get; }

        internal static Shape? TryCapture(Assembly host, out string reason)
        {
            var missing = new List<string>();

            Type? view = ReflectScope.HostType(host, ViewTypeName, missing);
            Type? panel = ReflectScope.HostType(host, PanelTypeName, missing);

            MethodInfo? viewReady = Declared(
                missing,
                view,
                ViewReadyMethodName,
                requireStatic: false,
                arity: 0,
                VoidTypeName,
                firstParameterTypeName: null);

            FieldInfo? label = ReflectScope.Field(missing, view, ViewLabelFieldName);
            if (label != null && !InheritsFrom(label.FieldType, RichTextLabelTypeName))
            {
                missing.Add(
                    ViewTypeName + "." + ViewLabelFieldName + " is " + label.FieldType.Name +
                    ", expected a Godot.RichTextLabel");
            }

            MethodInfo? scrollBarStyle = Declared(
                missing,
                panel,
                ScrollBarStyleMethodName,
                requireStatic: true,
                arity: 1,
                VoidTypeName,
                ScrollBarTypeName);

            ReflectScope.RequireParameterNames(missing, scrollBarStyle, ScrollBarParameterName);

            // RaiseReferencePressed(string, LanConnectRichMessageReference). The second parameter is a host type, so
            // arity plus the string first parameter identifies it without naming that type here
            MethodInfo? referencePressed = Declared(
                missing,
                view,
                ReferencePressedMethodName,
                requireStatic: false,
                arity: 2,
                VoidTypeName,
                StringTypeName);

            if (missing.Count > 0)
            {
                reason = "missing: " + string.Join(", ", missing);
                return null;
            }

            reason = string.Empty;
            return new Shape(viewReady!, label!, scrollBarStyle!, referencePressed!);
        }

        /// <summary>
        /// Resolves a method the type declares itself. <c>DeclaredOnly</c> is the point: <c>GetMethods</c> walks the
        /// base chain, and for a lifecycle name like <c>_Ready</c> it silently substitutes <c>Godot.Node._Ready</c>, a
        /// patch that would then run for every node in the process
        /// </summary>
        private static MethodInfo? Declared(
            List<string> missing,
            Type? owner,
            string name,
            bool requireStatic,
            int arity,
            string returnTypeName,
            string? firstParameterTypeName)
        {
            if (owner == null)
            {
                // Already reported by HostType, and repeating it here would bury the real reason
                return null;
            }

            MethodInfo[] matches = owner
                .GetMethods(
                    (requireStatic ? BindingFlags.Static : BindingFlags.Instance) |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly)
                .Where(method => string.Equals(method.Name, name, StringComparison.Ordinal))
                .Where(method => method.GetParameters().Length == arity)
                .Where(method => string.Equals(method.ReturnType.FullName, returnTypeName, StringComparison.Ordinal))
                .Where(method => firstParameterTypeName == null ||
                                 string.Equals(
                                     method.GetParameters()[0].ParameterType.FullName,
                                     firstParameterTypeName,
                                     StringComparison.Ordinal))
                .ToArray();

            if (matches.Length == 1)
            {
                return matches[0];
            }

            string expected = owner.Name + "." + name + "/" + arity + " declared by the type itself";
            missing.Add(matches.Length == 0
                ? expected
                : expected + " is ambiguous (" + matches.Length + " declared overloads)");
            return null;
        }

        private static bool InheritsFrom(Type? type, string baseTypeFullName)
        {
            for (Type? current = type; current != null; current = current.BaseType)
            {
                if (string.Equals(current.FullName, baseTypeFullName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
