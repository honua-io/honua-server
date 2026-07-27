import datetime as dt
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location(
    "generate_capability_matrix", ROOT / "scripts/ci/generate-capability-matrix.py"
)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader
SPEC.loader.exec_module(MODULE)


def envelope(lane: str, protocol: str, run_date: str, *, green: bool = True) -> dict:
    """Minimal committed client-compat envelope, green under the strict
    all-pass/terminal criteria of capability-impact.py's is_green."""
    return {
        "client_lane": lane,
        "protocol": protocol,
        "run_date": run_date,
        "summary": {
            "total": 1,
            "passed": 1 if green else 0,
            "failed": 0 if green else 1,
            "skipped": 0,
            "not_applicable": 0,
        },
        "results": [{"name": "smoke", "status": "pass" if green else "fail"}],
    }


def write_envelopes(root: Path, envelopes: list[dict]) -> None:
    for index, document in enumerate(envelopes):
        path = root / f"{document['client_lane']}-{document['protocol']}-{index}.cert.json"
        path.write_text(json.dumps(document), encoding="utf-8")


class InteropEvidenceFreshnessTests(unittest.TestCase):
    def load(self, envelopes: list[dict]):
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            write_envelopes(root, envelopes)
            return MODULE.load_interop_evidence(root)

    def test_future_dated_envelope_fails_generation_with_explicit_error(self):
        # A future-dated run_date is corrupt input: it must never anchor the
        # window (it would self-report fresh while staling legitimate
        # evidence), and silently excluding it against wall clock would make
        # the committed bytes time-dependent. The generator refuses to run.
        future = (dt.datetime.now(dt.timezone.utc) + dt.timedelta(days=30)).isoformat().replace("+00:00", "Z")
        with self.assertRaises(SystemExit) as raised:
            self.load(
                [
                    envelope("js", "wms", future),
                    envelope("cli", "wfs", "2026-07-20T00:00:00Z"),
                ]
            )
        message = str(raised.exception)
        self.assertIn("future-dated", message)
        self.assertIn("js", message)
        self.assertIn("wms", message)

    def test_all_future_dated_envelopes_fail_generation_too(self):
        future = (dt.datetime.now(dt.timezone.utc) + dt.timedelta(days=5)).isoformat().replace("+00:00", "Z")
        with self.assertRaises(SystemExit) as raised:
            self.load([envelope("js", "wms", future)])
        self.assertIn("future-dated", str(raised.exception))

    def test_valid_envelopes_anchor_and_stamp_fresh(self):
        legit_date = "2026-07-20T00:00:00Z"
        loaded, anchor = self.load([envelope("cli", "wfs", legit_date)])
        self.assertEqual(anchor.isoformat().replace("+00:00", "Z"), legit_date)
        self.assertEqual(
            MODULE.interop_freshness(loaded[("cli", "wfs")], anchor),
            {"state": "fresh", "ageDays": 0, "runDate": legit_date},
        )

    def test_negative_age_is_defensively_stale_with_reason(self):
        # load_interop_evidence rejects future-dated input outright, so an
        # envelope newer than the anchor should be unreachable — but the
        # stamping still fails closed defensively with an explicit reason.
        newer = envelope("js", "wms", "2026-07-20T00:00:00Z")
        older_anchor = dt.datetime(2026, 7, 1, tzinfo=dt.timezone.utc)
        flagged = MODULE.interop_freshness(newer, older_anchor)
        self.assertEqual(flagged["state"], "stale")
        self.assertLess(flagged["ageDays"], 0)
        self.assertIn("future-dated", flagged["reason"])

    def test_stale_and_not_green_envelopes_have_no_reason_flag(self):
        newest = "2026-07-20T00:00:00Z"
        old = "2026-05-01T00:00:00Z"
        loaded, anchor = self.load(
            [
                envelope("js", "wms", newest),
                envelope("cli", "wfs", old),
                envelope("desktop-qgis", "wfs", newest, green=False),
            ]
        )
        aged = MODULE.interop_freshness(loaded[("cli", "wfs")], anchor)
        self.assertEqual(aged["state"], "stale")
        self.assertGreater(aged["ageDays"], MODULE.FRESHNESS_MAX_AGE_DAYS)
        self.assertNotIn("reason", aged)
        not_green = MODULE.interop_freshness(loaded[("desktop-qgis", "wfs")], anchor)
        self.assertEqual(not_green["state"], "stale")
        self.assertNotIn("reason", not_green)


if __name__ == "__main__":
    unittest.main()
