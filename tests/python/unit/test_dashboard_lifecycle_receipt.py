# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""Contract tests for the candidate dashboard lifecycle receipt driver (honua-server#3429).

The driver judges a deployed candidate, so its own judgement rules must not drift: a denial that
carries draft state is a disclosure, one failing row fails the receipt, a minted access token is
a verifiable HS256 token, and audit evidence is joined only through the client's own trace id.
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import importlib.util
import json
from datetime import datetime, timezone
from pathlib import Path

REPOSITORY_ROOT = Path(__file__).parents[3]
DRIVER = REPOSITORY_ROOT / "scripts" / "studio" / "dashboard_lifecycle_receipt.py"


def _load():
    spec = importlib.util.spec_from_file_location("dashboard_lifecycle_receipt", DRIVER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


receipt = _load()


def test_denial_disclosure_flags_draft_state_but_not_error_fields():
    assert receipt.denial_disclosure({"code": "permission_denied", "message": "no"}) == []
    assert receipt.denial_disclosure({"code": "permission_denied", "generation": 4, "envelope": {}}) == [
        "envelope", "generation"]
    assert receipt.denial_disclosure(None) == []


def test_receipt_verdict_requires_every_row_to_pass():
    started = datetime.now(timezone.utc)
    passing = receipt.ReceiptRow("a", "first")
    failing = receipt.ReceiptRow("b", "second")
    failing.check(False, "observed failure")

    assert receipt.build_receipt([passing], {}, started)["verdict"] == "pass"
    mixed = receipt.build_receipt([passing, failing], {}, started)
    assert mixed["verdict"] == "fail"
    assert mixed["rows"][1]["failures"] == ["observed failure"]
    assert receipt.build_receipt([], {}, started)["verdict"] == "fail"


def test_minted_token_is_a_verifiable_hs256_token_with_unique_ids():
    key, issuer, audience = "k" * 40, "https://idp.example", "honua-mcp"
    first = receipt.mint_token(key, issuer, audience, {"sub": "alice", "tid": "public"})
    second = receipt.mint_token(key, issuer, audience, {"sub": "alice", "tid": "public"})

    header, payload, signature = first.split(".")
    expected = base64.urlsafe_b64encode(
        hmac.new(key.encode(), f"{header}.{payload}".encode(), hashlib.sha256).digest()).rstrip(b"=").decode()
    claims = json.loads(base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4)))

    assert signature == expected
    assert claims["iss"] == issuer and claims["aud"] == audience and claims["sub"] == "alice"
    assert claims["exp"] > claims["iat"]
    # Replay protection admits one request per token; every mint must be distinct.
    assert first.split(".")[1] != second.split(".")[1]


class _FakeDatabase:
    def __init__(self, rows):
        self._rows = rows
        self.queries = []

    def rows(self, sql):
        self.queries.append(sql)
        return self._rows


def _row(audit_id, action, resource_type, resource_id, correlation, actor="owner"):
    return {"audit_id": audit_id, "action": action, "actor": actor, "resource_type": resource_type,
            "resource_id": resource_id, "correlation_id": correlation}


def test_audit_join_requires_owner_actor_tenant_and_matching_instance_ids():
    trace = "9d5bce2637737efd3de6355119fd2dfa"
    correlation = f"00-{trace}-29763eb9495f9334-00"
    audit_rows = [
        _row(92, "operation.accepted", "operation_instance", "opinst-1", correlation),
        _row(93, "operation.completed", "operation_instance", "opinst-1", correlation),
    ]
    instance = {"status": "Completed", "tenantId": "public", "correlationId": correlation, "auditId": "92"}

    row = receipt.ReceiptRow("audit", "joined")
    database = _FakeDatabase(audit_rows)
    receipt.audit_joins(database, row, [("add_layer", {"traceId": trace})], "owner", "public", lambda _: instance)
    assert row.failures == []
    assert f"'00-{trace}-%'" in database.queries[0]
    assert "operation_proposal" in database.queries[0]

    wrong_tenant = receipt.ReceiptRow("audit", "joined")
    receipt.audit_joins(_FakeDatabase(audit_rows), wrong_tenant, [("add_layer", {"traceId": trace})], "owner", "tenant-b",
                        lambda _: instance)
    assert any("tenant" in failure for failure in wrong_tenant.failures)

    wrong_actor = receipt.ReceiptRow("audit", "joined")
    receipt.audit_joins(_FakeDatabase(audit_rows), wrong_actor, [("add_layer", {"traceId": trace})], "someone-else", "public",
                        lambda _: instance)
    assert any("not the draft owner" in failure for failure in wrong_actor.failures)

    not_completed = receipt.ReceiptRow("audit", "joined")
    receipt.audit_joins(_FakeDatabase(audit_rows[:1]), not_completed, [("add_layer", {"traceId": trace})], "owner", "public",
                        lambda _: instance)
    assert any("operation.accepted" in failure for failure in not_completed.failures)

    missing = receipt.ReceiptRow("audit", "joined")
    receipt.audit_joins(_FakeDatabase([]), missing, [("add_layer", {"traceId": trace})], "owner", "public", lambda _: None)
    assert missing.failures


def test_audit_join_accepts_approval_routed_mutation_only_with_its_proposal_row():
    # An awaiting-approval proposal never completes: its evidence is the accepted operation row plus
    # the proposal row, and the instance audit id is the proposal row (observed on a Production host).
    trace = "533dbdc4c3624074b8d6cdb1e0e2bdd9"
    correlation = f"00-{trace}-a257b11f7da75da5-00"
    accepted = _row(121, "operation.accepted", "operation_instance", "opinst-3", correlation)
    proposed = _row(122, "operation.proposed", "operation_proposal", "proposal-b1", correlation)
    instance = {"status": "RequiresApproval", "proposalId": "proposal-b1", "tenantId": "public",
                "correlationId": correlation, "auditId": "122"}
    mutation = [("propose_publication", {"traceId": trace})]

    joined = receipt.ReceiptRow("audit", "joined")
    receipt.audit_joins(_FakeDatabase([accepted, proposed]), joined, mutation, "owner", "public", lambda _: instance)
    assert joined.failures == []

    no_proposal = receipt.ReceiptRow("audit", "joined")
    receipt.audit_joins(_FakeDatabase([accepted]), no_proposal, mutation, "owner", "public", lambda _: instance)
    assert any("no durable proposal row" in failure for failure in no_proposal.failures)
    assert any("audit id 122" in failure for failure in no_proposal.failures)

    other_proposal = receipt.ReceiptRow("audit", "joined")
    receipt.audit_joins(_FakeDatabase([accepted, _row(122, "operation.proposed", "operation_proposal", "proposal-x", correlation)]),
                        other_proposal, mutation, "owner", "public", lambda _: instance)
    assert any("no durable proposal row" in failure for failure in other_proposal.failures)
