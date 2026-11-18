"""Generates packaging/image.png, the Steam Workshop cover for this mod.

Run from anywhere:  python packaging/make-image.py
Pillow only -- no other dependencies, no external art assets: every shape is drawn here.
Output is deterministic (no randomness, no timestamps), so re-running it yields identical bytes.

The layout targets the 512x512 preview that Steam shows at roughly 120 px wide, so the title is
large and the feature list uses one short line per row (<= 13 CJK glyphs, >= 22 px at 1x).
Everything is drawn on a 4x canvas and downsampled once, which is what keeps the strokes, the
glow and the CJK glyph edges clean.
"""

from __future__ import annotations

import math
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont

PREVIEW = 512
SCALE = 4
SIZE = PREVIEW * SCALE

# --- palette (deep wood / blood, warm gold) ---------------------------------
BG_TOP = (30, 17, 15)
BG_BOTTOM = (8, 6, 7)
GOLD_LIGHT = (255, 222, 156)
GOLD = (241, 178, 78)
GOLD_DEEP = (198, 126, 34)
GOLD_DIM = (126, 92, 42)
CREAM = (240, 232, 218)
MUTED = (158, 139, 116)
CARD_FILL_TOP = (44, 29, 27)
CARD_FILL_BOTTOM = (25, 17, 16)
CARD_EDGE = (78, 55, 37)
CHIP_BG = (17, 11, 10)
DARK = (10, 6, 6)

# --- layout (all values are 1x, multiplied by SCALE when drawn) -------------
TITLE = "游戏大厅增强"
SUBTITLE = "Game Lobby Enhance  ·  v2.1.0  ·  联机大厅扩展"
EYEBROW = "SLAY THE SPIRE 2   ·   v0.113"
FOOTER = "需游戏大厅 0.6.4  ·  联机双方都要安装"

PILL_TOP, PILL_HEIGHT, PILL_TEXT = 22, 30, 20
TITLE_CY, TITLE_SIZE = 96, 62
SUBTITLE_CY, SUBTITLE_SIZE = 148, 20
DIVIDER_Y = 174
CARD_X0, CARD_X1 = 34, 478
CARD_TOP, CARD_HEIGHT, CARD_GAP = 184, 40, 5
CARD_TEXT_X = 90
CARD_TEXT_SIZE = 21
FOOTER_CY, FOOTER_SIZE = 474, 20

FEATURES = [
    ("play", "结算页一键新开", " 全房回准备页"),
    ("bolt", "踢人不废房", " 掉线也能开"),
    ("restart", "读档免排队", " 不必按顺序"),
    ("grid", "所有服务器房间", " 一个列表"),
    ("funnel", "相同版本筛选", " 建房可选服"),
    ("phone", "手机滑聊天", " 长按不误触"),
]

FONT_DIR = Path("C:/Windows/Fonts")
FONT_STACKS = {
    "cjk_bold": ["msyhbd.ttc", "msyh.ttc", "simhei.ttf", "msjhbd.ttc"],
    "cjk": ["msyh.ttc", "simhei.ttf", "msjh.ttc"],
    "latin_bold": ["segoeuib.ttf", "seguisb.ttf", "arialbd.ttf"],
}

_cache: dict[tuple[str, int], ImageFont.FreeTypeFont] = {}
_used: dict[str, str] = {}


def font(kind: str, size: int) -> ImageFont.FreeTypeFont:
    key = (kind, size)
    if key in _cache:
        return _cache[key]
    for name in FONT_STACKS[kind]:
        path = FONT_DIR / name
        if not path.exists():
            continue
        try:
            face = ImageFont.truetype(str(path), size)
        except OSError:
            continue
        _cache[key] = face
        _used.setdefault(kind, name)
        return face
    raise SystemExit(
        "No usable font found for "
        + kind
        + ". Expected one of: "
        + ", ".join(FONT_STACKS[kind])
    )


def px(value: float) -> int:
    """Converts a 1x layout value to the 4x canvas."""
    return int(round(value * SCALE))


# --- painting helpers -------------------------------------------------------


def vertical_gradient(width: int, height: int, top, bottom, gamma: float = 1.0) -> Image.Image:
    strip = Image.new("RGB", (1, height))
    load = strip.load()
    span = max(1, height - 1)
    for y in range(height):
        t = (y / span) ** gamma
        load[0, y] = tuple(int(round(a + (b - a) * t)) for a, b in zip(top, bottom))
    return strip.resize((width, height), Image.BILINEAR)


