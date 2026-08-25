#!/usr/bin/env python3
"""Failure-injection tests for the NuGet cache policy validator."""

from __future__ import annotations

import importlib.util
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location(
    "validate_dotnet_cache_policy",
    ROOT / "scripts/ci/fixtures/validate-dotnet-cache-policy.py",
)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)

ACTION = (ROOT / ".github/actions/setup-dotnet-ci/action.yml").read_text(encoding="utf-8")
SDK_GATE = (ROOT / ".github/workflows/reusable-sdk-pr-gate.yml").read_text(encoding="utf-8")


class DotnetCachePolicyTests(unittest.TestCase):
    def test_current_contract_passes(self) -> None:
        MODULE.validate_action(ACTION)
        MODULE.validate_reusable_sdk_gate(SDK_GATE)

    def test_central_package_versions_are_part_of_key(self) -> None:
        candidate = ACTION.replace(", '**/*.props'", "", 2)
        with self.assertRaisesRegex(MODULE.ContractError, "dependency input.*props"):
            MODULE.validate_action(candidate)

    def test_pull_request_write_guard_is_required(self) -> None:
        candidate = ACTION.replace(
            '        if [[ "$EVENT_NAME" == "pull_request" ||\n',
            '        if [[ "$EVENT_NAME" == "workflow_run" ||\n',
            1,
        )
        with self.assertRaisesRegex(MODULE.ContractError, "missing guard"):
            MODULE.validate_action(candidate)

    def test_required_gate_fixture_invocation_is_pinned(self) -> None:
        candidate = ACTION.replace(
            "scripts/ci/fixtures/validate-dotnet-cache-policy.test.py",
            "scripts/ci/fixtures/validate-dotnet-cache-policy.py",
            1,
        )
        with self.assertRaisesRegex(MODULE.ContractError, "failure-injection tests"):
            MODULE.validate_action(candidate)

    def test_restore_only_step_cannot_become_writable(self) -> None:
        candidate = ACTION.replace("uses: actions/cache/restore@v5", "uses: actions/cache@v5", 1)
        with self.assertRaisesRegex(MODULE.ContractError, "must use cache/restore"):
            MODULE.validate_action(candidate)

    def test_one_off_run_identifier_is_rejected(self) -> None:
        candidate = ACTION.replace("-nuget-v2-${{ hashFiles", "-nuget-v2-${{ github.run_id }}-${{ hashFiles", 2)
        with self.assertRaisesRegex(MODULE.ContractError, "one-off component"):
            MODULE.validate_action(candidate)

    def test_sdk_pr_gate_cannot_save_nuget_cache(self) -> None:
        candidate = SDK_GATE.replace("actions/cache/restore@v6", "actions/cache@v6", 1)
        with self.assertRaisesRegex(MODULE.ContractError, "must be restore-only"):
            MODULE.validate_reusable_sdk_gate(candidate)


if __name__ == "__main__":
    unittest.main(verbosity=2)
