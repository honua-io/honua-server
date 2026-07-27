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
            "headSha": "deadbeef",
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
                "headSha": "deadbeef",
                "prNumber": 3031,
                "artifactName": "capability-impact-11",
                "newestRunForPr": {
                    "runId": 1,
                    "runUrl": "https://github.com/honua-io/honua-server/actions/runs/1",
                    "runCreatedAt": "2026-07-26T00:00:00Z",
                },
            },
        )
        summary = self.aggregate(reports, min_comparisons=1, min_strict_subset_pct=50.0)
        occurrences = summary["legacyOnlyShards"]["occurrences"]
        self.assertEqual(len(occurrences), 1)
        self.assertEqual(occurrences[0]["prNumber"], 3031)
        self.assertEqual(occurrences[0]["legacyOnlyShards"], ["B"])
        rendered = MODULE.markdown(summary, 28)
        self.assertIn("### Reports needing escape correlation", rendered)
        self.assertIn("| [1](https://github.com/honua-io/honua-server/actions/runs/1) | #3031 | B | B=unknown |", rendered)

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

    def test_sample_dedupes_to_latest_run_per_pr(self):
        older = report(capability_shards=["A"], legacy_shards=["A", "B"])
        older["_meta"] = {"prNumber": 5, "runId": 1, "runCreatedAt": "2026-07-01T00:00:00Z"}
        newer = report(capability_shards=["A", "B"], legacy_shards=["A", "B"])
        newer["_meta"] = {"prNumber": 5, "runId": 2, "runCreatedAt": "2026-07-02T00:00:00Z"}
        other = report(capability_shards=["A"], legacy_shards=["A", "B"])
        other["_meta"] = {"prNumber": 6, "runId": 3, "runCreatedAt": "2026-07-01T12:00:00Z"}
        summary = self.aggregate([older, newer, other], min_comparisons=2, min_strict_subset_pct=50.0)
        # One hot PR with two synchronize pushes counts once (latest run wins).
        self.assertEqual(summary["comparisonCount"], 2)
        self.assertEqual(summary["rawReportCount"], 3)
        self.assertEqual(summary["relations"]["equal"], 1)
        self.assertEqual(summary["relations"]["capabilitySubsetOfLegacy"], 1)
        self.assertEqual(summary["strictlySmaller"], {"count": 1, "pct": 50.0})
        self.assertEqual(summary["legacyOnlyShards"]["reportsWithLegacyOnlyShards"], 1)
        self.assertEqual(summary["legacyOnlyShards"]["shardFrequency"], {"B": 1})
        # Occurrences keep all reports with candidates, marking which is latest.
        occurrences = summary["legacyOnlyShards"]["occurrences"]
        self.assertEqual([(entry["runId"], entry["latestForPr"]) for entry in occurrences], [(1, False), (3, True)])
        self.assertTrue(summary["switchRecommendation"]["preconditionsMet"])
        rendered = MODULE.markdown(summary, 28)
        self.assertIn("(latest run per distinct PR; 3 raw reports)", rendered)
        self.assertIn("#5 (superseded)", rendered)

    def test_join_executed_outcomes_marks_failures_and_blocks_switch(self):
        document = report(capability_shards=["A"], legacy_shards=["A", "B"])
        document["_meta"] = {"prNumber": 9, "runId": 4, "headSha": "abc123", "runCreatedAt": "2026-07-01T00:00:00Z"}
        clean = report(capability_shards=["A"], legacy_shards=["A"])  # no candidates: no CI lookup
        calls = []

        def fake_gh_api(arguments, *, binary=False):
            calls.append(arguments)
            if "head_sha=abc123" in arguments:
                # Two runs at the head: the report-only comparison run (must be
                # skipped) and the ci.yml run that executed the shard matrix.
                return "\n".join(
                    json.dumps(run)
                    for run in (
                        {
                            "id": 76,
                            "url": "https://ci.test/runs/76",
                            "createdAt": "2026-07-01T00:20:00Z",
                            "path": ".github/workflows/capability-impact-comparison.yml",
                        },
                        {
                            "id": 77,
                            "url": "https://ci.test/runs/77",
                            "createdAt": "2026-07-01T00:10:00Z",
                            "path": ".github/workflows/ci.yml",
                        },
                    )
                )
            self.assertIn("actions/runs/77/jobs", arguments[2])
            return "\n".join(
                json.dumps(job)
                for job in (
                    {"name": "Server Tests (A)", "conclusion": "success"},
                    {"name": "Server Tests (B)", "conclusion": "failure"},
                )
            )

        original = MODULE.gh_api
        MODULE.gh_api = fake_gh_api
        try:
            MODULE.join_executed_outcomes("honua-io/honua-server", [document, clean])
        finally:
            MODULE.gh_api = original
        self.assertEqual(len(calls), 2)
        self.assertIn("head_sha=abc123", calls[0])
        self.assertEqual(document["_meta"]["ciRunUrl"], "https://ci.test/runs/77")
        self.assertEqual(
            document["_meta"]["shardOutcomes"],
            {"B": {"jobName": "Server Tests (B)", "conclusion": "failure", "runUrl": "https://ci.test/runs/77"}},
        )
        summary = self.aggregate([document], min_comparisons=1, min_strict_subset_pct=50.0, escapes_reviewed=True)
        recommendation = summary["switchRecommendation"]
        self.assertTrue(recommendation["preconditionsMet"])
        self.assertTrue(recommendation["escapedDefectSignal"])
        # A real executed failure blocks the switch even with the acknowledgment.
        self.assertFalse(recommendation["safeToSwitch"])
        self.assertEqual(
            recommendation["failedShardJobs"],
            [
                {
                    "prNumber": 9,
                    "shard": "B",
                    "jobName": "Server Tests (B)",
                    "conclusion": "failure",
                    "ciRunUrl": "https://ci.test/runs/77",
                }
            ],
        )
        rendered = MODULE.markdown(summary, 28)
        self.assertIn("ESCAPED-DEFECT SIGNAL", rendered)
        self.assertIn("[B=failure](https://ci.test/runs/77)", rendered)

    def test_non_success_conclusions_signal_or_count_as_gaps(self):
        # timed_out is a demonstrated non-pass: raises the blocking signal.
        timed_out = report(capability_shards=["A"], legacy_shards=["A", "B"])
        timed_out["_meta"] = {
            "prNumber": 11,
            "shardOutcomes": {"B": {"jobName": "Server Tests (B)", "conclusion": "timed_out", "runUrl": "https://ci.test/runs/80"}},
        }
        summary = self.aggregate([timed_out], min_comparisons=1, min_strict_subset_pct=50.0, escapes_reviewed=True)
        recommendation = summary["switchRecommendation"]
        self.assertTrue(recommendation["escapedDefectSignal"])
        self.assertFalse(recommendation["safeToSwitch"])
        self.assertEqual(recommendation["failedShardJobs"][0]["conclusion"], "timed_out")
        rendered = MODULE.markdown(summary, 28)
        self.assertIn("ESCAPED-DEFECT SIGNAL", rendered)
        self.assertIn("B=timed_out (PR #11)", rendered)
        # cancelled is neither clean nor a signal: it must be counted as a
        # coverage gap needing explicit review, not fall through silently.
        cancelled = report(capability_shards=["A"], legacy_shards=["A", "B"])
        cancelled["_meta"] = {
            "prNumber": 12,
            "shardOutcomes": {"B": {"jobName": "Server Tests (B)", "conclusion": "cancelled", "runUrl": None}},
        }
        summary = self.aggregate([cancelled], min_comparisons=1, min_strict_subset_pct=50.0, escapes_reviewed=True)
        self.assertFalse(summary["switchRecommendation"]["escapedDefectSignal"])
        self.assertEqual(summary["legacyOnlyShards"]["unknownOutcomeCount"], 1)
        self.assertIn("B=cancelled", MODULE.markdown(summary, 28))
        # A verified success is clean: no signal, no gap.
        succeeded = report(capability_shards=["A"], legacy_shards=["A", "B"])
        succeeded["_meta"] = {
            "prNumber": 13,
            "shardOutcomes": {"B": {"jobName": "Server Tests (B)", "conclusion": "success", "runUrl": None}},
        }
        summary = self.aggregate([succeeded], min_comparisons=1, min_strict_subset_pct=50.0, escapes_reviewed=True)
        self.assertFalse(summary["switchRecommendation"]["escapedDefectSignal"])
        self.assertEqual(summary["legacyOnlyShards"]["unknownOutcomeCount"], 0)
        self.assertTrue(summary["switchRecommendation"]["safeToSwitch"])

    def test_outcomes_merge_across_runs_at_same_head(self):
        # Newest run at the head lacks the candidate shard; an older run
        # executed it and failed. The failure must not be dropped as unknown.
        document = report(capability_shards=["A"], legacy_shards=["A", "B"])
        document["_meta"] = {"prNumber": 14, "runId": 5, "headSha": "feed42", "runCreatedAt": "2026-07-02T00:00:00Z"}

        def fake_gh_api(arguments, *, binary=False):
            if "head_sha=feed42" in arguments:
                return "\n".join(
                    json.dumps(run)
                    for run in (
                        {
                            "id": 91,
                            "url": "https://ci.test/runs/91",
                            "createdAt": "2026-07-02T01:00:00Z",
                            "path": ".github/workflows/ci.yml",
                        },
                        {
                            "id": 90,
                            "url": "https://ci.test/runs/90",
                            "createdAt": "2026-07-02T00:30:00Z",
                            "path": ".github/workflows/ci.yml",
                        },
                    )
                )
            if "actions/runs/91/jobs" in arguments[2]:
                return json.dumps({"name": "Server Tests (A)", "conclusion": "success"})
            self.assertIn("actions/runs/90/jobs", arguments[2])
            return json.dumps({"name": "Server Tests (B)", "conclusion": "failure"})

        original = MODULE.gh_api
        MODULE.gh_api = fake_gh_api
        try:
            MODULE.join_executed_outcomes("honua-io/honua-server", [document])
        finally:
            MODULE.gh_api = original
        self.assertEqual(
            document["_meta"]["shardOutcomes"]["B"],
            {"jobName": "Server Tests (B)", "conclusion": "failure", "runUrl": "https://ci.test/runs/90"},
        )
        summary = self.aggregate([document], min_comparisons=1, min_strict_subset_pct=50.0, escapes_reviewed=True)
        recommendation = summary["switchRecommendation"]
        self.assertTrue(recommendation["escapedDefectSignal"])
        self.assertFalse(recommendation["safeToSwitch"])
        self.assertEqual(recommendation["failedShardJobs"][0]["ciRunUrl"], "https://ci.test/runs/90")

    def test_signal_conclusion_wins_merge_over_newer_non_signal(self):
        # Newest run at the head has the candidate shard skipped; an older run
        # executed it and failed. The demonstrated failure must win the merge.
        document = report(capability_shards=["A"], legacy_shards=["A", "B"])
        document["_meta"] = {"prNumber": 15, "runId": 6, "headSha": "cafe77", "runCreatedAt": "2026-07-03T00:00:00Z"}

        def fake_gh_api(arguments, *, binary=False):
            if "head_sha=cafe77" in arguments:
                return "\n".join(
                    json.dumps(run)
                    for run in (
                        {
                            "id": 93,
                            "url": "https://ci.test/runs/93",
                            "createdAt": "2026-07-03T01:00:00Z",
                            "path": ".github/workflows/ci.yml",
                        },
                        {
                            "id": 92,
                            "url": "https://ci.test/runs/92",
                            "createdAt": "2026-07-03T00:30:00Z",
                            "path": ".github/workflows/ci.yml",
                        },
                    )
                )
            if "actions/runs/93/jobs" in arguments[2]:
                return json.dumps({"name": "Server Tests (B)", "conclusion": "skipped"})
            self.assertIn("actions/runs/92/jobs", arguments[2])
            return json.dumps({"name": "Server Tests (B)", "conclusion": "failure"})

        original = MODULE.gh_api
        MODULE.gh_api = fake_gh_api
        try:
            MODULE.join_executed_outcomes("honua-io/honua-server", [document])
        finally:
            MODULE.gh_api = original
        self.assertEqual(
            document["_meta"]["shardOutcomes"]["B"],
            {"jobName": "Server Tests (B)", "conclusion": "failure", "runUrl": "https://ci.test/runs/92"},
        )
        summary = self.aggregate([document], min_comparisons=1, min_strict_subset_pct=50.0, escapes_reviewed=True)
        self.assertTrue(summary["switchRecommendation"]["escapedDefectSignal"])
        self.assertFalse(summary["switchRecommendation"]["safeToSwitch"])

    def test_pr_whose_newest_run_has_no_report_is_flagged_as_evidence_gap(self):
        # End to end through the fetch: PR 55 has an artifact only on the
        # OLDER run 1; a newer run 2 exists with no parseable report.
        payloads = [report_zip(FIXTURES[0])]

        def fake_gh_api(arguments, *, binary=False):
            if binary:
                return payloads.pop(0)
            if "event=pull_request" in arguments:
                return "\n".join(
                    json.dumps(run)
                    for run in (
                        {
                            "id": 2,
                            "url": "https://runs.test/2",
                            "createdAt": "2026-07-25T02:00:00Z",
                            "headSha": "bbb",
                            "prNumber": 55,
                        },
                        {
                            "id": 1,
                            "url": "https://runs.test/1",
                            "createdAt": "2026-07-25T01:00:00Z",
                            "headSha": "aaa",
                            "prNumber": 55,
                        },
                    )
                )
            if "actions/runs/2/artifacts" in arguments[2]:
                return ""  # newest run uploaded nothing (cancelled/failed)
            self.assertIn("actions/runs/1/artifacts", arguments[2])
            return json.dumps({"id": 10, "name": "capability-impact-10"})

        original = MODULE.gh_api
        MODULE.gh_api = fake_gh_api
        try:
            reports = MODULE.fetch_reports("honua-io/honua-server", 28)
        finally:
            MODULE.gh_api = original
        self.assertEqual(len(reports), 1)
        self.assertEqual(reports[0]["_meta"]["newestRunForPr"]["runId"], 2)
        summary = self.aggregate(reports, min_comparisons=1, min_strict_subset_pct=50.0)
        self.assertEqual(
            summary["staleEvidencePrs"],
            [
                {
                    "prNumber": 55,
                    "latestReportRunId": 1,
                    "latestReportRunUrl": "https://runs.test/1",
                    "newestRunId": 2,
                    "newestRunUrl": "https://runs.test/2",
                }
            ],
        )
        rendered = MODULE.markdown(summary, 28)
        self.assertIn("### Evidence gaps (newest run has no report)", rendered)
        self.assertIn("PR #55: newest run [2](https://runs.test/2) produced no comparison report", rendered)
        # Control: when the sampled report IS from the newest run, no gap.
        current = report(capability_shards=["A"], legacy_shards=["A"])
        current["_meta"] = {
            "prNumber": 56,
            "runId": 7,
            "newestRunForPr": {"runId": 7, "runUrl": "https://runs.test/7", "runCreatedAt": "2026-07-25T03:00:00Z"},
        }
        summary = self.aggregate([current], min_comparisons=1, min_strict_subset_pct=0.0)
        self.assertEqual(summary["staleEvidencePrs"], [])
        self.assertNotIn("Evidence gaps", MODULE.markdown(summary, 28))

    def test_unknown_outcomes_are_counted_as_gaps_not_signal(self):
        document = report(capability_shards=["A"], legacy_shards=["A", "B"])  # no shardOutcomes embedded
        summary = self.aggregate([document], min_comparisons=1, min_strict_subset_pct=50.0, escapes_reviewed=True)
        recommendation = summary["switchRecommendation"]
        self.assertFalse(recommendation["escapedDefectSignal"])
        self.assertTrue(recommendation["safeToSwitch"])
        self.assertEqual(summary["legacyOnlyShards"]["unknownOutcomeCount"], 1)
        rendered = MODULE.markdown(summary, 28)
        self.assertIn("B=unknown", rendered)
        self.assertIn("Non-success shard outcomes needing review: 1", rendered)

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
