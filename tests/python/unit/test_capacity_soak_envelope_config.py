# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

"""The capacity soak must start, drive and sample exactly the frozen envelope (#5314).

Candidate run 40542363841 (53bb94a) failed qualification for harness reasons, not candidate ones:

* the compose file set the GP admission limit to ``gpWorkers`` (1). Admission counts queued and
  executing jobs, so no queue could form and 108,842 submissions were refused in-band;
* every virtual user carried the admin API key, which bypasses the anonymous output cache the
  frozen thresholds were measured through (NBomber, anonymous, run 34753540275);
* ``services`` counted the catalogue's built-in PrintingTools and geocode entries (3 for 1);
* sampling slept 30 seconds *after* each slow sample, so 52 gaps exceeded the 60-second limit.

Each test below fails against that harness and passes against the fix.
"""

from __future__ import annotations

import asyncio
import json
import sys
import time
from pathlib import Path

from aiohttp import web
import yaml

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "scripts/soak"))
import collect_capacity as collector_module  # noqa: E402
import envelope_config  # noqa: E402

LOCK = json.loads((ROOT / "tests/python/fixtures/capacity/lock.json").read_bytes())
ENVELOPE = LOCK["supportedEnvelope"]
WORKFLOW = ROOT / ".github/workflows/capacity-soak-candidate.yml"
COMPOSE = ROOT / "docker-compose.soak.yml"

# The catalogue root the candidate served in run 40542363841: one seeded service published by
# several adapters, plus two built-ins.
CATALOG = dict(services=[
    dict(name="test", type="FeatureServer"), dict(name="test", type="MapServer"), dict(name="test", type="GPServer"),
    dict(name="Utilities/PrintingTools", type="GPServer"), dict(name="World", type="GeocodeServer"),
])
# The effective environment run 40542363841's server started with.
RUN_40542363841_ENV = {
    "ExecutionAdmission__Enabled": "true",
    "ExecutionAdmission__MaxConcurrentJobsGlobal": "1",
    "ExecutionAdmission__MaxConcurrentJobsPerPartition": "1",
    "ExecutionAdmission__MaxSubmissionsPerWindow": "0",
    "RateLimiting__Enabled": "false",
}


def _rendered_admission(environment: dict[str, str]) -> dict[str, str]:
    """Interpolate the compose admission values the way `docker compose config` does."""
    honua = yaml.safe_load(COMPOSE.read_text(encoding="utf-8"))["services"]["honua"]["environment"]
    rendered = {}
    for key in ("ExecutionAdmission__MaxConcurrentJobsGlobal", "ExecutionAdmission__MaxConcurrentJobsPerPartition"):
        value = str(honua[key])
        assert value.startswith("${") and value.endswith("}"), f"{key} must come from the job environment"
        name, _, fallback = value[2:-1].partition(":")
        if name not in environment:
            assert fallback.startswith("?"), f"{key} silently defaults to {fallback!r} without the envelope"
            raise KeyError(name)
        rendered[key] = environment[name]
    return rendered


def test_admission_limit_admits_the_declared_queue_plus_worker():
    assert envelope_config.admission_active_job_limit(ENVELOPE) == ENVELOPE["gpWorkers"] + ENVELOPE["gpQueueDepth"] == 101


def test_effective_config_check_refuses_the_failed_runs_server():
    failures = envelope_config.check(ENVELOPE, RUN_40542363841_ENV, CATALOG, layers=4, replicas=1)
    assert failures == [
        "ExecutionAdmission__MaxConcurrentJobsGlobal is '1', envelope requires '101'",
        "ExecutionAdmission__MaxConcurrentJobsPerPartition is '1', envelope requires '101'",
    ]


def test_effective_config_check_accepts_the_envelope_server():
    environment = dict(RUN_40542363841_ENV, **envelope_config.expected_settings(ENVELOPE))
    assert envelope_config.check(ENVELOPE, environment, CATALOG, layers=4, replicas=1) == []


