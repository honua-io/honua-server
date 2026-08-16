#!/usr/bin/env python3
"""Unit tests for scripts/ci/audit-merged-pr-landing.py (#3248).

Runs offline: every case feeds the classifier directly or drives main() through
a fixture with pre-resolved `landed` / `baseLanded` flags, so no gh call and no
git repository are required.
"""

from __future__ import annotations

import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parent / "audit-merged-pr-landing.py"
_spec = importlib.util.spec_from_file_location("audit_merged_pr_landing", SCRIPT)
assert _spec and _spec.loader
mod = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(mod)

TRUNK = "trunk"


class IsStackBaseTests(unittest.TestCase):
    def test_trunk_base_is_not_a_stack(self):
        self.assertFalse(mod.is_stack_base("trunk", TRUNK))

    def test_merge_train_batch_base_is_not_a_stack(self):
        self.assertFalse(mod.is_stack_base("train/batch/abc123/7", TRUNK))

    def test_feature_branch_base_is_a_stack(self):
        self.assertTrue(mod.is_stack_base("feat/2661-tilecache-checkpoint", TRUNK))


class SelectionTests(unittest.TestCase):
    def test_open_and_unmerged_prs_are_not_audited_for_landing(self):
        prs = [
            {"number": 1, "state": "OPEN", "baseRefName": "trunk"},
            {"number": 2, "state": "CLOSED", "mergedAt": None, "baseRefName": "trunk"},
            {"number": 3, "state": "MERGED", "mergedAt": "2026-08-03T00:00:00Z",
             "mergeCommit": {"oid": "abc"}, "baseRefName": "trunk"},
        ]
        self.assertEqual([p["number"] for p in mod.select_merged_prs(prs)], [3])
        self.assertEqual([p["number"] for p in mod.select_open_prs(prs)], [1])

    def test_merged_pr_without_a_merge_commit_is_skipped(self):
        prs = [{"number": 9, "state": "MERGED", "mergedAt": "2026-08-03T00:00:00Z",
                "mergeCommit": None, "baseRefName": "trunk"}]
        self.assertEqual(mod.select_merged_prs(prs), [])


class ClassifyMergedTests(unittest.TestCase):
    def _pr(self, base: str):
        return {"number": 2835, "title": "feat(tiles): ...", "url": "u",
                "mergedAt": "2026-07-15T00:00:00Z", "baseRefName": base,
                "mergeCommit": {"oid": "485952be53b3204a9c8"}}

    def test_merged_into_a_stack_base_that_never_landed_is_stranded(self):
        r = mod.classify_merged_pr(self._pr("feat/2661-tilecache-checkpoint"),
                                   landed=False, trunk_branch=TRUNK)
        self.assertTrue(r["stranded"])
        self.assertTrue(r["stacked"])

    def test_merged_into_a_stack_base_that_did_land_is_fine(self):
        r = mod.classify_merged_pr(self._pr("fix/3046-gp-layer-authz"),
                                   landed=True, trunk_branch=TRUNK)
        self.assertFalse(r["stranded"])
        self.assertTrue(r["stacked"])

    def test_ancestry_not_the_base_branch_is_the_authority(self):
        """A trunk-based PR that somehow is not an ancestor is still stranded."""
        r = mod.classify_merged_pr(self._pr("trunk"), landed=False,
                                   trunk_branch=TRUNK)
        self.assertTrue(r["stranded"])
        self.assertFalse(r["stacked"])


class ClassifyOpenStackTests(unittest.TestCase):
    def _pr(self, base: str):
        return {"number": 3176, "title": "t", "url": "u", "baseRefName": base}

    def test_trunk_based_open_pr_is_not_reported(self):
        self.assertIsNone(mod.classify_open_stack_pr(
            self._pr("trunk"), base_landed=False, base_exists=True,
            trunk_branch=TRUNK))

    def test_stacked_on_a_landed_base_needs_retarget(self):
        r = mod.classify_open_stack_pr(self._pr("feat/base"), base_landed=True,
                                       base_exists=True, trunk_branch=TRUNK)
        self.assertTrue(r["needs_retarget"])

    def test_stacked_on_a_deleted_base_needs_retarget(self):
        r = mod.classify_open_stack_pr(self._pr("feat/base"), base_landed=False,
                                       base_exists=False, trunk_branch=TRUNK)
        self.assertTrue(r["needs_retarget"])

    def test_stacked_on_a_live_unlanded_base_is_left_alone(self):
        r = mod.classify_open_stack_pr(self._pr("feat/base"), base_landed=False,
                                       base_exists=True, trunk_branch=TRUNK)
        self.assertFalse(r["needs_retarget"])


