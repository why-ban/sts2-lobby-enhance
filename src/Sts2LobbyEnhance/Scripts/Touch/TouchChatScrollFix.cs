using System.Reflection;
using Godot;
using HarmonyLib;

namespace Sts2LobbyEnhance;

/// <summary>
/// Makes the chat log scroll with a finger on phones, turns a 400 ms press into a text selection the player can shape
/// with the same finger, and copies that selection when the finger comes up. A press that selected must not also open
/// the item/combat reference preview; <see cref="ReferenceActivationGate"/> handles that.
///
/// <para><b>Why the phone could not scroll, and why it then could not select either.</b> Every message row is a
/// <c>LanConnectRichMessageView</c> whose <c>MegaRichTextLabel</c> is built with <c>SelectionEnabled = true</c>
/// (LanConnectRichMessageView.cs:52) and <c>MouseFilter = Stop</c> (:55). Godot swallows a pointer event at a
/// MOUSE_FILTER_STOP control, so the enclosing <c>ScrollContainer</c> (LanConnectBasicChatPanel.cs:891-900) never saw the
/// drag; and because <c>emulate_mouse_from_touch</c> is on by default the same press landed on the label as a selection
/// anchor and the drag extended that selection. Turning selection off and the filter to Pass fixed the scroll and - for
/// as long as nothing else was done - made a long press unable to select anything at all, which is the regression the
/// players reported.</para>
///
/// <para><b>What this repair does, and only when <c>OS.HasFeature("mobile")</c> says so at runtime:</b></para>
/// <list type="number">
/// <item><c>SelectionEnabled = false</c> + <c>MouseFilter = Pass</c> on that label (:52, :55), so a plain drag bubbles to
/// the <c>ScrollContainer</c> and the log scrolls. Upstream expects exactly that, see its own comment at
/// LanConnectRichMessageView.cs:214 ("Leave presses/drags available to the enclosing chat scroll container"), which
/// cannot hold while :55 says Stop.</item>
/// <item>One <see cref="TouchSelectionGesture"/> per view. 400 ms of holding within 16 px - upstream's own tap/slide
/// boundary (LanConnectReferenceTapGesture.cs:9) - flips the label into selection mode: selection on, filter Stop, a
/// synthetic left-button press through <c>Viewport.PushInput</c> gives the engine the anchor that a selection needs
/// (RichTextLabel only ever starts a selection from a mouse press, rich_text_label.cpp:2636-2674), and
/// <c>SelectAll()</c> makes the message visibly highlighted right away. The finger's continuing drag then extends the
/// range, because every <c>InputEventScreenDrag</c> is re-dispatched as an <c>InputEventMouseMotion</c> with the left
/// button held (input.cpp:895-913) and <c>RichTextLabel</c> extends the selection on any such motion while the anchor is
/// set (rich_text_label.cpp:2940-3005). Because the label is Stop for the duration, the <c>ScrollContainer</c> is handed
/// none of those motions (viewport.cpp:2079-2085, 2163-2165) and does not scroll while the player selects.</item>
/// <item>On release the selected text goes to the clipboard and the highlight stays on screen as the feedback (plus the
/// row pulse this repair already had). The very next press ends selection mode from the emulated mouse press - in the
/// signal phase, before the engine's own handling (control.cpp:1852-1867) - so that press is not anchored and still
/// bubbles to the scroll container: the first drag after a copy scrolls, it does not need a second try. A press anywhere
/// else in the message log ends it too (<see cref="TouchSelectionSession"/> plus the hook on the log's
/// <c>ScrollContainer</c>).</item>
/// <item>A press that selected must not also open the reference preview. Upstream decides "tap" purely by travel
/// (LanConnectReferenceTapGesture.cs:54-55), so a motionless 400 ms press is a tap to it and a message carrying a
/// reference activates on release for a single reference wherever the finger landed
/// (LanConnectRichMessageView.cs:203-212). <see cref="ReferenceActivationGate"/> is armed when selection mode starts and
/// the prefix on <c>RaiseReferencePressed</c> - the one funnel every activation path goes through, touch release (:210),
/// meta click (:171) and keyboard/joypad (:252) - skips exactly the one activation that press produced.</item>
/// <item>The message scroll bar grows to the 44 px touch floor upstream already uses for buttons
/// (LanConnectHudLegibility.cs:13) through a postfix on <c>ApplyLobbyScrollBarStyle</c>
/// (LanConnectBasicChatPanel.cs:3022-3036, called at :919). It is the fallback surface when the log is too short to drag,
/// and a press on it ends a selection too: the same handler is connected to the bar, because the bar keeps Control's
/// default <c>MOUSE_FILTER_STOP</c> and stops that press before the container's hook can see it.</item>
/// </list>
///
/// <para>Attaching per view is deliberate: <c>Refresh()</c> drops and rebuilds every row
/// (LanConnectBasicChatPanel.cs:1280-1303) each time the message fingerprint changes, so anything cached across a
/// refresh would point at a freed node. <c>_Ready</c> runs once per newly inserted row and is therefore the seam that
/// stays correct across rebuilds; the gesture holds the label only for as long as the label lives.</para>
///
/// <para>Desktop must not change at all: every patch body returns before writing a single property or connecting a
/// single callback, so text selection, reference clicks and every mouse behaviour stay exactly as upstream built
/// them.</para>
///
/// <para>The platform gate lives in the patch bodies and not in <see cref="Applies"/> on purpose: the smoke test drives
/// Applies()/Apply() in a plain .NET process with no Godot native layer, where <c>OS.HasFeature</c> is an access
/// violation rather than a managed exception. Applies() also never touches this: whether the repair is installable has
/// nothing to do with the platform it is installed on - the patches are installed on desktop too and simply decide per
/// call that they have nothing to do.</para>
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
    /// The original parameter name. Harmony binds a patch argument to the target parameter *by name*, so a rename
    /// would silently stop feeding the scroll bar in and the postfix would throw on every build instead of working;
    /// <see cref="Shape.TryCapture"/> therefore checks it.
    /// </summary>
    private const string ScrollBarParameterName = "scrollBar";

    /// <summary>Types the patch bodies need are resolved by name so no host <c>typeof</c> exists in this file</summary>
    private const string RichTextLabelTypeName = "Godot.RichTextLabel";

    private const string ScrollBarTypeName = "Godot.ScrollBar";

    private const string VoidTypeName = "System.Void";

    private const string StringTypeName = "System.String";

    /// <summary>
    /// 44 px — upstream's own minimum touch target (<c>LanConnectHudLegibility.MinTouchTargetPixels</c>,
    /// LanConnectHudLegibility.cs:13). Duplicated as a literal rather than reflected: a missing constant is not worth
    /// losing the scroll fix over, and 44 is the number every other touch control in the panel uses.
    /// </summary>
    private const float ScrollBarTouchWidth = 44f;

    /// <summary>
    /// Marks a view whose gesture is already attached. <c>_Ready</c> runs once per insertion into the tree, but a
    /// reparented node runs it again, and a second gesture would copy twice per press.
    /// </summary>
    internal const string GestureAttachedMetaName = "lobby_enhance_touch_selection_gesture";

    /// <summary>
    /// Marks a <c>ScrollContainer</c> that already reports its presses to <see cref="TouchSelectionSession"/>. Rows are
    /// rebuilt on every message change, so without it each rebuild would add another connection to the same container.
    /// </summary>
    private const string SelectionExitHookMetaName = "lobby_enhance_selection_exit_hook";

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
    /// Nothing platform-specific gates this: which anchors exist is answered by the host build, never by the
    /// device, so a desktop startup installs the same (inert) patches as a phone one.
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

        // Priority.First because "this press was already consumed by the copy" has to be answerable before anything
        // else decides to open the preview; the prefix has no parameters, so it cannot displace upstream's arguments.
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
    /// One freshly inserted message row. Runs after upstream's own <c>_Ready</c> (a postfix always does), which is
    /// the first moment the label holds rendered text.
    ///
    /// Two properties, one gesture and one observer on the enclosing scroll container per row; nothing is cached, because
    /// the row is freed and rebuilt by <c>Refresh()</c> (LanConnectBasicChatPanel.cs:1280-1303) whenever the message list
    /// changes.
    /// </summary>
    // ReSharper disable once UnusedParameter.Local — Harmony binds by name.
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
                // A row without its label cannot be made draggable; leave it exactly as it is rather than guessing.
                return;
            }

            // 1) Pass: the label keeps receiving GuiInput (upstream's own reference handling needs it) but the event
            //    is no longer consumed, so the chat ScrollContainer finally sees the drag.
            label.MouseFilter = Control.MouseFilterEnum.Pass;

            // 2) No selection: with emulate_mouse_from_touch the press would otherwise anchor a selection and the
            //    drag would extend it — the reported "drag selects text" symptom.
            label.SelectionEnabled = false;

            if (view.HasMeta(GestureAttachedMetaName))
            {
                return;
            }

            view.SetMeta(GestureAttachedMetaName, true);

            // The gesture reads the selected text from the label and pulses the view (its parent), so it outlives
            // nothing: the connection lives on the label and dies with it.
            new TouchSelectionGesture(label, view as CanvasItem).Attach();

            // A press anywhere in the message log has to be able to end a selection the row is showing, not only a press
            // that happens to land on that same row.
            AttachSelectionExitHook(view);
        }
        catch (Exception exception)
        {
            WarnOnce(ref _viewWarned, "could not make the chat text scrollable and selectable: " +
                                      exception.GetType().Name + ": " + exception.Message);
        }
    }

    /// <summary>
    /// Called by <c>LanConnectBasicChatPanel</c> while it styles the message scroll bar
    /// (LanConnectBasicChatPanel.cs:919, definition :3022-3036). Widening the bar is the backstop for phones where
    /// the drag above is still awkward (very short logs that do not overflow, for instance).
    ///
    /// <para><b>Only the lobby sidebar style goes through this method.</b> The call sits inside
    /// <c>if (UsesLobbyStyle)</c> (:917-920), so the in-run HUD style
    /// (<c>LanConnectChatVisualStyle.DarkOverlay</c>, read at :175) never gets a widened bar. That is expected, not a
    /// missed edit: the HUD panel draws its own thin bar, and there the drag fix above is what makes the log
    /// scrollable. Do not "fix" this by widening every scroll bar.</para>
    ///
    /// The parameter name has to match upstream's (<c>scrollBar</c>), because Harmony binds a patch argument to the
    /// target parameter by name; <see cref="Shape.TryCapture"/> fails the repair rather than binding to nothing.
    /// </summary>
    // ReSharper disable once InconsistentNaming — named after the original parameter, which Harmony matches by name.
    private static void ScrollBarStylePostfix(ScrollBar scrollBar)
    {
        try
        {
            if (!Platform.IsMobile || scrollBar == null || !GodotObject.IsInstanceValid(scrollBar))
            {
                return;
            }

            scrollBar.CustomMinimumSize = new Vector2(ScrollBarTouchWidth, 0f);

            // The bar keeps Control's default MOUSE_FILTER_STOP, so a press on it is stopped there and never reaches
            // the ScrollContainer: without this second connection a press on the bar would leave the highlight on screen.
            scrollBar.GuiInput += OnContainerGuiInput;

            // Its parent is the message ScrollContainer (LanConnectBasicChatPanel.cs:910 uses GetVScrollBar).
            AttachSelectionExitHook(scrollBar);
        }
        catch (Exception exception)
        {
            WarnOnce(ref _scrollBarWarned, "could not widen the message scroll bar: " + exception.GetType().Name +
                                           ": " + exception.Message);
        }
    }

    /// <summary>
    /// Connects one press observer to the nearest enclosing <c>ScrollContainer</c>, once per container. The hook only
    /// ends a live selection; it never accepts an event, so scrolling is untouched (a handler on <c>GuiInput</c> runs
    /// before the container's own handling, control.cpp:1852-1867).
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

                if (container.HasMeta(SelectionExitHookMetaName))
                {
                    return;
                }

                container.SetMeta(SelectionExitHookMetaName, true);
                container.GuiInput += OnContainerGuiInput;
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
    /// does clear it. Nothing is consumed: the container still scrolls exactly as before. Connected to the message
    /// ScrollContainer and to its scroll bar, which stops its own presses before the container sees them.
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
    /// Upstream's single funnel for "open the item/combat reference preview" on a message
    /// (<c>LanConnectRichMessageView.cs:273-280</c>): every activation path ends here, so one guard covers all of
    /// them. Returns <c>false</c> — skipping the original — for exactly one activation after a long press put a message
    /// into selection mode, when that message could have opened a preview on release.
    ///
    /// Anything else (no armed gate, a desktop build, a throwing body) falls through to <c>true</c>, i.e. upstream's
    /// behaviour is preserved: a plain tap on a reference still opens it.
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
            // Never swallow a real activation because our own guard broke.
            WarnOnce(ref _referenceWarned, "could not tell whether a long press consumed the reference click: " +
                                           exception.GetType().Name + ": " + exception.Message);
            return true;
        }
    }

    /// <summary>
    /// One warning per patch body, never one per message: the rows are rebuilt continuously, so an unguarded log here
    /// would flood the log file from a single broken build.
    /// </summary>
    private static void WarnOnce(ref int warned, string message)
    {
        if (Interlocked.Exchange(ref warned, 1) != 0)
        {
            return;
        }

        Entry.Warn("touch-chat-scroll: " + message);
    }

    /// <summary>
    /// This repair's own platform read, cached because <c>OS.HasFeature</c> crosses into Godot's native layer, which
    /// exists only inside the game: the patch bodies are the one place it is safe to call. Not a mod-wide answer - the
    /// Chat group reads the platform for its own gate.
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

                // Written before the call so a throwing first call cannot leave this re-entrant.
                _known = true;
                try
                {
                    _mobile = OS.HasFeature("mobile");
                }
                catch (Exception)
                {
                    // No native layer (an out-of-process harness): treat it as desktop, i.e. change nothing.
                    _mobile = false;
                }

                return _mobile;
            }
        }
    }

    /// <summary>
    /// "At most one message is in selection mode, and any new press anywhere must be able to end it."
    ///
    /// <para>The gesture cannot see a press that lands on a different row, and a highlight left on screen after the player
    /// moved on is worse than no highlight at all - so the owner is tracked here instead of inside the label. Every
    /// gesture asks <see cref="ExitAny"/> on a fresh press, and so does the message log's ScrollContainer through the hook
    /// <see cref="TouchChatScrollFix"/> connects (see <c>AttachSelectionExitHook</c>).</para>
    ///
    /// <para>Single-threaded like the gate below: Godot delivers input and runs both sides of this on the main
    /// thread.</para>
    /// </summary>
    internal static class TouchSelectionSession
    {
        private static TouchSelectionGesture? _active;

        /// <summary>Makes <paramref name="gesture"/> the one live selection, ending whatever was showing before.</summary>
        internal static void Begin(TouchSelectionGesture gesture)
        {
            TouchSelectionGesture? previous = _active;
            _active = gesture;
            if (previous != null && !ReferenceEquals(previous, gesture))
            {
                previous.ForceExit();
            }
        }

        /// <summary>Forgets <paramref name="gesture"/> if it is the live one; called when it leaves selection mode.</summary>
        internal static void End(TouchSelectionGesture gesture)
        {
            if (ReferenceEquals(_active, gesture))
            {
                _active = null;
            }
        }

        /// <summary>Ends the live selection, if any. Safe to call on every press: it is a no-op when nothing is shown.</summary>
        internal static void ExitAny()
        {
            TouchSelectionGesture? active = _active;
            _active = null;
            active?.ForceExit();
        }
    }

    /// <summary>
    /// "The long press that just fired already put this message into selection mode (and will copy it), so the
    /// reference preview it would have opened must not open."
    ///
    /// A flag rather than a timestamp on purpose: the press boundary is what makes the state unambiguous. The gesture
    /// arms it the moment selection mode starts - strictly before the release that could activate - and clears it on the
    /// next press, so the gate can never swallow an activation belonging to a *different* press: the later press clears it
    /// before it can activate anything, while a press whose selection produced no activation (a message with no references
    /// at all) leaves nothing behind that matters.
    ///
    /// Single-threaded by construction: Godot delivers input and runs both sides of this on the main thread, so no
    /// interlocking is needed. Written as three named operations because the two call sites are in different files.
    /// </summary>
    internal static class ReferenceActivationGate
    {
        private static bool _armed;

        /// <summary>Arms the gate for the current press. Called by the gesture when selection mode starts.</summary>
        internal static void Arm() => _armed = true;

        /// <summary>Forgets any armed gate. Called when a new press starts, before it can activate anything.</summary>
        internal static void Disarm() => _armed = false;

        /// <summary>True exactly once per armed press, and only for the caller that gets there first.</summary>
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
    /// Every member this repair touches, resolved once in <see cref="Applies"/> and never again. Metadata only: no
    /// <c>typeof</c> on a host type, nothing invoked while probing, no Godot call.
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

            // DeclaredOnly, not merely "found on this type": a lookup that accepts inherited members resolves
            // Godot.Node._Ready the moment upstream drops its override, and patching *that* would install this repair
            // into every node in the game while reporting itself as applied. "No anchor" is the only honest answer.
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

            // RaiseReferencePressed(string, LanConnectRichMessageReference) — the second parameter is a host type, so
            // arity plus the string first parameter identifies it without naming that type here.
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
        /// Resolves a method the type declares itself. <c>DeclaredOnly</c> is the whole point: <c>GetMethods</c> walks
        /// the base chain, and for lifecycle names like <c>_Ready</c> that silently substitutes
        /// <c>Godot.Node._Ready</c> — a patch that would then run for every node in the process.
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
                // Already reported by HostType; repeating it here would bury the real reason.
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
