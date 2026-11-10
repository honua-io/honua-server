"""The roster process status is the verifier status.

Run: python3 -m unittest scripts/certification/test_record_roster_verdicts.py
"""
from __future__ import annotations

import os
import stat
import subprocess
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
LIB = REPO / "scripts" / "certification" / "record_roster_verdicts.sh"
ROSTER = REPO / "certification" / "bounded-roster" / "run-roster.sh"
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
            stub_lines = [
                "#!/bin/bash",
                'if [[ "$*" == *"--mode release"* ]]; then',
                "  echo release-body",
                f"  exit {release_exit}",
                "fi",
                "echo contract-body",
                f"exit {contract_exit}",
                "",
            ]
            stub.write_text("\n".join(stub_lines), encoding="utf-8")
            stub.chmod(stub.stat().st_mode | stat.S_IEXEC)
            env = os.environ.copy()
            env["PATH"] = f"{bindir}{os.pathsep}{env.get('PATH', '')}"
            env["EVIDENCE"] = str(evidence)
            env["LIB"] = str(LIB)
            env["SHA"] = SHA
            env["DIGEST"] = DIGEST
            completed = subprocess.run(
                [
                    "bash", "-c",
                    "\n".join([
                        "set -euo pipefail",
                        'source "$LIB"',
                        "declare -A candidate=(",
                        '  [source_sha]="$SHA"',
                        '  [image_digest]="$DIGEST"',
                        '  [cut_at]="2026-11-10T00:00:00Z"',
                        '  [producer_sha]="$SHA"',
                        ")",
                        'record_verdicts "$EVIDENCE" candidate',
                    ]),
                ],
                cwd=root,
                env=env,
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
        self.assertIn("declare -A roster_candidate=", verdict)


if __name__ == "__main__":
    unittest.main()
