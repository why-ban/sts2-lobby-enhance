using System.Reflection;
using Godot;
using HarmonyLib;

namespace Sts2LobbyEnhance;

/// <summary>
/// Phone-only: a finger drag anywhere on the chat log scrolls it, and that is the whole repair. Two things have to be
/// undone for that, and they happen in two different places.
///
/// The message row: its label ships with selection on (with emulate_mouse_from_touch a press would anchor a selection and
/// the drag would extend it instead of scrolling) and with a focus ring that the engine paints while ignoring every clip,
/// so the row postfix gives the pointer back, leaves selection off and empties that ring.
///
/// The message area: the lobby overlay walks its whole tree once the sidebar is built and makes every Control passive,
/// the chat's own <c>ScrollContainer</c> included. Silencing it means <c>ScrollContainer::gui_input</c> never runs and a
/// drag over the log does nothing; the in-run HUD never runs that pass, which is the whole difference between the two
/// chat surfaces. That pass is patched too, and the postfix fires for the message ScrollContainer right after it was
/// silenced - correcting it from the row instead loses the race, because the rows are built and their postfixes run one
/// line before the sweep.
///
/// On a phone nothing is offered in place of the removed selection: no highlight is painted, nothing is copied, and a
/// finger held still for 400 ms opens nothing when it lifts - upstream decides "tap" by travel alone, so without that
/// last piece a still long press would still open the reference preview. A shorter tap still opens it, a drag still only
/// scrolls, and desktop never enters any of these bodies, so mouse selection, copy and reference clicks stay exactly
/// upstream's own there.
/// </summary>
internal sealed class TouchChatScrollFix : ICompatFix
{
    private const string ViewTypeName = "Sts2LanConnect.Scripts.LanConnectRichMessageView";

    private const string ViewReadyMethodName = "_Ready";

    private const string ViewLabelFieldName = "_label";

    private const string ViewInputMethodName = "OnGuiInput";

    private const string ViewInputParameterName = "input";

    /// <summary>The pass's only argument; the postfix binds to it by name (upstream: <c>Node node</c>)</summary>
    private const string PassiveFilterParameterName = "node";

    private const string ReferencePressedMethodName = "RaiseReferencePressed";

    private const string InputEventTypeName = "Godot.InputEvent";

    private const string StringTypeName = "System.String";

    private const string PanelTypeName = "Sts2LanConnect.Scripts.LanConnectBasicChatPanel";

    private const string OverlayTypeName = "Sts2LanConnect.Scripts.LanConnectLobbyOverlay";

    private const string PassiveFilterMethodName = "ApplyPassiveMouseFilterRecursive";

    private const string NodeTypeName = "Godot.Node";

    /// <summary>
    /// Stamped on the two nodes the chat's pointer has to reach - the message label and the message ScrollContainer - so
    /// the passive pass can hand exactly those two back and nothing else
    /// </summary>
    private const string ChatPointerMetaName = "lobby_enhance_chat_pointer";

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

    /// <summary>
    /// Upstream's own minimum touch target, duplicated as a literal rather than reflected because a missing constant is
    /// not worth losing the scroll fix over
    /// </summary>
    private const float ScrollBarTouchWidth = 44f;

    /// <summary>
    /// When the finger that is currently on a message went down. A still finger that has been down longer than
    /// <see cref="LongPressMilliseconds"/> is a long press, not a tap, and must not open a reference preview
    /// </summary>
    private const string PressStartedMetaName = "lobby_enhance_reference_press_at";

    /// <summary>The same 400 ms the chat panel uses to tell a hold from a tap</summary>
    private const long LongPressMilliseconds = 400;

    private static Shape? _shape;

    private static int _viewWarned;
    private static int _scrollBarWarned;
    private static int _referenceWarned;
    private static int _passiveWarned;

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

        // The lobby overlay silences its whole tree once the sidebar exists, chat message area included. This postfix runs
        // for the message ScrollContainer inside that same pass, i.e. always after the node was silenced, so no ordering or
        // rebuild can leave the log draggable-dead
        harmony.Patch(
            shape.PassiveFilter,
            postfix: new HarmonyMethod(typeof(TouchChatScrollFix), nameof(PassiveFilterPostfix))
            {
                priority = Priority.Last
            });

        // Timing only: upstream's own handler has already run, and this records when the finger went down and drops the
        // stamp when it comes back up
        harmony.Patch(
            shape.ViewInput,
            postfix: new HarmonyMethod(typeof(TouchChatScrollFix), nameof(ViewInputPostfix))
            {
                priority = Priority.Last
            });

        // The one funnel every activation path goes through - the touch release and the label's meta click alike - so one
        // guard covers both
        harmony.Patch(
            shape.ReferencePressed,
            prefix: new HarmonyMethod(typeof(TouchChatScrollFix), nameof(ReferencePressedPrefix))
            {
                priority = Priority.First
            });

