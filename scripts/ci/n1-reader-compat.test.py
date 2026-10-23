#!/usr/bin/env python3
"""Offline self-test for scripts/ci/n1-reader-compat.py (no Docker, no network)."""

from __future__ import annotations

import importlib.util
import sys
import unittest
from pathlib import Path

SPEC = importlib.util.spec_from_file_location("n1", Path(__file__).with_name("n1-reader-compat.py"))
n1 = importlib.util.module_from_spec(SPEC)
sys.modules["n1"] = n1
SPEC.loader.exec_module(n1)

# The exact refusal the honua-release gate-upgrade run 39675394479 recorded (#6477).
INCIDENT_LOG = (
    "fail: Honua.Server[0]\n      schema_reader_incompatible: this server cannot verify compatibility with "
    "applied migration(s): Honua.Server.Migrations.126_AddStudioContentHashInput.sql, "
    "Honua.Server.Migrations.127_CompleteSensorThingsSensing.sql, "
    "Honua.Server.Migrations.128_AddNetworkTopologyPromotionRequestHash.sql. Start a compatible server or "
    "restore the database backup matching this revision. Application rollback does not reverse database "
    "migrations.\n"
)
DECLARED_LOG = (
    "schema_reader_incompatible: this server cannot verify compatibility with applied migration(s): "
    "Honua.Server.Migrations.140_DropThing.sql. Start a compatible server or restore the database backup "
    "matching this revision. Application rollback does not reverse database migrations. "
    "Forward-compatibility declaration(s) refused: Honua.Server.Migrations.140_DropThing.sql "
    "(declared contract, not expand).\n"
)
INCIDENT = ["126_AddStudioContentHashInput.sql", "127_CompleteSensorThingsSensing.sql",
            "128_AddNetworkTopologyPromotionRequestHash.sql"]


def evidence(**overrides) -> "n1.Evidence":
    values = dict(journal_delta=["140_AddThing.sql"], classifications={"140_AddThing.sql": "expand"},
                  pr_added={"140_AddThing.sql"}, prior_has_reader_declarations=True, refusal=None,
                  ready=True, serve_ok=True)
    values.update(overrides)
    return n1.Evidence(**values)


class ParseRefusal(unittest.TestCase):
    def test_incident_message(self) -> None:
        refusal = n1.parse_refusal(INCIDENT_LOG)
        self.assertEqual([n1.basename_of(s) for s in refusal["scripts"]], INCIDENT)
        self.assertEqual(refusal["declaration_refusals"], {})

    def test_declaration_refusals(self) -> None:
        refusal = n1.parse_refusal(DECLARED_LOG)
        self.assertEqual(refusal["scripts"], ["Honua.Server.Migrations.140_DropThing.sql"])
        self.assertEqual(refusal["declaration_refusals"],
                         {"Honua.Server.Migrations.140_DropThing.sql": "declared contract, not expand"})

    def test_no_refusal(self) -> None:
        self.assertIsNone(n1.parse_refusal("Now listening on: http://[::]:8080\n"))


