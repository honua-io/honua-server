#!/usr/bin/env python3
"""Executable fixtures for the serving-image boundary verifier."""

from __future__ import annotations

import importlib.util
import io
import sys
import tarfile
from pathlib import Path
from subprocess import CompletedProcess
from unittest.mock import patch


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


base = {"/app/Honua.Server": b"\x7fELF fixture"}
assert_clean(base)
assert_clean(base | {"/app/ProjNet.dll": b"managed CRS fixture"})
assert_rejected({"/app/Honua.Server": b"#!/bin/sh\n"}, "not an ELF executable")
assert_rejected(base | {"/usr/lib/libgdal.so.36": b"fixture"}, "native GDAL/PROJ/GEOS library")
assert_rejected(base | {"/usr/bin/gdalinfo": b"fixture"}, "native raster CLI")
for utility in ("nearblack", "rgb2pct.py", "sozip", "gnmmanage", "gdalserver", "testepsg"):
    assert_rejected(base | {f"/usr/bin/{utility}": b"fixture"}, "native raster CLI")
assert_rejected(base | {"/app/OSGeo.GDAL.dll": b"fixture"}, ".NET/Python GDAL binding")
assert_rejected(base | {"/app/MaxRev.Gdal.Core.dll": b"fixture"}, ".NET/Python GDAL binding")
assert_rejected(
    base | {"/usr/lib/python3/dist-packages/osgeo/_gdal.cpython-314-x86_64-linux-gnu.so": b"fixture"},
    ".NET/Python GDAL binding",
)
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

# The positive worker smoke must exercise the image's configured ENTRYPOINT. A
# command-only probe with --entrypoint can pass even when the managed worker host
# is absent or crashes before joining the durable Redis-backed execution loop.
worker_runs: list[list[str]] = []


def fake_worker_run(arguments: list[str], **_: object) -> CompletedProcess[str]:
    worker_runs.append(arguments)
    if arguments[1] == "run":
        return CompletedProcess(arguments, 0, stdout="container-id\n", stderr="")
    if arguments[1] == "logs":
        return CompletedProcess(
            arguments,
            0,
            stdout="Job execution worker started: worker-fixture\n",
            stderr="",
        )
    if arguments[1:3] == ["rm", "-f"]:
        return CompletedProcess(arguments, 0, stdout="", stderr="")
    raise AssertionError(f"unexpected worker smoke command: {arguments}")


with patch.object(MODULE.subprocess, "run", side_effect=fake_worker_run):
    smoke_violations = MODULE._smoke_worker_entrypoint("worker:fixture", "127.0.0.1:6379")

if smoke_violations:
    raise AssertionError(f"expected clean worker entrypoint smoke, got {smoke_violations}")
if "--entrypoint" in worker_runs[0]:
    raise AssertionError(f"worker smoke overrode the image entrypoint: {worker_runs[0]}")
if worker_runs[0][-1] != "worker:fixture":
    raise AssertionError(f"worker smoke did not launch the requested image: {worker_runs[0]}")

print("Serving-image boundary fixtures passed.")
