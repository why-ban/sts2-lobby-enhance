namespace Sts2LobbyEnhance;

    /// <summary>
    /// A rectangle in the coordinate space of the game-over screen, in plain floats so the layout can be tested
    /// without a running game. The regression suite compiles this file directly, so nothing here may touch a
    /// Godot type
    /// </summary>
internal readonly record struct LayoutRect(float X, float Y, float Width, float Height)
{
    internal static LayoutRect Empty { get; } = new(0f, 0f, 0f, 0f);

    internal float Right => X + Width;

    internal float Bottom => Y + Height;

    internal bool IsEmpty => Width <= 0f || Height <= 0f;

    /// <summary>
    /// Strict overlap test: rectangles that merely touch do not intersect, and an empty one never does
    /// </summary>
    internal bool Intersects(in LayoutRect other) =>
        !IsEmpty
        && !other.IsEmpty
        && X < other.Right
        && other.X < Right
        && Y < other.Bottom
        && other.Y < Bottom;

    /// <summary>
    /// An empty rectangle is vacuously inside, which keeps "nothing is off screen" one unconditional assertion
    /// </summary>
    internal bool IsInside(in LayoutRect bounds) =>
        IsEmpty
        || (X >= bounds.X
            && Y >= bounds.Y
            && Right <= bounds.Right
            && Bottom <= bounds.Bottom);
}

    /// <summary>
    /// The rectangles the game-over injection needs, in the anchor's coordinate space
    ///
    /// Only one shape is ever populated, because the host and client screens are mutually exclusive: with the
    /// restart button offered, <see cref="Button"/> and <see cref="Caption"/> are set and <see cref="Hint"/> is
    /// <see cref="LayoutRect.Empty"/>; otherwise <see cref="Hint"/> is set and both are empty. The empty
    /// rectangles keep the "nothing overlaps anything" assertions total for both shapes. <see cref="Menu"/> is
    /// the main-menu rect after clamping, returned so callers log and assert the rect actually used
    /// </summary>
internal readonly record struct GameOverSlots(
    LayoutRect Menu,
    LayoutRect Button,
    LayoutRect Caption,
    LayoutRect Hint,
    int FontSize,
    float Gap,
    bool PlacedBelow)
{
    internal bool HasButton => !Button.IsEmpty;
}

/// <summary>
/// Pure geometry for the nodes this fix injects into <c>NGameOverScreen</c>, as a dependency-free function a
/// test can assert on instead of a log line that claims success
///
/// <c>Node.Duplicate()</c> keeps the source node's position, so a copy placed at
/// <c>mainMenu.Position + (0, step)</c> before being added to the tree sits exactly on top of "主菜单" and
/// is invisible while the log still says "injected"
/// </summary>
internal static class GameOverLayout
{
    /// <summary>Below this the info text is unreadable at 1920x1080</summary>
    internal const int MinFontSize = 24;

    internal const int MaxFontSize = 30;

    /// <summary>Visual gap; the centre-to-centre step is height + Gap, and the suite asserts the 16px gap</summary>
    internal const float MinGap = 16f;

    /// <summary>Line height as a multiple of the font size. Matches Godot's default label metrics closely.</summary>
    internal const float LineHeightFactor = 1.45f;

    /// <summary>Two lines cover the longest caption and the longest client hint</summary>
    internal const int InfoLineCount = 2;

