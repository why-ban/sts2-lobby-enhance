namespace Sts2LobbyEnhance;

/// <summary>
/// A rectangle in the coordinate space of the game-over screen, expressed with plain floats.
///
/// This type exists so the layout below can be verified without a running game. The regression suite
/// (<c>tests/Sts2LobbyEnhance.Tests</c>) compiles this file directly instead of referencing the mod
/// assembly, which needs Godot and the game DLLs to build — so nothing in this file may touch a Godot type.
/// </summary>
internal readonly record struct LayoutRect(float X, float Y, float Width, float Height)
{
    /// <summary>Marker for "this node is not part of this layout". Never intersects anything.</summary>
    internal static LayoutRect Empty { get; } = new(0f, 0f, 0f, 0f);

    internal float Right => X + Width;

    internal float Bottom => Y + Height;

    internal bool IsEmpty => Width <= 0f || Height <= 0f;

    /// <summary>
    /// Strict overlap test: two rectangles that merely touch (or are separated by a gap) do not intersect,
    /// and an empty rectangle never intersects anything.
    /// </summary>
    internal bool Intersects(in LayoutRect other) =>
        !IsEmpty
        && !other.IsEmpty
        && X < other.Right
        && other.X < Right
        && Y < other.Bottom
        && other.Y < Bottom;

    /// <summary>
    /// True when this rectangle lies completely inside <paramref name="bounds"/>. An empty rectangle is
    /// vacuously inside, which keeps "nothing is off screen" a single unconditional assertion.
    /// </summary>
    internal bool IsInside(in LayoutRect bounds) =>
        IsEmpty
        || (X >= bounds.X
            && Y >= bounds.Y
            && Right <= bounds.Right
            && Bottom <= bounds.Bottom);
}

/// <summary>
/// The rectangles the game-over injection needs, in the anchor's coordinate space.
///
/// <see cref="Menu"/> is the main-menu button after normalisation (clamped into the viewport). It is returned
/// so the runtime can log the exact numbers it laid out against and so the tests can assert against the rect
/// that was actually used instead of the raw input.
///
/// Only one of the two "shapes" is ever populated, because the host and the client screen are mutually
/// exclusive:
/// <list type="bullet">
/// <item><c>withRestartButton: true</c> (host) — <see cref="Button"/> and <see cref="Caption"/> are set,
/// <see cref="Hint"/> is <see cref="LayoutRect.Empty"/>;</item>
/// <item><c>withRestartButton: false</c> (client, and the host screen when the button is not offered) —
/// <see cref="Hint"/> is set, <see cref="Button"/> and <see cref="Caption"/> are empty.</item>
/// </list>
/// The empty rectangles are what makes the "nothing overlaps anything" assertions total: they hold for both
/// shapes without the tests having to special-case which one is in play.
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
/// Pure geometry for the nodes this fix injects into <c>NGameOverScreen</c>.
///
/// Why this is a separate, dependency-free function (2026-11-13, second attempt):
/// the first implementation placed the duplicated button at <c>mainMenu.Position + (0, step)</c> *before*
/// adding it to the tree, and logged "injected". The button really was injected — and it was invisible,
/// because <c>Node.Duplicate()</c> keeps the source node's position, so the copy sat exactly on top of
/// "主菜单" and was covered by it. "Injected successfully" is not the same thing as "the player can see it",
/// so the geometry is now something a test can assert on instead of something a log line claims.
///
/// The rules the function guarantees, for every input:
/// <list type="bullet">
/// <item>the new button never overlaps the main-menu button, and the visual gap is at least
/// <see cref="MinGap"/> (16px, which also makes the centre-to-centre step larger than the button height);</item>
/// <item>the caption never overlaps either button, and the hint never overlaps anything;</item>
/// <item>every non-empty rectangle lies completely inside the viewport (no negative coordinates, no
/// off-screen edges);</item>
/// <item>the stack goes below the main-menu button when it fits and above it when it does not, so a
/// bottom-anchored button on a phone still gets a visible neighbour;</item>
/// <item>the font size follows the viewport height (<c>height / 45</c>, clamped to 24..30), so the text is
/// readable at 1920x1080 and does not become a wall of glyphs at 1080x2340.</item>
/// </list>
/// </summary>
internal static class GameOverLayout
{
    /// <summary>Smallest font the info text may use. 15 (the old value) was unreadable at 1920x1080.</summary>
    internal const int MinFontSize = 24;

    /// <summary>Largest font the info text may use, so a tall phone viewport cannot produce giant text.</summary>
    internal const int MaxFontSize = 30;

    /// <summary>
    /// Smallest visual gap between the main-menu button and the injected button. The task asked for
    /// "button height + 16px"; that is the centre-to-centre step, which this gap guarantees because the step
    /// is <c>height + Gap</c>. The regression suite asserts the stricter 16px, not the looser 8px floor.
    /// </summary>
    internal const float MinGap = 16f;

    /// <summary>Line height as a multiple of the font size. Matches Godot's default label metrics closely.</summary>
    internal const float LineHeightFactor = 1.45f;

    /// <summary>
    /// How many wrapped lines the info box reserves. Two is enough for the longest caption
    /// ("N 名队友将一起返回准备页，M 人未安装补丁需手动重连") and for the longest client hint.
    /// </summary>
    internal const int InfoLineCount = 2;

    /// <summary>
    /// Computes the layout for the injected nodes.
    /// </summary>
    /// <param name="mainMenuRect">
    /// The screen's own "主菜单" button, in the same coordinate space as the viewport. Measured at runtime —
    /// never hard-coded, because its size and position differ per aspect ratio.
    /// </param>
    /// <param name="viewportWidth">Width of the coordinate space the rect lives in.</param>
    /// <param name="viewportHeight">Height of the coordinate space the rect lives in.</param>
    /// <param name="withRestartButton">
    /// <c>true</c> when the "新开一局" button is offered (host with at least one patched peer), <c>false</c>
    /// when only the info line is shown.
    /// </param>
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

        // Wide enough for the longest caption on one line, but never wider than the viewport allows.
        float infoWidth = MathF.Min(maxMenuW, MathF.Max(menuW * 2f, 420f));
        float infoX = Math.Clamp(
            menuX + menuW * 0.5f - infoWidth * 0.5f,
            margin,
            MathF.Max(margin, viewportW - margin - infoWidth));

        float buttonX = Math.Clamp(menuX, margin, MathF.Max(margin, viewportW - margin - menuW));

        // How much room the whole stack needs on one side of the main-menu button.
        float stack = withRestartButton
            ? gap + menuH + labelGap + infoHeight
            : gap + infoHeight;

        bool fitsBelow = menuY + menuH + stack <= viewportH - margin;
        bool fitsAbove = menuY - stack >= margin;

        // Below is the natural reading order (the new action sits under the existing one). It is only
        // abandoned when it does not fit *and* there is room above.
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

    /// <summary>Width of the viewport as a rectangle, for callers that want the bounds object.</summary>
    internal static LayoutRect Viewport(float width, float height) => new(0f, 0f, width, height);

    private static bool IsUsable(float value) => float.IsFinite(value) && value >= 1f;

    private static float Safe(float value) => float.IsFinite(value) ? value : 0f;

    /// <summary>Clamps a start coordinate so that a <paramref name="length"/>-long span stays inside the viewport.</summary>
    private static float ClampAxis(float start, float length, float viewportLength, float margin) =>
        Math.Clamp(start, margin, MathF.Max(margin, viewportLength - margin - length));
}
