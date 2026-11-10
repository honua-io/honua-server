"""The roster process status is the verifier status.

Run: python3 -m unittest certification.bounded-roster.tests.test_record_verdicts
from the repository root, or discover certification/bounded-roster/tests.
"""
from __future__ import annotations

import os
import stat
import subprocess
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
LIB = HERE.parent / "lib" / "record_verdicts.sh"
ROSTER = HERE.parent / "run-roster.sh"
SHA = "a" * 40
DIGEST = "sha256:" + ("b" * 64)


class RecordVerdictsTest(unittest.TestCase):
    def run_verdicts(self, release_exit: int, contract_exit: int) -> tuple[subprocess.CompletedProcess[str], str, str]:
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            evidence = root / "evidence"
            bindir = root / "bin"
            bindir.mkdir()
            stub = bindir / "python3"
            stub.write_text(
                "#!/bin/bash\n"
                "if [[ \"$*\" == *\"--mode release\"* ]]; then\n"
                "  echo release-body\n"
                f"  exit {release_exit}\n"
                "fi\n"
                "echo contract-body\n"
                f"exit {contract_exit}\n",
                encoding="utf-8",
            )
            stub.chmod(stub.stat().st_mode | stat.S_IEXEC)
            env = os.environ.copy()
            env["PATH"] = f"{bindir}{os.pathsep}{env.get('PATH', '')}"
            env["EVIDENCE"] = str(evidence)
            completed = subprocess.run(
                [
                    "bash", "-c",
                    'set -euo pipefail; source "$LIB"; '
                    'record_verdicts "$EVIDENCE" "$SHA" "$DIGEST" "2026-11-10T00:00:00Z" "$SHA"',
                ],
                cwd=root,
                env={**env, "LIB": str(LIB), "SHA": SHA, "DIGEST": DIGEST},
                text=True,
                capture_output=True,
                check=False,
            )
            release_text = (evidence / "verdict.release.txt").read_text(encoding="utf-8")
            contract_text = (evidence / "verdict.contract.txt").read_text(encoding="utf-8")
            return completed, release_text, contract_text

    def test_release_failure_is_the_process_status(self) -> None:
        completed, release_text, contract_text = self.run_verdicts(3, 0)
        self.assertEqual(completed.returncode, 1, completed.stderr)
        self.assertIn("release=3", completed.stderr)
        self.assertEqual(release_text, "release-body\n")
        self.assertEqual(contract_text, "contract-body\n")

    def test_contract_failure_is_the_process_status(self) -> None:
        completed, _, _ = self.run_verdicts(0, 4)
        self.assertEqual(completed.returncode, 1, completed.stderr)
        self.assertIn("contract=4", completed.stderr)

    def test_both_verifiers_passing_exits_zero(self) -> None:
        completed, _, _ = self.run_verdicts(0, 0)
        self.assertEqual(completed.returncode, 0, completed.stderr)
        self.assertEqual(completed.stderr, "")

    def test_orchestrator_keeps_the_verifier_status(self) -> None:
        text = ROSTER.read_text(encoding="utf-8")
        verdict = text.split('log "release-mode verdict"', 1)[1]
        self.assertNotIn("set +e", verdict)
        self.assertIn("record_verdicts ", verdict)


if __name__ == "__main__":
    unittest.main()
