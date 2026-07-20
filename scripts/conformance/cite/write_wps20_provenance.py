#!/usr/bin/env python3
"""Write validated Honua server provenance for a WPS 2.0 CITE run."""

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path


GIT_SHA = re.compile(r"^[0-9a-fA-F]{40,64}$")
IMAGE_ID = re.compile(r"^sha256:[0-9a-fA-F]{64}$")


def write_provenance(
    tested_git_sha: str,
    checkout_git_sha: str,
    server_container_id: str,
    server_image_id: str,
    server_build_mode: str,
    requested_server_image: str,
    image_inspect_path: Path,
    output_path: Path,
    require_tested_git_sha: bool,
) -> None:
    if require_tested_git_sha and not GIT_SHA.fullmatch(tested_git_sha):
        raise ValueError("A full tested Honua git SHA is required")
    if tested_git_sha != "unknown" and not GIT_SHA.fullmatch(tested_git_sha):
        raise ValueError("Tested Honua git SHA must be full-length or 'unknown'")
    if checkout_git_sha != "unknown" and not GIT_SHA.fullmatch(checkout_git_sha):
        raise ValueError("Checked-out Honua git SHA must be full-length or 'unknown'")
    if tested_git_sha != "unknown" and tested_git_sha != checkout_git_sha:
        raise ValueError("Tested Honua git SHA does not match the checked-out commit")
    if not re.fullmatch(r"^[0-9a-fA-F]{64}$", server_container_id):
        raise ValueError("Honua Server container ID must be a full Docker identifier")
    if not IMAGE_ID.fullmatch(server_image_id):
        raise ValueError("Honua Server image ID must be an immutable sha256 identifier")
    if server_build_mode not in {"source-build", "prebuilt", "local-existing"}:
        raise ValueError("Honua Server build mode is invalid")
    if require_tested_git_sha and server_build_mode == "local-existing":
        raise ValueError("CI provenance cannot use an untracked local-existing image")
    if server_build_mode == "prebuilt" and not requested_server_image:
        raise ValueError("A requested image reference is required for a prebuilt image")

    inspected = json.loads(image_inspect_path.read_text(encoding="utf-8"))
    if not isinstance(inspected, list) or len(inspected) != 1:
        raise ValueError("Honua Server image inspection must contain exactly one image")
    if inspected[0].get("Id") != server_image_id:
        raise ValueError("Running Honua Server image ID does not match its image inspection")

    payload = {
        "schemaVersion": 1,
        "testedHonuaGitSha": tested_git_sha,
        "checkedOutHonuaGitSha": checkout_git_sha,
        "serverBuildMode": server_build_mode,
        "requestedServerImage": requested_server_image or None,
        "serverContainerId": server_container_id,
        "serverImageId": server_image_id,
        "serverImageRepoDigests": inspected[0].get("RepoDigests") or [],
        "serverImageReference": "honua-server:latest",
        "serverImageInspectFile": image_inspect_path.name,
    }
    temporary_path = output_path.with_suffix(output_path.suffix + ".tmp")
    temporary_path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    temporary_path.replace(output_path)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--tested-git-sha", required=True)
    parser.add_argument("--checkout-git-sha", required=True)
    parser.add_argument("--server-container-id", required=True)
    parser.add_argument("--server-image-id", required=True)
    parser.add_argument("--server-build-mode", choices=("source-build", "prebuilt", "local-existing"), required=True)
    parser.add_argument("--requested-server-image", default="")
    parser.add_argument("--image-inspect", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--require-tested-git-sha", action="store_true")
    args = parser.parse_args()

    try:
        write_provenance(
            args.tested_git_sha,
            args.checkout_git_sha,
            args.server_container_id,
            args.server_image_id,
            args.server_build_mode,
            args.requested_server_image,
            args.image_inspect,
            args.output,
            args.require_tested_git_sha,
        )
    except (OSError, ValueError, json.JSONDecodeError) as error:
        parser.error(str(error))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