class EndToEndTests(unittest.TestCase):
    """The three real #3248 cases plus the three that did land."""

    FIXTURE = [
        {"number": 3119, "state": "MERGED", "mergedAt": "2026-08-03T00:00:00Z",
         "title": "", "url": "", "baseRefName": "fix/3046-gp-layer-authz",
         "mergeCommit": {"oid": "ab38e2749"}, "landed": True},
        {"number": 3116, "state": "MERGED", "mergedAt": "2026-08-03T00:00:00Z",
         "title": "", "url": "", "baseRefName": "feat/rast-003-raster-source-contract",
         "mergeCommit": {"oid": "31fb09f27"}, "landed": False},
        {"number": 3113, "state": "MERGED", "mergedAt": "2026-08-04T00:00:00Z",
         "title": "", "url": "", "baseRefName": "fix/2997-2998-entitlement-gates",
         "mergeCommit": {"oid": "f3889461d"}, "landed": False},
        {"number": 2974, "state": "MERGED", "mergedAt": "2026-07-23T00:00:00Z",
         "title": "", "url": "", "baseRefName": "ci/single-merge-authority",
         "mergeCommit": {"oid": "a5f21e8e0"}, "landed": True},
        {"number": 2836, "state": "MERGED", "mergedAt": "2026-07-15T00:00:00Z",
         "title": "", "url": "", "baseRefName": "feat/2667-computetiepoints",
         "mergeCommit": {"oid": "a7dfbaa17"}, "landed": True},
        {"number": 2835, "state": "MERGED", "mergedAt": "2026-07-15T00:00:00Z",
         "title": "", "url": "", "baseRefName": "feat/2661-tilecache-checkpoint",
         "mergeCommit": {"oid": "485952be5"}, "landed": False},
        {"number": 3266, "state": "MERGED", "mergedAt": "2026-08-16T00:00:00Z",
         "title": "", "url": "", "baseRefName": "train/batch/07656f408/1",
         "mergeCommit": {"oid": "07656f408"}, "landed": True},
    ]

    def _run(self, fixture, extra=None):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "prs.json"
            path.write_text(json.dumps(fixture), encoding="utf-8")
            out = Path(tmp) / "report.json"
            code = mod.main(["--fixture", str(path), "--json", str(out),
                             "--trunk-ref", "origin/trunk"] + (extra or []))
            return code, json.loads(out.read_text(encoding="utf-8"))

    def test_finds_exactly_the_three_stranded_prs_and_fails(self):
        code, report = self._run(self.FIXTURE)
        self.assertEqual(code, 1)
        self.assertEqual(sorted(r["number"] for r in report["stranded"]),
                         [2835, 3113, 3116])

    def test_warn_only_reports_but_exits_zero(self):
        code, report = self._run(self.FIXTURE, ["--warn-only"])
        self.assertEqual(code, 0)
        self.assertEqual(len(report["stranded"]), 3)

    def test_clean_history_passes(self):
        clean = [dict(pr, landed=True) for pr in self.FIXTURE]
        code, report = self._run(clean)
        self.assertEqual(code, 0)
        self.assertEqual(report["stranded"], [])

    def test_open_pr_on_a_landed_base_is_flagged_for_retarget(self):
        fixture = [dict(pr) for pr in self.FIXTURE] + [
            {"number": 4001, "state": "OPEN", "title": "", "url": "",
             "baseRefName": "feat/already-landed", "baseLanded": True,
             "baseExists": True},
            {"number": 4002, "state": "OPEN", "title": "", "url": "",
             "baseRefName": "trunk"},
        ]
        code, report = self._run(fixture)
        self.assertEqual(code, 1)
        self.assertEqual([r["number"] for r in report["needsRetarget"]], [4001])

    def test_report_includes_the_verification_command(self):
        _, report = self._run(self.FIXTURE, ["--warn-only"])
        text = mod.render_report(report)
        self.assertIn("git merge-base --is-ancestor 485952be5 origin/trunk", text)


if __name__ == "__main__":
    unittest.main(verbosity=2)
