#!/usr/bin/env python3
"""Prove scoped approval effects against an immutable candidate in isolated Docker.

This is server API evidence, not a Console browser or terminal-journey receipt.
Requires Docker Compose and already pulled server and fixture images identified by digest.
"""

import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import secrets
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid


POSTGIS_IMAGE = "postgis/postgis@sha256:60f6ad1d21ea86a67d47780b9a0d1e1d200500f62b19293fa834d0dea80b8677"
REDIS_IMAGE = "redis@sha256:ccd6aa8d45ff3f033d6fa15b8cc1a50579f65c89f38cf9bb607a954c4f2128ed"


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def run(*args):
    return subprocess.check_output(args, text=True).strip()


def prove(image, revision):
    require(re.fullmatch(r"ghcr\.io/honua-io/honua-server@sha256:[0-9a-f]{64}", image),
            "An immutable Honua server image digest is required")
    require(re.fullmatch(r"[0-9a-f]{40}", revision), "A full source revision is required")
    actual = run("docker", "image", "inspect", image, "--format",
                 '{{index .Config.Labels "org.opencontainers.image.revision"}}')
    require(actual == revision, "Candidate image source revision differs from the expected pin")
    image_id = run("docker", "image", "inspect", image, "--format", "{{.Id}}")
    fixtures = {name: {"image": dependency,
                       "imageId": run("docker", "image", "inspect", dependency, "--format", "{{.Id}}")}
                for name, dependency in [("postgis", POSTGIS_IMAGE), ("redis", REDIS_IMAGE)]}
    project = "approve-proof-" + uuid.uuid4().hex[:12]
    admin_key = secrets.token_hex(32)
    receipt = {"schemaVersion": "honua.admin-approve-candidate/v1",
               "image": image, "imageId": image_id, "sourceRevision": revision, "fixtureImages": fixtures,
               "environment": "Development", "devGrantEdition": "Pro",
               "scope": "server API; excludes Console browser qualification", "checks": {}}

    with tempfile.TemporaryDirectory(prefix="honua-approve-") as directory:
        compose_path = Path(directory) / "compose.json"
        environment = {
            "ASPNETCORE_ENVIRONMENT": "Development",
            "ASPNETCORE_URLS": "http://+:8080",
            "ConnectionStrings__DefaultConnection":
                f"Host=postgres;Database=honua;Username=honua;Password={admin_key}",
            "ConnectionStrings__Redis": "redis:6379",
            "HONUA_ADMIN_PASSWORD": admin_key,
            "Security__ConnectionEncryption__MasterKey": secrets.token_hex(32),
            "HostValidation__AllowedHosts__0": "127.0.0.1",
            "Licensing__DevGrantEdition": "Pro",
        }
        compose = {"services": {
            "postgres": {"image": POSTGIS_IMAGE, "environment": {
                "POSTGRES_DB": "honua", "POSTGRES_USER": "honua", "POSTGRES_PASSWORD": admin_key},
                "healthcheck": {"test": ["CMD-SHELL", "pg_isready -h 127.0.0.1 -U honua -d honua"],
                                "interval": "2s", "retries": 30}},
            "redis": {"image": REDIS_IMAGE, "command": ["redis-server", "--appendonly", "yes"]},
            "server": {"image": image, "ports": ["127.0.0.1::8080"],
                       "environment": environment,
                       "depends_on": {"postgres": {"condition": "service_healthy"}}},
        }}

        def dc(*args):
            return run("docker", "compose", "-p", project, "-f", str(compose_path), *args)

        def start():
            compose_path.write_text(json.dumps(compose))
            os.chmod(compose_path, 0o600)
            dc("up", "-d")
            return "http://" + dc("port", "server", "8080")

        endpoint = ""

        def call(path, method="GET", body=None, key=admin_key):
            request = urllib.request.Request(endpoint + path, method=method,
                headers={"X-API-Key": key, "Content-Type": "application/json"},
                data=None if body is None else json.dumps(body).encode())
            try:
                response = urllib.request.urlopen(request, timeout=15)
            except urllib.error.HTTPError as error:
                response = error
            with response:
                raw = response.read().decode()
                return response.code, json.loads(raw) if raw.startswith("{") else raw

        def ready():
            deadline = time.monotonic() + 120
            while time.monotonic() < deadline:
                try:
                    if call("/healthz/ready")[0] == 200:
                        return
                except (OSError, TimeoutError):
                    # Connection refusal/timeouts are expected while the host starts;
                    # retry only until the readiness deadline, then fail the proof.
                    pass
                time.sleep(1)
            raise RuntimeError("Candidate did not become ready")

        def expect(path, status, **kwargs):
            observed, body = call(path, **kwargs)
            # Do not echo response bodies: key-mint responses contain credentials.
            require(observed == status, f"{kwargs.get('method', 'GET')} {path}: expected {status}, got {observed}")
            return body

        try:
            endpoint = start()
            ready()
            keys = {}
            for name, grants in [("approve", ["admin:read", "admin:approve"]), ("read", ["admin:read"])]:
                created = expect("/api/v1/admin/api-keys", 201, method="POST",
                                 body={"name": "candidate-" + name, "permissions": grants})["data"]
                keys[name] = created
                effective = expect(f"/api/v1/admin/api-keys/{created['apiKey']['id']}/effective-permissions",
                                   200, key=created["key"])["data"]
                require(effective["permissions"] == grants and effective["canAuthenticate"] is True,
                        "Minted key effective permissions do not match the requested grants")
                expect("/api/v1/admin/proposals", 200, key=created["key"])
                expect("/api/v1/admin/services/x/access-policy", 403, method="PUT",
                       body={"allowAnonymous": True}, key=created["key"])
            receipt["checks"]["mintReadApproveAndReadOnlyKeys"] = "passed"
            receipt["checks"]["exactEffectiveGrantsAndUnrelatedWriteDenial"] = "passed"

            drafts = []
            for decision in ["approve", "reject"]:
                draft = expect("/api/v1/studio/package-drafts", 201, method="POST", body={
                    "packageKey": "proof-" + decision, "workspaceId": project,
                    "envelope": {"family": "query", "schemaVersion": "1.0",
                                 "format": "studio_query_package.v1", "body": {"where": "population > 42"}},
                })["data"]
                # Read the persisted baseline: PostgreSQL normalizes timestamp precision
                # relative to the immediate create response. Assert fixture values before
                # using the full persisted document for subsequent no-change comparisons.
                draft = expect("/api/v1/studio/package-drafts/" + draft["draftId"], 200)["data"]
                require(draft["envelope"]["body"] == {"where": "population > 42"}, "Fixture values differ")
                require(draft["packageKey"] == "proof-" + decision and draft["generation"] == 1
                        and draft["workspaceId"] == project and draft["family"] == "query",
                        "Persisted fixture metadata differs")
                drafts.append(draft)

            # Tighten the canonical guardrail after setup; the API is the only fixture writer.
            environment["Guardrails__Overrides__StudioDraftMutation"] = "RequiresApproval"
            endpoint = start()
            ready()
            decisions = []
            for decision, draft in zip(["approve", "reject"], drafts):
                draft_path = "/api/v1/studio/package-drafts/" + draft["draftId"]
                handle = expect(draft_path, 202, method="DELETE")["data"]
                proposal_path = "/api/v1/admin/proposals/" + handle["proposalId"]
                before = expect(proposal_path, 200, key=keys["approve"]["key"])
                require(before["status"] == "AwaitingApproval", "Deletion did not pause for approval")
                require(expect(draft_path, 200)["data"] == draft, "Proposing deletion changed the draft")
                body = {"reason": "Candidate fixture rejected"} if decision == "reject" else None
                denied = expect(proposal_path + "/" + decision, 403, method="POST", body=body,
                                key=keys["read"]["key"])
                require("admin:approve" in denied["detail"], "Denial omits the missing grant")
                require(expect(proposal_path, 200, key=keys["approve"]["key"]) == before,
                        "Denied decision changed proposal state")
                require(expect(draft_path, 200)["data"] == draft, "Denied decision changed fixture values")
                result = expect(proposal_path + "/" + decision, 200, method="POST", body=body,
                                key=keys["approve"]["key"])
                expected_status = "Succeeded" if decision == "approve" else "Rejected"
                require(result["status"] == expected_status, "Decision did not reach the expected state")
                require(result["resolvedBy"] == keys["approve"]["apiKey"]["id"], "Wrong decision actor")
                persisted = expect(proposal_path, 200, key=keys["approve"]["key"])
                require(persisted["status"] == expected_status, "Decision was not persisted")
                if decision == "approve":
                    expect(draft_path, 404)
                else:
                    require(expect(draft_path, 200)["data"] == draft, "Reject changed fixture values")
                decisions.append({"decision": decision, "proposalId": handle["proposalId"],
                                  "operationInstanceId": handle["operationInstanceId"],
                                  "draftId": draft["draftId"], "status": expected_status})
            receipt["checks"]["readOnlyDecisionsDenyWithoutChangingProposalOrDraft"] = "passed"
            receipt["checks"]["approveDeletesOnlyApprovedDraftAndRejectPreservesExactDraft"] = "passed"
            receipt["decisions"] = decisions

            # Extend #4637: retained expired metadata must never mint unusable rotated material.
            expired = expect("/api/v1/admin/api-keys", 201, method="POST", body={
                "name": "expired-approve", "permissions": ["admin:read", "admin:approve"],
                "expiresAt": "2020-01-01T00:00:00Z"})["data"]
            key_path = "/api/v1/admin/api-keys/" + expired["apiKey"]["id"]
            expect(key_path + "/rotate", 404, method="POST")
            metadata = expect(key_path + "/effective-permissions", 200)["data"]
            require(metadata["status"] == "expired" and metadata["canAuthenticate"] is False,
                    "Expired key metadata or authentication eligibility changed")
            expect("/api/v1/admin/proposals", 401, key=expired["key"])
            receipt["checks"]["expiredApproveKeyCannotRotateOrAuthenticate"] = "passed"
            receipt["completedAt"] = datetime.now(timezone.utc).isoformat()
            return receipt
        finally:
            dc("down", "--volumes", "--remove-orphans")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--image", required=True)
    parser.add_argument("--source-revision", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    # A failed rerun must not leave an older success receipt in place.
    args.output.unlink(missing_ok=True)
    evidence = prove(args.image, args.source_revision)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(evidence, indent=2) + "\n")
    print(f"Passed {len(evidence['checks'])} server candidate checks; receipt: {args.output}")
