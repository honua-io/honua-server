import copy
import importlib.util
import json
import re
import subprocess
import tempfile
import unittest
from pathlib import Path


def module(name):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(name + ".py"))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


binding = module("gp-candidate-binding")
recovery = module("gp-store-recovery")
proxy = module("gp-terminal-proxy")


PRODUCER = Path(__file__).with_name("gp-terminal-recovery.sh")


def terminal_recovery_evidence(job, sha, size):
    """Run gp-terminal-recovery.sh's own evidence jq program, so the fixture is the producer's layout."""
    program = re.search(r"\\\r?\n\s*'(\{submitted_job:.*?\})' > \"\$scenario_evidence_file\"",
                        PRODUCER.read_text(), re.S)
    if program is None:
        raise AssertionError("gp-terminal-recovery.sh evidence program not found")
    before = {"operationId": job, "status": 3, "claimedBy": "worker-1", "version": 4, "attemptCount": 1,
              "artifactReferences": ["{}"]}
    fence = {"operationId": job, "barrier": "terminal-committed-registration-pending", "redis_reply": ":1",
             "reply_forwarded": False, "workerId": "worker-1", "terminal_record": before}
    package = {"resultPackageId": f"{job}:v4", "status": 5, "artifacts": [{"artifactId": f"{job}:artifact:1",
               "metadata": {"raster.output.checksum": "sha256:" + sha, "raster.output.sizeBytes": str(size)}}]}
    with tempfile.TemporaryDirectory() as temp:
        files = {}
        for name, value in (("fence", fence), ("before", before), ("final", before), ("package", package)):
            files[name] = Path(temp) / f"{name}.json"
            files[name].write_text(json.dumps(value))
        out = subprocess.run(
            ["jq", "-n", "--arg", "job", job, "--arg", "before_file", "before.geojson",
             "--arg", "after_file", "after.geojson", "--arg", "sha", sha, "--argjson", "bytes", str(size),
             *[arg for name in ("fence", "before", "final", "package")
               for arg in ("--slurpfile", name, str(files[name]))], program.group(1)],
            check=True, capture_output=True, text=True)
    return json.loads(out.stdout)


