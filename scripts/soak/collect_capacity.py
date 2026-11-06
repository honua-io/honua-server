#!/usr/bin/env python3
"""Collect request populations, topology samples and in-window fault recovery.

This collector owns its 170 virtual users and records every completed request,
including transport errors (599), at millisecond timer resolution. It does not
infer a population from NBomber summary percentiles. Failed sampling is retained.

The virtual users are anonymous, like the NBomber soak the frozen thresholds were
measured with (honua-release capacity lock baseline, run 34753540275). A credential
on a request bypasses the server's anonymous output cache by design, so an
admin-keyed workload measures a different, uncached server path than the one the
thresholds describe. Only workload probes and GP control requests carry the key.

The fault drill runs AFTER the measured window closes (founder ruling 2026-10-27,
as the 09-16 producer did): steady-state SLOs exclude the fault period, and the
drill is judged on its own evidence, `faultDrill`: recovery per dependency, no
data loss, and no stuck GP jobs. The workload keeps running through the drill
but is not recorded in the steady-state population.
"""
from __future__ import annotations

import argparse
import asyncio
import contextlib
from collections import Counter
from datetime import datetime, timedelta, timezone
import json
import math
import os
from pathlib import Path
import random
import time
from urllib.parse import urlencode

import aiohttp
import httpx

from capacity_evidence import assert_candidate, digest
import envelope_config
from drive_soak import SoakDriver


# Workload/metric samples are taken on this fixed cadence from the window start. The release
# checker refuses any gap above samplePeriodSeconds (60), so the cadence cannot be "sleep 30
# seconds after a sample", which adds the sample's own duration to every gap.
SAMPLE_CADENCE_SECONDS = 30
# Bound for each injected dependency to serve again, and for every outstanding GP job to reach a
# terminal state after the drill. A job still non-terminal after this is stuck.
RECOVERY_TIMEOUT_SECONDS = 300
GP_DRAIN_TIMEOUT_SECONDS = 300
TERMINAL_JOB_STATES = ("esriJobSucceeded", "esriJobFailed", "esriJobCancelled", "esriJobTimedOut")


def now():
    return datetime.now(timezone.utc)


def iso(value):
    return value.isoformat(timespec="microseconds").replace("+00:00", "Z")


def in_band_error(body: bytes, content_type: str) -> bool:
    if "json" not in content_type.lower():
        return False
    try:
        document = json.loads(body)
    except (ValueError, UnicodeError):
        return True
    return isinstance(document, dict) and (
        bool(document.get("error"))
        or "application/problem+json" in content_type.lower()
        or document.get("jobStatus") in ("esriJobFailed", "esriJobTimedOut")
        or str(document.get("type", "")).startswith("https://httpstatuses.com/")
    )


