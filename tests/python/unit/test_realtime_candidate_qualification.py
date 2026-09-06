import importlib.util
import json
import unittest
from copy import deepcopy
from datetime import datetime, timedelta, timezone
from pathlib import Path

SCRIPT = Path(__file__).parents[3] / "scripts/conformance/realtime/qualify_candidate.py"
SPEC = importlib.util.spec_from_file_location("qualify_candidate", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)

NOW = datetime(2026, 9, 2, 7, tzinfo=timezone.utc)
SERVER_SHA = "a" * 40
SERVER_IMAGE = "sha256:" + "1" * 64
SDK_SHA = "b" * 40


def expected():
    return {
        "serverRevision": SERVER_SHA,
        "serverImage": SERVER_IMAGE,
        "environment": "rc-2026.1",
        "sdkPackage": "@honua/sdk-js@0.2.0",
        "sdkRevision": SDK_SHA,
        "workflowRepository": "honua-io/honua-sdk-js",
        "workflowName": "Realtime Preview Qualification",
        "runId": "42",
        "runAttempt": "1",
        "artifactId": "99",
        "sourceArtifactUrl": "https://github.com/honua-io/honua-sdk-js/actions/runs/42/artifacts/99",
        "qualificationRunUrl": "https://github.com/honua-io/honua-server/actions/runs/100",
    }


def complete_evidence():
    rows = []
    for surface, transport, scenario in MODULE.PREVIEW_ROWS:
        rows.append({
            "surface": surface,
            "transport": transport,
            "scenario": scenario,
            "executed": True,
            "result": "passed",
            "assertions": [{"id": "live-assertion", "passed": True}],
            "serverRevision": SERVER_SHA,
            "serverImage": SERVER_IMAGE,
            "sdkRevision": SDK_SHA,
            "sdkPackage": "@honua/sdk-js@0.2.0",
            "environment": "rc-2026.1",
            "runId": "42",
            "runAttempt": 1,
            "artifactId": 99,
        })
        if scenario in {"token-expiry", "token-revocation", "tenant-isolation", "tenant-scope-change"}:
            rows[-1]["assertions"] = [
                {"id": name, "passed": True} for name in (
                    "no-cross-tenant-payload", "invalid-credentials-rejected",
                    "old-credential-terminated", "replacement-resume", "changed-scope-rejected",
                )
            ]
            rows[-1]["authorization"] = {
                "issuerFingerprint": "sha256:" + "2" * 64,
                "tenantIds": ["tenant-a", "tenant-b"],
                "mutationIds": ["a-before", "b-after"],
                "issuedAt": "2026-09-02T06:35:00Z",
                "expiresAt": "2026-09-02T06:36:00Z",
                "revokedAt": "2026-09-02T06:36:00Z",
                "terminatedAt": "2026-09-02T06:36:01Z",
                "enforcementBoundMilliseconds": 5000,
                "terminationReason": "unauthorized" if transport == "odata" else "authorization-ended",
                "observations": [
                    {"at": "2026-09-02T06:35:30Z", "raw": '{"objectId":"a-before"}'},
                    {"at": "2026-09-02T06:36:01Z", "raw": '{"code":"authorization-ended"}'},
                ],
            }
    return {
        "format": MODULE.EVIDENCE_FORMAT,
        "schemaVersion": 2,
        "lane": "live",
        "generatedAt": "2026-09-02T06:45:00Z",
        "candidate": {"environment": "rc-2026.1"},
        "server": {"revision": SERVER_SHA, "image": SERVER_IMAGE},
        "sdk": {"package": "@honua/sdk-js@0.2.0", "version": "0.2.0", "revision": SDK_SHA},
        "workflow": {
            "repository": "honua-io/honua-sdk-js",
            "name": "Realtime Preview Qualification",
            "runId": 42,
            "runAttempt": 1,
            "artifactId": 99,
            "artifactUrl": "https://github.com/honua-io/honua-sdk-js/actions/runs/42/artifacts/99",
            "conclusion": "success",
            "startedAt": "2026-09-02T06:30:00Z",
            "completedAt": "2026-09-02T06:50:00Z",
        },
        "rows": rows,
    }


def qualify(source):
    return MODULE.qualify(source, expected(), now=NOW, max_age=timedelta(hours=24))


