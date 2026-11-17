using Godot;

namespace Sts2LobbyEnhance;

/// <summary>
/// Scrolls the lobby's right-hand column when a press that began inside it turns into a vertical drag.
///
/// <see cref="Node._Input"/> runs before the GUI picks a target and cannot be cancelled by it, which is why this does not
/// go through <c>GuiInput</c>: inside the column a press lands on a button, on the chat's own list, or on a container that
/// stops the pointer, and the column's scroller never sees it.
///
/// What it swallows is narrow on purpose. Touch events are never touched: the chat's own long press and its selection both
/// read them, and a <c>ScrollContainer</c> ignores them anyway. Only the emulated mouse stream is filtered - its motion is
/// what makes any scroller in this column move natively, and its release is what would turn the press under the drag into
/// a click. The press itself is always left alone, because the reference preview needs the label to see the release.
///
/// A message that is being selected owns the gesture outright: its label turns into a stop target for the duration, so the
/// drag extends the selection and the chat list stays still without any help from here.
/// </summary>
internal sealed class MobileSidebarScrollWatcher : Node
{
    /// <summary>16 px, the same tap/slide boundary the chat's long press and the dialog rows use.</summary>
    private const float DragThreshold = 16f;

    private const string Tag = "mobile-sidebar-scroll: ";

    private static int _warned;

    private static int _reported;

    private Control? _column;

    private ScrollContainer? _scroll;

    private ScrollContainer? _inner;

    private bool _tracking;

    private bool _dragging;

    private int _index = int.MinValue;

    private Vector2 _origin;

    private float _startColumn;

    private float _startInner;

