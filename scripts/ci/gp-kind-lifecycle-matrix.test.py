"""The kind lifecycle script lists its cases and refuses to start a cluster."""

import importlib.util
import io
import contextlib
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts/ci/gp-kind-lifecycle-matrix.py"


def load_checker():
    spec = importlib.util.spec_from_file_location("gp_kind_lifecycle_matrix", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


CHECKER = load_checker()


def present(name):
    return "/usr/bin/" + name


def invoke(argv, which):
    stdout, stderr = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
        code = CHECKER.main(argv, which=which)
    return code, stdout.getvalue(), stderr.getvalue()


class KindLifecycleMatrixTests(unittest.TestCase):
    def test_main_lists_required_cases_without_executing(self):
        self.assertEqual(
            ["success", "failure", "cancellation", "timeout", "worker loss", "artifact-upload failure"],
            list(CHECKER.CASES),
        )
        code, stdout, stderr = invoke([], present)
        self.assertEqual(0, code)
        for case in CHECKER.CASES:
            self.assertIn(f"- {case}", stdout)
        self.assertIn("not executed", stdout)
        self.assertEqual("", stderr)
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertNotIn("subprocess", source)
        self.assertNotIn("os.system", source)
        self.assertNotIn("kind create", source)

    def test_main_missing_tool_exits_nonzero(self):
        for missing in CHECKER.TOOLS:
            with self.subTest(missing=missing):
                def which(name, absent=missing):
                    return None if name == absent else present(name)

                code, stdout, stderr = invoke([], which)
                self.assertNotEqual(0, code, missing)
                self.assertIn(missing, stderr)
                self.assertIn(CHECKER.REMEDIATIONS[missing], stderr)
                self.assertNotIn("success", stdout)
                self.assertNotIn("kind create", stdout + stderr)

    def test_main_execute_refuses_live_execution(self):
        code, stdout, stderr = invoke(["--execute"], present)
        self.assertNotEqual(0, code)
        self.assertIn("live execution is not implemented in this slice", stderr)
        self.assertNotIn("success", stdout)
        self.assertNotIn("kind create", stdout + stderr)


if __name__ == "__main__":
    unittest.main()
