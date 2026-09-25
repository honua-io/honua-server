#!/usr/bin/env python3
"""Build a seven-slot evidence bundle from actual scheduled-run artifacts."""
import json
import os
import shutil
import subprocess
import tempfile
from datetime import timedelta
from pathlib import Path

from gp_canary import FIXTURE, PROCESSES, SCHEMA, gh_json, now, oracle, payload, scheduled_slot, sha, timestamp, write_json


def candidate_key(receipt):
    candidate = dict(receipt["candidate"])
    # Unrelated trunk commits do not change the tested deployment or oracle.
    # Retain each run's harness SHA, but bind continuity to actual oracle bytes.
    candidate.pop("harness_sha", None)
    return {"candidate": candidate, "endpoint": receipt["endpoint"], "fixture": receipt["fixture"]}


def validate_receipt(folder, run, expected):
    receipt = json.loads((folder / "receipt.json").read_text(encoding="utf-8"))
    if receipt.get("schema") != SCHEMA or receipt.get("fixture") != FIXTURE or receipt.get("outcome") != "pass":
        raise ValueError("missing or failed interval receipt")
    if run["event"] != "schedule" or run["run_attempt"] != 1:
        raise ValueError("only original scheduled attempts count")
    github = receipt["github"]
    for key in ("id", "run_attempt", "event", "created_at", "head_sha", "html_url"):
        if github[key] != run[key]:
            raise ValueError("receipt does not match authoritative GitHub run metadata")
    slot = scheduled_slot(run["created_at"])
    if receipt.get("scheduled_slot") != slot:
        raise ValueError("scheduled slot mismatch")
    started, ended = timestamp(receipt["started_at"]), timestamp(receipt["completed_at"])
    if not timestamp(run["created_at"]) <= started <= ended <= timestamp(slot) + timedelta(hours=2):
        raise ValueError("stale or invalid interval timestamps")
    if candidate_key(receipt) != expected:
        raise ValueError("candidate, endpoint or oracle changed")
    identity = {"deploymentRevision": receipt["candidate"]["server_digest"], "deploymentRevisionSource": "image-digest"}
    if receipt.get("identity_before") != identity or receipt.get("identity_after") != identity:
        raise ValueError("live candidate identity proof is missing")
    operations = receipt.get("operations", [])
    if [item.get("process") for item in operations] != list(PROCESSES):
        raise ValueError("both operation receipts are required")
    retained = [folder / "receipt.json"]
    for operation in operations:
        process = operation["process"]
        if (operation.get("outcome") != "pass" or operation.get("fixture") != FIXTURE
                or not operation.get("operation_id") or operation.get("submit_http_status") != 201
                or not 0 < operation.get("latency_seconds", 0) < 240
                or not operation.get("transitions") or operation["transitions"][-1]["status"] != "successful"):
            raise ValueError("incomplete operation status/latency/transition receipt")
        documents = {}
        for kind in ("input", "output"):
            evidence = operation[kind]
            filename = f"{process.replace('.', '-')}-{kind}.json"
            if evidence["file"] != filename:
                raise ValueError("unexpected evidence filename")
            path = folder / filename
            if sha(path.read_bytes()) != evidence["sha256"]:
                raise ValueError("retained artifact checksum mismatch")
            documents[kind] = json.loads(path.read_text(encoding="utf-8"))
            retained.append(path)
        if documents["input"] != payload(process):
            raise ValueError("fixed input fixture changed")
        if operation.get("metrics") != oracle(process, documents["output"]):
            raise ValueError("missing or inconsistent numerical metrics")
    return receipt, retained


