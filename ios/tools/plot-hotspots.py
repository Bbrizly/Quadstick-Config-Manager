#!/usr/bin/env python3
"""Draw every model's hotspots and mode lights onto its own photo.

DeviceModelTests pins each photo's pixel size, so a swapped file fails
loudly. It cannot tell you a point is on the wrong part of a photo that is
still the right size: the arithmetic all checks out and the ring still
lands on a screw. Only a person looking at the picture catches that. This
script parses the shipped DeviceHotspot.swift and draws its numbers on the
real photos, so a review is "look at three PNGs," not "read four decimal
places and picture where they land."

Usage: python3 plot-hotspots.py [output-dir]

Depends only on Pillow (pip install pillow).
"""
import re
import sys
from pathlib import Path

from PIL import Image, ImageDraw

REPO = Path(__file__).resolve().parents[2]
SOURCE = REPO / "ios/QuadStickKit/Sources/QuadStickKit/DeviceHotspot.swift"
ASSETS = REPO / "ios/App/Assets.xcassets"

# A Swift numeric literal in this file is either a plain fraction ("0.3275")
# or a measured-pixel expression ("690.0 / 2048"). Both mean the same thing
# once divided out, so one pattern reads both.
NUMBER = re.compile(r"(-?[\d.]+)\s*(?:/\s*([\d.]+))?")


def number(text: str) -> float:
    numerator, denominator = NUMBER.match(text.strip()).groups()
    value = float(numerator)
    return value / float(denominator) if denominator else value


def numbers(text: str) -> list[float]:
    return [number(m.group(0)) for m in NUMBER.finditer(text) if m.group(0).strip()]


def field(pattern: str, body: str, default: float = 0.0) -> float:
    m = re.search(pattern + r": ([\d./ ]+)", body)
    return number(m.group(1)) if m else default


def plot(name: str, body: str, out_dir: Path) -> None:
    asset = re.search(r'assetName: "([^"]+)"', body).group(1)
    source_x = field("sourceX", body)
    source_y = field("sourceY", body)
    source_w = field("sourceWidth", body, default=1.0)
    source_h = field("sourceHeight", body, default=1.0)

    spots = re.findall(
        r'DeviceHotspot\(inputID: "([^"]+)", x: ([\d.]+), y: ([\d.]+), shortName: "([^"]+)"\)', body)
    lights = re.search(r"ModeLightRow\(\s*x: ([\d./ ]+), gap: ([\d./ ]+), y: ([\d./ ]+)(.*?)\)\)", body, re.S)

    png = next((ASSETS / f"{asset}.imageset").glob("*.png"))
    image = Image.open(png).convert("RGBA")
    width, height = image.size
    draw = ImageDraw.Draw(image)

    # The crop the app actually draws.
    box = (source_x * width, source_y * height,
           (source_x + source_w) * width, (source_y + source_h) * height)
    draw.rectangle(box, outline=(80, 180, 255, 255), width=4)

    ring = width * 0.035
    for input_id, x, y, short in spots:
        px, py = float(x) * width, float(y) * height
        draw.ellipse((px - ring, py - ring, px + ring, py + ring), outline=(255, 110, 20, 255), width=6)
        draw.text((px + ring + 4, py - ring), f"{short} ({input_id})", fill=(255, 255, 255, 255))

    if lights:
        lx, gap, ly, rest = number(lights.group(1)), number(lights.group(2)), number(lights.group(3)), lights.group(4)
        points_match = re.search(r"points:\s*\[(.*?)\]", rest, re.S)
        points = numbers(points_match.group(1)) if points_match else None
        for i in range(5):
            cx = (points[i] if points else lx + i * gap) * width
            cy = ly * height
            r = ring / 2
            draw.ellipse((cx - r, cy - r, cx + r, cy + r), outline=(60, 255, 120, 255), width=4)

    dest = out_dir / f"hotspots-{name}.png"
    image.save(dest)
    print(f"{name}: {asset} {width}x{height}, {len(spots)} hotspots, "
          f"lights={'measured' if lights and points_match else 'x+gap' if lights else 'none'} -> {dest}")


def main() -> None:
    out_dir = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(".")
    out_dir.mkdir(parents=True, exist_ok=True)

    text = SOURCE.read_text()
    blocks = re.split(r"\n    static let (\w+) = DevicePhoto\(", text)
    for model_name, body in zip(blocks[1::2], blocks[2::2]):
        plot(model_name, body, out_dir)


if __name__ == "__main__":
    main()