        Entry.Info(
            "touch-chat-scroll: patched " + ViewTypeName + "." + ViewReadyMethodName + ", " + ViewTypeName + "." +
            ViewInputMethodName + ", " + ViewTypeName + "." + ReferencePressedMethodName + ", " + PanelTypeName + "." +
            ScrollBarStyleMethodName + " and " + OverlayTypeName + "." + PassiveFilterMethodName + ". On a mobile build a " +
            "drag over the chat log scrolls it: the message label hands " +
            "the pointer on and never anchors a selection, the log's ScrollContainer hands it on too, and the theme's " +
            "focus ring on a message is emptied so nothing can be painted outside the message area. A phone offers no " +
            "selection and no copy, and a finger held still on a message for 400 ms or longer opens nothing when it lifts; " +
            "a shorter tap still opens the reference preview. On desktop every patch body returns without touching a " +
            "property, so selection, copy and reference clicks stay as upstream built them.");
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
            // chat ScrollContainer finally sees the drag. The mark is what the passive pass hands back below
            label.MouseFilter = Control.MouseFilterEnum.Pass;
            label.SetMeta(ChatPointerMetaName, true);

            // Selection off for good: with emulate_mouse_from_touch a press would otherwise anchor a selection and the
            // drag would extend it, which is the "drag selects text" symptom. Nothing on a phone turns this back on, so a
            // long press has no effect at all
            label.SelectionEnabled = false;

            // Turning selection off also lowers focus_mode to Accessibility; All is what upstream builds this label with,
            // and it is what keeps the message list reachable from a gamepad
            label.FocusMode = Control.FocusModeEnum.All;