class RequestLedger:
    """Disjoint completion-time intervals; no rounding of timestamps or counts."""

    def __init__(self, replica, incarnation, period=30):
        self.replica, self.incarnation, self.period = replica, incarnation, period
        self.started = None
        self.ended = None
        self.buckets = {}
        self.observed_count = 0

    def record(self, at, duration_ms, status, error, protocol):
        if self.started is None or at < self.started or self.ended is not None and at >= self.ended:
            return
        index = int((at-self.started).total_seconds() // self.period)
        self.buckets.setdefault(index, Counter())[(duration_ms, status, error, protocol)] += 1
        self.observed_count += 1

    def intervals(self):
        if self.started is None or self.ended is None or self.ended <= self.started:
            raise ValueError("ledger has no complete window")
        result = []
        for index in range(math.ceil((self.ended-self.started).total_seconds()/self.period)):
            begin = self.started + timedelta(seconds=index*self.period)
            finish = min(begin+timedelta(seconds=self.period), self.ended)
            result.append(dict(replica=self.replica, incarnation=self.incarnation,
                               startedAt=iso(begin), endedAt=iso(finish),
                               buckets=[dict(durationMs=duration, httpStatus=status, inBandError=error, protocol=protocol, count=count)
                                        for (duration, status, error, protocol), count in sorted(self.buckets.get(index, {}).items())]))
        return result


# Same eight scenario populations as LoadTestProfile.Soak. Each coroutine is an
# independently cycling virtual user, including when its request fails.
MIX = (("feature", 30), ("spatial", 15), ("ogc", 20), ("cql", 10),
       ("connections", 60), ("memory", 5), ("odata", 15), ("tiles", 15))
POOL_PATHS = ("/rest/services/test/FeatureServer", "/rest/services/test/FeatureServer/0/query?f=json&where=1=1", "/ogc/features/collections", "/healthz/ready")
CQL = ("name LIKE '%test%'", "category = 'test' AND timestamp > TIMESTAMP('2023-01-01T00:00:00Z')",
       "S_INTERSECTS(shape, POLYGON((-123 37, -123 38, -122 38, -122 37, -123 37)))", "(category IN ('test', 'sample')) AND (objectid > 1)")
ODATA = ("/odata/Features(0)?$top=10", "/odata/Features(0)?$top=5&$orderby=ObjectId desc",
         "/odata/Features(0)?$filter=ObjectId gt 1&$top=5", "/odata/Features(0)?$select=ObjectId,LayerId&$top=10", "/odata/Layers?$top=5")


def route(scenario, iteration, rng):
    query = "/rest/services/test/FeatureServer/0/query?f=json&where=1=1"
    if scenario == "spatial":
        x, y = rng.uniform(-122.50, -122.30), rng.uniform(37.70, 37.82)
        dx, dy = rng.uniform(.0025, .0075), rng.uniform(.0025, .0075)
        return "/rest/services/test/FeatureServer/0/query?" + urlencode(dict(f="json", geometry=f"{x-dx},{y-dy},{x+dx},{y+dy}", geometryType="esriGeometryEnvelope", spatialRel="esriSpatialRelIntersects", inSR=4326))
    if scenario == "ogc":
        return "/ogc/features/collections/0/items?limit=10"
    if scenario == "cql":
        return "/ogc/features/collections/0/items?" + urlencode(dict(filter=CQL[iteration % len(CQL)]))
    if scenario == "connections":
        return POOL_PATHS[iteration % len(POOL_PATHS)]
    if scenario == "memory":
        return query + "&resultRecordCount=1000"
    if scenario == "odata":
        return ODATA[iteration % len(ODATA)]
    if scenario == "tiles":
        zoom = rng.randrange(3)
        return f"/ogc/tiles/collections/0/tiles/WebMercatorQuad/{zoom}/{rng.randrange(1 << zoom)}/{rng.randrange(1 << zoom)}"
    return query


def protocol(path):
    if "/FeatureServer" in path:
        return "FeatureServer"
    if path.startswith("/odata"):
        return "OData"
    if path.startswith("/ogc/tiles"):
        return "OGC-API-Tiles"
    if path.startswith("/ogc"):
        return "OGC-API-Features"
    if "/GPServer" in path:
        return "GPServer"
    if path.startswith(("/api/", "/monitoring/")):
        return "admin"
    return "health"


def feature_bytes(feature):
    """Canonical UTF-8 feature payload, including attributes and geometry."""
    return json.dumps(feature, separators=(",", ":"), ensure_ascii=False).encode()


def padded_description(feature, target):
    # Only ASCII padding is added. Its encoded length equals its character count;
    # the observed remainder includes all actual metadata and geometry ordinates.
    empty = dict(feature, attributes=dict(feature["attributes"], description=""))
    length = target - len(feature_bytes(empty))
    if length < 0:
        raise ValueError("feature already exceeds the declared maximum payload")
    return "x" * length


async def command(*args):
    process = await asyncio.create_subprocess_exec(*args, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
    try:
        stdout, stderr = await asyncio.wait_for(process.communicate(), 45)
    except TimeoutError:
        process.kill()
        await process.wait()
        raise
    if process.returncode:
        raise RuntimeError(f"{args[0]} failed ({process.returncode}): {stderr.decode()[-500:]}")
    return stdout.decode().strip()


async def observe_request(session, url, ledger, path):
    started = time.perf_counter()
    status, error = 599, True
    try:
        async with session.get(url, allow_redirects=False) as response:
            body = await response.read()
            status = response.status
            error = in_band_error(body, response.headers.get("Content-Type", ""))
    except (aiohttp.ClientError, TimeoutError):
        pass  # Transport failures are counted 599s, never omitted observations.
    ledger.record(now(), math.ceil((time.perf_counter()-started)*1000), status, error, protocol(path))


class RecordingTransport(httpx.AsyncBaseTransport):
    """Include workload probes and GP control requests in the same population."""

    def __init__(self, ledger):
        self.ledger = ledger
        self.transport = httpx.AsyncHTTPTransport()

    async def handle_async_request(self, request):
        started = time.perf_counter()
        status, error = 599, True
        try:
            response = await self.transport.handle_async_request(request)
            body = await response.aread()
            status = response.status_code
            error = in_band_error(body, response.headers.get("Content-Type", ""))
            return response
        finally:
            self.ledger.record(now(), math.ceil((time.perf_counter()-started)*1000), status, error, protocol(request.url.path))

    async def aclose(self):
        await self.transport.aclose()


def producer_identity(environ) -> dict:
    """Bind the executing workflow, not the caller: under workflow_call GITHUB_WORKFLOW_REF names the
    caller, while the attestation's buildSignerURI is the OIDC job_workflow_ref of this workflow."""
    return dict(repository=environ["GITHUB_REPOSITORY"], workflowPath=".github/workflows/capacity-soak-candidate.yml",
                workflowRef=environ["CAPACITY_SIGNER_WORKFLOW_REF"], sourceRevision=environ["GITHUB_SHA"],
                runId=int(environ["GITHUB_RUN_ID"]), runAttempt=int(environ["GITHUB_RUN_ATTEMPT"]), predicateType="https://slsa.dev/provenance/v1")


class Collector:
    def __init__(self, args, lock):
        self.args, self.lock = args, lock
        self.ledger = RequestLedger("honua", "pending")
        self.failures, self.metrics, self.workloads, self.recoveries = [], [], [], []
        self.drill_failures = []
        self.fault_drill = None
        self.payload_sha256 = None
        self.users = []
        self.payload_id = None
        self._gp_error_cursor = 0
        self.stop = asyncio.Event()
        self.driver = SoakDriver(argparse.Namespace(base_url=args.base_url, admin_key=args.admin_key,
                                                     unexercised=[], gp_interval=1,
                                                     gp_queue_depth=lock["supportedEnvelope"]["gpQueueDepth"],
                                                     gp_workers=lock["supportedEnvelope"]["gpWorkers"]), lock)

    async def establish_payload(self, client):
        """Exercise an exact-size HTTP feature update and verify its served result."""
        document = await self.driver._get_json(client, "/rest/services/test/FeatureServer/0/query?f=json&where=1%3D1&outFields=*&returnGeometry=true&orderByFields=objectid%20DESC&resultRecordCount=1")
        feature = document["features"][0]
        self.payload_id = int(feature["attributes"]["objectid"])
        target = self.lock["supportedEnvelope"]["maximumFeaturePayloadBytes"]
        padding = padded_description(feature, target)
        feature["attributes"]["description"] = padding
        response = await client.post(
            self.args.base_url + "/rest/services/test/FeatureServer/0/updateFeatures",
            data=dict(f="json", features=json.dumps([feature], separators=(",", ":"), ensure_ascii=False)),
            headers=self.driver.admin_headers,
        )
        if not response.is_success:
            raise ValueError(f"maximum-payload HTTP update rejected: {response.status_code}")
        document = response.json()
        results = document.get("updateResults", [])
        if document.get("error") or len(results) != 1 or results[0].get("success") is not True or results[0].get("objectId") != self.payload_id:
            raise ValueError("maximum-payload HTTP update did not succeed for the seeded feature")
        # A fresh predicate avoids any cached pre-update query. Both the accepted
        # write and decoded serving response are required; SQL bypasses are forbidden.
        served = await self.driver._get_json(client, f"/rest/services/test/FeatureServer/0/query?f=json&where=objectid%3D{self.payload_id}&outFields=*&returnGeometry=true")
        if len(served.get("features", [])) != 1 or len(feature_bytes(served["features"][0])) != target:
            raise ValueError("candidate did not serve the exact maximum feature payload")
        actual = served["features"][0]
        if actual != feature:
            raise ValueError("maximum-payload round trip changed geometry or lost attribute data")
        self.payload_sha256 = digest(json.dumps(served["features"], sort_keys=True).encode())

    def capture_gp_errors(self):
        self.failures.extend(f"GP sampling: {error}" for error in self.driver.gp.errors[self._gp_error_cursor:])
        self._gp_error_cursor = len(self.driver.gp.errors)

    async def user(self, session, scenario, seed):
        rng, iteration = random.Random(seed), 0
        while not self.stop.is_set():
            path = route(scenario, iteration, rng)
            await observe_request(session, self.args.base_url + path, self.ledger, path)
            iteration += 1

    async def sample(self, client):
        """Retain actual dimensions, including mismatches; never echo lock targets as observations."""
        self.capture_gp_errors()
        dimensions, proofs = {}, {}
        metric = dict(worker=None, database=None, redis=None, queueAgeSeconds=None)
        try:
            await self.driver.observe_deployment(client)
            dimensions["tenants"] = 1 if self.driver.deployment["tenantSource"] == "Default" and not self.driver._capabilities.get("admin.multi-tenancy", {}).get("available") else None
            catalog = await self.driver._get_json(client, "/rest/services?f=json")
            # Multiple protocol adapters may list the same logical service, and the catalogue
            # also lists built-in services (Utilities/PrintingTools, the geocode locator).
            dimensions["services"] = len(envelope_config.data_services(catalog))
            proofs["catalogServices"] = sorted({(entry.get("name"), entry.get("type")) for entry in catalog["services"]})
            service = await self.driver._get_json(client, "/rest/services/test/FeatureServer?f=json")
            layers = service["layers"]
            dimensions["layersPerService"] = len(layers)
            counts = [await self.driver._get_json(client, f"/rest/services/test/FeatureServer/{layer['id']}/query?f=json&where=1%3D1&returnCountOnly=true") for layer in layers]
            proofs["layerCounts"] = counts
            dimensions["featuresPerLayer"] = counts[0]["count"] if counts and all(c["count"] == counts[0]["count"] for c in counts) else None
            # The feature itself must be accepted and readable. A non-413 error is
            # not evidence of a successfully exercised maximum feature payload.
            feature = await self.driver._get_json(client, f"/rest/services/test/FeatureServer/0/query?f=json&where=objectid%3D{self.payload_id}&outFields=*&returnGeometry=true")
            features = feature.get("features", [])
            dimensions["maximumFeaturePayloadBytes"] = len(feature_bytes(features[0])) if len(features) == 1 else None
            proofs["payloadSha256"] = digest(json.dumps(features, sort_keys=True).encode())
            pool = await self.driver._get_json(client, "/monitoring/metrics/connection-pool", admin=True)
            metric["database"] = pool["utilization"] if pool.get("hasUtilizationData") else None
            proofs["connectionPool"] = pool
        except Exception as exc:
            self.failures.append(f"serving sample: {type(exc).__name__}: {exc}")
        dimensions["concurrentVirtualUsers"] = sum(not task.done() for task in self.users)
        gp = next((row for row in reversed(self.driver.gp.samples) if "queueDepth" in row), None)
        if gp is not None and (now()-datetime.fromisoformat(gp["at"].replace("Z", "+00:00"))).total_seconds() <= 60:
            dimensions["gpQueueDepth"] = gp["queueDepth"]
            metric["queueAgeSeconds"] = gp["oldestQueueAgeSeconds"]
            proofs["gpQueue"] = gp
        else:
            dimensions["gpQueueDepth"] = None
            self.failures.append("GP sampling is absent or older than 60 seconds")
        try:
            inspection = json.loads(await command("docker", "inspect", "honua-capacity-soak-honua-1"))[0]
            configured = dict(item.split("=", 1) for item in inspection["Config"]["Env"] if "=" in item)
            admission_limit = int(configured["ExecutionAdmission__MaxConcurrentJobsGlobal"])
            # One inspected server replica runs one sequential claim loop. The admission limit is
            # the active (queued + executing) job bound, not the worker count.
            configured_workers = envelope_config.configured_gp_workers(1)
            dimensions["gpWorkers"] = gp["executing"] if gp else None
            # GP workers share the server container's CPU allocation. Include all
            # work on that CPU resource, rather than treating a busy single slot
            # as 100% CPU saturation regardless of the work it is doing.
            cpu_percent = await command("docker", "stats", "--no-stream", "--format", "{{.CPUPerc}}", "honua-capacity-soak-honua-1")
            cpu_count = int(await command("docker", "info", "--format", "{{.NCPU}}"))
            metric["worker"] = float(cpu_percent.rstrip("%"))/(100*cpu_count)
            proofs["workerCpu"] = dict(percent=float(cpu_percent.rstrip("%")), allocatedCpus=cpu_count)
            proofs["workerConfiguration"] = configured_workers
            proofs["admissionActiveJobLimit"] = admission_limit
            redis = await command("docker", "exec", "honua-capacity-soak-redis-1", "redis-cli", "INFO", "clients")
            clients = dict(line.split(":", 1) for line in redis.splitlines() if ":" in line)
            maximum = (await command("docker", "exec", "honua-capacity-soak-redis-1", "redis-cli", "--raw", "CONFIG", "GET", "maxclients")).splitlines()[-1]
            metric["redis"] = int(clients["connected_clients"])/int(maximum)
            proofs["redisClients"] = dict(connected=int(clients["connected_clients"]), maximum=int(maximum))
        except Exception as exc:
            self.failures.append(f"dependency sample: {type(exc).__name__}: {exc}")
        if any(metric[k] is None for k in metric):
            self.failures.append("metric sample contains unobserved components")
        at = now()
        if self.metrics and (at-datetime.fromisoformat(self.metrics[-1]["at"].replace("Z", "+00:00"))).total_seconds() > 60:
            self.failures.append("metric/workload sampling gap exceeded 60 seconds")
        self.metrics.append(dict(at=iso(at), **metric))
        self.workloads.append(dict(at=iso(at), dimensions={name: dimensions.get(name) for name in self.lock["supportedEnvelope"]},
                                   executionMode="candidate-topology", proxy=False, proofs=proofs))
        return at

    async def faults(self, client):
        for dependency, container in (("worker", "honua"), ("database", "postgres"), ("redis", "redis")):
            injected = now()
            name = f"honua-capacity-soak-{container}-1"
            try:
                await command("docker", "stop", "--time", "1", name)
                running = await command("docker", "inspect", "--format", "{{.State.Running}}", name)
                if running != "false":
                    raise ValueError("injected dependency did not stop")
                detected = now()
                await command("docker", "start", name)
                deadline = time.monotonic()+RECOVERY_TIMEOUT_SECONDS
                while time.monotonic() < deadline:
                    try:
                        # A direct dependency probe plus a serving count prevents
                        # cached readiness from masquerading as database/Redis recovery.
                        if dependency == "database":
                            await command("docker", "exec", name, "pg_isready", "-U", os.environ["SOAK_DB_USER"], "-d", os.environ["SOAK_DB_NAME"])
                        if dependency == "redis" and await command("docker", "exec", name, "redis-cli", "PING") != "PONG":
                            raise ValueError("Redis not ready")
                        document = await self.driver._get_json(client, "/rest/services/test/FeatureServer/0/query?f=json&where=1%3D1&returnCountOnly=true")
                        if document.get("count") != self.lock["supportedEnvelope"]["featuresPerLayer"]:
                            raise ValueError("recovery count mismatch")
                        self.recoveries.append(dict(dependency=dependency, failure="container-stop-start", injectedAt=iso(injected), detectedAt=iso(detected), recoveredAt=iso(now()), probe="dependency-ready-and-serving-feature-count"))
                        break
                    except Exception:
                        await asyncio.sleep(1)
                else:
                    raise TimeoutError(f"{dependency} did not recover within {RECOVERY_TIMEOUT_SECONDS} seconds")
            except Exception as exc:
                self.drill_failures.append(f"recovery {dependency}: {type(exc).__name__}: {exc}")

    async def data_integrity(self, client):
        """Every seeded layer still serves its full count and the maximum payload is byte-identical."""
        envelope = self.lock["supportedEnvelope"]
        counts = []
        for layer in range(envelope["layersPerService"]):
            document = await self.driver._get_json(client, f"/rest/services/test/FeatureServer/{layer}/query?f=json&where=1%3D1&returnCountOnly=true")
            counts.append(document.get("count"))
        served = await self.driver._get_json(client, f"/rest/services/test/FeatureServer/0/query?f=json&where=objectid%3D{self.payload_id}&outFields=*&returnGeometry=true")
        features = served.get("features", [])
        payload_sha256 = digest(json.dumps(features, sort_keys=True).encode())
        payload_intact = len(features) == 1 and payload_sha256 == self.payload_sha256
        passed = all(count == envelope["featuresPerLayer"] for count in counts) and payload_intact
        return dict(layerCounts=counts, expectedFeaturesPerLayer=envelope["featuresPerLayer"],
                    payloadSha256=payload_sha256, expectedPayloadSha256=self.payload_sha256,
                    payloadIntact=payload_intact, passed=passed)

    async def drain_gp(self, client, timeout=GP_DRAIN_TIMEOUT_SECONDS):
        """After submissions stop, wait for every outstanding GP job to reach a terminal state."""
        pending = getattr(self.driver, "gp_pending", {})
        outstanding = len(pending)
        statuses = Counter()
        started = time.monotonic()
        deadline = started + timeout
        while pending and time.monotonic() < deadline:
            for job_id in list(pending):
                # The budget is checked before, and bounds, every poll: a terminal state observed
                # after the deadline must not turn an over-budget drain into a pass.
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    break
                try:
                    document = await asyncio.wait_for(
                        self.driver._get_json(client, f"/rest/services/test/GPServer/geometry.buffer/jobs/{job_id}?f=json", admin=True),
                        remaining)
                except Exception:  # noqa: BLE001 - a transient or timed-out poll stays unresolved until the bound
                    continue
                if document.get("jobStatus") in TERMINAL_JOB_STATES:
                    statuses[document["jobStatus"]] += 1
                    pending.pop(job_id, None)
            if pending:
                await asyncio.sleep(min(1, max(0, deadline-time.monotonic())))
        stuck = sorted(pending)
        return dict(outstandingAtDrillEnd=outstanding, terminalStatuses=dict(statuses), stuckJobIds=stuck,
                    drainSeconds=round(time.monotonic()-started, 3), drainTimeoutSeconds=timeout, passed=not stuck)

    async def drill(self, client, gp_task):
        """Inject faults after the measured window, then judge recovery, data and GP jobs."""
        began = now()
        gp_error_cursor = len(self.driver.gp.errors)
        await self.faults(client)
        integrity = None
        try:
            integrity = await self.data_integrity(client)
        except Exception as exc:  # noqa: BLE001
            self.drill_failures.append(f"data integrity: {type(exc).__name__}: {exc}")
        self.driver._stop.set()
        with contextlib.suppress(Exception):
            await asyncio.wait_for(gp_task, 60)
        gp_jobs = await self.drain_gp(client)
        ended = now()
        recovered = {event["dependency"] for event in self.recoveries} == {"worker", "database", "redis"}
        gp_errors = self.driver.gp.errors[gp_error_cursor:]
        self.fault_drill = dict(
            window=dict(startedAt=iso(began), endedAt=iso(ended)), excludedFromMeasuredWindow=True,
            load="steady-state workload and GP queue kept running during injection; not recorded in requests",
            recoveries=self.recoveries, recovered=recovered, dataIntegrity=integrity, gpJobs=gp_jobs,
            gpErrorsDuringDrill=dict(count=len(gp_errors), first=gp_errors[:20]),
            failures=self.drill_failures,
            passed=recovered and bool(integrity and integrity["passed"]) and gp_jobs["passed"] and not self.drill_failures)
        return self.fault_drill

    def workload_session(self):
        """The virtual users' session. It carries no credential (see the module docstring)."""
        return aiohttp.ClientSession(timeout=aiohttp.ClientTimeout(total=30), connector=aiohttp.TCPConnector(limit=256))

    async def sample_window(self, client, steady_seconds, cadence=SAMPLE_CADENCE_SECONDS):
        """Sample on a fixed cadence anchored at the window start; return the final sample time.

        A sample slower than the cadence delays only the next sample, never every later one.
        """
        started = time.monotonic()
        deadline = started + steady_seconds
        tick = 0
        while True:
            tick += 1
            due = min(started + tick*cadence, deadline)
            await asyncio.sleep(max(0, due-time.monotonic()))
            at = await self.sample(client)
            print(f"{iso(at)} requests={self.ledger.observed_count} samplingFailures={len(self.failures)}", flush=True)
            if due >= deadline:
                return at

    async def run(self):
        replica = json.loads(await command("docker", "inspect", "honua-capacity-soak-honua-1"))[0]
        self.ledger.incarnation = replica["Id"]
        headers = {"X-API-Key": self.args.admin_key}
        async with httpx.AsyncClient(timeout=10, headers=headers, transport=RecordingTransport(self.ledger)) as client, self.workload_session() as session:
            await self.driver.observe_deployment(client)
            assert_candidate(self.args.candidate_sha, os.environ["GITHUB_SHA"], self.driver.deployment["observedRevision"])
            await self.establish_payload(client)
            self.users = [asyncio.create_task(self.user(session, scenario, seed)) for scenario, copies in MIX for seed in range(copies)]
            gp = asyncio.create_task(self.driver.drive_gp_queue(client))
            try:
                await asyncio.sleep(self.args.ramp_up_seconds)
                self._gp_error_cursor = len(self.driver.gp.errors)
                self.ledger.started = await self.sample(client)
                self.ledger.ended = await self.sample_window(client, self.args.steady_seconds)
                # Sampling failures describe the measured window only; drill-time errors are
                # drill evidence.
                self.capture_gp_errors()
                await self.drill(client, gp)
            finally:
                self.stop.set()
                self.driver._stop.set()
                tasks = [*self.users, gp]
                try:
                    results = await asyncio.wait_for(asyncio.gather(*tasks, return_exceptions=True), 60)
                    for result in results:
                        if isinstance(result, Exception):
                            self.failures.append(f"collector task failed: {type(result).__name__}: {result}")
                except TimeoutError:
                    self.failures.append("collector tasks exceeded the 60-second drain budget")
                if self.fault_drill is None:
                    # The window never completed normally; retain everything that went wrong.
                    self.capture_gp_errors()
        producer = producer_identity(os.environ)
        return dict(schema="honua.capacity-observations/v1", lockSha256=digest(self.args.lock.read_bytes()),
                    candidateIdentity=dict(serverRevision=self.args.candidate_sha, imageDigest=self.args.image_digest),
                    observedRevision=self.driver.deployment["observedRevision"], producer=producer,
                    topology=dict(replicas=[dict(id="honua", failureDomain="local-docker-host", imageDigest=self.args.image_digest)],
                                  database=dict(kind="postgres", failureDomain="local-docker-host"), redis=dict(kind="redis", failureDomain="local-docker-host"),
                                  gpWorkers=self.workloads[0]["proofs"].get("workerConfiguration")),
                    window=dict(startedAt=iso(self.ledger.started), endedAt=iso(self.ledger.ended)),
                    samplingFailures=self.failures, populationMode="complete-disjoint-intervals", samplePeriodSeconds=60,
                    requestCount=self.ledger.observed_count, requests=self.ledger.intervals(),
                    metrics=self.metrics, workloads=self.workloads, recoveries=self.recoveries,
                    faultDrill=self.fault_drill)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("base-url", "admin-key", "candidate-sha", "image-digest"):
        parser.add_argument("--"+name, required=True)
    parser.add_argument("--lock", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--steady-seconds", type=int, required=True)
    parser.add_argument("--ramp-up-seconds", type=int, default=300)
    args = parser.parse_args()
    lock = json.loads(args.lock.read_bytes())
    if lock["soak"]["profile"] != "soak":
        raise ValueError("collector only implements the frozen soak profile")
    freeze = max(datetime.fromisoformat(value.replace("Z", "+00:00"))
                 for value in (lock["frozenAt"], lock["receiptContract"]["frozenAt"]))
    if now() <= freeze:
        raise ValueError("capacity collection must begin after the committed freeze")
    if lock["supportedEnvelope"]["concurrentVirtualUsers"] != sum(copies for _, copies in MIX):
        raise ValueError("collector scenario mix differs from the frozen concurrency")
    if args.steady_seconds < lock["soak"]["minimumSteadyStateSeconds"]:
        raise ValueError("steady-state duration is below the frozen minimum")
    result = asyncio.run(Collector(args, lock).run())
    args.out.write_text(json.dumps(result, separators=(",", ":"), allow_nan=False)+"\n")


if __name__ == "__main__":
    main()
