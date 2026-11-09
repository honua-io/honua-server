#!/usr/bin/env python3
"""Select an official candidate archive and prepare an isolated installation proof input."""
import argparse
import base64
import hashlib
import json
from pathlib import Path
import re
import subprocess

from package_quickstart import ROOT
from validate_shipped_quickstart import unpack

REPOSITORY = "honua-io/honua-server"


def github(*arguments: str) -> str:
    result = subprocess.run(["gh", *arguments], stdin=subprocess.DEVNULL, capture_output=True,
                            text=True, timeout=180)
    if result.returncode:
        raise RuntimeError("Candidate artifact lookup failed; output withheld")
    return result.stdout


def select_candidate() -> tuple[dict, str]:
    runs = json.loads(github("api", f"repos/{REPOSITORY}/actions/workflows/nightly-container-build.yml/runs"
                            "?status=success&per_page=30"))["workflow_runs"]
    for run in runs:
        sha = run["head_sha"]
        if not re.fullmatch(r"[a-f0-9]{40}", sha):
            continue
        if run["head_branch"] not in ("trunk", "nightly-candidate-" + sha[:7]):
            continue
        name = "honua-compose-quickstart-" + sha[:7]
        artifacts = json.loads(github("api", f"repos/{REPOSITORY}/actions/runs/{int(run['id'])}/artifacts"))["artifacts"]
        if any(artifact["name"] == name and not artifact["expired"] for artifact in artifacts):
            return run, name
    raise ValueError("No successful official candidate run has an available quickstart archive")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--prospective-source-sha")
    args = parser.parse_args()
    if args.output.exists():
        raise ValueError("Proof output must be a new directory")
    if args.prospective_source_sha and not re.fullmatch(r"[a-f0-9]{40}", args.prospective_source_sha):
        raise ValueError("Prospective source revision must be a full commit identifier")
    args.output.mkdir(parents=True)
    run, name = select_candidate()
    download = args.output / "published"
    github("run", "download", str(run["id"]), "--repo", REPOSITORY, "--name", name, "--dir", str(download))
    archive = download / "honua-compose-quickstart.tar.gz"
    lock = unpack(archive, download / "contents")
    published_digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    kind = "shipped"
    source = run["head_sha"]
    if args.prospective_source_sha:
        actual_source = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
        if actual_source != args.prospective_source_sha:
            raise ValueError("Prospective package source must match the checked-out commit")
        kind = "prospective-pr"
        source = args.prospective_source_sha
        if "console" not in lock:
            raise ValueError("The candidate release lock does not yet pin Console; release packaging is required")
        command = ["python3", str(ROOT / "scripts/docker/package_quickstart.py")]
        for component in ("server", "executor", "worker", "console"):
            command.extend([f"--{component}-image", lock[component]])
        command.extend(["--version", lock["version"], "--output", str(args.output / "honua-compose-quickstart")])
        subprocess.run(command, stdin=subprocess.DEVNULL, check=True, timeout=60)
        archive = args.output / "honua-compose-quickstart.tar.gz"
        digest = hashlib.sha256(archive.read_bytes()).hexdigest()
        archive.with_name(archive.name + ".sha256").write_text(f"{digest}  {archive.name}\n")
        document = (ROOT / "docs/get-started/quickstart.md").read_bytes()
    else:
        for path in (archive, archive.with_name(archive.name + ".sha256")):
            path.rename(args.output / path.name)
        payload = json.loads(github("api", f"repos/{REPOSITORY}/contents/docs/get-started/quickstart.md?ref={source}"))
        document = base64.b64decode(payload["content"], validate=False)
    (args.output / "quickstart.md").write_bytes(document)
    metadata = {"packageKind": kind, "packageSourceSha": source, "serverSourceSha": run["head_sha"],
                "publishedRunId": run["id"], "publishedArchiveSha256": published_digest, "archiveSha256": hashlib.sha256(
                    (args.output / "honua-compose-quickstart.tar.gz").read_bytes()).hexdigest()}
    (args.output / "input.json").write_text(json.dumps(metadata, indent=2) + "\n")


if __name__ == "__main__":
    main()
