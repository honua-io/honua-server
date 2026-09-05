"""Opt-in local telemetry outage proof using native Windows tools and Docker Desktop.

The caller supplies an isolated Honua server and its disposable Postgres/Redis containers.
Only the alert-dispatch relation is interrupted; the process, authentication, and MCP stay up.
"""

import argparse
import datetime as dt
import json
import hashlib
import os
import re
from pathlib import Path
import subprocess
import time
import urllib.request


SOURCE = "honua_ops_findings.alert_dispatch"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default="http://127.0.0.1:18475")
    parser.add_argument("--postgres-container", default="honua-3475-postgres")
    parser.add_argument("--redis-container", default="honua-3475-redis")
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--server-assembly", type=Path, required=True)
    parser.add_argument("--receipt", type=Path, required=True)
    parser.add_argument("--allow-isolated-outage", action="store_true", required=True)
    args = parser.parse_args()
    if os.name != "nt":
        raise RuntimeError("This harness requires the native Windows host.")
    if not args.base_url.startswith("http://127.0.0.1:"):
        raise ValueError("The disposable harness must listen on IPv4 loopback.")
    if not re.fullmatch(r"[0-9a-f]{40}", args.source_sha):
        raise ValueError("source-sha must be an exact 40-character revision.")
    api_key = os.environ["HONUA_LIVE_EVIDENCE_API_KEY"]
    receipt = {"schemaVersion": "1.0", "sourceSha": args.source_sha,
               "execution": "windows-native-dotnet/docker-desktop-postgres-redis",
               "candidateQualification": False, "sourceId": SOURCE, "passed": False}
    receipt["serverAssemblySha256"] = hashlib.sha256(args.server_assembly.read_bytes()).hexdigest()

    def docker(*arguments):
        return subprocess.run(["docker", *arguments], check=True, capture_output=True,
                              text=True, timeout=30).stdout.strip()

    def sql(statement):
        return docker("exec", args.postgres_container, "psql", "-U", "honua", "-d",
                      "honua_evidence", "-v", "ON_ERROR_STOP=1", "-At", "-c", statement)

    def request(path, body=None, method="POST"):
        headers = {"X-API-Key": api_key, "Content-Type": "application/json"}
        data = json.dumps(body).encode() if body is not None else None
        req = urllib.request.Request(args.base_url + path, data=data, headers=headers,
                                     method=method)
        with urllib.request.urlopen(req, timeout=30) as response:
            assert response.status == 200
            return json.load(response)

    def findings():
        response = request("/mcp", {"jsonrpc": "2.0", "id": 3475, "method": "tools/call",
            "params": {"name": "honua_ops_findings", "arguments": {}}})
        assert "error" not in response, response
        assert not response["result"].get("isError"), response
        return response["result"]["structuredContent"]

    def source(document):
        return next(s for s in document["evidencePosture"]["sources"] if s["sourceId"] == SOURCE)

    def wait_for(status):
        deadline = time.monotonic() + 120
        while time.monotonic() < deadline:
            document = findings()
            if source(document)["completeness"] == status and any(
                    f["rule"] == "alert-dispatch-backlog" for f in document["findings"]):
                return document
            time.sleep(2)
        raise TimeoutError("The expected telemetry/finding state was not observed.")

    def fresh(document):
        assert document["evidencePosture"]["status"] == "complete"
        observation = source(document)
        assert observation["backendKind"] == "durableStore"
        assert observation["backendId"] == "alert-dispatch-store"
        now = dt.datetime.now(dt.timezone.utc)
        assert now - dt.timedelta(seconds=observation["maximumAgeSeconds"]) <= timestamp(observation["observedAt"]) <= now
        assert timestamp(observation["lastSuccessfulAt"]) <= now < timestamp(observation["validUntil"])

    def timestamp(value):
        return dt.datetime.fromisoformat(value)

    def proposals():
        return docker("exec", args.redis_container, "redis-cli", "--raw", "SMEMBERS",
                      "controlplane:proposal:active").splitlines()

    interrupted = False
    try:
        # Exactly one known failed dispatch, with no external destination and no network delivery.
        sql("WITH event AS (INSERT INTO honua.alert_events(dedupe_key,service_id,layer_id,objectid,"
            "trigger_type,generation,severity) VALUES('evidence-3475','evidence',0,1,1,0,'warning') "
            "ON CONFLICT(dedupe_key) DO UPDATE SET dedupe_key=excluded.dedupe_key RETURNING event_id) "
            "INSERT INTO honua.alert_dispatch(event_id,channel_type,status,attempts) "
            "SELECT event_id,1,4,5 FROM event WHERE NOT EXISTS(SELECT 1 FROM honua.alert_dispatch d "
            "WHERE d.event_id=event.event_id)")
        initial = wait_for("complete")
        fresh(initial)
        assert sql("SELECT count(*) FROM honua.alert_dispatch WHERE status=4 AND attempts=5") == "1"
        assert sql("SELECT count(*) FROM honua.alert_dispatch") == "1"
        receipt["expectedBacklog"] = {"pending": 0, "deadLettered": 1, "attempts": 5}
        finding = next(f for f in initial["findings"] if f["rule"] == "alert-dispatch-backlog")
        assert SOURCE in finding["requiredSourceIds"]
        receipt["initial"] = initial
        before_proposals = proposals()
        before_rows = sql("SELECT dispatch_id,status,attempts,updated_at FROM honua.alert_dispatch ORDER BY dispatch_id")
        outage_at = dt.datetime.now(dt.timezone.utc)
        sql("ALTER TABLE honua.alert_dispatch RENAME TO alert_dispatch_evidence_outage")
        interrupted = True
        unavailable = wait_for("unavailable")
        receipt["unavailable"] = unavailable
        assert unavailable["evidencePosture"]["status"] == "unavailable"
        assert "sourceUnavailable" in source(unavailable)["reasonCodes"]
        rest_findings = request("/api/v1/admin/observability/findings", method="GET")
        assert source(rest_findings) == source(unavailable)
        health = request("/api/v1/admin/observability/ops-health", method="GET")
        health_source = next(s for s in health["evidencePosture"]["sources"]
                             if s["sourceId"] == "honua_ops_health.alert_dispatch")
        assert health_source["completeness"] == "unavailable"
        assert health_source["observedAt"] == source(unavailable)["observedAt"]
        assert health_source["lastSuccessfulAt"] == source(unavailable)["lastSuccessfulAt"]
        receipt["restMcpSourceParity"] = True
        receipt["unavailableHealthSource"] = health_source
        proposal = request("/api/v1/admin/observability/findings/" + finding["id"] + "/propose")
        receipt["proposal"] = proposal
        assert proposal["status"] == "Blocked", proposal
        assert proposal["message"] == "evidencePostureNotActionable", proposal
        assert not proposal.get("proposalId") and not proposal.get("executionOperationId")
        assert proposals() == before_proposals
        assert sql("SELECT dispatch_id,status,attempts,updated_at FROM honua.alert_dispatch_evidence_outage ORDER BY dispatch_id") == before_rows
        receipt["zeroNewProposals"] = True
        receipt["unchangedDispatchRows"] = True
        # An unavailable backend cannot have successfully collected a newer observation.
        assert timestamp(source(unavailable)["observedAt"]) <= outage_at
        assert timestamp(source(unavailable)["lastSuccessfulAt"]) <= outage_at
        sql("ALTER TABLE honua.alert_dispatch_evidence_outage RENAME TO alert_dispatch")
        interrupted = False
        recovered = wait_for("complete")
        fresh(recovered)
        receipt["recovered"] = recovered
        assert timestamp(source(recovered)["observedAt"]) > outage_at
        receipt["passed"] = True
    finally:
        if interrupted:
            sql("ALTER TABLE honua.alert_dispatch_evidence_outage RENAME TO alert_dispatch")
        args.receipt.parent.mkdir(parents=True, exist_ok=True)
        args.receipt.write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
