#!/usr/bin/env python3
# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""Replay the terminal dashboard composition segment against a running candidate.

This is the candidate-backed half of honua-server#3429. The in-process integration fixture
(`StudioDashboardMcpIntegrationTests`) proves composition values and independently computed
content hashes; this driver proves the same lifecycle on a deployed image under the Production
startup policy, where the durable audit log, operation runtime, tenant resolution and OAuth
bearer validation are the real ones rather than Test-environment substitutes.

It drives exactly the segment the terminal release journey names for stage 6/7
("create/mutate/validate/version map+dashboard; restart; reopen; compare content identity" and
"submit publication; require a durable AwaitingApproval proposal"), through the same MCP tools a
terminal client calls, and records every criterion as an observed row:

* every row carries the evidence it was judged on, and a row that could not be observed is
  `fail`, never omitted;
* the receipt is `pass` only when every row passes;
* the driver never edits the database; SQL is read-only and used only to join what the server
  returned with what it durably recorded.

Only the Python standard library is used so the driver runs unchanged on a release runner.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import hmac
import json
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable

# A bearer source mints a fresh access token per request: deployments enable OAuth token replay
# protection by default, so one token authorizes exactly one request.
Bearer = Callable[[], str]

RECEIPT_FORMAT = "honua.studio.dashboard-lifecycle-receipt.v1"
ELEVEN_VERBS = (
    "add_layer",
    "set_layer_style",
    "set_layer_visibility",
    "set_view",
    "add_widget",
    "bind_interaction",
    "add_control",
    "remove_control",
    "remove_interaction",
    "remove_widget",
    "remove_layer",
)
CROSS_USER_DENIED = "studio_authorization/cross_user_denied"
SCOPE_DENIAL_CODES = ("studio_authorization/oauth_scope_required", "studio_authorization/insufficient_scope")
# Fields whose presence in a denial would disclose draft state to a caller who may not see it.
DISCLOSING_FIELDS = ("envelope", "generation", "currentGeneration", "ownerId", "draftId", "body", "validation")


class ReceiptRow:
    """One observed acceptance criterion."""

    def __init__(self, row_id: str, criterion: str) -> None:
        self.row_id = row_id
        self.criterion = criterion
        self.failures: list[str] = []
        self.evidence: dict[str, Any] = {}

    def check(self, condition: bool, message: str) -> bool:
        if not condition:
            self.failures.append(message)
        return condition

    def to_json(self) -> dict[str, Any]:
        return {
            "id": self.row_id,
            "criterion": self.criterion,
            "status": "pass" if not self.failures else "fail",
            "failures": self.failures,
            "evidence": self.evidence,
        }


def denial_disclosure(structured: dict[str, Any] | None) -> list[str]:
    """Return the draft-state fields a denial payload discloses (empty when it discloses none)."""
    if not isinstance(structured, dict):
        return []
    return sorted(field for field in DISCLOSING_FIELDS if field in structured)


def build_receipt(rows: list[ReceiptRow], candidate: dict[str, Any], started: datetime) -> dict[str, Any]:
    """Assemble the receipt; the verdict is pass only when every row passed."""
    rendered = [row.to_json() for row in rows]
    return {
        "format": RECEIPT_FORMAT,
        "issue": "https://github.com/honua-io/honua-server/issues/3429",
        "candidate": candidate,
        "startedAt": started.isoformat(),
        "completedAt": datetime.now(timezone.utc).isoformat(),
        "verdict": "pass" if rendered and all(row["status"] == "pass" for row in rendered) else "fail",
        "rows": rendered,
    }


def mint_token(key: str, issuer: str, audience: str, claims: dict[str, Any], lifetime: int = 1800) -> str:
    """Mint an HS256 access token for a deployment configured with a static signing key."""
    now = int(time.time())
    payload = dict(claims)
    payload.update({"iss": issuer, "aud": audience, "iat": now, "nbf": now - 5, "exp": now + lifetime,
                    "jti": uuid.uuid4().hex})

    def encode(value: dict[str, Any]) -> str:
        raw = json.dumps(value, separators=(",", ":")).encode()
        return base64.urlsafe_b64encode(raw).rstrip(b"=").decode()

    signing_input = f"{encode({'alg': 'HS256', 'typ': 'JWT'})}.{encode(payload)}"
    signature = hmac.new(key.encode(), signing_input.encode(), hashlib.sha256).digest()
    return f"{signing_input}.{base64.urlsafe_b64encode(signature).rstrip(b'=').decode()}"