class Classify(unittest.TestCase):
    def test_compatible(self) -> None:
        self.assertEqual(n1.classify(evidence())[0], "compatible")

    def test_ready_but_serve_failed(self) -> None:
        self.assertEqual(n1.classify(evidence(serve_ok=False))[0], "serve-check-failed")

    def test_neither_ready_nor_typed(self) -> None:
        self.assertEqual(n1.classify(evidence(ready=False, serve_ok=False))[0], "refusal-untyped")

    def test_incident_against_old_prior_is_known_blocked_not_pass(self) -> None:
        outcome, _ = n1.classify(evidence(
            journal_delta=INCIDENT, classifications={s: "undeclared" for s in INCIDENT}, pr_added=set(),
            prior_has_reader_declarations=False, refusal=n1.parse_refusal(INCIDENT_LOG), ready=False,
            serve_ok=False))
        self.assertEqual(outcome, "prior-lacks-reader-declarations")
        self.assertEqual(n1.exit_code_for(outcome), 3)
        self.assertNotIn(outcome, n1.PASSING)

    def test_expand_refused_by_declaring_prior_is_regression(self) -> None:
        outcome, _ = n1.classify(evidence(
            journal_delta=INCIDENT, classifications={s: "expand" for s in INCIDENT},
            refusal=n1.parse_refusal(INCIDENT_LOG), ready=False, serve_ok=False))
        self.assertEqual(outcome, "reader-incompatible-regression")
        self.assertEqual(n1.exit_code_for(outcome), 1)

    def test_declared_contract_refusal_documented(self) -> None:
        script = "140_DropThing.sql"
        outcome, _ = n1.classify(evidence(
            journal_delta=[script], classifications={script: "contract"}, pr_added={script},
            refusal=n1.parse_refusal(DECLARED_LOG), ready=False, serve_ok=False,
            script_text={script: "-- honua:compatibility-review reason=drop\nDROP TABLE x;\n"},
            rollback_doc="Rollback boundary: 140_DropThing requires a restore."))
        self.assertEqual(outcome, "expected-refusal")
        self.assertEqual(n1.exit_code_for(outcome), 0)

    def test_declared_contract_refusal_without_review_or_doc(self) -> None:
        script = "140_DropThing.sql"
        outcome, why = n1.classify(evidence(
            journal_delta=[script], classifications={script: "contract"}, pr_added={script},
            refusal=n1.parse_refusal(DECLARED_LOG), ready=False, serve_ok=False,
            script_text={script: "DROP TABLE x;\n"}, rollback_doc=""))
        self.assertEqual(outcome, "expected-refusal-undocumented")
        self.assertIn("compatibility-review", why)
        self.assertIn("upgrade-and-rollback.md", why)

    def test_reader_incompatible_served_is_failure(self) -> None:
        outcome, _ = n1.classify(evidence(classifications={"140_AddThing.sql": "reader-incompatible"}))
        self.assertEqual(outcome, "expected-refusal-missing")

    def test_prior_that_changes_the_journal_is_not_compatible(self) -> None:
        outcome, why = n1.classify(evidence(journal_changed=True))
        self.assertEqual(outcome, "prior-changed-journal")
        self.assertNotIn(outcome, n1.PASSING)
        self.assertEqual(n1.exit_code_for(outcome), 1)
        self.assertIn("journal_after != journal", why)

    def test_journal_change_does_not_turn_unguarded_prior_into_a_pass(self) -> None:
        outcome, _ = n1.classify(evidence(
            journal_delta=["115_AddDropImportStagingTable.sql", "135_RecordMigrationReaderCompatibility.sql"],
            classifications={"115_AddDropImportStagingTable.sql": "contract",
                             "135_RecordMigrationReaderCompatibility.sql": "expand"},
            prior_has_reader_declarations=False, prior_has_reader_guard=False,
            journal_changed=True, serve_ok=True))
        self.assertNotIn(outcome, n1.PASSING)
        self.assertNotEqual(outcome, "compatible")
        self.assertNotEqual(outcome, "prior-unguarded")
        self.assertEqual(n1.exit_code_for(outcome), 1)

    def test_unguarded_prior_that_serves_is_known_blocked_not_pass(self) -> None:
        # What the honua-2026.1 release image (schema 106, before #5333) does on schema 135.
        outcome, why = n1.classify(evidence(
            journal_delta=["115_AddDropImportStagingTable.sql", "135_RecordMigrationReaderCompatibility.sql"],
            classifications={"115_AddDropImportStagingTable.sql": "contract",
                             "135_RecordMigrationReaderCompatibility.sql": "expand"},
            prior_has_reader_declarations=False, prior_has_reader_guard=False))
        self.assertEqual(outcome, "prior-unguarded")
        self.assertIn("115_AddDropImportStagingTable.sql", why)
        self.assertEqual(n1.exit_code_for(outcome), 3)

    def test_guarded_prior_serving_contract_is_failure(self) -> None:
        outcome, _ = n1.classify(evidence(classifications={"140_AddThing.sql": "contract"},
                                          prior_has_reader_declarations=False))
        self.assertEqual(outcome, "expected-refusal-missing")

    def test_unknown_outcomes_fail(self) -> None:
        for outcome in ("prior-unresolved", "infrastructure-error", "candidate-migration-failed"):
            self.assertEqual(n1.exit_code_for(outcome), 1)


