"""Prepare Deep Waters fish inventory icon replacements.

Run from anywhere with:

    python prepare_fish_icons.py

The script reads the friendly fish source files in ../Flats and writes the
archive-style TEXTURE.216 replacement filenames used by DFU item icons.
"""

from pathlib import Path

try:
    from PIL import Image
except ImportError as exc:
    raise SystemExit("Pillow is required. Install it with: python -m pip install pillow") from exc

try:
    RESAMPLE_NEAREST = Image.Resampling.NEAREST
except AttributeError:
    RESAMPLE_NEAREST = Image.NEAREST


SCRIPT_DIR = Path(__file__).resolve().parent
FLATS_DIR = SCRIPT_DIR.parent / "Flats"

CANVAS_SIZE = 128
PADDING = 12
BACKUP_EXISTING = True

ICON_MAP = {
    "longnose_butterflyfish.png": "216_42-0.png",
    "largemouth_bass.png": "216_43-0.png",
    "canary_rockfish.png": "216_44-0.png",
    "crucian_carp.png": "216_45-0.png",
    "mackerel.png": "216_46-0.png",
    "white_zebra_angelfish.png": "216_47-0.png",
    "finulon.png": "216_48-0.png",
}


def alpha_bounds(image):
    if image.mode != "RGBA":
        image = image.convert("RGBA")
    return image.getchannel("A").getbbox() or image.getbbox()


def fit_pixel_art(image):
    bounds = alpha_bounds(image)
    cropped = image.crop(bounds).convert("RGBA") if bounds else image.convert("RGBA")

    max_size = CANVAS_SIZE - PADDING * 2
    scale = min(float(max_size) / cropped.width, float(max_size) / cropped.height)
    if scale >= 1.0:
        scale = max(1, int(scale))

    width = max(1, int(cropped.width * scale))
    height = max(1, int(cropped.height * scale))
    resized = cropped.resize((width, height), RESAMPLE_NEAREST)

    canvas = Image.new("RGBA", (CANVAS_SIZE, CANVAS_SIZE), (0, 0, 0, 0))
    x = (CANVAS_SIZE - width) // 2
    y = (CANVAS_SIZE - height) // 2
    canvas.alpha_composite(resized, (x, y))
    return canvas


def main():
    for source_name, output_name in ICON_MAP.items():
        source_path = FLATS_DIR / source_name
        output_path = FLATS_DIR / output_name

        image = Image.open(source_path)
        icon = fit_pixel_art(image)

        if BACKUP_EXISTING and output_path.exists():
            backup_path = output_path.with_suffix(output_path.suffix + ".bak")
            if not backup_path.exists():
                output_path.replace(backup_path)

        icon.save(output_path)
        print(f"{source_name} -> {output_name} ({CANVAS_SIZE}x{CANVAS_SIZE})")


if __name__ == "__main__":
    main()
