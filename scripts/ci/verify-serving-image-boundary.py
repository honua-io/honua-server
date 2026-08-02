#!/usr/bin/env python3
"""Verify the native-AOT serving image and the isolated GDAL worker boundary."""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import tarfile
from pathlib import PurePosixPath
from typing import BinaryIO, Iterable


FORBIDDEN_PACKAGE = re.compile(r"(?:^|[-_.]|lib)(gdal|proj|geos)(?:[-_.0-9]|$)", re.IGNORECASE)
FORBIDDEN_GDAL_LIBRARY = re.compile(
    r"^(?:lib)?gdal.*\.(?:so(?:\..*)?|dylib|dll)$",
    re.IGNORECASE,
)
FORBIDDEN_PROJ_GEOS_LIBRARY = re.compile(
    r"^(?:lib)?(?:proj|geos(?:_c)?)(?:[-_.][0-9].*)?\.(?:so(?:\..*)?|dylib|dll)$",
    re.IGNORECASE,
)
FORBIDDEN_BINDING = re.compile(
    r"^(?:osgeo[._-])?(?:gdal|ogr|osr)(?:[._-](?:const|csharp|wrap|bindings?))?.*\.(?:dll|so|dylib|py)$",
    re.IGNORECASE,
)
FORBIDDEN_EXECUTABLES = {
    "cct",
    "cs2cs",
    "gdal_contour",
    "gdal-config",
    "gdal_create",
    "gdal_grid",
    "gdal_rasterize",
    "gdal_translate",
    "gdal_viewshed",
    "gdaladdo",
    "gdalbuildvrt",
    "gdaldem",
    "gdalenhance",
    "gdalinfo",
    "gdallocationinfo",
    "gdalmanage",
    "gdalsrsinfo",
    "gdaltindex",
    "gdaltransform",
    "gdalwarp",
    "geod",
    "geos-config",
    "invgeod",
    "invproj",
    "ogr2ogr",
    "ogrinfo",
    "ogrlineref",
    "ogrtindex",
    "proj",
    "projinfo",
}
APP_MANIFEST_SUFFIXES = (".deps.json", ".nuspec")


def _normalise(name: str) -> str:
    return str(PurePosixPath("/" + name.lstrip("./")))


def _forbidden_file_reason(path: str) -> str | None:
    basename = PurePosixPath(path).name
    folded = basename.casefold()
    executable_name = folded.removesuffix(".exe")
    if executable_name in FORBIDDEN_EXECUTABLES or executable_name.startswith("gdal_") and "." not in executable_name:
        return "native raster CLI"
    if FORBIDDEN_GDAL_LIBRARY.match(basename) or FORBIDDEN_PROJ_GEOS_LIBRARY.match(basename):
        return "native GDAL/PROJ/GEOS library"
    if FORBIDDEN_BINDING.match(basename) or folded.endswith(".dll") and "gdal" in folded:
        return ".NET/Python GDAL binding"
    return None


def _package_names(path: str, content: bytes) -> Iterable[str]:
    text = content.decode("utf-8", errors="replace")
    if path == "/lib/apk/db/installed":
        for line in text.splitlines():
            if line.startswith("P:"):
                yield line[2:].strip()
    elif path == "/var/lib/dpkg/status":
        for line in text.splitlines():
            if line.startswith("Package:"):
                yield line.partition(":")[2].strip()
    elif path.casefold().endswith(APP_MANIFEST_SUFFIXES):
        for match in re.finditer(r"[A-Za-z0-9_.+-]+", text):
            yield match.group(0)


def scan_rootfs(stream: BinaryIO, entrypoint: str) -> list[str]:
    """Return invariant violations found in an exported container rootfs tar."""
    violations: list[str] = []
    entrypoint = _normalise(entrypoint)
    entrypoint_seen = False
    managed_entrypoint = f"{entrypoint}.dll"

    with tarfile.open(fileobj=stream, mode="r|*") as archive:
        for member in archive:
            path = _normalise(member.name)
            if path == entrypoint and member.isfile():
                entrypoint_seen = True
                if member.mode & 0o111 == 0:
                    violations.append(f"native entrypoint is not executable: {entrypoint}")

            if path == managed_entrypoint and member.isfile():
                violations.append(f"managed server entrypoint is present: {managed_entrypoint}")

            if not member.isdir():
                reason = _forbidden_file_reason(path)
                if reason:
                    violations.append(f"{reason}: {path}")

            should_read = member.isfile() and (
                path in {"/lib/apk/db/installed", "/var/lib/dpkg/status"}
                or path.casefold().endswith(APP_MANIFEST_SUFFIXES)
            )
            if not should_read:
                continue

            extracted = archive.extractfile(member)
            if extracted is None:
                continue
            for package in _package_names(path, extracted.read()):
                if FORBIDDEN_PACKAGE.search(package):
                    violations.append(f"forbidden package '{package}' recorded in {path}")

    if not entrypoint_seen:
        violations.append(f"native entrypoint is missing: {entrypoint}")

    return sorted(set(violations))


