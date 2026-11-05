# -*- coding: utf-8 -*-
"""
Icon for the HLA Broker Translation Test Kit.

The mark is a test report reduced to its verdict: one case passed, one failed.
A tick and a cross say "this is a thing that judges other things" faster than
any other pairing, which is what an icon for a test kit has to do.

Meaning is carried by SHAPE as well as colour - a tick and a cross are
distinguishable with no colour vision at all, so the red/cyan pairing is
reinforcement rather than the only signal.

Two designs, not one scaled. Below 32px the two name pills are dropped and the
tick and cross grow to fill the tile: four elements in a 16px square gives each
about six pixels, which reads as texture rather than as anything. Windows picks
per context, so the taskbar gets the bold verdict and the file preview gets the
full report.

Colours are the app's own (Themes/Precision.Dark.xaml) plus one semantic red, so
the icon and the window it launches read as the same product.
"""
import io, struct
from PIL import Image, ImageDraw

BG     = (0x1B, 0x20, 0x26, 255)   # SurfaceBackgroundColor
PASS   = (0x37, 0xB6, 0xD8, 255)   # AccentBackgroundColor - the tick
FAIL   = (0xF8, 0x71, 0x71, 255)   # the cross
LIGHT  = (0xE6, 0xEB, 0xF1, 255)   # TextPrimaryColor - the case names
EDGE   = (0x37, 0xB6, 0xD8, 60)    # faint rim so it lifts off a dark taskbar

S = 1024


def disc(d, cx, cy, r, fill):
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=fill)


def stroke(d, pts, w, fill):
    """Polyline with round joins and caps - PIL gives neither for free."""
    d.line(pts, fill=fill, width=w, joint="curve")
    for (x, y) in pts:
        disc(d, x, y, w // 2, fill)


def render(simple):
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([0, 0, S - 1, S - 1], radius=int(S * 0.22), fill=BG)
    d.rounded_rectangle([0, 0, S - 1, S - 1], radius=int(S * 0.22), outline=EDGE, width=8)

    if simple:
        # Verdict only. Both marks grow to fill the tile, because at 16px a shape
        # smaller than about half the width does not survive the downsample.
        # Set on a DIAGONAL, not stacked. Stacked, the tick's trough and the
        # cross's top edge land about a pixel apart at 16px and merge into one
        # tangled mass; offsetting them buys separation without shrinking either.
        stroke(d, [(170, 330), (300, 460), (560, 190)], 116, PASS)
        cx, cy, a, w = 650, 690, 152, 116
        stroke(d, [(cx - a, cy - a), (cx + a, cy + a)], w, FAIL)
        stroke(d, [(cx + a, cy - a), (cx - a, cy + a)], w, FAIL)
        return img

    # Full report: a passing case and a failing one, each with its name beside it.
    stroke(d, [(168, 372), (272, 476), (452, 268)], 92, PASS)
    d.rounded_rectangle([566, 330, 884, 412], radius=41, fill=LIGHT)

    cx, cy, a, w = 300, 706, 108, 92
    stroke(d, [(cx - a, cy - a), (cx + a, cy + a)], w, FAIL)
    stroke(d, [(cx + a, cy - a), (cx - a, cy + a)], w, FAIL)
    # Shorter, so the two rows read as different cases rather than a repeated motif.
    d.rounded_rectangle([566, 665, 772, 747], radius=41, fill=LIGHT)

    return img


def png_bytes(img, size):
    buf = io.BytesIO()
    img.resize((size, size), Image.LANCZOS).save(buf, format="PNG")
    return buf.getvalue()


detailed, simple = render(False), render(True)

frames = [(s, png_bytes(simple if s < 32 else detailed, s))
          for s in (16, 20, 24, 32, 48, 64, 128, 256)]

out = io.BytesIO()
out.write(struct.pack("<HHH", 0, 1, len(frames)))
offset = 6 + 16 * len(frames)
for size, data in frames:
    out.write(struct.pack("<BBBBHHII", size if size < 256 else 0,
                          size if size < 256 else 0, 0, 0, 1, 32, len(data), offset))
    offset += len(data)
for _, data in frames:
    out.write(data)

open("btk.ico", "wb").write(out.getvalue())
detailed.resize((256, 256), Image.LANCZOS).save("preview-256.png")

strip = Image.new("RGBA", (520, 150), (0x14, 0x18, 0x1D, 255))
x = 24
for s in (16, 24, 32, 48, 64, 128):
    f = (simple if s < 32 else detailed).resize((s, s), Image.LANCZOS)
    strip.paste(f, (x, 24 + (128 - s) // 2), f)
    x += s + 24
strip.save("preview-sizes.png")

print("btk.ico", len(out.getvalue()), "bytes,", len(frames), "sizes")
