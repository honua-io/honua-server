# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License.
"""Published rc.1 -> candidate -> verified restore -> candidate, using the product MCP path."""
import hashlib
import json
import os
import re
import secrets
import socket
import sys
import subprocess
import tempfile
import shutil
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'scripts/docker'))
from package_quickstart import package
from compose_quickstart import initialize

RC1 = "ghcr.io/honua-io/honua-server@sha256:2c3ce36be9b6a4c8b3911d150de329be002131609933bc3ca4a3932ea95dc574"


def run(args, *, env=None, cwd=None, check=True):
    result = subprocess.run(args, env=env, cwd=cwd, text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=600)
    if check and result.returncode:
        # Never put credentials, seeds, or command output in CI failure messages.
        raise AssertionError(f"{args[0]} failed with exit code {result.returncode}")
    return result.stdout.strip()


def free_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def request(base, path, key, payload=None, session=None):
    headers = {"X-API-Key": key, "Accept": "application/json, text/event-stream"}
    if session:
        headers["Mcp-Session-Id"] = session
    data = None if payload is None else json.dumps(payload).encode()
    if data is not None:
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(base + path, data=data, headers=headers)
    with urllib.request.urlopen(req, timeout=30) as response:
        raw = response.read().decode()
        if raw.startswith(("event:", "data:")):
            raw = "\n".join(line[5:].strip() for line in raw.splitlines() if line.startswith("data:"))
        return (json.loads(raw) if raw else {}, response.headers.get("Mcp-Session-Id"))


def rpc(base, key, session, method, params, ident):
    result, new_session = request(base, "/mcp", key, {"jsonrpc": "2.0", "id": ident, "method": method, "params": params}, session)
    assert "error" not in result, "MCP request was rejected"
    return result["result"], new_session


def wait_for_proposal(base, key, proposal, expected, proposer_key, session, timeout=1500):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        detail, _ = request(base, f"/api/v1/admin/proposals/{proposal}", key)
        status = detail["status"].lower()
        resource, _ = rpc(base, proposer_key, session, "resources/read",
                          {"uri": f"honua://proposals/{proposal}"}, 99)
        proposal_resource = json.loads(resource["contents"][0]["text"])
        resource_status = proposal_resource["status"].lower()
        if status in ("completed", "failed", "rejected", "cancelled") and status == resource_status:
            actual = "succeeded" if status == "completed" else "failed"
            assert actual == expected, f"unexpected proposal terminal state: {status}"
            assert detail.get("resolutionReason") == proposal_resource.get("resolutionReason")
            if status == "failed":
                assert detail.get("resolutionReason"), "failed execution must report its reason"
            return detail
        time.sleep(5)
    raise AssertionError("operation did not reach its terminal state")


def wait_for_approval(base, key, proposal, timeout=30):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        detail, _ = request(base, f"/api/v1/admin/proposals/{proposal}", key)
        if detail["status"].lower() == "awaitingapproval":
            return detail
        time.sleep(0.5)
    raise AssertionError("proposal did not enter awaiting approval")


