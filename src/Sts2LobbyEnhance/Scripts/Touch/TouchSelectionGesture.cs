using Godot;

namespace Sts2LobbyEnhance;

/// <summary>
/// Long press on a chat message = enter text selection, drag the finger to shape the selection, lift the finger to
/// copy it. Installed on the message label of one row by <see cref="TouchChatScrollFix"/> and alive only as long as
/// that label is.
///
/// <para><b>Why the engine cannot do this on its own.</b> A plain finger drag must scroll the log, so the label is
/// built with <c>SelectionEnabled = false</c> and <c>MouseFilter = Pass</c> (see the panel-level explanation in
/// <see cref="TouchChatScrollFix"/>). With selection off, the press Godot emulates from a finger
/// (<c>emulate_mouse_from_touch</c>, input.cpp:847-884) is deliberately not turned into a selection anchor, so by the
/// time a long press is recognised there is no anchor to drag from. <c>RichTextLabel</c> exposes no positional
/// selection API either: the only public entry point is <c>select_all()</c>. The anchor therefore has to be produced by
/// the engine's own code path, which is <c>RichTextLabel::gui_input</c> handling a left mouse button press
/// (rich_text_label.cpp:2636-2674). That is what a synthetic press injected with <c>Viewport.PushInput</c> does below.
/// </para>
///
/// <para><b>Measured engine facts this file depends on</b> (Godot 4.5.1, probe <c>work/lead/touch-probe</c>, output in
/// <c>work/lead/fix-touch-select.md</c>):</para>
/// <list type="number">
/// <item>A synthetic <c>InputEventMouseButton</c> pushed through <c>Viewport.PushInput(event, true)</c> while
/// <c>SelectionEnabled</c> is true anchors the selection, and the next mouse motion extends it, because
/// <c>RichTextLabel::gui_input</c> extends the selection on any mouse motion while the press anchor is set
/// (rich_text_label.cpp:2940-3005). A finger drag arrives as exactly such a motion: each <c>InputEventScreenDrag</c> is
/// re-dispatched as an <c>InputEventMouseMotion</c> carrying the left button mask (input.cpp:895-913).</item>
/// <item>The injected press must carry <c>DEVICE_ID_INTERNAL</c> (-2): <c>Control::_call_gui_input</c> then skips the
/// <c>gui_input</c> signal and the script virtual, i.e. neither this gesture nor the host mod's own input handler sees
/// the event, while the engine's built-in <c>RichTextLabel::gui_input</c> still runs (control.cpp:1852-1867). That is
/// what makes the injection invisible to everything except the selection.</item>
/// <item>While <c>MouseFilter = Stop</c> the label is the mouse focus and swallows the motions, so the enclosing
/// <c>ScrollContainer</c> receives no motion and does not scroll (viewport.cpp:2079-2085, 2163-2165, 1755-1759). That
/// decouples "drag to select" from "drag to scroll" without touching the container.</item>
/// <item>Lifting the finger keeps the selection text (<c>GetSelectedText()</c> needs only <c>selection.active</c> and
/// <c>selection.enabled</c>, rich_text_label.cpp:6869-6872) and clears the drag anchor, so the copy point is the touch
/// release and the highlight can stay on screen as the copy feedback.</item>
/// <item>Turning <c>SelectionEnabled</c> off deselects (rich_text_label.cpp:6539-6553). That, plus setting
/// <c>MouseFilter</c> back to <c>Pass</c>, is the whole exit: it runs from the emulated mouse press that starts the next
/// gesture, in the signal phase before the engine's own press handling (control.cpp:1852-1867), so no new anchor is
/// made and the press still bubbles to the scroll container.</item>
/// </list>
///
/// <para><b>Immediate visible feedback.</b> Right after the anchor, <c>SelectAll()</c> highlights the whole message (a
/// long press that never moves therefore copies the whole message, as before, but now visibly selected), and the first
/// drag replaces that with the range the finger spans (probe: 491 chars -> 413 chars after one drag).</para>
///
/// <para><b>Why the press/selection split is decided by travel, not by a mode flag.</b> 16 px is upstream's own
/// tap/slide boundary (<c>LanConnectReferenceTapGesture.cs:9</c>), so a press that drifts is a scroll here exactly as it
/// is there; 400 ms is the added dimension.</para>
///
/// <para><b>A press that selected must not also open the item/combat reference preview.</b> Upstream decides "tap"
/// purely by travel (<c>LanConnectReferenceTapGesture.cs:54-55</c>), so a motionless 400 ms press is a tap to it and a
/// message carrying a reference activates on release (<c>LanConnectRichMessageView.cs:203-212</c>) - and that host
/// handler is connected to the label's <c>gui_input</c> before this gesture is, so it runs first. The gate is therefore
/// armed when selection mode starts, always before that release:
/// <see cref="TouchChatScrollFix.ReferenceActivationGate"/> eats exactly that one activation and the prefix on
/// <c>RaiseReferencePressed</c> applies it.</para>
///
/// This is deliberately a plain class and not a <c>Node</c>: Godot only dispatches <c>_Ready</c>/<c>_Process</c> to C#
/// classes that went through its source generator (script classes), and a class that a mod instantiates by hand has no
/// generated dispatch. Everything time-based here therefore rides on engine objects that do - a <c>SceneTreeTimer</c>
/// for the hold threshold, a <c>Tween</c> for the feedback pulse and <c>Callable</c> for the one deferred recovery -
/// which also means they die together with the row they belong to.
/// </summary>
internal sealed class TouchSelectionGesture
{
    /// <summary>How long the finger must stay down. The product decision behind the 2.1.0 request.</summary>
    private const double HoldSeconds = 0.4;

