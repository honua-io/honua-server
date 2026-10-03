#!/usr/bin/env python3
"""Verify the native-AOT serving image and the isolated GDAL worker boundary."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import subprocess
import sys
import tarfile
import time
import tempfile
import uuid
from pathlib import Path, PurePosixPath
from typing import BinaryIO, Iterable


FORBIDDEN_PACKAGE = re.compile(r"(?:^|[-_.]|lib)(gdal|proj|geos)(?:[-_.0-9]|$)", re.IGNORECASE)
FORBIDDEN_GDAL_LIBRARY = re.compile(
    r"^(?:lib)?gdal.*\.(?:so(?:\..*)?|dylib|dll|a)$",
    re.IGNORECASE,
)
FORBIDDEN_PROJ_GEOS_LIBRARY = re.compile(
    r"^(?:lib)?(?:proj|geos(?:_c)?)(?:[-_.].+)?\.(?:so(?:\..*)?|dylib|dll|a)$",
    re.IGNORECASE,
)
FORBIDDEN_BINDING = re.compile(
    r"^_?(?:osgeo[._-])?(?:gdal|ogr|osr)(?:[._-](?:const|array|numeric|csharp|wrap|bindings?))?.*\.(?:dll|so|dylib|py)$",
    re.IGNORECASE,
)
GDAL_UTILITY_NAMES = {
    # Canonical command roster exposed across GDAL distributions. Keep explicit
    # names because several GDAL utilities have no gdal/ogr prefix.
    "gdal",
    "gdal2tiles",
    "gdal2xyz",
    "gdal_calc",
    "gdal_contour",
    "gdal-config",
    "gdal_create",
    "gdal_edit",
    "gdal_fillnodata",
    "gdal_footprint",
    "gdal_grid",
    "gdal_merge",
    "gdal_pansharpen",
    "gdal_polygonize",
    "gdal_proximity",
    "gdal_rasterize",
    "gdal_retile",
    "gdalserver",
    "gdal_sieve",
    "gdal_translate",
    "gdal_viewshed",
    "gdaladdo",
    "gdalattachpct",
    "gdalbuildvrt",
    "gdalcompare",
    "gdaldem",
    "gdalenhance",
    "gdalinfo",
    "gdallocationinfo",
    "gdalmanage",
    "gdalsrsinfo",
    "gdaltindex",
    "gdaltransform",
    "gdalwarp",
    "gdalmdiminfo",
    "gdalmdimtranslate",
    "gdalmove",
    "gnmanalyse",
    "gnmmanage",
    "nearblack",
    "ogr2ogr",
    "ogr_layer_algebra",
    "ogrinfo",
    "ogrlineref",
    "ogrmerge",
    "ogrtindex",
    "pct2rgb",
    "rgb2pct",
    "sozip",
    "testepsg",
}
PROJ_GEOS_UTILITY_NAMES = {
    "cct",
    "cs2cs",
    "geod",
    "geos-config",
    "geosop",
    "gie",
    "invgeod",
    "invproj",
    "proj",
    "projinfo",
    "projsync",
}
FORBIDDEN_EXECUTABLES = GDAL_UTILITY_NAMES | PROJ_GEOS_UTILITY_NAMES
APP_MANIFEST_SUFFIXES = (".deps.json", ".nuspec")
EXPECTED_WORKER_LABELS = {
    "honua.runtime.profile": "native",
    "honua.native.gdal.version": "3.13.1",
    "honua.native.pdal.version": "2.10.2",
    "honua.runtime.dotnet.version": "10.0.11",
}


def _normalise(name: str) -> str:
    return str(PurePosixPath("/" + name.lstrip("./")))


def _forbidden_file_reason(path: str) -> str | None:
    basename = PurePosixPath(path).name
    folded = basename.casefold()
    executable_name = folded.removesuffix(".exe").removesuffix(".py")
    if executable_name in FORBIDDEN_EXECUTABLES or executable_name.startswith("gdal") and "." not in executable_name:
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

    with tarfile.open(fileobj=stream, mode="r|*") as archive:
        for member in archive:
            path = _normalise(member.name)
            if path == entrypoint and member.isfile():
                entrypoint_seen = True
                if member.mode & 0o111 == 0:
                    violations.append(f"native entrypoint is not executable: {entrypoint}")
                extracted_entrypoint = archive.extractfile(member)
                if extracted_entrypoint is None or extracted_entrypoint.read(4) != b"\x7fELF":
                    violations.append(f"native entrypoint is not an ELF executable: {entrypoint}")

            if member.isfile() and PurePosixPath(path).name.casefold() == "honua.server.dll":
                violations.append(f"managed server entrypoint is present: {path}")

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


DIGEST_PATTERN = re.compile(r"sha256:[0-9a-f]{64}$")
PREDICATES = {
    "sbom": "https://spdx.dev/Document",
    "provenance": "https://slsa.dev/provenance/v0.2",
}


def _verified_json(content: bytes, digest: str) -> dict:
    """Decode only bytes matching an immutable registry descriptor."""
    if not DIGEST_PATTERN.fullmatch(digest):
        raise ValueError(f"invalid sha256 digest: {digest}")
    # Some CLI versions append a newline when printing a raw manifest.
    for candidate in (content, content.removesuffix(b"\n")):
        if "sha256:" + hashlib.sha256(candidate).hexdigest() == digest:
            value = json.loads(candidate)
            if not isinstance(value, dict):
                raise ValueError(f"expected a JSON object at {digest}")
            return value
    raise ValueError(f"registry content does not match digest {digest}")


def _registry(*arguments: str) -> bytes:
    """Use the existing Docker registry credentials; retry transport failures."""
    delays = (10, 30, 60, 120, 80)
    for attempt in range(len(delays) + 1):
        result = subprocess.run(["skopeo", *arguments], capture_output=True)
        if result.returncode == 0:
            return result.stdout
        detail = result.stderr.decode(errors="replace")
        transient = any(marker in detail.lower() for marker in (
            "could not resolve host", "connection reset by peer", "tls handshake timeout",
            "tls timeout", "i/o timeout", "temporary failure in name resolution",
            "timeout awaiting response", "error connecting to api.github.com",
        ))
        if not transient or attempt == len(delays):
            raise RuntimeError(f"registry attestation read failed: {detail.strip()}")
        time.sleep(delays[attempt])
    raise AssertionError("unreachable")


def verify_image_attestations(image: str) -> list[dict]:
    """Verify index -> platform -> attestation -> in-toto subject and payload."""
    repository, separator, digest = image.rpartition("@")
    if not separator or not DIGEST_PATTERN.fullmatch(digest):
        raise ValueError("attestation verification requires an image@sha256:<digest> reference")
    index = _verified_json(_registry("inspect", "--raw", f"docker://{image}"), digest)
    manifests = index.get("manifests", [])
    serving = [item for item in manifests
               if item.get("platform", {}).get("os") == "linux"
               and item.get("platform", {}).get("architecture") in {"amd64", "arm64"}
               and item.get("annotations", {}).get("vnd.docker.reference.type") != "attestation-manifest"]
    if not serving:
        raise ValueError("pushed digest has no supported serving platform manifests")
    records: list[dict] = []
    for target in serving:
        target_digest = target["digest"]
        _verified_json(_registry("inspect", "--raw", f"docker://{repository}@{target_digest}"), target_digest)
        platform = target["platform"]
        found: dict[str, dict] = {}
        for descriptor in manifests:
            annotations = descriptor.get("annotations", {})
            if annotations.get("vnd.docker.reference.type") != "attestation-manifest":
                continue
            if annotations.get("vnd.docker.reference.digest") != target_digest:
                continue
            attestation_digest = descriptor["digest"]
            attestation_ref = f"{repository}@{attestation_digest}"
            attestation = _verified_json(
                _registry("inspect", "--raw", f"docker://{attestation_ref}"), attestation_digest,
            )
            if "subject" in attestation and attestation["subject"].get("digest") != target_digest:
                raise ValueError(f"attestation manifest subject differs from {target_digest}")
            # Copy only the small attestation manifest, config and in-toto blobs.
            # Never copy the runnable image layers just to inspect evidence.
            with tempfile.TemporaryDirectory(prefix="honua-attestations-") as directory:
                _registry("copy", "--preserve-digests", f"docker://{attestation_ref}", f"dir:{directory}")
                for layer in attestation.get("layers", []):
                    predicate_type = layer.get("annotations", {}).get("in-toto.io/predicate-type")
                    if predicate_type not in PREDICATES.values():
                        continue
                    if layer.get("mediaType") != "application/vnd.in-toto+json":
                        raise ValueError(f"unexpected attestation media type at {layer['digest']}")
                    layer_digest = layer["digest"]
                    if not DIGEST_PATTERN.fullmatch(layer_digest):
                        raise ValueError(f"invalid attestation layer digest: {layer_digest}")
                    statement = _verified_json(
                        (Path(directory) / layer_digest.removeprefix("sha256:")).read_bytes(), layer_digest,
                    )
                    if statement.get("predicateType") != predicate_type or not statement.get("predicate"):
                        raise ValueError(f"missing or mismatched predicate at {layer_digest}")
                    if statement.get("_type") not in {
                        "https://in-toto.io/Statement/v0.1", "https://in-toto.io/Statement/v1",
                    }:
                        raise ValueError(f"invalid in-toto statement at {layer_digest}")
                    if not any(subject.get("digest", {}).get("sha256") == target_digest.removeprefix("sha256:")
                               for subject in statement.get("subject", [])):
                        raise ValueError(f"attestation subject does not bind pushed platform digest {target_digest}")
                    kind = next(name for name, value in PREDICATES.items() if value == predicate_type)
                    found[kind] = {
                        "attestation": f"oci://{attestation_ref}",
                        "digest": layer_digest,
                        "predicateType": predicate_type,
                    }
        missing = sorted(PREDICATES.keys() - found.keys())
        if missing:
            raise ValueError(f"missing {', '.join(missing)} attestations for {target_digest}")
        records.append({
            "image": f"oci://{image}",
            "platform": f"{platform['os']}/{platform['architecture']}",
            "subject": f"oci://{repository}@{target_digest}",
            **found,
        })
    return records


def verify_serving_image(image: str) -> list[str]:
    inspect = _docker("image", "inspect", image, capture=True)
    metadata = json.loads(inspect.stdout)[0]
    config = metadata.get("Config", {})
    labels = config.get("Labels") or {}
    violations: list[str] = []

    if labels.get("honua.runtime.profile") != "web":
        violations.append("image label honua.runtime.profile must be 'web'")
    if labels.get("honua.runtime.compilation") != "native-aot":
        violations.append("image label honua.runtime.compilation must be 'native-aot'")

    entrypoint = labels.get("honua.runtime.entrypoint")
    if not entrypoint or not entrypoint.startswith("/"):
        violations.append("image label honua.runtime.entrypoint must be an absolute path")
        return violations

    launch_command = [*(config.get("Entrypoint") or []), *(config.get("Cmd") or [])]
    for argument in launch_command:
        if PurePosixPath(str(argument)).name.casefold() == "honua.server.dll":
            violations.append(
                f"configured launch command uses managed server entrypoint: {argument}"
            )

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


def _smoke_worker_entrypoint(image: str, redis_connection: str) -> list[str]:
    """Start the image's real entrypoint and wait for the durable worker loop."""
    container_name = f"honua-worker-boundary-{uuid.uuid4().hex[:12]}"
    started = subprocess.run(
        [
            "docker",
            "run",
            "--detach",
            "--name",
            container_name,
            "--network",
            "host",
            "--env",
            f"ConnectionStrings__redis={redis_connection}",
            image,
        ],
        text=True,
        capture_output=True,
    )
    if started.returncode != 0:
        detail = (started.stderr or started.stdout).strip()
        return [f"GDAL worker entrypoint failed to start: {detail or 'unknown error'}"]

    try:
        deadline = time.monotonic() + 30
        last_logs = ""
        while time.monotonic() < deadline:
            logs = subprocess.run(
                ["docker", "logs", container_name],
                text=True,
                capture_output=True,
            )
            last_logs = f"{logs.stdout}\n{logs.stderr}".strip()
            if "Job execution worker started:" in last_logs:
                return []

            state = subprocess.run(
                ["docker", "inspect", "--format", "{{.State.Running}}", container_name],
                text=True,
                capture_output=True,
            )
            if state.returncode != 0 or state.stdout.strip() != "true":
                return [
                    "GDAL worker entrypoint exited before the durable worker loop started: "
                    + (last_logs or "no container logs")
                ]
            time.sleep(1)

        return [
            "GDAL worker entrypoint did not report a started durable worker loop within 30 seconds: "
            + (last_logs or "no container logs")
        ]
    finally:
        subprocess.run(
            ["docker", "rm", "-f", container_name],
            text=True,
            capture_output=True,
            check=False,
        )


