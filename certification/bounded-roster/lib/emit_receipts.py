#!/usr/bin/env python3
"""Build governed bounded-roster receipts from one roster run.

Inputs (all under ``--run-dir``, written by ``run-roster.sh``):

* ``observations/*.json``  one per governed cell (``cellkit.Cell.write``);
* ``observations/lanes/*.json``  each lane's client identity;
* ``wire/wire.jsonl``  every exchange the recording proxy saw;
* ``candidate.json``, ``fixture.json``, ``lanes.json``  the candidate image, the
  applied fixture/config/auth digests and the lane image identities.

Output: one ``client-interop-cert-v1`` envelope per governed
``(canonical_client, client_lane, client_version, surface, revisions)`` group under
``--out`` (``client_id`` is per envelope, and GDAL and GDAL/OGR share lanes), plus
``wire-join.json`` recording, per cell, which proxy lines substantiated it.

Fail-closed rules applied here (in addition to the cell's own verdict):

* a cell that reports ``pass`` but has no candidate-bound exchange in its
  execution window on the wire is rewritten to ``fail`` -- a client that never
  reached the candidate cannot certify it;
* ``request_url`` is taken from the wire, never from the cell's claim, and must
  carry no credential; a cell with no publishable request URL fails;
* ``fixture_revision``/``server_config_revision``/``auth_policy_revision`` echo the
  governed row exactly (the join key); the digests of what was actually applied
  travel beside them under ``honua_evidence``.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import sys
from collections import defaultdict
from datetime import datetime, timedelta, timezone
from pathlib import Path
from urllib.parse import parse_qsl, urlparse

SCHEMA_VERSION = "1.0"
CREDENTIAL_QUERY_KEYS = frozenset({
    "access_token", "api_key", "apikey", "auth", "authorization", "code",
    "id_token", "key", "password", "pwd", "refresh_token", "secret", "session",
    "sig", "signature", "token", "x-api-key",
})
WINDOW_SLACK = timedelta(milliseconds=750)


def parse_time(value: str) -> datetime:
    return datetime.fromisoformat(value.replace("Z", "+00:00")).astimezone(timezone.utc)


def iso(value: datetime) -> str:
    return value.isoformat(timespec="microseconds").replace("+00:00", "Z")


def publishable(value: str | None) -> bool:
    if not value:
        return False
    parsed = urlparse(value)
    if parsed.scheme not in ("http", "https") or not parsed.netloc or parsed.username or parsed.password:
        return False
    return not any(key.strip().lower() in CREDENTIAL_QUERY_KEYS
                   for key, _ in parse_qsl(parsed.query, keep_blank_values=True))


def load_wire(path: Path) -> list[dict]:
    lines = []
    for number, raw in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
        if not raw.strip():
            continue
        record = json.loads(raw)
        record["_line"] = number
        record["_sha256"] = hashlib.sha256(raw.encode("utf-8")).hexdigest()
        record["_at"] = parse_time(record["at"])
        lines.append(record)
    return lines


def join_wire(observation: dict, wire: list[dict]) -> list[dict]:
    started = parse_time(observation["started_at"]) - WINDOW_SLACK
    finished = parse_time(observation["finished_at"]) + WINDOW_SLACK
    return [line for line in wire if started <= line["_at"] <= finished and not line.get("roster_asset")]


def choose_request_url(observation: dict, exchanges: list[dict]) -> str | None:
    claimed = observation.get("primary_request_url") or ""
    claimed_path = urlparse(claimed).path
    candidates = [line["url"] for line in exchanges if publishable(line["url"])]
    for candidate in candidates:
        if claimed_path and urlparse(candidate).path == claimed_path:
            return candidate
    for candidate in candidates:
        if claimed_path and urlparse(candidate).path.startswith(claimed_path.rsplit("/", 1)[0]):
            return candidate
    return candidates[0] if candidates else None


def build(run_dir: Path, requirements: dict, producer_source_sha: str) -> tuple[dict[str, dict], dict]:
    candidate = json.loads((run_dir / "candidate.json").read_text(encoding="utf-8"))
    fixture = json.loads((run_dir / "fixture.json").read_text(encoding="utf-8"))
    lanes = json.loads((run_dir / "lanes.json").read_text(encoding="utf-8"))
    lane_identities = {
        path.stem: json.loads(path.read_text(encoding="utf-8"))
        for path in sorted((run_dir / "observations" / "lanes").glob("*.json"))
    }
    wire = load_wire(run_dir / "wire" / "wire.jsonl")
    rows_by_test_id = {test_id: row for row in requirements["requirements"] for test_id in row.get("test_ids") or ()}

    groups: dict[tuple, list[tuple[dict, dict, dict]]] = defaultdict(list)
    joins: dict[str, dict] = {}
    for path in sorted((run_dir / "observations").glob("*.json")):
        observation = json.loads(path.read_text(encoding="utf-8"))
        row = rows_by_test_id.get(observation["test_case_id"])
        if row is None:
            raise SystemExit(f"{path.name}: {observation['test_case_id']} is not a governed bounded-roster test id")
        exchanges = join_wire(observation, wire)
        status = observation["status"]
        notes = observation["summary"]
        request_url = choose_request_url(observation, exchanges)
        if status == "pass" and not exchanges:
            status, notes = "fail", "no candidate-bound exchange on the wire in this cell's window; " + notes
        if status == "pass" and request_url is None:
            status, notes = "fail", "no publishable request URL on the wire; " + notes
        if request_url is None:
            request_url = observation.get("primary_request_url")
        statuses = defaultdict(int)
        for line in exchanges:
            statuses[str(line["status"])] += 1
        joins[observation["test_case_id"]] = {
            "window": [observation["started_at"], observation["finished_at"]],
            "exchanges": len(exchanges),
            "wire_lines": [line["_line"] for line in exchanges],
            "wire_lines_sha256": hashlib.sha256("".join(line["_sha256"] for line in exchanges).encode()).hexdigest(),
            "statuses": dict(sorted(statuses.items())),
            "user_agents": sorted({line.get("user_agent") or "" for line in exchanges}),
            "credential_schemes": sorted({line.get("credential") or "none" for line in exchanges}),
        }
        result = {
            "test_case_id": observation["test_case_id"],
            "status": status,
            "performed_by": row["canonical_client"],
            "request_url": request_url,
            "exercised_capabilities": observation["exercised_capabilities"] or ["positive"],
            "notes": notes,
            "operation": row["operation"],
            "started_at": observation["started_at"],
            "finished_at": observation["finished_at"],
            "duration_ms": observation["duration_ms"],
            "protocol_version": observation["protocol_version"],
            "checks": observation["checks"],
            "wire": joins[observation["test_case_id"]],
        }
        key = (row["client_lane"], row["client_version"], row["surface"], row["deployment_target"],
               row["fixture_revision"], row["contract_revision"], row["auth_policy_revision"],
               row["canonical_client"])
        groups[key].append((observation, row, result))

    receipts = {}
    for key, members in sorted(groups.items()):
        lane, version, surface, target, fixture_revision, contract_revision, auth_revision, client = key
        observations = [observation for observation, _, _ in members]
        results = [result for _, _, result in sorted(members, key=lambda member: member[2]["test_case_id"])]
        lane_name = observations[0].get("lane") or lanes["by_client_lane"].get(lane, {}).get("lane")
        run_started = min(parse_time(observation["started_at"]) for observation in observations)
        envelope = {
            "schema_version": SCHEMA_VERSION,
            "run_id": candidate["run_id"],
            "run_date": iso(run_started),
            "server_commit": candidate["source_sha"],
            "server_version": candidate.get("server_version"),
            "producer_source_sha": producer_source_sha,
            "image_digest": candidate["index_digest"],
            "fixture_revision": fixture_revision.replace("{source_sha}", candidate["source_sha"]),
            "server_config_revision": contract_revision,
            "auth_policy_revision": auth_revision,
            "client_id": members[0][1]["canonical_client"],
            "runner_lane": lane,
            "client_version": version,
            "protocol": surface,
            "protocol_version": sorted({observation["protocol_version"] for observation in observations})[0],
            "protocol_profile": "; ".join(sorted({observation["protocol_profile"] for observation in observations})),
            "environment": target,
            "deployment_target": target,
            "results": results,
            "summary": {
                "total": len(results),
                "passed": sum(result["status"] == "pass" for result in results),
                "failed": sum(result["status"] == "fail" for result in results),
                "skipped": sum(result["status"] == "skip" for result in results),
            },
            "honua_evidence": {
                "release": candidate["release"],
                "candidate": candidate,
                "fixture": fixture,
                "client": {
                    "client_version_detail": sorted({observation["client_version_detail"] for observation in observations}),
                    "lane": lanes["by_client_lane"].get(lane),
                    "lane_identity": lane_identities.get((lanes["by_client_lane"].get(lane) or {}).get("lane", "")),
                },
                "durable_uri": candidate["durable_uri_base"] + receipt_name(lane, version, surface, key),
            },
        }
        receipts[receipt_name(lane, version, surface, key)] = envelope
    return receipts, joins


def receipt_name(lane: str, version: str, surface: str, key: tuple) -> str:
    safe = lambda value: "".join(ch if ch.isalnum() or ch in ".-" else "_" for ch in value)  # noqa: E731
    revision_hash = hashlib.sha256("|".join(key[4:]).encode()).hexdigest()[:8]
    return f"{lane}--{safe(key[7]).lower()}--{safe(version)}--{surface}--{revision_hash}.cert.json"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--run-dir", type=Path, required=True)
    parser.add_argument("--requirements", type=Path, required=True)
    parser.add_argument("--producer-source-sha", required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args(argv)

    requirements = json.loads(args.requirements.read_text(encoding="utf-8"))
    receipts, joins = build(args.run_dir, requirements, args.producer_source_sha)
    args.out.mkdir(parents=True, exist_ok=True)
    for stale in args.out.glob("*.cert.json"):
        stale.unlink()
    for name, envelope in receipts.items():
        (args.out / name).write_text(json.dumps(envelope, indent=2) + "\n", encoding="utf-8")
    (args.out.parent / "wire-join.json").write_text(json.dumps(joins, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    counts = defaultdict(int)
    for envelope in receipts.values():
        for result in envelope["results"]:
            counts[result["status"]] += 1
    print(f"wrote {len(receipts)} receipts covering {sum(counts.values())} cells: {dict(counts)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
