#!/usr/bin/env python3
"""Project sdk-js transport evidence into the server release qualification contract."""

from __future__ import annotations

import argparse
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

TRANSPORTS = ("sse", "websocket", "odata")
SINGLE_NODE_SCENARIOS = {
    "baseline-completion": {"baseline-completion", "snapshot-delta-contract"},
    "ordering": {"ordering", "ordered-delivery", "snapshot-delta-contract"},
    "duplicate-behavior": {"duplicate-behavior", "transport-duplicate"},
    "resume-gap-detection": {"resume-gap-detection", "sequence-gap", "transport-gap"},
    "reconnect-under-partition": {"reconnect-under-partition", "transport-partition"},
    "token-expiry": {"token-expiry", "credential-expiry"},
}
MULTI_NODE_SCENARIOS = (
    "ha-failover",
    "backpressure",
    "scale-proxy",
    "redis-failover-snapshot",
    "sink-outage-recovery",
    "tenant-isolation",
    "soak-24-72h",
)


def qualify(evidence: dict, expected_revision: str) -> dict:
    actual_revision = evidence.get("server", {}).get("revision")
    if actual_revision != expected_revision:
        raise ValueError(
            f"evidence server revision {actual_revision!r} does not match exact candidate {expected_revision!r}"
        )

    by_transport = {item.get("id"): item for item in evidence.get("transports", [])}
    if set(by_transport) != set(TRANSPORTS):
        raise ValueError("evidence must contain exactly sse, websocket, and odata transport verdicts")

    transports = []
    for transport_id in TRANSPORTS:
        source = by_transport[transport_id]
        observed = {
            item.get("id"): item.get("result")
            for item in source.get("scenarios", [])
            if isinstance(item, dict)
        }
        scenarios = []
        for scenario_id, aliases in SINGLE_NODE_SCENARIOS.items():
            matches = [observed[name] for name in aliases if name in observed]
            if any(result == "failed" for result in matches):
                state = "failed"
                reason = "candidate evidence recorded a failed scenario"
            elif any(result == "passed" for result in matches):
                state = "qualified"
                reason = None
            else:
                state = "not-yet-qualified"
                reason = "exact-candidate evidence does not contain this scenario"
            scenarios.append({"id": scenario_id, "state": state, "reason": reason})
        transports.append({"id": transport_id, "scenarios": scenarios})

    single_node_qualified = all(
        scenario["state"] == "qualified"
        for transport in transports
        for scenario in transport["scenarios"]
    )
    return {
        "format": "honua.realtime-qualification.v1",
        "generatedAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "candidateRevision": expected_revision,
        "sdkRevision": evidence.get("sdk", {}).get("revision"),
        "status": "not-yet-qualified",
        "singleNodeStatus": "qualified" if single_node_qualified else "not-yet-qualified",
        "transports": transports,
        "multiNode": [
            {
                "id": scenario_id,
                "state": "not-yet-qualified",
                "reason": "requires an explicit multi-node candidate run; no silent skip is permitted",
            }
            for scenario_id in MULTI_NODE_SCENARIOS
        ],
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--evidence", required=True, type=Path)
    parser.add_argument("--candidate-revision", required=True)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--require-qualified", action="store_true")
    args = parser.parse_args()

    try:
        receipt = qualify(json.loads(args.evidence.read_text(encoding="utf-8")), args.candidate_revision)
    except (OSError, json.JSONDecodeError, ValueError) as exc:
        print(f"realtime qualification refused: {exc}", file=sys.stderr)
        return 2

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print(f"realtime qualification: {receipt['status']} ({args.output})")
    return 1 if args.require_qualified and receipt["status"] != "qualified" else 0


if __name__ == "__main__":
    raise SystemExit(main())
