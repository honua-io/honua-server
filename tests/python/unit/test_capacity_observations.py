"""Independent populations and real HTTP outcomes for the capacity producer."""
from __future__ import annotations

import asyncio
import copy
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import sys
import subprocess
import zipfile

import aiohttp
from aiohttp import web
import pytest
import httpx

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "scripts/soak"))
import capacity_evidence as emitter
import collect_capacity as collector_module
from collect_capacity import RequestLedger, feature_bytes, iso, now, observe_request, padded_description

LOCK_PATH = ROOT / "tests/python/fixtures/capacity/lock.json"
LOCK = json.loads(LOCK_PATH.read_bytes())
REVISION = "a" * 40
IMAGE = "sha256:" + "b" * 64
ARTIFACT = "https://github.com/honua-io/honua-server/actions/runs/123/artifacts/456"


def observations():
    start = datetime(2026, 9, 29, 2, tzinfo=timezone.utc)
    window = dict(startedAt=iso(start), endedAt=iso(start+timedelta(hours=1)))
    ledger = []
    for minute in range(60):
        buckets = [dict(count=count, durationMs=latency, httpStatus=200, inBandError=False, protocol="FeatureServer")
                   for count, latency in ((68400, 200), (2880, 600), (720, 900))]
        if minute == 0:
            buckets[0]["count"] -= 2
            buckets.extend([dict(count=1, durationMs=200, httpStatus=status, inBandError=error, protocol="FeatureServer")
                            for status, error in ((500, False), (200, True))])
        ledger.append(dict(replica="honua", incarnation="container-1", startedAt=iso(start+timedelta(minutes=minute)),
                           endedAt=iso(start+timedelta(minutes=minute+1)), buckets=buckets))
    times = [iso(start+timedelta(minutes=i)) for i in range(61)]
    return dict(schema="honua.capacity-observations/v1", candidateIdentity=dict(serverRevision=REVISION, imageDigest=IMAGE),
                observedRevision=REVISION, window=window, lockSha256=emitter.digest(LOCK_PATH.read_bytes()),
                topology=dict(replicas=[dict(id="honua", failureDomain="local-docker-host", imageDigest=IMAGE)],
                              database=dict(kind="postgres", failureDomain="local-docker-host"),
                              redis=dict(kind="redis", failureDomain="local-docker-host"), gpWorkers=1),
                producer=dict(repository="honua-io/honua-server", workflowPath=".github/workflows/capacity-soak-candidate.yml",
                              workflowRef="honua-io/honua-server/.github/workflows/capacity-soak-candidate.yml@refs/heads/candidate",
                              sourceRevision=REVISION, runId=123, runAttempt=1, predicateType="https://slsa.dev/provenance/v1"),
                samplingFailures=[], populationMode="complete-disjoint-intervals", samplePeriodSeconds=60,
                requestCount=4320000, requests=ledger,
                metrics=[dict(at=at, worker=.2, database=.4, redis=.3, queueAgeSeconds=7) for at in times],
                workloads=[dict(at=at, dimensions=copy.deepcopy(LOCK["supportedEnvelope"]), executionMode="candidate-topology", proxy=False) for at in times],
                recoveries=[dict(dependency=name, failure="stop-start", probe="dependency-and-serving-query",
                                 injectedAt=iso(start+timedelta(minutes=30)), detectedAt=iso(start+timedelta(minutes=30, seconds=1)),
                                 recoveredAt=iso(start+timedelta(minutes=30, seconds=seconds)))
                            for name, seconds in (("worker", 2), ("database", 3), ("redis", 4))])


def emit(source=None):
    source = observations() if source is None else source
    payload = json.dumps(source, allow_nan=False).encode()
    return emitter.receipt(LOCK, emitter.digest(LOCK_PATH.read_bytes()), source, payload, ARTIFACT), payload


