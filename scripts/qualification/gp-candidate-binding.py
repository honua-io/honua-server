#!/usr/bin/env python3
"""Resolve the release pin and reject GP recovery receipts from any other image."""

import argparse
import hashlib
import json
import re
from pathlib import Path

import yaml


def candidate(path):
    raw = path.read_bytes()
    manifest = yaml.safe_load(raw)
    server = manifest["components"]["honua-server"]
    source = server["sha"]
    digest = server["digest"]
    if not re.fullmatch(r"[0-9a-f]{40}", source):
        raise ValueError("candidate server source must be a full commit SHA")
    if not re.fullmatch(r"sha256:[0-9a-f]{64}", digest):
        raise ValueError("candidate server image must have an immutable digest")
    image = server["image"].split("@")[0].split(":")[0] + "@" + digest
    if not image.startswith("ghcr.io/honua-io/honua-server@"):
        raise ValueError("unexpected server image repository")
    return {"manifest_sha256": hashlib.sha256(raw).hexdigest(),
            "source_sha": source, "server_image": image, "server_digest": digest}


def verify(pin, summary):
    required = {"topology", "output-store-attestation", "output-store-dr", "cleanup"}
    if (summary["lane"] != "output-store-dr" or set(summary["declared_scenarios"]) != required
            or summary["missing_scenarios"] or summary["duplicate_receipts"]
            or summary["failed"] != 0 or summary["passed"] != len(required)):
        raise ValueError("all required GP restore scenarios must execute and pass")
    scenarios = summary["scenarios"]
    if len(scenarios) != len(required) or {s["scenario"] for s in scenarios} != required:
        raise ValueError("scenario receipts do not match the declared denominator")
    for scenario in scenarios:
        if scenario["outcome"] != "pass":
            raise ValueError("a required scenario did not pass")
        identity = scenario["candidate"]
        if (identity["requested"]["server_image"] != pin["server_image"]
                or identity["requested"]["source_sha"] != pin["source_sha"]):
            raise ValueError("receipt was requested against another candidate")
        for host in ("server", "server-peer", "worker"):
            observed = identity["observed"][host]
            expected = pin["server_image"] if host != "worker" else identity["requested"]["worker_image"]
            if (observed["image_ref"] != expected or observed["revision"] != pin["source_sha"]
                    or expected not in observed["repo_digests"]):
                raise ValueError("running host does not match the pinned image/source")
    recovery = next(s["evidence"] for s in scenarios if s["scenario"] == "output-store-dr")
    artifact = recovery["artifact"]
    if (not re.fullmatch(r"[0-9a-f]{64}", artifact["sha256_before"])
            or artifact["sha256_before"] != artifact["sha256_after"]):
        raise ValueError("restored GP bytes do not match the server-read checksum")
    stores = recovery["recovery"]["substrates"]
    if len(stores) != 3 or {s["store"] for s in stores} != {"postgres", "redis", "gp-output"}:
        raise ValueError("recovery did not restore every GP durable store")
    for store in stores:
        if (store["original_destroyed"] is not True or store["restored_into_empty_store"] is not True
                or not store["files_before"] or store["files_before"] != store["files_after"]):
            raise ValueError("restore did not recover an empty store from the backup")
    return {"schema": "honua.gp-store-dr-candidate.v1", "candidate": pin,
            "qualification": summary}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--summary", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    pin = candidate(args.manifest)
    result = verify(pin, json.loads(args.summary.read_text())) if args.summary else pin
    args.output.write_text(json.dumps(result, indent=2) + "\n")
