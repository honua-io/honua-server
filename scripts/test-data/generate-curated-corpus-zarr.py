#!/usr/bin/env python3
"""Write the deterministic uncompressed float32 chunk for curated corpus v1."""

from pathlib import Path
import struct


VALUES = (
    10, 11, 12, 13,
    14, 15, 16, 17,
    18, 19, 20, 21,
    35, 34, 33, 32,
    31, 30, 29, 28,
    27, 26, 25, 24,
)


def main() -> None:
    root = Path(__file__).resolve().parents[2]
    destination = root / "tests/fixtures/curated-edge-corpus/v1/sea-surface-temperature.zarr/temperature/0.0.0"
    destination.write_bytes(struct.pack("<24f", *VALUES))


if __name__ == "__main__":
    main()
