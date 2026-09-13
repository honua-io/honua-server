"""Qualify layer bounds on an exact candidate in a disposable, constrained deployment.

Uses production OGC execution, PostGIS layer reads and Redis jobs. Run with Python 3,
Docker Compose, --image registry/image@sha256:digest and --receipt /path/receipt.json.
"""

import argparse
import base64
import hashlib
import json
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.request
import uuid
from datetime import datetime, timezone
from pathlib import Path


def utc():
    return datetime.now(timezone.utc).isoformat()


def main():
    if not __debug__:
        raise RuntimeError("Qualification requires Python assertions; do not use optimization flags")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--image", required=True)
    parser.add_argument("--receipt", required=True)
    parser.add_argument("--port", type=int, default=18459)
    args = parser.parse_args()
    assert "@sha256:" in args.image, "Use the manifest-pinned image digest"
    repo = Path(subprocess.check_output(["git", "rev-parse", "--show-toplevel"], text=True).strip())
    receipt_path = Path(args.receipt).resolve()
    receipt_path.parent.mkdir(parents=True, exist_ok=True)
    project = "gp-bounds-proof-" + uuid.uuid4().hex[:10]
    receipt = {"schema": "honua.layer-resource-proof.v1", "outcome": "fail", "startedAt": utc(),
               "image": args.image, "scenarios": [], "serving": [],
               "harnessSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest()}
    stop = threading.Event()
    monitor = None
    base = f"http://127.0.0.1:{args.port}"

    def api(path, body=None):
        request = urllib.request.Request(base + path,
            data=None if body is None else json.dumps(body).encode(),
            headers={"Content-Type": "application/json", "X-API-Key": "gp-bounds-local",
                     "Prefer": "respond-async"})
        try:
            with urllib.request.urlopen(request, timeout=10) as response:
                return json.load(response)
        except urllib.error.HTTPError as error:
            raise AssertionError(f"{request.method} {path}: {error.code} {error.read().decode()}") from error

    def readiness():
        for _ in range(120):
            try:
                request = urllib.request.Request(base + "/healthz/ready", headers={"X-API-Key": "gp-bounds-local"})
                with urllib.request.urlopen(request, timeout=2) as response:
                    if response.status == 200:
                        return
            except (OSError, urllib.error.URLError):
                # Expected while the listener and health checks start; the bounded loop fails closed below.
                pass
            time.sleep(1)
        raise AssertionError("candidate readiness timed out")

    def observe_serving():
        while not stop.is_set():
            started = time.monotonic()
            observation = {"at": utc()}
            try:
                result = api("/rest/services/gp-bounds/FeatureServer/946290/query?where=1%3D1&returnCountOnly=true&f=json")
                observation.update(outcome="pass" if result.get("count") == 2 else "fail", response=result)
            except Exception as error:
                observation.update(outcome="fail", error=str(error))
            observation["seconds"] = time.monotonic() - started
            receipt["serving"].append(observation)
            stop.wait(0.2)

    with tempfile.TemporaryDirectory(prefix="gp-bounds-compose-") as temporary:
        compose_path = Path(temporary) / "compose.json"
        services = {
            "postgres": {"image": "postgis/postgis:18-3.6", "environment": {
                "POSTGRES_DB": "honua", "POSTGRES_USER": "honua", "POSTGRES_PASSWORD": "gp-bounds-local"},
                "volumes": [f"{repo}/docker/init-db.sql:/docker-entrypoint-initdb.d/init-db.sql:ro"],
                "healthcheck": {"test": ["CMD-SHELL", "pg_isready -h 127.0.0.1 -U honua -d honua"], "interval": "2s", "retries": 30}},
            "redis": {"image": "redis:7.2-alpine", "command": ["redis-server", "--appendonly", "yes", "--appendfsync", "always"]},
            "server": {"image": args.image, "mem_limit": "1g", "cpus": 1,
                "ports": [f"127.0.0.1:{args.port}:8080"], "environment": {
                    "ASPNETCORE_ENVIRONMENT": "Development", "HONUA_ADMIN_PASSWORD": "gp-bounds-local", "Database__Schema": "honua",
                    "ConnectionStrings__DefaultConnection": "Host=postgres;Database=honua;Username=honua;Password=gp-bounds-local",
                    "ConnectionStrings__Redis": "redis:6379", "Licensing__Mode": "Disabled", "Licensing__DevGrantEdition": "Pro",
                    "Security__ConnectionEncryption__MasterKey": "gp-bounds-local-master-key-0123456789",
                    "Security__ConnectionEncryption__Salt": "aG9udWEtZ3AtcmVsaWFiaWxpdHktc2FsdA==",
                    "Kestrel__Endpoints__Http__Url": "http://+:8080", "Kestrel__Endpoints__Http__Protocols": "Http1",
                    "Geoprocessing__Executors__MaxTopologyWork": "99", "Geoprocessing__Executors__MaxLayerVertices": "100",
                    "Geoprocessing__Executors__MaxArtifactBytes": "32768", "Geoprocessing__Executors__MaxLayerExecutionSeconds": "10",
                    "Limits__Analytics__MaxInputFeatures": "100", "Limits__Analytics__MaxInputBytes": "1048576",
                    "Limits__Geometry__MaxVerticesPerGeometry": "100"},
                "depends_on": {"postgres": {"condition": "service_healthy"}, "redis": {"condition": "service_started"}}}}
        compose_path.write_text(json.dumps({"services": services}))
        command = ["docker", "compose", "-p", project, "-f", str(compose_path)]

        def compose(*arguments, **kwargs):
            return subprocess.run([*command, *arguments], check=True, **kwargs)

        try:
            compose("up", "-d")
            readiness()
            # Literal squares have area 4 each and overlap area 1; union area is 7.
            # The third layer deliberately has 1001 vertices in ONE geometry.
            seed = """
INSERT INTO honua.services(service_name) VALUES ('gp-bounds');
CREATE TABLE honua.features(objectid BIGSERIAL PRIMARY KEY, layer_id int NOT NULL, geometry geometry(Polygon,4326), attributes jsonb);
INSERT INTO honua.layers(layer_id,layer_name,table_schema,table_name,geometry_type,srid)
VALUES (946290,'boxes','honua','features','Polygon',4326),
       (946291,'large-ring','honua','features','Polygon',4326),
       (946292,'join-boxes','honua','features','Polygon',4326);
INSERT INTO honua.service_layers(service_name,layer_id,layer_order)
VALUES ('gp-bounds',946290,0),('gp-bounds',946291,1),('gp-bounds',946292,2);
INSERT INTO honua.layer_fields(layer_id,field_name,field_type,field_order)
VALUES (946290,'objectid','Oid',0),(946291,'objectid','Oid',0),(946292,'objectid','Oid',0);
INSERT INTO honua.features(layer_id,geometry,attributes) VALUES
(946290,ST_GeomFromText('POLYGON((0 0,2 0,2 2,0 2,0 0))',4326),'{}'),
(946290,ST_GeomFromText('POLYGON((1 1,3 1,3 3,1 3,1 1))',4326),'{}');
INSERT INTO honua.features(layer_id,geometry,attributes) VALUES
(946291,ST_Buffer(ST_SetSRID(ST_Point(0,0),4326),1,'quad_segs=250'),'{}');
INSERT INTO honua.features(layer_id,geometry,attributes) SELECT 946292,geometry,attributes FROM honua.features WHERE layer_id=946290;
UPDATE honua.layers SET extent=ST_MakeEnvelope(-1,-1,3,3,4326) WHERE layer_id IN (946290,946291,946292);
UPDATE honua.services SET service_extent=ST_MakeEnvelope(-1,-1,3,3,4326) WHERE service_name='gp-bounds';
"""
            compose("exec", "-T", "postgres", "psql", "-U", "honua", "-d", "honua", "-v", "ON_ERROR_STOP=1", input=seed, text=True)
            # Publish the catalog using the repository's canonical metadata fixture.
            base_seed = (repo / "tests/seed/base-schema.sql").read_text()
            function_start = base_seed.index("CREATE OR REPLACE FUNCTION honua.seed_metadata_v2_compat_snapshot()")
            function_end = base_seed.index("\n$$;", function_start) + len("\n$$;")
            metadata_seed = base_seed[function_start:function_end] + "\nSELECT honua.seed_metadata_v2_compat_snapshot();\n"
            compose("exec", "-T", "postgres", "psql", "-U", "honua", "-d", "honua", "-v", "ON_ERROR_STOP=1", input=metadata_seed, text=True)
            # No jobs exist yet. Drop only this disposable deployment's empty startup cache.
            compose("exec", "-T", "redis", "redis-cli", "FLUSHDB")
            compose("restart", "server")
            readiness()
            container = compose("ps", "-q", "server", capture_output=True, text=True).stdout.strip()
            observed = json.loads(subprocess.check_output(["docker", "inspect", container]))[0]
            image = json.loads(subprocess.check_output(["docker", "image", "inspect", observed["Image"]]))[0]
            assert observed["Config"]["Image"] == args.image and args.image in image["RepoDigests"]
            receipt["candidate"] = {"containerId": container, "imageId": observed["Image"],
                "labels": image["Config"].get("Labels"), "memoryLimitBytes": observed["HostConfig"]["Memory"],
                "nanoCpus": observed["HostConfig"]["NanoCpus"], "budgets": {
                    key: value for key, value in services["server"]["environment"].items()
                    if key.startswith(("Limits__", "Geoprocessing__"))}}
            # Verify serving before making any resource-outcome claims.
            receipt["catalog"] = api("/rest/services?f=json")
            serving = api("/rest/services/gp-bounds/FeatureServer/946290/query?where=1%3D1&returnCountOnly=true&f=json")
            assert serving.get("count") == 2, serving
            monitor = threading.Thread(target=observe_serving, daemon=True)
            monitor.start()
            scenarios = [
                ("bounded-passthrough", "generalization.dissolve", {"layerId": 946290, "dissolve": False}, None),
                ("dissolve-work-limit", "generalization.dissolve", {"layerId": 946290}, "MaxTopologyWork"),
                ("join-both-sides", "analytics.spatial-join", {"layerId": 946290, "joinLayerId": 946292}, "MaxTopologyWork"),
                ("buffer-work-limit", "analytics.buffer-aggregate", {"layerId": 946290, "distance": 1}, "MaxTopologyWork"),
                ("single-geometry", "generalization.dissolve", {"layerId": 946291}, "vertices"),
            ]
            for name, process, inputs, expected in scenarios:
                scenario = {"name": name, "startedAt": utc(), "outcome": "fail"}
                receipt["scenarios"].append(scenario)
                try:
                    job = api(f"/ogc/processes/processes/{process}/execution", {"inputs": inputs})
                    job_id = job.get("jobID", job.get("jobId"))
                    assert job_id, job
                    # Retain production retry/backoff behavior while requiring terminal failure.
                    deadline = time.monotonic() + 180
                    while True:
                        job = api("/ogc/processes/jobs/" + job_id)
                        if job["status"] in ["successful", "failed", "dismissed"] or time.monotonic() >= deadline:
                            break
                        time.sleep(0.2)
                    scenario["job"] = job
                    if expected is None:
                        assert job["status"] == "successful", job
                        artifacts = api(f"/api/v1/admin/jobs/{job_id}/artifacts")
                        artifacts = artifacts.get("data", artifacts)
                        refs = [item["artifactId"] for item in artifacts["items"]]
                        assert len(refs) == 1 and refs[0].startswith("data:application/geo+json;base64,"), artifacts
                        payload = base64.b64decode(refs[0].split(",", 1)[1], validate=True)
                        document = json.loads(payload)
                        assert document["featureCount"] == 2 and document["processId"] == process
                        geometries = [feature["geometry"] for feature in document["features"]]
                        assert geometries == [
                            {"type": "Polygon", "coordinates": [[[0, 0], [2, 0], [2, 2], [0, 2], [0, 0]]]},
                            {"type": "Polygon", "coordinates": [[[1, 1], [3, 1], [3, 3], [1, 3], [1, 1]]]}], geometries
                        scenario["outputSha256"] = hashlib.sha256(payload).hexdigest()
                        scenario["outcome"] = "pass"
                        scenario["completedAt"] = utc()
                        continue
                    assert job["status"] == "failed", job
                    assert expected in json.dumps(job), job
                    scenario["outcome"] = "pass"
                except Exception as error:
                    scenario["error"] = str(error)
                scenario["completedAt"] = utc()
            stop.set()
            monitor.join(timeout=12)
            receipt["finalContainerState"] = json.loads(subprocess.check_output(["docker", "inspect", container]))[0]["State"]
            assert receipt["serving"] and all(o["outcome"] == "pass" for o in receipt["serving"])
            assert not receipt["finalContainerState"]["OOMKilled"]
            assert all(s["outcome"] == "pass" for s in receipt["scenarios"]), "candidate failed resource-bound qualification"
            receipt["outcome"] = "pass"
        except Exception as error:
            receipt["error"] = str(error)
        finally:
            stop.set()
            if monitor:
                monitor.join(timeout=12)
            with receipt_path.with_suffix(".server.log").open("w") as log:
                subprocess.run([*command, "logs", "--no-color"], stdout=log, stderr=subprocess.STDOUT, check=False)
            cleanup = subprocess.run([*command, "down", "--volumes", "--remove-orphans"], check=False)
            receipt["cleanup"] = {"outcome": "pass" if cleanup.returncode == 0 else "fail"}
            if cleanup.returncode:
                receipt["outcome"] = "fail"
            receipt["completedAt"] = utc()
            observations = receipt.pop("serving")
            receipt["serving"] = {"samples": len(observations),
                "failures": [o for o in observations if o["outcome"] != "pass"],
                "maxSeconds": max((o["seconds"] for o in observations), default=None),
                "first": observations[0] if observations else None,
                "last": observations[-1] if observations else None}
            receipt_path.write_text(json.dumps(receipt, indent=2) + "\n")
    return 0 if receipt["outcome"] == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
