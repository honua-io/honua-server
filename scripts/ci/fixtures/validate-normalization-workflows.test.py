#!/usr/bin/env python3
"""Negative tests for the normalization security contract validator.

Each case mutates a copy of the real workflow the way a bypass attempt would and
asserts that `validate` rejects it. A validator that only passes on the current
tree is not a control; these cases pin the shapes it must keep catching.
"""

from __future__ import annotations

import unittest
from pathlib import Path

import importlib.util

ROOT = Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location(
    "validate_normalization_workflows",
    ROOT / "scripts/ci/fixtures/validate-normalization-workflows.py",
)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)

SOURCES = {
    "producer": (ROOT / ".github/workflows/normalize-derived-artifacts.yml").read_text(encoding="utf-8"),
    "consumer": (ROOT / ".github/workflows/normalize-derived-artifacts-consumer.yml").read_text(encoding="utf-8"),
    "router": (ROOT / "scripts/ci/validate-ci-router.sh").read_text(encoding="utf-8"),
    "contract_validator": (ROOT / "scripts/ci/validate-normalization-contract.sh").read_text(encoding="utf-8"),
    "mutation_module": (ROOT / "scripts/ci/normalization-mutation.js").read_text(encoding="utf-8"),
}


def run(**overrides: str) -> str:
    sources = dict(SOURCES)
    sources.update(overrides)
    return MODULE.validate(**sources)