def test_analytical_population_values_and_metadata():
    result, payload = emit()
    expected = dict(availability=4319998/4320000, errorRate=2/4320000, p95LatencyMs=200, p99LatencyMs=600,
                    throughputRps=1200, queueAgeSeconds=7, saturationRatio=.4, recoveryTimeSeconds=4)
    assert {name: value["value"] for name, value in result["signals"].items()} == expected
    assert result["status"] == "completed"
    assert result["steadyStateSeconds"] == 3600
    assert result["rawArtifacts"][0]["sha256"] == emitter.digest(payload)
    assert result["signals"]["saturationRatio"]["observationPopulation"] == {"kind": "gauge", "sampleCount": 183}
    assert result["signals"]["throughputRps"]["observationPopulation"] == {"kind": "ratio", "numerator": 4320000, "denominator": 3600, "sampleCount": 4320000}
    for signal in result["signals"].values():
        assert signal["window"] == result["window"]
        assert signal["candidateIdentity"] == result["candidateIdentity"]
        assert signal["thresholdVerdict"]["passed"] is True
    for workload in result["workloads"].values():
        assert workload["sampleCount"] == 61
        assert workload["status"] == "exercised"


def test_maximum_payload_preserves_geometry_and_attributes():
    feature = dict(attributes=dict(objectid=10000, name="Hawaiʻi", description="initial", missing=None),
                   geometry=dict(x=-157.8, y=21.3, z=4, m=7, spatialReference=dict(wkid=4326)))
    original = copy.deepcopy(feature)
    feature["attributes"]["description"] = padded_description(feature, 1048576)
    assert len(feature_bytes(feature)) == 1048576
    assert feature["geometry"] == original["geometry"]
    assert feature["attributes"]["objectid"] == 10000
    assert feature["attributes"]["name"] == "Hawaiʻi"
    assert feature["attributes"]["missing"] is None


def test_zip_contains_exact_receipt_and_cited_raw_bytes(tmp_path):
    result, payload = emit()
    output = tmp_path / "capacity-evidence.zip"
    emitter.write_bundle(output, result, payload)
    with zipfile.ZipFile(output) as archive:
        assert sorted(archive.namelist()) == ["capacity-observations.json", "capacity-soak-receipt.json"]
        assert archive.read("capacity-observations.json") == payload
        assert json.loads(archive.read("capacity-soak-receipt.json")) == result


@pytest.mark.parametrize("field", ["worker", "database", "redis", "queueAgeSeconds"])
def test_missing_metric_is_not_defaulted_to_zero(field):
    source = observations()
    source["metrics"][5][field] = None
    result, _ = emit(source)
    assert result["status"] == "incomplete"
    signal = result["signals"]["queueAgeSeconds" if field == "queueAgeSeconds" else "saturationRatio"]
    assert signal["value"] is None
    assert signal["status"] == "unobserved"
    assert signal["thresholdVerdict"]["passed"] is False


def test_unfilled_queue_and_sampling_failures_cannot_claim_completion():
    source = observations()
    source["workloads"][3]["dimensions"]["gpQueueDepth"] = 0
    result, _ = emit(source)
    assert result["status"] == "incomplete"
    assert result["workloads"]["gpQueueDepth"]["observed"] == 0
    assert result["workloads"]["gpQueueDepth"]["status"] == "not-exercised"
    source = observations()
    source["samplingFailures"] = ["missed database sample"]
    assert emit(source)[0]["status"] == "incomplete"


def test_candidate_source_mismatch_refused_before_emitting():
    source = observations()
    source["producer"]["sourceRevision"] = "c" * 40
    with pytest.raises(ValueError, match="workflow source"):
        emit(source)
    with pytest.raises(ValueError, match="workflow source"):
        emitter.assert_candidate(REVISION, REVISION, "c"*40)


def test_legacy_aggregate_cannot_be_expanded_into_raw_observations():
    with pytest.raises(ValueError, match="not legacy aggregates"):
        emit({"allRequestCount": 4320000, "p95Ms": 200})


def test_artifact_from_another_run_is_refused():
    source = observations()
    payload = json.dumps(source).encode()
    with pytest.raises(ValueError, match="producer Actions run"):
        emitter.receipt(LOCK, source["lockSha256"], source, payload, ARTIFACT.replace("/123/", "/124/"))


def test_disjoint_interval_boundaries_include_empty_intervals():
    start = datetime(2026, 9, 29, 2, tzinfo=timezone.utc)
    ledger = RequestLedger("honua", "container-1")
    ledger.started = start
    ledger.record(start, 10, 200, False, "FeatureServer")
    ledger.record(start+timedelta(seconds=30), 20, 500, False, "FeatureServer")
    ledger.ended = start+timedelta(seconds=95)
    ledger.record(ledger.ended, 99, 200, False, "FeatureServer")
    rows = ledger.intervals()
    assert len(rows) == 4
    assert [sum(b["count"] for b in row["buckets"]) for row in rows] == [1, 1, 0, 0]
    assert ledger.observed_count == 2
    assert rows[0]["startedAt"] == iso(start)
    assert rows[-1]["endedAt"] == iso(ledger.ended)
    assert all(a["endedAt"] == b["startedAt"] for a, b in zip(rows, rows[1:]))


