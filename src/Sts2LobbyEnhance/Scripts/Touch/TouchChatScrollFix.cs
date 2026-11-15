using System.Reflection;
using Godot;
using HarmonyLib;

namespace Sts2LobbyEnhance;

/// <summary>
/// Makes the chat log scroll with a finger on phones, and turns a 400 ms press into "copy this message".
///
/// Why the phone cannot scroll today (engine level, not styling): every message row is a
/// <c>LanConnectRichMessageView</c> whose <c>MegaRichTextLabel</c> is built with <c>SelectionEnabled = true</c>
/// (LanConnectRichMessageView.cs:52) and <c>MouseFilter = Stop</c> (:55). Godot swallows a pointer event at a
/// MOUSE_FILTER_STOP control, so the enclosing <c>ScrollContainer</c> (LanConnectBasicChatPanel.cs:891-900) never
/// sees the drag; and because <c>emulate_mouse_from_touch</c> is on by default the same press lands on the label as
/// a selection anchor and the drag extends that selection. That is exactly the reported behaviour: a finger drag
/// selects text, and only the right-most sliver (the scroll bar itself) scrolls.
///
/// What this repair does, and only when <c>OS.HasFeature("mobile")</c> says so at runtime:
/// <list type="number">
/// <item><c>SelectionEnabled = false</c> + <c>MouseFilter = Pass</c> on that label (:52, :55). Pass still delivers
/// GuiInput to the label, but the event keeps bubbling towards the ScrollContainer, which is the component that
/// implements drag scrolling — upstream expects exactly that, see its own comment at
/// LanConnectRichMessageView.cs:214 ("Leave presses/drags available to the enclosing chat scroll container"), which
/// today cannot hold because of :55.</item>
/// <item>One <see cref="LongPressCopyGesture"/> per view: a 400 ms hold within 16 px copies the whole message using
/// the string upstream already assembles for Ctrl+C (meta <c>lan_connect_copy_text</c>,
/// LanConnectRichMessageView.cs:84). 16 px is upstream's own tap/slide boundary
/// (LanConnectReferenceTapGesture.cs:9), so a press that copies and a press that merely taps are decided by the
/// same threshold the mod uses everywhere else. No partial selection is implemented on purpose: synthesising the
/// drag that a text selection needs is a much larger change than this feature is worth.</item>
/// <item>The message scroll bar grows to the 44 px touch floor upstream already uses for buttons
/// (LanConnectHudLegibility.cs:13) through a postfix on <c>ApplyLobbyScrollBarStyle</c>
/// (LanConnectBasicChatPanel.cs:3022-3036, called at :919). It is a backstop for the sliver that used to be the
/// only reachable scroll surface.</item>
/// </list>
///
/// Attaching per view is deliberate: <c>Refresh()</c> drops and rebuilds every row
/// (LanConnectBasicChatPanel.cs:1280-1303) each time the message fingerprint changes, so anything cached across a
/// refresh would point at a freed node. <c>_Ready</c> runs once per newly inserted row and is therefore the seam
/// that stays correct across rebuilds; the gesture holds the label only for as long as the label lives.
///
/// Desktop must not change at all: both postfixes return before writing a single property or connecting a single
/// callback, so text selection and every mouse behaviour stay exactly as upstream built them.
///
/// The platform gate lives in the patch bodies and not in <see cref="Applies"/> on purpose: the smoke test drives
/// Applies()/Apply() in a plain .NET process with no Godot native layer, where <c>OS.HasFeature</c> is an access
/// violation rather than a managed exception. Applies() also never touches this because whether the repair is
/// installable has nothing to do with the platform it is installed on — the patches are installed on desktop too and
/// simply decide per call that they have nothing to do.
/// </summary>
internal sealed class TouchChatScrollFix : ICompatFix
{
    private const string ViewTypeName = "Sts2LanConnect.Scripts.LanConnectRichMessageView";
    private const string ViewReadyMethodName = "_Ready";
    private const string ViewLabelFieldName = "_label";

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
    internal const string GestureAttachedMetaName = "lobby_enhance_long_press_copy_gesture";

    private static Shape? _shape;

    private static int _viewWarned;
    private static int _scrollBarWarned;

    public string Id => "touch-chat-scroll";