class NormalizationContractTests(unittest.TestCase):
    def test_current_tree_passes(self) -> None:
        self.assertIn(run(), {"observe", "enforce"})

    def test_declared_enforce_mode_passes(self) -> None:
        consumer = SOURCES["consumer"].replace(
            "  NORMALIZATION_MODE: observe", "  NORMALIZATION_MODE: enforce")
        self.assertEqual(run(consumer=consumer), "enforce")

    def test_job_level_mode_override_is_rejected(self) -> None:
        # Shadows the workflow value at runtime while the workflow line still
        # reads `observe`.
        consumer = SOURCES["consumer"].replace(
            "    env:\n      NORMALIZATION_CREDENTIAL_PRESENT:",
            "    env:\n      NORMALIZATION_MODE: enforce\n      NORMALIZATION_CREDENTIAL_PRESENT:",
            1,
        )
        with self.assertRaisesRegex(AssertionError, "exactly once"):
            run(consumer=consumer)

    def test_step_level_mode_override_is_rejected(self) -> None:
        consumer = SOURCES["consumer"].replace(
            "        env:\n          CHANGED_PATHS:",
            "        env:\n          NORMALIZATION_MODE: enforce\n          CHANGED_PATHS:",
            1,
        )
        with self.assertRaisesRegex(AssertionError, "exactly once"):
            run(consumer=consumer)

    def test_unknown_mode_is_rejected(self) -> None:
        consumer = SOURCES["consumer"].replace(
            "  NORMALIZATION_MODE: observe", "  NORMALIZATION_MODE: enforce-later")
        with self.assertRaisesRegex(AssertionError, "observe or enforce"):
            run(consumer=consumer)

    def test_index_form_secret_is_rejected(self) -> None:
        consumer = SOURCES["consumer"].replace(
            "          app-id: ${{ secrets.NORMALIZATION_APP_ID }}",
            "          app-id: ${{ secrets['MERGE_TRAIN_TOKEN'] }}",
        )
        with self.assertRaisesRegex(AssertionError, "unexpected secret expression"):
            run(consumer=consumer)

    def test_tojson_secrets_is_rejected(self) -> None:
        consumer = SOURCES["consumer"].replace(
            "          app-id: ${{ secrets.NORMALIZATION_APP_ID }}",
            "          app-id: ${{ toJSON(secrets) }}",
        )
        with self.assertRaisesRegex(AssertionError, "unexpected secret expression"):
            run(consumer=consumer)

    def test_lowercase_secret_name_is_rejected(self) -> None:
        consumer = SOURCES["consumer"].replace(
            "          app-id: ${{ secrets.NORMALIZATION_APP_ID }}",
            "          app-id: ${{ secrets.merge_train_token }}",
        )
        with self.assertRaisesRegex(AssertionError, "unexpected secret expression"):
            run(consumer=consumer)

    def test_secret_outside_an_expression_is_rejected(self) -> None:
        consumer = SOURCES["consumer"].replace(
            "            const fs = require('fs');\n            const {\n              applyNormalizationMutation,",
            "            const fs = require('fs');\n            core.info(secrets.MERGE_TRAIN_TOKEN);\n            const {\n              applyNormalizationMutation,",
        )
        with self.assertRaisesRegex(AssertionError, "only inside allowlisted expressions"):
            run(consumer=consumer)

    def test_write_permission_is_rejected(self) -> None:
        consumer = SOURCES["consumer"].replace("  contents: read", "  contents: write", 1)
        with self.assertRaisesRegex(AssertionError, "never gain a write permission"):
            run(consumer=consumer)

    def test_missing_credential_gate_is_rejected(self) -> None:
        consumer = SOURCES["consumer"].replace(
            "          env.NORMALIZATION_CREDENTIAL_PRESENT == 'true' &&\n"
            "          steps.artifact.outputs.same_repository == 'true' &&\n"
            "          steps.compare.outputs.change_count != '0'\n"
            "        uses: actions/github-script@v9",
            "          steps.artifact.outputs.same_repository == 'true' &&\n"
            "          steps.compare.outputs.change_count != '0'\n"
            "        uses: actions/github-script@v9",
        )
        with self.assertRaisesRegex(AssertionError, "NORMALIZATION_CREDENTIAL_PRESENT"):
            run(consumer=consumer)

    def test_github_token_fallback_is_rejected(self) -> None:
        consumer = SOURCES["consumer"].replace(
            "github-token: ${{ steps.app-token.outputs.token }}",
            "github-token: ${{ steps.app-token.outputs.token || secrets.GITHUB_TOKEN }}",
        )
        with self.assertRaisesRegex(AssertionError, "unexpected secret expression"):
            run(consumer=consumer)

    def test_floating_mint_tag_is_rejected(self) -> None:
        consumer = SOURCES["consumer"].replace(
            "uses: actions/create-github-app-token@fee1f7d63c2ff003460e3d139729b119787bc349",
            "uses: actions/create-github-app-token@v2",
        )
        with self.assertRaisesRegex(AssertionError, "pin actions/create-github-app-token"):
            run(consumer=consumer)

    def test_inline_git_object_write_is_rejected(self) -> None:
        consumer = SOURCES["consumer"].replace(
            "            const result = await applyNormalizationMutation({",
            "            await github.rest.git.createBlob({});\n"
            "            const result = await applyNormalizationMutation({",
        )
        with self.assertRaisesRegex(AssertionError, "belongs in the tested module"):
            run(consumer=consumer)

    def test_forced_ref_update_is_rejected(self) -> None:
        mutation_module = SOURCES["mutation_module"].replace("force: false,", "force: true,")
        with self.assertRaisesRegex(AssertionError, "never force"):
            run(mutation_module=mutation_module)

    def test_missing_compare_and_swap_is_rejected(self) -> None:
        mutation_module = SOURCES["mutation_module"].replace("beforeOid: sourceSha", "beforeOid: null")
        with self.assertRaisesRegex(AssertionError, "compare-and-swap"):
            run(mutation_module=mutation_module)

    def test_missing_ref_readback_is_rejected(self) -> None:
        mutation_module = SOURCES["mutation_module"].replace(
            "normalization ref verification failed", "normalization ref probably fine")
        with self.assertRaisesRegex(AssertionError, "read the ref back"):
            run(mutation_module=mutation_module)

    def test_second_allowlist_copy_is_rejected(self) -> None:
        consumer = SOURCES["consumer"].replace(
            "              allowedPaths: plan.outputs.map(output => output.path),",
            "              allowedPaths: ['docs/gis/data/feature-catalog.json'],",
        )
        with self.assertRaisesRegex(AssertionError, "validated plan"):
            run(consumer=consumer)

    def test_mint_step_must_precede_mutation(self) -> None:
        consumer = SOURCES["consumer"].replace(
            "      - name: Mint scoped normalization credential",
            "      - name: Mint scoped normalization credential renamed",
        )
        with self.assertRaisesRegex(AssertionError, "Mint scoped normalization credential"):
            run(consumer=consumer)


if __name__ == "__main__":
    unittest.main(verbosity=1)
