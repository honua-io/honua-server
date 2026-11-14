"""Exercise the compiled SARIF converter on the build host."""

from __future__ import annotations

import hashlib
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

CONVERTER = Path(sys.argv.pop(1)).resolve()


class CaptureTests(unittest.TestCase):
    def test_duplicate_project_reports_preserve_one_contextual_finding(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = "class C { void Old() {\nthrow null!;\n} }\n"
            (root / "C.cs").write_text(source, encoding="utf-8")
            reports = root / "reports"
            reports.mkdir()
            result = self.finding("C.cs")
            document = {"runs": [{"results": [result]}]}
            for name in ("first", "second"):
                (reports / f"{name}.sarif").write_text(json.dumps(document), encoding="utf-8")
            completed = self.capture(root)
            self.assertEqual(completed.returncode, 0, completed.stderr)
            fingerprint = hashlib.sha256(b"Existing finding\nC/Old()\nthrow null!;").hexdigest()
            rows = (root / "baseline.tsv").read_text(encoding="utf-8").splitlines()
            self.assertEqual(rows[1:], [f"CA2000\tC.cs\t{fingerprint}\t1"])

    def test_missing_reports_refuse_to_replace_baseline(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "reports").mkdir()
            (root / "baseline.tsv").write_text("preserve me", encoding="utf-8")
            self.assertNotEqual(self.capture(root).returncode, 0)
            self.assertEqual((root / "baseline.tsv").read_text(encoding="utf-8"), "preserve me")

    def test_source_outside_repository_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "reports").mkdir()
            document = {"runs": [{"results": [self.finding("../outside.cs")]}]}
            (root / "reports/input.sarif").write_text(json.dumps(document), encoding="utf-8")
            completed = self.capture(root)
            self.assertNotEqual(completed.returncode, 0)
            self.assertIn("Source outside repository", completed.stderr)
            self.assertFalse((root / "baseline.tsv").exists())

    @staticmethod
    def finding(uri: str) -> dict:
        return {"ruleId": "CA2000", "message": {"text": "Existing finding"},
                "locations": [{"physicalLocation": {"artifactLocation": {"uri": uri},
                                                     "region": {"startLine": 2, "startColumn": 1}}}]}

    @staticmethod
    def capture(root: Path) -> subprocess.CompletedProcess[str]:
        return subprocess.run(["dotnet", str(CONVERTER), str(root), str(root / "reports"),
                               str(root / "baseline.tsv")], text=True, capture_output=True, check=False)


if __name__ == "__main__":
    unittest.main()
