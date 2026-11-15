using Godot;

namespace Sts2LobbyEnhance;

/// <summary>
/// Long press = copy the whole chat message. Installed on the message label of one row by
/// <see cref="TouchChatScrollFix"/> and alive only as long as that label is.
///
/// The gesture is the same one the mod already uses for item references, only held longer: upstream's
/// <c>LanConnectReferenceTapGesture</c> decides tap versus slide with a 16 px travel limit
/// (LanConnectReferenceTapGesture.cs:9), and a hold of 400 ms is the added dimension. A press that travels further
/// than 16 px is a scroll and never copies; a press released before 400 ms is a tap and stays whatever upstream makes
/// of it. Both numbers are checked twice — at the 400 ms mark and again on release — so a timer that never fires (a
/// paused tree, a view freed mid-press) costs nothing but promptness, never the gesture itself.
///
/// The copied text is the string upstream already assembles for Ctrl+C and stores on the view as the meta
/// <c>lan_connect_copy_text</c> (LanConnectRichMessageView.cs:84); <c>RichTextLabel.GetParsedText()</c> is only a
/// fallback in case that meta ever stops being written.
///
/// This is deliberately a plain class and not a <c>Node</c>: Godot only dispatches <c>_Ready</c>/<c>_Process</c> to
/// C# classes that went through its source generator (script classes), and a class that a mod instantiates by hand
/// has no generated dispatch. Everything time-based here therefore rides on engine objects that do — a
/// <c>SceneTreeTimer</c> for the hold threshold and a <c>Tween</c> for the feedback pulse — which also means the pulse
/// is killed together with the row it belongs to.
/// </summary>
internal sealed class LongPressCopyGesture
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

    /// <summary>Written by <c>LanConnectRichMessageView</c> on the view itself (LanConnectRichMessageView.cs:84)</summary>
    private const string CopyTextMetaName = "lan_connect_copy_text";

    /// <summary>
    /// A no-vibration acknowledgement: Android silently ignores vibration without the VIBRATE permission, so a
    /// device without it would copy with no sign at all.
    /// </summary>
    private static readonly Color FeedbackModulate = new(1.35f, 1.35f, 1.35f, 1f);

    private static readonly Color DefaultModulate = new(1f, 1f, 1f, 1f);

    private static int _warned;

    private readonly RichTextLabel _label;
    private readonly CanvasItem? _row;
    private readonly Color _rowModulate;

    private int? _touchIndex;
    private Vector2 _origin;
    private ulong _pressedAt;
    private bool _cancelled;
    private bool _copied;
    private int _press;
    private Tween? _feedback;

    /// <param name="label">The message label; its <c>GuiInput</c> signal is the only thing this subscribes to.</param>
    /// <param name="row">What gets brightened on a copy: the view that owns the label, or null to skip the pulse.</param>
    internal LongPressCopyGesture(RichTextLabel label, CanvasItem? row)
    {
        _label = label;
        _row = row;
        _rowModulate = row != null && GodotObject.IsInstanceValid(row) ? row.Modulate : DefaultModulate;
    }

    /// <summary>
    /// Subscribes to the label. Called once per row, from the view's <c>_Ready</c> postfix, so the connection is
    /// dropped by Godot when either end is freed — including every row that <c>Refresh()</c> rebuilds
    /// (LanConnectBasicChatPanel.cs:1280-1303).
    /// </summary>
    internal void Attach() => _label.GuiInput += OnGuiInput;

    private void OnGuiInput(InputEvent input)
    {
        try
        {
            switch (input)
            {
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
            WarnOnce("copying the message failed: " + exception.GetType().Name + ": " + exception.Message);
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

            // Same gesture, judged on release: covers a hold whose timer never fired.
            if (!_cancelled &&
                !_copied &&
                Time.GetTicksMsec() - _pressedAt >= HoldMilliseconds &&
                touch.Position.DistanceSquaredTo(_origin) <= MaxTravelSquared)
            {
                TryCopy();
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
            // A second finger is never a copy gesture (same rule as LanConnectReferenceTapGesture.cs:23-27).
            _cancelled = true;
            return;
        }

        _touchIndex = touch.Index;
        _origin = touch.Position;
        _pressedAt = Time.GetTicksMsec();
        _cancelled = false;
        _copied = false;
        ArmHoldTimer(++_press);
    }

    private void OnDrag(InputEventScreenDrag drag)
    {
        if (_touchIndex != drag.Index)
        {
            return;
        }

        if (drag.Position.DistanceSquaredTo(_origin) > MaxTravelSquared)
        {
            // Sticky on purpose, like upstream's own travel limit (LanConnectReferenceTapGesture.cs:43): dragging
            // away and back is a scroll that happened to end where it started, not a press.
            _cancelled = true;
        }
    }

    /// <summary>
    /// The 400 ms mark, on an engine timer rather than a frame counter: the chat is often open over a paused tree,
    /// and <c>CreateTimer</c>'s default <c>processAlways</c> keeps counting there.
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
            if (press != _press || !_touchIndex.HasValue || _cancelled || _copied)
            {
                return;
            }

            if (!GodotObject.IsInstanceValid(_label))
            {
                return;
            }

            TryCopy();
        }
        catch (Exception exception)
        {
            WarnOnce("timing the long press failed: " + exception.GetType().Name + ": " + exception.Message);
        }
    }

    private void TryCopy()
    {
        if (_copied)
        {
            return;
        }

        _copied = true;

        string text = ReadCopyText();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        DisplayServer.ClipboardSet(text);
        Pulse();
    }

    /// <summary>
    /// The whole message as one string. The view builds it by concatenating every span's copy text
    /// (LanConnectRichMessageView.cs:44, published at :84), so an item reference copies as the item name rather than
    /// as BBCode.
    /// </summary>
    private string ReadCopyText()
    {
        Node? view = _label.GetParent();
        if (view != null && GodotObject.IsInstanceValid(view) && view.HasMeta(CopyTextMetaName))
        {
            string published = view.GetMeta(CopyTextMetaName, default).AsString();
            if (!string.IsNullOrEmpty(published))
            {
                return published;
            }
        }

        return _label.GetParsedText();
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
    /// One warning per process: a row is rebuilt on every message change, so an unguarded log here would flood the
    /// file from a single broken build.
    /// </summary>
    private static void WarnOnce(string message)
    {
        if (Interlocked.Exchange(ref _warned, 1) != 0)
        {
            return;
        }

        Entry.Warn("touch-chat-scroll: " + message);
    }
}
