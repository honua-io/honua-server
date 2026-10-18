"""Real file/HTTP checks for bounded immutable GP artifact transmission."""
from dataclasses import FrozenInstanceError
import hashlib
import http.client
import json
import os
import socket
from pathlib import Path
import tempfile
import threading
import tracemalloc
import unittest

from workspace import DEFAULT_LIMITS, WorkspaceLimits, command, snapshot
from workspace_scope import Access, ScopedWorkspace, atomic_json, read_json
from workspace_service import Workspace, create_server

IMAGE = "sha256:" + "a" * 64
OWNER = Access("human", "human", "workspace", "environment", "tenant")


class StreamingWorkspaceTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.project = self.root / "project"
        self.project.mkdir()
        (self.project / "honua-gp.yaml").write_text("schema: honua.gp-process/v1\n")
        (self.project / "process.py").write_text("original code")
        self.limits = WorkspaceLimits(chunk_bytes=64 * 1024)
        self.workspace = Workspace(self.project, IMAGE, self.root / "runs", self.limits)
        self.scoped = ScopedWorkspace(self.workspace)
        identity = self.scoped.identity(OWNER)
        self.scoped.bind(OWNER, identity["projectId"])
        self.frozen = self.scoped.snapshot(OWNER, {"projectId": identity["projectId"], "expectedRevision": identity["revision"], "sourceDigest": identity["sourceDigest"]})
        self.run_id = "b" * 32

    def tearDown(self):
        self.scoped.lock.file.close()
        self.temporary.cleanup()

    def seed(self, size=256 * 1024):
        path = self.workspace.runs / self.run_id / "output/artifacts/features.geojson"
        path.parent.mkdir(parents=True)
        chunk, digest = b"a" * 65536, hashlib.sha256()
        with path.open("wb") as stream:
            remaining = size
            while remaining:
                data = chunk[:min(len(chunk), remaining)]
                stream.write(data); digest.update(data); remaining -= len(data)
        item = {"path": "artifacts/features.geojson", "bytes": size, "sha256": digest.hexdigest()}
        record = self.frozen | {"acceptedRevision": self.frozen["revision"], "runId": self.run_id,
            "scope": OWNER.binding(), "actorId": OWNER.actor, "status": "succeeded", "artifacts": [item]}
        atomic_json(self.scoped.directory / "jobs" / (self.run_id + ".json"), record)
        return path, item

    def test_reader_uses_bounded_memory_and_preserves_the_accepted_snapshot_version(self):
        _, item = self.seed(16 * 1024 * 1024)
        (self.project / "process.py").write_text("newer IDE code")
        latest = self.scoped.identity(OWNER)
        self.assertGreater(latest["revision"], self.frozen["revision"])
        tracemalloc.start()
        try:
            with self.scoped.artifact(OWNER, self.run_id, item["path"]) as reader:
                self.assertEqual(reader.binding["acceptedRevision"], self.frozen["revision"])
                digest, total = hashlib.sha256(), 0
                for chunk in reader.chunks():
                    self.assertLessEqual(len(chunk), self.limits.chunk_bytes)
                    digest.update(chunk); total += len(chunk)
                self.assertEqual(total, item["bytes"])
                self.assertEqual(digest.hexdigest(), item["sha256"])
            self.assertLess(tracemalloc.get_traced_memory()[1], 1024 * 1024)
        finally:
            tracemalloc.stop()

    def test_in_place_change_and_job_replacement_abort_the_pinned_stream(self):
        path, item = self.seed()
        with self.scoped.artifact(OWNER, self.run_id, item["path"]) as reader:
            chunks = reader.chunks(); next(chunks)
            with path.open("r+b") as stream: stream.write(b"changed")
            with self.assertRaises(ValueError): next(chunks)
        # The changed file can never be accepted under the original digest.
        with self.assertRaises(ValueError): self.scoped.artifact(OWNER, self.run_id, item["path"])
        path.write_bytes(b"a" * item["bytes"])
        with self.scoped.artifact(OWNER, self.run_id, item["path"]) as reader:
            chunks = reader.chunks(); next(chunks)
            job = self.scoped.directory / "jobs" / (self.run_id + ".json")
            record = read_json(job); record["acceptedRevision"] += 1; atomic_json(job, record)
            with self.assertRaises(PermissionError): next(chunks)

    def test_revoking_the_current_workspace_stops_an_open_reader(self):
        _, item = self.seed()
        with self.scoped.artifact(OWNER, self.run_id, item["path"]) as reader:
            chunks = reader.chunks(); next(chunks)
            self.scoped.revoke(OWNER, OWNER.workspace)
            with self.assertRaises(PermissionError): next(chunks)

    def test_rejects_linked_parent_and_hardlinked_artifacts_without_reading_the_target(self):
        path, item = self.seed()
        path.rename(path.with_name("original"))
        path.symlink_to(path.with_name("original"))
        with self.assertRaises((ValueError, OSError)): self.scoped.artifact(OWNER, self.run_id, item["path"])
        path.unlink(); os.link(path.with_name("original"), path)
        with self.assertRaises(ValueError): self.scoped.artifact(OWNER, self.run_id, item["path"])
        path.unlink(); path.with_name("original").rename(path)
        directory = path.parent
        directory.rename(directory.with_name("retained"))
        directory.symlink_to(directory.with_name("retained"), target_is_directory=True)
        with self.assertRaises((ValueError, OSError)): self.scoped.artifact(OWNER, self.run_id, item["path"])

    def test_http_stream_returns_exact_receipt_length_and_digest_etag(self):
        _, item = self.seed(2 * 1024 * 1024)
        key = "test-private-service-key-" + "x" * 32
        server = create_server(self.workspace, key, 0)
        thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
        try:
            headers = {"X-Honua-Workspace-Key": key, "X-Honua-Actor-Id": OWNER.actor,
                "X-Honua-Workspace-Owner-Id": OWNER.owner, "X-Honua-Workspace-Id": OWNER.workspace,
                "X-Honua-Environment-Id": OWNER.environment, "X-Honua-Tenant-Id": OWNER.tenant}
            connection = http.client.HTTPConnection("127.0.0.1", server.server_address[1], timeout=5)
            connection.request("GET", f"/runs/{self.run_id}/artifacts/{item['path']}", headers=headers)
            response = connection.getresponse()
            self.assertEqual(response.status, 200)
            self.assertEqual(response.getheader("Content-Length"), str(item["bytes"]))
            self.assertEqual(response.getheader("ETag"), '"sha256-' + item["sha256"] + '"')
            self.assertEqual(response.getheader("Cache-Control"), "no-store")
            digest, total = hashlib.sha256(), 0
            while data := response.read(65536): digest.update(data); total += len(data)
            self.assertEqual((total, digest.hexdigest()), (item["bytes"], item["sha256"]))
            connection.close()
        finally:
            server.shutdown(); server.server_close(); thread.join(2)

    def test_http_grant_revocation_truncates_body_without_appending_an_error_response(self):
        _, item = self.seed(8 * 1024 * 1024)
        key = "test-private-service-key-" + "x" * 32
        server = create_server(self.workspace, key, 0)
        original = server.get_request
        def small_send_buffer():
            connection, address = original()
            connection.setsockopt(socket.SOL_SOCKET, socket.SO_SNDBUF, 4096)
            return connection, address
        server.get_request = small_send_buffer
        thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
        connection = None
        try:
            headers = {"X-Honua-Workspace-Key": key, "X-Honua-Actor-Id": OWNER.actor,
                "X-Honua-Workspace-Owner-Id": OWNER.owner, "X-Honua-Workspace-Id": OWNER.workspace,
                "X-Honua-Environment-Id": OWNER.environment, "X-Honua-Tenant-Id": OWNER.tenant}
            connection = http.client.HTTPConnection("127.0.0.1", server.server_address[1], timeout=10)
            connection.request("GET", f"/runs/{self.run_id}/artifacts/{item['path']}", headers=headers)
            response = connection.getresponse()
            self.assertEqual(response.status, 200)
            prefix = response.read(65536)
            self.assertEqual(prefix, b"a" * 65536)
            self.scoped.revoke(OWNER, OWNER.workspace)
            with self.assertRaises(http.client.IncompleteRead) as interrupted:
                response.read()
            received = prefix + interrupted.exception.partial
            self.assertLess(len(received), item["bytes"])
            self.assertEqual(received, b"a" * len(received))
        finally:
            if connection is not None: connection.close()
            server.shutdown(); server.server_close(); thread.join(2)

    def test_limits_are_immutable_and_independently_bound_into_the_snapshot(self):
        with self.assertRaises(FrozenInstanceError): self.limits.output_bytes = 1
        alternative = WorkspaceLimits(source_bytes=1024, fixture_bytes=4, snapshot_bytes=2048)
        (self.project / "fixtures").mkdir(); (self.project / "fixtures/input.bin").write_bytes(b"12345")
        with self.assertRaises(ValueError): snapshot(self.project, self.root / "too-large-fixture", alternative)
        (self.project / "fixtures/input.bin").write_bytes(b"1234")
        first = snapshot(self.project, self.root / "first", alternative)
        second = snapshot(self.project, self.root / "second", DEFAULT_LIMITS)
        self.assertEqual(first["files"], second["files"])
        self.assertNotEqual(first["digest"], second["digest"])
        self.assertEqual(first["limits"], alternative.record())
        with self.assertRaises(ValueError): WorkspaceLimits(output_bytes=8 * 1024**3)
        argv = command(IMAGE, self.project, self.root / "out", "test", alternative)
        self.assertIn("--read-only", argv)
        self.assertEqual(argv[argv.index("--network") + 1], "none")
        self.assertEqual(argv[argv.index("--memory") + 1], "1g")
        self.assertIn("fsize=134217728:134217728", argv)