class CandidateBindingTests(unittest.TestCase):
    def test_repin_and_missing_store_recovery_reject_previously_passing_receipt(self):
        pin = {"source_sha": "1" * 40, "server_image": "server@sha256:" + "2" * 64}
        worker = "worker@sha256:" + "3" * 64
        identity = {"requested": {**pin, "worker_image": worker}, "observed": {}}
        for host in ("server", "server-peer", "worker"):
            image = worker if host == "worker" else pin["server_image"]
            identity["observed"][host] = {"revision": pin["source_sha"],
                                         "image_ref": image, "repo_digests": [image]}
        names = ["topology", "output-store-attestation", "output-store-dr", "cleanup"]
        summary = {"lane": "output-store-dr", "declared_scenarios": names,
                   "missing_scenarios": [], "duplicate_receipts": [], "failed": 0, "passed": 4,
                   "scenarios": [{"scenario": name, "outcome": "pass", "candidate": identity}
                                 for name in names]}
        summary["scenarios"][2]["evidence"] = {
            "artifact": {"sha256_before": "a" * 64, "sha256_after": "a" * 64},
            "recovery": {"substrates": [{"store": store, "original_destroyed": True,
                                         "restored_into_empty_store": True,
                                         "files_before": "checksum file", "files_after": "checksum file"}
                                        for store in ("postgres", "redis", "gp-output")]}}
        self.assertEqual(pin, binding.verify(pin, summary)["candidate"])
        with self.assertRaisesRegex(ValueError, "another candidate"):
            binding.verify({**pin, "server_image": "server@sha256:" + "4" * 64}, summary)
        for mutation in ("missing-store", "retained-original", "corrupt-bytes", "wrong-worker", "skipped"):
            bad = copy.deepcopy(summary)
            proof = bad["scenarios"][2]["evidence"]
            if mutation == "missing-store":
                proof["recovery"]["substrates"].pop()
            elif mutation == "retained-original":
                proof["recovery"]["substrates"][2]["original_destroyed"] = False
            elif mutation == "corrupt-bytes":
                proof["artifact"]["sha256_after"] = "b" * 64
            elif mutation == "wrong-worker":
                bad["scenarios"][0]["candidate"]["observed"]["worker"]["revision"] = "0" * 40
            else:
                bad["scenarios"][2]["outcome"] = "skipped"
            with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                binding.verify(pin, bad)

    def test_signed_envelope_rejects_missing_gp_proof_and_repin(self):
        with self.assertRaises(KeyError):
            binding.verify_envelope({}, {"schema": "honua.dr-drill-receipt/v2"})
        with self.assertRaisesRegex(ValueError, "current manifest"):
            binding.verify_envelope({"server_digest": "new"}, {
                "geoprocessingOutputs": {"candidate": {"server_digest": "old"}}})

    def test_crash_consumer_requires_all_six_injections_and_real_terminal_fence(self):
        pin = {"source_sha": "1" * 40, "server_image": "server@sha256:" + "2" * 64}
        worker = "worker@sha256:" + "3" * 64
        identity = {"requested": {**pin, "worker_image": worker}, "observed": {}}
        for host in ("server", "server-peer", "worker"):
            image = worker if host == "worker" else pin["server_image"]
            identity["observed"][host] = {"revision": pin["source_sha"],
                                         "image_ref": image, "repo_digests": [image]}
        scenarios = [{"scenario": n, "outcome": "pass", "candidate": identity}
                     for n in ("topology", "cleanup")]
        for boundary in ("output-bytes-written-unpublished", "artifact-reference-published-terminal-cas-pending",
                         "terminal-committed-registration-pending"):
            for mode in ("worker", "store"):
                scenarios.append({"scenario": f"crash-{boundary}-{mode}", "outcome": "pass",
                    "candidate": identity, "job": {"id": "fixture-job", "terminal_state": "successful"},
                    "output": {"sha256": "a" * 64, "bytes": 2048},
                    "disruptions": [{"component": mode, "boundary": boundary,
                                     "action": "SIGKILL" if mode == "worker" else "hide-marker-and-bytes"}],
                    "evidence": {"fence": {"barrier": boundary, "operationId": "fixture-job",
                                           "reply_forwarded": False, "redis_reply": ":1"},
                                 "sha256_before": "a" * 64, "sha256_after": "a" * 64,
                                 "job_before": {"status": 3}, "job_after": {"artifactReferences": [{}]},
                                 "result_package_before": 0, "result_package_after": 1}})
        summary = {"lane": "crash-boundaries", "declared_scenarios": [s["scenario"] for s in scenarios],
                   "missing_scenarios": [], "duplicate_receipts": [], "failed": 0, "passed": 8,
                   "scenarios": scenarios}
        # gp-store-crash.sh routes the terminal worker cell through gp-terminal-recovery.sh.
        terminal = next(s for s in scenarios if s["scenario"] == binding.TERMINAL_WORKER_CASE)
        terminal["evidence"] = terminal_recovery_evidence("fixture-job", "a" * 64, 2048)
        binding.verify_crashes(pin, summary)
        for mutation in ("missing-cell", "missing-injection", "duplicate-output", "inline", "uncommitted", "registered"):
            bad = copy.deepcopy(summary)
            case = bad["scenarios"][-1]
            if mutation == "missing-cell":
                bad["scenarios"].pop()
            elif mutation == "missing-injection":
                case["disruptions"] = []
            elif mutation == "duplicate-output":
                case["evidence"]["job_after"]["artifactReferences"].append({})
            elif mutation == "inline":
                case["output"]["bytes"] = 512
            elif mutation == "uncommitted":
                case["evidence"]["fence"]["redis_reply"] = ":0"
            else:
                case["evidence"]["result_package_before"] = 1
            with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                binding.verify_crashes(pin, bad)

    def test_terminal_worker_cell_consumes_the_terminal_recovery_producer(self):
        """The producer's real layout (no sha256_before/job_before) passes; each broken fact fails."""
        scenario = {"scenario": binding.TERMINAL_WORKER_CASE, "job": {"id": "job-1", "terminal_state": "successful"},
                    "output": {"sha256": "c" * 64, "bytes": 4096},
                    "evidence": terminal_recovery_evidence("job-1", "c" * 64, 4096)}
        self.assertNotIn("sha256_before", scenario["evidence"])
        binding.verify_terminal_recovery(scenario, scenario["evidence"])
        mutations = {
            "acknowledged": lambda p, s: p["fence"].__setitem__("reply_forwarded", True),
            "uncommitted": lambda p, s: p["fence"].__setitem__("redis_reply", ":0"),
            "other-job": lambda p, s: p["fence"].__setitem__("operationId", "job-2"),
            "fence-record": lambda p, s: p["fence"]["terminal_record"].__setitem__("version", 5),
            "record-changed": lambda p, s: p["terminal_final"].__setitem__("version", 5),
            "not-terminal": lambda p, s: (p["terminal_before"].__setitem__("status", 2),
                                          p["terminal_final"].__setitem__("status", 2),
                                          p["fence"]["terminal_record"].__setitem__("status", 2)),
            "registered-early": lambda p, s: p.__setitem__("result_package_before", 1),
            "registered-before-read": lambda p, s: p.__setitem__("result_package_before_read", 1),
            "not-registered": lambda p, s: p.__setitem__("result_package_after", 0),
            "not-killed": lambda p, s: p.__setitem__("worker_exit_code", 0),
            "duplicate-artifact": lambda p, s: p["result_package"]["artifacts"].append({}),
            "checksum": lambda p, s: p["result_package"]["artifacts"][0]["metadata"].__setitem__(
                "raster.output.checksum", "sha256:" + "d" * 64),
            "bytes-changed": lambda p, s: s["output"].__setitem__("sha256", "d" * 64),
            "inline": lambda p, s: (p.__setitem__("bytes", 512), s["output"].__setitem__("bytes", 512)),
            "failed-job": lambda p, s: s["job"].__setitem__("terminal_state", "failed"),
        }
        for name, mutate in mutations.items():
            bad = copy.deepcopy(scenario)
            mutate(bad["evidence"], bad)
            with self.subTest(mutation=name), self.assertRaises((ValueError, KeyError, TypeError)):
                binding.verify_terminal_recovery(bad, bad["evidence"])

    def test_repin_changes_both_image_identity_and_manifest_binding(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "manifest.json"
            value = {"components": {"honua-server": {
                "image": "ghcr.io/honua-io/honua-server:nightly",
                "sha": "1" * 40, "digest": "sha256:" + "2" * 64}}}
            path.write_text(json.dumps(value))
            first = binding.candidate(path)
            value["components"]["honua-server"]["digest"] = "sha256:" + "3" * 64
            path.write_text(json.dumps(value))
            second = binding.candidate(path)
            self.assertNotEqual(first["server_image"], second["server_image"])
            self.assertNotEqual(first["manifest_sha256"], second["manifest_sha256"])
            for field, invalid in (("digest", "latest"), ("sha", "1234567"),
                                   ("image", "ghcr.io/other/server:nightly")):
                broken = copy.deepcopy(value)
                broken["components"]["honua-server"][field] = invalid
                path.write_text(json.dumps(broken))
                with self.subTest(field=field), self.assertRaises(ValueError):
                    binding.candidate(path)

    def test_existing_replacement_receipt_cannot_claim_disaster_recovery(self):
        # A passing replacement run does not become a restored-output proof by
        # adding a candidate identity or changing the outer schema.
        summary = {"lane": "output-store", "declared_scenarios": [
            "topology", "output-store-attestation", "cleanup"],
            "missing_scenarios": [], "duplicate_receipts": [], "passed": 3, "failed": 0}
        with self.assertRaisesRegex(ValueError, "required GP restore"):
            binding.verify({}, summary)

    def test_recovery_rejects_nonqualification_project_before_docker(self):
        with self.assertRaisesRegex(ValueError, "isolated GP"):
            recovery.recover(Path("unused"), "customer-production", Path("unused"), Path("unused"))

    def test_recovery_rejects_backup_inside_destroyed_store(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            with self.assertRaisesRegex(ValueError, "outside the volume"):
                recovery.recover(Path("unused"), "honua-gp-reliability-test", root, root / "backup")


class TerminalProxyTests(unittest.IsolatedAsyncioTestCase):
    async def test_committed_reply_is_withheld_until_fence_release(self):
        import asyncio
        from unittest.mock import AsyncMock, patch

        class Writer:
            def __init__(self):
                self.bytes = b""

            def write(self, data):
                self.bytes += data

            async def drain(self):
                pass

            def close(self):
                pass

        notification = b"*3\r\n$7\r\nmessage\r\n$1\r\nc\r\n$1\r\nx\r\n"
        for response, notice in ((b":1\r\n", b""), (b":0\r\n", b""), (b":1\r\n", notification)):
            with self.subTest(response=response, notification=bool(notice)), tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                directory = root / "job-1"
                directory.mkdir()
                fence = directory / "terminal-committed-registration-pending"
                fence.with_suffix(".arm").touch()
                record = json.dumps({"operationId": "job-1", "status": "succeeded"}).encode()
                command = [b"EVALSHA", b"sha", b"1", b"key", b"1", record]
                raw = b"*6\r\n" + b"".join(
                    b"$" + str(len(x)).encode() + b"\r\n" + x + b"\r\n" for x in command)
                reader, upstream = asyncio.StreamReader(), asyncio.StreamReader()
                reader.feed_data(raw)
                upstream.feed_data(notice + response)
                output, forwarded = Writer(), Writer()
                with patch.object(proxy.asyncio, "open_connection", new=AsyncMock(return_value=(upstream, forwarded))):
                    task = asyncio.create_task(proxy.client(reader, output, root))
                    try:
                        async with asyncio.timeout(2):
                            while not (fence.with_suffix(".ready.json").exists() or output.bytes.endswith(response)):
                                await asyncio.sleep(0.005)
                        self.assertEqual(raw, forwarded.bytes)
                        if response == b":1\r\n":
                            self.assertEqual(notice, output.bytes)
                            evidence = json.loads(fence.with_suffix(".ready.json").read_text())
                            self.assertFalse(evidence["reply_forwarded"])
                            self.assertEqual("succeeded", evidence["terminal_record"]["status"])
                            fence.with_suffix(".release").touch()
                            async with asyncio.timeout(2):
                                while not output.bytes.endswith(response):
                                    await asyncio.sleep(0.005)
                        else:
                            self.assertFalse(fence.with_suffix(".ready.json").exists())
                        self.assertEqual(notice + response, output.bytes)
                    finally:
                        reader.feed_eof()
                        await asyncio.wait_for(task, 2)

    async def test_resp_pipeline_preserves_binary_bulk_and_cas_reply(self):
        import asyncio
        stream = asyncio.StreamReader()
        first = b"*2\r\n$3\r\nSET\r\n$5\r\na\x00b\r\n\r\n"
        stream.feed_data(first + b":1\r\n")
        stream.feed_eof()
        self.assertEqual((first, [b"SET", b"a\x00b\r\n"]), await proxy.frame(stream))
        self.assertEqual((b":1\r\n", b"1"), await proxy.frame(stream))

    async def test_terminal_fence_only_matches_successful_job_cas_commands(self):
        record = {"operationId": "job-1", "status": "succeeded"}
        command = [b"EVALSHA", b"script", b"1", b"controlplane:job:job-1",
                   b"2", json.dumps(record).encode()]
        self.assertEqual(record, proxy.terminal_job(command))
        for status in ("running", "failed", "cancelled"):
            changed = command[:-1] + [json.dumps({**record, "status": status}).encode()]
            self.assertIsNone(proxy.terminal_job(changed))
        self.assertIsNone(proxy.terminal_job([b"GET", command[-1]]))

    async def test_terminal_fence_recognizes_the_candidate_persisted_numeric_enum(self):
        # The pinned image's Redis/AOF record uses numeric ExecutionJobStatus:
        # Queued=0, Provisioning=1, Running=2, Succeeded=3, Failed=4, Cancelled=5.
        record = {"operationId": "gp-candidate-record", "status": 3, "version": 8}
        command = [b"EVALSHA", b"script", b"1", b"controlplane:job:gp-candidate-record",
                   b"7", json.dumps(record).encode()]
        self.assertEqual(record, proxy.terminal_job(command))
        for status in (0, 1, 2, 4, 5):
            self.assertIsNone(proxy.terminal_job(command[:-1] + [json.dumps({**record, "status": status}).encode()]))


if __name__ == "__main__":
    unittest.main()
