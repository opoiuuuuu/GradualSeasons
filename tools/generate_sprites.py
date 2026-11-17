#!/usr/bin/env python3
"""Generate the Gradual Seasons sprite sheets and an enlarged preview from the pixel art in SpriteFactory.cs.

The pixel art lives in one place only: the PALETTE, PARTICLE-CELLS and GROUND-CELLS blocks of
GradualSeasons/SpriteFactory.cs. The mod uses those grids as its built-in fallback; this script turns the same
grids into the PNGs, so the two can never drift apart.

Snow, the spring thaw and each season's ground colour aren't drawn from these sheets: in game they come from
Stardew Valley's own seasonal tiles (see SeasonTileLayer.cs), so they can't be previewed here.

Usage:
    python tools/generate_sprites.py                          # write both sheets and the preview
    python tools/generate_sprites.py --check-particles X.png  # check a PNG matches the particle art exactly
    python tools/generate_sprites.py --check-ground X.png     # check a PNG matches the ground decoration art exactly

Needs Pillow:  python -m pip install pillow
"""

import argparse
import re
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "GradualSeasons" / "SpriteFactory.cs"
PARTICLES_PATH = ROOT / "GradualSeasons" / "assets" / "particles.png"
GROUND_PATH = ROOT / "GradualSeasons" / "assets" / "ground.png"
PREVIEW_PATH = ROOT / "preview" / "particles_preview.png"

PARTICLE_CELL = 8
PARTICLE_COUNT = 10
GROUND_CELL = 16
GROUND_COUNT = 2
PARTICLE_NAMES = [
    "snow S", "snow M", "snow L",
    "orange leaf", "red leaf", "yellow leaf", "brown leaf",
    "petal light", "petal deep", "sleet",
]
GROUND_NAMES = ["grass tuft", "summer flowers"]

# cell indexes (same as SpriteFactory.cs)
GRASS_TUFT, SUMMER_FLOWERS = 0, 1
LEAF_ORANGE, LEAF_RED, LEAF_YELLOW, LEAF_BROWN, PETAL_LIGHT, PETAL_DEEP = 3, 4, 5, 6, 7, 8


# ---------------------------------------------------------------------------
# parsing
# ---------------------------------------------------------------------------
def read_block(text, name):
    match = re.search(r"//\s*%s-BEGIN(.*?)//\s*%s-END" % (name, name), text, re.S)
    if not match:
        sys.exit(f"Couldn't find the // {name}-BEGIN ... // {name}-END block in {SOURCE}")
    return match.group(1)


def parse_palette(text):
    palette = {}
    for key, value in re.findall(r"\('(.)',\s*0x([0-9A-Fa-f]{8})\)", read_block(text, "PALETTE")):
        number = int(value, 16)
        palette[key] = ((number >> 24) & 255, (number >> 16) & 255, (number >> 8) & 255, number & 255)
    return palette


def parse_cells(text, block, size, count, palette):
    rows = re.findall(r'"([^"\n]*)"', read_block(text, block))
    if len(rows) != size * count:
        sys.exit(f"{block}: expected {count} cells of {size} rows ({size * count} strings), found {len(rows)} strings.")
    cells = [rows[i:i + size] for i in range(0, len(rows), size)]
    for index, cell in enumerate(cells):
        for y, row in enumerate(cell):
            if len(row) != size:
                sys.exit(f"{block} cell {index}, row {y}: expected {size} characters, found {len(row)}: {row!r}")
            unknown = sorted(set(row) - set(palette))
            if unknown:
                sys.exit(f"{block} cell {index}, row {y}: characters not in the palette: {unknown}")
    return cells


def build_sheet(palette, cells, size):
    sheet = Image.new("RGBA", (size * len(cells), size), (0, 0, 0, 0))
    pixels = sheet.load()
    for index, cell in enumerate(cells):
        for y, row in enumerate(cell):
            for x, key in enumerate(row):
                pixels[index * size + x, y] = palette[key]
    return sheet


def cell_image(sheet, size, index):
    return sheet.crop((index * size, 0, index * size + size, size))


# ---------------------------------------------------------------------------
# the decoration placement from GroundLayer.cs, ported so the preview shows the real patterns
# ---------------------------------------------------------------------------
M32 = 0xFFFFFFFF