class Server:
    """Minimal HTTP/MCP client over urllib."""

    def __init__(self, base_url: str) -> None:
        self.base_url = base_url.rstrip("/")

    def request(self, method: str, path: str, headers: dict[str, str], body: Any = None) -> tuple[int, Any, dict[str, str]]:
        headers = dict(headers)
        headers.setdefault("traceparent", f"00-{uuid.uuid4().hex}-{uuid.uuid4().hex[:16]}-01")
        data = None if body is None else json.dumps(body).encode()
        request = urllib.request.Request(self.base_url + path, data=data, method=method)
        request.add_header("Content-Type", "application/json")
        request.add_header("Accept", "application/json, text/event-stream")
        for name, value in headers.items():
            request.add_header(name, value)
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                return response.status, _decode(response.read()), dict(response.headers)
        except urllib.error.HTTPError as error:
            return error.code, _decode(error.read()), dict(error.headers)

    def tool(self, bearer: Bearer, verb: str, arguments: dict[str, Any]) -> dict[str, Any]:
        """Call honua_studio_<verb>; returns {isError, structured, http, traceId}."""
        trace_id = uuid.uuid4().hex
        status, payload, _ = self.request("POST", "/mcp", {
            "Authorization": f"Bearer {bearer()}",
            "traceparent": f"00-{trace_id}-{uuid.uuid4().hex[:16]}-01",
        }, {
            "jsonrpc": "2.0", "id": 1, "method": "tools/call",
            "params": {"name": f"honua_studio_{verb}", "arguments": arguments},
        })
        result = payload.get("result") if isinstance(payload, dict) else None
        if not isinstance(result, dict):
            return {"isError": True, "structured": {"transport": payload}, "http": status, "traceId": trace_id}
        return {"isError": bool(result.get("isError")), "structured": result.get("structuredContent"),
                "http": status, "traceId": trace_id}

    def wait_ready(self, bearer: Bearer, timeout: float) -> bool:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            try:
                status, payload, _ = self.request("POST", "/mcp", {"Authorization": f"Bearer {bearer()}"},
                                                  {"jsonrpc": "2.0", "id": 1, "method": "tools/list"})
                if status == 200 and isinstance(payload, dict) and "result" in payload:
                    return True
            except (urllib.error.URLError, ConnectionError, TimeoutError):
                pass
            time.sleep(3)
        return False


def _decode(raw: bytes) -> Any:
    text = raw.decode("utf-8", errors="replace").strip()
    if not text:
        return None
    if text.startswith("event:") or text.startswith("data:"):
        text = "\n".join(line[5:].strip() for line in text.splitlines() if line.startswith("data:"))
    try:
        return json.loads(text)
    except ValueError:
        return {"raw": text[:500]}


class Database:
    """Read-only SQL through a psql command prefix (for example `docker exec pg psql -U honua -d honua`)."""

    def __init__(self, psql: list[str]) -> None:
        self.psql = psql

    def rows(self, sql: str) -> list[dict[str, Any]]:
        wrapped = f"SELECT coalesce(json_agg(q), '[]'::json) FROM ({sql}) q"
        output = subprocess.run([*self.psql, "-At", "-v", "ON_ERROR_STOP=1", "-c", wrapped],
                                check=True, capture_output=True, text=True).stdout.strip()
        return json.loads(output or "[]")


def quote(value: str) -> str:
    return "'" + value.replace("'", "''") + "'"