    /// <summary>The same number, for the release-time check that cannot use a timer.</summary>
    private const ulong HoldMilliseconds = 400;

    /// <summary>
    /// 16 px, the tap/slide boundary upstream already uses (LanConnectReferenceTapGesture.cs:9). Reused rather than
    /// re-invented so that "this is still a press" means the same thing everywhere in the chat.
    /// </summary>
    private const float MaxTravelSquared = 16f * 16f;

    /// <summary>Length of the copy feedback: the row brightens, then fades back over this long.</summary>
    private const double FeedbackSeconds = 0.12;

    /// <summary>
    /// <c>InputEvent::DEVICE_ID_INTERNAL</c>. Not exposed in C# (only <c>DEVICE_ID_EMULATION</c> is), and it is a fixed
    /// engine constant, so it is duplicated here rather than reflected. -1 would be the emulated-device id and would
    /// not be hidden from the host mod's input handler.
    /// </summary>
    private const int InternalDeviceId = -2;

    /// <summary>Written by <c>LanConnectRichMessageView</c> on the view itself (LanConnectRichMessageView.cs:84)</summary>
    private const string CopyTextMetaName = "lan_connect_copy_text";

    /// <summary>
    /// How many reference spans the message carries (LanConnectRichMessageView.cs:86). A message without one can never
    /// raise the preview, so arming the gate for it would only risk eating a later, unrelated activation.
    /// </summary>
    private const string ReferenceCountMetaName = "lan_connect_reference_count";

    private static readonly Color FeedbackModulate = new(1.35f, 1.35f, 1.35f, 1f);

    private static readonly Color DefaultModulate = new(1f, 1f, 1f, 1f);

    private static int _pressWarned;
    private static int _holdWarned;
    private static int _copyWarned;
    private static int _exitWarned;

    private readonly RichTextLabel _label;
    private readonly CanvasItem? _row;
    private readonly Color _rowModulate;

    /// <summary>Whether this message can open the reference preview at all; decides if a selection has to arm the gate.</summary>
    private readonly bool _raisesReference;

    private int? _touchIndex;
    private Vector2 _origin;
    private ulong _pressedAt;
    private bool _cancelled;
    private bool _copied;
    private bool _selecting;
    private bool _showingSelection;
    private bool? _deselectOnFocusLoss;
    private int _press;
    private Tween? _feedback;

    /// <param name="label">The message label; its <c>GuiInput</c> signal is what this subscribes to.</param>
    /// <param name="row">What gets brightened on a copy: the view that owns the label, or null to skip the pulse.</param>
    internal TouchSelectionGesture(RichTextLabel label, CanvasItem? row)
    {
        _label = label;
        _row = row;
        _rowModulate = row != null && GodotObject.IsInstanceValid(row) ? row.Modulate : DefaultModulate;
        _raisesReference = ReadReferenceCount(label) > 0;
    }

    /// <summary>
    /// Subscribes to the label. Called once per row, from the view's <c>_Ready</c> postfix, so the connection is
    /// dropped by Godot when either end is freed - including every row that <c>Refresh()</c> rebuilds
    /// (LanConnectBasicChatPanel.cs:1280-1303).
    /// </summary>
    internal void Attach() => _label.GuiInput += OnGuiInput;

