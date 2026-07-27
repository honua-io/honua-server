import importlib.util
import json
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location(
    "capability_impact_observation_report", ROOT / "scripts/ci/capability-impact-observation-report.py"
)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader
SPEC.loader.exec_module(MODULE)


def report(
    *,
    capability_shards,
    legacy_shards,
    capability_run_all=False,
    capability_reason="capability_match",
    legacy_run_all=False,
    legacy_reason="targeted",
):
    """Build a fixture matching capability-impact.py build_report's output shape."""
    capability = set(capability_shards)
    legacy = set(legacy_shards)
    return {
        "schemaVersion": 1,
        "mode": "report-only",
        "changedFileCount": 2,
        "capabilityLabels": [],
        "legacy": {"run_all": legacy_run_all, "shards": sorted(legacy), "reason": legacy_reason},
        "capabilitySelection": {
            "runAll": capability_run_all,
            "reason": capability_reason,
            "capabilities": ["serve.x"],
            "provingTestCount": 3,
            "shards": sorted(capability),
            "interopLanes": [],
            "unmatchedSourceFiles": [],
        },
        "comparison": {
            "legacyShardCount": len(legacy),
            "capabilityShardCount": len(capability),
            "legacyOnlyShards": sorted(legacy - capability),
            "capabilityOnlyShards": sorted(capability - legacy),
            "escapedDefectCandidates": sorted(legacy - capability),
        },
        "freshness": [],
        "freshnessSummary": {"greenCount": 0, "staleCount": 0, "observedCount": 0},
    }


FIXTURES = [
    # Capability strict subset of legacy: legacy-only "B" is an escaped candidate.
    report(capability_shards=["A"], legacy_shards=["A", "B"]),
    # Equal selections.
    report(capability_shards=["A", "B"], legacy_shards=["A", "B"]),
    # Capability strict superset of legacy.
    report(capability_shards=["A", "B", "C"], legacy_shards=["A"]),
    # Legacy fell back to run_all; capability escalated too (unmapped source).
    report(
        capability_shards=["A", "B", "C", "D"],
        legacy_shards=["A", "B", "C", "D"],
        capability_run_all=True,
        capability_reason="unmapped_graph_source",
        legacy_run_all=True,
        legacy_reason="infrastructure_change",
    ),
]


def write_fixtures(root: Path, reports):
    for index, document in enumerate(reports):
        directory = root / f"capability-impact-{index}"
        directory.mkdir(parents=True)
        (directory / "capability-impact-report.json").write_text(json.dumps(document), encoding="utf-8")
    # Non-report artifact members must be ignored.
    (root / "capability-impact-0" / "legacy-selection.json").write_text(
        json.dumps({"run_all": False, "shards": ["A"]}), encoding="utf-8"
    )
    (root / "capability-impact-0" / "changed-files.txt").write_text("src/X.cs\n", encoding="utf-8")
    (root / "not-json.json").write_text("{broken", encoding="utf-8")