            // The focus ring is the only draw in a RichTextLabel that escapes clipping: the engine paints it between
            // canvas_item_add_clip_ignore calls, a couple of pixels outside the label's own rect, so a focused row smears
            // a light frame across the input box and past the chat window on touch. The row stays focusable on purpose
            // (gamepad navigation and the host's focus restore keep working), the ring itself is emptied, and with nothing
            // left to paint outside the box the message area's own edge contains everything that is drawn in it
            label.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());

            MarkScrollTarget(view);
        }
        catch (Exception exception)
        {
            WarnOnce(ref _viewWarned, "could not make the chat log draggable: " +
                                      exception.GetType().Name + ": " + exception.Message);
        }
    }

    /// <summary>
    /// The lobby overlay walks its whole tree once the sidebar is built and sets <c>MouseFilter.Ignore</c> on every Control
    /// that is not a Button, a LineEdit or a ColorRect - the message label and the message ScrollContainer included, which
    /// silences the whole chat surface: no GuiInput on the label, so no reference tap either, and no
    /// <c>ScrollContainer::gui_input</c>, so no drag. The rows exist before that sweep runs (the panel builds them inside
    /// its own <c>_Ready</c>), so correcting them from the row is overwritten; this postfix fires per visited node, i.e.
    /// for each marked node right after the sweep silenced it, and hands it back
    /// </summary>
    // ReSharper disable once InconsistentNaming
    private static void PassiveFilterPostfix(Node? node)
    {
        try
        {
            if (!Platform.IsMobile || node is not Control control || !GodotObject.IsInstanceValid(control))
            {
                return;
            }

            if (control.MouseFilter != Control.MouseFilterEnum.Ignore || !control.HasMeta(ChatPointerMetaName))
            {
                return;
            }

            control.MouseFilter = Control.MouseFilterEnum.Pass;
        }
        catch (Exception exception)
        {
            WarnOnce(ref _passiveWarned, "could not keep the chat log draggable: " + exception.GetType().Name + ": " +
                                         exception.Message);
        }
    }

    /// <summary>
    /// Marks the message area's <c>ScrollContainer</c> from the row: the first one above a message row is the one whose
    /// drag scrolls the log
    /// </summary>
    private static void MarkScrollTarget(Node view)
    {
        for (Node? node = view.GetParent(); node != null; node = node.GetParent())
        {
            if (node is not ScrollContainer container || !GodotObject.IsInstanceValid(container))
            {
                continue;
            }

            container.SetMeta(ChatPointerMetaName, true);
            if (container.MouseFilter == Control.MouseFilterEnum.Ignore)
            {
                container.MouseFilter = Control.MouseFilterEnum.Pass;
            }

            return;
        }
    }

    /// <summary>
    /// Called by upstream while it styles the message scroll bar, inside <c>if (UsesLobbyStyle)</c>: the in-run HUD does
    /// not come through here, and that is intended. Its only job on a phone is a bar wide enough to hit with a finger
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
        }
        catch (Exception exception)
        {
            WarnOnce(ref _scrollBarWarned, "could not widen the message scroll bar: " + exception.GetType().Name +
                                           ": " + exception.Message);
        }
    }

    /// <summary>
    /// Remembers when the finger currently on this row went down, and forgets it when the finger lifts. Runs after
    /// upstream's handler so nothing about its tap detection changes: the only thing added here is a timestamp
    /// </summary>
    // ReSharper disable once InconsistentNaming
    private static void ViewInputPostfix(object? __instance, InputEvent? input)
    {
        try
        {
            if (!Platform.IsMobile || __instance is not Node view || !GodotObject.IsInstanceValid(view)
                || input is not InputEventScreenTouch touch)
            {
                return;
            }

            if (touch.Pressed)
            {
                view.SetMeta(PressStartedMetaName, (long)Time.GetTicksMsec());
            }
            else if (view.HasMeta(PressStartedMetaName))
            {
                // Removing a meta that is not there logs an engine error, so this is guarded
                view.RemoveMeta(PressStartedMetaName);
            }
        }
        catch (Exception exception)
        {
            WarnOnce(ref _viewWarned, "could not time a press on a message: " + exception.GetType().Name + ": " +
                                      exception.Message);
        }
    }

    /// <summary>
    /// A finger that stayed down for 400 ms without travelling is a long press, not a tap, so the preview it would
    /// otherwise open on release is skipped. Both of upstream's activation paths call this one method - the touch release
    /// in its own handler and the label's meta click - and the travel rule is already upstream's own (16 px cancels the
    /// tap), so a drag never gets here in the first place. Desktop never reaches this guard
    /// </summary>
    private static bool ReferencePressedPrefix(object? __instance)
    {
        try
        {
            if (!Platform.IsMobile || __instance is not Node view || !GodotObject.IsInstanceValid(view)
                || !view.HasMeta(PressStartedMetaName))
            {
                return true;
            }

            long startedAt = view.GetMeta(PressStartedMetaName).AsInt64();
            if (startedAt <= 0L)
            {
                return true;
            }

            return (long)Time.GetTicksMsec() - startedAt < LongPressMilliseconds;
        }
        catch (Exception exception)
        {
            // A broken guard must not swallow a real activation, so fall through to upstream
            WarnOnce(ref _referenceWarned, "could not tell whether a long press opened a reference: " +
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
    /// Every member this repair touches, resolved once in <see cref="Applies"/> and not probed again. Metadata only: no
    /// <c>typeof</c> on a host type, nothing invoked while probing, no Godot call
    /// </summary>
    internal sealed class Shape
    {
        private Shape(
            MethodInfo viewReady,
            MethodInfo viewInput,
            MethodInfo referencePressed,
            FieldInfo label,
            MethodInfo scrollBarStyle,
            MethodInfo passiveFilter)
        {
            ViewReady = viewReady;
            ViewInput = viewInput;
            ReferencePressed = referencePressed;
            Label = label;
            ScrollBarStyle = scrollBarStyle;
            PassiveFilter = passiveFilter;
        }

        internal MethodInfo ViewReady { get; }

        internal MethodInfo ViewInput { get; }

        internal MethodInfo ReferencePressed { get; }

        internal FieldInfo Label { get; }

        internal MethodInfo ScrollBarStyle { get; }

        internal MethodInfo PassiveFilter { get; }

        internal static Shape? TryCapture(Assembly host, out string reason)
        {
            var missing = new List<string>();

            Type? view = ReflectScope.HostType(host, ViewTypeName, missing);
            Type? panel = ReflectScope.HostType(host, PanelTypeName, missing);
            Type? overlay = ReflectScope.HostType(host, OverlayTypeName, missing);

            MethodInfo? viewReady = Declared(
                missing,
                view,
                ViewReadyMethodName,
                requireStatic: false,
                arity: 0,
                VoidTypeName,
                firstParameterTypeName: null);

            // OnGuiInput(InputEvent) - the row's own handler for the pointer stream
            MethodInfo? viewInput = Declared(
                missing,
                view,
                ViewInputMethodName,
                requireStatic: false,
                arity: 1,
                VoidTypeName,
                InputEventTypeName);

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
            ReflectScope.RequireParameterNames(missing, viewInput, ViewInputParameterName);

            // ApplyPassiveMouseFilterRecursive(Node) - static on the overlay, the pass that silences the sidebar chat
            MethodInfo? passiveFilter = Declared(
                missing,
                overlay,
                PassiveFilterMethodName,
                requireStatic: true,
                arity: 1,
                VoidTypeName,
                NodeTypeName);

            ReflectScope.RequireParameterNames(missing, passiveFilter, PassiveFilterParameterName);
            if (missing.Count > 0)
            {
                reason = "missing: " + string.Join(", ", missing);
                return null;
            }

            reason = string.Empty;
            return new Shape(viewReady!, viewInput!, referencePressed!, label!, scrollBarStyle!, passiveFilter!);
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