def vertical_gradient_in(size: int, y0: int, y1: int, top, bottom) -> Image.Image:
    """Full-canvas gradient whose top colour sits at y0 and bottom colour at y1."""
    strip = Image.new("RGB", (1, size))
    load = strip.load()
    span = max(1, y1 - y0)
    for y in range(size):
        t = min(1.0, max(0.0, (y - y0) / span))
        load[0, y] = tuple(int(round(a + (b - a) * t)) for a, b in zip(top, bottom))
    return strip.resize((size, size), Image.BILINEAR)


def radial_mask(size: int, cx: float, cy: float, rx: float, ry: float, blur: float) -> Image.Image:
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).ellipse((cx - rx, cy - ry, cx + rx, cy + ry), fill=255)
    return mask.filter(ImageFilter.GaussianBlur(blur))


def paint(image: Image.Image, mask: Image.Image, colour, strength: float = 1.0) -> None:
    tint = Image.new("RGB", image.size, colour)
    image.paste(tint, (0, 0), mask.point(lambda v: int(v * strength)))


def text_mask(size: int, xy, text: str, face, anchor: str) -> Image.Image:
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).text(xy, text, font=face, fill=255, anchor=anchor)
    return mask


def draw_spaced(draw: ImageDraw.ImageDraw, cy: float, text: str, face, fill, spacing: float) -> float:
    widths = [draw.textlength(char, font=face) for char in text]
    total = sum(widths) + spacing * (len(text) - 1)
    x = (SIZE - total) / 2
    for char, width in zip(text, widths):
        draw.text((x, cy), char, font=face, fill=fill, anchor="lm")
        x += width + spacing
    return total


# --- icons (drawn inside a 30x30 chip, centred on cx/cy) --------------------


def icon_bolt(draw, cx, cy, s, colour, w) -> None:
    points = [
        (0.20, -1.00), (-0.56, 0.12), (-0.06, 0.12),
        (-0.20, 1.00), (0.56, -0.14), (0.06, -0.14),
    ]
    draw.polygon([(cx + x * s, cy + y * s) for x, y in points], fill=colour)


def arrow_head(draw, tip, direction, length, half_width, colour) -> None:
    dx, dy = direction
    norm = math.hypot(dx, dy) or 1.0
    dx, dy = dx / norm, dy / norm
    base_x, base_y = tip[0] - dx * length, tip[1] - dy * length
    nx, ny = -dy, dx
    draw.polygon(
        [
            tip,
            (base_x + nx * half_width, base_y + ny * half_width),
            (base_x - nx * half_width, base_y - ny * half_width),
        ],
        fill=colour,
    )


def icon_restart(draw, cx, cy, s, colour, w) -> None:
    """Two left-pointing wedges."""
    for offset in (0.10, -0.62):
        draw.polygon(
            [
                (cx + (offset + 0.52) * s, cy - 0.78 * s),
                (cx + (offset + 0.52) * s, cy + 0.78 * s),
                (cx + (offset - 0.30) * s, cy),
            ],
            fill=colour,
        )


def icon_play(draw, cx, cy, s, colour, w) -> None:
    box = (cx - s * 0.86, cy - s * 0.72, cx + s * 0.86, cy + s * 0.72)
    draw.rounded_rectangle(box, radius=s * 0.28, outline=colour, width=w)
    draw.polygon(
        [
            (cx - s * 0.20, cy - s * 0.34),
            (cx - s * 0.20, cy + s * 0.34),
            (cx + s * 0.36, cy),
        ],
        fill=colour,
    )


def icon_grid(draw, cx, cy, s, colour, w) -> None:
    cell, gap = s * 0.36, s * 0.10
    for dx in (-cell - gap, gap):
        for dy in (-cell - gap, gap):
            draw.rounded_rectangle(
                (cx + dx, cy + dy, cx + dx + cell, cy + dy + cell),
                radius=s * 0.07,
                outline=colour,
                width=w,
            )


def icon_funnel(draw, cx, cy, s, colour, w) -> None:
    draw.polygon(
        [
            (cx - 0.66 * s, cy - 0.56 * s), (cx + 0.66 * s, cy - 0.56 * s),
            (cx + 0.12 * s, cy + 0.02 * s), (cx + 0.12 * s, cy + 0.60 * s),
            (cx - 0.12 * s, cy + 0.42 * s), (cx - 0.12 * s, cy + 0.02 * s),
        ],
        fill=colour,
    )


