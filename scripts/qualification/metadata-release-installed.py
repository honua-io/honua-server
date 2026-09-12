#!/usr/bin/env python3
"""Exercise staged metadata release/recovery in a manifest-pinned installed image.

Only lane-created Docker resources are touched. SQL establishes/observes the fixture;
release submission uses the admin API and the installed worker advances stages; feature/schema
assertions use its public FeatureServer. No source-built server or mocked services.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import secrets
import subprocess
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
STATUSES = ("Planned", "AwaitingApproval", "Submitted", "Reconciling", "Succeeded", "Failed", "RollbackRequested", "RolledBack", "ManualInterventionRequired")
STAGES = ("Preflight", "Backup", "ScriptMigration", "MetadataApply", "ServicePublication", "Smoke", "SloWatch", "Promotion", "Complete", "Failed", "RollbackRequested")
TERMINAL = {"Succeeded", "Failed", "RolledBack", "ManualInterventionRequired"}
EXPECTED = {
    "Harbor City": (-122.4194, 37.7749, 1000000),
    "Baytown": (-122.2711, 37.8044, 430000),
    "Meridian Marker": (0.0, 51.4779, 0),
    "Equator Station": (-75.0, 0.0, 0),
    "Dateline Post": (179.5, 0.5, 0),
    "Polar Outpost": (0.0, 86.0, 0),
}


def run(*args: str, data: str | None = None) -> str:
    result = subprocess.run(args, input=data, text=True, capture_output=True, timeout=180)
    if result.returncode:
        raise RuntimeError(f"{args[0]} {args[1]} failed: {result.stderr[-2000:]}")
    return result.stdout.strip()


def pinned_server(manifest: Path) -> dict:
    text = manifest.read_text()
    block = re.search(r"^  honua-server:\n(.*?)(?=^  [\w-]+:|\Z)", text, re.M | re.S)
    if not block:
        raise ValueError("manifest has no honua-server component")
    values = {}
    for key in ("sha", "image", "digest"):
        match = re.search(rf'^    {key}:\s*"([^"\n]+)"', block[1], re.M)
        if not match:
            raise ValueError(f"manifest has no server {key}")
        values[key] = match[1]
    if not re.fullmatch(r"[0-9a-f]{40}", values["sha"]) or not re.fullmatch(r"sha256:[0-9a-f]{64}", values["digest"]):
        raise ValueError("candidate requires exact source SHA and OCI digest")
    values["reference"] = values["image"].rsplit(":", 1)[0] + "@" + values["digest"]
    return values


class Harness:
    def __init__(self, candidate: dict, output: Path):
        self.harness_hash = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
        self.candidate = candidate
        self.output = output
        self.prefix = "honua-4619-" + secrets.token_hex(4)
        self.pg, self.redis, self.server = (self.prefix + suffix for suffix in ("-pg", "-redis", "-server"))
        self.password = secrets.token_urlsafe(30)
        self.base = ""
        self.created = []
        self.receipt = {"schema": "honua.metadata-release-installed/v1", "candidate": candidate,
                        "createdAt": datetime.now(timezone.utc).isoformat(), "scenarios": [], "status": "failed"}

    def sql(self, statement: str) -> str:
        return run("docker", "exec", "-i", "-e", "PGPASSWORD=" + self.password, self.pg, "psql", "-h", "127.0.0.1", "-XAt", "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", "honua", data=statement)

    def request(self, path: str, payload=None, admin=False, status=200, method=None):
        headers = {"Content-Type": "application/json"}
        if admin:
            headers["X-API-Key"] = self.password
        request = urllib.request.Request(self.base + path, data=None if payload is None else json.dumps(payload).encode(), headers=headers, method=method)
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                code, raw = response.status, response.read()
        except urllib.error.HTTPError as error:
            code, raw = error.code, error.read()
        if code != status:
            raise AssertionError(f"{path}: expected HTTP {status}, got {code}: {raw[:1000]!r}")
        return json.loads(raw) if raw else None

    def ready(self):
        self.base = "http://" + run("docker", "port", self.server, "8080/tcp")
        deadline = time.monotonic() + 150
        while time.monotonic() < deadline:
            try:
                with urllib.request.urlopen(self.base + "/healthz/ready", timeout=3) as response:
                    if response.status == 200:
                        return
            except (OSError, urllib.error.URLError):
                pass
            time.sleep(1)
        raise AssertionError("installed candidate never became ready")

    def start(self):
        inspect = json.loads(run("docker", "image", "inspect", self.candidate["reference"]))[0]
        if inspect["Config"]["Labels"]["org.opencontainers.image.revision"] != self.candidate["sha"]:
            raise AssertionError("installed image source label does not match manifest")
        if self.candidate["reference"] not in inspect["RepoDigests"]:
            raise AssertionError("installed image digest does not match manifest")
        self.receipt["imageId"] = inspect["Id"]
        run("docker", "network", "create", self.prefix)
        for name, image, env in [
            (self.pg, "postgis/postgis:16-3.4", ["POSTGRES_DB=honua", "POSTGRES_PASSWORD=" + self.password]),
            (self.redis, "redis:7.2-alpine", []),
        ]:
            args = ["docker", "run", "-d", "--name", name, "--network", self.prefix]
            for entry in env:
                args += ["-e", entry]
            extra = ["redis-server", "--appendonly", "yes", "--appendfsync", "always"] if name == self.redis else []
            run(*args, image, *extra)
            self.created.append(name)
        for _ in range(60):
            try:
                if self.sql("SELECT 1") == "1":
                    break
            except RuntimeError:
                pass
            time.sleep(1)
        # Durable test-owned leases pause the real polling worker between stages.
        # Only the worker writes operation records; the harness never manufactures a stage.
        env = {
            "ASPNETCORE_ENVIRONMENT": "Development", "ASPNETCORE_URLS": "http://+:8080",
            "Kestrel__Endpoints__Http__Url": "http://+:8080", "PUBLIC_BASE_URL": "http://localhost:8080",
            "HONUA_ADMIN_PASSWORD": self.password, "Licensing__DevGrantEdition": "Enterprise",
            "ConnectionStrings__DefaultConnection": f"Host={self.pg};Database=honua;Username=postgres;Password={self.password}",
            "ConnectionStrings__Redis": self.redis + ":6379", "HostValidation__Enabled": "false",
            "Cache__Enabled": "false", "Metadata__Environment": "default", "ControlPlane__TriggerMode": "Poll",
            "ControlPlane__BackstopInterval": "1.00:00:00", "ControlPlane__StaleThreshold": "1.00:00:00",
            "ControlPlane__MetadataRelease__FaultInjection__Enabled": "true",
            "ControlPlane__MetadataRelease__FaultInjection__ForceSmokeFailure": "true",
            "Security__ConnectionEncryption__MasterKey": secrets.token_urlsafe(40),
        }
        args = ["docker", "run", "-d", "--name", self.server, "--network", self.prefix, "-p", "127.0.0.1::8080"]
        for key, value in env.items():
            args += ["-e", f"{key}={value}"]
        run(*args, self.candidate["reference"])
        self.created.append(self.server)
        self.base = "http://" + run("docker", "port", self.server, "8080/tcp")
        self.ready()
        self.sql("CREATE TABLE IF NOT EXISTS public.features (objectid bigserial PRIMARY KEY, layer_id int NOT NULL, geometry geometry, attributes jsonb, created_at timestamptz DEFAULT now(), updated_at timestamptz DEFAULT now());")
        self.sql((ROOT / "docker/cng/seed.sql").read_text())
        run("docker", "restart", self.server)
        self.ready()

    def current(self):
        return json.loads(self.sql("SELECT json_build_object('revision',c.revision,'etag',c.etag,'graph',s.document) FROM honua.metadata_v2_current c JOIN honua.metadata_v2_snapshots s USING(environment,revision) WHERE c.environment='default';"))

    def operation(self, operation_id):
        op = json.loads(run("docker", "exec", self.redis, "redis-cli", "--raw", "GET", "controlplane:workflow:" + operation_id))
        if isinstance(op["status"], int):
            op["status"] = STATUSES[op["status"]]
        release = op["metadataRelease"]
        if isinstance(release["currentStage"], int):
            release["currentStage"] = STAGES[release["currentStage"]]
        return op

    def submit(self, label, **overrides):
        payload = {"packageId": "installed-" + label, "targetEnvironment": "staging", "resourceSemanticId": "res-cng-1000",
                   "newFieldName": "owner_email", "newFieldType": "String", "idempotencyKey": label}
        payload.update(overrides)
        operation_id = "metadata-release-" + label
        self.hold(operation_id)
        response = self.request("/api/v1/admin/metadata/releases/operations", payload, admin=True, status=201)
        return response["operationId"]

    def hold(self, operation_id):
        deadline = time.monotonic() + 40
        while time.monotonic() < deadline:
            result = run("docker", "exec", self.redis, "redis-cli", "--raw", "SET",
                         "controlplane:workflow:lease:" + operation_id, self.prefix, "NX", "PX", "600000")
            if result == "OK":
                return
            time.sleep(0.05)
        raise AssertionError("could not pause worker at durable stage boundary")

    def advance(self, operation_id):
        before = self.operation(operation_id)
        key = "controlplane:workflow:lease:" + operation_id
        script = "if redis.call('GET',KEYS[1]) == ARGV[1] then return redis.call('DEL',KEYS[1]) else return 0 end"
        assert run("docker", "exec", self.redis, "redis-cli", "--raw", "EVAL", script, "1", key, self.prefix) == "1"
        deadline = time.monotonic() + 45
        while time.monotonic() < deadline:
            op = self.operation(operation_id)
            if op["version"] != before["version"]:
                self.hold(operation_id)
                return self.operation(operation_id)
            time.sleep(0.1)
        raise AssertionError(f"worker did not advance operation: {before}")

    def until(self, operation_id, stage):
        for _ in range(20):
            op = self.operation(operation_id)
            if op["metadataRelease"]["currentStage"].lower() == stage.lower():
                return op
            if op["status"].lower() in {s.lower() for s in TERMINAL}:
                raise AssertionError(f"expected {stage}, operation ended: {op}")
            self.advance(operation_id)
        raise AssertionError(f"operation did not reach {stage}")

    def terminal(self, operation_id):
        for _ in range(20):
            op = self.operation(operation_id)
            if op["status"].lower() in {s.lower() for s in TERMINAL}:
                return op
            self.advance(operation_id)
        raise AssertionError("operation did not terminate")

    def features(self, edited=False):
        data = self.request("/rest/services/cng/FeatureServer/1000/query?where=1%3D1&outFields=*&returnGeometry=true&f=json")
        assert "features" in data, data
        rows = data["features"]
        assert len(rows) == 6, data
        assert {row["attributes"]["name"] for row in rows} == set(EXPECTED), data
        assert data["spatialReference"]["wkid"] == 4326, data
        for row in rows:
            attrs, geom = row["attributes"], row["geometry"]
            x, y, population = EXPECTED[attrs["name"]]
            assert abs(geom["x"] - x) < 1e-8 and abs(geom["y"] - y) < 1e-8, row
            assert attrs["population"] == (1000007 if edited and attrs["name"] == "Harbor City" else population), row
        schema = self.request("/rest/services/cng/FeatureServer/1000?f=json")
        assert "owner_email" not in {field["name"] for field in schema["fields"]}, schema
        self.request("/api/v1/admin/metadata/releases/operations", {}, status=401)
        return {"rowCount": 6, "srid": 4326, "coordinatesAndValuesVerified": True,
                "newFieldAbsent": True, "anonymousAdminDenied": True}

    def exercise(self):
        self.start()
        self.receipt["baseline"] = self.features()
        before = self.current()

        operation_id = self.submit("staged-crash-recovery")
        staged = self.until(operation_id, "ServicePublication")
        release = staged["metadataRelease"]
        # A pre-#4663 candidate fails here: it has already changed the live graph.
        assert release.get("priorRevision") == before["revision"], "prior identity missing or captured after mutation"
        assert release["priorEtag"] == before["etag"]
        assert release["candidateRevision"] != before["revision"]
        assert self.current() == before, "staging exposed a partial live catalog"
        self.features()
        candidate = json.loads(self.sql(f"SELECT json_build_object('etag',etag,'graph',document) FROM honua.metadata_v2_snapshots WHERE environment='default' AND revision={int(release['candidateRevision'])}"))
        assert candidate["etag"] == release["candidateEtag"]
        assert "owner_email" in {f["name"] for f in candidate["graph"]["resources"][0]["schemaFields"]}
        assert self.sql(f"SELECT count(*) FROM honua.metadata_v2_resources_idx WHERE environment='default' AND revision={int(release['candidateRevision'])}") == "0"
        self.receipt["stagedOperation"] = staged
        run("docker", "kill", "--signal", "KILL", self.server)
        run("docker", "start", self.server)
        self.ready()
        assert self.operation(operation_id) == staged, "restart lost the durable operation"
        assert self.current() == before, "restart activated staged work"
        self.until(operation_id, "MetadataApply")
        assert self.current() == before, "candidate smoke activated the graph"
        activated = self.until(operation_id, "SloWatch")
        assert self.current()["revision"] == release["candidateRevision"], "candidate did not activate atomically"
        assert activated["metadataRelease"]["activatedAt"]
        # An independently specified committed edit must survive metadata-only recovery.
        self.sql("UPDATE public.features SET attributes=jsonb_set(attributes,'{population}','1000007') WHERE layer_id=1000 AND attributes->>'name'='Harbor City';")
        result = self.terminal(operation_id)
        assert result["status"].lower() == "rolledback", result
        assert self.current() == before, "rollback did not restore the immutable prior graph"
        kinds = {e["kind"] for e in result["metadataRelease"]["evidenceRefs"]}
        assert {"smoke-candidate", "smoke", "smoke-recovered"} <= kinds, kinds
        values = self.features(edited=True)
        self.receipt["scenarios"].append({"name": "staged-crash-post-activation-recovery", "status": "passed", "operation": result, "functionalAssertions": values})
        concurrent_id = self.submit("concurrent-services")
        pending = self.until(concurrent_id, "MetadataApply")
        old_candidate = pending["metadataRelease"]["candidateRevision"]
        self.request("/api/v1/admin/services/cng-stac/access-policy", {"allowAnonymous": False}, admin=True, method="PUT")
        foreign = self.current()
        rebased = self.advance(concurrent_id)
        assert rebased["metadataRelease"]["rebaseCount"] == 1, rebased
        assert rebased["metadataRelease"]["priorEtag"] == foreign["etag"]
        assert self.current() == foreign, "ETag conflict overwrote the other service"
        assert self.sql(f"SELECT count(*) FROM honua.metadata_v2_snapshots WHERE environment='default' AND revision={int(old_candidate)}") == "0"
        self.until(concurrent_id, "SloWatch")
        # A second unrelated update after activation forces owned-only inverse recovery.
        self.request("/api/v1/admin/services/cng-stac/access-policy", {"allowedRoles": ["recovery-reviewer"]}, admin=True, method="PUT")
        later = self.current()
        recovered = self.terminal(concurrent_id)
        assert recovered["status"] == "RolledBack", recovered
        graph = self.current()["graph"]
        assert graph["services"] == later["graph"]["services"], "rollback discarded an unrelated service update"
        fields = {f["name"]: f["type"] for f in graph["resources"][0]["schemaFields"]}
        assert fields == {"objectid": "integer", "name": "string", "category": "string", "population": "integer",
                          "ratio": "double", "active": "boolean", "observed_at": "datetime", "geometry": "geometry"}, fields
        self.receipt["scenarios"].append({"name": "etag-rebase-and-owned-only-recovery", "status": "passed", "operation": recovered,
                                         "functionalAssertions": self.features(edited=True)})
        before = self.current()
        failed = self.submit("missing-resource", resourceSemanticId="missing-resource")
        result = self.terminal(failed)
        assert result["status"].lower() == "failed", result
        assert "metadata-release-resource-missing" in result["metadataRelease"]["blockers"], result
        assert self.current() == before
        self.receipt["scenarios"].append({"name": "preparation-failure", "status": "passed", "operation": result})

        for label, extra, blocker in [
            ("unsafe-etl-rejected", {"dataPopulateWorkloadId": "unregistered-workload", "dataPopulateFields": ["population"]}, "metadata-release-etl-unproven-compensation"),
            ("etl-failure-cleans-stage", {"dataPopulateWorkloadId": "unregistered-workload", "dataPopulateFields": ["owner_email"]}, "metadata-release-etl-failed"),
        ]:
            before = self.current()
            operation_id = self.submit(label, **extra)
            result = self.terminal(operation_id)
            assert result["status"] == "Failed", result
            assert blocker in result["metadataRelease"]["blockers"], result
            assert self.current() == before, "failed preparation changed the live graph"
            candidate_revision = result["metadataRelease"].get("candidateRevision")
            if candidate_revision is not None:
                assert self.sql(f"SELECT count(*) FROM honua.metadata_v2_snapshots WHERE environment='default' AND revision={int(candidate_revision)}") == "0"
            self.receipt["scenarios"].append({"name": label, "status": "passed", "operation": result,
                                             "functionalAssertions": self.features(edited=True)})
        self.receipt["status"] = "passed"

    def finish(self):
        cleanup_errors = []
        try:
            if self.server in self.created:
                logs = run("docker", "logs", self.server).replace(self.password, "[redacted]")
                (self.output / "server.log").write_text(logs)
                self.receipt["serverLogSha256"] = hashlib.sha256(logs.encode()).hexdigest()
        except Exception as error:
            cleanup_errors.append(str(error))
        for name in reversed(self.created):
            try:
                run("docker", "rm", "-f", "-v", name)
            except Exception as error:
                cleanup_errors.append(str(error))
        try:
            run("docker", "network", "rm", self.prefix)
        except Exception as error:
            if self.created:
                cleanup_errors.append(str(error))
        self.receipt["cleanup"] = cleanup_errors or "removed lane containers and network"
        self.receipt["harnessSha256"] = self.harness_hash
        if cleanup_errors:
            self.receipt["status"] = "failed"
        (self.output / "receipt.json").write_text(json.dumps(self.receipt, indent=2) + "\n")
        if cleanup_errors:
            raise RuntimeError("proof cleanup failed: " + "; ".join(cleanup_errors))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    candidate = pinned_server(args.manifest)
    candidate["manifestSha256"] = hashlib.sha256(args.manifest.read_bytes()).hexdigest()
    harness = Harness(candidate, args.output)
    try:
        harness.exercise()
    except Exception as error:
        harness.receipt["error"] = str(error).replace(harness.password, "[redacted]")
        raise
    finally:
        harness.finish()


if __name__ == "__main__":
    main()
