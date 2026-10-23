import importlib.util
import json
import re
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
SCRIPT = ROOT / "scripts/client-compat/stage-artifacts.sh"


def run_staging(artifacts: Path, output: Path, selection: str = "") -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        ["bash", str(SCRIPT), str(artifacts), str(output), selection],
        check=False,
        capture_output=True,
        text=True,
    )


class ClientInteropArtifactStagingTests(unittest.TestCase):
    def test_compose_readiness_probe_uses_get_not_head(self) -> None:
        compose = (ROOT / "docker/client-compat/compose.yml").read_text(
            encoding="utf-8"
        )

        self.assertIn('"--output-document=/dev/null"', compose)
        self.assertNotIn('"--spider"', compose)

    def test_stages_envelopes_from_isolated_v4_artifact_directories(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            tmp_path = Path(directory)
            artifact = tmp_path / "artifacts" / (
                "honua-server-real-client-interop-matrix-(nightly)-123-"
                "evidence-client-compat-gdal"
            )
            artifact.mkdir(parents=True)
            (artifact / "observation.cert.json").write_text("{}", encoding="utf-8")

            output = tmp_path / "output"
            result = run_staging(tmp_path / "artifacts", output)

            self.assertEqual(0, result.returncode, result.stderr)
            self.assertTrue((output / "gdal" / "observation.cert.json").is_file())

    def test_fails_loudly_when_downloaded_lane_artifacts_have_zero_envelopes(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory() as directory:
            tmp_path = Path(directory)
            artifact = tmp_path / "artifacts" / (
                "honua-server-real-client-interop-matrix-(nightly)-123-"
                "evidence-client-compat-gdal"
            )
            artifact.mkdir(parents=True)
            (artifact / "lane.log").write_text(
                "failed before evidence", encoding="utf-8"
            )

            result = run_staging(tmp_path / "artifacts", tmp_path / "output")

            self.assertNotEqual(0, result.returncode)
            self.assertIn("staged zero .cert.json envelopes", result.stderr)


class SingleArtifactLayoutTests(unittest.TestCase):
    def test_flat_download_uses_declared_lane_and_keeps_bytes(self) -> None:
        for lane, client in (("owslib", "py-owslib"), ("gdal", "cli"), ("r-sf", "r-sf")):
            with self.subTest(lane=lane), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                artifacts = root / "artifacts"
                artifacts.mkdir()
                payload = json.dumps({"client_lane": client, "server_commit": "a" * 40}) + "\n"
                (artifacts / "receipt.cert.json").write_text(payload)
                (artifacts / "lane.log").write_text("real lane diagnostic")
                result = run_staging(artifacts, root / "output", lane)
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertEqual(payload, (root / "output" / lane / "receipt.cert.json").read_text())
                self.assertEqual("real lane diagnostic", (root / "output" / lane / "lane.log").read_text())

    def test_flat_download_rejects_undeclared_ambiguous_and_unsafe_selection(self) -> None:
        for selection in ("", "owslib,gdal", "owslib,owslib", "unknown", "../owslib"):
            with self.subTest(selection=selection), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                artifacts = root / "artifacts"
                artifacts.mkdir()
                (artifacts / "receipt.cert.json").write_text('{"client_lane":"py-owslib"}')
                result = run_staging(artifacts, root / "output", selection)
                self.assertNotEqual(0, result.returncode)
                self.assertFalse((root / "output").exists())

    def test_flat_download_rejects_wrong_or_missing_client_identity_and_malformed_json(self) -> None:
        for payload in ('{"client_lane":"cli"}', '{}', '[]', '{not-json'):
            with self.subTest(payload=payload), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                artifacts = root / "artifacts"
                artifacts.mkdir()
                (artifacts / "receipt.cert.json").write_text(payload)
                result = run_staging(artifacts, root / "output", "owslib")
                self.assertNotEqual(0, result.returncode)
                self.assertFalse((root / "output").exists())

    def test_missing_and_empty_downloads_fail(self) -> None:
        for kind in ("missing", "empty", "logs-only"):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                artifacts = root / "artifacts"
                if kind != "missing":
                    artifacts.mkdir()
                if kind == "logs-only":
                    (artifacts / "lane.log").write_text("no receipts")
                result = run_staging(artifacts, root / "output", "owslib")
                self.assertNotEqual(0, result.returncode)

    def test_named_multi_lane_downloads_keep_separate_destinations(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for lane in ("owslib", "gdal"):
                artifact = root / "artifacts" / ("run-evidence-client-compat-" + lane)
                artifact.mkdir(parents=True)
                (artifact / "receipt.cert.json").write_text(lane)
            result = run_staging(root / "artifacts", root / "output", "gdal,owslib")
            self.assertEqual(0, result.returncode, result.stderr)
            for lane in ("owslib", "gdal"):
                self.assertEqual(lane, (root / "output" / lane / "receipt.cert.json").read_text())

    def test_named_download_rejects_missing_extra_duplicate_and_unknown_lanes(self) -> None:
        for names, selection in (
            (["a-evidence-client-compat-gdal"], "gdal,owslib"),
            (["a-evidence-client-compat-gdal"], "owslib"),
            (["a-evidence-client-compat-gdal", "b-evidence-client-compat-gdal"], "gdal"),
            (["a-evidence-client-compat-unknown"], ""),
        ):
            with self.subTest(names=names), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                for name in names:
                    artifact = root / "artifacts" / name
                    artifact.mkdir(parents=True)
                    (artifact / "receipt.cert.json").write_text("{}")
                result = run_staging(root / "artifacts", root / "output", selection)
                self.assertNotEqual(0, result.returncode)
                self.assertFalse((root / "output").exists())

    def test_mixed_download_layout_cannot_hide_receipts(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            artifact = root / "artifacts" / "run-evidence-client-compat-owslib"
            artifact.mkdir(parents=True)
            (artifact / "receipt.cert.json").write_text("{}")
            (root / "artifacts" / "other.cert.json").write_text('{"client_lane":"py-owslib"}')
            result = run_staging(root / "artifacts", root / "output", "owslib")
            self.assertNotEqual(0, result.returncode)
            self.assertIn("Mixed", result.stderr)

    def test_symlink_cannot_copy_outside_artifact(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            artifacts = root / "artifacts"
            artifacts.mkdir()
            (root / "outside.cert.json").write_text('{"client_lane":"py-owslib"}')
            (artifacts / "receipt.cert.json").symlink_to(root / "outside.cert.json")
            result = run_staging(artifacts, root / "output", "owslib")
            self.assertNotEqual(0, result.returncode)
            self.assertFalse((root / "output").exists())

    def test_flat_staging_does_not_bypass_exact_source_validator(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            artifacts = root / "artifacts"
            artifacts.mkdir()
            (artifacts / "receipt.cert.json").write_text(json.dumps({"client_lane": "py-owslib", "server_commit": "b" * 40}))
            result = run_staging(artifacts, root / "output", "owslib")
            self.assertEqual(0, result.returncode, result.stderr)
            validator = ROOT / "scripts/client-compat/validate-envelope-source.py"
            check = subprocess.run(["python3", str(validator), str(root / "output"), "a" * 40], capture_output=True, text=True, check=False)
            self.assertNotEqual(0, check.returncode)
            self.assertIn("does not match", check.stderr)

    def test_lane_aliases_match_authoritative_workflow_selection(self) -> None:
        spec = importlib.util.spec_from_file_location("staging", SCRIPT.with_suffix(".py"))
        assert spec and spec.loader
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        workflow = (ROOT / ".github/workflows/client-interop-nightly.yml").read_text()
        mappings = dict(re.findall(r'\[([\w-]+)\]="([\w-]+)"', workflow))
        self.assertEqual(mappings, module.LANES)
        self.assertIn('EXPECTED_LANES: ${{ join(fromJSON(needs.prepare.outputs.lanes), \',\') }}', workflow)
        self.assertIn('output "$EXPECTED_LANES"', workflow)


if __name__ == "__main__":
    unittest.main()
