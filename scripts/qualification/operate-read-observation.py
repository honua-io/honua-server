#!/usr/bin/env python3
"""Replay the #3302 Operate read, status-contract and MCP catalog observation on a pinned image.

Boots the image through the unchanged installed harness (metadata-release-installed.py
Harness.start: isolated seeded PostGIS/Redis, Development Enterprise dev grant, no deploy targets,
alerting disabled, no operation submitted). Expected values are specified here from that fixture,
not copied from observed output. The receipt is observation evidence only; it does not qualify
proposal authorization, deployment, recovery, installed clients or placements.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import sys
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
REST_ROUTES = (
    "/api/v1/admin/version",
    "/api/v1/operate/status",
    "/api/v1/admin/observability/ops-health",
    "/api/v1/admin/observability/findings",
    "/api/v1/admin/observability/events?pageSize=5",
)
MCP_CHECKS = (
    ("honua_ops_health", {}, ["evidencePosture"]),
    ("honua_ops_findings", {}, ["evidencePosture", "findings"]),
    ("honua_operate_events", {"pageSize": 5}, ["items", "partialResult"]),
    ("honua_supported_operation_kinds", {}, ["supportedKinds"]),
)
EXPECTED_TOOLS = ["honua_ops_health", "honua_ops_findings", "honua_operate_events", "honua_supported_operation_kinds", "honua_propose_finding"]
EXPECTED_STATUS = {
    "schemaVersion": "1.1",
    "slo.configured": False,
    "slo.availability": None,
    "slo.nodeLocalRetainedTail.scope": "replica-local",
    "slo.nodeLocalRetainedTail.isPlatformSli": False,
}
DIGEST_ENCODING = "SHA-256 of UTF-8 json.dumps(descriptor, sort_keys=True, separators=(',', ':')), ensure_ascii=True"
REPLAY_PROCEDURE = [
    "Run scripts/qualification/operate-read-observation.py --manifest <platform-manifest.yaml> --output <new dir> with the pinned image pulled locally; the installed harness verifies RepoDigests and the OCI revision label before boot.",
    f"Read {len(REST_ROUTES)} REST routes with the isolated admin key. POST initialize, notifications/initialized and the four scenario tools/call messages to /mcp with Accept application/json, text/event-stream; parse data events as JSON-RPC.",
    "Assert HTTP 200, no JSON-RPC error or isError, and the listed structured fields. Independently expect no findings/events and a notConfigured alert_dispatch source without observedAt/lastSuccessfulAt; check REST and MCP separately.",
    "Compare operate status against the schema 1.1 local-only SLO contract.",
    "Page tools/list with view=full through every nextCursor, rejecting duplicate names and cursor cycles; require the five scenario tools, absence of honua_propose_operation, and required string findingId/candidateId on honua_propose_finding.",
    "Harness.finish removes lane containers and network in every outcome.",
]


def load_harness():
    spec = importlib.util.spec_from_file_location("metadata_release_installed", ROOT / "scripts/qualification/metadata-release-installed.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def unwrap(body):
    return body.get("data", body) if isinstance(body, dict) else body


def source(posture, suffix):
    return next((entry for entry in posture.get("sources", []) if entry.get("sourceId", "").endswith(suffix)), None)


class McpSession:
    def __init__(self, harness):
        self.harness, self.session, self.next_id = harness, None, 1

    def _headers(self):
        headers = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream", "X-API-Key": self.harness.password}
        if self.session:
            headers["Mcp-Session-Id"] = self.session
        return headers

    def call(self, method, params):
        body = {"jsonrpc": "2.0", "id": self.next_id, "method": method, "params": params}
        self.next_id += 1
        request = urllib.request.Request(self.harness.base + "/mcp", data=json.dumps(body).encode(), headers=self._headers(), method="POST")
        with urllib.request.urlopen(request, timeout=60) as response:
            status, raw = response.status, response.read().decode()
            self.session = response.headers.get("Mcp-Session-Id") or self.session
        messages = [json.loads(line[5:].strip()) for line in raw.splitlines() if line.startswith("data:")] or [json.loads(raw)]
        return status, next(message for message in messages if message.get("id") == body["id"])

    def notify(self, method):
        request = urllib.request.Request(self.harness.base + "/mcp", data=json.dumps({"jsonrpc": "2.0", "method": method}).encode(), headers=self._headers(), method="POST")
        try:
            urllib.request.urlopen(request, timeout=30).read()
        except urllib.error.HTTPError as error:
            if error.code not in (202, 204):
                raise


def observe(harness, receipt, failures):
    harness.start()
    receipt["imageId"] = harness.receipt["imageId"]
    rest = {route: harness.request(route, admin=True) for route in REST_ROUTES}
    receipt["restReads"] = [{"route": route, "httpStatus": 200} for route in rest]
    receipt["versionResponse"] = rest["/api/v1/admin/version"]

    mcp = McpSession(harness)
    status, init = mcp.call("initialize", {"protocolVersion": "2025-03-26", "capabilities": {}, "clientInfo": {"name": "operate-read-observation", "version": "1"}})
    assert status == 200 and "error" not in init, init
    mcp.notify("notifications/initialized")
    receipt["mcp"] = {"protocolVersion": init["result"].get("protocolVersion"), "checks": []}
    structured = {}
    for tool, arguments, fields in MCP_CHECKS:
        status, reply = mcp.call("tools/call", {"name": tool, "arguments": arguments})
        result = reply.get("result", {})
        content = result.get("structuredContent") or {}
        passed = status == 200 and "error" not in reply and not result.get("isError") and all(field in content for field in fields)
        structured[tool] = content
        receipt["mcp"]["checks"].append({"tool": tool, "expected": {"httpStatus": 200, "rpcError": False, "isError": False, "structuredFields": fields},
                                         "status": "passed" if passed else "failed"})
        if not passed:
            failures.append(f"mcp {tool}: {json.dumps(reply)[:600]}")
    receipt["mcp"]["supportedKinds"] = structured.get("honua_supported_operation_kinds", {}).get("supportedKinds")

    surfaces = {
        "rest": (unwrap(rest["/api/v1/admin/observability/findings"]), unwrap(rest["/api/v1/admin/observability/ops-health"]), unwrap(rest["/api/v1/admin/observability/events?pageSize=5"])),
        "mcp": (structured.get("honua_ops_findings", {}), structured.get("honua_ops_health", {}), structured.get("honua_operate_events", {})),
    }
    receipt["fixtureAssertions"] = {}
    for surface, (findings, health, events) in surfaces.items():
        alert = source(findings.get("evidencePosture", {}), "alert_dispatch")
        checks = {
            "findingsEmpty": findings.get("findings") == [],
            "alertSourceNotConfigured": bool(alert) and alert.get("completeness") == "notConfigured" and not alert.get("observedAt") and not alert.get("lastSuccessfulAt"),
            "eventsEmpty": events.get("items") == [] and events.get("partialResult") is False,
            "healthHasPosture": "evidencePosture" in health,
        }
        receipt["fixtureAssertions"][surface] = checks
        failures.extend(f"{surface} fixture {name}" for name, ok in checks.items() if not ok)

    status_body = unwrap(rest["/api/v1/operate/status"])
    slo = status_body.get("slo") or {}
    tail = slo.get("nodeLocalRetainedTail") or {}
    observed = {"schemaVersion": status_body.get("schemaVersion"), "slo.configured": slo.get("configured"), "slo.availability": slo.get("availability"),
                "slo.nodeLocalRetainedTail.scope": tail.get("scope"), "slo.nodeLocalRetainedTail.isPlatformSli": tail.get("isPlatformSli")}
    receipt["statusContract"] = {"expected": EXPECTED_STATUS, "observed": observed, "status": "passed" if observed == EXPECTED_STATUS else "failed", "observedSlo": slo}
    if observed != EXPECTED_STATUS:
        failures.append(f"status contract {observed}")

    pages, names, cursors, digests, cursor, propose = [], set(), set(), {}, None, {}
    while True:
        status, reply = mcp.call("tools/list", {"view": "full"} | ({"cursor": cursor} if cursor else {}))
        assert status == 200 and "error" not in reply, reply
        tools = reply["result"]["tools"]
        for descriptor in tools:
            assert descriptor["name"] not in names, "duplicate descriptor " + descriptor["name"]
            names.add(descriptor["name"])
            digests[descriptor["name"]] = hashlib.sha256(json.dumps(descriptor, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
            if descriptor["name"] == "honua_propose_finding":
                propose = descriptor
        cursor = reply["result"].get("nextCursor")
        pages.append({"count": len(tools), "nextCursor": cursor})
        if not cursor:
            break
        assert cursor not in cursors, "cursor cycle"
        cursors.add(cursor)
    schema = propose.get("inputSchema", {})
    input_ok = set(schema.get("required", [])) >= {"findingId", "candidateId"} and all(
        schema.get("properties", {}).get(name, {}).get("type") == "string" for name in ("findingId", "candidateId"))
    missing = [tool for tool in EXPECTED_TOOLS if tool not in names]
    receipt["catalog"] = {"pages": pages, "totalDescriptors": len(names), "expectedTools": EXPECTED_TOOLS, "missingTools": missing,
                          "removedOpaqueProposalToolAbsent": "honua_propose_operation" not in names,
                          "proposalInputCheck": {"required": schema.get("required"), "status": "passed" if input_ok else "failed"},
                          "digestEncoding": DIGEST_ENCODING, "descriptorDigests": {tool: digests[tool] for tool in EXPECTED_TOOLS if tool in digests}}
    if missing or not input_ok or "honua_propose_operation" in names:
        failures.append(f"catalog missing={missing} proposalInput={input_ok}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--accepted-manifest", action="store_true", help="the manifest is the release repository's accepted platform manifest")
    args = parser.parse_args()
    if not __debug__:
        parser.error("observation requires assertions; do not use Python optimization")
    installed = load_harness()
    candidate = installed.pinned_server(args.manifest)
    candidate["manifestSha256"] = hashlib.sha256(args.manifest.read_bytes()).hexdigest()
    args.output.mkdir(parents=True, exist_ok=False)
    harness = installed.Harness(candidate, args.output)
    receipt = {
        "schemaVersion": "honua.operate-read-observation/v1",
        "observedAt": datetime.now(timezone.utc).isoformat(),
        "candidate": candidate,
        "qualificationStatus": "accepted-pin-observation" if args.accepted_manifest else "not-accepted-pin",
        "scope": f"{len(REST_ROUTES)} REST reads, {len(MCP_CHECKS)} MCP observation calls, full-view catalog discovery and the status contract; not installed CLI, DevOps stdio, Console, proposal authorization, source outage, deployment or recovery qualification.",
        "fixture": {
            "harness": "scripts/qualification/metadata-release-installed.py",
            "harnessSha256": harness.harness_hash,
            "replayScript": "scripts/qualification/operate-read-observation.py",
            "replayScriptSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
            "setup": "Harness.start only; isolated seeded PostGIS/Redis, Development Enterprise dev grant, no deploy targets, alerting disabled; no operation submitted",
        },
        "replayProcedure": REPLAY_PROCEDURE,
    }
    failures = []
    try:
        observe(harness, receipt, failures)
    except Exception as error:
        failures.append(f"{type(error).__name__}: {str(error).replace(harness.password, '[redacted]')[:1500]}")
    finally:
        try:
            harness.finish()
        finally:
            receipt["cleanup"] = harness.receipt.get("cleanup")
            receipt["status"] = "failed" if failures else "passed"
            receipt["failures"] = failures
            (args.output / "observation.json").write_text(json.dumps(receipt, indent=2) + "\n")
    print(receipt["status"], failures)
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    main()
