#!/usr/bin/env python3
"""Regression checks for release-bundle trunk/candidate separation."""

from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / ".github" / "workflows" / "release-bundle.yml"


def require(source: str, fragment: str, description: str) -> None:
    if fragment not in source:
        raise SystemExit(f"[ERROR] {description}: missing {fragment!r}")


def main() -> None:
    source = WORKFLOW.read_text(encoding="utf-8")

    require(
        source,
        'if [[ "${GITHUB_REF}" == "refs/heads/trunk" ]]',
        "candidate classification must use the repository trunk ref",
    )
    require(source, "tag_prefix=rc", "trunk builds must retain the RC tag channel")
    require(
        source,
        "tag_prefix=test-off-trunk",
        "branch builds must use an explicit non-candidate tag channel",
    )
    require(
        source,
        "Candidate promotion is permitted only from refs/heads/trunk",
        "off-trunk promotion must hard-fail",
    )
    require(
        source,
        "needs.release-context.outputs.candidate == 'true' && needs.rc-image.result == 'success'",
        "candidate manifest aggregation must be trunk-only",
    )
    require(
        source,
        "needs.release-context.outputs.candidate == 'true' && github.event.inputs.promote == 'true'",
        "release publication must be trunk-only",
    )
    require(
        source,
        'tag="${{ needs.release-context.outputs.tag_prefix }}-${{ github.event.inputs.release_id }}-${short}"',
        "image tags must consume the classified channel",
    )

    print("release-bundle ref policy validation passed")


if __name__ == "__main__":
    main()