def run(args: argparse.Namespace) -> dict[str, Any]:
    started = datetime.now(timezone.utc)
    server = Server(args.base_url)
    db = Database(args.psql.split())
    admin = {"X-API-Key": args.admin_key}
    run_id = uuid.uuid4().hex[:8]

    def token(subject: str, tenant: str, scope: str) -> Bearer:
        claims = {"sub": subject, "tid": tenant, "roles": [args.role], "scope": scope}
        return lambda: mint_token(args.jwt_key, args.jwt_issuer, args.jwt_audience, claims)

    owner_subject = f"dashboard-owner-{run_id}"
    alice = token(owner_subject, args.tenant, "honua.mcp.full")
    bob = token(f"dashboard-other-{run_id}", args.tenant, "honua.mcp.full")
    alice_other_tenant = token(owner_subject, args.other_tenant, "honua.mcp.full")
    alice_read_only = token(owner_subject, args.tenant, "honua.mcp.read")

    rows: list[ReceiptRow] = []
    mutations: list[tuple[str, dict[str, Any]]] = []

    # Operator provisioning through the product surface: a role holding the StudioDraft grants.
    status, role, _ = server.request("POST", "/api/v1/admin/roles", admin,
                                     {"name": f"{args.role}", "description": "honua-server#3429 dashboard receipt"})
    if status == 201:
        role_id = role["data"]["roleId"]
    else:
        _, listed, _ = server.request("GET", "/api/v1/admin/roles", admin)
        role_id = next(r["roleId"] for r in listed["data"] if r["name"] == args.role)
    server.request("PUT", f"/api/v1/admin/roles/{role_id}/permissions", admin,
                   {"permissions": [{"service": "StudioDraft", "layer": "*", "operation": "*"}]})

    compose = ReceiptRow("dashboard.compose.eleven-verbs",
                         "A terminal MCP client creates a dashboard and all eleven composition verbs succeed with the map/app contract.")
    rows.append(compose)
    created = server.tool(alice, "create_draft", {"packageKey": f"dashboard-receipt-{run_id}", "family": "dashboard",
                                                  "schemaVersion": "1.0"})
    structured = created["structured"] or {}
    draft_id = structured.get("draftId")
    compose.check(not created["isError"] and draft_id is not None, f"create_draft failed: {structured}")
    if draft_id is None:
        return build_receipt(rows, candidate_identity(args), started)
    mutations.append(("create_draft", created))
    generation = structured.get("generation", 1)
    owner_id = structured.get("ownerId")
    compose.evidence.update({"draftId": draft_id, "ownerId": owner_id, "family": structured.get("family")})

    verb_arguments = {
        "add_layer": {"layer": {"id": "parcels", "type": "fill"}},
        "set_layer_style": {"layerId": "parcels", "styleRef": "night"},
        "set_layer_visibility": {"layerId": "parcels", "visible": False},
        "set_view": {"view": {"center": [-157.86, 21.31], "zoom": 10}},
        "add_widget": {"widget": {"id": "legend", "kind": "legend"}},
        "bind_interaction": {"interaction": {"id": "select", "on": {"ref": "layer:parcels", "event": "featureSelect"},
                                             "do": {"ref": "map", "verb": "setViewport"}}},
        "add_control": {"control": {"id": "scale", "kind": "navigation"}},
    }

    def mutate(token_value: Bearer, verb: str, fields: dict[str, Any]) -> dict[str, Any]:
        return server.tool(token_value, verb, {"draftId": draft_id, "generation": generation, **fields})

    for verb in ELEVEN_VERBS[:7]:
        call = mutate(alice, verb, verb_arguments[verb])
        if compose.check(not call["isError"] and (call["structured"] or {}).get("generation") == generation + 1,
                         f"{verb} did not advance generation {generation}: {call['structured']}"):
            generation += 1
            mutations.append((verb, call))
    body = (server.tool(alice, "get_draft", {"draftId": draft_id})["structured"] or {}).get("envelope", {}).get("body", {})
    compose.check(body.get("layers", [{}])[0].get("styleRef") == "night"
                  and body.get("layers", [{}])[0].get("visible") is False
                  and body.get("view", {}).get("center") == [-157.86, 21.31]
                  and body.get("widgets", [{}])[0].get("kind") == "legend"
                  and body.get("interactions", [{}])[0].get("do", {}).get("verb") == "setViewport"
                  and body.get("controls", [{}])[0].get("kind") == "navigation",
                  f"composed body does not carry the literal values written: {body}")

    cas = ReceiptRow("dashboard.cas.stale-retry",
                     "Stale generation returns the canonical conflict and current generation; a re-read non-conflicting retry succeeds exactly once; a conflicting retry stays failed.")
    rows.append(cas)
    stale = server.tool(alice, "remove_control", {"draftId": draft_id, "generation": 1, "controlId": "scale"})
    stale_error = stale["structured"] or {}
    cas.check(stale["isError"] and stale_error.get("code") == "failed_precondition"
              and stale_error.get("currentGeneration") == generation,
              f"stale write did not return failed_precondition/currentGeneration={generation}: {stale_error}")
    reread = server.tool(alice, "get_draft", {"draftId": draft_id})["structured"] or {}
    cas.check(reread.get("generation") == generation
              and any(c.get("id") == "scale" for c in reread.get("envelope", {}).get("body", {}).get("controls", [])),
              "re-read after the stale write does not show the unchanged target")
    conflicting = mutate(alice, "remove_layer", {"layerId": "parcels"})
    cas.check(conflicting["isError"] and (conflicting["structured"] or {}).get("code") == "invalid_argument",
              f"removing an interaction-referenced layer was not rejected: {conflicting['structured']}")
    retry_generation = generation
    retried = mutate(alice, "remove_control", {"controlId": "scale"})
    if cas.check(not retried["isError"] and (retried["structured"] or {}).get("generation") == generation + 1,
                 f"non-conflicting retry failed: {retried['structured']}"):
        generation += 1
        mutations.append(("remove_control", retried))
    duplicate = server.tool(alice, "remove_control", {"draftId": draft_id, "generation": retry_generation, "controlId": "scale"})
    cas.check(duplicate["isError"] and (duplicate["structured"] or {}).get("code") == "failed_precondition",
              f"duplicate retry was applied twice: {duplicate['structured']}")
    for verb, fields in (("remove_interaction", {"interactionId": "select"}), ("remove_widget", {"widgetId": "legend"}),
                         ("remove_layer", {"layerId": "parcels"})):
        call = mutate(alice, verb, fields)
        if compose.check(not call["isError"], f"{verb} failed: {call['structured']}"):
            generation += 1
            mutations.append((verb, call))
    compose.check(generation == 12, f"eleven verbs plus create should end at generation 12, observed {generation}")
    compose.evidence["verbsApplied"] = [verb for verb, _ in mutations if verb in ELEVEN_VERBS]

    whole = ReceiptRow("dashboard.update-draft.shared-validator",
                       "update_draft accepts a valid dashboard document and rejects malformed or unsupported content without mutation.")
    rows.append(whole)
    package_key = f"dashboard-receipt-{run_id}"
    valid = mutate(alice, "update_draft", {"packageKey": package_key, "schemaVersion": "1.0",
                                           "body": {"layers": [{"id": "roads"}], "view": {"center": [-158, 22], "zoom": 7}}})
    if whole.check(not valid["isError"], f"valid whole document rejected: {valid['structured']}"):
        generation += 1
        mutations.append(("update_draft", valid))
    rejected = []
    for malformed in ({"layers": "bad"}, {"layers": [{"id": "same"}, {"id": "same"}]}, {"widgets": [{"id": "missing-kind"}]},
                      {"view": {"center": [1]}}, {"view": {"zoom": 25}}):
        call = mutate(alice, "update_draft", {"packageKey": package_key, "schemaVersion": "1.0", "body": malformed})
        whole.check(call["isError"] and (call["structured"] or {}).get("code") == "invalid_argument",
                    f"malformed body accepted: {malformed} -> {call['structured']}")
        rejected.append(malformed)
    unsupported = mutate(alice, "update_draft", {"packageKey": package_key, "schemaVersion": "1.0",
                                                 "format": "unsupported.dashboard.v1"})
    whole.check(unsupported["isError"] and (unsupported["structured"] or {}).get("code") == "invalid_argument",
                f"unsupported format accepted: {unsupported['structured']}")
    preserved = db.rows(f"SELECT generation FROM honua.studio_package_drafts WHERE draft_id = {quote(draft_id)}")
    whole.check(preserved and preserved[0]["generation"] == generation,
                f"rejected documents changed the durable generation: {preserved}")
    whole.evidence["rejectedBodies"] = rejected

    denials = ReceiptRow("dashboard.authorization.nondisclosure",
                         "A different owner, a different tenant, or insufficient OAuth scope receives the canonical denial without leaking draft existence or content.")
    rows.append(denials)
    missing_id = str(uuid.uuid4())
    observed: dict[str, Any] = {}
    for label, token_value, verb, fields in (
        ("other-owner-read", bob, "get_draft", {"draftId": draft_id}),
        ("other-owner-compose", bob, "add_layer", {"draftId": draft_id, "generation": generation, "layer": {"id": "stolen"}}),
        ("other-owner-missing-id", bob, "get_draft", {"draftId": missing_id}),
        ("same-subject-other-tenant-read", alice_other_tenant, "get_draft", {"draftId": draft_id}),
        ("same-subject-other-tenant-compose", alice_other_tenant, "add_layer",
         {"draftId": draft_id, "generation": generation, "layer": {"id": "stolen"}}),
        ("read-only-scope-compose", alice_read_only, "add_layer",
         {"draftId": draft_id, "generation": generation, "layer": {"id": "stolen"}}),
    ):
        call = server.tool(token_value, verb, fields)
        error = call["structured"] or {}
        observed[label] = {"isError": call["isError"], "code": error.get("code"),
                           "studioAuthorizationCode": error.get("studioAuthorizationCode"),
                           "disclosedFields": denial_disclosure(error)}
        denials.check(call["isError"], f"{label}: call succeeded")
        denials.check(not denial_disclosure(error), f"{label}: denial disclosed {denial_disclosure(error)}")
        if label == "read-only-scope-compose":
            denials.check(error.get("studioAuthorizationCode") in SCOPE_DENIAL_CODES,
                          f"{label}: expected a scope denial, observed {error}")
        else:
            denials.check(error.get("code") == "permission_denied" and error.get("studioAuthorizationCode") == CROSS_USER_DENIED,
                          f"{label}: expected permission_denied/{CROSS_USER_DENIED}, observed {error.get('code')}/{error.get('studioAuthorizationCode')}")
    denials.evidence["observed"] = observed
    unchanged = db.rows(f"SELECT generation, owner_id FROM honua.studio_package_drafts WHERE draft_id = {quote(draft_id)}")
    denials.check(unchanged and unchanged[0]["generation"] == generation and unchanged[0]["owner_id"] == owner_id,
                  f"denied calls changed the durable draft: {unchanged}")

    durable = ReceiptRow("dashboard.version.restart-identity",
                         "Validate, save, get and reopen preserve the exact content hash and immutable version identity across a server restart.")
    rows.append(durable)
    validated = server.tool(alice, "validate_draft", {"draftId": draft_id})
    durable.check(not validated["isError"], f"validate_draft failed: {validated['structured']}")
    save_trace = uuid.uuid4().hex
    status, saved, headers = server.request("POST", f"/api/v1/studio/package-drafts/{draft_id}/content-versions",
                                            {"Authorization": f"Bearer {alice()}",
                                             "traceparent": f"00-{save_trace}-{uuid.uuid4().hex[:16]}-01"},
                                            {"changeNote": "honua-server#3429 receipt"})
    version = (saved or {}).get("data") or {}
    item_id, version_id, content_hash = version.get("itemId"), version.get("versionId"), version.get("contentHash")
    if not durable.check(status == 201 and bool(version_id) and bool(content_hash), f"save version failed: {status} {saved}"):
        rows.append(ReceiptRow("dashboard.publication.governed-proposal",
                               "The saved dashboard enters the governed proposal lifecycle without changing a publication pointer."))
        rows[-1].check(False, "no saved version exists to propose")
        audit = ReceiptRow("dashboard.audit.joined-evidence",
                           "Every mutation joins owner, tenant, actor, durable audit and correlation evidence through the shared seam.")
        rows.append(audit)
        audit_joins(db, audit, mutations, owner_id, args.tenant, instance_reader(args))
        return build_receipt(rows, candidate_identity(args), started)
    if status == 201:
        mutations.append(("save_version", {"traceId": save_trace}))
    stored = db.rows(f"SELECT content_hash FROM honua.studio_content_versions WHERE version_id = {quote(str(version_id))}")
    durable.check(stored and stored[0]["content_hash"] == content_hash, f"stored hash differs from the saved response: {stored}")

    restart = subprocess.run(args.restart_command, shell=True, capture_output=True, text=True)
    durable.check(restart.returncode == 0, f"restart command failed: {restart.stderr.strip()}")
    durable.check(server.wait_ready(alice, args.ready_timeout), "server did not serve MCP again after restart")
    status, fetched, _ = server.request("GET", f"/api/v1/studio/content-items/{item_id}/versions/{version_id}",
                                        {"Authorization": f"Bearer {alice()}"})
    fetched_version = (fetched or {}).get("data") or {}
    durable.check(status == 200 and fetched_version.get("versionId") == version_id
                  and fetched_version.get("contentHash") == content_hash,
                  f"version identity or hash changed across restart: {status} {fetched_version}")
    reopen_trace = uuid.uuid4().hex
    status, reopened, headers = server.request("POST", f"/api/v1/studio/content-items/{item_id}/versions/{version_id}/reopen",
                                               {"Authorization": f"Bearer {alice()}",
                                                "traceparent": f"00-{reopen_trace}-{uuid.uuid4().hex[:16]}-01"}, {})
    reopened_draft = (reopened or {}).get("data") or {}
    durable.check(status == 201 and reopened_draft.get("baseVersionId") == version_id
                  and reopened_draft.get("envelope", {}).get("body") == fetched_version.get("envelope", {}).get("body"),
                  f"reopen did not bind the saved version and its body: {status} {reopened_draft}")
    if status == 201:
        mutations.append(("reopen_version", {"traceId": reopen_trace}))
    after = db.rows(f"SELECT content_hash FROM honua.studio_content_versions WHERE version_id = {quote(str(version_id))}")
    durable.check(after and after[0]["content_hash"] == content_hash, "stored hash changed across restart and reopen")
    durable.evidence.update({"itemId": item_id, "versionId": version_id, "contentHash": content_hash,
                             "reopenedDraftId": reopened_draft.get("draftId")})

    proposal = ReceiptRow("dashboard.publication.governed-proposal",
                          "The saved dashboard enters the governed proposal lifecycle without changing a publication pointer.")
    rows.append(proposal)
    proposed = server.tool(alice, "propose_publication", {"itemId": item_id, "versionId": version_id,
                                                          "contentHash": content_hash, "route": f"/studio/dashboard-{run_id}",
                                                          "visibility": "personal"})
    proposed_body = proposed["structured"] or {}
    proposal.evidence["response"] = {k: proposed_body.get(k) for k in ("status", "proposalId", "proposalUri", "code",
                                                                       "humanConfirmationRequired")}
    proposal.check(not proposed["isError"] and proposed_body.get("status") == "AwaitingApproval"
                   and proposed_body.get("proposalId"), f"publication did not enter AwaitingApproval: {proposed_body}")
    pointer = db.rows(f"SELECT published_version_id, current_version_id FROM honua.studio_content_items WHERE item_id = {quote(str(item_id))}")
    proposal.check(pointer and pointer[0]["published_version_id"] is None and pointer[0]["current_version_id"] == version_id,
                   f"publication pointer moved without approval: {pointer}")
    if not proposed["isError"]:
        mutations.append(("propose_publication", proposed))

    audit = ReceiptRow("dashboard.audit.joined-evidence",
                       "Every mutation joins owner, tenant, actor, durable audit and correlation evidence through the shared seam.")
    rows.append(audit)
    audit_joins(db, audit, mutations, owner_id, args.tenant, instance_reader(args))

    return build_receipt(rows, candidate_identity(args), started)