def test_published_rc1_mcp_convergence_and_verified_rollback(tmp_path):
    candidate = os.environ["HONUA_LIFECYCLE_IMAGE"]
    candidate_schema = max(int(path.name.split("_", 1)[0]) for path in (ROOT / "src/Honua.Server/Migrations").glob("[0-9]*_*.sql"))
    assert candidate_schema >= 142
    assert re.fullmatch(r"sha256:[a-f0-9]{64}", candidate)
    run(["docker", "pull", "--platform", "linux/amd64", RC1])
    prior_id = run(["docker", "image", "inspect", "--format", "{{.Id}}", RC1])
    project = "honua-lifecycle-" + secrets.token_hex(4)
    port, manager_port = free_port(), free_port()
    project_dir = Path(tempfile.mkdtemp(prefix="honua-lifecycle-"))
    project_dir.chmod(0o755)
    executor = os.environ["HONUA_LIFECYCLE_EXECUTOR_IMAGE"]
    assert run(["docker", "image", "inspect", "--format", '{{index .Config.Labels "honua.runtime.profile"}}', executor]) == "compose-executor"
    # Install the customer package, not a test-owned executor or Compose substitute.
    package(project_dir, candidate, executor, candidate, "2026.1-rc.2")
    env = os.environ.copy()
    settings = {"COMPOSE_PROJECT_NAME": project, "HONUA_IMAGE": RC1, "HONUA_HTTP_PORT": str(port),
        "HONUA_GRPC_PORT": str(free_port()), "HONUA_MANAGER_PORT": str(manager_port),
        "HONUA_PRIOR_IMAGE": RC1, "HONUA_PRIOR_MAX_SCHEMA": "140"}
    (project_dir / ".env").write_text("".join(f"{name}={value}\n" for name, value in settings.items()))
    env.update(initialize(project_dir))
    key = env["HONUA_MANAGER_ADMIN_PASSWORD"]
    postgres = env["HONUA_MANAGER_POSTGRES_PASSWORD"]
    keyring = env["HONUA_KEYRING_PASSWORD"]
    compose = ["docker", "compose", "--project-directory", str(project_dir), "-p", project,
        "-f", str(project_dir / "compose.yaml"), "-f", str(project_dir / "compose.executor.yaml")]
    def dc(*args, check=True):
        return run(compose + list(args), env=env, cwd=project_dir, check=check)
    def sql(statement):
        return dc("exec", "-T", "postgres", "psql", "-U", "honua", "-d", "honua", "-tA", "-v", "ON_ERROR_STOP=1", "-c", statement)
    def image_id():
        container = dc("ps", "-q", "honua")
        return run(["docker", "inspect", "--format", "{{.Image}}", container])
    def schema():
        return int(sql("SELECT max(substring(scriptname from '([0-9]+)_')::int) FROM public.schema_versions"))
    base = f"http://127.0.0.1:{manager_port}"
    try:
        try:
            dc("up", "-d", "--wait", "--wait-timeout", "300")
        except AssertionError as error:
            # Only report service state; do not print container environments or logs.
            states = dc("ps", "--all", "--format", "json", check=False)
            try:
                services = [json.loads(line) for line in states.splitlines() if line.strip()]
                summary = [{name: service.get(name) for name in ("Service", "State", "Health", "ExitCode")}
                           for service in services]
            except (ValueError, TypeError):
                summary = []
            # Emit bounded diagnostic lines with every generated secret removed.
            logs = run(compose + ["logs", "--no-color", "--tail", "80", "compose-manager"],
                       env=env, cwd=project_dir, check=False)
            for secret in (key, postgres, keyring, env["HONUA_MANAGER_MASTER_KEY"]):
                logs = logs.replace(secret, "[redacted]")
            diagnostic = []
            for line in logs.splitlines():
                if re.search(r"exception|critical|fail|error", line, re.I):
                    # Avoid printing values from connection strings or unknown generated credentials.
                    line = re.sub(r"(?i)(password|token|secret|key)=([^;\s]+)", r"\1=[redacted]", line)
                    line = re.sub(r"[A-Za-z0-9+/=_-]{32,}", "[redacted]", line)
                    diagnostic.append(line[:300])
            raise AssertionError(f"Compose startup failed; service states: {summary}; manager diagnostics: {diagnostic[-8:]}") from error
        assert image_id() == prior_id and schema() == 140
        sql("CREATE TABLE public.rc2_lifecycle_marker (id integer PRIMARY KEY, value text NOT NULL); INSERT INTO public.rc2_lifecycle_marker VALUES (1, 'retained')")
        proposer, _ = request(base, "/api/v1/admin/api-keys/", key, {"name": "proposer", "permissions": ["admin:*"]})
        approver, _ = request(base, "/api/v1/admin/api-keys/", key, {"name": "approver", "permissions": ["admin:*"]})
        proposer_key, approver_key = proposer["data"]["key"], approver["data"]["key"]
        _, session = rpc(base, proposer_key, None, "initialize", {"protocolVersion": "2025-03-26", "capabilities": {}, "clientInfo": {"name": "lifecycle", "version": "1"}}, 1)
        def call(name, arguments, ident):
            result, _ = rpc(base, proposer_key, session, "tools/call", {"name": name, "arguments": arguments}, ident)
            assert not result.get("isError"), "MCP tool refused the request"
            return result["structuredContent"]
        failed = call("honua_propose_platform_release_convergence", {
            "reason": "convergence failure regression", "idempotencyKey": "failed-convergence"}, 20)
        failed_proposal = failed["targets"][0]["proposalId"]
        assert failed_proposal
        wait_for_approval(base, approver_key, failed_proposal)
        # The stable manager keeps its own Redis; only the serving stack loses Redis.
        dc("stop", "redis")
        request(base, f"/api/v1/admin/proposals/{failed_proposal}/approve", approver_key, {})
        wait_for_proposal(base, approver_key, failed_proposal, "failed", proposer_key, session)
        dc("start", "redis")
        deadline = time.monotonic() + 120
        while time.monotonic() < deadline:
            try:
                with urllib.request.urlopen(f"http://127.0.0.1:{port}/healthz/ready", timeout=5) as ready:
                    if ready.status == 200:
                        break
            except (urllib.error.URLError, TimeoutError):
                # Redis recovery can temporarily refuse readiness; the deadline bounds retries.
                pass
            time.sleep(2)
        else:
            raise AssertionError("serving stack did not recover after Redis restarted")
        convergence = call("honua_propose_platform_release_convergence", {
            "reason": "candidate convergence after failed operation"}, 2)
        proposal = convergence["targets"][0]["proposalId"]
        assert proposal
        wait_for_approval(base, approver_key, proposal)
        assert image_id() == prior_id and schema() == 140
        try:
            request(base, f"/api/v1/admin/proposals/{proposal}/approve", proposer_key, {})
            raise AssertionError("self approval succeeded")
        except urllib.error.HTTPError as error:
            assert error.code in (400, 403, 409)
        detail, _ = request(base, f"/api/v1/admin/proposals/{proposal}", key)
        assert detail["status"].lower() == "awaitingapproval"
        request(base, f"/api/v1/admin/proposals/{proposal}/approve", approver_key, {})
        first_convergence = wait_for_proposal(base, approver_key, proposal, "succeeded", proposer_key, session)
        assert image_id() == candidate and schema() == candidate_schema
        assert sql("SELECT count(*) FROM public.schema_versions WHERE scriptname LIKE '%142_CreateAiFinancialAccounting.sql'") == "1"
        assert sql("SELECT value FROM public.rc2_lifecycle_marker WHERE id=1") == "retained"
        assert sql("SELECT to_regclass('honua.ai_financial_invocations') IS NOT NULL") == "t"
        sql("UPDATE public.rc2_lifecycle_marker SET value='changed-after-upgrade' WHERE id=1")
        manifests = list((project_dir / "backups").glob("backup-*/manifest.json"))
        assert len(manifests) == 1
        backup = json.loads(manifests[0].read_text())
        assert backup["image"] == prior_id and backup["schemaVersion"] == 140
        with (manifests[0].parent / "database.dump").open("rb") as dump:
            assert hashlib.file_digest(dump, "sha256").hexdigest() == backup["sha256"]
        # A wrong backup must be rejected while migration 142 is still applied,
        # before any prior image can be started against the incompatible schema.
        wrong = call("honua_propose_rollback", {"targetId": "compose-local", "toRevision": prior_id,
            "backupId": "backup-" + "0" * 32, "idempotencyKey": "wrong-backup"}, 3)
        if wrong.get("proposalId"):
            wait_for_approval(base, approver_key, wrong["proposalId"])
            request(base, f"/api/v1/admin/proposals/{wrong['proposalId']}/approve", approver_key, {})
            wait_for_proposal(base, approver_key, wrong["proposalId"], "failed", proposer_key, session)
        else:
            assert wrong.get("outcome", "").lower() in ("blocked", "rejected", "failed")
        assert image_id() == candidate and schema() == candidate_schema
        assert sql("SELECT value FROM public.rc2_lifecycle_marker WHERE id=1") == "changed-after-upgrade"
        rollback = call("honua_propose_rollback", {"targetId": "compose-local",
            "idempotencyKey": "verified-backup"}, 4)
        assert rollback["proposalId"]
        preview = wait_for_approval(base, approver_key, rollback["proposalId"])
        assert any(prior_id in line for line in preview["diff"])
        assert any(backup["backupId"] in line for line in preview["diff"])
        assert any(backup["backupId"] in line for line in preview["dryRun"])
        assert image_id() == candidate and schema() == candidate_schema
        request(base, f"/api/v1/admin/proposals/{rollback['proposalId']}/approve", approver_key, {})
        restored = wait_for_proposal(base, approver_key, rollback["proposalId"], "succeeded", proposer_key, session)
        assert image_id() == prior_id and schema() == 140
        assert sql("SELECT value FROM public.rc2_lifecycle_marker WHERE id=1") == "retained"
        assert sql("SELECT to_regclass('honua.ai_financial_invocations') IS NULL") == "t"
        assert sql("SELECT PostGIS_Version()")
        # Closing a client early must not terminate the listener or block later requests.
        disconnect = "import socket; s=socket.socket(socket.AF_UNIX); s.connect('/sock/executor.sock'); s.sendall(b'{\"op\":\"status\"}\\n'); s.close()"
        run(["docker", "run", "--rm", "-v", project + "_compose-executor-socket:/sock", "python:3.12-alpine", "python", "-c", disconnect])
        # Send an out-of-scope field to the sidecar's actual socket.
        socket_test = "import socket; s=socket.socket(socket.AF_UNIX); s.connect('/sock/executor.sock'); s.sendall(b'{\"op\":\"status\",\"project\":\"other\"}\\n'); print(s.recv(65536).decode())"
        rejected = run(["docker", "run", "--rm", "-v", project + "_compose-executor-socket:/sock", "python:3.12-alpine", "python", "-c", socket_test])
        scope = json.loads(rejected)
        assert not scope["ok"] and scope["code"] == "compose-scope-violation"
        assert image_id() == prior_id and schema() == 140
        sql("UPDATE public.rc2_lifecycle_marker SET value='after-first-rollback' WHERE id=1")
        reconvergence = call("honua_propose_platform_release_convergence", {
            "reason": "candidate convergence after verified rollback"}, 5)
        next_target = reconvergence["targets"][0]
        next_proposal = next_target.get("proposalId")
        assert next_proposal, f"re-convergence did not produce a proposal: {next_target.get('outcome')}"
        assert next_proposal != proposal
        wait_for_approval(base, approver_key, next_proposal)
        request(base, f"/api/v1/admin/proposals/{next_proposal}/approve", approver_key, {})
        converged_again = wait_for_proposal(base, approver_key, next_proposal, "succeeded", proposer_key, session)
        restore_ids = {detail["executionOperationId"] for detail in (first_convergence, restored, converged_again)}
        assert len(restore_ids) == 3 and all(ident.startswith("restore-") for ident in restore_ids)
        assert image_id() == candidate and schema() == candidate_schema
        assert sql("SELECT value FROM public.rc2_lifecycle_marker WHERE id=1") == "after-first-rollback"
        assert sql("SELECT to_regclass('honua.ai_financial_invocations') IS NOT NULL") == "t"
        new_manifests = list((project_dir / "backups").glob("backup-*/manifest.json"))
        assert len(new_manifests) == 2
        new_backup = next(json.loads(path.read_text()) for path in new_manifests if path != manifests[0])
        assert new_backup["backupId"] != backup["backupId"]
        assert new_backup["image"] == prior_id and new_backup["schemaVersion"] == 140
        with urllib.request.urlopen(f"http://127.0.0.1:{port}/healthz/ready", timeout=10) as ready:
            assert ready.status == 200
        sql("UPDATE public.rc2_lifecycle_marker SET value='changed-after-second-upgrade' WHERE id=1")
        second_rollback = call("honua_propose_rollback", {"targetId": "compose-local",
            "backupId": new_backup["backupId"], "idempotencyKey": "second-verified-backup"}, 6)
        assert second_rollback["proposalId"] != rollback["proposalId"]
        preview = wait_for_approval(base, approver_key, second_rollback["proposalId"])
        assert any(new_backup["backupId"] in line for line in preview["diff"])
        request(base, f"/api/v1/admin/proposals/{second_rollback['proposalId']}/approve", approver_key, {})
        restored_again = wait_for_proposal(base, approver_key, second_rollback["proposalId"], "succeeded", proposer_key, session)
        assert restored_again["executionOperationId"] not in restore_ids
        assert image_id() == prior_id and schema() == 140
        assert sql("SELECT value FROM public.rc2_lifecycle_marker WHERE id=1") == "after-first-rollback"
        assert sql("SELECT to_regclass('honua.ai_financial_invocations') IS NULL") == "t"
    finally:
        dc("down", "-v", "--remove-orphans", check=False)
        run(["docker", "run", "--rm", "-v", f"{project_dir}:{project_dir}", "redis:7.4-alpine", "chown", "-R", f"{os.getuid()}:{os.getgid()}", str(project_dir)], check=False)
        shutil.rmtree(project_dir, ignore_errors=True)
