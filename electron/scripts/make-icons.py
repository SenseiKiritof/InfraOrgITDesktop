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

    layers = ((310, ACCENT_DIM), (410, ACCENT), (510, ACCENT_DIM))

    # Soft emerald glow behind the stack.
    glow = Image.new("RGBA", image.size, (0, 0, 0, 0))
    glow_draw = ImageDraw.Draw(glow)
    for y, color in layers:
        glow_draw.rounded_rectangle(
            (rounded(260), rounded(y), rounded(764), rounded(y + 70)),
            radius=rounded(35),
            fill=(color[0], color[1], color[2], 55),
        )
    glow_draw.ellipse(
        (rounded(452), rounded(204), rounded(572), rounded(324)),
        fill=(ACCENT[0], ACCENT[1], ACCENT[2], 60),
    )
    glow = glow.filter(ImageFilter.GaussianBlur(rounded(30)))
    image.alpha_composite(glow)

    for y, color in layers:
        draw.rounded_rectangle(
            (rounded(260), rounded(y), rounded(764), rounded(y + 70)),
            radius=rounded(35),
            fill=(17, 29, 48, 255),
            outline=color,
            width=rounded(12),
        )
        draw.rounded_rectangle(
            (rounded(340), rounded(y + 26), rounded(684), rounded(y + 44)),
            radius=rounded(9),
            fill=(94, 234, 212, 100),
        )

    # Central spine and top node: the stack reads as one connected system.
    draw.line(
        (rounded(512), rounded(230), rounded(512), rounded(790)),
        fill=(94, 234, 212, 120),
        width=rounded(10),
    )
    draw.ellipse(
        (rounded(452), rounded(204), rounded(572), rounded(324)),
        fill=ACCENT,
    )
    draw.ellipse(
        (rounded(472), rounded(224), rounded(552), rounded(304)),
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
