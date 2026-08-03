"""Proofs for the Admin OpenAPI breaking-change acknowledgement policy."""

from __future__ import annotations

import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
POLICY_SPEC = importlib.util.spec_from_file_location(
    "openapi_breaking_change_policy",
    ROOT / "scripts/ci/openapi-breaking-change-policy.py",
)
POLICY = importlib.util.module_from_spec(POLICY_SPEC)
assert POLICY_SPEC and POLICY_SPEC.loader
sys.modules[POLICY_SPEC.name] = POLICY
POLICY_SPEC.loader.exec_module(POLICY)


class ResolvePolicyTests(unittest.TestCase):
    def test_ResolvePolicy_NoOverrideOrMarker_DisallowsBreakingChanges(self):
        decision = POLICY.resolve_policy("false", "## Breaking Changes\nNone")

        self.assertFalse(decision.allow_breaking_changes)
        self.assertEqual(decision.source, "none")

    def test_ResolvePolicy_CheckedPrMarker_AllowsOnlyThisPr(self):
        decision = POLICY.resolve_policy(
            "false",
            "- [X] `OPENAPI_BREAKING_CHANGE_APPROVED` — migration guide updated",
        )

        self.assertTrue(decision.allow_breaking_changes)
        self.assertEqual(
            decision.source,
            "pull-request marker OPENAPI_BREAKING_CHANGE_APPROVED",
        )

    def test_ResolvePolicy_UncheckedPrMarker_DoesNotAllowBreakingChanges(self):
        decision = POLICY.resolve_policy(
            "false",
            "- [ ] `OPENAPI_BREAKING_CHANGE_APPROVED` — migration guide updated",
        )

        self.assertFalse(decision.allow_breaking_changes)

    def test_ResolvePolicy_NonRenderedTaskMarkerSpacing_DoesNotAllowBreakingChanges(self):
        bodies = {
            "no whitespace after list marker": (
                "-[x] `OPENAPI_BREAKING_CHANGE_APPROVED`"
            ),
            "no whitespace after checkbox": (
                "- [x]`OPENAPI_BREAKING_CHANGE_APPROVED`"
            ),
            "no task-list whitespace": (
                "-[x]`OPENAPI_BREAKING_CHANGE_APPROVED`"
            ),
        }

        for scenario, body in bodies.items():
            with self.subTest(scenario=scenario):
                decision = POLICY.resolve_policy("false", body)

                self.assertFalse(decision.allow_breaking_changes)

    def test_ResolvePolicy_MarkerInNonRenderedMarkdown_DoesNotAllowBreakingChanges(self):
        marker = "- [x] `OPENAPI_BREAKING_CHANGE_APPROVED` - example only"
        bodies = {
            "backtick fence": f"```markdown\n{marker}\n```",
            "tilde fence": f"~~~markdown\n{marker}\n~~~",
            "backtick fence nested in list": f"- ```markdown\n  {marker}\n  ```",
            "tilde fence nested in ordered list": f"1. ~~~markdown\n   {marker}\n   ~~~",
            "unclosed fence": f"```markdown\n{marker}",
            "single-line HTML comment": f"<!-- {marker} -->",
            "multiline HTML comment": f"<!--\n{marker}\n-->",
            "unclosed HTML comment": f"<!--\n{marker}",
            "raw pre block": f"<pre>\n{marker}\n</pre>",
            "raw uppercase pre block": f"<PRE class=example>\n{marker}\n</PRE>",
            "unclosed raw script block": f"<script>\n{marker}",
            "raw block-level HTML": f"<div>\n{marker}\n</div>",
            "raw custom-element HTML": f"<honua-example>\n{marker}\n</honua-example>",
            "raw processing instruction": f"<?xml\n{marker}\n?>",
            "raw declaration": f"<!DOCTYPE\n{marker}\n>",
            "raw CDATA section": f"<![CDATA[\n{marker}\n]]>",
            "raw pre block nested in list": f"- <pre>\n  {marker}\n  </pre>",
            "raw div block nested in ordered list": f"1. <div>\n   {marker}\n   </div>",
            "raw declaration nested in list": f"- <!DOCTYPE\n  {marker}\n  >",
            "indented code": f"    {marker}",
        }

        for scenario, body in bodies.items():
            with self.subTest(scenario=scenario):
                decision = POLICY.resolve_policy("false", body)

                self.assertFalse(decision.allow_breaking_changes)

    def test_ResolvePolicy_RenderedMarkerAfterNonRenderedExample_AllowsBreakingChanges(self):
        decision = POLICY.resolve_policy(
            "false",
            "```markdown\n"
            "- [x] `OPENAPI_BREAKING_CHANGE_APPROVED` - example only\n"
            "```\n"
            "<!-- another non-approval example -->\n"
            "   - [x] `OPENAPI_BREAKING_CHANGE_APPROVED` - reviewed approval",
        )

        self.assertTrue(decision.allow_breaking_changes)
        self.assertEqual(
            decision.source,
            "pull-request marker OPENAPI_BREAKING_CHANGE_APPROVED",
        )

    def test_ResolvePolicy_RenderedMarkerAfterListNestedFence_AllowsBreakingChanges(self):
        marker = "- [x] `OPENAPI_BREAKING_CHANGE_APPROVED` - reviewed approval"
        decision = POLICY.resolve_policy(
            "false",
            f"- ```markdown\n  {marker} - example only\n  ```\n\n{marker}",
        )

        self.assertTrue(decision.allow_breaking_changes)
        self.assertEqual(
            decision.source,
            "pull-request marker OPENAPI_BREAKING_CHANGE_APPROVED",
        )

    def test_ResolvePolicy_RenderedMarkerAfterRawHtml_AllowsBreakingChanges(self):
        marker = "- [x] `OPENAPI_BREAKING_CHANGE_APPROVED` - reviewed approval"
        bodies = {
            "closed raw container": f"<pre>example</pre>\n{marker}",
            "blank-terminated HTML block": f"<div>\nexample\n</div>\n\n{marker}",
            "closed processing instruction": f"<?xml?>\n{marker}",
            "closed declaration": f"<!DOCTYPE html>\n{marker}",
            "closed CDATA section": f"<![CDATA[example]]>\n{marker}",
            "closed raw container nested in list": f"- <pre>example</pre>\n{marker}",
            "blank-terminated nested HTML block": f"1. <div>\n   example\n   </div>\n\n{marker}",
        }

        for scenario, body in bodies.items():
            with self.subTest(scenario=scenario):
                decision = POLICY.resolve_policy("false", body)

                self.assertTrue(decision.allow_breaking_changes)

    def test_ResolvePolicy_PrePublicationRepositoryOverride_TakesPrecedence(self):
        decision = POLICY.resolve_policy(
            "true",
            "- [x] `OPENAPI_BREAKING_CHANGE_APPROVED` — migration guide updated",
        )

        self.assertTrue(decision.allow_breaking_changes)
        self.assertEqual(
            decision.source,
            "repository variable OPENAPI_ALLOW_BREAKING_CHANGES",
        )

    def test_ParseRepositoryOverride_InvalidValue_FailsClosed(self):
        with self.assertRaisesRegex(ValueError, "must be a boolean"):
            POLICY.resolve_policy("tru", "")