    private void OnGuiInput(InputEvent input)
    {
        try
        {
            switch (input)
            {
                // The engine emulates this from the finger going down, and it arrives before the ScreenTouch
                // (input.cpp:882 then 983). Acting here - in the signal phase, before RichTextLabel's own handling
                // (control.cpp:1852-1867) - is what turns the next gesture into a scroll instead of a new selection.
                case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } press
                    when press.Device != InternalDeviceId:
                    ExitSelection();
                    break;
                case InputEventScreenTouch touch:
                    OnTouch(touch);
                    break;
                case InputEventScreenDrag drag:
                    OnDrag(drag);
                    break;
            }
        }
        catch (Exception exception)
        {
            WarnOnce(ref _pressWarned, "reading the press on a message failed: " + exception.GetType().Name + ": " +
                                       exception.Message);
        }
    }

    private void OnTouch(InputEventScreenTouch touch)
    {
        if (!touch.Pressed)
        {
            if (_touchIndex != touch.Index)
            {
                return;
            }

            _touchIndex = null;

            if (_selecting)
            {
                // The finger that shaped the selection is up: this is the copy point. The engine has already run its
                // own release handling (input.cpp:882 before 983), i.e. the drag anchor is cleared and the selected
                // text is final (rich_text_label.cpp:2721-2753, 6869-6872).
                FinishSelection();
                return;
            }

            // Same gesture, judged on release: covers a hold whose timer never fired (a tree that does not tick, a row
            // built while the finger was already down). Deferred, so the synthetic press it needs is never delivered
            // from inside an input dispatch.
            if (!_cancelled &&
                !_copied &&
                Time.GetTicksMsec() - _pressedAt >= HoldMilliseconds &&
                touch.Position.DistanceSquaredTo(_origin) <= MaxTravelSquared)
            {
                DeferredSelectAndCopy(_origin);
            }

            return;
        }

        if (touch.Canceled)
        {
            _touchIndex = null;
            _cancelled = true;
            return;
        }

        if (_touchIndex.HasValue)
        {
            // A second finger is never a selection gesture (same rule as LanConnectReferenceTapGesture.cs:23-27).
            _cancelled = true;
            return;
        }

        // A fresh press also ends a selection shown by an earlier one, in case the emulated mouse press above never
        // reached us (a build with emulate_mouse_from_touch off).
        ExitSelection();

        _touchIndex = touch.Index;
        _origin = touch.Position;
        _pressedAt = Time.GetTicksMsec();
        _cancelled = false;
        _copied = false;
        _selecting = false;

        // This press has not copied anything yet, so it must not inherit the previous press's suppression. Clearing
        // here (before anything can activate) is what bounds the gate to one press.
        TouchChatScrollFix.ReferenceActivationGate.Disarm();
        ArmHoldTimer(++_press);
    }

    private void OnDrag(InputEventScreenDrag drag)
    {
        if (_touchIndex != drag.Index)
        {
            return;
        }

        // While selecting, travel is the point: the engine extends the selection from the injected anchor and the
        // label swallows the motion, so the log does not scroll underneath the finger.
        if (_selecting)
        {
            return;
        }

        if (drag.Position.DistanceSquaredTo(_origin) > MaxTravelSquared)
        {
            // Sticky on purpose, like upstream's own travel limit (LanConnectReferenceTapGesture.cs:43): dragging away
            // and back is a scroll that happened to end where it started, not a press.
            _cancelled = true;
        }
    }

    /// <summary>
    /// The 400 ms mark, on an engine timer rather than a frame counter: the chat is often open over a paused tree, and
    /// <c>CreateTimer</c>'s default <c>processAlways</c> keeps counting there.
    /// </summary>
    private void ArmHoldTimer(int press)
    {
        SceneTree? tree = _label.GetTree();
        if (tree == null)
        {
            // Not in the tree: the release-time check still covers the gesture.
            return;
        }

        SceneTreeTimer timer = tree.CreateTimer(HoldSeconds);
        timer.Timeout += () => OnHoldElapsed(press);
    }

    private void OnHoldElapsed(int press)
    {
        try
        {
            if (press != _press || !_touchIndex.HasValue || _cancelled || _copied || _selecting)
            {
                return;
            }

            if (!GodotObject.IsInstanceValid(_label))
            {
                return;
            }

            EnterSelection(_origin);
        }
        catch (Exception exception)
        {
            WarnOnce(ref _holdWarned, "timing the long press failed: " + exception.GetType().Name + ": " +
                                      exception.Message);
        }
    }

    /// <summary>
    /// Turns the row into a text selection: anchor at the finger, highlight the message, and stop the label from
    /// passing the drag on to the scroll container.
    /// </summary>
    private void EnterSelection(Vector2 labelLocal)
    {
        if (_selecting)
        {
            return;
        }

        _selecting = true;
        _showingSelection = true;

        // The release that ends this press would otherwise open the reference preview (upstream decides "tap" by travel
        // alone and its own handler runs before this one, LanConnectRichMessageView.cs:203-212).
        if (_raisesReference)
        {
            TouchChatScrollFix.ReferenceActivationGate.Arm();
        }

        // A selection can be dropped by a focus change (rich_text_label.cpp:2603-2607) and GetSelectedText() would then
        // be empty at the copy point, so hold on to it for the duration; the previous value is restored on exit.
        _deselectOnFocusLoss = _label.DeselectOnFocusLossEnabled;
        _label.DeselectOnFocusLossEnabled = false;

        _label.SelectionEnabled = true;
        _label.MouseFilter = Control.MouseFilterEnum.Stop;

        InjectAnchorPress(labelLocal);
        _label.SelectAll();

        TouchChatScrollFix.TouchSelectionSession.Begin(this);
    }

    /// <summary>Finger up after selecting: copy the selected text and keep the highlight as the visible feedback.</summary>
    private void FinishSelection()
    {
        _selecting = false;
        CopySelection();

        if (GodotObject.IsInstanceValid(_label))
        {
            // No finger is down any more, so the label must stop blocking the pointer: the next touch scrolls again (it
            // also ends this selection, see ExitSelection). The highlight stays visible on purpose.
            _label.MouseFilter = Control.MouseFilterEnum.Pass;
        }
    }

    /// <summary>
    /// The recovery path for a hold whose timer never fired: the finger is already up, so the anchor press and the copy
    /// are deferred to the next idle frame instead of being pushed from inside the input dispatch.
    /// </summary>
    private void DeferredSelectAndCopy(Vector2 labelLocal)
    {
        Callable.From(() =>
        {
            try
            {
                if (!GodotObject.IsInstanceValid(_label) || _touchIndex.HasValue || _copied || _cancelled)
                {
                    return;
                }

                EnterSelection(labelLocal);
                _selecting = false;
                CopySelection();

                // No finger will extend this selection: drop the anchor again so a later stray motion cannot reshape the
                // highlight that is now feedback rather than input.
                InjectAnchorRelease(labelLocal);
                if (GodotObject.IsInstanceValid(_label))
                {
                    _label.MouseFilter = Control.MouseFilterEnum.Pass;
                }
            }
            catch (Exception exception)
            {
                WarnOnce(ref _holdWarned, "recovering a long press without a timer failed: " +
                                          exception.GetType().Name + ": " + exception.Message);
            }
        }).CallDeferred();
    }

    /// <summary>
    /// Leaves selection mode. Called for the emulated press that starts the next gesture, so it runs before the engine's
    /// own press handling: no new anchor is made, and the press keeps bubbling to the chat scroll container, which means
    /// the very first drag after a copy scrolls again (probe step S7: ScrollVertical 160 -> 130).
    /// </summary>
    internal void ExitSelection()
    {
        try
        {
            if (!_showingSelection)
            {
                return;
            }

            _showingSelection = false;
            TouchChatScrollFix.TouchSelectionSession.End(this);

            if (!GodotObject.IsInstanceValid(_label))
            {
                return;
            }

            // A second finger can end a selection that is still being shaped, i.e. while this press's engine anchor is
            // still set. Nothing is injected to clear it (a synthetic event pushed from inside an input dispatch would
            // re-enter the viewport's gui state); the engine clears the anchor with the release of that same finger, and
            // this press is cancelled so no copy and no gate arming survives it. The one visible residue is described in
            // the report: if that release never arrives, RichTextLabel can repaint a highlight on a later motion
            // (rich_text_label.cpp:2940 does not check selection.enabled) until the next press.
            if (_selecting)
            {
                _selecting = false;
                _cancelled = true;
            }

            if (_deselectOnFocusLoss is { } restore)
            {
                _deselectOnFocusLoss = null;
                _label.DeselectOnFocusLossEnabled = restore;
            }

            _label.MouseFilter = Control.MouseFilterEnum.Pass;
            _label.SelectionEnabled = false; // deselects (rich_text_label.cpp:6539-6553)
            _label.Deselect();
        }
        catch (Exception exception)
        {
            WarnOnce(ref _exitWarned, "leaving the text selection failed: " + exception.GetType().Name + ": " +
                                      exception.Message);
        }
    }

    /// <summary>Forces the selection out of the way, whatever state it is in.</summary>
    internal void ForceExit()
    {
        _cancelled = true;
        ExitSelection();
    }

    /// <summary>
    /// Pushes the one event that gives the label a selection anchor: a left mouse button press at the finger.
    ///
    /// <para><c>DEVICE_ID_INTERNAL</c> is what keeps it invisible to every C# handler - the host mod's
    /// <c>OnGuiInput</c> included - while leaving the engine's own <c>RichTextLabel::gui_input</c> running
    /// (control.cpp:1852-1867). The position is passed already converted to the viewport's space, hence
    /// <c>localCoords</c> true.</para>
    /// </summary>
    private void InjectAnchorPress(Vector2 labelLocal) => Inject(labelLocal, pressed: true);

    private void InjectAnchorRelease(Vector2 labelLocal) => Inject(labelLocal, pressed: false);

    private void Inject(Vector2 labelLocal, bool pressed)
    {
        if (!GodotObject.IsInstanceValid(_label))
        {
            return;
        }

        Viewport? viewport = _label.GetViewport();
        if (viewport == null)
        {
            return;
        }

        Vector2 position = _label.GetGlobalTransformWithCanvas() * labelLocal;
        var press = new InputEventMouseButton
        {
            Device = InternalDeviceId,
            ButtonIndex = MouseButton.Left,
            Pressed = pressed,
            DoubleClick = false,
            Position = position,
            GlobalPosition = position,
            ButtonMask = pressed ? MouseButtonMask.Left : 0,
        };
        viewport.PushInput(press, true);
    }

    /// <summary>
    /// The clipboard write. Reads the selected text first: <c>get_selected_text()</c> returns an empty string once
    /// selection is disabled (rich_text_label.cpp:6869-6872), so the text has to be taken while the selection is still
    /// live - which is exactly the moment the finger comes up.
    /// </summary>
    private void CopySelection()
    {
        if (_copied || !GodotObject.IsInstanceValid(_label))
        {
            return;
        }

        string text = _label.GetSelectedText();
        if (string.IsNullOrEmpty(text))
        {
            // The highlight is on screen but the engine reports nothing selected: ask for the whole message instead of
            // copying nothing. Only reachable if the selection was dropped between anchor and lift.
            _label.SelectAll();
            text = _label.GetSelectedText();
        }

        if (string.IsNullOrEmpty(text))
        {
            WarnOnce(ref _copyWarned, "the selection was empty when the finger came up; nothing was copied.");
            return;
        }

        _copied = true;
        DisplayServer.ClipboardSet(text);
        Pulse();
    }

    /// <summary>
    /// Upstream publishes how many spans carry a reference (LanConnectRichMessageView.cs:86); zero means no activation
    /// can follow this press at all. Read once per row instead of per press: the meta is written in the view's
    /// constructor and never changes.
    /// </summary>
    private static int ReadReferenceCount(RichTextLabel label)
    {
        Node? view = label.GetParent();
        if (view == null || !GodotObject.IsInstanceValid(view) || !view.HasMeta(ReferenceCountMetaName))
        {
            return 0;
        }

        return view.GetMeta(ReferenceCountMetaName, default).AsInt32();
    }

    /// <summary>
    /// One brightening per copy. The previous pulse is killed first so two quick presses cannot leave the row bright
    /// (two live tweens on <c>modulate</c> resolve in the wrong order otherwise).
    /// </summary>
    private void Pulse()
    {
        if (_row == null || !GodotObject.IsInstanceValid(_row))
        {
            return;
        }

        try
        {
            if (_feedback != null && GodotObject.IsInstanceValid(_feedback) && _feedback.IsValid())
            {
                _feedback.Kill();
            }
        }
        catch (Exception)
        {
            // A finished tween is already gone; nothing to stop.
        }

        _row.Modulate = FeedbackModulate;
        Tween tween = _row.CreateTween();
        tween.TweenProperty(_row, "modulate", _rowModulate, FeedbackSeconds);
        _feedback = tween;
    }

    /// <summary>
    /// One warning per call site, never one per message: the rows are rebuilt continuously, so an unguarded log here
    /// would flood the log file from a single broken build.
    /// </summary>
    private static void WarnOnce(ref int warned, string message)
    {
        if (Interlocked.Exchange(ref warned, 1) != 0)
        {
            return;
        }

        Entry.Warn("touch-chat-select: " + message);
    }
}
