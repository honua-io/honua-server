#!/usr/bin/env python3
"""Collect request populations, topology samples and in-window fault recovery.

This collector owns its 170 virtual users and records every completed request,
including transport errors (599), at millisecond timer resolution. It does not
infer a population from NBomber summary percentiles. Failed sampling is retained.
"""
from __future__ import annotations

import argparse
import asyncio
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
from drive_soak import SoakDriver


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
    return isinstance(document, dict) and (bool(document.get("error")) or str(document.get("type", "")).startswith("https://httpstatuses.com/"))


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
    return "health"


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


class Collector:
    def __init__(self, args, lock):
        self.args, self.lock = args, lock
        self.ledger = RequestLedger("honua", "pending")
        self.failures, self.metrics, self.workloads, self.recoveries = [], [], [], []
        self.users = []
        self.stop = asyncio.Event()
        self.driver = SoakDriver(argparse.Namespace(base_url=args.base_url, admin_key=args.admin_key,
                                                     unexercised=[], gp_interval=1), lock)

    async def user(self, session, scenario, seed):
        rng, iteration = random.Random(seed), 0
        while not self.stop.is_set():
            path = route(scenario, iteration, rng)
            await observe_request(session, self.args.base_url + path, self.ledger, path)
            iteration += 1

    async def sample(self, client):
        """Retain actual dimensions, including mismatches; never echo lock targets as observations."""
        dimensions, proofs = {}, {}
        metric = dict(worker=None, database=None, redis=None, queueAgeSeconds=None)
        try:
            await self.driver.observe_deployment(client)
            dimensions["tenants"] = 1 if self.driver.deployment["tenantSource"] == "Default" and not self.driver._capabilities.get("admin.multi-tenancy", {}).get("available") else None
            catalog = await self.driver._get_json(client, "/rest/services?f=json")
            dimensions["services"] = len(catalog["services"])
            service = await self.driver._get_json(client, "/rest/services/test/FeatureServer?f=json")
            layers = service["layers"]
            dimensions["layersPerService"] = len(layers)
            counts = [await self.driver._get_json(client, f"/rest/services/test/FeatureServer/{layer['id']}/query?f=json&where=1%3D1&returnCountOnly=true") for layer in layers]
            proofs["layerCounts"] = counts
            dimensions["featuresPerLayer"] = counts[0]["count"] if counts and all(c["count"] == counts[0]["count"] for c in counts) else None
            # The feature itself must be accepted and readable. A non-413 error is
            # not evidence of a successfully exercised maximum feature payload.
            feature = await self.driver._get_json(client, "/rest/services/test/FeatureServer/0/query?f=json&where=objectid%3D1&outFields=*&returnGeometry=true")
            features = feature.get("features", [])
            dimensions["maximumFeaturePayloadBytes"] = len(json.dumps(features[0], separators=(",", ":"), ensure_ascii=False).encode()) if len(features) == 1 else None
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
            dimensions["gpWorkers"] = int(configured["ExecutionAdmission__MaxConcurrentJobsGlobal"])
            # Worker pressure is observed executing jobs / configured worker slots.
            metric["worker"] = gp["executing"]/dimensions["gpWorkers"] if gp else None
            proofs["workerConfiguration"] = dimensions["gpWorkers"]
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
        self.metrics.append(dict(at=iso(at), **metric))
        self.workloads.append(dict(at=iso(at), dimensions={name: dimensions.get(name) for name in self.lock["supportedEnvelope"]},
                                   executionMode="candidate-topology", proxy=False, proofs=proofs))
        return at

    async def faults(self, client):
        await asyncio.sleep(self.args.steady_seconds/2)
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
                deadline = time.monotonic()+300
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
                    raise TimeoutError(f"{dependency} did not recover within 300 seconds")
            except Exception as exc:
                self.failures.append(f"recovery {dependency}: {type(exc).__name__}: {exc}")

    async def run(self):
        replica = json.loads(await command("docker", "inspect", "honua-capacity-soak-honua-1"))[0]
        self.ledger.incarnation = replica["Id"]
        headers = {"X-API-Key": self.args.admin_key}
        async with httpx.AsyncClient(timeout=10, headers=headers) as client, aiohttp.ClientSession(headers=headers, timeout=aiohttp.ClientTimeout(total=30), connector=aiohttp.TCPConnector(limit=256)) as session:
            await self.driver.observe_deployment(client)
            assert_candidate(self.args.candidate_sha, os.environ["GITHUB_SHA"], self.driver.deployment["observedRevision"])
            self.users = [asyncio.create_task(self.user(session, scenario, seed)) for scenario, copies in MIX for seed in range(copies)]
            gp = asyncio.create_task(self.driver.drive_gp_queue(client))
            fault = None
            try:
                await asyncio.sleep(self.args.ramp_up_seconds)
                self.ledger.started = await self.sample(client)
                fault = asyncio.create_task(self.faults(client))
                deadline = time.monotonic()+self.args.steady_seconds
                while time.monotonic() < deadline:
                    await asyncio.sleep(min(30, max(0, deadline-time.monotonic())))
                    at = await self.sample(client)
                    print(f"{iso(at)} requests={self.ledger.observed_count} samplingFailures={len(self.failures)}", flush=True)
                self.ledger.ended = at
                if not fault.done():
                    self.failures.append("recovery did not complete inside the observation window")
            finally:
                self.stop.set()
                self.driver._stop.set()
                if fault is not None and not fault.done():
                    fault.cancel()
                await asyncio.gather(*self.users, gp, *([fault] if fault else []), return_exceptions=True)
        producer = dict(repository=os.environ["GITHUB_REPOSITORY"], workflowPath=".github/workflows/capacity-soak-candidate.yml",
                        workflowRef=os.environ["GITHUB_WORKFLOW_REF"], sourceRevision=os.environ["GITHUB_SHA"],
                        runId=int(os.environ["GITHUB_RUN_ID"]), runAttempt=int(os.environ["GITHUB_RUN_ATTEMPT"]), predicateType="https://slsa.dev/provenance/v1")
        return dict(schema="honua.capacity-observations/v1", lockSha256=digest(self.args.lock.read_bytes()),
                    candidateIdentity=dict(serverRevision=self.args.candidate_sha, imageDigest=self.args.image_digest),
                    observedRevision=self.driver.deployment["observedRevision"], producer=producer,
                    topology=dict(replicas=[dict(id="honua", failureDomain="local-docker-host", imageDigest=self.args.image_digest)],
                                  database=dict(kind="postgres", failureDomain="local-docker-host"), redis=dict(kind="redis", failureDomain="local-docker-host"),
                                  gpWorkers=self.workloads[0]["dimensions"]["gpWorkers"]),
                    window=dict(startedAt=iso(self.ledger.started), endedAt=iso(self.ledger.ended)),
                    samplingFailures=self.failures, populationMode="complete-disjoint-intervals", samplePeriodSeconds=60,
                    requestCount=self.ledger.observed_count, requests=self.ledger.intervals(),
                    metrics=self.metrics, workloads=self.workloads, recoveries=self.recoveries)


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
    if lock["supportedEnvelope"]["concurrentVirtualUsers"] != sum(copies for _, copies in MIX):
        raise ValueError("collector scenario mix differs from the frozen concurrency")
    if args.steady_seconds < lock["soak"]["minimumSteadyStateSeconds"]:
        raise ValueError("steady-state duration is below the frozen minimum")
    result = asyncio.run(Collector(args, lock).run())
    args.out.write_text(json.dumps(result, separators=(",", ":"), allow_nan=False)+"\n")


if __name__ == "__main__":
    main()
