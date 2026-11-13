#!/usr/bin/env python3
"""Materialize an exact historical implementation for a same-process comparison."""
from pathlib import Path
import subprocess
import sys

root = Path(__file__).resolve().parents[2]
revision = sys.argv[1] if len(sys.argv) > 1 else "161420e43720e5baca66ba4e12130ed55226e6ed"
revision = subprocess.check_output(["git", "rev-parse", "--verify", revision + "^{commit}"], cwd=root, text=True).strip()
output = root / "artifacts/decoding-baseline"
output.mkdir(parents=True, exist_ok=True)
paths = ["src/Honua.Hosting/Features/Rendering/WkbToSkiaConverter.cs"]
paths += ["src/Honua.Core/Core/Features/Raster/CogParser/" + name + ".cs"
          for name in ("TilePredictor", "TilePixelLayout", "UnsupportedTileCodecException")]
for path in paths:
    source = subprocess.check_output(["git", "show", revision + ":" + path], cwd=root, text=True)
    source = source.replace("namespace Honua.", "namespace Baseline.Honua.")
    (output / Path(path).name).write_text(source)
(output / "revision.txt").write_text(revision + "\n")
print("Baseline: " + revision)
