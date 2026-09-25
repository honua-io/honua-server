#!/usr/bin/env python3
"""Drive the existing multidimensional scan API and retain its actual derived store."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

from derived_zarr import (MAX_OBJECT_BYTES, MAX_OBJECTS, MAX_TOTAL_BYTES, SCHEMA,
                          bind_registration, safe_key, validate_receipt)

COMPOSE = ["docker", "compose", "-f", "docker/cng/compose.yml", "-f", "docker/cng/derived-zarr.yml"]
BUCKET = "honua-cng-fixtures"


def now():
    return datetime.now(timezone.utc).isoformat()


def command(*args):
    return subprocess.check_output(args, text=True, timeout=90)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--base-url", default="http://localhost:8094")
    args = parser.parse_args()
    artifacts = args.artifacts
    receipt = {"schema": SCHEMA, "qualification": False, "outcome": "fail",
               "started_at": now(), "responses": [], "scope": "runner-local worker and S3 emulator"}

    def api(path, method="GET", data=None, expected=200):
        if not path.startswith("/api/v1/admin/") or ".." in path or "?" in path.split("/admin/", 1)[0]:
            raise ValueError("Unexpected admin response URL")
        request = urllib.request.Request(args.base_url + path, method=method,
                                        data=None if data is None else json.dumps(data).encode(),
                                        headers={"Content-Type": "application/json", "X-API-Key": "CngAdminPassword123!"})
        try:
            response = urllib.request.urlopen(request, timeout=45)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            body = response.read(1024 * 1024 + 1)
            if response.geturl() != args.base_url + path or len(body) > 1024 * 1024:
                raise ValueError("Redirected or oversized admin response")
            try:
                parsed = json.loads(body)
            except ValueError:
                parsed = {"raw": body.decode("utf-8", errors="replace")}
            receipt["responses"].append({"path": path, "method": method, "status": response.status, "body": parsed})
            if response.status != expected:
                raise ValueError(f"Unexpected admin status {response.status}: {path}")
            return parsed

    try:
        identity = json.loads((artifacts / "derived-zarr-identity.json").read_text())
        receipt["identity"] = identity
        nonce = f"{os.environ.get('GITHUB_RUN_ID', 'diagnostic')}-{os.environ.get('GITHUB_RUN_ATTEMPT', '1')}"
        prefix = safe_key(f"derived-zarr/{nonce}")
        source_key = prefix + "/canonical.nc"
        source = artifacts / "canonical.nc"
        receipt["input_sha256"] = hashlib.sha256(source.read_bytes()).hexdigest()
        container = command(*COMPOSE, "ps", "-q", "localstack").strip()
        if not container:
            raise ValueError("LocalStack fixture is not running")
        existing = json.loads(command(*COMPOSE, "exec", "-T", "localstack", "awslocal", "s3api", "list-objects-v2",
                                      "--bucket", BUCKET, "--prefix", prefix + "/"))
        if existing.get("Contents"):
            raise ValueError("Derived rehearsal prefix must be empty before the scan")
        command("docker", "cp", str(source), container + ":/tmp/cng-derived-input.nc")
        command(*COMPOSE, "exec", "-T", "localstack", "awslocal", "s3api", "put-object", "--bucket", BUCKET,
                "--key", source_key, "--body", "/tmp/cng-derived-input.nc")
        # Public read applies only to this ephemeral output prefix. No real AWS
        # credentials/infrastructure or product authorization claim is involved.
        policy = {"Version": "2012-10-17", "Statement": [{"Effect": "Allow", "Principal": "*",
                  "Action": "s3:GetObject", "Resource": f"arn:aws:s3:::{BUCKET}/{prefix}/canonical.zarr/*"}]}
        command(*COMPOSE, "exec", "-T", "localstack", "awslocal", "s3api", "put-bucket-policy",
                "--bucket", BUCKET, "--policy", json.dumps(policy))
        receipt["before"] = api("/api/v1/admin/zarr-stores?layerId=1000")
        if receipt["before"]:
            raise ValueError("Fixture layer already has a Zarr registration")
        receipt["coverage"] = api("/api/v1/admin/multidim-coverages/", "POST",
                                   {"layerId": 1000, "name": "CNG derived " + nonce, "format": "NetCdf4",
                                    "provider": "AwsS3", "bucket": BUCKET, "objectKey": source_key,
                                    "variables": ["temperature"]}, 201)
        submitted = api(f"/api/v1/admin/multidim-coverages/{receipt['coverage']['id']}/refresh", "POST", expected=202)
        job_id = submitted["jobId"]
        if submitted["statusUrl"] != f"/api/v1/admin/multidim-coverages/jobs/{job_id}" or not job_id or "/" in job_id:
            raise ValueError("Scan response has an unexpected job identity")
        command(*COMPOSE, "--profile", "derived-zarr", "up", "-d", "zarr-worker")
        deadline = time.monotonic() + 300
        while True:
            receipt["job"] = api(submitted["statusUrl"])
            if receipt["job"].get("jobId") != job_id:
                raise ValueError("Scan polling changed job identity")
            if receipt["job"].get("status") in ("succeeded", "failed", "cancelled"):
                break
            if time.monotonic() >= deadline:
                raise TimeoutError("Multidimensional conversion did not complete within five minutes")
            time.sleep(2)
        receipt["after"] = api("/api/v1/admin/zarr-stores?layerId=1000")
        registration = bind_registration(receipt["before"], receipt["after"], receipt["coverage"], receipt["job"])
        receipt["job_logs"] = api(f"/api/v1/admin/jobs/{job_id}/logs?limit=200")
        root = safe_key(registration["rootPath"])
        receipt["store_url"] = f"http://127.0.0.1:4595/{BUCKET}/{root}"
        listing = json.loads(command(*COMPOSE, "exec", "-T", "localstack", "awslocal", "s3api", "list-objects-v2",
                                     "--bucket", BUCKET, "--prefix", root + "/"))
        objects = listing.get("Contents", [])
        if listing.get("IsTruncated") or not objects or len(objects) > MAX_OBJECTS:
            raise ValueError("Derived object listing is empty, truncated or oversized")
        receipt["objects"] = {}
        total = 0
        for entry in objects:
            key = safe_key(entry["Key"])
            if not key.startswith(root + "/"):
                raise ValueError("Listed object escapes the derived prefix")
            relative = safe_key(key[len(root) + 1:])
            with urllib.request.urlopen(receipt["store_url"] + "/" + relative, timeout=30) as response:
                data = response.read(MAX_OBJECT_BYTES + 1)
                if response.status != 200 or response.geturl() != receipt["store_url"] + "/" + relative:
                    raise ValueError("Unexpected output archive response")
            total += len(data)
            if len(data) != entry["Size"] or len(data) > MAX_OBJECT_BYTES or total > MAX_TOTAL_BYTES:
                raise ValueError("Derived output size changed or exceeds the rehearsal bound")
            target = artifacts / "honua-derived.zarr" / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
            receipt["objects"][relative] = {"size": len(data), "sha256": hashlib.sha256(data).hexdigest()}
        # Archival GETs above preserve immutable bytes; they are never credited
        # as canonical reader requests. The reader separately measures its I/O.
        receipt["archive_transfer"] = {"requests": len(objects), "bytes": total}
        # Durable completion may precede the final worker log flush briefly.
        for attempt in range(5):
            logs = command(*COMPOSE, "logs", "--no-color", "--no-log-prefix", "zarr-worker")
            if f"Job execution completed: {job_id}, Status=Succeeded" in logs:
                break
            time.sleep(1)
        (artifacts / "derived-zarr-worker.log").write_text(logs, encoding="utf-8")
        receipt["worker_log_sha256"] = hashlib.sha256(logs.encode()).hexdigest()
        receipt["completed_at"] = now()
        receipt["outcome"] = "bound"
        validate_receipt(receipt, artifacts, identity["source_sha"], identity["server_image"].split("@")[-1])
    except Exception as error:
        receipt["outcome"] = "fail"
        receipt["error"] = f"{type(error).__name__}: {error}"
        print(receipt["error"], flush=True)
    finally:
        receipt["completed_at"] = now()
        (artifacts / "derived-zarr-receipt.json").write_text(json.dumps(receipt, indent=2), encoding="utf-8")
    return 0 if receipt["outcome"] == "bound" else 1


if __name__ == "__main__":
    raise SystemExit(main())