    internal void Follow(Control column, ScrollContainer scroll)
    {
        if (!ReferenceEquals(column, _column) || !ReferenceEquals(scroll, _scroll))
        {
            Reset();
        }

        _column = column;
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
                if (_dragging)
                {
                    Swallow();
                }

                Reset();
                break;

            case InputEventScreenTouch { Pressed: true } touch:
                Begin(touch.Index, touch.Position);
                break;

            case InputEventScreenTouch touch:
                if (_tracking && touch.Index == _index)
                {
                    End();
                }

                break;

            // The touch stream is what the chat's own gesture reads and what a ScrollContainer ignores, so it is handed
            // on untouched. The column is moved by the absolute offset applied in Move.
            case InputEventScreenDrag drag when _tracking && drag.Index == _index:
                Move(drag.Position);
                break;

            // The emulated mouse copy of the same finger, dispatched just before the touch one. Only motion and release
            // are filtered; see the type comment for why.
            case InputEventMouseMotion motion
                when motion.Device == InputEvent.DeviceIdEmulation && _tracking && !Shaping():
                Swallow();
                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.Left } release
                when release.Device == InputEvent.DeviceIdEmulation && _dragging:
                Swallow();
                break;
        }
    }

    private void Begin(int index, Vector2 position)
    {
        if (_tracking || _scroll == null || _column == null || Shaping())
        {
            return;
        }

        if (!GodotObject.IsInstanceValid(_column) || !GodotObject.IsInstanceValid(_scroll)
            || !_column.IsVisibleInTree())
        {
            return;
        }

        if (!Inside(position, out Control? over) || IsTextEntry(over))
        {
            return;
        }

        _tracking = true;
        _dragging = false;
        _index = index;
        _origin = position;
        _startColumn = _scroll.ScrollVertical;
        _inner = Nested(over, position);
        _startInner = _inner != null && GodotObject.IsInstanceValid(_inner) ? _inner.ScrollVertical : 0f;

        if (Interlocked.Exchange(ref _reported, 1) == 0)
        {
            Entry.Info($"{Tag}a press inside the column now drags it (scroll 0..{Range(_scroll):0}, " +
                       $"nested={(_inner == null ? "none" : _inner.Name.ToString())}).");
        }
    }

    /// <summary>
    /// True when the press belongs to the column. The hovered control is asked first because the engine computed it in
    /// the same space the GUI uses, while a raw input position and a control rectangle only agree while the window and
    /// canvas transforms are identity.
    /// </summary>
    private bool Inside(Vector2 position, out Control? over)
    {
        over = null;
        Control? column = _column;
        if (column == null || !GodotObject.IsInstanceValid(column))
        {
            return false;
        }

        over = GetViewport()?.GuiGetHoveredControl();
        if (over != null && GodotObject.IsInstanceValid(over)
            && (ReferenceEquals(over, column) || column.IsAncestorOf(over)))
        {
            return true;
        }

        return column.GetGlobalRect().HasPoint(position);
    }

    /// <summary>
    /// The composer keeps the gesture: a drag that starts in a text field is a selection, not a scroll.
    /// </summary>
    private bool IsTextEntry(Control? over)
    {
        Control? column = _column;
        for (Node? node = over; node != null; node = node.GetParent())
        {
            if (node is LineEdit or TextEdit
                || node.GetType().Name.Contains("DraftEditor", StringComparison.Ordinal))
            {
                return true;
            }

            if (ReferenceEquals(node, column))
            {
                break;
            }
        }

        return false;
    }

    /// <summary>
    /// The scroller under the press that is not the column's own - the chat log. Resolved at press time so the handoff at
    /// the column's ends lands on whatever the finger is actually over.
    /// </summary>
    private ScrollContainer? Nested(Control? over, Vector2 position)
    {
        Control? column = _column;
        if (column == null || !GodotObject.IsInstanceValid(column))
        {
            return null;
        }

        if (over != null && GodotObject.IsInstanceValid(over)
            && (ReferenceEquals(over, column) || column.IsAncestorOf(over)))
        {
            for (Node? node = over; node != null && !ReferenceEquals(node, column); node = node.GetParent())
            {
                if (node is ScrollContainer scroll && !ReferenceEquals(scroll, _scroll)
                    && GodotObject.IsInstanceValid(scroll))
                {
                    return scroll;
                }
            }

            return null;
        }

        return Deepest(column, position);
    }

    private ScrollContainer? Deepest(Node node, Vector2 position)
    {
        ScrollContainer? found = null;
        foreach (Node child in node.GetChildren())
        {
            if (child is not Control control || !GodotObject.IsInstanceValid(control) || !control.Visible)
            {
                continue;
            }

            if (!control.GetGlobalRect().HasPoint(position))
            {
                continue;
            }

            if (control is ScrollContainer scroll && !ReferenceEquals(scroll, _scroll))
            {
                found = scroll;
            }

            found = Deepest(control, position) ?? found;
        }

        return found;
    }

    private void Move(Vector2 position)
    {
        if (Shaping())
        {
            // A long press turned into a selection since this press began. That drag is the selection's, and the label is
            // a stop target for its duration, so nothing here has to keep the chat list still: let go entirely.
            Reset();
            return;
        }

        Vector2 travel = position - _origin;
        if (!_dragging)
        {
            if (Mathf.Abs(travel.Y) <= DragThreshold)
            {
                return;
            }

            _dragging = true;
            // Only the travel past the threshold moves the column, so the drag does not start with a jump
            _origin += new Vector2(0f, Mathf.Sign(travel.Y) * DragThreshold);
            travel = position - _origin;
        }

        Apply(travel.Y);
    }

    /// <summary>
    /// The column first, then whatever travel it could not use goes to the nested list, both by absolute offset from the
    /// values captured at the press, so a repeated event never accumulates.
    /// </summary>
    private void Apply(float dy)
    {
        float wanted = _startColumn - dy;
        float column = Mathf.Clamp(wanted, 0f, Range(_scroll));
        SetScroll(_scroll, column);

        float left = wanted - column;
        if (Mathf.Abs(left) < 0.5f || _inner == null || !GodotObject.IsInstanceValid(_inner))
        {
            return;
        }

        SetScroll(_inner, Mathf.Clamp(_startInner + left, 0f, Range(_inner)));
    }

    private void End()
    {
        bool dragged = _dragging;
        Reset();

        if (dragged)
        {
            // A drag that moved the column must not also activate the button it started on
            Swallow();
        }
    }

    private void Reset()
    {
        _tracking = false;
        _dragging = false;
        _index = int.MinValue;
        _inner = null;
    }

    /// <summary>
    /// True while a message is being selected: that gesture belongs to the selection, not to the column
    /// </summary>
    private static bool Shaping() => TouchChatScrollFix.TouchSelectionSession.IsShaping;

    private void Swallow() => GetViewport()?.SetInputAsHandled();

    private static float Range(ScrollContainer? scroll)
    {
        if (scroll == null || !GodotObject.IsInstanceValid(scroll))
        {
            return 0f;
        }

        ScrollBar bar = scroll.GetVScrollBar();
        return Mathf.Max((float)bar.MaxValue - (float)bar.Page, 0f);
    }

    private static void SetScroll(ScrollContainer? scroll, float value)
    {
        if (scroll == null || !GodotObject.IsInstanceValid(scroll))
        {
            return;
        }

        scroll.ScrollVertical = Mathf.RoundToInt(value);
    }

    private static void WarnOnce(string message) => Entry.WarnOnce(ref _warned, "mobile-sidebar-scroll", message);
}
