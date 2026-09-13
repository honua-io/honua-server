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


def test_audit_join_requires_owner_actor_tenant_and_matching_instance_ids():
    trace = "9d5bce2637737efd3de6355119fd2dfa"
    correlation = f"00-{trace}-29763eb9495f9334-00"
    audit_rows = [
        {"audit_id": 92, "action": "operation.accepted", "actor": "owner", "resource_id": "opinst-1", "correlation_id": correlation},
        {"audit_id": 93, "action": "operation.completed", "actor": "owner", "resource_id": "opinst-1", "correlation_id": correlation},
    ]
    instance = {"tenantId": "public", "correlationId": correlation, "auditId": "92"}

    row = receipt.ReceiptRow("audit", "joined")
    database = _FakeDatabase(audit_rows)
    receipt.audit_joins(database, row, [("add_layer", {"traceId": trace})], "owner", "public", lambda _: instance)
    assert row.failures == []
    assert f"'00-{trace}-%'" in database.queries[0]

    wrong_tenant = receipt.ReceiptRow("audit", "joined")
    receipt.audit_joins(_FakeDatabase(audit_rows), wrong_tenant, [("add_layer", {"traceId": trace})], "owner", "tenant-b",
                        lambda _: instance)
    assert any("tenant" in failure for failure in wrong_tenant.failures)

    wrong_actor = receipt.ReceiptRow("audit", "joined")
    receipt.audit_joins(_FakeDatabase(audit_rows), wrong_actor, [("add_layer", {"traceId": trace})], "someone-else", "public",
                        lambda _: instance)
    assert any("not the draft owner" in failure for failure in wrong_actor.failures)

    missing = receipt.ReceiptRow("audit", "joined")
    receipt.audit_joins(_FakeDatabase([]), missing, [("add_layer", {"traceId": trace})], "owner", "public", lambda _: None)
    assert missing.failures
