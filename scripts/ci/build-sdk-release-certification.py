#!/usr/bin/env python3
"""Build a complete 33 x 3 official-SDK exact-image certification fragment."""
import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path

SCHEMA = "honua.protocol-certification-fragment/v1"

def load(path):
    return json.loads(Path(path).read_text(encoding="utf-8"))

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--results-dir", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--run-url", default="local")
    args = parser.parse_args()
    manifest = load(args.manifest)
    results_dir = Path(args.results_dir)
    generated_at = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    observed = {}
    for sdk in ("js", "python"):
        path = results_dir / f"{sdk}.json"
        if path.exists():
            for row in load(path).get("observations", []):
                observed.setdefault((sdk, row["capability"]), []).append(row)
    installs = load(results_dir / "install-results.json")
    observations = []
    for sdk, coordinate in manifest["sdks"].items():
        install = installs[sdk]
        for capability in manifest["capabilities"]:
            executed = observed.get((sdk, capability), [])
            if executed:
                result = "pass" if all(row["result"] == "pass" for row in executed) else "fail"
                operation = "; ".join(row["operation"] for row in executed)
                gap = None
            else:
                result = "fail"
                operation = f"required capability: {capability}"
                if not install["installed"]:
                    gap = f"public registry coordinate unavailable: {coordinate['package']} {coordinate['version']} from {coordinate['registry']}"
                else:
                    gap = "published SDK does not expose an executed certification probe for this required 2026.1 capability"
            payload = {
                "schema": "honua.sdk-protocol-operation-result/v1", "sdk": sdk,
                "coordinate": coordinate, "install": install, "operation": operation,
                "result": result, "gap": gap, "operation_results": executed,
                "trace": "\n".join(row.get("trace", "") for row in executed if row.get("trace")) if executed else install.get("trace"),
                "run_url": args.run_url,
            }
            digest = "sha256:" + hashlib.sha256(json.dumps(payload, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
            observations.append({
                "capability_key": capability, "surface": capability.split(".", 1)[0], "operation": operation,
                "canonical_client": coordinate["package"], "client_version": coordinate["version"],
                "deployment_target": "exact-candidate-local-docker", "client_id": sdk,
                "runner_lane": "sdk-release-certification", "protocol_version": "2026.1",
                "protocol_profile": "frozen-2026.1", "performed_by": "published SDK public API",
                "request_url": None, "exercised_capabilities": [capability] if executed else [],
                "result": result, "skip_reason": None, "gap": gap,
                "source_sha": manifest["candidate"]["sourceSha"], "producer_source_sha": manifest["candidate"]["sourceSha"],
                "image_digest": manifest["candidate"]["imageDigest"], "fixture_revision": manifest["candidate"]["fixtureRevision"],
                "contract_revision": manifest["candidate"]["contractRevision"], "auth_policy_revision": "anonymous-public-v1",
                "evidence_uri": None, "evidence_digest": digest, "evidence_receipt": payload,
                "facet_results": {"positive": {"result": result, "evidence_digest": digest}},
                "started_at": min(row["startedAt"] for row in executed) if executed else generated_at,
                "completed_at": max(row["completedAt"] for row in executed) if executed else generated_at,
            })
    fragment = {"schema": SCHEMA, "producer": "honua-server-sdk-release", "generated_at": generated_at,
        "candidate": {"source_sha": manifest["candidate"]["sourceSha"], "image_digest": manifest["candidate"]["imageDigest"]},
        "operation_scope": {"complete": len(observations) == 99, "expected": 99, "observed": len(observations)},
        "observations": observations}
    Path(args.output).write_text(json.dumps(fragment, indent=2) + "\n", encoding="utf-8")
    summary = {"schema": "honua.sdk-release-certification-report/v1", "passed": all(o["result"] == "pass" for o in observations),
        "total": len(observations), "passedCells": sum(o["result"] == "pass" for o in observations),
        "failedCells": sum(o["result"] == "fail" for o in observations), "fragment": str(args.output)}
    (Path(args.output).parent / "report.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")

if __name__ == "__main__":
    main()
