#!/usr/bin/env python3
"""Move the exact gated worker image into an optional, non-release publication."""

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import xml.etree.ElementTree as ET
from pathlib import Path

REPOSITORY = "honua-io/honua-server"
SUBJECT = "ghcr.io/honua-io/honua-worker-gdal"
LOCAL_IMAGE = "honua-worker-gdal:boundary"
WORKFLOW = ".github/workflows/worker-gdal-image.yml"
TRXS = ("worker-container-handoff.trx", "worker-pdal-proof.trx", "native-public-api.trx")


def require(condition, finding):
    if not condition:
        raise ValueError(finding)


def digest(path):
    checksum = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            checksum.update(chunk)
    return checksum.hexdigest()


def command(*args):
    return subprocess.check_output(args, text=True, encoding="utf-8").strip()


def write_json(path, value):
    Path(path).write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def identity(environment):
    sha = environment.get("GITHUB_SHA", "")
    run = environment.get("GITHUB_RUN_ID", "")
    attempt = environment.get("GITHUB_RUN_ATTEMPT", "")
    require(re.fullmatch(r"[0-9a-f]{40}", sha), "missing exact source SHA")
    require(re.fullmatch(r"[1-9][0-9]*", run), "missing authoritative run ID")
    require(re.fullmatch(r"[1-9][0-9]*", attempt), "missing authoritative run attempt")
    require(environment.get("GITHUB_REPOSITORY") == REPOSITORY, "unexpected repository")
    return {"source_sha": sha, "run_id": run, "run_attempt": attempt,
            "repository": REPOSITORY, "workflow": WORKFLOW}


def require_publication(environment):
    result = identity(environment)
    require(environment.get("GITHUB_EVENT_NAME") == "workflow_dispatch", "publication requires explicit dispatch")
    require(environment.get("GITHUB_REF") == "refs/heads/trunk", "publication requires canonical trunk")
    require(environment.get("PUBLISH_NIGHTLY") == "true", "publication was not explicitly selected")
    return result


def test_receipt(path):
    cases = ET.parse(path).getroot().findall(".//{*}UnitTestResult")
    require(cases and all(case.get("outcome") == "Passed" for case in cases),
            f"missing, failed, or skipped cases in {Path(path).name}")
    return {"file": Path(path).name, "sha256": digest(path), "passed": len(cases), "nonpassing": 0}


def inspect_image(image, source_sha, expected_id=None):
    records = json.loads(command("docker", "image", "inspect", image))
    require(len(records) == 1, "expected exactly one Docker image")
    image = records[0]
    require(re.fullmatch(r"sha256:[0-9a-f]{64}", image["Id"]), "invalid Docker config digest")
    require(image["Os"] == "linux" and image["Architecture"] == "amd64", "unexpected worker platform")
    require(image["Config"]["Labels"].get("org.opencontainers.image.revision") == source_sha,
            "worker revision does not match the gated source")
    require(expected_id is None or image["Id"] == expected_id, "worker differs from the tested image")
    return image


def prepare(root, results, scan, environment):
    origin = identity(environment)
    require(environment.get("GITHUB_EVENT_NAME") == "workflow_dispatch", "bundle preparation requires dispatch")
    root.mkdir(parents=True, exist_ok=True)
    image = inspect_image(LOCAL_IMAGE, origin["source_sha"])
    receipts = [test_receipt(results / name) for name in TRXS]
    scan_document = json.loads(scan.read_text(encoding="utf-8"))
    require(scan_document.get("runs"), "missing vulnerability report")
    require(shutil.disk_usage(root).free > image["Size"] + 512 * 1024 * 1024,
            "insufficient free disk to retain the tested worker archive")
    archive = root / "worker-image.tar"
    subprocess.run(["docker", "save", "--output", str(archive), LOCAL_IMAGE], check=True)
    for name in TRXS:
        shutil.copyfile(results / name, root / name)
    shutil.copyfile(scan, root / "trivy-worker-gdal.sarif")
    receipt = {"schema": "honua.worker-publication-bundle.v1", "qualification": False, **origin,
               "image_id": image["Id"], "platform": "linux/amd64", "archive_sha256": digest(archive),
               "tests": receipts, "scan_sha256": digest(scan),
               "scan_policy": {"severity": ["CRITICAL", "HIGH"], "ignore_unfixed": True, "exit_code": 1}}
    write_json(root / "bundle.json", receipt)
    # Round-trip the archive through the same validation the publisher uses.
    load_verified(root, environment)


