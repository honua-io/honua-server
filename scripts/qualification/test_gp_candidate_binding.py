import copy
import importlib.util
import json
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
