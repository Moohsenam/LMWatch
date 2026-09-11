"""Generates Assets/app.ico — a warm dark tile with a coral watch ring."""
from PIL import Image, ImageDraw
from pathlib import Path

OUT = Path(__file__).resolve().parent.parent / "src" / "ClaudeWatch.App" / "Assets" / "app.ico"
OUT.parent.mkdir(parents=True, exist_ok=True)

BASE = 1024
TILE = (27, 26, 24, 255)
CORAL = (217, 119, 87, 255)
CREAM = (240, 238, 230, 255)


def rounded(size, radius, fill):
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    ImageDraw.Draw(img).rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=fill)
    return img


canvas = rounded(BASE, int(BASE * 0.22), TILE)
draw = ImageDraw.Draw(canvas)

cx = cy = BASE / 2
outer = BASE * 0.33
ring = BASE * 0.075

# Open ring: a watch face with a gap at the top right.
draw.arc(
    [cx - outer, cy - outer, cx + outer, cy + outer],
    start=-52, end=232, fill=CORAL, width=int(ring),
)

# Pupil.
dot = BASE * 0.105
draw.ellipse([cx - dot, cy - dot, cx + dot, cy + dot], fill=CREAM)

# Tick at the gap, so the mark reads at 16px too.
tick = BASE * 0.055
tx, ty = cx + outer * 0.80, cy - outer * 0.80
draw.ellipse([tx - tick, ty - tick, tx + tick, ty + tick], fill=CORAL)

sizes = [16, 24, 32, 48, 64, 128, 256]
canvas.save(OUT, format="ICO", sizes=[(s, s) for s in sizes])
print(f"wrote {OUT} ({OUT.stat().st_size} bytes)")

png = OUT.with_name("preview.png")
canvas.resize((256, 256), Image.LANCZOS).save(png)
print(f"wrote {png}")
