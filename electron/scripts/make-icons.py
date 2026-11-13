#!/usr/bin/env python3
"""Generate the InfraOrgIT desktop icon from the brand mark.

Requires Pillow and, for the macOS .icns, `iconutil` (macOS). Run from
`electron/`: `python3 scripts/make-icons.py`.
"""

from __future__ import annotations

import shutil
import subprocess
import tempfile
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "assets"
SCALE = 4
SIZE = 1024

BACKGROUND = (11, 18, 32, 255)
BORDER = (30, 41, 59, 255)
ACCENT = (94, 234, 212, 255)
ACCENT_DIM = (45, 212, 191, 235)
ACCENT_SOFT = (94, 234, 212, 205)


def rounded(value: int) -> int:
    return value * SCALE


def render() -> Image.Image:
    image = Image.new("RGBA", (rounded(SIZE), rounded(SIZE)), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    # Rounded dark tile, matching the control-room surface.
    draw.rounded_rectangle(
        (rounded(48), rounded(48), rounded(976), rounded(976)),
        radius=rounded(220),
        fill=BACKGROUND,
        outline=BORDER,
        width=rounded(8),
    )

    nodes = ((512, 292), (306, 704), (718, 704))
    links = ((0, 1), (1, 2), (2, 0))

    # Thick links survive the 16 px menu-bar size; the glow is decorative.
    glow = Image.new("RGBA", image.size, (0, 0, 0, 0))
    glow_draw = ImageDraw.Draw(glow)
    for start, end in links:
        glow_draw.line(
            (
                rounded(nodes[start][0]),
                rounded(nodes[start][1]),
                rounded(nodes[end][0]),
                rounded(nodes[end][1]),
            ),
            fill=(94, 234, 212, 80),
            width=rounded(78),
        )
    for x, y in nodes:
        glow_draw.ellipse(
            (rounded(x - 122), rounded(y - 122), rounded(x + 122), rounded(y + 122)),
            fill=(94, 234, 212, 75),
        )
    glow = glow.filter(ImageFilter.GaussianBlur(rounded(26)))
    image.alpha_composite(glow)

    for index, (start, end) in enumerate(links):
        draw.line(
            (
                rounded(nodes[start][0]),
                rounded(nodes[start][1]),
                rounded(nodes[end][0]),
                rounded(nodes[end][1]),
            ),
            fill=(94, 234, 212, 205 if index != 1 else 235),
            width=rounded(52),
        )

    for index, (x, y) in enumerate(nodes):
        color = ACCENT if index == 0 else ACCENT_DIM
        draw.ellipse(
            (rounded(x - 106), rounded(y - 106), rounded(x + 106), rounded(y + 106)),
            fill=color,
        )
        # Small dark center keeps the nodes distinct from plain dots.
        draw.ellipse(
            (rounded(x - 34), rounded(y - 34), rounded(x + 34), rounded(y + 34)),
            fill=BACKGROUND,
        )

    return image.resize((SIZE, SIZE), Image.Resampling.LANCZOS)

def write_png(image: Image.Image, path: Path, size: int) -> None:
    image.resize((size, size), Image.Resampling.LANCZOS).save(path)


def write_icns(image: Image.Image, path: Path) -> None:
    iconutil = shutil.which("iconutil")
    if not iconutil:
        print("iconutil not found; skipping .icns")
        return
    with tempfile.TemporaryDirectory() as temporary:
        iconset = Path(temporary) / "InfraOrgIT.iconset"
        iconset.mkdir()
        for size in (16, 32, 128, 256, 512):
            write_png(image, iconset / f"icon_{size}x{size}.png", size)
            write_png(image, iconset / f"icon_{size}x{size}@2x.png", size * 2)
        subprocess.run(
            [iconutil, "-c", "icns", "-o", str(path), str(iconset)],
            check=True,
        )


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)
    image = render()
    write_png(image, ASSETS / "icon.png", 1024)

    sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
    image.save(ASSETS / "icon.ico", sizes=sizes)
    write_icns(image, ASSETS / "icon.icns")
    print(f"Icons written to {ASSETS}")


if __name__ == "__main__":
    main()
