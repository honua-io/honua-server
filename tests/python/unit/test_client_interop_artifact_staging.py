import subprocess
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
SCRIPT = ROOT / "scripts/client-compat/stage-artifacts.sh"


def run_staging(artifacts: Path, output: Path) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        ["bash", str(SCRIPT), str(artifacts), str(output)],
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


if __name__ == "__main__":
    unittest.main()
