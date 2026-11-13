using Xunit;

namespace Sts2LobbyEnhance.Tests;

/// <summary>
/// Geometry assertions for the two nodes the patch injects into the game-over screen.
///
/// These exist because the first implementation of this feature was verified by a log line
/// (<c>host controls injected ...</c>) and shipped a button the player could not see: <c>Node.Duplicate()</c>
/// keeps the source node's position, so the copy sat exactly on top of "主菜单" and was hidden behind it.
/// "Injected" is not "visible", so the placement is now a pure function that a test can prove, in both
/// viewports the feature has to support (PC 1920x1080 and phone 1080x2340).
///
/// The file under test (<c>Scripts/Restart/GameOverLayout.cs</c>) is linked into this project by the .csproj
/// rather than referenced, so it must stay free of Godot types — and the code that ships is the code asserted
/// on here.
/// </summary>
public sealed class GameOverLayoutTests
{
    private const float DesktopWidth = 1920f;
    private const float DesktopHeight = 1080f;
    private const float PhoneWidth = 1080f;
    private const float PhoneHeight = 2340f;

    /// <summary>
    /// Where the game's own "主菜单" button can plausibly sit. The real rectangle is measured at runtime
    /// (the layout is a function of it, never a hard-coded coordinate), so the suite covers the extremes:
    /// bottom-anchored, vertically centred, top-anchored, narrow, and a degenerate rect for the case where the
    /// scene has not laid the button out yet.
    /// </summary>
    private static readonly (string Name, float X, float Y, float Width, float Height)[] MenuRects =
    [
        ("bottom centre", 810f, 880f, 300f, 96f),
        ("middle", 810f, 500f, 300f, 96f),
        ("top centre", 810f, 40f, 300f, 96f),
        ("narrow left", 40f, 940f, 200f, 72f),
        ("wide centre", 560f, 860f, 800f, 120f),
        ("degenerate", 0f, 0f, 0f, 0f)
    ];

    private static readonly bool[] ButtonVariants = [true, false];

    public static TheoryData<float, float> Viewports => new()
    {
        { DesktopWidth, DesktopHeight },
        { PhoneWidth, PhoneHeight }
    };

    /// <summary>No injected rectangle may leave the viewport or use a negative coordinate.</summary>
    [Theory]
    [MemberData(nameof(Viewports))]
    public void InjectedRectsStayInsideTheViewport(float viewportWidth, float viewportHeight)
    {
        LayoutRect viewport = GameOverLayout.Viewport(viewportWidth, viewportHeight);

        foreach ((string name, float x, float y, float width, float height) in MenuRects)
        {
            LayoutRect menu = new(x, y, width, height);

            foreach (bool withButton in ButtonVariants)
            {
                GameOverSlots slots = GameOverLayout.ComputeLayout(menu, viewportWidth, viewportHeight, withButton);
                string where = Describe(name, viewportWidth, viewportHeight, withButton);

                Assert.True(slots.Menu.IsInside(viewport), $"{where}: the normalised main-menu rect is off screen");
                AssertInside(viewport, slots.Button, "button", where);
                AssertInside(viewport, slots.Caption, "caption", where);
                AssertInside(viewport, slots.Hint, "hint", where);
            }
        }
    }

    /// <summary>Nothing the patch injects may overlap the game's button, the other injected node, or itself.</summary>
    [Theory]
    [MemberData(nameof(Viewports))]
    public void InjectedRectsNeverOverlap(float viewportWidth, float viewportHeight)
    {
        foreach ((string name, float x, float y, float width, float height) in MenuRects)
        {
            LayoutRect menu = new(x, y, width, height);

            foreach (bool withButton in ButtonVariants)
            {
                GameOverSlots slots = GameOverLayout.ComputeLayout(menu, viewportWidth, viewportHeight, withButton);
                string where = Describe(name, viewportWidth, viewportHeight, withButton);

                // The button is the whole point of the fix: it must not be covered by "主菜单" again.
                Assert.False(slots.Button.Intersects(slots.Menu), $"{where}: the new button overlaps 主菜单");
                Assert.False(slots.Caption.Intersects(slots.Menu), $"{where}: the caption overlaps 主菜单");
                Assert.False(slots.Caption.Intersects(slots.Button), $"{where}: the caption overlaps the new button");
                Assert.False(slots.Hint.Intersects(slots.Menu), $"{where}: the hint overlaps 主菜单");
                Assert.False(slots.Hint.Intersects(slots.Button), $"{where}: the hint overlaps the new button");
                Assert.False(slots.Hint.Intersects(slots.Caption), $"{where}: the hint overlaps the caption");
            }
        }
    }

    /// <summary>
    /// The task asked for at least "button height + 16px" between the two buttons. The layout expresses that as
    /// a centre-to-centre step of <c>height + gap</c> with a 16px gap, so the measured empty space between the
    /// rectangles must be at least 16px — comfortably above the 8px floor.
    /// </summary>
    [Theory]
    [MemberData(nameof(Viewports))]
    public void TheTwoButtonsAreSeparatedByAtLeast16Pixels(float viewportWidth, float viewportHeight)
    {
        foreach ((string name, float x, float y, float width, float height) in MenuRects)
        {
            LayoutRect menu = new(x, y, width, height);
            GameOverSlots slots = GameOverLayout.ComputeLayout(menu, viewportWidth, viewportHeight, withRestartButton: true);
            string where = Describe(name, viewportWidth, viewportHeight, withButton: true);

            Assert.True(slots.HasButton, $"{where}: the host layout must contain the new button");
            Assert.True(slots.Gap >= GameOverLayout.MinGap, $"{where}: gap {slots.Gap} is below the 16px minimum");

            float measured = slots.PlacedBelow
                ? slots.Button.Y - slots.Menu.Bottom
                : slots.Menu.Y - slots.Button.Bottom;

            Assert.True(
                measured >= GameOverLayout.MinGap,
                $"{where}: only {measured}px of empty space between the two buttons");
        }
    }