def test_real_http_outcomes_include_in_band_errors_and_timeouts():
    async def exercise():
        async def respond(request):
            kind = request.match_info["kind"]
            if kind == "timeout":
                await asyncio.sleep(.1)
            if kind == "malformed":
                return web.Response(text="{", content_type="application/json")
            return web.json_response({"error": {"code": 400}} if kind == "inband" else {"features": []}, status=500 if kind == "server" else 200)
        app = web.Application()
        app.router.add_get("/{kind}", respond)
        runner = web.AppRunner(app)
        await runner.setup()
        site = web.TCPSite(runner, "127.0.0.1", 0)
        await site.start()
        port = site._server.sockets[0].getsockname()[1]
        ledger = RequestLedger("honua", "fixture")
        ledger.started = now()
        try:
            async with aiohttp.ClientSession(timeout=aiohttp.ClientTimeout(total=.03)) as session:
                for kind in ("success", "server", "inband", "malformed", "timeout"):
                    await observe_request(session, f"http://127.0.0.1:{port}/{kind}", ledger, "/rest/services/test/FeatureServer/0/query")
            ledger.ended = now()
            buckets = ledger.intervals()[0]["buckets"]
            outcomes = {(status, error): sum(b["count"] for b in buckets if (b["httpStatus"], b["inBandError"]) == (status, error))
                        for status, error in ((200, False), (500, False), (200, True), (599, True))}
            assert outcomes == {(200, False): 1, (500, False): 1, (200, True): 2, (599, True): 1}
            assert ledger.observed_count == 5
            assert all(b["durationMs"] >= 0 and b["protocol"] == "FeatureServer" for b in buckets)
            assert next(b["durationMs"] for b in buckets if b["httpStatus"] == 599) >= 25
        finally:
            await runner.cleanup()
    asyncio.run(exercise())


def test_sampler_retains_served_dimensions_and_actual_dependency_measurements(monkeypatch):
    import argparse
    from types import SimpleNamespace

    feature = dict(attributes=dict(objectid=10000, description=""), geometry=dict(x=-157.8, y=21.3, spatialReference=dict(wkid=4326)))
    feature["attributes"]["description"] = padded_description(feature, 1048576)

    async def docker(*args):
        if args[:2] == ("docker", "inspect"):
            return json.dumps([{"Config": {"Env": ["ExecutionAdmission__MaxConcurrentJobsGlobal=1"]}}])
        if args[:2] == ("docker", "stats"):
            return "40.00%"
        if args[:2] == ("docker", "info"):
            return "4"
        if "INFO" in args:
            return "# Clients\nconnected_clients:25\nblocked_clients:0\n"
        assert "maxclients" in args
        return "maxclients\n100"

    monkeypatch.setattr(collector_module, "command", docker)

    async def exercise():
        async def serve(request):
            if request.path == "/api/v1/capabilities/manifest":
                document = dict(server=dict(deploymentRevision=REVISION, deploymentEnvironment="Production"),
                                scope=dict(tenantSource="Default"), capabilities=[])
            elif request.path == "/rest/services":
                document = dict(services=[dict(name="test")])
            elif request.path.endswith("/FeatureServer"):
                document = dict(layers=[dict(id=i) for i in range(4)])
            elif request.path.endswith("/query"):
                document = dict(count=10000) if request.query.get("returnCountOnly") == "true" else dict(features=[feature])
            else:
                assert request.path == "/monitoring/metrics/connection-pool"
                document = dict(utilization=.4, hasUtilizationData=True)
            return web.json_response(document)
        app = web.Application()
        app.router.add_get("/{tail:.*}", serve)
        runner = web.AppRunner(app)
        await runner.setup()
        site = web.TCPSite(runner, "127.0.0.1", 0)
        await site.start()
        port = site._server.sockets[0].getsockname()[1]
        try:
            collector = collector_module.Collector(argparse.Namespace(base_url=f"http://127.0.0.1:{port}", admin_key="fixture"), LOCK)
            collector.payload_id = 10000
            collector.users = [SimpleNamespace(done=lambda: False), SimpleNamespace(done=lambda: False), SimpleNamespace(done=lambda: True)]
            collector.driver.gp.add(queueDepth=3, executing=1, oldestQueueAgeSeconds=7)
            async with httpx.AsyncClient() as client:
                at = await collector.sample(client)
            assert collector.failures == []
            assert collector.metrics == [dict(at=iso(at), worker=.1, database=.4, redis=.25, queueAgeSeconds=7)]
            # A configured target of 170 users / 100 queued jobs must not replace
            # two live users / three actually observed queued jobs.
            assert collector.workloads[0]["dimensions"] == dict(tenants=1, services=1, layersPerService=4, featuresPerLayer=10000,
                                                               maximumFeaturePayloadBytes=1048576, concurrentVirtualUsers=2, gpWorkers=1, gpQueueDepth=3)
            assert collector.workloads[0]["proofs"]["layerCounts"] == [dict(count=10000)]*4
        finally:
            await runner.cleanup()
    asyncio.run(exercise())