def load_verified(root, environment):
    origin = identity(environment)
    receipt = json.loads((root / "bundle.json").read_text(encoding="utf-8"))
    require(receipt.get("schema") == "honua.worker-publication-bundle.v1"
            and receipt.get("qualification") is False, "invalid publication bundle")
    require(all(receipt.get(key) == value for key, value in origin.items()), "bundle is from a different source/run")
    require(receipt.get("platform") == "linux/amd64", "unexpected bundle platform")
    require(receipt.get("archive_sha256") == digest(root / "worker-image.tar"), "archive digest mismatch")
    require(receipt.get("scan_sha256") == digest(root / "trivy-worker-gdal.sarif"), "scan digest mismatch")
    require(receipt.get("tests") == [test_receipt(root / name) for name in TRXS], "test receipt mismatch")
    require(receipt.get("scan_policy") == {"severity": ["CRITICAL", "HIGH"], "ignore_unfixed": True, "exit_code": 1},
            "vulnerability policy mismatch")
    subprocess.run(["docker", "load", "--input", str(root / "worker-image.tar")], check=True)
    inspect_image(LOCAL_IMAGE, origin["source_sha"], receipt["image_id"])
    return receipt


def verify_remote(root, receipt, reference):
    require(re.fullmatch(re.escape(SUBJECT) + r"@sha256:[0-9a-f]{64}", reference), "unexpected published subject")
    manifest = json.loads(command("docker", "buildx", "imagetools", "inspect", reference, "--raw"))
    require(manifest.get("config", {}).get("digest") == receipt["image_id"],
            "remote manifest does not contain the exact tested image config")
    subprocess.run(["docker", "pull", reference], check=True)
    inspect_image(reference, receipt["source_sha"], receipt["image_id"])
    write_json(root / "published-manifest.json", manifest)


def publish(root, environment):
    origin = require_publication(environment)  # Before Docker or registry mutations.
    receipt = load_verified(root, environment)
    tag = f"{SUBJECT}:nightly-{origin['source_sha']}-{origin['run_id']}-{origin['run_attempt']}"
    subprocess.run(["docker", "tag", receipt["image_id"], tag], check=True)
    subprocess.run(["docker", "push", tag], check=True)
    remote_digest = command("docker", "buildx", "imagetools", "inspect", tag, "--format", "{{.Manifest.Digest}}")
    require(re.fullmatch(r"sha256:[0-9a-f]{64}", remote_digest), "registry returned an invalid manifest digest")
    reference = f"{SUBJECT}@{remote_digest}"
    verify_remote(root, receipt, reference)
    write_json(root / "publication.json", {"schema": "honua.worker-nightly-publication.v1",
               "qualification": False, **origin, "platform": "linux/amd64", "image_id": receipt["image_id"],
               "image": reference, "tag": tag, "bundle_sha256": digest(root / "bundle.json"),
               "provenance_verified": False})
    with Path(environment["GITHUB_OUTPUT"]).open("a", encoding="utf-8") as output:
        output.write(f"subject_name={SUBJECT}\nsubject_digest={remote_digest}\nimage={reference}\n")


def finish(root, environment):
    origin = require_publication(environment)
    receipt = json.loads((root / "publication.json").read_text(encoding="utf-8"))
    require(all(receipt.get(key) == value for key, value in origin.items()), "publication receipt source/run mismatch")
    verification = root / "provenance-verification.json"
    require(json.loads(verification.read_text(encoding="utf-8")), "missing verified provenance result")
    receipt.update(provenance_verified=True, provenance_verification_sha256=digest(verification))
    write_json(root / "publication.json", receipt)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("prepare", "publish", "finish"))
    parser.add_argument("--bundle", type=Path, default=Path("artifacts/worker-publication"))
    parser.add_argument("--results", type=Path, default=Path("tests/TestResults"))
    parser.add_argument("--scan", type=Path, default=Path("trivy-worker-gdal.sarif"))
    args = parser.parse_args()
    if args.action == "prepare":
        prepare(args.bundle, args.results, args.scan, os.environ)
    elif args.action == "publish":
        publish(args.bundle, os.environ)
    else:
        finish(args.bundle, os.environ)


if __name__ == "__main__":
    main()
