#!/usr/bin/env python3
"""Exercise staged metadata release/recovery in a manifest-pinned installed image.

Only lane-created Docker resources are touched. SQL establishes/observes the fixture;
release submission and progression use the installed admin API, and feature/schema
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
        return run("docker", "exec", "-i", self.pg, "psql", "-XAt", "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", "honua", data=statement)

    def request(self, path: str, payload=None, admin=False, status=200):
        headers = {"Content-Type": "application/json"}
        if admin:
            headers["X-API-Key"] = self.password
        request = urllib.request.Request(self.base + path, data=None if payload is None else json.dumps(payload).encode(), headers=headers)
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                code, raw = response.status, response.read()
        except urllib.error.HTTPError as error:
            code, raw = error.code, error.read()
        if code != status:
            raise AssertionError(f"{path}: expected HTTP {status}, got {code}: {raw[:1000]!r}")
        return json.loads(raw) if raw else None

    def ready(self):
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
            run(*args, image)
            self.created.append(name)
        for _ in range(60):
            try:
                if self.sql("SELECT 1") == "1":
                    break
            except RuntimeError:
                pass
            time.sleep(1)
        # Event mode suppresses polling; supported operation reads drive exactly one stage.
        # The backstop remains configured, but its day-long interval cannot race this test.
        env = {
            "ASPNETCORE_ENVIRONMENT": "Development", "ASPNETCORE_URLS": "http://+:8080",
            "Kestrel__Endpoints__Http__Url": "http://+:8080", "PUBLIC_BASE_URL": "http://localhost:8080",
            "HONUA_ADMIN_PASSWORD": self.password, "Licensing__DevGrantEdition": "Enterprise",
            "ConnectionStrings__DefaultConnection": f"Host={self.pg};Database=honua;Username=postgres;Password={self.password}",
            "ConnectionStrings__Redis": self.redis + ":6379", "HostValidation__Enabled": "false",
            "Cache__Enabled": "false", "ControlPlane__TriggerMode": "Event",
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
        return json.loads(run("docker", "exec", self.redis, "redis-cli", "--raw", "GET", "controlplane:workflow:" + operation_id))

    def submit(self, label, **overrides):
        payload = {"packageId": "installed-" + label, "targetEnvironment": "staging", "resourceSemanticId": "res-cng-1000",
                   "newFieldName": "owner_email", "newFieldType": "String", "idempotencyKey": label}
        payload.update(overrides)
        response = self.request("/api/v1/admin/metadata/releases/operations", payload, admin=True, status=201)
        return response["operationId"]

    def advance(self, operation_id):
        self.request("/api/v1/admin/deploy/operations/" + operation_id, admin=True)
        return self.operation(operation_id)

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
        rows = data["features"]
        assert len(rows) == 6, data
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
        failed = self.submit("missing-resource", resourceSemanticId="missing-resource")
        result = self.terminal(failed)
        assert result["status"].lower() == "failed", result
        assert "metadata-release-resource-missing" in result["metadataRelease"]["blockers"], result
        assert self.current() == before
        self.receipt["scenarios"].append({"name": "preparation-failure", "status": "passed", "operation": result})

        operation_id = self.submit("staged-crash-recovery")
        staged = self.until(operation_id, "ServicePublication")
        release = staged["metadataRelease"]
        # A pre-#4663 candidate fails here: it has already changed the live graph.
        assert release.get("priorRevision") == before["revision"], "prior identity missing or captured after mutation"
        assert release["priorEtag"] == before["etag"]
        assert release["candidateRevision"] != before["revision"]
        assert self.current() == before, "staging exposed a partial live catalog"
        self.features()
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
        self.receipt["status"] = "passed"

    def finish(self):
        if self.server in self.created:
            logs = run("docker", "logs", self.server).replace(self.password, "[redacted]")
            (self.output / "server.log").write_text(logs)
        for name in reversed(self.created):
            run("docker", "rm", "-f", "-v", name)
        if self.created:
            run("docker", "network", "rm", self.prefix)
        self.receipt["cleanup"] = "removed lane containers and network"
        (self.output / "receipt.json").write_text(json.dumps(self.receipt, indent=2) + "\n")


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