@unittest.skipUnless(
    os.name != "nt" and shutil.which("bash"),
    "a native bash environment is required for the shell validator",
)
class SuppressedFindingVisibilityTests(unittest.TestCase):
    """A successful suppression must remain loud in the PR's Actions UI."""

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.repo = Path(self.directory.name)

        script_target = self.repo / "scripts/ci/validate-openapi-contracts.sh"
        resolver_target = self.repo / "scripts/ci/lib/python-resolve.sh"
        script_target.parent.mkdir(parents=True)
        resolver_target.parent.mkdir(parents=True)
        shutil.copy2(ROOT / "scripts/ci/validate-openapi-contracts.sh", script_target)
        shutil.copy2(ROOT / "scripts/ci/lib/python-resolve.sh", resolver_target)

        specs_target = self.repo / "docs/developer/api-specs"
        specs_target.mkdir(parents=True)
        for name in (
            "admin-api.json",
            "ogc-api-features.json",
            "ogc-api-tiles.json",
        ):
            shutil.copy2(ROOT / "docs/developer/api-specs" / name, specs_target / name)

        admin_path = specs_target / "admin-api.json"
        current_admin = json.loads(admin_path.read_text(encoding="utf-8"))
        baseline_admin = json.loads(json.dumps(current_admin))
        baseline_admin["paths"]["/__breaking-change-visibility-probe"] = {
            "get": {"responses": {"200": {"description": "probe"}}}
        }
        admin_path.write_text(
            json.dumps(baseline_admin, separators=(",", ":")),
            encoding="utf-8",
        )

        self._git("init")
        self._git("config", "user.name", "OpenAPI Policy Test")
        self._git("config", "user.email", "openapi-policy-test@example.invalid")
        self._git("add", ".")
        self._git("commit", "-m", "baseline")

        admin_path.write_text(json.dumps(current_admin), encoding="utf-8")
        self.summary_path = self.repo / "step-summary.md"

    def _git(self, *args: str) -> None:
        subprocess.run(
            ["git", *args],
            cwd=self.repo,
            check=True,
            capture_output=True,
            text=True,
        )

    def _run_validator(self, allow_breaking: bool) -> subprocess.CompletedProcess[str]:
        environment = os.environ.copy()
        environment.update(
            {
                "OPENAPI_BASE_REF": "HEAD",
                "OPENAPI_ALLOW_BREAKING_CHANGES": (
                    "true" if allow_breaking else "false"
                ),
                "OPENAPI_BREAKING_CHANGE_SOURCE": (
                    "pull-request marker OPENAPI_BREAKING_CHANGE_APPROVED"
                ),
                "GITHUB_ACTIONS": "true",
                "GITHUB_STEP_SUMMARY": str(self.summary_path),
            }
        )
        return subprocess.run(
            [shutil.which("bash") or "bash", "scripts/ci/validate-openapi-contracts.sh"],
            cwd=self.repo,
            env=environment,
            check=False,
            capture_output=True,
            text=True,
        )

    def test_Validator_UnacknowledgedBreakingChange_Fails(self):
        result = self._run_validator(allow_breaking=False)

        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("Path '/__breaking-change-visibility-probe' was removed", result.stdout)
        self.assertNotIn("::warning title=OpenAPI breaking-change suppression", result.stdout)

    def test_Validator_AcknowledgedBreakingChange_AnnotatesAndSummarizes(self):
        result = self._run_validator(allow_breaking=True)

        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn(
            "::warning title=OpenAPI breaking-change suppression active::",
            result.stdout,
        )
        self.assertIn("pull-request marker OPENAPI_BREAKING_CHANGE_APPROVED", result.stdout)
        summary = self.summary_path.read_text(encoding="utf-8")
        self.assertIn("Admin OpenAPI breaking changes acknowledged", summary)
        self.assertIn("/__breaking-change-visibility-probe", summary)


if __name__ == "__main__":
    unittest.main()
