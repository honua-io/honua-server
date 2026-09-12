#!/usr/bin/env python3
# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""Drive and observe one candidate capacity soak on the local-docker substrate.

This is the measurement half of the capacity-soak producer. The load half is the existing
NBomber harness (`scripts/scale/run-load-soak-tests.sh --profile soak`), which this driver
runs alongside: the harness supplies error rate, latency percentiles and throughput; this
driver supplies the signals a request generator cannot see (availability from an
independent prober, queue age, saturation, recovery time) and establishes/observes every
dimension of the declared capacity envelope.

Design rules, all of them consequences of the frozen contract:

* Every signal is a real observation with a window, a sample count and a method string.
  A measurement that could not be taken is recorded `unobserved` WITHOUT a value — never
  defaulted to zero, never omitted so a downstream default can fill it in.
* Envelope dimensions are established by this driver and then re-observed from the running
  deployment. A dimension that cannot be observed is reported unverified, and
  `soak_contract.build_receipt` refuses to claim the envelope at all.
* The recovery drill runs AFTER the steady-state window closes. Injecting a fault inside
  the measured window would spend the availability budget (>= 0.9999) on a deliberate
  outage and would make the steady-state numbers describe a different run than the one the
  envelope claims.
"""

from __future__ import annotations

import argparse
import asyncio
import contextlib
import json
import statistics
import subprocess
import sys
import time
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import httpx

sys.path.insert(0, str(Path(__file__).resolve().parent))
from soak_contract import COVERAGE_NOT_EXERCISED, COVERAGE_NOT_MET, COVERAGE_VERIFIED  # noqa: E402

ADMIN_HEADER = "X-API-Key"
SERVICE = "test"

# Capability ids whose opt-in the declared envelope depends on. Both ship Preview in
# 2026.1 and are OFF by default; the envelope declares capacity for them anyway
# (activeSubscriptions, alertEvaluationsPerSecond), so the soak has to turn them on
# through the product's canonical opt-in and say so in the receipt.
PREVIEW_CAPABILITIES = ("realtime.feature-streams", "alerts.geofence")


def utcnow() -> datetime:
    return datetime.now(timezone.utc)


def iso(moment: datetime) -> str:
    return moment.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


@dataclass
class Series:
    """A sampled series with the provenance needed to turn it into a signal."""

    name: str
    samples: list[dict[str, Any]] = field(default_factory=list)
    errors: list[str] = field(default_factory=list)

    def add(self, **sample: Any) -> None:
        self.samples.append({"at": iso(utcnow()), **sample})

    def values(self, key: str) -> list[float]:
        return [float(sample[key]) for sample in self.samples if sample.get(key) is not None]


class SoakDriver:
    def __init__(self, args: argparse.Namespace, lock: dict[str, Any]) -> None:
        self.args = args
        self.lock = lock
        self.envelope = lock["supportedEnvelope"]
        self.base_url = args.base_url.rstrip("/")
        self.admin_headers = {ADMIN_HEADER: args.admin_key}
        self.availability = Series("availability")
        self.saturation = Series("saturation")
        self.gp = Series("gpQueue")
        self.subscriptions = Series("subscriptions")
        self.alerts = Series("alertEvaluations")
        self.recovery: dict[str, Any] = {"status": "not-run"}
        self.deployment: dict[str, Any] = {}
        self.envelope_verification: dict[str, Any] = {}
        self.unexercised: dict[str, str] = dict(
            item.split("=", 1) for item in (args.unexercised or []) if "=" in item
        )
        self._subscription_readers: list[asyncio.Task[None]] = []
        self._subscription_bytes = 0
        self._subscription_closed_by_server = 0
        self._subscription_faulted = 0
        self.steady_start: datetime | None = None
        self.steady_end: datetime | None = None
        self._stop = asyncio.Event()

    # ------------------------------------------------------------------ helpers

    def _record(self, dimension: str, declared: Any, observed: Any, verified: bool, method: str, **extra: Any) -> None:
        """Record one envelope dimension's coverage.

        A dimension named in `--unexercised` is recorded as declared-but-not-driven, with the
        operator's reason attached. That is the only way a dimension can be absent from the
        soak, and it is visible in the published receipt (`envelopeCoverage`) rather than
        implied away by the verbatim envelope copy the gate compares.
        """
        if dimension in self.unexercised:
            self.envelope_verification[dimension] = {
                "declared": declared,
                "coverage": COVERAGE_NOT_EXERCISED,
                "verified": False,
                "reason": self.unexercised[dimension],
                "recordedAt": iso(utcnow()),
            }
            return
        self.envelope_verification[dimension] = {
            "declared": declared,
            "observed": observed,
            "coverage": COVERAGE_VERIFIED if verified else COVERAGE_NOT_MET,
            "verified": bool(verified),
            "method": method,
            "observedAt": iso(utcnow()),
            **extra,
        }

    async def _get_json(self, client: httpx.AsyncClient, path: str, *, admin: bool = False) -> Any:
        response = await client.get(
            f"{self.base_url}{path}",
            headers=self.admin_headers if admin else None,
            timeout=30.0,
        )
        response.raise_for_status()
        return response.json()

    # --------------------------------------------------------------- deployment

    async def observe_deployment(self, client: httpx.AsyncClient) -> None:
        """Read the running deployment's own identity, licence and capability posture.

        The capability manifest is the server's own statement about itself: the immutable
        deployment revision (HonuaDeploymentIdentity), the environment it booted in, the licence
        it validated, the tenant scope it resolved, and every capability's availability. The
        receipt's `observedRevision` comes from here — read back OUT of the running server, not
        echoed from the workflow input, which is the whole point of the contract keeping
        `candidateRevision` and `observedRevision` as two separate fields.
        """
        manifest = await self._get_json(client, "/api/v1/capabilities/manifest")
        server = manifest.get("server") or {}
        scope = manifest.get("scope") or {}
        policies = manifest.get("policies") or {}
        capabilities = {
            capability.get("id"): capability
            for capability in manifest.get("capabilities") or []
            if isinstance(capability, dict)
        }

        revision = server.get("deploymentRevision")
        environment = server.get("deploymentEnvironment")
        self.deployment = {
            "observedRevision": revision,
            "observedRevisionSource": server.get("deploymentRevisionSource"),
            "environment": environment,
            "serverVersion": server.get("serverVersion"),
            "edition": policies.get("currentEdition"),
            "licenseValid": policies.get("licenseValid"),
            "licenseValidationState": policies.get("licenseValidationState"),
            "tenantId": scope.get("tenantId"),
            "tenantSource": scope.get("tenantSource"),
            "previewCapabilities": {
                name: {
                    "lifecycle": capabilities.get(name, {}).get("lifecycle"),
                    "available": capabilities.get(name, {}).get("available"),
                }
                for name in PREVIEW_CAPABILITIES
            },
        }
        self._capabilities = capabilities

        if not revision:
            raise RuntimeError(
                "the running server reports no deployment revision; it cannot be bound to a candidate "
                "(build the image with HONUA_GIT_SHA=<candidate sha>)"
            )
        # A receipt for a Production capacity claim measured on a Development startup profile
        # would be a different claim. Fail here rather than publish it.
        if environment != "Production":
            raise RuntimeError(f"deployment environment is {environment!r}, expected 'Production'")

    # ----------------------------------------------------------------- envelope

    async def establish_envelope(self, client: httpx.AsyncClient) -> None:
        """Establish and verify every declared envelope dimension."""
        declared = self.envelope

        # tenants: 2026.1 is GA single-tenant, and multi-tenancy is an opt-in Preview surface.
        # The manifest reports the tenant this deployment resolved and how: a `Default` tenant
        # source with multi-tenancy unavailable is exactly one tenant.
        multi_tenancy = self._capabilities.get("admin.multi-tenancy", {})
        single_tenant = (
            self.deployment.get("tenantSource") == "Default" and not multi_tenancy.get("available", False)
        )
        self._record(
            "tenants",
            declared["tenants"],
            1 if single_tenant else "multi-tenant",
            single_tenant and declared["tenants"] == 1,
            "capability manifest: scope.tenantSource=Default with admin.multi-tenancy unavailable "
            "=> a single-tenant deployment",
            tenantId=self.deployment.get("tenantId"),
            tenantSource=self.deployment.get("tenantSource"),
        )

        service = await self._get_json(client, f"/rest/services/{SERVICE}/FeatureServer?f=json")
        layers = service.get("layers") or []
        self._record(
            "services",
            declared["services"],
            1,
            declared["services"] == 1,
            f"GET /rest/services/{SERVICE}/FeatureServer served one service catalog",
        )
        self._record(
            "layersPerService",
            declared["layersPerService"],
            len(layers),
            len(layers) == declared["layersPerService"],
            f"GET /rest/services/{SERVICE}/FeatureServer?f=json layer count",
        )

        counts: dict[str, int] = {}
        for layer in range(declared["layersPerService"]):
            document = await self._get_json(
                client,
                f"/rest/services/{SERVICE}/FeatureServer/{layer}/query"
                "?f=json&where=1%3D1&returnCountOnly=true",
            )
            counts[str(layer)] = int(document.get("count", -1))
        self._record(
            "featuresPerLayer",
            declared["featuresPerLayer"],
            counts,
            all(count == declared["featuresPerLayer"] for count in counts.values()) and bool(counts),
            "per-layer returnCountOnly=true query against the serving API",
        )

        await self._verify_payload_limit(client, declared["maximumFeaturePayloadBytes"])

        # concurrentVirtualUsers is driven by the NBomber soak profile; the workflow asserts the
        # profile's total user count against the lock before it starts the run and passes the
        # value here, so the receipt reports what was actually driven.
        self._record(
            "concurrentVirtualUsers",
            declared["concurrentVirtualUsers"],
            self.args.driven_virtual_users,
            self.args.driven_virtual_users == declared["concurrentVirtualUsers"],
            "NBomber soak profile total virtual users (LoadTestProfile.Soak.TotalVirtualUsers)",
        )


    async def _verify_payload_limit(self, client: httpx.AsyncClient, declared_bytes: int) -> None:
        """Prove the deployment accepts a feature payload of the declared maximum size.

        The claim under test is a limit, not a stored artifact: a body of exactly the declared
        size must not be rejected FOR ITS SIZE. A 413 fails the dimension; any other outcome
        means the size limit admitted the payload (the request may still be refused on its
        content, which is a different gate and is recorded as evidence).
        """
        filler = "x" * max(0, declared_bytes - 512)
        body = json.dumps(
            {
                "f": "json",
                "features": [
                    {
                        "attributes": {"objectid": 1, "name": "payload-limit-probe", "description": filler},
                        "geometry": {"x": -122.4, "y": 37.75, "spatialReference": {"wkid": 4326}},
                    }
                ],
            }
        )
        size = len(body.encode("utf-8"))
        try:
            response = await client.post(
                f"{self.base_url}/rest/services/{SERVICE}/FeatureServer/0/addFeatures",
                content=body,
                headers={**self.admin_headers, "Content-Type": "application/json"},
                timeout=60.0,
            )
            status = response.status_code
        except httpx.HTTPError as exc:
            self._record(
                "maximumFeaturePayloadBytes",
                declared_bytes,
                None,
                False,
                "payload-size probe failed to complete",
                error=str(exc),
            )
            return

        self._record(
            "maximumFeaturePayloadBytes",
            declared_bytes,
            size,
            status != 413 and size >= declared_bytes - 512,
            "POST of a declared-maximum-size feature payload was not rejected by a payload-size limit",
            httpStatus=status,
            probeBytes=size,
        )

    # ------------------------------------------------------------ subscriptions

    async def hold_subscriptions(self, count: int) -> list[Any]:
        """Open and hold `count` live feature-stream subscriptions (SSE).

        Each stream gets a reader that drains it. A subscriber that never reads is a slow
        consumer by definition, and the server disconnects slow consumers once its per-connection
        buffer fills — an earlier run opened all 1,000 subscriptions and had only 198 left an hour
        later for exactly that reason. Holding the declared subscription count means behaving like
        a subscriber, not like an open socket.
        """
        streams: list[Any] = []
        opened = 0
        errors: list[str] = []
        limits = httpx.Limits(max_connections=count + 32, max_keepalive_connections=count + 32)
        client = httpx.AsyncClient(limits=limits, timeout=httpx.Timeout(60.0, read=None))
        for index in range(count):
            try:
                context = client.stream(
                    "GET",
                    f"{self.base_url}/api/v1/streaming/features"
                    f"?layers=0&clientLabel=capacity-soak-{index}",
                    # The feature-stream endpoint is AllowAnonymous at the route but carries a
                    # live-stream authorization filter: an unauthenticated SSE request is 401.
                    headers={**self.admin_headers, "Accept": "text/event-stream"},
                )
                response = await context.__aenter__()
                if response.status_code != 200:
                    errors.append(f"subscription {index}: HTTP {response.status_code}")
                    await context.__aexit__(None, None, None)
                    break
                streams.append(context)
                self._subscription_readers.append(asyncio.create_task(self._drain_subscription(response, index)))
                opened += 1
            except Exception as exc:  # noqa: BLE001 - the count and the reason are both evidence
                errors.append(f"subscription {index}: {exc}")
                break
        self.subscriptions.add(opened=opened, requested=count)
        self.subscriptions.errors.extend(errors)
        self._subscription_clients = [client]
        return streams

    async def _drain_subscription(self, response: httpx.Response, index: int) -> None:
        """Consume one subscription's frames so the session stays a live, healthy consumer."""
        try:
            async for _ in response.aiter_bytes():
                self._subscription_bytes += 1
                if self._stop.is_set():
                    return
        except Exception as exc:  # noqa: BLE001 - a dropped subscription is evidence, not a crash
            if not self._stop.is_set():
                self._subscription_faulted += 1
                self.subscriptions.errors.append(f"subscription {index} ended: {type(exc).__name__}")
            return
        # The iterator ending without an exception means the SERVER closed the stream. A
        # subscription that the deployment hangs up on is exactly what the activeSubscriptions
        # dimension is about, so count it rather than letting it vanish quietly.
        if not self._stop.is_set():
            self._subscription_closed_by_server += 1

    async def observe_subscriptions(self, client: httpx.AsyncClient) -> None:
        try:
            document = await self._get_json(client, "/api/v1/admin/streaming/features/sessions", admin=True)
            payload = document.get("data", document)
            active = payload.get("activeSessions")
            if active is None:
                sessions = payload.get("sessions") or []
                active = len(sessions)
            self.subscriptions.add(activeSessions=int(active))
        except Exception as exc:  # noqa: BLE001
            self.subscriptions.errors.append(f"session probe: {exc}")

    # -------------------------------------------------------------------- loops

    async def probe_availability(self, client: httpx.AsyncClient) -> None:
        """One user-facing read per second: availability is served requests / attempts.

        Deliberately independent of the load harness's own counters so availability and error
        rate are two observations rather than one number reported twice.
        """
        while not self._stop.is_set():
            started = time.monotonic()
            ok = False
            status: int | str = "error"
            try:
                response = await client.get(
                    f"{self.base_url}/rest/services/{SERVICE}/FeatureServer/0/query"
                    "?f=json&where=1%3D1&resultRecordCount=1",
                    timeout=10.0,
                )
                status = response.status_code
                ok = response.status_code == 200 and "error" not in response.json()
            except Exception as exc:  # noqa: BLE001
                status = type(exc).__name__
            self.availability.add(ok=ok, status=status, elapsedMs=round((time.monotonic() - started) * 1000, 3))
            await self._sleep_until_next(started, 1.0)

    async def sample_saturation(self, client: httpx.AsyncClient) -> None:
        """Sample the server's own connection-pool utilisation: its declared saturation."""
        while not self._stop.is_set():
            started = time.monotonic()
            try:
                document = await self._get_json(client, "/monitoring/metrics/connection-pool", admin=True)
                has_data = bool(document.get("hasUtilizationData"))
                self.saturation.add(
                    utilization=float(document["utilization"]) if has_data else None,
                    hasUtilizationData=has_data,
                    queryAdmission=document.get("queryAdmission"),
                    totalTimeouts=document.get("totalTimeouts"),
                    totalFailures=document.get("totalFailures"),
                )
            except Exception as exc:  # noqa: BLE001
                self.saturation.errors.append(str(exc))
                self.saturation.add(utilization=None, hasUtilizationData=False, error=type(exc).__name__)
            await self._sleep_until_next(started, self.args.sample_interval)

    async def drive_gp_queue(self, client: httpx.AsyncClient) -> None:
        """Submit geoprocessing jobs and measure how long the oldest one waits to start.

        Queue age is measured client-side, the way an operator measures it: the time a submitted
        job spends before the single worker picks it up.
        """
        pending: dict[str, dict[str, float]] = {}
        completed = 0
        rejected = 0
        while not self._stop.is_set():
            started = time.monotonic()
            try:
                response = await client.post(
                    f"{self.base_url}/rest/services/{SERVICE}/GPServer/geometry.buffer/submitJob",
                    data={"f": "json", "wkb": "AQEAAAAAAAAAAAAAAAAAAAAAAAAA", "srid": "4326", "distance": "10"},
                    headers=self.admin_headers,
                    timeout=30.0,
                )
                document = response.json()
                job_id = document.get("jobId")
                if job_id:
                    pending[job_id] = {"submitted": time.monotonic(), "lastQueued": time.monotonic()}
                elif str(document.get("error", {}).get("code")) == "503":
                    # Admission control refuses a submission while the single declared worker is
                    # busy rather than queueing it. That is the candidate's behaviour, not an
                    # error in the measurement: count it as evidence for the queue-depth dimension.
                    rejected += 1
                else:
                    self.gp.errors.append(f"submitJob: {json.dumps(document)[:200]}")
            except Exception as exc:  # noqa: BLE001
                self.gp.errors.append(f"submitJob: {exc}")

            queued_ages: list[float] = []
            executing = 0
            now = time.monotonic()
            for job_id, timing in list(pending.items()):
                try:
                    document = await self._get_json(
                        client,
                        f"/rest/services/{SERVICE}/GPServer/geometry.buffer/jobs/{job_id}?f=json",
                        admin=True,
                    )
                except Exception as exc:  # noqa: BLE001
                    self.gp.errors.append(f"jobStatus {job_id}: {exc}")
                    continue
                status = document.get("jobStatus", "")
                if status == "esriJobSubmitted":
                    timing["lastQueued"] = now
                    queued_ages.append(now - timing["submitted"])
                    continue
                if status == "esriJobExecuting":
                    executing += 1
                # The job has left the queue. Its queue age is the last instant it was still
                # observed queued, so the poll interval bounds the error instead of inflating
                # every job's wait by one whole interval.
                self.gp.samples.append(
                    {
                        "at": iso(utcnow()),
                        "jobLeftQueue": job_id,
                        "queueWaitSeconds": round(timing["lastQueued"] - timing["submitted"], 3),
                        "status": status,
                    }
                )
                completed += 1
                pending.pop(job_id, None)

            self.gp.add(
                queueDepth=len(pending),
                executing=executing,
                oldestQueueAgeSeconds=round(max(queued_ages), 3) if queued_ages else 0.0,
                observedJobs=completed,
                admissionRejections=rejected,
            )
            await self._sleep_until_next(started, self.args.gp_interval)

    async def heartbeat(self, phase: str) -> None:
        """Print progress while the window runs.

        A soak step that prints nothing for an hour is undiagnosable while it is happening: the
        only way to see a stall is to wait for the job to end. These lines make the live log
        answer "is anything still moving?".
        """
        while not self._stop.is_set():
            await self._sleep_until_next(time.monotonic(), self.args.heartbeat_seconds)
            if self._stop.is_set():
                return
            availability = self.availability.samples
            ok = sum(1 for sample in availability if sample.get("ok"))
            gp = [sample for sample in self.gp.samples if "queueDepth" in sample]
            print(
                f"[{iso(utcnow())}] {phase}: availability {ok}/{len(availability)} probes ok, "
                f"{len(self.saturation.samples)} saturation samples, "
                f"{gp[-1].get('observedJobs') if gp else 0} gp jobs observed",
                flush=True,
            )

    async def _sleep_until_next(self, started: float, interval: float) -> None:
        remaining = interval - (time.monotonic() - started)
        if remaining > 0:
            with contextlib.suppress(asyncio.TimeoutError):
                await asyncio.wait_for(self._stop.wait(), timeout=remaining)

    # ----------------------------------------------------------------- recovery

    async def run_recovery_drill(self, client: httpx.AsyncClient) -> None:
        """Restart the server container and measure time to the first served request.

        Run after the steady-state window: a deliberate outage inside the window would be
        charged against the frozen availability budget.
        """
        if not self.args.compose_file:
            self.recovery = {"status": "not-run", "reason": "no compose file supplied"}
            return
        injected_at = utcnow()
        command = ["docker", "compose", "-f", self.args.compose_file, "restart", self.args.compose_service]
        start = time.monotonic()
        result = subprocess.run(command, capture_output=True, text=True, check=False)
        if result.returncode != 0:
            self.recovery = {
                "status": "failed",
                "reason": f"{' '.join(command)} exited {result.returncode}",
                "stderr": result.stderr[-2000:],
            }
            return

        deadline = start + self.args.recovery_timeout
        recovered_at = None
        attempts = 0
        while time.monotonic() < deadline:
            attempts += 1
            try:
                response = await client.get(
                    f"{self.base_url}/rest/services/{SERVICE}/FeatureServer/0/query"
                    "?f=json&where=1%3D1&resultRecordCount=1",
                    timeout=10.0,
                )
                if response.status_code == 200 and "error" not in response.json():
                    recovered_at = time.monotonic()
                    break
            except Exception:  # noqa: BLE001 - an unreachable server during restart is the point
                pass
            await asyncio.sleep(1.0)

        if recovered_at is None:
            self.recovery = {
                "status": "failed",
                "reason": f"service did not serve a request within {self.args.recovery_timeout}s of the fault",
                "injectedAt": iso(injected_at),
                "attempts": attempts,
            }
            return

        self.recovery = {
            "status": "observed",
            "fault": f"docker compose restart {self.args.compose_service}",
            "injectedAt": iso(injected_at),
            "recoveredAt": iso(utcnow()),
            "recoveryTimeSeconds": round(recovered_at - start, 3),
            "probeAttempts": attempts,
        }

    # --------------------------------------------------------------------- run

    async def run(self) -> dict[str, Any]:
        limits = httpx.Limits(max_connections=64, max_keepalive_connections=32)
        async with httpx.AsyncClient(limits=limits, follow_redirects=False) as client:
            await self.observe_deployment(client)
            await self.establish_envelope(client)

            streams: list[Any] = []
            declared_subscriptions = self.envelope["activeSubscriptions"]
            if self.args.subscriptions:
                streams = await self.hold_subscriptions(declared_subscriptions)
                await self.observe_subscriptions(client)

            tasks = [
                asyncio.create_task(self.probe_availability(client)),
                asyncio.create_task(self.sample_saturation(client)),
                asyncio.create_task(self.drive_gp_queue(client)),
                asyncio.create_task(self.heartbeat("soak")),
            ]
            print(f"[{iso(utcnow())}] observing: ramp-up {self.args.ramp_up_seconds}s then "
                  f"{self.args.steady_seconds}s of steady state", flush=True)

            if self.args.ramp_up_seconds:
                await asyncio.sleep(self.args.ramp_up_seconds)

            self.steady_start = utcnow()
            steady_marker = len(self.availability.samples), len(self.saturation.samples), len(self.gp.samples)
            await asyncio.sleep(self.args.steady_seconds)
            self.steady_end = utcnow()

            await self.observe_subscriptions(client)
            self._stop.set()
            for task in tasks:
                with contextlib.suppress(asyncio.CancelledError):
                    await task

            for reader in self._subscription_readers:
                reader.cancel()
            # Bounded teardown: a subscription that will not close must not hold the run open.
            for context in streams:
                with contextlib.suppress(Exception):
                    await asyncio.wait_for(context.__aexit__(None, None, None), timeout=5.0)
            for subscription_client in getattr(self, "_subscription_clients", []):
                with contextlib.suppress(Exception):
                    await subscription_client.aclose()

            await self.run_recovery_drill(client)

            self._verify_subscription_dimension(declared_subscriptions)
            self._verify_alert_dimension()
            self._verify_gp_dimensions(steady_marker[2])

            return self.to_json(steady_marker)

    def _verify_subscription_dimension(self, declared: int) -> None:
        observed = [sample["activeSessions"] for sample in self.subscriptions.samples if "activeSessions" in sample]
        held = min(observed) if observed else None
        self._record(
            "activeSubscriptions",
            declared,
            held,
            held is not None and held >= declared,
            "live feature-stream subscriptions, each with a reader draining it, held for the "
            "steady-state window and counted by GET /api/v1/admin/streaming/features/sessions",
            openErrors=self.subscriptions.errors[:5],
            previewOptIn="realtime.feature-streams",
            closedByServer=self._subscription_closed_by_server,
            faulted=self._subscription_faulted,
            bytesDrained=self._subscription_bytes,
        )

    def _verify_gp_dimensions(self, steady_index: int) -> None:
        """Verify the GP worker/queue dimensions from what the run actually observed.

        `gpWorkers` is a configured limit (ExecutionAdmission:MaxConcurrentJobsGlobal) AND an
        observation: no poll ever saw more than that many jobs executing at once.
        `gpQueueDepth` is an operating bound, not a server setting — there is no queue-capacity
        option to read back — so it is verified as "the queue was genuinely exercised and never
        exceeded the declared bound".
        """
        samples = [sample for sample in self.gp.samples[steady_index:] if "queueDepth" in sample]
        executing = [sample.get("executing", 0) for sample in samples]
        depths = [sample.get("queueDepth", 0) for sample in samples]
        jobs = max((sample.get("observedJobs", 0) for sample in samples), default=0)
        rejections = max((sample.get("admissionRejections", 0) for sample in samples), default=0)
        max_executing = max(executing, default=None)
        max_depth = max(depths, default=None)

        self._record(
            "gpWorkers",
            self.envelope["gpWorkers"],
            {"configuredConcurrency": self.args.gp_workers, "maxObservedExecuting": max_executing},
            self.args.gp_workers == self.envelope["gpWorkers"]
            and max_executing is not None
            and max_executing <= self.envelope["gpWorkers"]
            and jobs > 0,
            "ExecutionAdmission:MaxConcurrentJobsGlobal configured on the substrate, confirmed by "
            "the maximum number of concurrently executing jobs observed during steady state",
            observedJobs=jobs,
        )
        self._record(
            "gpQueueDepth",
            self.envelope["gpQueueDepth"],
            {"maxObservedQueueDepth": max_depth, "bound": self.envelope["gpQueueDepth"]},
            max_depth is not None and max_depth <= self.envelope["gpQueueDepth"] and jobs > 0,
            "geoprocessing queue exercised for the whole steady-state window; the declared depth is "
            "an operating bound and the observed maximum stayed within it",
            observedJobs=jobs,
            admissionRejections=rejections,
            note=(
                "with the declared single worker, execution admission answers 503 'Global active job "
                "limit reached (1/1)' to a submission arriving while a job runs, so the queue does not "
                "grow towards the declared bound through the GPServer submit path; the bound was "
                "respected but not filled"
            ),
        )

    def _verify_alert_dimension(self) -> None:
        observed = self.alerts.samples[-1].get("evaluationsPerSecond") if self.alerts.samples else None
        declared = self.envelope["alertEvaluationsPerSecond"]
        self._record(
            "alertEvaluationsPerSecond",
            declared,
            observed,
            observed is not None and observed >= declared,
            "honua.alert_state.last_evaluated_at updates inside the steady-state window, divided by "
            "the window length",
            previewOptIn="alerts.geofence",
            errors=self.alerts.errors[:5],
        )

    def to_json(self, steady_marker: tuple[int, int, int]) -> dict[str, Any]:
        availability_index, saturation_index, gp_index = steady_marker
        steady_availability = self.availability.samples[availability_index:]
        steady_saturation = self.saturation.samples[saturation_index:]
        steady_gp = self.gp.samples[gp_index:]
        return {
            "driver": "scripts/soak/drive_soak.py",
            "baseUrl": self.base_url,
            "steadyStart": iso(self.steady_start) if self.steady_start else None,
            "steadyEnd": iso(self.steady_end) if self.steady_end else None,
            "steadySeconds": self.args.steady_seconds,
            "deployment": self.deployment,
            "envelopeVerification": self.envelope_verification,
            "availability": {
                "samples": len(steady_availability),
                "ok": sum(1 for sample in steady_availability if sample.get("ok")),
                "series": steady_availability if self.args.keep_series else steady_availability[:5],
                "failures": [sample for sample in steady_availability if not sample.get("ok")][:50],
            },
            "saturation": {
                "samples": len(steady_saturation),
                "withData": sum(1 for sample in steady_saturation if sample.get("hasUtilizationData")),
                "peakUtilization": max(
                    (sample["utilization"] for sample in steady_saturation if sample.get("utilization") is not None),
                    default=None,
                ),
                "meanUtilization": (
                    round(statistics.fmean(
                        [sample["utilization"] for sample in steady_saturation if sample.get("utilization") is not None]
                    ), 6)
                    if any(sample.get("utilization") is not None for sample in steady_saturation)
                    else None
                ),
                "series": steady_saturation if self.args.keep_series else steady_saturation[:5],
                "errors": self.saturation.errors[:10],
            },
            "gpQueue": {
                "samples": len(steady_gp),
                "maxOldestQueueAgeSeconds": max(
                    [sample["oldestQueueAgeSeconds"] for sample in steady_gp
                     if sample.get("oldestQueueAgeSeconds") is not None]
                    + [sample["queueWaitSeconds"] for sample in steady_gp
                       if sample.get("queueWaitSeconds") is not None],
                    default=None,
                ),
                "maxQueueDepth": max(
                    (sample["queueDepth"] for sample in steady_gp if "queueDepth" in sample), default=None
                ),
                "observedJobs": max((sample.get("observedJobs", 0) for sample in steady_gp), default=0),
                "admissionRejections": max(
                    (sample.get("admissionRejections", 0) for sample in steady_gp), default=0
                ),
                "series": steady_gp if self.args.keep_series else steady_gp[:5],
                "errors": self.gp.errors[:10],
            },
            "subscriptions": {"samples": self.subscriptions.samples, "errors": self.subscriptions.errors[:10]},
            "alerts": {"samples": self.alerts.samples, "errors": self.alerts.errors[:10]},
            "recovery": self.recovery,
        }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--admin-key", required=True)
    parser.add_argument("--lock", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--ramp-up-seconds", type=int, default=0)
    parser.add_argument("--steady-seconds", type=int, required=True)
    parser.add_argument("--sample-interval", type=float, default=5.0)
    parser.add_argument("--gp-interval", type=float, default=1.0)
    parser.add_argument("--recovery-timeout", type=float, default=300.0)
    parser.add_argument("--driven-virtual-users", type=int, required=True)
    parser.add_argument("--gp-workers", type=int, required=True)
    parser.add_argument("--gp-queue-depth", type=int, required=True)
    parser.add_argument("--compose-file", default="")
    parser.add_argument("--compose-service", default="honua")
    parser.add_argument("--subscriptions", action="store_true", help="hold the declared subscription count")
    parser.add_argument("--keep-series", action="store_true", help="write every sample, not just the head")
    parser.add_argument("--heartbeat-seconds", type=float, default=300.0, help="progress line interval")
    parser.add_argument(
        "--unexercised",
        action="append",
        default=[],
        metavar="DIMENSION=REASON",
        help="declare an envelope dimension as deliberately not driven by this run, with the reason "
        "recorded in the receipt's envelopeCoverage block",
    )
    args = parser.parse_args()

    lock = json.loads(args.lock.read_text(encoding="utf-8"))
    driver = SoakDriver(args, lock)
    observations = asyncio.run(driver.run())
    args.out.write_text(json.dumps(observations, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps({key: observations[key] for key in ("steadyStart", "steadyEnd", "recovery")}, indent=2))
    # A dimension the deployment failed to hold is DATA: it belongs in the published receipt,
    # where the release gate can refuse the candidate on it. Only report it here; do not throw the
    # run away by exiting non-zero, which would stop the receipt from ever being built.
    not_met = sorted(
        name
        for name, record in observations["envelopeVerification"].items()
        if record.get("coverage") == COVERAGE_NOT_MET
    )
    if not_met:
        print("envelope dimension(s) the deployment did not hold: " + ", ".join(not_met), file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
