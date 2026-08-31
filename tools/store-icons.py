#!/usr/bin/env python3
"""Render every Windows icon from docs/QSLogo.png.

Package images go in packaging/windows/Images, Store listing images in
dist/store-images. The mark is white on the app's dark background so it reads
on a light or dark Store card; the unplated pair is the shell's own theming.

    python3 tools/store-icons.py
"""
from pathlib import Path
from PIL import Image

BG = (0x0F, 0x12, 0x16)      # Palette.Dark AppBackground
MARK = (0xF0, 0xF4, 0xF6)    # Palette.Dark TextPrimary
INK = (0x16, 0x19, 0x1C)     # Palette.Light TextPrimary

ROOT = Path(__file__).resolve().parent.parent
SRC = Image.open(ROOT / "docs/QSLogo.png").convert("RGBA")


def render(size, colour, bg, fill=0.78):
    """The mark at `fill` of the shorter side, centred, on `bg` (None = clear)."""
    w, h = size
    side = int(min(w, h) * fill)
    mask = SRC.split()[3].resize((side, side), Image.LANCZOS)
    out = Image.new("RGBA", size, (*bg, 255) if bg else (0, 0, 0, 0))
    out.paste(Image.new("RGBA", (side, side), (*colour, 255)),
              ((w - side) // 2, (h - side) // 2), mask)
    return out


def save(img, path):
    path.parent.mkdir(parents=True, exist_ok=True)
    img.save(path)
    print(path.relative_to(ROOT))


pkg = ROOT / "packaging/windows/Images"
save(render((50, 50), MARK, BG), pkg / "StoreLogo.png")
save(render((150, 150), MARK, BG), pkg / "Square150x150Logo.png")
save(render((44, 44), MARK, BG), pkg / "Square44x44Logo.png")
# Unplated: no tile behind the mark, so the shell's theme picks the colour.
for n in (16, 24, 32, 48, 256):
    save(render((n, n), MARK, None), pkg / f"Square44x44Logo.targetsize-{n}_altform-unplated.png")
    save(render((n, n), INK, None), pkg / f"Square44x44Logo.targetsize-{n}_altform-lightunplated.png")

store = ROOT / "dist/store-images"
for n in (71, 150, 300):
    save(render((n, n), MARK, BG), store / f"tile-{n}x{n}.png")
for n in (1080, 2160):
    save(render((n, n), MARK, BG), store / f"boxart-{n}x{n}.png")
for w, h in ((720, 1080), (1440, 2160)):
    save(render((w, h), MARK, BG, fill=0.62), store / f"poster-{w}x{h}.png")
