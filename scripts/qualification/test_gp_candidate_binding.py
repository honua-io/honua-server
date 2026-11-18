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


PRODUCER = Path(__file__).with_name("gp-terminal-recovery.sh")


def committed_descriptor(job, sha, size):
    """A staged-output descriptor as the worker commits it in the terminal record (serialized JSON)."""
    return json.dumps({"outputType": "staged-object", "jobId": job, "attemptNumber": 1,
                       "content": {"mediaType": "application/geo+json", "sizeBytes": size,
                                   "checksum": {"algorithm": "sha256", "value": sha}}})


def terminal_recovery_evidence(job, sha, size, committed_sha=None):
    """Run gp-terminal-recovery.sh's own evidence jq program, so the fixture is the producer's layout."""
    program = re.search(r"\\\r?\n\s*'(\{submitted_job:.*?\})' > \"\$scenario_evidence_file\"",
                        PRODUCER.read_text(), re.S)
    if program is None:
        raise AssertionError("gp-terminal-recovery.sh evidence program not found")
    before = {"operationId": job, "status": 3, "claimedBy": "worker-1", "version": 4, "attemptCount": 1,
              "artifactReferences": [committed_descriptor(job, committed_sha or sha, size)]}
    fence = {"operationId": job, "barrier": "terminal-committed-registration-pending", "workerId": "worker-1",
             "workerProcessId": 7, "released": False}
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


def crash_summary():
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
                                       "workerId": "worker-1", "workerProcessId": 7},
                             "sha256_before": "a" * 64, "sha256_after": "a" * 64,
                             "job_before": {"status": 3}, "job_after": {"artifactReferences": [{}]},
                             "result_package_before": 0, "result_package_after": 1}})
    summary = {"lane": "crash-boundaries", "declared_scenarios": [s["scenario"] for s in scenarios],
               "missing_scenarios": [], "duplicate_receipts": [], "failed": 0, "passed": 8,
               "scenarios": scenarios}
    # gp-store-crash.sh routes the terminal worker cell through gp-terminal-recovery.sh.
    terminal = next(s for s in scenarios if s["scenario"] == binding.TERMINAL_WORKER_CASE)
    terminal["evidence"] = terminal_recovery_evidence("fixture-job", "a" * 64, 2048)
    return pin, summary


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
                                        for store in ("postgres", "gp-output")]}}
        self.assertEqual(pin, binding.verify(pin, summary)["candidate"])
        with self.assertRaisesRegex(ValueError, "another candidate"):
            binding.verify({**pin, "server_image": "server@sha256:" + "4" * 64}, summary)
        for mutation in ("missing-store", "retained-original", "legacy-store", "corrupt-bytes", "wrong-worker", "skipped"):
            bad = copy.deepcopy(summary)
            proof = bad["scenarios"][2]["evidence"]
            if mutation == "missing-store":
                proof["recovery"]["substrates"].pop()
            elif mutation == "retained-original":
                proof["recovery"]["substrates"][1]["original_destroyed"] = False
            elif mutation == "legacy-store":
                proof["recovery"]["substrates"].append({"store": "unsupported-store"})
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
        pin, summary = crash_summary()
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
                case["evidence"]["fence"].pop("workerProcessId")
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
            "acknowledged": lambda p, s: p["fence"].__setitem__("released", True),
            "no-worker-process": lambda p, s: p["fence"].pop("workerProcessId"),
            "other-job": lambda p, s: p["fence"].__setitem__("operationId", "job-2"),
            "other-worker": lambda p, s: p["fence"].__setitem__("workerId", "worker-2"),
            "record-changed": lambda p, s: p["terminal_final"].__setitem__("version", 5),
            "not-terminal": lambda p, s: (p["terminal_before"].__setitem__("status", 2),
                                          p["terminal_final"].__setitem__("status", 2)),
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
            "descriptor-size": lambda p, s: [r.__setitem__("artifactReferences", [committed_descriptor("job-1", "c" * 64, 4097)])
                                             for r in (p["terminal_before"], p["terminal_final"])],
            "descriptor-inline": lambda p, s: [r.__setitem__("artifactReferences", [json.dumps({**json.loads(
                committed_descriptor("job-1", "c" * 64, 4096)), "outputType": "inline"})])
                for r in (p["terminal_before"], p["terminal_final"])],
            "descriptor-unparsable": lambda p, s: [r.__setitem__("artifactReferences", ["not json"])
                for r in (p["terminal_before"], p["terminal_final"])],
        }
        for name, mutate in mutations.items():
            bad = copy.deepcopy(scenario)
            mutate(bad["evidence"], bad)
            with self.subTest(mutation=name), self.assertRaises((ValueError, KeyError, TypeError)):
                binding.verify_terminal_recovery(bad, bad["evidence"])

    def test_recovered_bytes_must_be_the_bytes_committed_before_the_crash(self):
        """Evidence, output and package agree on a checksum the terminal record never committed: refused
        through the full verify_crashes entry point (Sol review of bb6d1088)."""
        pin, summary = crash_summary()
        terminal = next(s for s in summary["scenarios"] if s["scenario"] == binding.TERMINAL_WORKER_CASE)
        binding.verify_crashes(pin, summary)
        terminal["evidence"] = terminal_recovery_evidence("fixture-job", "a" * 64, 2048, committed_sha="b" * 64)
        with self.assertRaisesRegex(ValueError, "committed before the crash"):
            binding.verify_crashes(pin, summary)

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


if __name__ == "__main__":
    unittest.main()
