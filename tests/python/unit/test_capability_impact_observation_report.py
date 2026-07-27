import importlib.util
import io
import json
import tempfile
import unittest
import zipfile
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
    changed_file_count=2,
):
    """Build a fixture matching capability-impact.py build_report's output shape.

    Mirrors the authoritative definition: `escapedDefectCandidates` is emitted
    as exactly the legacy-only set difference (capability-impact.py line ~393).
    """
    capability = set(capability_shards)
    legacy = set(legacy_shards)
    return {
        "schemaVersion": 1,
        "mode": "report-only",
        "changedFileCount": changed_file_count,
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


def dispatch_shaped_empty_report():
    """A workflow_dispatch (trunk-vs-trunk) artifact: zero changed files, empty selections."""
    return report(capability_shards=[], legacy_shards=[], changed_file_count=0)


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
    # A dispatch-shaped empty comparison must be excluded from aggregation.
    (root / "capability-impact-dispatch").mkdir(parents=True)
    (root / "capability-impact-dispatch" / "capability-impact-report.json").write_text(
        json.dumps(dispatch_shaped_empty_report()), encoding="utf-8"
    )


def report_zip(document) -> bytes:
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as archive:
        archive.writestr("capability-impact-report.json", json.dumps(document))
    return buffer.getvalue()


class ObservationReportTests(unittest.TestCase):
    def aggregate(self, reports=FIXTURES, **overrides):
        options = {"min_comparisons": 25, "min_strict_subset_pct": 60.0}
        options.update(overrides)
        return MODULE.aggregate(reports, 4, **options)

    def test_from_dir_loads_only_comparison_reports(self):
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            write_fixtures(root, FIXTURES)
            reports = MODULE.load_reports_from_dir(root)
        # The dispatch-shaped empty report in the fixture directory is excluded.
        self.assertEqual(len(reports), len(FIXTURES))
        self.assertTrue(all(MODULE.is_comparison_report(document) for document in reports))
        self.assertTrue(all(document["changedFileCount"] > 0 for document in reports))

    def test_observable_comparison_excludes_empty_reports(self):
        self.assertFalse(MODULE.is_observable_comparison(dispatch_shaped_empty_report()))
        # Changed files present but nothing selected on either side and no
        # run_all: nothing to compare.
        hollow = report(capability_shards=[], legacy_shards=[])
        self.assertFalse(MODULE.is_observable_comparison(hollow))
        # A docs-only PR (files changed, capability empty, legacy non-empty) stays.
        docs_only = report(capability_shards=[], legacy_shards=["Core"])
        self.assertTrue(MODULE.is_observable_comparison(docs_only))

    def test_fetch_reports_filters_to_pull_request_runs_and_skips_empty_artifacts(self):
        calls = []
        payloads = [report_zip(dispatch_shaped_empty_report()), report_zip(FIXTURES[0])]
        run = {
            "id": 1,
            "url": "https://github.com/honua-io/honua-server/actions/runs/1",
            "createdAt": "2026-07-26T00:00:00Z",
            "prNumber": 3031,
        }

        def fake_gh_api(arguments, *, binary=False):
            calls.append(arguments)
            if binary:
                return payloads.pop(0)
            if "/artifacts" in arguments[2]:
                return "\n".join(
                    json.dumps(artifact)
                    for artifact in ({"id": 10, "name": "capability-impact-10"}, {"id": 11, "name": "capability-impact-11"})
                )
            return json.dumps(run)

        original = MODULE.gh_api
        MODULE.gh_api = fake_gh_api
        try:
            reports = MODULE.fetch_reports("honua-io/honua-server", 28)
        finally:
            MODULE.gh_api = original
        runs_request = calls[0]
        self.assertIn("event=pull_request", runs_request)
        self.assertEqual(len(reports), 1)
        self.assertEqual(reports[0]["changedFileCount"], 2)
        # Provenance is stamped for the escape-correlation table.
        self.assertEqual(
            reports[0]["_meta"],
            {
                "runId": 1,
                "runUrl": "https://github.com/honua-io/honua-server/actions/runs/1",
                "runCreatedAt": "2026-07-26T00:00:00Z",
                "prNumber": 3031,
                "artifactName": "capability-impact-11",
            },
        )
        summary = self.aggregate(reports, min_comparisons=1, min_strict_subset_pct=50.0)
        occurrences = summary["legacyOnlyShards"]["occurrences"]
        self.assertEqual(len(occurrences), 1)
        self.assertEqual(occurrences[0]["prNumber"], 3031)
        self.assertEqual(occurrences[0]["legacyOnlyShards"], ["B"])
        rendered = MODULE.markdown(summary, 28)
        self.assertIn("### Reports needing escape correlation", rendered)
        self.assertIn("| [1](https://github.com/honua-io/honua-server/actions/runs/1) | #3031 | B |", rendered)

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

    def test_legacy_only_shard_rollup_consumes_per_report_field(self):
        summary = self.aggregate()
        legacy_only = summary["legacyOnlyShards"]
        self.assertEqual(legacy_only["shardFrequency"], {"B": 1})
        self.assertEqual(legacy_only["reportsWithLegacyOnlyShards"], 1)
        self.assertEqual(legacy_only["reportsWithLegacyOnlyShardsExcludingLegacyRunAll"], 1)
        # The aggregate must consume comparison.escapedDefectCandidates (the
        # authoritative per-report field), not recompute a set difference.
        divergent = report(capability_shards=["A", "C"], legacy_shards=["A", "B"])
        divergent["comparison"]["escapedDefectCandidates"] = ["Z"]
        summary = self.aggregate([divergent])
        self.assertEqual(summary["legacyOnlyShards"]["shardFrequency"], {"Z": 1})

    def test_from_dir_occurrences_carry_source_identity_and_omit_clean_reports(self):
        embedded = report(capability_shards=["A"], legacy_shards=["A", "C"])
        embedded["_meta"] = {"runId": 7, "runUrl": "https://example.test/runs/7", "prNumber": 42}
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            write_fixtures(root, FIXTURES + [embedded])
            reports = MODULE.load_reports_from_dir(root)
        summary = self.aggregate(reports)
        occurrences = summary["legacyOnlyShards"]["occurrences"]
        # Only the two reports with legacy-only shards appear; clean reports
        # (equal, superset, run_all-equal) are omitted.
        self.assertEqual(summary["comparisonCount"], len(FIXTURES) + 1)
        self.assertEqual(len(occurrences), 2)
        by_shards = {tuple(entry["legacyOnlyShards"]): entry for entry in occurrences}
        # From-dir identity falls back to the report's path within the artifact dir.
        self.assertEqual(by_shards[("B",)]["source"], "capability-impact-0/capability-impact-report.json")
        self.assertIsNone(by_shards[("B",)]["runId"])
        # Embedded _meta provenance is preserved and merged with the source path.
        self.assertEqual(by_shards[("C",)]["runId"], 7)
        self.assertEqual(by_shards[("C",)]["prNumber"], 42)
        self.assertEqual(by_shards[("C",)]["source"], f"capability-impact-{len(FIXTURES)}/capability-impact-report.json")
        rendered = MODULE.markdown(summary, 28)
        self.assertIn("### Reports needing escape correlation", rendered)
        self.assertIn("| capability-impact-0/capability-impact-report.json | ? | B |", rendered)
        self.assertIn("| [7](https://example.test/runs/7) | #42 | C |", rendered)

    def test_run_all_fallback_frequency_and_reasons(self):
        summary = self.aggregate()
        self.assertEqual(summary["runAllFallbacks"]["capability"], {"count": 1, "reasons": {"unmapped_graph_source": 1}})
        self.assertEqual(summary["runAllFallbacks"]["legacy"], {"count": 1, "reasons": {"infrastructure_change": 1}})

    def test_switch_recommendation_not_safe_with_few_comparisons_or_low_subset_pct(self):
        summary = self.aggregate()
        recommendation = summary["switchRecommendation"]
        self.assertFalse(recommendation["preconditionsMet"])
        self.assertFalse(recommendation["safeToSwitch"])
        by_name = {criterion["name"]: criterion for criterion in recommendation["criteria"]}
        self.assertEqual(set(by_name), {"comparisonCount", "strictlySmallerSelectionPct"})
        self.assertFalse(by_name["comparisonCount"]["met"])
        self.assertFalse(by_name["strictlySmallerSelectionPct"]["met"])

    def test_strictly_smaller_selection_does_not_block_preconditions(self):
        # A genuinely tighter selector produces legacy-only shards on every
        # strictly-smaller comparison (escapedDefectCandidates is definitionally
        # the legacy-only set); those are informational and must not make the
        # quantitative criteria mutually exclusive with min_strict_subset_pct.
        tighter = [report(capability_shards=["A"], legacy_shards=["A", "B"]) for _ in range(3)]
        summary = self.aggregate(tighter, min_comparisons=3, min_strict_subset_pct=60.0)
        self.assertEqual(summary["strictlySmaller"], {"count": 3, "pct": 100.0})
        self.assertEqual(summary["legacyOnlyShards"]["reportsWithLegacyOnlyShards"], 3)
        self.assertTrue(summary["switchRecommendation"]["preconditionsMet"])

    def test_safe_to_switch_requires_operator_escape_review_acknowledgment(self):
        tighter = [report(capability_shards=["A"], legacy_shards=["A", "B"]) for _ in range(3)]
        # Preconditions met, no acknowledgment: verdict caps below safe.
        summary = self.aggregate(tighter, min_comparisons=3, min_strict_subset_pct=60.0)
        recommendation = summary["switchRecommendation"]
        self.assertTrue(recommendation["preconditionsMet"])
        self.assertFalse(recommendation["escapesReviewed"])
        self.assertFalse(recommendation["safeToSwitch"])
        rendered = MODULE.markdown(summary, 28)
        self.assertIn("PRECONDITIONS MET", rendered)
        self.assertIn("--escapes-reviewed", rendered)
        self.assertNotIn("SAFE to switch", rendered)
        # With the acknowledgment the full safe verdict is allowed.
        summary = self.aggregate(tighter, min_comparisons=3, min_strict_subset_pct=60.0, escapes_reviewed=True)
        recommendation = summary["switchRecommendation"]
        self.assertTrue(recommendation["escapesReviewed"])
        self.assertTrue(recommendation["safeToSwitch"])
        self.assertIn("SAFE to switch", MODULE.markdown(summary, 28))
        # Acknowledgment alone never overrides unmet preconditions.
        summary = self.aggregate(escapes_reviewed=True)
        self.assertFalse(summary["switchRecommendation"]["safeToSwitch"])
        self.assertIn("NOT yet safe to switch", MODULE.markdown(summary, 28))

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
        self.assertIn("strictlySmallerSelectionPct >= 60.0", rendered)
        self.assertNotIn("reportsWithEscapedDefectCandidates", rendered)
        self.assertIn("cannot be a zero-tolerance switch criterion", rendered)
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