def verify_release_contract(tools_path: Path, output: Path):
    """Run the real release verifier, including wrong-source ZIP attestation rejection.

    This is an explicit integration entrypoint, not a skipped dependency in the unit suite.
    The certificate-shaped input represents gh's already-verified output; it is
    never represented as cryptographic or hosted-run qualification.
    """
    sys.path.insert(0, str(tools_path))
    import check_capacity_soak as gate
    from extract_capacity_evidence import extract
    result, payload = emit()
    bundle = output / "capacity-evidence.zip"
    emitter.write_bundle(bundle, result, payload)
    extract(bundle, output / "extracted")
    assert gate.evaluate(LOCK, result, emitter.digest(LOCK_PATH.read_bytes()), REVISION, output / "extracted", IMAGE) == []
    bundle_hash = emitter.digest(bundle.read_bytes())
    certificate = dict(buildSignerURI="https://github.com/"+result["producer"]["workflowRef"],
                       sourceRepositoryURI="https://github.com/honua-io/honua-server", sourceRepositoryDigest=REVISION,
                       runnerEnvironment="github-hosted", runInvocationURI="https://github.com/honua-io/honua-server/actions/runs/123/attempts/1")
    verified = [dict(verificationResult=dict(signature=dict(certificate=certificate), statement=dict(
        subject=[dict(name=bundle.name, digest=dict(sha256=bundle_hash))], predicateType="https://slsa.dev/provenance/v1",
        predicate=dict(buildDefinition=dict(resolvedDependencies=[dict(digest=dict(gitCommit=REVISION))])))))]
    assert gate.bind_attestation(verified, bundle_hash, result, REVISION) == []
    wrong = copy.deepcopy(verified)
    wrong[0]["verificationResult"]["signature"]["certificate"]["sourceRepositoryDigest"] = "c" * 40
    wrong[0]["verificationResult"]["statement"]["predicate"]["buildDefinition"]["resolvedDependencies"][0]["digest"]["gitCommit"] = "c" * 40
    failures = gate.bind_attestation(wrong, bundle_hash, result, REVISION)
    assert any("attested source commit" in failure for failure in failures), failures
    assert any("SLSA resolved source" in failure for failure in failures), failures
    verification_path = output / "verified.json"
    cli = [sys.executable, str(tools_path / "check_capacity_soak.py"), "--lock", str(LOCK_PATH),
           "--receipt", str(output / "extracted/capacity-soak-receipt.json"), "--artifact-root", str(output / "extracted"),
           "--expected-revision", REVISION, "--expected-image-digest", IMAGE, "--bundle", str(bundle), "--attestation", str(verification_path)]
    verification_path.write_text(json.dumps(verified))
    passed = subprocess.run(cli, capture_output=True, text=True)
    assert passed.returncode == 0, passed.stdout + passed.stderr
    verification_path.write_text(json.dumps(wrong))
    refused = subprocess.run(cli, capture_output=True, text=True)
    assert refused.returncode == 1 and "attested source commit" in refused.stdout, refused.stdout + refused.stderr
    print("release checker: positive ZIP accepted; wrong-source ZIP attestation rejected")


if __name__ == "__main__":
    import tempfile
    with tempfile.TemporaryDirectory() as directory:
        verify_release_contract(Path(sys.argv[1]).resolve(), Path(directory))
