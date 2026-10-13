#!/usr/bin/env python3
"""Bind a read-only image vulnerability inventory to an immutable candidate."""

import hashlib
import json
import os
from pathlib import Path
import re
import sys


def validate(image, platform, revision):
    if not re.fullmatch(r"ghcr\.io/honua-io/honua-server@sha256:[0-9a-f]{64}", image):
        raise ValueError("An immutable honua-server image digest is required")
    if platform not in ("linux/amd64", "linux/arm64"):
        raise ValueError("Unsupported image platform")
    if not re.fullmatch(r"[0-9a-f]{40}", revision):
        raise ValueError("A full expected server revision is required")


def bind_identity(image, platform, revision, inspected):
    validate(image, platform, revision)
    if len(inspected) != 1:
        raise ValueError("Expected exactly one inspected image")
    metadata = inspected[0]
    if image not in metadata.get("RepoDigests", []):
        raise ValueError("Pulled image does not match the requested digest")
    if f"{metadata.get('Os')}/{metadata.get('Architecture')}" != platform:
        raise ValueError("Pulled image platform mismatch")
    labels = metadata.get("Config", {}).get("Labels") or {}
    if labels.get("org.opencontainers.image.revision") != revision:
        raise ValueError("Pulled image source revision mismatch")
    return {"image": image, "platform": platform, "sourceRevision": revision,
            "imageId": metadata["Id"]}


def verdict(identity, inventory):
    metadata = inventory.get("Metadata", {})
    if identity["image"] not in metadata.get("RepoDigests", []):
        raise ValueError("Trivy inventory digest mismatch")
    if metadata.get("ImageID") != identity["imageId"]:
        raise ValueError("Trivy inventory image config mismatch")
    if not isinstance(inventory.get("Results"), list) or not inventory["Results"]:
        raise ValueError("Trivy inventory has no scan results")
    findings = [finding for result in inventory["Results"]
                for finding in (result.get("Vulnerabilities") or [])]
    blocking = [finding for finding in findings
                if finding.get("Severity") in ("HIGH", "CRITICAL")
                and finding.get("FixedVersion")]
    return {**identity, "status": "fail" if blocking else "pass",
            "findingCount": len(findings), "blockingFindings": blocking,
            "policy": "Fail fixed HIGH/CRITICAL; retain all severities and unfixed findings"}


def main():
    image, platform, revision = (os.environ.get(name, "") for name in
                                 ("SCAN_IMAGE", "SCAN_PLATFORM", "SCAN_REVISION"))
    validate(image, platform, revision)
    mode = sys.argv[1]
    if mode == "validate":
        return 0
    if mode == "identity":
        identity = bind_identity(image, platform, revision,
                                 json.loads(Path("existing-image-inspect.json").read_text()))
        identity["workflowRun"] = f"{os.environ.get('GITHUB_SERVER_URL')}/{os.environ.get('GITHUB_REPOSITORY')}/actions/runs/{os.environ.get('GITHUB_RUN_ID')}"
        Path("existing-image-identity.json").write_text(json.dumps(identity, indent=2) + "\n")
        return 0
    if mode != "verdict":
        raise ValueError("Unknown scan mode")
    raw = Path("existing-image-trivy.json").read_bytes()
    identity = json.loads(Path("existing-image-identity.json").read_text())
    result = verdict(identity, json.loads(raw))
    result["inventorySha256"] = hashlib.sha256(raw).hexdigest()
    Path("existing-image-verdict.json").write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps({key: result[key] for key in ("image", "platform", "status", "findingCount")}))
    return int(result["status"] != "pass")


if __name__ == "__main__":
    sys.exit(main())