    /// <summary>
    /// <paramref name="mainMenuRect"/> is measured at runtime, never hard-coded, because its size and position
    /// differ per aspect ratio
    /// </summary>
    internal static GameOverSlots ComputeLayout(
        LayoutRect mainMenuRect,
        float viewportWidth,
        float viewportHeight,
        bool withRestartButton = true)
    {
        float viewportW = IsUsable(viewportWidth) ? viewportWidth : 1f;
        float viewportH = IsUsable(viewportHeight) ? viewportHeight : 1f;

        // Keep everything off the screen edges. 2% of the height reads as a comfortable margin on both a
        // 1080-high desktop window and a 2340-high phone viewport.
        float margin = Math.Clamp(viewportH * 0.02f, 8f, 32f);
        margin = MathF.Min(margin, MathF.Min(viewportW, viewportH) / 4f);

        int fontSize = (int)Math.Clamp(MathF.Round(viewportH / 45f), MinFontSize, MaxFontSize);
        float lineHeight = MathF.Ceiling(fontSize * LineHeightFactor);
        float infoHeight = lineHeight * InfoLineCount;

        // The measured rect is normalised instead of trusted: a Container that has not sorted yet, or a scene
        // whose button has no size, must not be able to push the injected nodes off screen.
        float maxMenuW = MathF.Max(1f, viewportW - 2f * margin);
        float maxMenuH = MathF.Max(1f, viewportH - 2f * margin);
        float menuW = Math.Clamp(Safe(mainMenuRect.Width), 1f, maxMenuW);
        float menuH = Math.Clamp(Safe(mainMenuRect.Height), 1f, maxMenuH);
        float menuX = Math.Clamp(Safe(mainMenuRect.X), 0f, MathF.Max(0f, viewportW - menuW));
        float menuY = Math.Clamp(Safe(mainMenuRect.Y), 0f, MathF.Max(0f, viewportH - menuH));

        float gap = MathF.Max(MinGap, MathF.Round(menuH * 0.25f));
        float labelGap = MathF.Max(8f, MathF.Round(fontSize * 0.5f));

        float infoWidth = MathF.Min(maxMenuW, MathF.Max(menuW * 2f, 420f));
        float infoX = Math.Clamp(
            menuX + menuW * 0.5f - infoWidth * 0.5f,
            margin,
            MathF.Max(margin, viewportW - margin - infoWidth));

        float buttonX = Math.Clamp(menuX, margin, MathF.Max(margin, viewportW - margin - menuW));

        float stack = withRestartButton
            ? gap + menuH + labelGap + infoHeight
            : gap + infoHeight;

        bool fitsBelow = menuY + menuH + stack <= viewportH - margin;
        bool fitsAbove = menuY - stack >= margin;

    // Below is the natural reading order, abandoned only when it does not fit and there is room above
        bool placedBelow = fitsBelow || !fitsAbove;

        LayoutRect button = LayoutRect.Empty;
        LayoutRect caption = LayoutRect.Empty;
        LayoutRect hint = LayoutRect.Empty;

        if (withRestartButton)
        {
            float buttonY = placedBelow ? menuY + menuH + gap : menuY - gap - menuH;
            float captionY = placedBelow ? buttonY + menuH + labelGap : buttonY - labelGap - infoHeight;

            button = new LayoutRect(buttonX, ClampAxis(buttonY, menuH, viewportH, margin), menuW, menuH);
            caption = new LayoutRect(infoX, ClampAxis(captionY, infoHeight, viewportH, margin), infoWidth, infoHeight);
        }
        else
        {
            float hintY = placedBelow ? menuY + menuH + gap : menuY - gap - infoHeight;
            hint = new LayoutRect(infoX, ClampAxis(hintY, infoHeight, viewportH, margin), infoWidth, infoHeight);
        }

        return new GameOverSlots(
            new LayoutRect(menuX, menuY, menuW, menuH),
            button,
            caption,
            hint,
            fontSize,
            gap,
            placedBelow);
    }

    internal static LayoutRect Viewport(float width, float height) => new(0f, 0f, width, height);

    private static bool IsUsable(float value) => float.IsFinite(value) && value >= 1f;

    private static float Safe(float value) => float.IsFinite(value) ? value : 0f;

    private static float ClampAxis(float start, float length, float viewportLength, float margin) =>
        Math.Clamp(start, margin, MathF.Max(margin, viewportLength - margin - length));
}
