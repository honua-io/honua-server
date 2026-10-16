"""Lock public desktop-arcgis example envelopes.

These files are historical schema examples. They are excluded from
certification gates and are not a desktop certification result. This test
does not import a desktop runner.
"""

from __future__ import annotations

import json
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[3]
EXAMPLES = REPO_ROOT / "docs/gis/certification-evidence/20260402T000000Z"
EXAMPLE_FILES = (
    "20260402T000000Z-desktop-arcgis-featureserver.cert.example.json",
    "20260402T000000Z-desktop-arcgis-mapserver.cert.example.json",
)
STATUSES = {"pass", "fail", "skip", "not-applicable"}


def test_example_envelopes_cite_desktop_arcgis_cell_and_status():
    for name in EXAMPLE_FILES:
        path = EXAMPLES / name
        envelope = json.loads(path.read_text(encoding="utf-8"))
        assert name.endswith(".cert.example.json")
        assert envelope["client_lane"] == "desktop-arcgis"
        counts = {status: 0 for status in STATUSES}
        seen: set[str] = set()
        for result in envelope["results"]:
            cell = result["test_case_id"]
            status = result["status"]
            assert isinstance(cell, str) and cell
            assert cell not in seen
            seen.add(cell)
            assert status in STATUSES
            counts[status] += 1
        summary = envelope["summary"]
        assert summary["total"] == len(envelope["results"])
        assert summary["passed"] == counts["pass"]
        assert summary["failed"] == counts["fail"]
        assert summary["skipped"] == counts["skip"]
        assert summary["not_applicable"] == counts["not-applicable"]
