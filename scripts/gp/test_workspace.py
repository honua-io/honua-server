import hashlib
import json
import os
from pathlib import Path
import tempfile
import unittest

from workspace import command, read_regular, sanitized, snapshot
from workspace_service import Workspace

IMAGE = "sha256:" + "a" * 64


class WorkspaceTests(unittest.TestCase):
    def project(self, root: Path) -> Path:
        project = root / "project"
        project.mkdir()
        (project / "honua-gp.yaml").write_text("schema: honua.gp-process/v1\n")
        (project / "tool.py").write_text("def execute(context):\n    raise ValueError('broken')\n")
        (project / "input.json").write_text('{"features":[]}')
        return project

    def test_corrected_source_retains_distinct_snapshot_bytes(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            project = self.project(root)
            before = snapshot(project, root / "before")
            workspace = Workspace(project, IMAGE, root / "runs")
            source = next(item for item in workspace.project_files()["files"] if item["path"] == "tool.py")
            corrected = "def execute(context):\n    return {'status': 'succeeded'}\n"
            workspace.write("tool.py", corrected, source["sha256"])
            after = snapshot(project, root / "after")
            self.assertNotEqual(before["digest"], after["digest"])
            self.assertIn("broken", (root / "before" / "tool.py").read_text())
            self.assertEqual(corrected, (root / "after" / "tool.py").read_text())
            self.assertEqual((root / "before" / "input.json").read_bytes(), (root / "after" / "input.json").read_bytes())
            with self.assertRaises(FileExistsError): workspace.write("tool.py", "stale", source["sha256"])

    def test_snapshot_and_source_paths_reject_symlinks_and_escape(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); project = self.project(root)
            (project / "escape.py").symlink_to(root / "secret")
            with self.assertRaises(ValueError): snapshot(project, root / "snapshot")
            workspace = Workspace(project, IMAGE, root / "runs")
            for path in ["../secret.py", "/tmp/secret.py", "escape.py", ".git/config", "token.env"]:
                with self.assertRaises(ValueError): workspace.source(path)

    def test_all_dependency_build_and_run_work_stays_inside_denied_network_sandbox(self):
        argv = command(IMAGE, Path("/snapshot"), Path("/output"), "run")
        self.assertIn("--read-only", argv)
        self.assertEqual("none", argv[argv.index("--network") + 1])
        self.assertEqual("never", argv[argv.index("--pull") + 1])
        self.assertIn("ALL", argv)
        self.assertIn("no-new-privileges", argv)
        self.assertNotIn("--privileged", argv)
        self.assertFalse(any("docker.sock" in value for value in argv))
        self.assertIn("readonly", argv[argv.index("--mount") + 1])
        with self.assertRaises(ValueError): command("mutable:latest", Path("/src"), Path("/out"), "run")

    def test_diagnostics_are_bounded_and_common_credentials_redacted(self):
        result = sanitized("x" * 20000 + "\nAuthorization: Bearer token-value\napi_key=key-value\nhttps://user:pass@example.com/path")
        self.assertLessEqual(len(result), 16384)
        self.assertNotIn("token-value", result)
        self.assertNotIn("key-value", result)
        self.assertNotIn("user:pass", result)

    def test_fifo_sources_and_artifacts_refused_without_host_read(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); project = self.project(root)
            fifo = project / "fifo.py"
            os.mkfifo(fifo)
            with self.assertRaises(ValueError): read_regular(fifo, 1024)
            with self.assertRaises(ValueError): snapshot(project, root / "snapshot")
            workspace = Workspace(project, IMAGE, root / "runs")
            with self.assertRaises(ValueError): workspace.project_files()
            output = root / "runs" / ("c" * 32) / "output"
            output.mkdir(parents=True)
            os.mkfifo(output / "fifo.json")
            workspace.jobs["c" * 32] = {"status":"succeeded", "artifacts":[{"path":"fifo.json", "bytes":0, "sha256":hashlib.sha256(b"").hexdigest()}]}
            with self.assertRaises(ValueError): workspace.artifact("c" * 32, "fifo.json")

    def test_actual_read_bytes_are_bounded(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "growing.py"
            path.write_bytes(b"12345")
            with self.assertRaises(ValueError): read_regular(path, 4)
            self.assertEqual(read_regular(path, 5), b"12345")

    def test_failed_receipts_never_expose_success_artifacts(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); workspace = Workspace(self.project(root), IMAGE, root / "runs")
            run_id = "b" * 32
            workspace.jobs[run_id] = {"runId": run_id, "status": "failed", "artifacts": [{"path": "secret.json"}]}
            with self.assertRaises(ValueError): workspace.artifact(run_id, "secret.json")


if __name__ == "__main__": unittest.main()
