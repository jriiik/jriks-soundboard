"""Generate the Jrik's Soundboard app icon: a segmented radial wheel with a music note inside.

Every size is rendered separately (8x supersampled, then downscaled) so the small sizes used in
the taskbar and title bar stay crisp, and all of them are packed into one multi-size .ico.
Only basic shapes are drawn (no font glyphs), so the artwork is entirely original.

Usage:  python tools/make_icon.py <output-folder>
Writes: app.ico, icon-256.png, icon-preview.png
Needs:  Pillow  (pip install pillow)
"""
import math
import os
import sys

from PIL import Image, ImageDraw

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
SUPERSAMPLE = 8

PURPLE = (168, 85, 247)
PINK = (236, 72, 153)
DISC_TOP = (46, 46, 56)
DISC_BOTTOM = (16, 16, 20)
SEGMENT = (255, 255, 255, 64)
RIM = (255, 255, 255, 36)
WHITE = (255, 255, 255, 255)
HIGHLIGHTED_SEGMENT = 1  # up-right, like a wheel slice picked with a flick

_gradients = {}


def point(cx, cy, radius, degrees):
    """Point on a circle; 0 degrees is straight up, angles go clockwise (screen coordinates)."""
    a = math.radians(degrees)
    return cx + radius * math.sin(a), cy - radius * math.cos(a)


def sector(cx, cy, inner, outer, start, end, step=0.5):
    """Polygon for a ring segment between two radii and two angles."""
    n = max(2, int((end - start) / step))
    outer_edge = [point(cx, cy, outer, start + (end - start) * i / n) for i in range(n + 1)]
    inner_edge = [point(cx, cy, inner, end - (end - start) * i / n) for i in range(n + 1)]
    return outer_edge + inner_edge


def ellipse(cx, cy, rx, ry, rotation_degrees, steps=120):
    """Polygon for a rotated ellipse (used for the tilted note heads)."""
    phi = math.radians(rotation_degrees)
    points = []
    for i in range(steps):
        t = 2 * math.pi * i / steps
        x, y = rx * math.cos(t), ry * math.sin(t)
        points.append((cx + x * math.cos(phi) - y * math.sin(phi), cy + x * math.sin(phi) + y * math.cos(phi)))
    return points


def gradient(size, start, end, diagonal):
    """Smooth RGB gradient (vertical, or top-left to bottom-right), cached per size."""
    key = (size, start, end, diagonal)
    if key not in _gradients:
        base = 256
        img = Image.new("RGB", (base, base))
        px = img.load()
        for y in range(base):
            for x in range(base):
                t = (x + y) / (2 * (base - 1)) if diagonal else y / (base - 1)
                px[x, y] = tuple(round(s + (e - s) * t) for s, e in zip(start, end))
        _gradients[key] = img.resize((size, size), Image.BILINEAR)
    return _gradients[key]


def render(size):
    small = size <= 24  # bolder shapes so the tiny sizes still read
    s = size * SUPERSAMPLE
    k = s / 256.0  # design units (a 256 x 256 canvas) -> pixels
    c = s / 2
    icon = Image.new("RGBA", (s, s), (0, 0, 0, 0))

    def shape_mask(polygons=()):
        mask = Image.new("L", (s, s), 0)
        draw = ImageDraw.Draw(mask)
        for polygon in polygons:
            draw.polygon(polygon, fill=255)
        return mask

    def fill(mask, paint):
        layer = Image.new("RGBA", (s, s), (0, 0, 0, 0))
        source = paint if isinstance(paint, Image.Image) else Image.new("RGBA", (s, s), paint)
        layer.paste(source, (0, 0), mask)
        icon.alpha_composite(layer)

    # Dark disc with a faint rim.
    disc = 124 * k
    fill(shape_mask([ellipse(c, c, disc, disc, 0, 240)]), gradient(s, DISC_TOP, DISC_BOTTOM, diagonal=False))
    fill(shape_mask([sector(c, c, disc - 3 * k, disc, 0, 360)]), RIM)

    # The wheel: 8 ring segments, one of them lit.
    inner, outer, gap = (78, 112, 12) if small else (86, 110, 7)
    for i in range(8):
        mid = i * 45
        segment = shape_mask([sector(c, c, inner * k, outer * k, mid - 22.5 + gap / 2, mid + 22.5 - gap / 2)])
        fill(segment, gradient(s, PURPLE, PINK, diagonal=True) if i == HIGHLIGHTED_SEGMENT else SEGMENT)

    # Two beamed notes in the middle.
    rx, ry = (19, 14) if small else (17, 12.5)
    stem_width = 10 if small else 7
    beam_height = 21 if small else 17
    tilt = -22
    stem_height = 72
    heads = [(101, 160), (157, 148)]
    right_edge = math.hypot(rx * math.cos(math.radians(tilt)), ry * math.sin(math.radians(tilt)))

    shapes = []
    stems = []
    for hx, hy in heads:
        shapes.append(ellipse(hx * k, hy * k, rx * k, ry * k, tilt))
        x_right = hx + right_edge
        top = hy - stem_height
        stems.append((x_right - stem_width, x_right, top))
        shapes.append([((x_right - stem_width) * k, top * k), (x_right * k, top * k), (x_right * k, hy * k), ((x_right - stem_width) * k, hy * k)])
    (left, _, top_left), (_, right, top_right) = stems
    shapes.append([(left * k, top_left * k), (right * k, top_right * k), (right * k, (top_right + beam_height) * k), (left * k, (top_left + beam_height) * k)])
    fill(shape_mask(shapes), WHITE)

    # Downscale in premultiplied alpha so edges don't pick up dark fringes.
    return icon.convert("RGBa").resize((size, size), Image.LANCZOS).convert("RGBA")


def preview(images):
    """Sheet with every size on a dark and a light background, plus 16/32 px magnified 4x."""
    items = [256, 64, 48, 32, 24, 16]
    gap, row_height = 16, 288
    width = gap + sum(items) + 64 + 128 + gap * (len(items) + 2)
    sheet = Image.new("RGBA", (width, row_height * 2), (0, 0, 0, 255))
    for row, background in enumerate([(32, 32, 32, 255), (243, 243, 243, 255)]):
        y0 = row * row_height
        sheet.paste(Image.new("RGBA", (width, row_height), background), (0, y0))
        x = gap
        for size in items:
            sheet.alpha_composite(images[size], (x, y0 + (row_height - size) // 2))
            x += size + gap
        for size in (16, 32):
            big = images[size].resize((size * 4, size * 4), Image.NEAREST)
            sheet.alpha_composite(big, (x, y0 + (row_height - size * 4) // 2))
            x += size * 4 + gap
    return sheet


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    out = sys.argv[1]
    os.makedirs(out, exist_ok=True)

    images = {size: render(size) for size in SIZES}
    largest = images[SIZES[-1]]
    ico_path = os.path.join(out, "app.ico")
    largest.save(ico_path, format="ICO", sizes=[(n, n) for n in SIZES], append_images=[images[n] for n in SIZES[:-1]])
    largest.save(os.path.join(out, "icon-256.png"))
    preview(images).save(os.path.join(out, "icon-preview.png"))

    with Image.open(ico_path) as ico:
        print(f"app.ico sizes: {sorted(ico.info.get('sizes', []))}")


if __name__ == "__main__":
    main()
