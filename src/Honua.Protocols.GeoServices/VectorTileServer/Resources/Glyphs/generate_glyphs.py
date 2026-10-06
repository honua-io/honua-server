#!/usr/bin/env python3
# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
"""Regenerates the bundled VectorTileServer glyph ranges (signed-distance-field glyph PBFs).

Each output file is a standard glyph protobuf (``glyphs`` > ``fontstack`` > ``glyph``) for one
256-codepoint range, rendered with the conventional SDF parameters used by vector tile
renderers: 24 px em, 3 px buffer, 8 px radius, 0.25 cutoff. ``top`` is reported relative to
the fixed shaping baseline renderers assume (25 px at a 24 px em) rather than this font's own
ascender, ``left``/``top`` are zig-zag encoded ``sint32`` values, and glyphs without ink
(spaces) carry metrics only.

Usage (DejaVu Sans Book is the face the server images install for rendering):

    python3 -I generate_glyphs.py /usr/share/fonts/truetype/dejavu/DejaVuSans.ttf 0-255

Requires Pillow (with FreeType), NumPy and SciPy. Output is deterministic for a given font
file and library versions; the server rewrites only the fontstack name at request time.
"""

import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont
from scipy.ndimage import distance_transform_edt

FONT_SIZE = 24
BUFFER = 3
RADIUS = 8.0
CUTOFF = 0.25
SCALE = 16  # supersampling factor used to compute the distance field
# Renderer shaping places glyph tops against a fixed 25 px baseline at a 24 px em, whatever the
# font's own ascender, so ``top`` is measured from it to keep labels aligned with icons and
# collision boxes.
SHAPING_BASELINE = 25
FONTSTACK_NAME = "DejaVu Sans Book"


def varint(value):
    out = bytearray()
    while True:
        byte = value & 0x7F
        value >>= 7
        if value:
            out.append(byte | 0x80)
        else:
            out.append(byte)
            return bytes(out)


def zigzag(value):
    return (value << 1) ^ (value >> 31)


def field_varint(number, value):
    return varint(number << 3) + varint(value)


def field_bytes(number, payload):
    return varint((number << 3) | 2) + varint(len(payload)) + payload


def has_glyph(font, codepoint):
    # Control characters are never drawn; the font maps every other Latin-1 codepoint.
    return not (codepoint < 0x20 or 0x7F <= codepoint < 0xA0)


def render_glyph(hi_font, codepoint):
    char = chr(codepoint)
    advance = int(round(hi_font.getlength(char) / SCALE))

    pad = int((BUFFER + RADIUS) * SCALE)
    size = FONT_SIZE * SCALE
    width = size * 2 + pad * 2
    height = size * 2 + pad * 2
    origin_x = pad + size // 2
    origin_y = pad + size + size // 2  # baseline

    image = Image.new("L", (width, height), 0)
    ImageDraw.Draw(image).text((origin_x, origin_y), char, font=hi_font, fill=255, anchor="ls")
    inside = np.asarray(image) >= 128

    if not inside.any():
        return {"id": codepoint, "bitmap": b"", "width": 0, "height": 0,
                "left": 0, "top": 0 - SHAPING_BASELINE, "advance": advance}

    rows = np.where(inside.any(axis=1))[0]
    cols = np.where(inside.any(axis=0))[0]
    x_min = (cols[0] - origin_x) / SCALE
    x_max = (cols[-1] + 1 - origin_x) / SCALE
    y_max = (origin_y - rows[0]) / SCALE
    y_min = (origin_y - (rows[-1] + 1)) / SCALE

    left = int(np.floor(x_min))
    right = int(np.ceil(x_max))
    top = int(np.ceil(y_max))
    bottom = int(np.floor(y_min))
    glyph_width = right - left
    glyph_height = top - bottom

    # Signed distance in supersampled pixels: positive outside the outline, negative inside.
    distance = distance_transform_edt(~inside) - distance_transform_edt(inside)

    bitmap_width = glyph_width + 2 * BUFFER
    bitmap_height = glyph_height + 2 * BUFFER
    bitmap = bytearray(bitmap_width * bitmap_height)
    for row in range(bitmap_height):
        y = top + BUFFER - row - 0.5
        hy = int(round(origin_y - y * SCALE))
        for col in range(bitmap_width):
            x = left - BUFFER + col + 0.5
            hx = int(round(origin_x + x * SCALE))
            d = distance[hy, hx] / SCALE
            value = 255.0 - 255.0 * (d / RADIUS + CUTOFF)
            bitmap[row * bitmap_width + col] = max(0, min(255, int(round(value))))

    return {"id": codepoint, "bitmap": bytes(bitmap), "width": glyph_width,
            "height": glyph_height, "left": left, "top": top - SHAPING_BASELINE, "advance": advance}


def encode_glyph(glyph):
    payload = field_varint(1, glyph["id"])
    if glyph["bitmap"]:
        payload += field_bytes(2, glyph["bitmap"])
    payload += field_varint(3, glyph["width"])
    payload += field_varint(4, glyph["height"])
    payload += field_varint(5, zigzag(glyph["left"]))
    payload += field_varint(6, zigzag(glyph["top"]))
    payload += field_varint(7, glyph["advance"])
    return payload


def main(argv):
    if len(argv) < 3:
        print(__doc__, file=sys.stderr)
        return 2

    font_path = argv[1]
    hi_font = ImageFont.truetype(font_path, FONT_SIZE * SCALE, layout_engine=ImageFont.Layout.BASIC)
    out_dir = os.path.dirname(os.path.abspath(__file__))

    for glyph_range in argv[2:]:
        start, end = (int(part) for part in glyph_range.split("-"))
        stack = field_bytes(1, FONTSTACK_NAME.encode("utf-8"))
        stack += field_bytes(2, glyph_range.encode("ascii"))
        count = 0
        for codepoint in range(start, end + 1):
            if not has_glyph(hi_font, codepoint):
                continue
            stack += field_bytes(3, encode_glyph(render_glyph(hi_font, codepoint)))
            count += 1

        path = os.path.join(out_dir, f"{glyph_range}.pbf")
        with open(path, "wb") as handle:
            handle.write(field_bytes(1, stack))
        print(f"{path}: {count} glyphs")

    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
