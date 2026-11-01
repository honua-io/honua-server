# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License.
"""Published rc.1 -> approved candidate -> verified restore, using the product MCP path."""
import glob
import hashlib
import json
import os
import re
import secrets
import socket
import subprocess
import tempfile
import shutil
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
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
    (project_dir / "secrets").mkdir()
    (project_dir / "backups").mkdir()
    quickstart = (ROOT / "docs/get-started/quickstart.md").read_text()
    compose_text = re.search(r"<!-- docs-validation:install.compose mode=run -->\s*```yaml\n(.*?)\n```", quickstart, re.S).group(1)
    (project_dir / "compose.yaml").write_text(compose_text)
    (project_dir / "compose.executor.yaml").write_text((ROOT / "docs/get-started/compose.executor.yaml").read_text())
    key = "Aa1!" + secrets.token_hex(24)
    postgres = secrets.token_hex(24)
    keyring = secrets.token_hex(16)
    run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-subj", "/CN=honua-test", "-keyout", str(project_dir / "secrets/key.pem"), "-out", str(project_dir / "secrets/cert.pem"), "-days", "1"])
    run(["openssl", "pkcs12", "-export", "-inkey", str(project_dir / "secrets/key.pem"), "-in", str(project_dir / "secrets/cert.pem"), "-out", str(project_dir / "secrets/keyring.pfx"), "-passout", "pass:" + keyring])
    plugins = glob.glob("/usr/libexec/docker/cli-plugins/docker-compose") + glob.glob("/usr/lib/docker/cli-plugins/docker-compose") + glob.glob("/usr/local/lib/docker/cli-plugins/docker-compose")
    assert plugins, "Docker Compose plugin executable is required"
    env = os.environ.copy()
    env.update({"COMPOSE_PROJECT_NAME": project, "HONUA_IMAGE": RC1, "HONUA_HTTP_PORT": str(port),
        "HONUA_GRPC_PORT": str(free_port()), "HONUA_MANAGER_PORT": str(manager_port), "POSTGRES_PASSWORD": postgres,
        "HONUA_ADMIN_PASSWORD": key, "HONUA_MASTER_KEY": secrets.token_hex(32), "HONUA_KEYRING_PASSWORD": keyring,
        "HONUA_RELEASE_IMAGE": candidate, "HONUA_RELEASE_WORKER_IMAGE": candidate, "HONUA_RELEASE_VERSION": "2026.1-rc.2", "HONUA_PRIOR_IMAGE": RC1,
        "HONUA_PRIOR_MAX_SCHEMA": "140", "DOCKER_GID": str(os.stat("/var/run/docker.sock").st_gid),
        "DOCKER_COMPOSE_PLUGIN": plugins[0], "HONUA_COMPOSE_PROJECT_DIRECTORY": str(project_dir),
        "HONUA_COMPOSE_CUSTOMER_PROBE": "http://honua:8080/healthz/ready"})
    # The executor invokes Compose in its own container. Preserve the fixture's
    # interpolation values beside compose.yaml, as a real quickstart install does.
    fixture_keys = ["COMPOSE_PROJECT_NAME", "HONUA_IMAGE", "HONUA_HTTP_PORT", "HONUA_GRPC_PORT", "HONUA_MANAGER_PORT",
        "POSTGRES_PASSWORD", "HONUA_ADMIN_PASSWORD", "HONUA_MASTER_KEY", "HONUA_KEYRING_PASSWORD", "HONUA_RELEASE_IMAGE",
        "HONUA_RELEASE_VERSION", "HONUA_RELEASE_WORKER_IMAGE", "HONUA_PRIOR_IMAGE", "HONUA_PRIOR_MAX_SCHEMA", "DOCKER_GID", "DOCKER_COMPOSE_PLUGIN",
        "HONUA_COMPOSE_PROJECT_DIRECTORY", "HONUA_COMPOSE_CUSTOMER_PROBE"]
    (project_dir / ".env").write_text("".join(f"{name}={env[name]}\n" for name in fixture_keys))
    (project_dir / ".env").chmod(0o600)
    compose = ["docker", "compose", "--project-directory", str(project_dir), "-p", project,
        "-f", str(project_dir / "compose.yaml"), "-f", str(project_dir / "compose.executor.yaml")]
    # The executor runs as uid 1001 and owns only this disposable fixture directory.
    run(["docker", "run", "--rm", "-v", f"{project_dir}:{project_dir}", "redis:7.4-alpine", "chown", "-R", "1001:1001", str(project_dir)])
    run(["docker", "run", "--rm", "-v", f"{project_dir}:{project_dir}", "redis:7.4-alpine", "chown", f"{os.getuid()}:{env['DOCKER_GID']}", str(project_dir / ".env")])
    run(["docker", "run", "--rm", "-v", f"{project_dir}:{project_dir}", "redis:7.4-alpine", "chmod", "640", str(project_dir / ".env")])
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
            for secret in (key, postgres, keyring, env["HONUA_MASTER_KEY"]):
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
                pass
            time.sleep(2)
        else:
            raise AssertionError("serving stack did not recover after Redis restarted")
        convergence = call("honua_propose_platform_release_convergence", {
            "reason": "candidate convergence after failed operation", "idempotencyKey": "recovered-convergence"}, 2)
        proposal = convergence["targets"][0]["proposalId"]
        assert proposal and image_id() == prior_id and schema() == 140
        try:
            request(base, f"/api/v1/admin/proposals/{proposal}/approve", proposer_key, {})
            raise AssertionError("self approval succeeded")
        except urllib.error.HTTPError as error:
            assert error.code in (400, 403, 409)
        request(base, f"/api/v1/admin/proposals/{proposal}/approve", approver_key, {})
        wait_for_proposal(base, approver_key, proposal, "succeeded", proposer_key, session)
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
            request(base, f"/api/v1/admin/proposals/{wrong['proposalId']}/approve", approver_key, {})
            wait_for_proposal(base, approver_key, wrong["proposalId"], "failed", proposer_key, session)
        else:
            assert wrong.get("outcome", "").lower() in ("blocked", "rejected", "failed")
        assert image_id() == candidate and schema() == candidate_schema
        assert sql("SELECT value FROM public.rc2_lifecycle_marker WHERE id=1") == "changed-after-upgrade"
        rollback = call("honua_propose_rollback", {"targetId": "compose-local",
            "idempotencyKey": "verified-backup"}, 4)
        assert rollback["proposalId"]
        preview, _ = request(base, f"/api/v1/admin/proposals/{rollback['proposalId']}", approver_key)
        assert any(prior_id in line for line in preview["diff"])
        assert any(backup["backupId"] in line for line in preview["diff"])
        assert any(backup["backupId"] in line for line in preview["dryRun"])
        assert image_id() == candidate and schema() == candidate_schema
        request(base, f"/api/v1/admin/proposals/{rollback['proposalId']}/approve", approver_key, {})
        wait_for_proposal(base, approver_key, rollback["proposalId"], "succeeded", proposer_key, session)
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
    finally:
        dc("down", "-v", "--remove-orphans", check=False)
        run(["docker", "run", "--rm", "-v", f"{project_dir}:{project_dir}", "redis:7.4-alpine", "chown", "-R", f"{os.getuid()}:{os.getgid()}", str(project_dir)], check=False)
        shutil.rmtree(project_dir, ignore_errors=True)