class RealtimeCandidateQualificationTests(unittest.TestCase):
    def test_expiry_does_not_stand_in_for_revocation_or_scope_change(self):
        source = complete_evidence()
        source["rows"] = [row for row in source["rows"] if row["scenario"] not in {"token-revocation", "tenant-scope-change"}]
        result = qualify(source)
        self.assertEqual("rejected", result["status"])
        for transport in ("sse", "websocket", "odata"):
            for scenario in ("token-revocation", "tenant-scope-change"):
                row = next(row for row in result["rows"] if row["surface"] == "feature-stream"
                           and row["transport"] == transport and row["scenario"] == scenario)
                self.assertIn("does not contain", " ".join(row["reasons"]))

    def test_projected_green_authorization_without_raw_receipt_is_rejected(self):
        for field in ("issuerFingerprint", "tenantIds", "mutationIds", "issuedAt", "expiresAt", "observations"):
            with self.subTest(field=field):
                source = complete_evidence()
                target = next(row for row in source["rows"] if row["scenario"] == "token-expiry")
                del target["authorization"][field]
                self.assertEqual("rejected", qualify(source)["status"])

    def test_late_termination_and_missing_security_assertions_are_rejected(self):
        for change in ("late", "assertions", "after-only", "same-tenant", "revocation-time"):
            with self.subTest(change=change):
                source = complete_evidence()
                target = next(row for row in source["rows"] if row["scenario"] == "token-revocation")
                proof = target["authorization"]
                if change == "late":
                    proof["terminatedAt"] = "2026-09-02T06:36:06Z"
                elif change == "assertions":
                    target["assertions"] = [{"id": "live-assertion", "passed": True}]
                elif change == "after-only":
                    proof["observations"][0]["at"] = "2026-09-02T06:36:02Z"
                elif change == "same-tenant":
                    proof["tenantIds"] = ["tenant-a", "tenant-a"]
                else:
                    del proof["revokedAt"]
                self.assertEqual("rejected", qualify(source)["status"])

    def test_complete_exact_candidate_preview_matrix_qualifies(self):
        receipt = qualify(complete_evidence())
        self.assertEqual("qualified", receipt["status"])
        self.assertTrue(all(row["state"] == "qualified" for row in receipt["rows"]))
        self.assertFalse(receipt["graduationRowsRequired"])

    def test_fixture_only_receipt_rejects_every_row(self):
        source = complete_evidence()
        source["lane"] = "fixture"
        receipt = qualify(source)
        self.assertEqual("rejected", receipt["status"])
        self.assertIn("fixture or synthetic", " ".join(receipt["diagnostics"]))
        self.assertTrue(all(row["state"] == "rejected" for row in receipt["rows"]))

    def test_failed_and_unexecuted_rows_have_cell_specific_reasons(self):
        source = complete_evidence()
        source["rows"][0]["result"] = "failed"
        source["rows"][1]["executed"] = False
        receipt = qualify(source)
        self.assertIn("not 'passed'", " ".join(receipt["rows"][0]["reasons"]))
        self.assertIn("not executed live", " ".join(receipt["rows"][1]["reasons"]))

    def test_incomplete_receipt_cannot_qualify(self):
        source = complete_evidence()
        missing = source["rows"].pop()
        receipt = qualify(source)
        row = next(
            item for item in receipt["rows"]
            if item["surface"] == missing["surface"]
            and item["transport"] == missing["transport"]
            and item["scenario"] == missing["scenario"]
        )
        self.assertIn("does not contain", " ".join(row["reasons"]))

    def test_replayed_duplicate_row_is_rejected(self):
        source = complete_evidence()
        source["rows"].append(deepcopy(source["rows"][0]))
        receipt = qualify(source)
        self.assertIn("duplicate/replayed", " ".join(receipt["rows"][0]["reasons"]))

    def test_stale_and_predating_receipt_is_rejected(self):
        source = complete_evidence()
        source["generatedAt"] = "2026-08-30T00:00:00Z"
        receipt = qualify(source)
        text = " ".join(receipt["diagnostics"])
        self.assertIn("stale", text)
        self.assertIn("replayed evidence", text)

    def test_missing_post_candidate_digest_binding_fails_closed(self):
        source = complete_evidence()
        candidate = expected()
        candidate["serverImage"] = ""

        receipt = MODULE.qualify(source, candidate, now=NOW, max_age=timedelta(hours=24))

        self.assertEqual("rejected", receipt["status"])
        diagnostics = " ".join(receipt["diagnostics"])
        self.assertIn("candidate digest binding is unavailable", diagnostics)
        self.assertIn("post-candidate receipt required", diagnostics)
        self.assertEqual(SERVER_IMAGE, receipt["candidate"]["serverImage"])

        source["server"]["image"] = "honua/server:latest"
        receipt = MODULE.qualify(source, candidate, now=NOW, max_age=timedelta(hours=24))
        self.assertIn("immutable sha256 digest", " ".join(receipt["diagnostics"]))

    def test_all_candidate_identities_are_bound(self):
        mutations = (
            ("server", "revision", "c" * 40, "server revision"),
            ("server", "image", "honua/server:latest", "server image"),
            ("sdk", "revision", "d" * 40, "SDK revision"),
            ("workflow", "runAttempt", 2, "workflow run attempt"),
            ("workflow", "artifactUrl", "https://example.invalid/replayed", "source artifact URL"),
            ("candidate", "environment", "other", "candidate environment"),
        )
        for section, field, value, reason in mutations:
            with self.subTest(field=field):
                source = complete_evidence()
                source[section][field] = value
                self.assertIn(reason, " ".join(qualify(source)["diagnostics"]))

        row_mutations = (
            ("sdkPackage", "@honua/sdk-js@other", "exact SDK package"),
            ("environment", "other", "candidate environment"),
            ("artifactId", 100, "immutable source artifact"),
        )
        for field, value, reason in row_mutations:
            with self.subTest(row_field=field):
                source = complete_evidence()
                source["rows"][0][field] = value
                self.assertIn(reason, " ".join(qualify(source)["rows"][0]["reasons"]))

    def test_unrelated_alias_cannot_credit_a_required_cell(self):
        source = complete_evidence()
        source["rows"][0]["scenario"] = "snapshot-delta-contract"
        receipt = qualify(source)
        self.assertIn("unrelated scenario", " ".join(receipt["diagnostics"]))
        self.assertIn("does not contain", " ".join(receipt["rows"][0]["reasons"]))

    def test_legacy_failed_live_artifact_remains_negative(self):
        fixture = Path(__file__).parents[1] / "fixtures/realtime/run-32941656924-live.json"
        source = json.loads(fixture.read_text(encoding="utf-8"))
        receipt = qualify(source)
        self.assertEqual("rejected", receipt["status"])
        self.assertIn(MODULE.EVIDENCE_FORMAT, " ".join(receipt["diagnostics"]))
        self.assertTrue(all(row["state"] == "rejected" for row in receipt["rows"]))

    def test_required_workflow_cannot_omit_gate_or_retained_ledger(self):
        workflow = Path(__file__).parents[3] / ".github/workflows/realtime-preview-qualification.yml"
        text = workflow.read_text(encoding="utf-8")
        self.assertIn("artifact-ids: ${{ inputs.sdk_artifact_id }}", text)
        self.assertIn("release_bundle_token", text)
        self.assertIn("run.path !== expectedWorkflowPath", text)
        self.assertIn("artifact.name !== expectedArtifactName", text)
        self.assertIn(".github/workflows/realtime-live-conformance.yml", text)
        self.assertIn("realtime-cross-transport-conformance-${process.env.SDK_RUN_ID}", text)
        self.assertIn("--require-qualified", text)
        self.assertIn("if: always()", text)
        self.assertIn("retention-days: 180", text)
        self.assertIn("an absent digest binding is still a hard rejection", text)

    def test_release_bundle_requires_successful_realtime_execution(self):
        workflow = Path(__file__).parents[3] / ".github/workflows/release-bundle.yml"
        text = workflow.read_text(encoding="utf-8")
        self.assertIn("realtime-preview-qualification:", text)
        self.assertIn("needs.realtime-preview-qualification.result == 'success'", text)
        self.assertIn("candidate_image: ${{ needs.rc-image.outputs.image_digest }}", text)
        self.assertIn("honua-release#269", text)
        self.assertIn("realtime_sdk_artifact_id:", text)


if __name__ == "__main__":
    unittest.main()
