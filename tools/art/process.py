"""Turns Codex images (Art/gen/raw) into game assets (Assets/Art).

Characters are drawn on a flat chroma colour (people on pure green, PIP on pure magenta because PIP is
mint green). This removes that colour, recovers the true colour of semi-transparent edge pixels,
trims the empty margin and scales the sprite down. Backgrounds are centre-cropped to 16:9, the
shape of the game's stage.

    python -I tools/art/process.py              # every image that has a raw file
    python -I tools/art/process.py police_calm  # only these

Needs Pillow and numpy.
"""

import sys
from pathlib import Path

import numpy as np
from PIL import Image

REPO = Path(__file__).resolve().parents[2]
RAW = REPO / "Art" / "gen" / "raw"
CHARACTERS = REPO / "Assets" / "Art" / "Characters"
BACKGROUNDS = REPO / "Assets" / "Art" / "Backgrounds"

GREEN = "green"
MAGENTA = "magenta"

# name -> chroma key for characters; backgrounds have no key.
SPRITES = {
    "police_calm": GREEN,
    "police_stern": GREEN,
    "police_angry": GREEN,
    "scammer_real_smug": GREEN,
    "scammer_real_annoyed": GREEN,
    "pip_happy": MAGENTA,
    "pip_worried": MAGENTA,
}
SCENES = ["bg_street", "bg_living_room", "bg_call_center"]

MAX_SPRITE_HEIGHT = 1400
TRIM_PADDING = 8
BORDER = 4
MIN_LINE_PIXELS = 4


def keyness(rgb, key):
    """How much each pixel looks like the key colour: 255 on the backdrop, about 0 or below on the character."""
    r, g, b = rgb[..., 0], rgb[..., 1], rgb[..., 2]
    if key == GREEN:
        return g - np.maximum(r, b)
    return np.minimum(r, b) - g


def remove_key(rgb, key):
    key_rgb = np.array([0.0, 255.0, 0.0]) if key == GREEN else np.array([255.0, 0.0, 255.0])

    # An edge pixel is a mix: colour = a * character + (1 - a) * key. Its keyness falls linearly
    # from 255 (all backdrop) to about 0 (all character), which gives a.
    a = np.clip(1.0 - keyness(rgb, key) / 255.0, 0.0, 1.0)

    # Recover the character colour where the pixel is part backdrop.
    safe = np.maximum(a, 1e-3)[..., None]
    unmixed = (rgb - (1.0 - a)[..., None] * key_rgb) / safe
    edge = (a < 0.999)[..., None]
    out = np.where(edge, np.clip(unmixed, 0.0, 255.0), rgb)

    # Tighten the edge a little: backdrop noise below 6% goes fully clear, 94% and up fully solid.
    alpha = np.clip((a - 0.06) / 0.88, 0.0, 1.0)
    return out, alpha


def process_sprite(name, key):
    image = Image.open(RAW / f"{name}.png").convert("RGB")
    rgb = np.asarray(image, dtype=np.float64)
    colour, alpha = remove_key(rgb, key)

    # The generator sometimes leaves a faint line of pixels on the outer frame; clear it. People are cut
    # off at the knees, so losing a few rows at the bottom changes nothing (the dialogue box covers it).
    alpha[:BORDER, :] = 0.0
    alpha[-BORDER:, :] = 0.0
    alpha[:, :BORDER] = 0.0
    alpha[:, -BORDER:] = 0.0
    # Fully clear pixels keep no colour; Unity's alpha-is-transparency import fills them from the edge.
    colour[alpha == 0.0] = 0.0

    rgba = np.dstack([colour, alpha * 255.0]).round().astype(np.uint8)
    sprite = Image.fromarray(rgba, "RGBA")

    # Trim to the rows and columns that hold real content, ignoring single specks of noise.
    solid = rgba[..., 3] > 24
    rows = np.nonzero(solid.sum(axis=1) >= MIN_LINE_PIXELS)[0]
    cols = np.nonzero(solid.sum(axis=0) >= MIN_LINE_PIXELS)[0]
    if rows.size == 0 or cols.size == 0:
        raise SystemExit(f"{name}: nothing left after removing the {key} backdrop")
    left, top, right, bottom = cols[0], rows[0], cols[-1] + 1, rows[-1] + 1
    left = max(0, left - TRIM_PADDING)
    top = max(0, top - TRIM_PADDING)
    right = min(sprite.width, right + TRIM_PADDING)
    bottom = min(sprite.height, bottom + TRIM_PADDING)
    sprite = sprite.crop((left, top, right, bottom))

    if sprite.height > MAX_SPRITE_HEIGHT:
        scale = MAX_SPRITE_HEIGHT / sprite.height
        sprite = sprite.resize((round(sprite.width * scale), MAX_SPRITE_HEIGHT), Image.LANCZOS)

    # Block-compressed textures need sides that are multiples of 4: pad, keeping the feet at the bottom.
    width = (sprite.width + 3) // 4 * 4
    height = (sprite.height + 3) // 4 * 4
    if (width, height) != sprite.size:
        padded = Image.new("RGBA", (width, height), (0, 0, 0, 0))
        padded.paste(sprite, ((width - sprite.width) // 2, height - sprite.height))
        sprite = padded

    CHARACTERS.mkdir(parents=True, exist_ok=True)
    out = CHARACTERS / f"{name}.png"
    sprite.save(out, optimize=True)
    left_over = int((np.asarray(sprite)[..., 3] > 0).sum())
    print(f"{name}: {sprite.width}x{sprite.height}, {left_over} visible pixels -> {out.relative_to(REPO)}")


def process_scene(name):
    image = Image.open(RAW / f"{name}.png").convert("RGB")
    width = image.width
    height = round(width * 9 / 16 / 4) * 4
    if height > image.height:
        height = image.height
        width = round(height * 16 / 9 / 4) * 4
    left = (image.width - width) // 2
    top = (image.height - height) // 2
    scene = image.crop((left, top, left + width, top + height))

    BACKGROUNDS.mkdir(parents=True, exist_ok=True)
    out = BACKGROUNDS / f"{name}.png"
    scene.save(out, optimize=True)
    print(f"{name}: {scene.width}x{scene.height} -> {out.relative_to(REPO)}")


def main(names):
    wanted = names or list(SPRITES) + SCENES
    missing = []
    for name in wanted:
        if not (RAW / f"{name}.png").exists():
            missing.append(name)
            continue
        if name in SPRITES:
            process_sprite(name, SPRITES[name])
        elif name in SCENES:
            process_scene(name)
        else:
            raise SystemExit(f"unknown image '{name}'")
    if missing:
        print("not generated yet: " + ", ".join(missing))


if __name__ == "__main__":
    main(sys.argv[1:])
