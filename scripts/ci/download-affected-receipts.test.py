#!/usr/bin/env python3
"""Newest-attempt receipt selection never fetches a stale signed URL."""

from __future__ import annotations

import hashlib
import importlib.util
import io
import json
import os
import stat
import subprocess
import sys
import tempfile
import textwrap
import unittest
import zipfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location(
    "download_affected_receipts", ROOT / "scripts/ci/download-affected-receipts.py"
)
receipts = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
SPEC.loader.exec_module(receipts)


def artifact(name: str, artifact_id: int, digest: str | None = None) -> dict:
    value = {"name": name, "id": artifact_id, "expired": False}
    if digest is not None:
        value["digest"] = digest
    return value


def expired_artifact(name: str, artifact_id: int) -> dict:
    value = artifact(name, artifact_id)
    value["expired"] = True
    return value


def zip_bytes(member: str = "control.timing.json") -> bytes:
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as archive:
        archive.writestr(member, b"{}")
    return buffer.getvalue()


class SelectArtifactsTest(unittest.TestCase):
    def test_keeps_previous_attempt_when_the_rerun_skipped_that_shard(self) -> None:
        matrix = [{"artifact_suffix": "packages"}, {"artifact_suffix": "studio"}]
        catalog = [
            artifact("pr-gate-affected-shard-packages-attempt-2", 20),
            artifact("pr-gate-affected-shard-packages-attempt-1", 11),
            artifact("pr-gate-affected-shard-studio-attempt-1", 12),
            artifact("pr-gate-affected-shard-other-attempt-3", 99),
        ]
        selected = receipts.select_artifacts(matrix, catalog, attempt=3)
        self.assertEqual([item["id"] for item in selected], [20, 12])

    def test_expired_or_ambiguous_newest_does_not_fall_back(self) -> None:
        matrix = [{"artifact_suffix": "reporting"}]
        older = artifact("pr-gate-affected-shard-reporting-attempt-1", 1)
        expired = expired_artifact("pr-gate-affected-shard-reporting-attempt-2", 2)
        with self.assertRaises(ValueError):
            receipts.select_artifacts(matrix, [older, expired], attempt=2)
        duplicate = artifact("pr-gate-affected-shard-reporting-attempt-2", 3)
        with self.assertRaises(ValueError):
            receipts.select_artifacts(matrix, [older, duplicate, dict(duplicate)], attempt=2)

    def test_rejects_unsafe_duplicate_or_missing_suffixes(self) -> None:
        catalog = [artifact("pr-gate-affected-shard-reporting-attempt-1", 1)]
        for matrix in (
            [{"artifact_suffix": "../reporting"}],
            [{"artifact_suffix": "reporting"}, {"artifact_suffix": "reporting"}],
            [{"artifact_suffix": "missing"}],
            [{"name": "reporting"}],
        ):
            with self.subTest(matrix=matrix):
                with self.assertRaises(ValueError):
                    receipts.select_artifacts(matrix, catalog, attempt=1)

    def test_extract_rejects_escape_and_duplicate_members(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            safe = root / "safe.zip"
            with zipfile.ZipFile(safe, "w") as archive:
                archive.writestr("control.timing.json", b"{}")
            destination = root / "out"
            receipts.extract_archive(safe, destination)
            self.assertEqual((destination / "control.timing.json").read_bytes(), b"{}")
            for member in ("../escape.json", "/tmp/escape.json", "nested\\escape.json"):
                slipped = root / "slipped.zip"
                with zipfile.ZipFile(slipped, "w") as archive:
                    archive.writestr(member, b"x")
                with self.assertRaises(ValueError):
                    receipts.extract_archive(slipped, root / "rejected")
            duplicate = root / "duplicate.zip"
            with zipfile.ZipFile(duplicate, "w") as archive:
                archive.writestr("control.timing.json", b"a")
                archive.writestr("control.timing.json", b"b")
            with self.assertRaises(ValueError):
                receipts.extract_archive(duplicate, root / "dup")


class DownloadCommandTest(unittest.TestCase):
    def _fake_gh(self, root: Path, catalog: list[dict], payload: bytes) -> None:
        (root / "catalog.json").write_text(json.dumps([{"artifacts": catalog}]))
        (root / "receipt.zip").write_bytes(payload)
        script = root / "gh"
        script.write_text(textwrap.dedent("""\
            #!/usr/bin/env python3
            import pathlib, sys
            root = pathlib.Path(__file__).resolve().parent
            joined = " ".join(sys.argv[1:])
            calls = root / "calls.txt"
            calls.write_text(calls.read_text() + joined + "\\n" if calls.exists() else joined + "\\n")
            if "/artifacts?" in joined:
                sys.stdout.buffer.write((root / "catalog.json").read_bytes())
            elif joined.endswith("/zip"):
                sys.stdout.buffer.write((root / "receipt.zip").read_bytes())
            else:
                raise SystemExit("unexpected " + joined)
            """))
        script.chmod(script.stat().st_mode | stat.S_IEXEC)

    def test_downloads_only_the_newest_selected_id(self) -> None:
        payload = zip_bytes()
        digest = "sha256:" + hashlib.sha256(payload).hexdigest()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self._fake_gh(root, [
                artifact("pr-gate-affected-shard-packages-attempt-1", 11, digest),
                artifact("pr-gate-affected-shard-packages-attempt-2", 22, digest),
            ], payload)
            env = {**os.environ, "PATH": f"{root}:{os.environ['PATH']}",
                   "GITHUB_REPOSITORY": "honua-io/honua-server", "GITHUB_RUN_ID": "43657318712",
                   "GITHUB_RUN_ATTEMPT": "3",
                   "SELECT_MATRIX": json.dumps([{"artifact_suffix": "packages"}])}
            output = root / "shard-results"
            run = subprocess.run([sys.executable, str(ROOT / "scripts/ci/download-affected-receipts.py"),
                                  "--output", str(output)], env=env, capture_output=True, text=True)
            self.assertEqual(run.returncode, 0, run.stderr)
            self.assertTrue((output / "pr-gate-affected-shard-packages-attempt-2" / "control.timing.json").is_file())
            calls = (root / "calls.txt").read_text()
            self.assertIn("/artifacts/22/zip", calls)
            self.assertNotIn("/artifacts/11/zip", calls)

    def test_digest_mismatch_leaves_the_destination_empty(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self._fake_gh(root, [
                artifact("pr-gate-affected-shard-packages-attempt-1", 11, "sha256:" + "0" * 64),
            ], zip_bytes())
            env = {**os.environ, "PATH": f"{root}:{os.environ['PATH']}",
                   "GITHUB_REPOSITORY": "honua-io/honua-server", "GITHUB_RUN_ID": "1",
                   "GITHUB_RUN_ATTEMPT": "1",
                   "SELECT_MATRIX": json.dumps([{"artifact_suffix": "packages"}])}
            output = root / "shard-results"
            run = subprocess.run([sys.executable, str(ROOT / "scripts/ci/download-affected-receipts.py"),
                                  "--output", str(output)], env=env, capture_output=True, text=True)
            self.assertEqual(run.returncode, 1)
            self.assertIn("digest mismatch", run.stderr)
            self.assertFalse(output.exists() and any(output.iterdir()))


class WorkflowContractTest(unittest.TestCase):
    def test_pr_gate_downloads_explicit_ids_and_full_ci_keeps_unmerged_patterns(self) -> None:
        import yaml

        workflow = yaml.safe_load((ROOT / ".github/workflows/pr-gate.yml").read_text())
        upload = next(step for step in workflow["jobs"]["affected-shard"]["steps"]
                      if str(step.get("with", {}).get("name", "")).startswith("pr-gate-affected-shard-"))
        self.assertTrue(upload["with"]["name"].endswith("-attempt-${{ github.run_attempt }}"))
        patterns = [step for job in workflow["jobs"].values() for step in job.get("steps", [])
                    if step.get("with", {}).get("pattern") == "pr-gate-affected-shard-*"]
        self.assertEqual(patterns, [])
        download = next(step for step in workflow["jobs"]["affected-shards"]["steps"]
                        if step.get("name") == "Download affected shard receipts")
        self.assertIn("download-affected-receipts.py", download["run"])
        self.assertIn("matrix_include", download["env"]["SELECT_MATRIX"])
        self.assertNotIn("merge-multiple", download)
        full = yaml.safe_load((ROOT / ".github/workflows/ci.yml").read_text())
        downloads = [step for job in full["jobs"].values() for step in job.get("steps", [])
                     if step.get("with", {}).get("pattern") == "server-test-results-*"]
        self.assertTrue(downloads)
        for step in downloads:
            self.assertFalse(step["with"].get("merge-multiple", False))


if __name__ == "__main__":
    unittest.main()