def hash32(seed, x, y, salt):
    h = (seed ^ 0x9E3779B9) & M32
    h ^= ((x & M32) * 0x85EBCA6B) & M32
    h = ((((h << 13) | (h >> 19)) & M32) * 5 + 0xE6546B64) & M32
    h ^= ((y & M32) * 0xC2B2AE35) & M32
    h = ((((h << 13) | (h >> 19)) & M32) * 5 + 0xE6546B64) & M32
    h ^= ((salt & M32) * 0x27D4EB2F) & M32
    h ^= h >> 16
    h = (h * 0x85EBCA6B) & M32
    h ^= h >> 13
    h = (h * 0xC2B2AE35) & M32
    h ^= h >> 16
    return h


def stable_hash(text):
    h = 2166136261
    for ch in text:
        h ^= ord(ch)
        h = (h * 16777619) & M32
    return h


def to_unit(h):
    return (h >> 8) / 16777216.0


def value_noise(seed, x, y, cell_size, salt):
    fx, fy = x / cell_size, y / cell_size
    x0, y0 = int(fx // 1), int(fy // 1)
    smooth = lambda t: t * t * (3 - 2 * t)
    tx, ty = smooth(fx - x0), smooth(fy - y0)
    a, b = to_unit(hash32(seed, x0, y0, salt)), to_unit(hash32(seed, x0 + 1, y0, salt))
    c, d = to_unit(hash32(seed, x0, y0 + 1, salt)), to_unit(hash32(seed, x0 + 1, y0 + 1, salt))
    top, bottom = a + (b - a) * tx, c + (d - c) * tx
    return top + (bottom - top) * ty


def noise(seed, x, y):
    return 0.65 * value_noise(seed, x, y, 7.0, 1) + 0.35 * value_noise(seed, x, y, 3.0, 2)


def thresholds(seed, width, height):
    raw = sorted((0.72 * noise(seed, x, y) + 0.28 * to_unit(hash32(seed, x, y, 99)), (x, y)) for y in range(height) for x in range(width))
    return {xy: (i + 0.5) / len(raw) for i, (_, xy) in enumerate(raw)}


def jitter(h, shift, limit, zoom):
    span = limit * 2 + 1
    return (((h >> shift) % span) - limit) * zoom, (((h >> (shift + 5)) % span) - limit) * zoom


def flip_h(h, bit):
    return (h >> (24 + bit)) & 1 == 1


def paste(scene, sprite, x, y):
    """Alpha-composite a sprite at (x, y), clipped to the scene (positions may be negative)."""
    left, top = max(0, x), max(0, y)
    right, bottom = min(scene.width, x + sprite.width), min(scene.height, y + sprite.height)
    if right <= left or bottom <= top:
        return
    part = sprite.crop((left - x, top - y, right - x, bottom - y))
    scene.alpha_composite(part, (left, top))


def with_alpha(image, alpha):
    if alpha >= 1:
        return image
    r, g, b, a = image.split()
    return Image.merge("RGBA", (r, g, b, a.point(lambda v: int(v * alpha))))


# ---------------------------------------------------------------------------
# preview
# ---------------------------------------------------------------------------
def load_font(size):
    for name in ("segoeui.ttf", "arial.ttf", "DejaVuSans.ttf"):
        for folder in (Path("C:/Windows/Fonts"), Path("/usr/share/fonts/truetype/dejavu"), Path("/Library/Fonts")):
            path = folder / name
            if path.exists():
                return ImageFont.truetype(str(path), size)
    return ImageFont.load_default()


def checkerboard(width, height, square, light=(236, 236, 236, 255), dark=(206, 206, 206, 255)):
    board = Image.new("RGBA", (width, height), light)
    draw = ImageDraw.Draw(board)
    for y in range(0, height, square):
        for x in range(0, width, square):
            if (x // square + y // square) % 2:
                draw.rectangle([x, y, x + square - 1, y + square - 1], fill=dark)
    return board


def text_width(draw, text, font):
    left, _, right, _ = draw.textbbox((0, 0), text, font=font)
    return right - left


GROUND_COLOURS = {
    # base colour, speckle colour (rough stand-ins for each season's grass)
    "spring": ((112, 178, 70, 255), (92, 156, 56, 255)),
    "summer": ((78, 150, 52, 255), (62, 128, 42, 255)),
    "fall": ((182, 132, 66, 255), (158, 108, 52, 255)),
    "winter": ((234, 240, 248, 255), (206, 220, 238, 255)),
}


def ground_scene(particles, ground, season, kind, coverage, tiles=(6, 4), zoom=4, seed_name="Farm", leaf_palette="autumn"):
    """Draw a small patch of ground with the mod's decorations, the same way GroundLayer.cs places them."""
    width, height = tiles
    tile_px = GROUND_CELL * zoom
    base, speckle = GROUND_COLOURS[season]
    scene = Image.new("RGBA", (width * tile_px, height * tile_px), base)
    draw = ImageDraw.Draw(scene)
    seed = stable_hash(seed_name)

    # a little texture so it reads as ground
    for y in range(0, height * tile_px, zoom):
        for x in range(0, width * tile_px, zoom):
            if hash32(seed, x // zoom, y // zoom, 5) % 9 == 0:
                draw.rectangle([x, y, x + zoom - 1, y + zoom - 1], fill=speckle)

    limits = thresholds(seed, width, height)
    leaf_sprites = [LEAF_ORANGE, LEAF_RED, LEAF_YELLOW] if leaf_palette == "autumn" else [LEAF_BROWN, LEAF_BROWN, LEAF_ORANGE]

    for y in range(height):
        for x in range(width):
            margin = coverage - limits[(x, y)]
            if margin <= 0:
                continue
            alpha = min(1.0, margin / 0.02)
            origin = (x * tile_px, y * tile_px)
            h = hash32(seed, x, y, 0)

            if kind in ("leaves", "petals"):
                sprites = leaf_sprites if kind == "leaves" else [PETAL_LIGHT, PETAL_DEEP]
                maximum = 4 if kind == "leaves" else 3
                count = 1 + min(maximum - 1, int(margin / 0.09))
                for i in range(count):
                    hi = hash32(seed, x, y, i + 1)
                    sprite = cell_image(particles, PARTICLE_CELL, sprites[hi % len(sprites)]).resize((PARTICLE_CELL * zoom,) * 2, Image.NEAREST)
                    sprite = sprite.rotate(90 * ((hi >> 12) & 3))
                    if (hi >> 14) & 1:
                        sprite = sprite.transpose(Image.FLIP_LEFT_RIGHT)
                    cx = 16 + ((hi >> 4) % 9) * zoom
                    cy = 16 + ((hi >> 8) % 9) * zoom
                    paste(scene, with_alpha(sprite, alpha), origin[0] + cx - 16, origin[1] + cy - 16)
            else:
                index = GRASS_TUFT if h % 3 == 0 else SUMMER_FLOWERS
                sprite = cell_image(ground, GROUND_CELL, index).resize((tile_px, tile_px), Image.NEAREST)
                if flip_h(h, 0):
                    sprite = sprite.transpose(Image.FLIP_LEFT_RIGHT)
                dx, dy = jitter(h, 4, 2, zoom)
                paste(scene, with_alpha(sprite, alpha), origin[0] + dx, origin[1] + dy)

    return scene


def build_preview(particles, ground):
    pad, gap = 24, 12
    title_font, label_font, small_font = load_font(26), load_font(15), load_font(16)

    scene_tiles = (6, 4)
    scene_w, scene_h = scene_tiles[0] * 64, scene_tiles[1] * 64
    width = pad * 2 + scene_w * 4 + gap * 3

    scenes = [
        ("Late summer (day 28): fallen leaves", "summer", "leaves", 0.55, "autumn"),
        ("Early winter (day 1): last leaves on snow", "winter", "leaves", 0.3, "last"),
        ("Late spring (day 28): summer flowers", "spring", "summer flowers", 0.3, "autumn"),
        ("Early summer (day 1): fallen petals", "summer", "petals", 0.27, "autumn"),
    ]

    particle_px = PARTICLE_CELL * 16
    ground_px = GROUND_CELL * 8
    height = pad + 40 + particle_px + 46 + 40 + ground_px + 46 + 66 + scene_h + 34 + pad
    preview = Image.new("RGBA", (width, height), (250, 248, 242, 255))
    draw = ImageDraw.Draw(preview)
    ink = (70, 60, 50, 255)

    # particles at 16x
    top = pad
    draw.text((pad, top - 4), "particles.png (80x8) at 16x: falling particles", font=title_font, fill=(60, 50, 40, 255))
    top += 40
    for index in range(PARTICLE_COUNT):
        left = pad + index * (particle_px + gap)
        preview.alpha_composite(checkerboard(particle_px, particle_px, 16), (left, top))
        preview.alpha_composite(cell_image(particles, PARTICLE_CELL, index).resize((particle_px, particle_px), Image.NEAREST), (left, top))
        draw.rectangle([left - 1, top - 1, left + particle_px, top + particle_px], outline=(150, 140, 125, 255))
        label = f"{index}: {PARTICLE_NAMES[index]}"
        draw.text((left + (particle_px - text_width(draw, label, label_font)) // 2, top + particle_px + 8), label, font=label_font, fill=ink)
    top += particle_px + 46

    # ground decorations at 8x
    draw.text((pad, top - 4), "ground.png (32x16) at 8x: ground decorations (one cell = one map tile in game)", font=title_font, fill=(60, 50, 40, 255))
    top += 40
    for index in range(GROUND_COUNT):
        left = pad + index * (ground_px + gap)
        preview.alpha_composite(checkerboard(ground_px, ground_px, 16), (left, top))
        preview.alpha_composite(cell_image(ground, GROUND_CELL, index).resize((ground_px, ground_px), Image.NEAREST), (left, top))
        draw.rectangle([left - 1, top - 1, left + ground_px, top + ground_px], outline=(150, 140, 125, 255))
        label = f"{index}: {GROUND_NAMES[index]}"
        draw.text((left + (ground_px - text_width(draw, label, label_font)) // 2, top + ground_px + 8), label, font=label_font, fill=ink)
    top += ground_px + 46

    # scenes at the real in-game size (4x)
    draw.text((pad, top - 4), "In game at 4x, using the mod's own placement logic. Snow, the thaw and each season's ground", font=small_font, fill=ink)
    draw.text((pad, top + 18), "colour use Stardew Valley's own seasonal tiles in game, so they aren't shown here.", font=small_font, fill=ink)
    top += 66
    for column, (label, season, kind, coverage, leaf_palette) in enumerate(scenes):
        left = pad + column * (scene_w + gap)
        scene = ground_scene(particles, ground, season, kind, coverage, tiles=scene_tiles, leaf_palette=leaf_palette)
        preview.alpha_composite(scene, (left, top))
        draw.rectangle([left - 1, top - 1, left + scene_w, top + scene_h], outline=(150, 140, 125, 255))
        draw.text((left, top + scene_h + 6), label, font=label_font, fill=ink)

    return preview


# ---------------------------------------------------------------------------
# main
# ---------------------------------------------------------------------------
def check(path, sheet, name):
    other = Image.open(path).convert("RGBA")
    if other.size != sheet.size:
        sys.exit(f"{path}: different size from the {name} art: {other.size} vs {sheet.size}")
    different = [(x, y) for y in range(sheet.height) for x in range(sheet.width) if other.getpixel((x, y)) != sheet.getpixel((x, y))]
    if different:
        sys.exit(f"{path}: {len(different)} pixels differ from the {name} art, first at {different[0]}: {other.getpixel(different[0])} vs {sheet.getpixel(different[0])}")
    print(f"OK: {path} matches the {name} art in SpriteFactory.cs exactly ({sheet.width}x{sheet.height}).")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--check-particles", metavar="PNG", help="compare this PNG's pixels with the particle art")
    parser.add_argument("--check-ground", metavar="PNG", help="compare this PNG's pixels with the ground decoration art")
    args = parser.parse_args()

    text = SOURCE.read_text(encoding="utf-8")
    palette = parse_palette(text)
    particles = build_sheet(palette, parse_cells(text, "PARTICLE-CELLS", PARTICLE_CELL, PARTICLE_COUNT, palette), PARTICLE_CELL)
    ground = build_sheet(palette, parse_cells(text, "GROUND-CELLS", GROUND_CELL, GROUND_COUNT, palette), GROUND_CELL)

    if args.check_particles or args.check_ground:
        if args.check_particles:
            check(args.check_particles, particles, "particle")
        if args.check_ground:
            check(args.check_ground, ground, "ground decoration")
        return

    for path, sheet in ((PARTICLES_PATH, particles), (GROUND_PATH, ground)):
        path.parent.mkdir(parents=True, exist_ok=True)
        sheet.save(path, "PNG", optimize=True)
        print(f"Wrote {path} ({sheet.width}x{sheet.height})")

    PREVIEW_PATH.parent.mkdir(parents=True, exist_ok=True)
    build_preview(particles, ground).save(PREVIEW_PATH, "PNG")
    print(f"Wrote {PREVIEW_PATH}")


if __name__ == "__main__":
    main()
