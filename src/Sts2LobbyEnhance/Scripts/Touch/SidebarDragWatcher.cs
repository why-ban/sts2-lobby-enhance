using Godot;

namespace Sts2LobbyEnhance;

/// <summary>
/// Reads the input stream itself and scrolls the lobby's right-hand column when a press that began inside that column
/// becomes a vertical drag.
///
/// <see cref="Node._Input"/> runs before the GUI decides who owns the press, and nothing downstream can cancel it, which
/// is why this does not go through <c>GuiInput</c>: the column is built from containers and buttons, and the control the
/// press lands on is exactly the one that would eat the gesture.
///
/// A press that begins on something that needs the gesture for itself is ignored: a button (so a drag never also
/// activates it), a nested scroller (the chat log keeps scrolling itself), a text field, or the composer. Everything else
/// inside the column - the room details card, the action card, the frame around the chat - is a drag surface.
/// </summary>
internal sealed class SidebarDragWatcher : Node
{
    /// <summary>16 px, the same tap/slide boundary the chat's long press and the dialog rows use.</summary>
    private const float DragThreshold = 16f;

    private const int MouseIndex = -1;

    private const string Tag = "sidebar-scroll: ";

    private static int _warned;

    private static int _logged;

    private Control? _sidebar;

    private ScrollContainer? _scroll;

    private bool _tracking;

    private bool _dragging;

    private int _index = int.MinValue;

    private Vector2 _origin;

    private float _startScroll;

    internal void Follow(Control sidebar, ScrollContainer scroll)
    {
        _sidebar = sidebar;
        _scroll = scroll;
    }

    public override void _Ready() => SetProcessInput(true);

    public override void _Input(InputEvent @event)
    {
        try
        {
            Route(@event);
        }
        catch (Exception exception)
        {
            WarnOnce("a drag on the right-hand column failed: " + exception.GetType().Name + ": " + exception.Message);
        }
    }

