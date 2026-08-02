#!/usr/bin/env python3
"""Executable fixtures for the serving-image boundary verifier."""

from __future__ import annotations

import importlib.util
import io
import sys
import tarfile
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[1] / "verify-serving-image-boundary.py"
SPEC = importlib.util.spec_from_file_location("serving_image_boundary", SCRIPT)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError(f"Unable to load {SCRIPT}")
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


def rootfs(files: dict[str, bytes]) -> io.BytesIO:
    output = io.BytesIO()
    with tarfile.open(fileobj=output, mode="w") as archive:
        for name, content in files.items():
            info = tarfile.TarInfo(name.lstrip("/"))
            info.size = len(content)
            info.mode = 0o755 if name == "/app/Honua.Server" else 0o644
            archive.addfile(info, io.BytesIO(content))
    output.seek(0)
    return output


def assert_clean(files: dict[str, bytes]) -> None:
    violations = MODULE.scan_rootfs(rootfs(files), "/app/Honua.Server")
    if violations:
        raise AssertionError(f"expected clean rootfs, got {violations}")


def assert_rejected(files: dict[str, bytes], expected: str) -> None:
    violations = MODULE.scan_rootfs(rootfs(files), "/app/Honua.Server")
    if not any(expected in violation for violation in violations):
        raise AssertionError(f"expected a '{expected}' violation, got {violations}")


base = {"/app/Honua.Server": b"ELF fixture"}
assert_clean(base)
assert_clean(base | {"/app/ProjNet.dll": b"managed CRS fixture"})
assert_rejected(base | {"/usr/lib/libgdal.so.36": b"fixture"}, "native GDAL/PROJ/GEOS library")
assert_rejected(base | {"/usr/bin/gdalinfo": b"fixture"}, "native raster CLI")
assert_rejected(base | {"/app/OSGeo.GDAL.dll": b"fixture"}, ".NET/Python GDAL binding")
assert_rejected(base | {"/app/MaxRev.Gdal.Core.dll": b"fixture"}, ".NET/Python GDAL binding")
assert_rejected(
    base | {"/lib/apk/db/installed": b"P:gdal\nV:3.12.0-r0\n"},
    "forbidden package 'gdal'",
)
assert_rejected(
    base | {"/var/lib/dpkg/status": b"Package: libproj25\nStatus: install ok installed\n"},
    "forbidden package 'libproj25'",
)
assert_rejected(base | {"/usr/lib/libgeos_c.so.1": b"fixture"}, "native GDAL/PROJ/GEOS library")
assert_rejected(base | {"/app/Honua.Server.dll": b"fixture"}, "managed server entrypoint")

print("Serving-image boundary fixtures passed.")
