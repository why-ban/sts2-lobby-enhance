using Godot;

namespace Sts2LobbyEnhance;

/// <summary>
/// Long press on a chat message enters text selection, dragging shapes it, lifting copies it. Installed on one row's
/// label by <see cref="TouchChatScrollFix"/>, and its lifetime is that label's
///
/// A plain class rather than a <c>Node</c>: Godot dispatches <c>_Ready</c>/<c>_Process</c> to script classes, and a class
/// a mod instantiates by hand is not one. The timing therefore rides on a <c>SceneTreeTimer</c>, a <c>Tween</c> and a
/// <c>Callable</c>, which also die with the row
/// </summary>
internal sealed class TouchSelectionGesture
{
    /// <summary>400 ms is the product decision, not a size the engine defines</summary>
    private const double HoldSeconds = 0.4;

    private const ulong HoldMilliseconds = 400;

    /// <summary>
    /// 16 px, the tap/slide boundary upstream already uses, so "this is still a press" means the same thing in both
    /// gesture handlers
    /// </summary>
    private const float MaxTravelSquared = 16f * 16f;

    private const double FeedbackSeconds = 0.12;

    /// <summary>
    /// <c>InputEvent::DEVICE_ID_INTERNAL</c>, not exposed in C# (only <c>DEVICE_ID_EMULATION</c> is) and a fixed engine
    /// constant, so duplicated here. -1 is the emulated device id and would still reach the host mod's handler
    /// </summary>
    private const int InternalDeviceId = -2;

    /// <summary>
    /// How many reference spans the message carries. A message without one cannot raise the preview, so arming the gate
    /// for it would only risk eating a later, unrelated activation
    /// </summary>
    private const string ReferenceCountMetaName = "lan_connect_reference_count";

    private static readonly Color FeedbackModulate = new(1.35f, 1.35f, 1.35f, 1f);

    private static readonly Color DefaultModulate = new(1f, 1f, 1f, 1f);

    private static int _pressWarned;
    private static int _holdWarned;
    private static int _recoverWarned;
    private static int _copyWarned;
    private static int _exitWarned;

    private readonly RichTextLabel _label;
    private readonly CanvasItem? _row;
    private readonly Color _rowModulate;
    private readonly bool _raisesReference;

    private Control.FocusModeEnum? _focusModeBeforeSelection;
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

    internal TouchSelectionGesture(RichTextLabel label, CanvasItem? row)
    {
        _label = label;
        _row = row;
        _rowModulate = row != null && GodotObject.IsInstanceValid(row) ? row.Modulate : DefaultModulate;
        _raisesReference = ReadReferenceCount(label) > 0;
    }

    internal void Attach() => _label.GuiInput += OnGuiInput;

    private void OnGuiInput(InputEvent input)
    {
        try
        {
            switch (input)
            {
                // The engine emulates this from the finger going down and it arrives before the ScreenTouch, so acting
                // here, in the signal phase before RichTextLabel's own handling, is what turns the next gesture into a
                // scroll instead of a new selection
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
                // The finger that shaped the selection is up, so this is the copy point. The engine already ran its own
                // release handling, which cleared the drag anchor and left the selected text final
                FinishSelection();
                return;
            }

            // Same gesture judged on release, which covers a hold whose timer did not fire: a tree that does not tick, or
            // a row built while the finger was already down. Deferred so the synthetic press it needs is not delivered
            // from inside an input dispatch
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
            // A second finger is not part of a selection gesture, same rule as upstream's own tap gesture
            _cancelled = true;
            return;
        }

        // A fresh press also ends a selection shown by an earlier one, in case the emulated mouse press above did not
        // reach us: a build with emulate_mouse_from_touch off
        ExitSelection();

        _touchIndex = touch.Index;
        _origin = touch.Position;
        _pressedAt = Time.GetTicksMsec();
        _cancelled = false;
        _copied = false;
        _selecting = false;

        // This press has not copied anything, so it does not inherit the previous press's suppression. Clearing it here,
        // before anything can activate, is what bounds the gate to one press
        TouchChatScrollFix.ReferenceActivationGate.Disarm();
        ArmHoldTimer(++_press);
    }

    private void OnDrag(InputEventScreenDrag drag)
    {
        if (_touchIndex != drag.Index)
        {
            return;
        }

        // While selecting, travel is the point: the engine extends the selection from the injected anchor and the label
        // swallows the motion, so the log does not scroll underneath the finger
        if (_selecting)
        {
            return;
        }

        if (drag.Position.DistanceSquaredTo(_origin) > MaxTravelSquared)
        {
            // Sticky on purpose, like upstream's own travel limit: dragging away and back is a scroll that happened to end
            // where it started, not a press
            _cancelled = true;
        }
    }

