#!/usr/bin/env python3

from __future__ import annotations

import importlib.util
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).with_name("summarize-dotnet-trx.py")
SPEC = importlib.util.spec_from_file_location("trx_summary", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def trx(results: list[tuple[str, str]]) -> str:
    rows = "".join(
        f'<UnitTestResult testName="{name}" outcome="{outcome}" duration="00:00:99" />'
        for name, outcome in results
    )
    return f'<TestRun xmlns="urn:test"><Results>{rows}</Results></TestRun>'


class TrxEvidenceTests(unittest.TestCase):
    def test_order_and_duration_do_not_change_digest(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            first = Path(temp) / "a.trx"
            second = Path(temp) / "b.trx"
            first.write_text(trx([("B", "Passed"), ("A", "Failed")]), encoding="utf-8")
            second.write_text(trx([("A", "Failed"), ("B", "Passed")]), encoding="utf-8")
            self.assertEqual(
                MODULE.summarize([first])["result_sha256"],
                MODULE.summarize([second])["result_sha256"],
            )

    def test_outcome_change_changes_digest(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            first = Path(temp) / "a.trx"
            second = Path(temp) / "b.trx"
            first.write_text(trx([("A", "Passed")]), encoding="utf-8")
            second.write_text(trx([("A", "Failed")]), encoding="utf-8")
            self.assertNotEqual(
                MODULE.summarize([first])["result_sha256"],
                MODULE.summarize([second])["result_sha256"],
            )

    def test_empty_trx_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "empty.trx"
            path.write_text('<TestRun xmlns="urn:test"><Results /></TestRun>', encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "no executed"):
                MODULE.summarize([path])


if __name__ == "__main__":
    unittest.main()