def build_streak(current, runs, load, output, current_event):
    result = {"schema": "honua.gp-canary-streak.v2", "generated_at": now(),
              "required_consecutive_green": 7, "consecutive_green": 0, "ready": False, "runs": []}
    try:
        if current_event != "schedule" or current["run_attempt"] != 1:
            raise ValueError("manual runs and retries cannot advance the scheduled streak")
        anchor = timestamp(scheduled_slot(current["created_at"]))
        current_folder = load(current)
        receipt = json.loads((current_folder / "receipt.json").read_text(encoding="utf-8"))
        expected = candidate_key(receipt)
        result["binding"] = expected
        by_slot = {}
        for run in runs:
            if run["event"] != "schedule":
                continue
            try:
                slot = scheduled_slot(run["created_at"])
                by_slot.setdefault(slot, []).append(run)
            except ValueError:
                continue  # It cannot fill any valid scheduled slot.
        by_slot.setdefault(anchor.isoformat().replace("+00:00", "Z"), [])
        current_slot = anchor.isoformat().replace("+00:00", "Z")
        by_slot[current_slot] = [run for run in by_slot[current_slot] if run["id"] != current["id"]] + [current]
        unbroken = True
        for index in range(7):
            slot = (anchor - timedelta(hours=6 * index)).isoformat().replace("+00:00", "Z")
            entry = {"scheduled_slot": slot, "outcome": "fail"}
            result["runs"].append(entry)
            try:
                matching = by_slot.get(slot, [])
                if len(matching) != 1:
                    raise ValueError("missing or duplicate scheduled interval")
                run = matching[0]
                entry.update(run_id=run["id"], run_attempt=run["run_attempt"], run_url=run["html_url"])
                if run["id"] != current["id"] and (run["status"] != "completed" or run["conclusion"] != "success"):
                    raise ValueError("scheduled workflow did not complete successfully")
                folder = current_folder if run["id"] == current["id"] else load(run)
                checked, files = validate_receipt(folder, run, expected)
                retained_folder = output.parent / "intervals" / str(run["id"])
                retained_folder.mkdir(parents=True, exist_ok=True)
                hashes = {}
                for path in files:
                    shutil.copyfile(path, retained_folder / path.name)
                    hashes[path.name] = sha(path.read_bytes())
                entry.update(outcome="pass", evidence_sha256=hashes, receipt=checked)
                if unbroken:
                    result["consecutive_green"] += 1
            except Exception as error:
                entry["finding"] = str(error)
                unbroken = False
        result["ready"] = result["consecutive_green"] == 7
    except Exception as error:
        result["finding"] = str(error)
        result["runs"].append({"run_id": current.get("id"), "outcome": "fail", "finding": str(error)})
    result["observed_runs"] = len(result["runs"])
    write_json(output, result)
    return result


def main():
    output = Path(os.environ.get("HONUA_GP_CANARY_STREAK_RECEIPT", "artifacts/gp-canary/streak.json"))
    try:
        repository, run_id = os.environ["GITHUB_REPOSITORY"], os.environ["GITHUB_RUN_ID"]
        current = gh_json("api", f"repos/{repository}/actions/runs/{run_id}")
        history = gh_json("api", f"repos/{repository}/actions/workflows/gp-buffer-canary.yml/runs?event=schedule&per_page=100")["workflow_runs"]
        with tempfile.TemporaryDirectory(prefix="gp-canary-history-") as temporary:
            def load(run):
                if str(run["id"]) == run_id:
                    return Path(os.environ.get("HONUA_GP_CANARY_RECEIPT", "artifacts/gp-canary/receipt.json")).parent
                folder = Path(temporary) / str(run["id"])
                subprocess.run(["gh", "run", "download", str(run["id"]), "--repo", repository,
                                "--name", f"gp-buffer-canary-{run['id']}-1", "--dir", str(folder)],
                               capture_output=True, check=True)
                return folder
            build_streak(current, history, load, output, os.environ.get("GITHUB_EVENT_NAME"))
    except Exception as error:
        write_json(output, {"schema": "honua.gp-canary-streak.v2", "ready": False,
                            "consecutive_green": 0, "generated_at": now(), "finding": str(error)})
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
