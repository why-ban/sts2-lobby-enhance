using Godot;

namespace Sts2LobbyEnhance;

/// <summary>
/// On a phone the control under the finger owns the whole gesture: the viewport hands it the press and every later
/// drag, so a Button row turns a swipe into a click and the ScrollContainer above it never sees the movement. This
/// scrolls from those same events and reports the drag back, so the activation that ends it can be dropped
///
/// The scroll is driven here rather than left to the container because the container's own drag-scroll is switched off
/// on anything that does not report a touchscreen, and because a row that stops the pointer never gives it the press
/// it would need to start
/// </summary>
internal sealed class TouchDragScroll
{
    /// <summary>
    /// 16 px, the tap/slide boundary the chat's long-press gesture already uses, so "this is still a press" means the
    /// same thing on both surfaces. A jittering finger therefore still activates, a real swipe never does
    /// </summary>
    private const float DragThreshold = 16f;

    private const int MouseIndex = -1;

    private static int _attachWarned;
    private static int _inputWarned;

    private readonly Control _control;
    private readonly ScrollContainer? _fixedTarget;

    private ScrollContainer? _target;
    private Vector2 _origin;
    private float _startScroll;
    private int _index = int.MinValue;
    private bool _tracking;
    private bool _dragging;
    private bool _suppress;

    private TouchDragScroll(Control control, ScrollContainer? fixedTarget)
    {
        _control = control;
        _fixedTarget = fixedTarget;
    }

    /// <summary>
    /// Screens a button and the scroll it should move. A null <paramref name="target"/> follows the nearest enclosing
    /// ScrollContainer at press time, which is what the one dialog scroll and the one sidebar scroll need
    /// </summary>
    internal static TouchDragScroll? Attach(Control control, ScrollContainer? target = null)
    {
        try
        {
            if (!GodotObject.IsInstanceValid(control))
            {
                return null;
            }

            var drag = new TouchDragScroll(control, target);
            control.GuiInput += drag.OnGuiInput;
            return drag;
        }
        catch (Exception exception)
        {
            WarnOnce(ref _attachWarned, "could not watch a button for touch drags: " + exception.GetType().Name + ": " +
                                        exception.Message);
            return null;
        }
    }

    /// <summary>
    /// True for the one activation that closes a drag, so the caller can drop it. A tap and a finger that wobbles
    /// inside the threshold both stay false
    /// </summary>
    internal bool ConsumeActivation()
    {
        bool suppressed = _suppress;
        _suppress = false;
        return suppressed;
    }

