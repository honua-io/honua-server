"""Build the Honua QGIS plugin zip without depending on a system ``zip``.

Run from the ``clients/qgis/`` directory:

    python3 scripts/build_zip.py [--out dist/honua_qgis.zip]

Includes everything under ``honua_qgis/`` except ``__pycache__`` dirs and
``.pyc`` files, mirroring the Makefile target so QGIS Plugin Manager
sees a clean tree.
"""

from __future__ import annotations

import argparse
import os
import sys
import zipfile


PLUGIN_DIR = "honua_qgis"
DEFAULT_OUT = os.path.join("dist", "honua_qgis.zip")
EXCLUDED_DIRS = {"__pycache__"}
EXCLUDED_SUFFIXES = (".pyc",)


def _iter_plugin_files(root: str):
    for dirpath, dirnames, filenames in os.walk(root):
        # mutate in place so os.walk skips excluded subtrees entirely
        dirnames[:] = [d for d in dirnames if d not in EXCLUDED_DIRS]
        for filename in filenames:
            if filename.endswith(EXCLUDED_SUFFIXES):
                continue
            yield os.path.join(dirpath, filename)


def build(out_path: str) -> int:
    cwd = os.getcwd()
    if not os.path.isdir(os.path.join(cwd, PLUGIN_DIR)):
        sys.stderr.write(
            f"error: run from the directory containing '{PLUGIN_DIR}/'\n"
        )
        return 1

    out_dir = os.path.dirname(os.path.abspath(out_path))
    if out_dir:
        os.makedirs(out_dir, exist_ok=True)
    if os.path.exists(out_path):
        os.remove(out_path)

    count = 0
    with zipfile.ZipFile(out_path, "w", zipfile.ZIP_DEFLATED) as zf:
        for source in _iter_plugin_files(PLUGIN_DIR):
            arcname = os.path.relpath(source, cwd)
            zf.write(source, arcname)
            count += 1
    sys.stdout.write(f"wrote {out_path} ({count} files)\n")
    return 0


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", default=DEFAULT_OUT)
    return parser.parse_args()


if __name__ == "__main__":
    sys.exit(build(_parse_args().out))