def verify_worker_image(image: str, redis_connection: str | None = None) -> list[str]:
    inspect = _docker("image", "inspect", image, capture=True)
    metadata = json.loads(inspect.stdout)[0]
    config = metadata.get("Config", {})
    labels = config.get("Labels") or {}
    violations: list[str] = []
    for label, expected in EXPECTED_WORKER_LABELS.items():
        if labels.get(label) != expected:
            violations.append(f"worker label {label} must be '{expected}'")
    if config.get("Entrypoint") != ["dotnet", "Honua.Worker.Gdal.dll"]:
        violations.append(
            "worker image entrypoint must be ['dotnet', 'Honua.Worker.Gdal.dll']"
        )
    if config.get("User") != "1001:1001":
        violations.append("worker image must run as user 1001:1001")

    command = f"""
set -eu
command -v gdalinfo >/dev/null
command -v gdal_translate >/dev/null
command -v gdalwarp >/dev/null
command -v ogr2ogr >/dev/null
! command -v pebble >/dev/null
gdalinfo --version | grep -F 'GDAL {EXPECTED_WORKER_LABELS["honua.native.gdal.version"]}'
gdalinfo --formats | grep -qi netCDF
gdalinfo --formats | grep -qi GRIB
python3 -c 'from osgeo import gdal'
command -v pdal >/dev/null
pdal --version | grep -F 'pdal {EXPECTED_WORKER_LABELS["honua.native.pdal.version"]}'
pdal --drivers | grep -qi readers.las
pdal --drivers | grep -qi filters.reprojection
! ldd "$(command -v pdal)" | grep -q 'not found'
! ldd "$(readlink -f /opt/pdal/lib/libpdalcpp.so)" | grep -q 'not found'
dotnet --list-runtimes | grep -F 'Microsoft.AspNetCore.App {EXPECTED_WORKER_LABELS["honua.runtime.dotnet.version"]}'
dotnet --list-runtimes | grep -F 'Microsoft.NETCore.App {EXPECTED_WORKER_LABELS["honua.runtime.dotnet.version"]}'
""".strip()
    result = subprocess.run(
        ["docker", "run", "--rm", "--entrypoint", "/bin/sh", image, "-c", command],
        text=True,
        capture_output=True,
    )
    if result.returncode != 0:
        detail = (result.stderr or result.stdout).strip()
        violations.append(f"GDAL worker capability probe failed: {detail or 'unknown error'}")
    if redis_connection:
        violations.extend(_smoke_worker_entrypoint(image, redis_connection))
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
    parser.add_argument(
        "--worker-redis",
        metavar="HOST:PORT",
        help="Redis endpoint used to smoke the worker image's real entrypoint",
    )
    parser.add_argument("--require-attestations", action="store_true",
                        help="Require digest-bound registry SBOM and provenance (needs skopeo)")
    parser.add_argument("--attestation-report", metavar="PATH",
                        help="Write immutable image, subject, manifest and statement references as JSON")
    args = parser.parse_args()

    if (args.require_attestations or args.attestation_report) and not args.serving_image:
        parser.error("attestation options require --serving-image")
    if args.attestation_report and not args.require_attestations:
        parser.error("--attestation-report requires --require-attestations")

    if args.worker_redis and not args.worker_image:
        parser.error("--worker-redis requires --worker-image")

    if args.serving_image:
        records = []
        if args.require_attestations:
            try:
                records = verify_image_attestations(args.serving_image)
            except (OSError, RuntimeError, ValueError, KeyError) as exc:
                return _write_result([str(exc)], "")
        violations = verify_serving_image(args.serving_image)
        if not violations and args.require_attestations:
            report = json.dumps(records, indent=2) + "\n"
            print(report, end="")
            if args.attestation_report:
                Path(args.attestation_report).write_text(report, encoding="utf-8")
        return _write_result(
            violations,
            f"Serving image {args.serving_image} is native AOT and GDAL/PROJ/GEOS-free.",
        )
    if args.worker_image:
        success = f"Worker image {args.worker_image} exposes the required native tools and drivers."
        if args.worker_redis:
            success = (
                f"Worker image {args.worker_image} exposes the required native tools "
                "and starts its worker loop."
            )
        return _write_result(
            verify_worker_image(args.worker_image, args.worker_redis),
            success,
        )

    with open(args.rootfs_tar, "rb") as stream:
        return _write_result(
            scan_rootfs(stream, args.entrypoint),
            f"Rootfs archive {args.rootfs_tar} satisfies the serving-image boundary.",
        )


if __name__ == "__main__":
    raise SystemExit(main())
