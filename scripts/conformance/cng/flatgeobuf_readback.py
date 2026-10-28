#!/usr/bin/env python3
"""Fully decode the small CNG FlatGeobuf fixtures with GDAL (3.7 or later)."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import subprocess


def require_decoded_features(info: dict) -> int:
    """Reject summary-only, partial and null-geometry reads of our seeded fixture."""
    layers = info.get('layers') or []
    if len(layers) != 1:
        raise ValueError('GDAL must decode exactly one FlatGeobuf layer')
    layer = layers[0]
    features = layer.get('features')
    expected = layer.get('featureCount')
    if not isinstance(features, list) or not features:
        raise ValueError('GDAL did not decode the seeded FlatGeobuf features')
    if type(expected) is not int or expected != len(features):
        raise ValueError(f'GDAL decoded {len(features)} features; layer declares {expected}')
    for feature in features:
        if not isinstance(feature, dict) or feature.get('type') != 'Feature':
            raise ValueError('GDAL emitted an invalid feature')
        if not isinstance(feature.get('geometry'), dict) or not feature['geometry'].get('type'):
            raise ValueError('GDAL did not decode a non-null fixture geometry')
        if not isinstance(feature.get('properties'), dict):
            raise ValueError('GDAL did not decode fixture properties')
    return len(features)


def read_flatgeobuf(path: Path) -> dict:
    """Return full ogrinfo JSON only after every feature has been read successfully."""
    try:
        result = subprocess.run(
            ['ogrinfo', '-ro', '-json', '-al', '-features', str(path)],
            env={**os.environ, 'OGR_FLATGEOBUF_VERIFY_BUFFERS': 'YES'},
            capture_output=True, text=True, check=True, timeout=120,
        )
    except subprocess.CalledProcessError as error:
        raise ValueError(f'GDAL FlatGeobuf read failed: {error.stderr or error}') from error
    # Some GDAL versions report a read error while still returning a layer summary.
    if re.search(r'^ERROR\b', result.stderr, re.MULTILINE):
        raise ValueError(f'GDAL reported a FlatGeobuf read error: {result.stderr.strip()}')
    info = json.loads(result.stdout)
    require_decoded_features(info)
    return info


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('path', type=Path)
    arguments = parser.parse_args()
    print(json.dumps(read_flatgeobuf(arguments.path)))
