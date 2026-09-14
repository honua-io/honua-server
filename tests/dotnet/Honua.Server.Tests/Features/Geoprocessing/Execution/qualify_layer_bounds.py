"""Qualify layer bounds on an exact candidate in a disposable, constrained deployment.

Uses production OGC execution, PostGIS layer reads and Redis jobs. Run with Python 3,
Docker Compose, --image registry/image@sha256:digest and --receipt /path/receipt.json.
"""

import argparse
import base64
import hashlib
import json
import re
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.request
import uuid
from datetime import datetime, timezone
from pathlib import Path


DEADLINE_SECONDS = 8
# Slack over the deadline for one attempt: the executor observes cancellation between
# managed predicate calls, which take microseconds here, plus log-line emission.
DEADLINE_SLACK_SECONDS = 4
IDLE_CPU_PERCENT = 50.0
# Dismissal, including the DELETE request itself, must stop the running join within this bound.
DISMISS_BOUND_SECONDS = 10
# JobRetryPolicy.Default schedules the first retry 30 seconds after a failed attempt; the
# dismissed job is observed past that point before asserting it ran only once.
FIRST_RETRY_BACKOFF_SECONDS = 30
RETRY_OBSERVATION_MARGIN_SECONDS = 15
# The dismissal profile's elapsed-time limit stays far beyond the dismissal bound and the
# retry observation window, so only dismissal can stop the join in that scenario.
DISMISS_PROFILE_DEADLINE_SECONDS = 300


def utc():
    return datetime.now(timezone.utc).isoformat()


def parse_log_time(value):
    # Docker emits nanoseconds; datetime accepts microseconds.
    trimmed = re.sub(r"(\.\d{6})\d*Z$", r"\1+00:00", value)
    return datetime.fromisoformat(trimmed.replace("Z", "+00:00"))


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

    def api(path, body=None, method=None):
        request = urllib.request.Request(base + path,
            data=None if body is None else json.dumps(body).encode(), method=method,
            headers={"Content-Type": "application/json", "X-API-Key": "gp-bounds-local",
                     "Prefer": "respond-async"})
        try:
            # Dismissal may wait for the worker's confirmation; every other call keeps the serving bound.
            with urllib.request.urlopen(request, timeout=40 if method == "DELETE" else 10) as response:
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

    def start_monitor():
        stop.clear()
        thread = threading.Thread(target=observe_serving, daemon=True)
        thread.start()
        return thread

    def stop_monitor(thread):
        stop.set()
        if thread:
            thread.join(timeout=45)

    def submit(process, inputs):
        job = api(f"/ogc/processes/processes/{process}/execution", {"inputs": inputs})
        job_id = job.get("jobID", job.get("jobId"))
        assert job_id, job
        return job_id

    def wait_terminal(job_id, seconds=180):
        # Retain production retry/backoff behavior while requiring a terminal state.
        deadline = time.monotonic() + seconds
        while True:
            job = api("/ogc/processes/jobs/" + job_id)
            if job["status"] in ["successful", "failed", "dismissed"] or time.monotonic() >= deadline:
                return job
            time.sleep(0.2)

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
        # The deadline profile admits a join whose unbounded managed work far exceeds the deadline:
        # 6,000 x 6,000 overlapping five-vertex diamonds is 36 million exact predicate calls, and
        # 30,000 x 30,000 vertices stays inside MaxTopologyWork. Only the elapsed-time bound stops it.
        deadline_budgets = {
            "Geoprocessing__Executors__MaxTopologyWork": "1000000000", "Geoprocessing__Executors__MaxLayerVertices": "100000",
            "Geoprocessing__Executors__MaxArtifactBytes": "16777216",
            "Geoprocessing__Executors__MaxLayerExecutionSeconds": str(DEADLINE_SECONDS),
            "Limits__Analytics__MaxInputFeatures": "10000", "Limits__Analytics__MaxInputBytes": "16777216"}
        compose_path.write_text(json.dumps({"services": services}))
        command = ["docker", "compose", "-p", project, "-f", str(compose_path)]

        def compose(*arguments, **kwargs):
            return subprocess.run([*command, *arguments], check=True, **kwargs)

        def verify_candidate():
            container = compose("ps", "-q", "server", capture_output=True, text=True).stdout.strip()
            observed = json.loads(subprocess.check_output(["docker", "inspect", container]))[0]
            image = json.loads(subprocess.check_output(["docker", "image", "inspect", observed["Image"]]))[0]
            assert observed["Config"]["Image"] == args.image and args.image in image["RepoDigests"]
            configured = dict(item.split("=", 1) for item in observed["Config"]["Env"])
            budgets = {key: value for key, value in services["server"]["environment"].items()
                       if key.startswith(("Limits__", "Geoprocessing__"))}
            assert all(configured.get(key) == value for key, value in budgets.items()), (budgets, configured)
            return container, {"containerId": container, "imageId": observed["Image"],
                "labels": image["Config"].get("Labels"), "memoryLimitBytes": observed["HostConfig"]["Memory"],
                "nanoCpus": observed["HostConfig"]["NanoCpus"], "budgets": budgets}

        def job_events(job_id):
            logs = compose("logs", "--no-color", "--timestamps", "server", capture_output=True, text=True).stdout
            events = []
            for line in logs.splitlines():
                match = re.search(r"(\d{4}-\d\d-\d\dT\S+Z) \[\d\d:\d\d:\d\d \w+\] (Job execution started|Job executor returned failure|Job requeued|Job claimed): " + re.escape(job_id) + r"\b", line)
                if match:
                    events.append({"at": match.group(1), "event": match.group(2)})
            return events

        def cpu_percent(container):
            output = subprocess.check_output(["docker", "stats", "--no-stream", "--format", "{{.CPUPerc}}", container], text=True)
            return float(output.strip().rstrip("%"))

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
       (946292,'join-boxes','honua','features','Polygon',4326),
       (946293,'deadline-targets','honua','features','Polygon',4326),
       (946294,'deadline-join','honua','features','Polygon',4326);