def test_effective_config_check_refuses_drift_in_every_dimension():
    environment = dict(envelope_config.expected_settings(ENVELOPE), RateLimiting__Enabled="true")
    del environment["ExecutionAdmission__MaxSubmissionsPerWindow"]
    catalog = dict(services=CATALOG["services"] + [dict(name="other", type="FeatureServer")])
    failures = envelope_config.check(ENVELOPE, environment, catalog, layers=3, replicas=2)
    assert len(failures) == 5
    assert any(f.startswith("RateLimiting__Enabled") for f in failures)
    assert any(f.startswith("ExecutionAdmission__MaxSubmissionsPerWindow is None") for f in failures)
    assert any(f.startswith("gpWorkers: 2 replica(s)") for f in failures)
    assert any(f.startswith("services: catalogue publishes ['other', 'test']") for f in failures)
    assert any(f.startswith("layersPerService: service serves 3") for f in failures)


def test_compose_admission_comes_from_the_lock_and_has_no_silent_default():
    try:
        _rendered_admission({})
    except KeyError as missing:
        assert missing.args == ("SOAK_GP_ACTIVE_JOBS",)
    else:
        raise AssertionError("compose rendered GP admission without the envelope-derived limit")
    limit = str(envelope_config.admission_active_job_limit(ENVELOPE))
    rendered = _rendered_admission({"SOAK_GP_ACTIVE_JOBS": limit})
    assert all(rendered[key] == envelope_config.expected_settings(ENVELOPE)[key] for key in rendered)


def test_compose_static_settings_match_the_effective_config_check():
    honua = yaml.safe_load(COMPOSE.read_text(encoding="utf-8"))["services"]["honua"]["environment"]
    for key in ("ExecutionAdmission__Enabled", "ExecutionAdmission__MaxSubmissionsPerWindow", "RateLimiting__Enabled"):
        assert str(honua[key]) == envelope_config.expected_settings(ENVELOPE)[key]


def test_workflow_exports_the_lock_derived_limit_and_checks_before_collecting():
    workflow = yaml.safe_load(WORKFLOW.read_text(encoding="utf-8"))
    assert "SOAK_GP_WORKERS" not in workflow["env"], "a workflow literal must not decide GP admission"
    steps = workflow["jobs"]["soak"]["steps"]
    names = [step.get("name", "") for step in steps]
    lock_step = steps[names.index("Fetch the frozen capacity lock")]["run"]
    assert "envelope_config.admission_active_job_limit" in lock_step
    assert 'echo "SOAK_GP_ACTIVE_JOBS=$gp_active_jobs" >> "$GITHUB_ENV"' in lock_step
    check = names.index("Refuse a server whose effective configuration differs from the envelope")
    assert names.index("Republish the served catalogue from the seeded rows") < check
    assert check < names.index("Collect the complete candidate observation window")
    assert "scripts/soak/envelope_config.py --lock ../lock.json" in steps[check]["run"]
    assert "continue-on-error" not in steps[check]


def test_sampled_services_exclude_catalogue_built_ins():
    assert envelope_config.data_services(CATALOG) == ["test"]


def _serve(handler):
    async def start():
        app = web.Application()
        app.router.add_get("/{tail:.*}", handler)
        runner = web.AppRunner(app)
        await runner.setup()
        site = web.TCPSite(runner, "127.0.0.1", 0)
        await site.start()
        return runner, site._server.sockets[0].getsockname()[1]
    return start()


def test_virtual_users_are_anonymous_like_the_threshold_baseline():
    import argparse

    seen = []

    async def handler(request):
        seen.append(dict(request.headers))
        return web.json_response(dict(features=[]))

    async def exercise():
        runner, port = await _serve(handler)
        try:
            collector = collector_module.Collector(argparse.Namespace(base_url=f"http://127.0.0.1:{port}", admin_key="admin-secret"), LOCK)
            ledger = collector_module.RequestLedger("honua", "fixture")
            ledger.started = collector_module.now()
            async with collector.workload_session() as session:
                for scenario, _ in collector_module.MIX:
                    path = collector_module.route(scenario, 0, __import__("random").Random(0))
                    await collector_module.observe_request(session, f"http://127.0.0.1:{port}{path}", ledger, path)
        finally:
            await runner.cleanup()
        assert len(seen) == len(collector_module.MIX)
        for headers in seen:
            lowered = {key.lower(): value for key, value in headers.items()}
            assert "x-api-key" not in lowered and "authorization" not in lowered
            assert "admin-secret" not in json.dumps(headers)

    asyncio.run(exercise())