def _docker(*arguments: str, capture: bool = False) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        ["docker", *arguments],
        check=True,
        text=True,
        capture_output=capture,
    )


def verify_serving_image(image: str) -> list[str]:
    inspect = _docker("image", "inspect", image, capture=True)
    metadata = json.loads(inspect.stdout)[0]
    labels = metadata.get("Config", {}).get("Labels") or {}
    violations: list[str] = []

    if labels.get("honua.runtime.profile") != "web":
        violations.append("image label honua.runtime.profile must be 'web'")
    if labels.get("honua.runtime.compilation") != "native-aot":
        violations.append("image label honua.runtime.compilation must be 'native-aot'")

    entrypoint = labels.get("honua.runtime.entrypoint")
    if not entrypoint or not entrypoint.startswith("/"):
        violations.append("image label honua.runtime.entrypoint must be an absolute path")
        return violations

    container = _docker("create", image, capture=True).stdout.strip()
    try:
        process = subprocess.Popen(["docker", "export", container], stdout=subprocess.PIPE)
        if process.stdout is None:
            raise RuntimeError("docker export did not provide a rootfs stream")
        violations.extend(scan_rootfs(process.stdout, entrypoint))
        if process.wait() != 0:
            raise subprocess.CalledProcessError(process.returncode, ["docker", "export", container])
    finally:
        _docker("rm", "-f", container)

    return sorted(set(violations))


def verify_worker_image(image: str) -> list[str]:
    inspect = _docker("image", "inspect", image, capture=True)
    metadata = json.loads(inspect.stdout)[0]
    labels = metadata.get("Config", {}).get("Labels") or {}
    violations: list[str] = []
    if labels.get("honua.runtime.profile") != "native":
        violations.append("worker label honua.runtime.profile must be 'native'")

    command = """
set -eu
command -v gdalinfo >/dev/null
command -v gdal_translate >/dev/null
command -v gdalwarp >/dev/null
command -v ogr2ogr >/dev/null
gdalinfo --formats | grep -qi netCDF
gdalinfo --formats | grep -qi GRIB
python3 -c 'from osgeo import gdal'
""".strip()
    result = subprocess.run(
        ["docker", "run", "--rm", "--entrypoint", "/bin/sh", image, "-c", command],
        text=True,
        capture_output=True,
    )
    if result.returncode != 0:
        detail = (result.stderr or result.stdout).strip()
        violations.append(f"GDAL worker capability probe failed: {detail or 'unknown error'}")
    return violations


def _write_result(violations: list[str], success: str) -> int:
    if violations:
        print("Raster runtime boundary verification failed:", file=sys.stderr)
        for violation in violations:
            print(f"  - {violation}", file=sys.stderr)
        return 1
    print(success)
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--serving-image", metavar="IMAGE")
    group.add_argument("--worker-image", metavar="IMAGE")
    group.add_argument("--rootfs-tar", metavar="PATH")
    parser.add_argument("--entrypoint", default="/app/Honua.Server")
    args = parser.parse_args()

    if args.serving_image:
        return _write_result(
            verify_serving_image(args.serving_image),
            f"Serving image {args.serving_image} is native AOT and GDAL/PROJ/GEOS-free.",
        )
    if args.worker_image:
        return _write_result(
            verify_worker_image(args.worker_image),
            f"Worker image {args.worker_image} exposes the required GDAL tools and drivers.",
        )

    with open(args.rootfs_tar, "rb") as stream:
        return _write_result(
            scan_rootfs(stream, args.entrypoint),
            f"Rootfs archive {args.rootfs_tar} satisfies the serving-image boundary.",
        )


if __name__ == "__main__":
    raise SystemExit(main())