def audit_joins(db: Database, audit: ReceiptRow, mutations: list[tuple[str, dict[str, Any]]],
                owner_id: str | None, tenant: str, instance_reader: Callable[[str], dict[str, Any] | None]) -> None:
    """Join each mutation the client sent with the durable evidence the server recorded for it.

    The client's own W3C trace id is the join key: the operation runtime's correlation id embeds
    it, so no response field has to be trusted. For every mutation the durable audit log must hold
    an accepted and a completed row for one operation instance under that trace; the operation
    instance must carry the request tenant and the same correlation and audit ids; and the audit
    actor must be the draft owner the lifecycle store recorded.
    """
    joined = []
    for verb, call in mutations:
        trace_id = call.get("traceId")
        if not audit.check(bool(trace_id), f"{verb}: no client trace id was sent"):
            continue
        recorded = db.rows("SELECT audit_id, action, actor, resource_id, correlation_id FROM honua.audit_log "
                           f"WHERE resource_type = 'operation_instance' AND correlation_id LIKE {quote('00-' + trace_id + '-%')} "
                           "ORDER BY audit_id")
        actions = [row["action"] for row in recorded]
        instances = sorted({row["resource_id"] for row in recorded})
        audit.check("operation.accepted" in actions and "operation.completed" in actions,
                    f"{verb}: durable audit rows for trace {trace_id} are {actions}")
        audit.check(len(instances) == 1, f"{verb}: trace {trace_id} maps to operation instances {instances}")
        actors = sorted({row["actor"] for row in recorded})
        audit.check(actors == [owner_id], f"{verb}: audit actors {actors} are not the draft owner {owner_id}")
        instance = instance_reader(instances[0]) if len(instances) == 1 else None
        if audit.check(instance is not None, f"{verb}: operation instance {instances} is not durably readable"):
            audit.check(instance.get("tenantId") == tenant, f"{verb}: instance tenant {instance.get('tenantId')} != {tenant}")
            audit.check(instance.get("correlationId") in {row["correlation_id"] for row in recorded},
                        f"{verb}: instance correlation differs from the audit rows")
            audit.check(str(instance.get("auditId")) in {str(row["audit_id"]) for row in recorded},
                        f"{verb}: instance audit id {instance.get('auditId')} is not one of its audit rows")
        joined.append({"verb": verb, "traceId": trace_id, "operationInstanceId": instances[0] if instances else None,
                       "tenantId": (instance or {}).get("tenantId"), "auditActions": actions, "actors": actors,
                       "auditIds": [row["audit_id"] for row in recorded]})
    audit.evidence.update({"ownerId": owner_id, "joins": joined})