def test_sampling_cadence_does_not_add_sample_duration_to_every_gap():
    import argparse

    cadence, sample_seconds, window = 0.2, 0.15, 1.0

    async def exercise():
        collector = collector_module.Collector(argparse.Namespace(base_url="http://fixture", admin_key="fixture"), LOCK)
        taken = []

        async def slow_sample(client):
            await asyncio.sleep(sample_seconds)
            taken.append(time.monotonic())
            return collector_module.now()

        collector.sample = slow_sample
        begin = time.monotonic()
        await collector.sample_window(None, window, cadence=cadence)
        return begin, taken

    begin, taken = asyncio.run(exercise())
    gaps = [b - a for a, b in zip([begin] + taken, taken)]
    # Sleep-after-sample spaces samples cadence + duration apart (0.35 s here); a fixed cadence
    # keeps every gap at the cadence plus scheduling jitter.
    assert max(gaps[1:]) < cadence + sample_seconds / 2
    # The window still ends with a sample at its deadline.
    assert taken[-1] - begin >= window


# --- Founder ruling 2026-10-27 -------------------------------------------------------------------
# 1. Steady-state SLOs exclude the fault window; the drill runs after the measured window and is
#    judged on recovery, data integrity and stuck GP jobs.
# 2. GP is out of the GA capacity envelope and is reported as Preview evidence.

from datetime import timedelta  # noqa: E402

import httpx  # noqa: E402

import capacity_evidence as emitter  # noqa: E402
sys.path.insert(0, str(Path(__file__).resolve().parent))
from test_capacity_observations import emit, observations  # noqa: E402


def _post_window_drill(source, passed=True):
    """Move the fixture's recoveries after the window and attach drill evidence."""
    end = emitter.timestamp(source["window"]["endedAt"])
    source["recoveries"] = [dict(event, injectedAt=collector_module.iso(end+timedelta(seconds=10*i+1)),
                                 detectedAt=collector_module.iso(end+timedelta(seconds=10*i+2)),
                                 recoveredAt=collector_module.iso(end+timedelta(seconds=10*i+5)))
                            for i, event in enumerate(source["recoveries"])]
    source["faultDrill"] = dict(window=dict(startedAt=collector_module.iso(end+timedelta(seconds=1)),
                                            endedAt=collector_module.iso(end+timedelta(seconds=40))),
                                excludedFromMeasuredWindow=True, recoveries=source["recoveries"], recovered=True,
                                dataIntegrity=dict(passed=True), gpJobs=dict(stuckJobIds=[] if passed else ["gp-1"], passed=passed),
                                failures=[], passed=passed)
    return source


def test_receipt_marks_the_post_window_fault_period():
    result, _ = emit(_post_window_drill(observations()))
    assert result["status"] == "completed"
    assert result["faultDrill"]["excludedFromMeasuredWindow"] is True
    fault = result["signals"]["recoveryTimeSeconds"]["recoveryEvidence"]["faultWindow"]
    assert fault == result["faultDrill"]["window"]
    assert fault["startedAt"] >= result["window"]["endedAt"]
    assert result["signals"]["recoveryTimeSeconds"]["value"] == 4


def test_a_failed_drill_cannot_claim_completion():
    result, _ = emit(_post_window_drill(observations(), passed=False))
    assert result["status"] == "incomplete"
    assert result["faultDrill"]["gpJobs"]["stuckJobIds"] == ["gp-1"]


def test_gp_requests_are_preview_evidence_not_the_ga_slo_population():
    source = observations()
    # The failed run's shape: a flood of in-band GP refusals beside healthy GA traffic.
    source["requests"][0]["buckets"].append(dict(count=100000, durationMs=1500, httpStatus=200, inBandError=True, protocol="GPServer"))
    source["requestCount"] += 100000
    result, _ = emit(source)
    baseline, _ = emit(observations())
    for name in ("availability", "errorRate", "p95LatencyMs", "p99LatencyMs", "throughputRps"):
        assert result["signals"][name]["value"] == baseline["signals"][name]["value"], name
    assert result["gaPopulation"] == dict(excludedProtocols=["GPServer"], requestCount=4320000, totalRequestCount=4420000)
    gp = result["previewEvidence"]["gp"]
    assert gp["tier"] == "preview"
    assert gp["requests"] == dict(count=100000, errors=100000, p95LatencyMs=1500, p99LatencyMs=1500)


def test_unfilled_gp_queue_is_reported_as_preview_and_does_not_decide_ga():
    source = observations()
    source["workloads"][3]["dimensions"]["gpQueueDepth"] = 1
    result, _ = emit(source)
    assert result["status"] == "completed"
    queue = result["workloads"]["gpQueueDepth"]
    assert (queue["tier"], queue["status"], queue["observed"]) == ("preview", "not-exercised", 1)
    assert result["previewEvidence"]["gp"]["workloads"]["gpQueueDepth"] == queue
    assert result["workloads"]["layersPerService"]["tier"] == "ga"