INSERT INTO honua.service_layers(service_name,layer_id,layer_order)
VALUES ('gp-bounds',946290,0),('gp-bounds',946291,1),('gp-bounds',946292,2),('gp-bounds',946293,3),('gp-bounds',946294,4);
INSERT INTO honua.layer_fields(layer_id,field_name,field_type,field_order)
VALUES (946290,'objectid','Oid',0),(946291,'objectid','Oid',0),(946292,'objectid','Oid',0),(946293,'objectid','Oid',0),(946294,'objectid','Oid',0);
INSERT INTO honua.features(layer_id,geometry,attributes) VALUES
(946290,ST_GeomFromText('POLYGON((0 0,2 0,2 2,0 2,0 0))',4326),'{}'),
(946290,ST_GeomFromText('POLYGON((1 1,3 1,3 3,1 3,1 1))',4326),'{}');
INSERT INTO honua.features(layer_id,geometry,attributes) VALUES
(946291,ST_Buffer(ST_SetSRID(ST_Point(0,0),4326),1,'quad_segs=250'),'{}');
INSERT INTO honua.features(layer_id,geometry,attributes) SELECT 946292,geometry,attributes FROM honua.features WHERE layer_id=946290;
INSERT INTO honua.features(layer_id,geometry,attributes)
SELECT layer, ST_GeomFromText(format('POLYGON((%s %s,%s %s,%s %s,%s %s,%s %s))',
         x-1, y, x, y+1, x+1, y, x, y-1, x-1, y), 4326), '{}'
FROM (SELECT layer, (i % 100) * 0.001 + (layer - 946293) * 0.0005 AS x, (i / 100) * 0.001 AS y
      FROM generate_series(0, 5999) AS i CROSS JOIN (VALUES (946293), (946294)) AS layers(layer)) AS grid;
