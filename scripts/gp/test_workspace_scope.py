import hashlib
import http.client
import json
from pathlib import Path
import tempfile
import threading
import time
import unittest
from unittest.mock import patch

from workspace import snapshot, source_manifest
from workspace_scope import Access, ScopedWorkspace
from workspace_service import Workspace, create_server, cancel_active

IMAGE = "sha256:" + "a" * 64
OWNER = Access("human:owner", "human:owner", "workspace-one", "server-target", "tenant-one")
AGENT = Access("agent:one", OWNER.owner, OWNER.workspace, OWNER.environment, OWNER.tenant, True)


class ScopedWorkspaceTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.project = self.root / "project"
        self.project.mkdir()
        (self.project / "honua-gp.yaml").write_text("schema: honua.gp-process/v1\n")
        (self.project / "tool.py").write_text("original source")
        (self.project / "input.json").write_text('{"features":[]}')
        self.adapters = []
        self.adapter = self.peer()

    def tearDown(self):
        for adapter in self.adapters: adapter.lock.file.close()
        self.temporary.cleanup()

    def peer(self):
        adapter = ScopedWorkspace(Workspace(self.project, IMAGE, self.root / "runs"))
        self.adapters.append(adapter)
        return adapter

    def bind(self):
        identity = self.adapter.identity(OWNER)
        return self.adapter.bind(OWNER, identity["projectId"])

    def capture(self):
        identity = self.bind()
        return self.adapter.snapshot(AGENT, {"projectId": identity["projectId"], "expectedRevision": identity["revision"], "sourceDigest": identity["sourceDigest"]})

    def test_peer_created_before_binding_requires_current_persisted_workspace_grant(self):
        stale_peer = self.peer()
        self.bind()
        foreign = Access(OWNER.actor, OWNER.owner, "not-granted", OWNER.environment, OWNER.tenant)
        with self.assertRaises(PermissionError): stale_peer.identity(foreign)
        self.assertEqual(stale_peer.identity(OWNER)["binding"], OWNER.binding())

    def test_service_shutdown_signals_existing_runner_cancellation_and_waits_boundedly(self):
        cancellation = threading.Event()
        self.adapter.workspace.cancellations["active"] = cancellation
        def finish():
            cancellation.wait(1)
            self.adapter.workspace.cancellations.pop("active", None)
        worker = threading.Thread(target=finish)
        worker.start()
        cancel_active(self.adapter.workspace, 1)
        worker.join(1)
        self.assertTrue(cancellation.is_set())
        self.assertFalse(self.adapter.workspace.cancellations)

    def test_binding_is_durable_human_only_and_rejects_cross_principal_tenant_environment_workspace(self):
        identity = self.adapter.identity(OWNER)
        self.assertIsNone(identity["binding"])
        self.assertNotIn("files", identity)
        self.assertNotIn("ideHandoff", identity)
        for denied in [AGENT, Access(OWNER.actor, OWNER.owner, OWNER.workspace, OWNER.environment, OWNER.tenant, True)]:
            with self.assertRaises(PermissionError): self.adapter.bind(denied, identity["projectId"])
        bound = self.bind()
        self.assertEqual(bound, self.adapter.bind(OWNER, bound["projectId"]))
        peer = self.peer()
        self.assertEqual(bound, peer.identity(OWNER))
        for denied in [Access("other", "other", OWNER.workspace, OWNER.environment, OWNER.tenant),
                       Access(OWNER.actor, OWNER.owner, OWNER.workspace, OWNER.environment, "other-tenant"),
                       Access(OWNER.actor, OWNER.owner, OWNER.workspace, "other-environment", OWNER.tenant),
                       Access(OWNER.actor, OWNER.owner, "other-workspace", OWNER.environment, OWNER.tenant)]:
            with self.assertRaises(PermissionError): peer.identity(denied)
            with self.assertRaises(PermissionError): peer.source(denied, "tool.py")
        self.assertNotIn("ideHandoff", peer.project_files(AGENT))
        self.assertIn("ideHandoff", peer.project_files(OWNER))
        peer.revoke(OWNER, OWNER.workspace)
        with self.assertRaises(PermissionError): self.adapter.identity(AGENT)
        with self.assertRaises(PermissionError): peer.revoke(AGENT, OWNER.workspace)

    def test_snapshot_source_is_immutable_and_source_writes_require_actual_project_revision(self):
        identity = self.bind()
        source = self.adapter.source(AGENT, "tool.py")
        frozen = self.adapter.snapshot(AGENT, {"projectId": identity["projectId"], "expectedRevision": identity["revision"], "sourceDigest": identity["sourceDigest"]})
        saved = self.adapter.write(AGENT, "tool.py", {"projectId": identity["projectId"], "expectedRevision": identity["revision"], "content": "corrected source"}, source["sha256"])
        self.assertGreater(saved["revision"], identity["revision"])
        self.assertNotEqual(saved["sourceDigest"], frozen["snapshotDigest"])
        self.assertEqual(self.adapter.snapshot_source(AGENT, frozen["snapshotId"], "tool.py")["content"], "original source")
        with self.assertRaises(FileExistsError): self.adapter.write(AGENT, "tool.py", {"projectId": identity["projectId"], "expectedRevision": identity["revision"], "content": "stale"}, source["sha256"])
        with self.assertRaises(FileExistsError): self.adapter.snapshot(AGENT, {"projectId": identity["projectId"], "expectedRevision": identity["revision"], "sourceDigest": identity["sourceDigest"]})
        self.assertEqual(self.adapter.source(OWNER, "tool.py")["content"], "corrected source")

    def test_external_ide_write_is_detected_before_conditional_save(self):
        identity = self.bind()
        source = self.adapter.source(AGENT, "tool.py")
        (self.project / "input.json").write_text('{"features":["new fixture"]}')
        with self.assertRaises(FileExistsError): self.adapter.write(AGENT, "tool.py", {"projectId": identity["projectId"], "expectedRevision": identity["revision"], "content": "not accepted"}, source["sha256"])
        self.assertEqual((self.project / "tool.py").read_text(), "original source")

    def test_durable_command_reconciliation_never_reexecutes_after_peer_restart_or_receipt_loss(self):
        frozen = self.capture()
        entered, release = threading.Event(), threading.Event()
        calls = []
        def execute(source, image, runs, timeout, cancellation, run_id):
            calls.append((source / "tool.py").read_text())
            entered.set(); release.wait(5)
            return {"runId": run_id, "snapshotDigest": frozen["snapshotDigest"], "status": "succeeded", "artifacts": [], "elapsedMs": 1}
        request = {key: frozen[key] for key in ("projectId", "snapshotId", "snapshotDigest")} | {"commandId": "stable-command"}
        with patch("workspace_scope.run", side_effect=execute):
            accepted = self.adapter.start(AGENT, request)
            self.assertTrue(entered.wait(3))
            peer = self.peer()
            observed = peer.get_command(AGENT, "stable-command")
            self.assertEqual(observed["runId"], accepted["runId"])
            self.assertEqual(peer.start(AGENT, request)["runId"], accepted["runId"])
            with self.assertRaises(FileExistsError): peer.start(AGENT, request | {"snapshotDigest": "b" * 64})
            release.set()
            deadline = time.monotonic() + 3
            while self.adapter.status(AGENT, accepted["runId"])["status"] != "succeeded" and time.monotonic() < deadline: time.sleep(.01)
            self.assertEqual(self.adapter.status(AGENT, accepted["runId"])["status"], "succeeded")
            (peer.directory / "jobs" / f'{accepted["runId"]}.json').unlink()
            self.assertEqual(peer.get_command(AGENT, "stable-command")["status"], "unknown")
            self.assertEqual(peer.start(AGENT, request)["runId"], accepted["runId"])
            self.assertEqual(calls, ["original source"])

    def test_local_run_discovery_is_scoped_bounded_and_newest_first(self):
        frozen = self.capture()
        def execute(source, image, runs, timeout, cancellation, run_id):
            return {"runId": run_id, "snapshotDigest": frozen["snapshotDigest"], "status": "failed", "diagnostics": "diagnostic" * 1000, "artifacts": []}
        receipts = []
        with patch("workspace_scope.run", side_effect=execute):
            for index in range(35):
                receipt = self.adapter.start(AGENT, {key: frozen[key] for key in ("projectId", "snapshotId", "snapshotDigest")} | {"commandId": f"command-{index}"})
                receipts.append(receipt)
                deadline = time.monotonic() + 5
                while self.adapter.status(AGENT, receipt["runId"])["status"] != "failed" and time.monotonic() < deadline: time.sleep(.01)
        observed = self.adapter.list_runs(OWNER)["runs"]
        self.assertEqual(len(observed), 32)
        self.assertEqual([run["runId"] for run in observed], [run["runId"] for run in reversed(receipts[-32:])])
        self.assertTrue(all(len(run["diagnostics"]) <= 512 for run in observed))
        self.assertTrue(all("artifacts" not in run for run in observed))
        with self.assertRaises(PermissionError): self.adapter.list_runs(Access("other", "other", OWNER.workspace, OWNER.environment, OWNER.tenant))

    def test_input_fixture_source_reads_are_immutable_after_live_edits(self):
        (self.project / "input.geojson").write_text('{"type":"FeatureCollection","features":[]}')
        frozen = self.capture()
        original = self.adapter.snapshot_source(AGENT, frozen["snapshotId"], "input.geojson")
        self.assertEqual(original["content"], self.adapter.source(AGENT, "input.geojson")["content"])
        (self.project / "input.geojson").write_text('{"type":"FeatureCollection","features":[1]}')
        self.assertEqual(self.adapter.snapshot_source(AGENT, frozen["snapshotId"], "input.geojson"), original)

    def test_snapshot_tamper_is_rejected_before_execution(self):
        frozen = self.capture()
        source = self.adapter.directory / "snapshots" / frozen["snapshotId"] / "source/tool.py"
        source.chmod(0o644); source.write_text("tampered")
        with self.assertRaises(FileExistsError): self.adapter.get_snapshot(AGENT, frozen["snapshotId"])
        with patch("workspace_scope.run") as executor:
            with self.assertRaises(FileExistsError): self.adapter.start(AGENT, {key: frozen[key] for key in ("projectId", "snapshotId", "snapshotDigest")} | {"commandId": "tampered-command"})
            executor.assert_not_called()

    def test_conventional_credentials_never_reach_snapshot_or_editable_source(self):
        names = [".config/gcloud/application_default_credentials.json", ".yarnrc.yml", ".env", ".env.production.json", ".npmrc", ".pypirc", ".netrc", ".aws/credentials", ".ssh/id_ed25519", "credentials.json", "secrets.json", "private.pem"]
        for name in names:
            path = self.project / name; path.parent.mkdir(parents=True, exist_ok=True); path.write_text("UNIQUE-CREDENTIAL-BYTES")
        destination = self.root / "snapshot"
        snapshot(self.project, destination)
        self.assertFalse(any(b"UNIQUE-CREDENTIAL-BYTES" in path.read_bytes() for path in destination.rglob("*") if path.is_file()))
        self.assertTrue((destination / "input.json").is_file())
        self.bind()
        self.assertFalse(any(item["path"] in names for item in self.adapter.project_files(AGENT)["files"]))
        with self.assertRaises(ValueError): self.adapter.source(AGENT, "credentials.json")

    def test_http_boundary_requires_service_auth_and_verified_scope_and_pins_allowed_host(self):
        key = "test-service-key-" + "a" * 32
        server = create_server(self.adapter.workspace, key, 0)
        thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
        def request(method, path, body=None, scope=OWNER, extra=None):
            headers = {"X-Honua-Workspace-Key": key, "X-Honua-Actor-Id": scope.actor, "X-Honua-Workspace-Owner-Id": scope.owner,
                       "X-Honua-Workspace-Id": scope.workspace, "X-Honua-Environment-Id": scope.environment,
                       "X-Honua-Tenant-Id": scope.tenant, "X-Honua-Workspace-Delegated": str(scope.delegated).lower()} | (extra or {})
            connection = http.client.HTTPConnection("127.0.0.1", server.server_address[1], timeout=5)
            connection.request(method, path, None if body is None else json.dumps(body), headers)
            response = connection.getresponse(); content = json.loads(response.read()); status = response.status; connection.close()
            return status, content
        try:
            status, identity = request("GET", "/project/identity")
            self.assertEqual(status, 200)
            self.assertEqual(request("GET", "/project")[0], 403)
            self.assertEqual(request("POST", "/project/bind", {"projectId": identity["projectId"]}, AGENT)[0], 403)
            self.assertEqual(request("POST", "/project/bind", {"projectId": identity["projectId"]})[0], 200)
            self.assertEqual(request("GET", "/sources/tool.py", scope=AGENT)[1]["content"], "original source")
            self.assertEqual(request("GET", "/project", extra={"Origin": "https://browser.invalid"})[0], 403)
            self.assertEqual(request("GET", "/project", extra={"Host": "untrusted.invalid"})[0], 403)
            self.assertEqual(request("GET", "/project", extra={"X-Honua-Workspace-Key": "wrong"})[0], 403)
        finally:
            server.shutdown(); server.server_close(); thread.join(3)
        for address in ["0.0.0.0", "8.8.8.8"]:
            with self.assertRaises(ValueError): create_server(self.adapter.workspace, key, 0, address, ("example:7194",))


if __name__ == "__main__": unittest.main()