    /// <summary>The caption belongs under the button when the stack goes down, and above it when it goes up.</summary>
    [Theory]
    [MemberData(nameof(Viewports))]
    public void TheCaptionFollowsTheButton(float viewportWidth, float viewportHeight)
    {
        foreach ((string name, float x, float y, float width, float height) in MenuRects)
        {
            LayoutRect menu = new(x, y, width, height);
            GameOverSlots slots = GameOverLayout.ComputeLayout(menu, viewportWidth, viewportHeight, withRestartButton: true);
            string where = Describe(name, viewportWidth, viewportHeight, withButton: true);

            bool followsTheButton = slots.PlacedBelow
                ? slots.Caption.Y >= slots.Button.Bottom
                : slots.Caption.Bottom <= slots.Button.Y;

            Assert.True(followsTheButton, $"{where}: the caption is not adjacent to the button");
        }
    }

    /// <summary>
    /// 15px was unreadable at 1920x1080. The size now follows the viewport height, clamped so a tall phone
    /// viewport cannot produce giant text.
    /// </summary>
    [Fact]
    public void TheInfoFontIsReadableOnBothViewports()
    {
        LayoutRect menu = new(810f, 880f, 300f, 96f);

        GameOverSlots desktop = GameOverLayout.ComputeLayout(menu, DesktopWidth, DesktopHeight);
        GameOverSlots phone = GameOverLayout.ComputeLayout(menu, PhoneWidth, PhoneHeight);

        Assert.Equal(24, desktop.FontSize);
        Assert.Equal(30, phone.FontSize);

        foreach (GameOverSlots slots in new[] { desktop, phone })
        {
            Assert.InRange(slots.FontSize, GameOverLayout.MinFontSize, GameOverLayout.MaxFontSize);
            Assert.True(slots.FontSize >= 24, $"font {slots.FontSize} is below the readable minimum");
        }
    }

    /// <summary>
    /// The client has no button, and must not get one: the layout switches to the info-only shape so the hint
    /// takes the slot right below "主菜单" instead of leaving a hole where the button would have been.
    /// </summary>
    [Theory]
    [MemberData(nameof(Viewports))]
    public void TheClientLayoutHasNoButton(float viewportWidth, float viewportHeight)
    {
        LayoutRect menu = new(810f, 880f, 300f, 96f);
        GameOverSlots slots = GameOverLayout.ComputeLayout(menu, viewportWidth, viewportHeight, withRestartButton: false);

        Assert.False(slots.HasButton, "the client layout must not reserve a button");
        Assert.True(slots.Button.IsEmpty, "the client layout must not produce a button rect");
        Assert.True(slots.Caption.IsEmpty, "the client layout must not produce a caption rect");
        Assert.False(slots.Hint.IsEmpty, "the client layout must produce a hint rect");

        // The hint sits in the slot the button would have used: right next to the main-menu button, with the
        // same minimum gap, instead of floating a button-height lower.
        float measured = slots.PlacedBelow
            ? slots.Hint.Y - slots.Menu.Bottom
            : slots.Menu.Y - slots.Hint.Bottom;

        Assert.True(measured >= GameOverLayout.MinGap, $"only {measured}px between the hint and 主菜单");
    }

    /// <summary>With room underneath the stack goes down; without it, it goes up instead of off screen.</summary>
    [Fact]
    public void TheStackFlipsAboveWhenThereIsNoRoomBelow()
    {
        GameOverSlots high = GameOverLayout.ComputeLayout(new LayoutRect(810f, 300f, 300f, 96f), DesktopWidth, DesktopHeight);
        Assert.True(high.PlacedBelow, "a mid-screen button has room below; the stack must not flip");

        GameOverSlots low = GameOverLayout.ComputeLayout(new LayoutRect(810f, 960f, 300f, 96f), DesktopWidth, DesktopHeight);
        Assert.False(low.PlacedBelow, "a bottom-anchored button has no room below; the stack must flip above");
        Assert.True(low.Button.Bottom <= low.Menu.Y, "the flipped stack must sit above 主菜单");
    }

    private static void AssertInside(in LayoutRect viewport, in LayoutRect rect, string what, string where)
    {
        if (rect.IsEmpty)
        {
            return;
        }

        Assert.True(rect.IsInside(viewport), $"{where}: the {what} rect {rect} leaves the viewport");
        Assert.True(rect.X >= 0f && rect.Y >= 0f, $"{where}: the {what} rect starts at a negative coordinate");
    }

    private static string Describe(string name, float viewportWidth, float viewportHeight, bool withButton) =>
        $"'{name}' in {viewportWidth}x{viewportHeight} (button={withButton})";
}