UPDATE honua.layers SET extent=ST_MakeEnvelope(-1,-1,3,3,4326) WHERE layer_id IN (946290,946291,946292,946293,946294);
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
            _, receipt["candidate"] = verify_candidate()
            # Verify serving before making any resource-outcome claims.
            receipt["catalog"] = api("/rest/services?f=json")
            serving = api("/rest/services/gp-bounds/FeatureServer/946290/query?where=1%3D1&returnCountOnly=true&f=json")
            assert serving.get("count") == 2, serving
            monitor = start_monitor()
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
                    job_id = submit(process, inputs)
                    job = wait_terminal(job_id)
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
                    # A deterministic refusal is terminal: exactly one attempt, no retry backoff.
                    scenario["events"] = job_events(job_id)
                    attempts = [event for event in scenario["events"] if event["event"] == "Job execution started"]
                    assert len(attempts) == 1, scenario["events"]
                    scenario["outcome"] = "pass"
                except Exception as error:
                    scenario["error"] = str(error)
                scenario["completedAt"] = utc()

            # Deadline profile: recreate the server from configuration with budgets that admit an
            # expensive join, so only the elapsed-time bound and dismissal can stop the work.
            stop_monitor(monitor)
            monitor = None
            services["server"]["environment"].update(deadline_budgets)
            compose_path.write_text(json.dumps({"services": services}))
            compose("up", "-d", "--no-deps", "server")
            readiness()
            container, receipt["deadlineCandidate"] = verify_candidate()
            monitor = start_monitor()
            join_inputs = {"layerId": 946293, "joinLayerId": 946294}

            scenario = {"name": "elapsed-time-limit", "startedAt": utc(), "outcome": "fail"}
            receipt["scenarios"].append(scenario)
            try:
                job_id = submit("analytics.spatial-join", join_inputs)
                job = wait_terminal(job_id, seconds=300)
                scenario["job"] = job
                scenario["events"] = job_events(job_id)
                assert job["status"] == "failed", job
                assert f"MaxLayerExecutionSeconds={DEADLINE_SECONDS}" in job["message"], job
                attempts = [event for event in scenario["events"] if event["event"] == "Job execution started"]
                failures = [event for event in scenario["events"] if event["event"] == "Job executor returned failure"]
                assert len(attempts) == 1 and len(failures) == 1, scenario["events"]
                elapsed = (parse_log_time(failures[0]["at"]) - parse_log_time(attempts[0]["at"])).total_seconds()
                scenario["attemptSeconds"] = elapsed
                assert elapsed <= DEADLINE_SECONDS + DEADLINE_SLACK_SECONDS, elapsed
                scenario["outcome"] = "pass"
            except Exception as error:
                scenario["error"] = str(error)
            scenario["completedAt"] = utc()

            # Dismissal profile: the same admitted join with a deadline far beyond the observation
            # window, so only dismissal can stop it and the scenario never races the elapsed-time bound.
            stop_monitor(monitor)
            monitor = None
            services["server"]["environment"]["Geoprocessing__Executors__MaxLayerExecutionSeconds"] = str(DISMISS_PROFILE_DEADLINE_SECONDS)
            compose_path.write_text(json.dumps({"services": services}))
            compose("up", "-d", "--no-deps", "server")
            readiness()
            container, receipt["dismissCandidate"] = verify_candidate()
            monitor = start_monitor()

            scenario = {"name": "dismiss-running-join", "startedAt": utc(), "outcome": "fail", "cpuPercent": []}
            receipt["scenarios"].append(scenario)
            try:
                job_id = submit("analytics.spatial-join", join_inputs)
                running_by = time.monotonic() + 60
                while api("/ogc/processes/jobs/" + job_id)["status"] != "running":
                    assert time.monotonic() < running_by, "join never started running"
                    time.sleep(0.1)
                time.sleep(1.5)
                busy = cpu_percent(container)
                scenario["cpuPercent"].append({"phase": "running", "value": busy})
                # The oracle is not vacuous only if the join was consuming the worker when dismissed.
                assert busy >= IDLE_CPU_PERCENT, busy
                scenario["jobAtDismiss"] = api("/ogc/processes/jobs/" + job_id)
                assert scenario["jobAtDismiss"]["status"] == "running", scenario["jobAtDismiss"]
                # The bound starts before DELETE: the endpoint may itself wait for the worker.
                dismiss_started = time.monotonic()
                dismissed = api("/ogc/processes/jobs/" + job_id, method="DELETE")
                scenario["dismissRequestSeconds"] = time.monotonic() - dismiss_started
                scenario["dismissResponse"] = dismissed
                # 200 dismissed when already stopped; 202 with the running status when cancellation
                # is delegated to the executing worker. Either way the job must reach dismissed promptly.
                assert dismissed["status"] in ["dismissed", "running"], dismissed
                while api("/ogc/processes/jobs/" + job_id)["status"] != "dismissed":
                    assert time.monotonic() - dismiss_started < DISMISS_BOUND_SECONDS, "dismissal was not confirmed by the worker"
                    time.sleep(0.1)
                scenario["secondsToDismissed"] = time.monotonic() - dismiss_started
                assert scenario["secondsToDismissed"] < DISMISS_BOUND_SECONDS, scenario["secondsToDismissed"]
                while True:
                    value = cpu_percent(container)
                    scenario["cpuPercent"].append({"phase": "dismissed", "value": value,
                                                   "secondsAfterDismiss": time.monotonic() - dismiss_started})
                    if value < IDLE_CPU_PERCENT:
                        break
                    assert time.monotonic() - dismiss_started < DISMISS_BOUND_SECONDS, "worker stayed busy after dismissal"
                # Observe past the production first-retry backoff, measured from dismissal: a
                # dismissed job must neither be retried nor complete.
                time.sleep(max(0.0, FIRST_RETRY_BACKOFF_SECONDS + RETRY_OBSERVATION_MARGIN_SECONDS
                                    - (time.monotonic() - dismiss_started)))
                scenario["observedSecondsAfterDismiss"] = time.monotonic() - dismiss_started
                job = api("/ogc/processes/jobs/" + job_id)
                scenario["job"] = job
                scenario["events"] = job_events(job_id)
                assert job["status"] == "dismissed", job
                attempts = [event for event in scenario["events"] if event["event"] == "Job execution started"]
                assert len(attempts) == 1, scenario["events"]
                scenario["outcome"] = "pass"
            except Exception as error:
                scenario["error"] = str(error)
            scenario["completedAt"] = utc()

            stop_monitor(monitor)
            receipt["finalContainerState"] = json.loads(subprocess.check_output(["docker", "inspect", container]))[0]["State"]
            assert receipt["serving"] and all(o["outcome"] == "pass" for o in receipt["serving"])
            assert not receipt["finalContainerState"]["OOMKilled"]
            assert all(s["outcome"] == "pass" for s in receipt["scenarios"]), "candidate failed resource-bound qualification"
            receipt["outcome"] = "pass"
        except Exception as error:
            receipt["error"] = str(error)
        finally:
            stop_monitor(monitor)
            with receipt_path.with_suffix(".server.log").open("w") as log:
                subprocess.run([*command, "logs", "--no-color", "--timestamps"], stdout=log, stderr=subprocess.STDOUT, check=False)
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