    private void Route(InputEvent input)
    {
        switch (input)
        {
            case InputEventScreenTouch { Canceled: true }:
                Stop();
                break;

            case InputEventScreenTouch { Pressed: true } touch:
                Begin(touch.Index, touch.Position);
                break;

            case InputEventScreenTouch touch:
                if (touch.Index == _index)
                {
                    Stop();
                }

                break;

            case InputEventScreenDrag drag when _tracking && drag.Index == _index:
                Move(drag.Position);
                break;

            // A phone reports the same finger twice, as an emulated mouse event and as a touch event. The touch event
            // carries the index this gesture tracks, so the mouse copy must not restart it - but on a desktop, where
            // there is no touch event at all, the mouse copy is the only one there is.
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } press:
                if (press.Pressed)
                {
                    if (_index != MouseIndex)
                    {
                        Begin(MouseIndex, press.Position);
                    }
                }
                else if (_index == MouseIndex)
                {
                    Stop();
                }

                break;

            case InputEventMouseMotion motion when _tracking && _index == MouseIndex:
                Move(motion.Position);
                break;
        }
    }

    private void Begin(int index, Vector2 position)
    {
        if (_tracking || _sidebar == null || _scroll == null)
        {
            return;
        }

        if (!GodotObject.IsInstanceValid(_sidebar) || !GodotObject.IsInstanceValid(_scroll)
            || !_sidebar.IsVisibleInTree())
        {
            return;
        }

        if (!Inside(position) || IsTaken(position))
        {
            return;
        }

        _tracking = true;
        _dragging = false;
        _index = index;
        _origin = position;
        _startScroll = _scroll.ScrollVertical;

        if (Interlocked.Exchange(ref _logged, 1) == 0)
        {
            Entry.Info($"{Tag}a press at ({position.X:0},{position.Y:0}) inside the column now drags it " +
                       $"(scroll range {_scroll.ScrollVertical:0}..{MaxScroll():0}).");
        }
    }

    /// <summary>
    /// True when the press belongs to the column. Two independent tests, because the input position and a control's
    /// rectangle are only the same space when the window transform is identity: the rectangle itself, and - as a
    /// fallback - the control the GUI currently considers hovered, which the engine computed in its own space.
    /// </summary>
    private bool Inside(Vector2 position)
    {
        Control? sidebar = _sidebar;
        if (sidebar == null || !GodotObject.IsInstanceValid(sidebar))
        {
            return false;
        }

        if (sidebar.GetGlobalRect().HasPoint(position))
        {
            return true;
        }

        Control? over = GetViewport()?.GuiGetHoveredControl();
        return over != null
               && GodotObject.IsInstanceValid(over)
               && (ReferenceEquals(over, sidebar) || sidebar.IsAncestorOf(over));
    }

    private void Move(Vector2 position)
    {
        if (!_tracking)
        {
            return;
        }

        Vector2 travel = position - _origin;
        if (!_dragging && Mathf.Abs(travel.Y) > DragThreshold)
        {
            _dragging = true;
            // Only the travel past the threshold moves the column, so the drag does not start with a jump
            _origin += new Vector2(0f, Mathf.Sign(travel.Y) * DragThreshold);
            travel = position - _origin;
        }

        if (!_dragging)
        {
            return;
        }

        float before = CurrentScroll();
        ScrollTo(_startScroll - travel.Y);

        // The column moved, so this drag is ours: consuming it here keeps a nested list (the chat log) from scrolling
        // on the same finger. When the column is already at its end the value does not change, the event is left
        // alone, and the list under the finger scrolls as it always did.
        if (Mathf.Abs(CurrentScroll() - before) >= 1f)
        {
            GetViewport()?.SetInputAsHandled();
        }
    }

    private void Stop()
    {
        _tracking = false;
        _dragging = false;
        _index = int.MinValue;
    }

    private float MaxScroll()
    {
        ScrollContainer? scroll = _scroll;
        if (scroll == null || !GodotObject.IsInstanceValid(scroll))
        {
            return 0f;
        }

        ScrollBar bar = scroll.GetVScrollBar();
        return Mathf.Max((float)bar.MaxValue - (float)bar.Page, 0f);
    }

    private float CurrentScroll() =>
        _scroll != null && GodotObject.IsInstanceValid(_scroll) ? _scroll.ScrollVertical : 0f;

    private void ScrollTo(float value)
    {
        ScrollContainer? scroll = _scroll;
        if (scroll == null || !GodotObject.IsInstanceValid(scroll))
        {
            return;
        }

        scroll.ScrollVertical = Mathf.RoundToInt(Mathf.Clamp(value, 0f, MaxScroll()));
    }

    /// <summary>
    /// True when the press landed on something that has to keep the gesture: a button (a drag there must not also
    /// activate it), a nested scroller (the chat log scrolls itself), a text field, or the composer.
    /// </summary>
    private bool IsTaken(Vector2 position) => _sidebar != null && Taken(_sidebar, position);

    private bool Taken(Node node, Vector2 position)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is not Node childNode || !GodotObject.IsInstanceValid(childNode))
            {
                continue;
            }

            if (childNode is Control control
                && control.Visible
                && control.GetGlobalRect().HasPoint(position)
                && Blocks(control))
            {
                return true;
            }

            if (Taken(childNode, position))
            {
                return true;
            }
        }

        return false;
    }

    private bool Blocks(Control control)
    {
        if (ReferenceEquals(control, _scroll))
        {
            return false;
        }

        if (control is BaseButton or LineEdit or TextEdit)
        {
            return true;
        }

        // The composer is a custom editor with its own drag behaviour; its type lives in the host mod.
        return control.GetType().Name.Contains("DraftEditor", StringComparison.Ordinal);
    }

    private static void WarnOnce(string message) => Entry.WarnOnce(ref _warned, "sidebar-scroll", message);
}