class Applies(unittest.TestCase):
    def test_paths(self) -> None:
        for path in ("src/Honua.Server/Migrations/136_AddX.sql",
                     "src/Honua.Db/Postgres/Migrations/008_AddRaster.sql",
                     "src/Honua.Db/Postgres/Features/Infrastructure/Migrations/PostgresCoreSchemaGuard.cs",
                     "src/Honua.Core/Features/Infrastructure/Migrations/MigrationReaderCompatibility.cs",
                     "certification/schema-migration-hashes.json",
                     "scripts/ci/n1-reader-compat.py"):
            self.assertTrue(n1.path_applies(path), path)
        for path in ("src/Honua.Server/Program.cs", "docs/guides/deploy/upgrade-and-rollback.md",
                     "src/Honua.Server/Features/Admin/LayerPublishingEndpoints.cs"):
            self.assertFalse(n1.path_applies(path), path)


# The honua-server entry of the honua-2026.1 finalized-manifest.yaml asset (honua-io/honua-release).
RELEASE_MANIFEST = """snapshotKind: candidate-manifest-snapshot
components:
  honua-server:
    version: pre-release
    sha: f897700159e2791c9468c6ca85bb4e2a3a8d8433
    image: ghcr.io/honua-io/honua-server:nightly-aot-f897700
    digest: sha256:78e3088d64d832d3e2752c87d80bfcad201b414f4525989ca5d9a242cd5fee8a
    awsLambdaImage: ghcr.io/honua-io/honua-server:nightly-lambda-aot-f897700-arm64
    awsLambdaDigest: sha256:d97baf44b17ba5b9537320281f721252ed12f1fb43bee001011d720f3ae7d622
  honua-console:
    image: ghcr.io/honua-io/honua-console:candidate-2cd3a73a6b10
    digest: sha256:0000000000000000000000000000000000000000000000000000000000000000
"""


class ReleasePin(unittest.TestCase):
    def test_server_pin_is_read_from_the_server_block_only(self) -> None:
        self.assertEqual(n1.parse_release_server_pin(RELEASE_MANIFEST), (
            "ghcr.io/honua-io/honua-server:nightly-aot-f897700",
            "sha256:78e3088d64d832d3e2752c87d80bfcad201b414f4525989ca5d9a242cd5fee8a"))

    def test_crlf_manifest(self) -> None:
        self.assertEqual(n1.parse_release_server_pin(RELEASE_MANIFEST.replace("\n", "\r\n"))[1],
                         "sha256:78e3088d64d832d3e2752c87d80bfcad201b414f4525989ca5d9a242cd5fee8a")

    def test_missing_server_pin_raises(self) -> None:
        with self.assertRaises(ValueError):
            n1.parse_release_server_pin("components:\n  honua-console:\n    image: x\n")
        with self.assertRaises(ValueError):
            n1.parse_release_server_pin("components:\n  honua-server:\n    image: x\n")

    def test_image_repo(self) -> None:
        self.assertEqual(n1.image_repo("ghcr.io/honua-io/honua-server:nightly-aot-f897700"),
                         "ghcr.io/honua-io/honua-server")
        self.assertEqual(n1.image_repo("localhost:5000/honua-server@sha256:ab"), "localhost:5000/honua-server")
        self.assertEqual(n1.image_repo("localhost:5000/honua-server"), "localhost:5000/honua-server")

    def test_moved_tag_is_reported(self) -> None:
        text = n1.describe_release({"release_tag": "honua-2026.1", "release_image": "img:tag",
                                    "release_digest": "sha256:aa", "tag_moved_since_release": True,
                                    "tag_digest_now": "sha256:bb"})
        self.assertIn("now resolves to `sha256:bb`", text)
        self.assertEqual(n1.describe_release({}), "")


class Summary(unittest.TestCase):
    def test_table(self) -> None:
        text = n1.render_summary({
            "outcome": "prior-lacks-reader-declarations", "explanation": "x",
            "priorSource": "nightly-scaffold", "prior": {"reference": "ghcr.io/honua-io/honua-server@sha256:ab"},
            "journal_delta": INCIDENT, "classifications": {INCIDENT[0]: "expand"}, "pr_added": [],
            "refusal": n1.parse_refusal(INCIDENT_LOG)})
        self.assertIn("| `126_AddStudioContentHashInput.sql` | `expand` | no | yes |", text)
        self.assertIn("Prior source: `nightly-scaffold`", text)


if __name__ == "__main__":
    unittest.main()