class ObservationReportTests(unittest.TestCase):
    def aggregate(self, reports=FIXTURES, **overrides):
        options = {"min_comparisons": 25, "max_escaped_candidate_reports": 0, "min_strict_subset_pct": 60.0}
        options.update(overrides)
        return MODULE.aggregate(reports, 4, **options)

    def test_from_dir_loads_only_comparison_reports(self):
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            write_fixtures(root, FIXTURES)
            reports = MODULE.load_reports_from_dir(root)
        self.assertEqual(len(reports), len(FIXTURES))
        self.assertTrue(all(MODULE.is_comparison_report(document) for document in reports))

    def test_aggregate_counts_relations_and_size_distributions(self):
        summary = self.aggregate()
        self.assertEqual(summary["comparisonCount"], 4)
        self.assertEqual(summary["totalShardCount"], 4)
        self.assertEqual(
            summary["relations"],
            {"equal": 2, "capabilitySubsetOfLegacy": 1, "capabilitySupersetOfLegacy": 1, "divergent": 0},
        )
        self.assertEqual(summary["selectionSizes"]["capability"], {"min": 1, "median": 2.5, "mean": 2.5, "max": 4})
        self.assertEqual(summary["selectionSizes"]["legacy"], {"min": 1, "median": 2.0, "mean": 2.25, "max": 4})
        self.assertEqual(summary["strictlySmaller"], {"count": 1, "pct": 25.0})

    def test_legacy_only_frequency_and_escaped_candidate_rollup(self):
        summary = self.aggregate()
        self.assertEqual(summary["legacyOnlyShardFrequency"], {"B": 1})
        self.assertEqual(summary["escapedDefectCandidates"]["shardFrequency"], {"B": 1})
        self.assertEqual(summary["escapedDefectCandidates"]["reportsWithCandidates"], 1)
        self.assertEqual(summary["escapedDefectCandidates"]["reportsWithCandidatesExcludingLegacyRunAll"], 1)

    def test_run_all_fallback_frequency_and_reasons(self):
        summary = self.aggregate()
        self.assertEqual(summary["runAllFallbacks"]["capability"], {"count": 1, "reasons": {"unmapped_graph_source": 1}})
        self.assertEqual(summary["runAllFallbacks"]["legacy"], {"count": 1, "reasons": {"infrastructure_change": 1}})

    def test_switch_recommendation_not_safe_with_escaped_candidates_or_few_comparisons(self):
        summary = self.aggregate()
        recommendation = summary["switchRecommendation"]
        self.assertFalse(recommendation["safeToSwitch"])
        by_name = {criterion["name"]: criterion for criterion in recommendation["criteria"]}
        self.assertFalse(by_name["comparisonCount"]["met"])
        self.assertFalse(by_name["reportsWithEscapedDefectCandidates"]["met"])
        self.assertFalse(by_name["strictlySmallerSelectionPct"]["met"])

    def test_switch_recommendation_safe_when_all_criteria_met(self):
        clean = [report(capability_shards=["A"], legacy_shards=["A", "B"]) for _ in range(3)]
        for document in clean:
            document["comparison"]["escapedDefectCandidates"] = []
            document["comparison"]["legacyOnlyShards"] = []
            document["comparison"]["legacyShardCount"] = 2
        summary = self.aggregate(clean, min_comparisons=3, min_strict_subset_pct=60.0)
        self.assertTrue(summary["switchRecommendation"]["safeToSwitch"])

    def test_empty_window_produces_null_distributions_and_unsafe_verdict(self):
        summary = self.aggregate([])
        self.assertEqual(summary["comparisonCount"], 0)
        self.assertEqual(summary["selectionSizes"]["capability"], {"min": None, "median": None, "mean": None, "max": None})
        self.assertFalse(summary["switchRecommendation"]["safeToSwitch"])

    def test_markdown_states_thresholds_and_verdict(self):
        summary = self.aggregate()
        rendered = MODULE.markdown(summary, 28)
        self.assertIn("## Capability selection observation report", rendered)
        self.assertIn("ADR-0037 remains authoritative", rendered)
        self.assertIn("### Switch recommendation", rendered)
        self.assertIn("comparisonCount >= 25", rendered)
        self.assertIn("reportsWithEscapedDefectCandidates <= 0", rendered)
        self.assertIn("strictlySmallerSelectionPct >= 60.0", rendered)
        self.assertIn("NOT yet safe to switch", rendered)
        self.assertIn("| B | 1 |", rendered)

    def test_cli_offline_mode_emits_json_and_markdown(self):
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            write_fixtures(root / "artifacts", FIXTURES)
            markdown_path = root / "summary.md"
            import contextlib
            import io

            stdout = io.StringIO()
            with contextlib.redirect_stdout(stdout):
                exit_code = MODULE.main(
                    [
                        "--from-dir",
                        str(root / "artifacts"),
                        "--total-shards",
                        "4",
                        "--markdown",
                        str(markdown_path),
                    ]
                )
            self.assertEqual(exit_code, 0)
            summary = json.loads(stdout.getvalue())
            self.assertEqual(summary["comparisonCount"], 4)
            self.assertIn("Verdict", markdown_path.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