    private void OnGuiInput(InputEvent input)
    {
        try
        {
            switch (input)
            {
                case InputEventScreenTouch { Canceled: true }:
                    Cancel();
                    break;
                case InputEventScreenTouch { Pressed: true } touch:
                    Begin(touch.Index, touch.Position);
                    break;
                case InputEventScreenTouch touch:
                    End(touch.Index);
                    break;
                // A phone reports the same finger twice, the emulated mouse event first and the touch event after it,
                // and both carry the same position. The scroll is computed from the press point rather than summed from
                // deltas, so arriving twice moves the list exactly once
                case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } press
                    when press.Device == InputEvent.DeviceIdEmulation:
                    Begin(MouseIndex, press.Position);
                    break;
                case InputEventMouseButton { Pressed: false, ButtonIndex: MouseButton.Left } release
                    when release.Device == InputEvent.DeviceIdEmulation:
                    End(MouseIndex);
                    break;
                case InputEventScreenDrag drag when _index < 0 || drag.Index == _index:
                    Move(drag.Position);
                    break;
                case InputEventMouseMotion motion when motion.Device == InputEvent.DeviceIdEmulation:
                    Move(motion.Position);
                    break;
            }
        }
        catch (Exception exception)
        {
            WarnOnce(ref _inputWarned, "scrolling a list by touch failed: " + exception.GetType().Name + ": " +
                                       exception.Message);
        }
    }

    private void Begin(int index, Vector2 position)
    {
        // A second finger is not part of a scroll gesture, same rule the chat's long press applies
        if (_tracking && _index >= 0 && index != _index)
        {
            return;
        }

        _tracking = true;
        _index = index;
        _origin = position;
        _dragging = false;
        _suppress = false;
        _target = _fixedTarget != null && GodotObject.IsInstanceValid(_fixedTarget) ? _fixedTarget : NearestScrollContainer();
        _startScroll = _target?.ScrollVertical ?? 0f;

        // A press focuses the row, and the host paints the focus box in the same green as hover: from then on the row
        // stays lit after the finger has slid to another one, which reads as two highlighted rows at once
        try
        {
            if (_control.HasFocus())
            {
                _control.ReleaseFocus();
            }
        }
        catch (Exception exception)
        {
            WarnOnce(ref _inputWarned, "dropping the pressed row's focus failed: " + exception.GetType().Name + ": " +
                                       exception.Message);
        }
    }

    private void Move(Vector2 position)
    {
        if (!_tracking)
        {
            return;
        }

        Vector2 travel = position - _origin;
        if (!_dragging && travel.LengthSquared() > DragThreshold * DragThreshold)
        {
            _dragging = true;
            // Only the travel past the threshold moves the list: applying the whole travel on the frame the drag is
            // recognised moves it by the threshold in one step, which is the jerk a drag starts with
            _origin += new Vector2(0f, Mathf.Sign(travel.Y) * DragThreshold);
            travel = position - _origin;
        }

        if (_dragging)
        {
            ScrollTo(_startScroll - travel.Y);
        }
    }

    private void End(int index)
    {
        if (!_tracking)
        {
            return;
        }

        if (_index >= 0 && index >= 0 && index != _index)
        {
            return;
        }

        _suppress = _dragging;
        if (_dragging)
        {
            DropHover();
        }

        Reset();
    }

    private void Cancel()
    {
        _suppress = false;
        Reset();
    }

    private void Reset()
    {
        _tracking = false;
        _dragging = false;
        _index = int.MinValue;
        _target = null;
    }

    /// <summary>
    /// Clamped by the bar's own range: the list stops at its own ends instead of asking anything outside it to move,
    /// which is what keeps the dialog scroll from moving while a finger keeps dragging past the end of the list
    /// </summary>
    private void ScrollTo(float value)
    {
        ScrollContainer? scroll = _target;
        if (scroll == null || !GodotObject.IsInstanceValid(scroll))
        {
            return;
        }

        ScrollBar bar = scroll.GetVScrollBar();
        float max = Mathf.Max((float)bar.MaxValue - (float)bar.Page, 0f);
        scroll.ScrollVertical = Mathf.RoundToInt(Mathf.Clamp(value, 0f, max));
    }

    private float CurrentScroll()
    {
        ScrollContainer? scroll = _target;
        return scroll != null && GodotObject.IsInstanceValid(scroll) ? scroll.ScrollVertical : 0f;
    }

    /// <summary>
    /// The engine keeps a row hovered after the finger lifts, and the host paints hover in green, so a finished drag
    /// would end with a green row and no finger on it. Flipping the filter is the one public way to make the viewport
    /// drop that hover: it sends the exit notification and does not re-enter until the pointer moves again
    /// </summary>
    private void DropHover()
    {
        try
        {
            Control? hovered = _control.GetViewport()?.GuiGetHoveredControl();
            if (hovered is not BaseButton button || !GodotObject.IsInstanceValid(button))
            {
                return;
            }

            Control.MouseFilterEnum filter = button.MouseFilter;
            if (filter == Control.MouseFilterEnum.Ignore)
            {
                return;
            }

            button.MouseFilter = Control.MouseFilterEnum.Ignore;
            button.MouseFilter = filter;
        }
        catch (Exception exception)
        {
            WarnOnce(ref _inputWarned, "dropping the hover box left by a drag failed: " + exception.GetType().Name + ": " +
                                       exception.Message);
        }
    }

    private ScrollContainer? NearestScrollContainer()
    {
        for (Node? node = _control.GetParent(); node != null; node = node.GetParent())
        {
            if (node is ScrollContainer scroll && GodotObject.IsInstanceValid(scroll))
            {
                return scroll;
            }
        }

        return null;
    }

    private static void WarnOnce(ref int warned, string message)
    {
        if (Interlocked.Exchange(ref warned, 1) != 0)
        {
            return;
        }

        Entry.Warn("touch-drag-scroll: " + message);
    }
}
