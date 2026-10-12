#!/usr/bin/env python3
"""Verify the published public scene contains the exact tileset-referenced source bytes."""
import argparse
import hashlib
import json
from pathlib import Path
from urllib.parse import urlsplit

FIXTURE = Path("fixtures/scenes/downtown-honolulu")


def verify(publish_dir, source_dir):
    published = Path(publish_dir).resolve() / FIXTURE
    source = Path(source_dir).resolve()
    tileset = json.loads((source / "tileset.json").read_bytes())
    required = {"tileset.json", "metadata/scene.json"}

    def visit(tile):
        content = tile.get("content")
        if content:
            uri = content["uri"]
            parsed = urlsplit(uri)
            path = Path(uri)
            if (parsed.scheme or parsed.netloc or parsed.query or parsed.fragment
                    or "\\" in uri or path.is_absolute() or ".." in path.parts):
                raise ValueError("The owned fixture requires a local relative content URI")
            required.add(path.as_posix())
        for child in tile.get("children", []):
            visit(child)

    visit(tileset["root"])
    if len(required) <= 2:
        raise ValueError("The scene fixture must declare binary content")
    result = {}
    for name in sorted(required):
        original = source / name
        target = published / name
        if not original.is_file() or not target.is_file():
            raise ValueError("Missing source or published scene asset: " + name)
        raw = original.read_bytes()
        if not raw or target.read_bytes() != raw:
            raise ValueError("Published scene asset differs from source: " + name)
        result[name] = {"bytes": len(raw), "sha256": hashlib.sha256(raw).hexdigest()}
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--publish-dir", type=Path, required=True)
    parser.add_argument("--source-dir", type=Path,
                        default=Path(__file__).resolve().parents[2] / "src/Honua.Server" / FIXTURE)
    args = parser.parse_args()
    print(json.dumps(verify(args.publish_dir, args.source_dir), sort_keys=True))


if __name__ == "__main__":
    main()