class FixtureMetadataTests(unittest.TestCase):
    def test_actual_large_json_geojson_and_csv_fixtures_are_metadata_only_and_code_remains_editable(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); project = root / "project"; project.mkdir()
            (project / "honua-gp.yaml").write_text("schema: honua.gp-process/v1\n")
            code = project / "process.py"; code.write_text("def execute(context):\n    return None\n")
            fixtures = project / "fixtures"; fixtures.mkdir()
            expected = {}
            for extension in ("json", "geojson", "csv"):
                path = fixtures / ("large-input." + extension)
                marker = "LARGE-FIXTURE-CONTENT" * 20000
                if extension == "geojson":
                    data = json.dumps({"type": "FeatureCollection", "features": [{"type": "Feature", "geometry": {"type": "Point", "coordinates": [0, 0]}, "properties": {"data": marker}}]}).encode()
                elif extension == "json": data = json.dumps({"data": marker}).encode()
                else: data = ("name,value\n" + "LARGE-FIXTURE-CONTENT,1\n" * 20000).encode()
                self.assertGreater(len(data), 256 * 1024)
                path.write_bytes(data)
                expected[path.relative_to(project).as_posix()] = {"bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}
            workspace = Workspace(project, IMAGE, root / "runs")
            from workspace_service import read_regular as original_read
            def editor_read(path, *args, **kwargs):
                self.assertNotIn("fixtures", path.relative_to(project).parts)
                self.assertLessEqual(args[0], 256 * 1024)
                return original_read(path, *args, **kwargs)
            from unittest.mock import patch
            with patch("workspace_service.read_regular", side_effect=editor_read):
                result = workspace.project_files()
            self.assertEqual({item["path"] for item in result["files"]}, {"honua-gp.yaml", "process.py"})
            self.assertEqual(len(result["readOnlyFiles"]), 3)
            for item in result["readOnlyFiles"]:
                self.assertEqual(item, {"path": item["path"], **expected[item["path"]], "role": "fixture", "readOnly": True})
                self.assertNotIn("content", item)
            self.assertNotIn("LARGE-FIXTURE-CONTENT", json.dumps(result))
            source = next(item for item in result["files"] if item["path"] == "process.py")
            workspace.write("process.py", "def execute(context):\n    return 'corrected'\n", source["sha256"])
            self.assertEqual(code.read_text(), "def execute(context):\n    return 'corrected'\n")
            with self.assertRaises(ValueError): workspace.write("fixtures/large-input.json", "modified fixture", expected["fixtures/large-input.json"]["sha256"])
            manifest = snapshot(project, root / "snapshot")
            self.assertTrue(all(item["role"] == "fixture" for item in manifest["files"] if item["path"].startswith("fixtures/")))
            self.assertTrue(all(item["role"] == "source" for item in manifest["files"] if item["path"] == "process.py"))


if __name__ == "__main__": unittest.main()