def instance_reader(args: argparse.Namespace) -> Callable[[str], dict[str, Any] | None]:
    """Read a durable operation instance from the control-plane Redis store (read-only GET)."""
    def read(instance_id: str) -> dict[str, Any] | None:
        result = subprocess.run([*args.redis_cli.split(), "GET", f"controlplane:operation-instance:{instance_id}"],
                                capture_output=True, text=True)
        text = result.stdout.strip()
        return json.loads(text) if result.returncode == 0 and text.startswith("{") else None
    return read


def candidate_identity(args: argparse.Namespace) -> dict[str, Any]:
    identity: dict[str, Any] = {"image": args.candidate_image, "sha": args.candidate_sha}
    if args.container:
        inspected = subprocess.run(["docker", "inspect", "--format", "{{.Image}}|{{.Config.Image}}", args.container],
                                   capture_output=True, text=True)
        if inspected.returncode == 0:
            image_id, reference = inspected.stdout.strip().split("|", 1)
            identity.update({"runningImageId": image_id, "runningReference": reference})
    return identity


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--admin-key", required=True)
    parser.add_argument("--jwt-key", required=True, help="Oidc:TokenValidation:SymmetricSigningKey of the target")
    parser.add_argument("--jwt-issuer", required=True)
    parser.add_argument("--jwt-audience", required=True)
    parser.add_argument("--psql", required=True, help="psql command prefix, e.g. 'docker exec pg psql -U honua -d honua'")
    parser.add_argument("--redis-cli", required=True, help="redis-cli command prefix, e.g. 'docker exec redis redis-cli'")
    parser.add_argument("--restart-command", required=True, help="shell command that restarts the server process")
    parser.add_argument("--container", help="server container name, recorded as running image identity")
    parser.add_argument("--candidate-image", required=True)
    parser.add_argument("--candidate-sha", required=True)
    parser.add_argument("--tenant", default="public")
    parser.add_argument("--other-tenant", default="tenant-b")
    parser.add_argument("--role", default="studio-author")
    parser.add_argument("--ready-timeout", type=float, default=180)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args(argv)
    receipt = run(args)
    args.out.write_text(json.dumps(receipt, indent=2) + "\n")
    for row in receipt["rows"]:
        print(f"{row['status']:4}  {row['id']}")
        for failure in row["failures"]:
            print(f"      - {failure}")
    print(f"verdict: {receipt['verdict']}")
    return 0 if receipt["verdict"] == "pass" else 1


if __name__ == "__main__":
    sys.exit(main())