    /// <summary>
    /// The only repair that reads the platform, and the only one that patches the chat panel's message rows: a shape
    /// change in either rolls back this repair and nothing else
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

        Entry.Info(
            "touch-chat-scroll: patched " + ViewTypeName + "." + ViewReadyMethodName + " and " + PanelTypeName + "." +
            ScrollBarStyleMethodName + ". On a mobile build the message labels stop capturing the pointer (a drag " +
            "scrolls the log) and a 400 ms press copies the message; on desktop every patch body returns without " +
            "touching a property, so selection and dragging stay as upstream built them.");
    }

    /// <summary>
    /// One freshly inserted message row. Runs after upstream's own <c>_Ready</c> (a postfix always does), which is
    /// the first moment the label holds rendered text.
    ///
    /// Two properties and one signal connection per row; nothing is cached, because the row is freed and rebuilt by
    /// <c>Refresh()</c> (LanConnectBasicChatPanel.cs:1280-1303) whenever the message list changes.
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

            // The gesture reads the copy text from the view (its parent) and pulses the view, so it outlives nothing:
            // the connection lives on the label and dies with it.
            new LongPressCopyGesture(label, view as CanvasItem).Attach();
        }
        catch (Exception exception)
        {
            WarnOnce(ref _viewWarned, "could not make the chat text draggable: " + exception.GetType().Name + ": " +
                                      exception.Message);
        }
    }

    /// <summary>
    /// Called by <c>LanConnectBasicChatPanel</c> while it styles the message scroll bar
    /// (LanConnectBasicChatPanel.cs:919, definition :3022-3036). Widening the bar is the backstop for phones where
    /// the drag above is still awkward (very short logs that do not overflow, for instance).
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
        }
        catch (Exception exception)
        {
            WarnOnce(ref _scrollBarWarned, "could not widen the message scroll bar: " + exception.GetType().Name +
                                           ": " + exception.Message);
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
    /// The mod's only platform read, and the reason it is a property with a cache: <c>OS.HasFeature</c> crosses into
    /// Godot's native layer, which exists only inside the game. A patch body is the only place that is guaranteed.
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
    /// Every member this repair touches, resolved once in <see cref="Applies"/> and never again. Metadata only: no
    /// <c>typeof</c> on a host type, nothing invoked while probing, no Godot call.
    /// </summary>
    internal sealed class Shape
    {
        private const string VoidTypeName = "System.Void";

        private Shape(MethodInfo viewReady, FieldInfo label, MethodInfo scrollBarStyle)
        {
            ViewReady = viewReady;
            Label = label;
            ScrollBarStyle = scrollBarStyle;
        }

        internal MethodInfo ViewReady { get; }

        internal FieldInfo Label { get; }

        internal MethodInfo ScrollBarStyle { get; }

        internal static Shape? TryCapture(Assembly host, out string reason)
        {
            var missing = new List<string>();

            Type? view = ReflectScope.HostType(host, ViewTypeName, missing);
            Type? panel = ReflectScope.HostType(host, PanelTypeName, missing);

            // Arity alone is not enough: the body reads the label as a RichTextLabel (SelectionEnabled lives there,
            // not on Control), so a re-typed field has to cost this anchor instead of throwing inside the game.
            MethodInfo? viewReady = ReflectScope.Exact(
                view,
                ViewReadyMethodName,
                requireStatic: false,
                Array.Empty<string>(),
                VoidTypeName,
                missing);

            FieldInfo? label = ReflectScope.Field(missing, view, ViewLabelFieldName);
            if (label != null && !InheritsFrom(label.FieldType, RichTextLabelTypeName))
            {
                missing.Add(
                    ViewTypeName + "." + ViewLabelFieldName + " is " + label.FieldType.Name +
                    ", expected a Godot.RichTextLabel");
            }

            MethodInfo? scrollBarStyle = ReflectScope.Exact(
                panel,
                ScrollBarStyleMethodName,
                requireStatic: true,
                new[] { ScrollBarTypeName },
                VoidTypeName,
                missing);

            ReflectScope.RequireParameterNames(missing, scrollBarStyle, ScrollBarParameterName);

            if (missing.Count > 0)
            {
                reason = "missing: " + string.Join(", ", missing);
                return null;
            }

            reason = string.Empty;
            return new Shape(viewReady!, label!, scrollBarStyle!);
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
