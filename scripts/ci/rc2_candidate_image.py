"""Verify that a candidate image matches its immutable Native AOT identity."""

from __future__ import annotations

import json
import subprocess


def verify_candidate_image_identity(
    server_sha: str,
    server_digest: str,
    arch_target: tuple[str, str],
) -> None:
    """Verify index membership, commit revision, and Native AOT compilation label."""
    arch, platform_digest = arch_target
    raw = subprocess.check_output(
        ["docker", "buildx", "imagetools", "inspect", f"ghcr.io/honua-io/honua-server@{server_digest}", "--raw"]
    )
    doc = json.loads(raw)
    matches = [
        m["digest"]
        for m in doc.get("manifests", [])
        if m.get("platform", {}).get("os") == "linux" and m.get("platform", {}).get("architecture") == arch
    ]
    if platform_digest not in matches:
        raise ValueError(f"index {server_digest} does not contain {arch} digest {platform_digest}; found {matches}")
    image = f"ghcr.io/honua-io/honua-server@{platform_digest}"
    raw_labels = subprocess.check_output(
        ["docker", "image", "inspect", "--format", "{{json .Config.Labels}}", image],
        text=True,
    ).strip()
    labels = json.loads(raw_labels) if raw_labels and raw_labels != "null" else {}
    rev = labels.get("org.opencontainers.image.revision", "")
    if rev != server_sha:
        raise ValueError(f"image {image} revision {rev!r} does not match server_sha {server_sha!r}")
    compilation = labels.get("honua.runtime.compilation", "")
    if compilation != "native-aot":
        raise ValueError(f"image {image} honua.runtime.compilation {compilation!r} is not 'native-aot'")
