#!/usr/bin/env python3
"""Validate the fail-closed native-image PR cadence contract (#3204)."""

from __future__ import annotations

from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
WORKFLOWS = ROOT / ".github" / "workflows"


def read(name: str) -> str:
    return (WORKFLOWS / name).read_text(encoding="utf-8")


def require(source: str, needle: str, message: str) -> None:
    if needle not in source:
        raise AssertionError(message)


def validate_pr_cadence(name: str, source: str) -> None:
    require(
        source,
        "    types: [opened, reopened, ready_for_review]",
        f"{name} must run once for a ready PR, not on every synchronize push",
    )
    require(source, "  workflow_dispatch:", f"{name} must retain manual exact-ref execution")
    require(
        source,
        "    if: github.event_name != 'pull_request' || github.event.pull_request.draft == false",
        f"{name} must skip draft PR openings while retaining schedule/manual execution",
    )
    trigger = source.split("permissions:", 1)[0]
    if "synchronize" in trigger:
        raise AssertionError(f"{name} must not restart heavyweight images on review pushes")
    require(source, "  contents: read", f"{name} must stay read-only for candidate source")
    require(source, "  packages: read", f"{name} must not publish from candidate source")
    if "packages: write" in source or "contents: write" in source:
        raise AssertionError(f"{name} must not grant candidate-controlled write permissions")


def validate_serving_publication_boundaries() -> None:
    placements = {
        "docker/Dockerfile.aot": (
            "nightly-container-build.yml",
            "deploy.yml",
            "deploy-platform-images.yml",
            "release-bundle.yml",
        ),
        "docker/Dockerfile.lambda.aot": (
            "nightly-container-build.yml",
            "deploy-platform-images.yml",
        ),
        "docker/Dockerfile.functions.aot": ("deploy-platform-images.yml",),
    }
    for dockerfile, workflows in placements.items():
        for workflow in workflows:
            source = read(workflow)
            require(source, dockerfile, f"{workflow} no longer builds {dockerfile}")
            require(
                source,
                "scripts/ci/verify-serving-image-boundary.py --serving-image",
                f"{workflow} must verify the final rootfs before publishing {dockerfile}",
            )


def main() -> None:
    serving = read("serving-image-boundary.yml")
    worker = read("worker-gdal-image.yml")
    validate_pr_cadence("serving-image-boundary.yml", serving)
    validate_pr_cadence("worker-gdal-image.yml", worker)

    # Manual serving runs remain the explicit exact-ref escape hatch and fail
    # closed to every variant, independent of the candidate path classifier.
    dispatch = serving.split('if [[ "${{ github.event_name }}" == "workflow_dispatch" ]]', 1)
    if len(dispatch) != 2:
        raise AssertionError("manual serving runs must force every variant")
    for variant in ("generic", "lambda", "functions"):
        require(
            dispatch[1].split("exit 0", 1)[0],
            f'echo "{variant}=true"',
            f"manual serving runs must force {variant}",
        )

    # The worker is still re-proved independently of PR cadence, including the
    # real entrypoint and a fresh vulnerability verdict.
    require(worker, "  schedule:", "worker image must retain its scheduled backstop")
    require(worker, "docker/worker-gdal/Dockerfile", "worker backstop must build the final image")
    require(worker, "--worker-image honua-worker-gdal:boundary", "worker entrypoint smoke is required")
    require(worker, "exit-code: '1'", "worker Trivy policy must remain enforcing")

    validate_serving_publication_boundaries()
    print("Native-image PR cadence fixtures passed.")


if __name__ == "__main__":
    main()