    /// <summary>
    /// On an engine timer rather than a frame counter: the chat is often open over a paused tree, and <c>CreateTimer</c>
    /// keeps counting there by default
    /// </summary>
    private void ArmHoldTimer(int press)
    {
        SceneTree? tree = _label.GetTree();
        if (tree == null)
        {
            // Not in the tree, where the release-time check still covers the gesture
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

    private void EnterSelection(Vector2 labelLocal)
    {
        if (_selecting)
        {
            return;
        }

        _selecting = true;
        _showingSelection = true;

        // The release that ends this press would otherwise open the reference preview, because upstream decides "tap" by
        // travel alone and its own handler runs before this one
        if (_raisesReference)
        {
            TouchChatScrollFix.ReferenceActivationGate.Arm();
        }

        // A focus change can drop the selection, and GetSelectedText() would then be empty at the copy point, so hold on
        // to it for the duration and restore the previous value on exit
        _deselectOnFocusLoss = _label.DeselectOnFocusLossEnabled;
        _label.DeselectOnFocusLossEnabled = false;

        // Read before enabling selection, which forces the focus mode to All
        _focusModeBeforeSelection = _label.FocusMode;
        _label.SelectionEnabled = true;
        _label.MouseFilter = Control.MouseFilterEnum.Stop;

        // Not focusable while this press is anchored: the injected press below would otherwise hand the label keyboard
        // focus, and the theme draws that focus ring as an outline a couple of pixels OUTSIDE the label's rect. Nothing
        // above the label clips it (a Control's clip contains its children, not its own drawing), so on touch, where the
        // row sits flush with the message area and with the panel's edge, the ring lands outside both — the "the
        // highlight draws over the input box" report. Selection does not need focus: the anchor comes from the synthetic
        // press and DeselectOnFocusLoss is off for the duration
        _label.FocusMode = Control.FocusModeEnum.None;

        InjectAnchorPress(labelLocal);
        _label.SelectAll();

        TouchChatScrollFix.TouchSelectionSession.Begin(this);
    }

    /// <summary>Finger up after selecting: copy the text and keep the highlight on screen as the feedback</summary>
    private void FinishSelection()
    {
        _selecting = false;
        CopySelection();

        if (GodotObject.IsInstanceValid(_label))
        {
            // No finger is down any more, so the label stops blocking the pointer and the next touch scrolls again, and
            // also ends this selection. The highlight stays visible on purpose
            _label.MouseFilter = Control.MouseFilterEnum.Pass;
        }
    }

    /// <summary>
    /// The recovery path for a hold whose timer did not fire. The finger is already up, so the anchor press and the copy
    /// are deferred to the next idle frame rather than pushed from inside the input dispatch
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

                // No finger will extend this selection, so drop the anchor again: a later stray motion would otherwise
                // reshape a highlight that is feedback rather than input
                InjectAnchorRelease(labelLocal);
                if (GodotObject.IsInstanceValid(_label))
                {
                    _label.MouseFilter = Control.MouseFilterEnum.Pass;
                }
            }
            catch (Exception exception)
            {
                WarnOnce(ref _recoverWarned, "recovering a long press without a timer failed: " +
                                             exception.GetType().Name + ": " + exception.Message);
            }
        }).CallDeferred();
    }

    /// <summary>
    /// Leaves selection mode. Runs on the emulated press that starts the next gesture, which is the signal phase, before
    /// the engine's own press handling: no new anchor is made and the press still bubbles to the chat scroll container, so
    /// the first drag after a copy scrolls (probe: scroll 160 -> 130 on that drag)
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
            // still set. Nothing is injected to clear it, because a synthetic event pushed from inside an input dispatch
            // re-enters the viewport's gui state. The engine clears the anchor when that finger is released, and this
            // press is cancelled so no copy and no armed gate survive it. Known residue: if that release does not arrive,
            // RichTextLabel can repaint a highlight on a later motion, since the motion branch does not check
            // selection.enabled, until the next press
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

            if (_focusModeBeforeSelection is { } restoreFocus)
            {
                _focusModeBeforeSelection = null;
                _label.FocusMode = restoreFocus;
            }

            _label.MouseFilter = Control.MouseFilterEnum.Pass;
            _label.SelectionEnabled = false; // turning selection off also deselects
            _label.Deselect();
        }
        catch (Exception exception)
        {
            WarnOnce(ref _exitWarned, "leaving the text selection failed: " + exception.GetType().Name + ": " +
                                      exception.Message);
        }
    }

    internal void ForceExit()
    {
        _cancelled = true;
        ExitSelection();
    }

    /// <summary>
    /// Pushes the one event that gives the label a selection anchor, a left mouse button press at the finger
    ///
    /// <c>DEVICE_ID_INTERNAL</c> keeps the <c>gui_input</c> signal and the script virtual quiet, the host mod's
    /// <c>OnGuiInput</c> included, while the engine's own <c>RichTextLabel::gui_input</c> still runs. The event is not
    /// invisible elsewhere: it goes through <c>Viewport.PushInput</c>, so the engine's <c>_input</c> stage sees it first,
    /// and the position is already in the viewport's space, hence <c>localCoords</c> true
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
    /// <c>get_selected_text()</c> returns an empty string once selection is disabled, so the text has to be read while the
    /// selection is live, which is the moment the finger comes up
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
            // The highlight is on screen but the engine reports nothing selected, so ask for the whole message rather than
            // copy nothing. Reachable if the selection was dropped between anchor and lift
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
    /// Read once per row rather than per press, because the meta is written in the view's constructor and does not change
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
    /// One brightening per copy. The previous pulse is killed first because two live tweens on <c>modulate</c> resolve in
    /// the wrong order
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
            // A finished tween is already gone, so nothing to stop
        }

        _row.Modulate = FeedbackModulate;
        Tween tween = _row.CreateTween();
        tween.TweenProperty(_row, "modulate", _rowModulate, FeedbackSeconds);
        _feedback = tween;
    }

    /// <summary>
    /// One warning per call site. The rows are rebuilt continuously, so an unguarded log here would flood the log file
    /// from a single broken build
    /// </summary>
    private static void WarnOnce(ref int warned, string message)
    {
        if (Interlocked.Exchange(ref warned, 1) != 0)
        {
            return;
        }

        Entry.Warn("touch-chat-scroll: " + message);
    }
}
