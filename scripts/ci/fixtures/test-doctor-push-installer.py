"""Guard installer anchors, drift handling and executable workflow paths."""
import importlib.util
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location("installer", ROOT / "scripts/ci/install-doctor-push-guard.py")
INSTALLER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(INSTALLER)
# Exact insertion anchors from pinned gh-aw v1.0.1's push handler.
RUNTIME = "    let pushRepo = itemRepo;\n          return pushSignedCommits({\n"


class DoctorPushInstallerTests(unittest.TestCase):
    def test_installs_both_guards_and_rejects_duplicate_install(self):
        with tempfile.TemporaryDirectory() as directory:
            runtime = Path(directory) / "push_to_pull_request_branch.cjs"
            runtime.write_text(RUNTIME)
            INSTALLER.install_guard(runtime)
            patched = runtime.read_text()
            self.assertEqual(patched.count("await getDoctorPushDeferral("), 2)
            self.assertIn("throw new StalePullRequestError(doctorDeferral)", patched)
            self.assertEqual((runtime.parent / "doctor-push-guard.cjs").read_bytes(),
                             (ROOT / "scripts/ci/doctor-push-guard.cjs").read_bytes())
            with self.assertRaisesRegex(RuntimeError, "already installed"):
                INSTALLER.install_guard(runtime)

    def test_runtime_drift_fails_without_publishing_a_partial_patch(self):
        with tempfile.TemporaryDirectory() as directory:
            runtime = Path(directory) / "push_to_pull_request_branch.cjs"
            runtime.write_text(RUNTIME.replace("return pushSignedCommits", "return changedRuntime"))
            original = runtime.read_bytes()
            with self.assertRaisesRegex(RuntimeError, "Unexpected gh-aw push runtime"):
                INSTALLER.install_guard(runtime)
            self.assertEqual(runtime.read_bytes(), original)

    def test_all_workers_patch_the_executed_actions_runtime(self):
        for name in ("ci-doctor", "pr-review-doctor", "pr-sync-doctor"):
            source = (ROOT / f".github/workflows/{name}.md").read_text()
            self.assertIn('${RUNNER_TEMP}/gh-aw/actions/push_to_pull_request_branch.cjs', source)
            self.assertNotIn('${RUNNER_TEMP}/gh-aw/safeoutputs/push_to_pull_request_branch.cjs', source)


if __name__ == "__main__":
    unittest.main()