def icon_phone(draw, cx, cy, s, colour, w) -> None:
    draw.rounded_rectangle(
        (cx - 0.44 * s, cy - 0.88 * s, cx + 0.44 * s, cy + 0.88 * s),
        radius=s * 0.22,
        outline=colour,
        width=w,
    )
    draw.line([(cx - 0.16 * s, cy + 0.58 * s), (cx + 0.16 * s, cy + 0.58 * s)], fill=colour, width=w)


ICONS = {
    "grid": icon_grid,
    "funnel": icon_funnel,
    "phone": icon_phone,
    "bolt": icon_bolt,
    "restart": icon_restart,
    "play": icon_play,
}


# --- build ------------------------------------------------------------------


def build() -> Image.Image:
    image = vertical_gradient(SIZE, SIZE, BG_TOP, BG_BOTTOM, gamma=0.92)

    # warm halo behind the title + a colder one low down, so the flat gradient reads as depth
    paint(image, radial_mask(SIZE, SIZE * 0.50, px(96), px(250), px(150), px(90)), (150, 74, 24), 0.42)
    paint(image, radial_mask(SIZE, SIZE * 0.50, SIZE * 1.02, px(330), px(200), px(110)), (96, 20, 16), 0.30)

    # hairline grid, deliberately faint
    grid = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    grid_draw = ImageDraw.Draw(grid)
    step = px(32)
    for x in range(step, SIZE, step):
        grid_draw.line([(x, 0), (x, SIZE)], fill=(255, 198, 132, 9), width=max(1, SCALE // 2))
    for y in range(step, SIZE, step):
        grid_draw.line([(0, y), (SIZE, y)], fill=(255, 198, 132, 9), width=max(1, SCALE // 2))
    image = Image.alpha_composite(image.convert("RGBA"), grid).convert("RGB")

    # two soft diagonal light streaks: enough sheen to stop the gradient reading as flat
    streaks = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    streak_draw = ImageDraw.Draw(streaks)
    for start_x, band, lean, alpha in ((-SIZE * 0.30, SIZE * 0.20, SIZE * 0.42, 13),
                                       (SIZE * 0.52, SIZE * 0.10, SIZE * 0.42, 9)):
        streak_draw.polygon(
            [(start_x, 0), (start_x + band, 0), (start_x + band - lean, SIZE), (start_x - lean, SIZE)],
            fill=(255, 214, 158, alpha),
        )
    streaks = streaks.filter(ImageFilter.GaussianBlur(px(26)))
    image = Image.alpha_composite(image.convert("RGBA"), streaks).convert("RGB")

    # vignette
    vignette = Image.new("L", (SIZE, SIZE), 0)
    ImageDraw.Draw(vignette).ellipse(
        (-SIZE * 0.22, -SIZE * 0.22, SIZE * 1.22, SIZE * 1.22), fill=255
    )
    vignette = vignette.filter(ImageFilter.GaussianBlur(px(46)))
    paint(image, vignette.point(lambda v: 255 - v), (0, 0, 0), 0.78)

    draw = ImageDraw.Draw(image)

    # frame + corner brackets
    inset = px(14)
    draw.rounded_rectangle(
        (inset, inset, SIZE - inset, SIZE - inset), radius=px(24), outline=(96, 68, 30), width=max(1, px(1.5))
    )
    arm, thick = px(38), px(3)
    for cx, cy, sx, sy in (
        (inset + px(6), inset + px(6), 1, 1),
        (SIZE - inset - px(6), inset + px(6), -1, 1),
        (inset + px(6), SIZE - inset - px(6), 1, -1),
        (SIZE - inset - px(6), SIZE - inset - px(6), -1, -1),
    ):
        draw.line([(cx, cy), (cx + arm * sx, cy)], fill=GOLD, width=thick)
        draw.line([(cx, cy), (cx, cy + arm * sy)], fill=GOLD, width=thick)

    # eyebrow pill
    eyebrow_face = font("latin_bold", px(PILL_TEXT))
    pill_text_w = sum(draw.textlength(c, font=eyebrow_face) for c in EYEBROW) + px(1.6) * (len(EYEBROW) - 1)
    pill_w = pill_text_w + px(38)
    pill_x0 = (SIZE - pill_w) / 2
    draw.rounded_rectangle(
        (pill_x0, px(PILL_TOP), pill_x0 + pill_w, px(PILL_TOP + PILL_HEIGHT)),
        radius=px(PILL_HEIGHT / 2),
        fill=(23, 15, 13),
        outline=GOLD_DIM,
        width=max(1, px(1.4)),
    )
    draw_spaced(draw, px(PILL_TOP + PILL_HEIGHT / 2), EYEBROW, eyebrow_face, (224, 184, 122), px(1.6))

    # title: glow -> dark stroke -> gold gradient fill
    title_face = font("cjk_bold", px(TITLE_SIZE))
    title_xy = (SIZE / 2, px(TITLE_CY))
    title_mask = text_mask(SIZE, title_xy, TITLE, title_face, "mm")
    paint(image, title_mask.filter(ImageFilter.GaussianBlur(px(13))), (255, 146, 44), 0.62)
    paint(image, title_mask.filter(ImageFilter.GaussianBlur(px(5))), (255, 190, 110), 0.28)
    draw.text(
        title_xy, TITLE, font=title_face, fill=(18, 9, 7), anchor="mm",
        stroke_width=max(1, px(3)), stroke_fill=(20, 11, 8),
    )
    image.paste(
        vertical_gradient_in(SIZE, px(TITLE_CY - 34), px(TITLE_CY + 34), GOLD_LIGHT, GOLD_DEEP),
        (0, 0),
        title_mask,
    )

    # subtitle
    draw.text(
        (SIZE / 2, px(SUBTITLE_CY)), SUBTITLE, font=font("cjk", px(SUBTITLE_SIZE)),
        fill=(206, 186, 154), anchor="mm",
    )

    # divider with a diamond
    line_w = max(1, px(1.6))
    draw.line([(px(112), px(DIVIDER_Y)), (px(238), px(DIVIDER_Y))], fill=GOLD_DIM, width=line_w)
    draw.line([(px(274), px(DIVIDER_Y)), (px(400), px(DIVIDER_Y))], fill=GOLD_DIM, width=line_w)
    half = px(7)
    draw.polygon(
        [(px(256), px(DIVIDER_Y) - half), (px(256) + half, px(DIVIDER_Y)),
         (px(256), px(DIVIDER_Y) + half), (px(256) - half, px(DIVIDER_Y))],
        fill=GOLD,
    )
    for x in (px(96), px(416)):
        draw.ellipse((x - px(2.5), px(DIVIDER_Y) - px(2.5), x + px(2.5), px(DIVIDER_Y) + px(2.5)), fill=GOLD_DIM)

    # feature cards
    accent_face = font("cjk_bold", px(CARD_TEXT_SIZE))
    body_face = font("cjk", px(CARD_TEXT_SIZE))
    for index, (icon, accent, body) in enumerate(FEATURES):
        top = px(CARD_TOP + index * (CARD_HEIGHT + CARD_GAP))
        bottom = top + px(CARD_HEIGHT)
        left, right = px(CARD_X0), px(CARD_X1)
        radius = px(10)

        card = vertical_gradient(int(right - left), int(bottom - top), CARD_FILL_TOP, CARD_FILL_BOTTOM)
        card_mask = Image.new("L", card.size, 0)
        ImageDraw.Draw(card_mask).rounded_rectangle(
            (0, 0, card.width - 1, card.height - 1), radius=radius, fill=255
        )
        image.paste(card, (int(left), int(top)), card_mask)
        draw.rounded_rectangle(
            (left, top, right, bottom), radius=radius, outline=CARD_EDGE, width=max(1, px(1.3))
        )

        # icon chip
        chip = px(15)
        cy = (top + bottom) / 2
        draw.rounded_rectangle(
            (left + px(12), cy - chip, left + px(12) + chip * 2, cy + chip),
            radius=px(7), fill=CHIP_BG, outline=(92, 65, 33), width=max(1, px(1.2)),
        )
        ICONS[icon](draw, left + px(12) + chip, cy, px(8.2), GOLD, max(1, px(2.4)))

        x = left + px(CARD_TEXT_X)
        draw.text((x, cy), accent, font=accent_face, fill=GOLD, anchor="lm")
        x += draw.textlength(accent, font=accent_face)
        draw.text((x, cy), body, font=body_face, fill=CREAM, anchor="lm")

    # footer
    draw.text(
        (SIZE / 2, px(FOOTER_CY)), FOOTER, font=font("cjk", px(FOOTER_SIZE)),
        fill=MUTED, anchor="mm",
    )

    return image.resize((PREVIEW, PREVIEW), Image.LANCZOS)


def main() -> int:
    image = build()
    target = Path(__file__).resolve().parent / "image.png"
    image.save(target, format="PNG", optimize=True)

    size = target.stat().st_size
    fonts = ", ".join(f"{k}={v}" for k, v in sorted(_used.items()))
    print(f"wrote {target} ({image.width}x{image.height}, {size} bytes)")
    print(f"fonts: {fonts}")
    if size >= 1024 * 1024:
        print("ERROR: Steam rejects previews of 1 MiB or more.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