def _drill_collector(monkeypatch, job_states):
    import argparse

    collector = collector_module.Collector(argparse.Namespace(base_url="http://fixture", admin_key="fixture"), LOCK)
    start = collector_module.now() - timedelta(seconds=5)
    collector.ledger.started, collector.ledger.ended = start, start + timedelta(seconds=1)
    collector.payload_id = 10000
    collector.payload_sha256 = "sha"
    collector.driver.gp_pending = {job: {} for job in job_states}

    async def faults(client):
        # Drill-time traffic goes through the recording client but must not join the window.
        await client.get("http://fixture/rest/services/test/FeatureServer/0/query?f=json")
        collector.recoveries.extend(dict(dependency=name) for name in ("worker", "database", "redis"))

    async def integrity(client):
        return dict(passed=True)

    collector.faults = faults
    collector.data_integrity = integrity

    def serve(request):
        job = request.url.path.rsplit("/", 1)[-1]
        return httpx.Response(200, json={"jobStatus": job_states.get(job, "esriJobSucceeded")})

    return collector, httpx.MockTransport(serve)


def test_drill_runs_outside_the_measured_window_and_is_not_recorded(monkeypatch):
    collector, transport = _drill_collector(monkeypatch, {"gp-1": "esriJobSucceeded", "gp-2": "esriJobFailed"})

    async def exercise():
        recording = collector_module.RecordingTransport(collector.ledger)
        recording.transport = transport
        async with httpx.AsyncClient(transport=recording) as client:
            gp = asyncio.create_task(asyncio.sleep(0))
            return await collector.drill(client, gp)

    drill = asyncio.run(exercise())
    assert collector.ledger.observed_count == 0
    assert drill["window"]["startedAt"] >= collector_module.iso(collector.ledger.ended)
    assert drill["excludedFromMeasuredWindow"] is True
    assert drill["gpJobs"] == dict(outstandingAtDrillEnd=2, terminalStatuses={"esriJobSucceeded": 1, "esriJobFailed": 1},
                                   stuckJobIds=[], drainSeconds=drill["gpJobs"]["drainSeconds"], drainTimeoutSeconds=300, passed=True)
    assert drill["passed"] is True
    assert collector.failures == []


def test_drill_reports_stuck_gp_jobs(monkeypatch):
    collector, transport = _drill_collector(monkeypatch, {"gp-1": "esriJobSucceeded", "gp-stuck": "esriJobExecuting"})

    async def exercise():
        async with httpx.AsyncClient(transport=transport) as client:
            return await collector.drain_gp(client, timeout=0.05)

    gp_jobs = asyncio.run(exercise())
    assert gp_jobs["stuckJobIds"] == ["gp-stuck"] and gp_jobs["passed"] is False


def test_collector_no_longer_injects_faults_inside_the_window():
    import inspect

    run = inspect.getsource(collector_module.Collector.run)
    assert run.index("sample_window") < run.index("self.drill(")
    assert "create_task(self.faults" not in run
    assert "steady_seconds/2" not in inspect.getsource(collector_module.Collector.faults)


def test_drain_deadline_bounds_every_status_poll(monkeypatch):
    """Slow status polls cannot run a batch past the budget and then report success (#6703 review)."""
    jobs = {f"gp-{index}": "esriJobSucceeded" for index in range(10)}
    collector, _ = _drill_collector(monkeypatch, jobs)

    async def slow(request):
        await asyncio.sleep(0.05)
        return httpx.Response(200, json={"jobStatus": "esriJobSucceeded"})

    async def exercise():
        async with httpx.AsyncClient(transport=httpx.MockTransport(slow)) as client:
            return await collector.drain_gp(client, timeout=0.12)

    gp_jobs = asyncio.run(exercise())
    # Ten 50 ms polls cannot all finish inside 120 ms: the late ones stay unresolved, never "passed".
    assert gp_jobs["passed"] is False
    assert gp_jobs["stuckJobIds"]
    assert gp_jobs["drainSeconds"] < 0.12 + 0.1
    assert sum(gp_jobs["terminalStatuses"].values()) + len(gp_jobs["stuckJobIds"]) == 10
