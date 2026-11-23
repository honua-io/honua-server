#!/usr/bin/env python3
# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE.
"""Collect dated diagnostic evidence from the latest completed trunk runs.

Uses gh's authenticated REST transport with actions:read. Never dispatches jobs,
executes artifact contents, or chooses an older successful run over a failure.
"""
from __future__ import annotations

import argparse
import datetime as dt
import io
import json
from pathlib import Path
import subprocess
import zipfile

WORKFLOWS = {
    "client-interop-nightly.yml": "interop",
    "pyqgis-client-compat-nightly.yml": "interop",
    "client-compat-smoke-nightly.yml": "interop",
    "cite-evidence-report.yml": "cite",
    "load-soak-nightly.yml": "geobench",
}
MAX_ARCHIVE_BYTES = 50 * 1024 * 1024
MAX_DOCUMENT_BYTES = 2 * 1024 * 1024


def api(repo: str, endpoint: str, *, binary: bool = False):
    data = subprocess.check_output(["gh", "api", f"repos/{repo}/{endpoint}"], timeout=120)
    return data if binary else json.loads(data)


def latest_run(runs: list[dict], repo: str, workflow: str) -> dict | None:
    trusted = [run for run in runs if run.get("status") == "completed"
               and run.get("head_branch") == "trunk"
               and run.get("head_repository", {}).get("full_name") == repo
               and run.get("event") in {"schedule", "workflow_dispatch"}
               and run.get("path") == f".github/workflows/{workflow}"]
    return max(trusted, key=lambda run: (run["created_at"], run["id"]), default=None)


def wanted(name: str, kind: str) -> bool:
    basename = name.rsplit("/", 1)[-1]
    return ((kind == "interop" and basename.endswith(".cert.json"))
            or (kind == "cite" and basename == "conformance-summary.json")
            or (kind == "geobench" and basename == "load-stats.json"))


def sanitize(document: dict, kind: str) -> dict:
    if kind == "interop":
        fields = ("client_lane", "clientLane", "protocol", "run_date", "server_commit", "summary")
        result = {key: document[key] for key in fields if key in document}
        for key in ("results", "extensions"):
            if key in document:
                result[key] = [{"status": item.get("status")} for item in document[key]]
        return result
    if kind == "cite":
        result = {key: document[key] for key in ("generatedAt", "git") if key in document}
        fields = ("name", "status", "totalTests", "passed", "failed", "skipped", "cantTell")
        result["suites"] = [{key: row[key] for key in fields if key in row}
                            for row in document.get("suites", [])]
        return result
    result = {key: document[key] for key in ("generatedAt", "profile", "durationSeconds") if key in document}
    fields = ("name", "durationSeconds", "okCount", "failCount", "okRps", "meanMs", "maxMs", "p95Ms", "p99Ms")
    result["scenarios"] = [{key: row[key] for key in fields if key in row}
                           for row in document.get("scenarios", [])]
    return result


def read_documents(data: bytes, kind: str) -> list[dict]:
    documents = []
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        members = [entry for entry in archive.infolist() if wanted(entry.filename, kind)]
        if len(members) > 200:
            raise ValueError("too many evidence documents")
        for entry in sorted(members, key=lambda item: item.filename):
            if entry.file_size > MAX_DOCUMENT_BYTES:
                raise ValueError("oversized evidence document")
            document = json.loads(archive.read(entry))
            if not isinstance(document, dict):
                raise ValueError("evidence document must be an object")
            documents.append(sanitize(document, kind))
    return documents


def collect_producer(repo: str, workflow: str, kind: str) -> dict:
    runs = api(repo, f"actions/workflows/{workflow}/runs?branch=trunk&status=completed&per_page=100")
    run = latest_run(runs["workflow_runs"], repo, workflow)
    result = {"workflow": workflow, "kind": kind, "documents": [], "artifacts": []}
    if run is None:
        return {**result, "conclusion": "missing"}
    result.update(runId=run["id"], runAttempt=run["run_attempt"], sourceSha=run["head_sha"],
                  conclusion=run["conclusion"], observedAt=run["updated_at"],
                  runUrl=run["html_url"])
    artifacts = api(repo, f"actions/runs/{run['id']}/artifacts?per_page=100")["artifacts"]
    if len(artifacts) >= 100:
        raise ValueError("artifact listing exceeds collection bound")
    for artifact in sorted(artifacts, key=lambda row: row["id"]):
        name = artifact["name"]
        eligible = ((kind == "interop" and "-evidence" in name)
                    or (kind == "cite" and name.startswith("cite-conformance-evidence-"))
                    or (kind == "geobench" and name.startswith("load-soak-reports-")))
        if (not eligible or artifact["expired"]
                or artifact["created_at"] < run["run_started_at"]):
            continue
        if artifact["size_in_bytes"] > MAX_ARCHIVE_BYTES:
            raise ValueError("oversized evidence artifact")
        documents = read_documents(api(repo, f"actions/artifacts/{artifact['id']}/zip", binary=True), kind)
        if documents:
            result["artifacts"].append(artifact["id"])
            result["documents"].extend(documents)
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", default="honua-io/honua-server")
    parser.add_argument("--output", type=Path,
                        default=Path("docs/gis/data/nightly-capability-evidence.v1.json"))
    args = parser.parse_args()
    # Transport/decoding failures abort publication; never replace evidence
    # with an apparently successful empty snapshot after an API failure.
    producers = [collect_producer(args.repo, workflow, kind) for workflow, kind in WORKFLOWS.items()]
    snapshot = {"schemaVersion": "1.0.0", "repository": args.repo,
                "collectedAt": dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00", "Z"),
                "producers": producers}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(snapshot, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
