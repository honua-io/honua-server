#!/usr/bin/env python3
"""Unit tests for scripts/ci/emit_rc2_rigor_proof.py."""

from __future__ import annotations

import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

SCRIPT_DIR = Path(__file__).resolve().parent
if str(SCRIPT_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPT_DIR))

import emit_rc2_rigor_proof as emitter  # noqa: E402

SHA = "d" * 40
INDEX_DIGEST = "sha256:" + "a" * 64
LOCK_DIGEST = "sha256:" + "b" * 64
AMD64_DIGEST = "sha256:" + "1" * 64
ARM64_DIGEST = "sha256:" + "2" * 64
OBSERVED = "2026-11-26T01:45:00Z"


def _passing_parity_scorecard() -> dict:
    probe_names = sorted({name for names in emitter.AOT_PROBE_FLAG_MAP.values() for name in names})
    return {
        "summary": {"passed": True, "reflection_error_count": 0},
        "probes": [{"name": name, "passed": True} for name in probe_names],
    }


def _passing_soak_scorecard() -> dict:
    return {
        "summary": {
            "passed": True,
            "elapsed_s": 601.2,
            "total_requests": 15_000,
            "error_count": 0,
            "http_5xx_count": 0,
            "latency_ms": {"p50": 11.2, "p95": 38.4, "p99": 79.1},
            "rss": {"warmup_mean_mb": 160.0, "tail_mean_mb": 172.0, "peak_mb": 195.0},
            "per_route": {
                route: {"requests": 3000, "errors": 0, "p95_ms": 25.0}
                for route in emitter.SOAK_ROUTE_TO_WORKLOAD
            },
        }
    }


class EmitRc2RigorProofTests(unittest.TestCase):
    def setUp(self) -> None:
        self.identity = emitter.CandidateIdentity(SHA, INDEX_DIGEST, LOCK_DIGEST, OBSERVED)

    def test_aot_multiarch_proof_emits_valid_schema(self) -> None:
        proof = emitter.build_aot_multiarch_proof(
            self.identity,
            (_passing_parity_scorecard(), AMD64_DIGEST),
            (_passing_parity_scorecard(), ARM64_DIGEST),
        )
        self.assertEqual(proof["serverSha"], SHA)
        self.assertEqual(proof["imageDigest"], INDEX_DIGEST)
        self.assertEqual(proof["architectures"]["amd64"]["platformDigest"], AMD64_DIGEST)
        self.assertEqual(proof["architectures"]["arm64"]["platformDigest"], ARM64_DIGEST)
        for flag in emitter.AOT_PROBE_FLAG_MAP:
            self.assertTrue(proof["architectures"]["amd64"][flag])

    def test_aot_multiarch_rejects_missing_probe_or_identical_digests(self) -> None:
        broken = _passing_parity_scorecard()
        broken["probes"] = [p for p in broken["probes"] if p["name"] != "wms_get_map"]
        with self.assertRaises(ValueError):
            emitter.build_aot_multiarch_proof(
                self.identity, (broken, AMD64_DIGEST), (_passing_parity_scorecard(), ARM64_DIGEST)
            )
        with self.assertRaises(ValueError):
            emitter.build_aot_multiarch_proof(
                self.identity,
                (_passing_parity_scorecard(), AMD64_DIGEST),
                (_passing_parity_scorecard(), AMD64_DIGEST),
            )

    def test_aot_soak_proof_emits_valid_schema_and_fails_on_missing_workload(self) -> None:
        limits = {"memoryLimitBytes": 1_073_741_824, "swapDisabled": True, "oomKillCount": 0}
        proof = emitter.build_aot_soak_proof(self.identity, _passing_soak_scorecard(), limits)
        self.assertEqual(proof["totalRequests"], 15_000)
        self.assertTrue(proof["swapDisabled"])
        self.assertEqual(len(proof["workloadsCovered"]), 5)

        incomplete = _passing_soak_scorecard()
        del incomplete["summary"]["per_route"]["mapserver_export"]
        with self.assertRaises(ValueError):
            emitter.build_aot_soak_proof(self.identity, incomplete, limits)

    def test_cli_roundtrip_writes_json_file(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            soak_in = root / "soak.json"
            out = root / "aot-memory-soak-proof.json"
            soak_in.write_text(json.dumps(_passing_soak_scorecard()), encoding="utf-8")
            rc = emitter.main([
                "aot-soak",
                "--server-sha", SHA,
                "--image-digest", INDEX_DIGEST,
                "--lock-digest", LOCK_DIGEST,
                "--observed-at", OBSERVED,
                "--soak-scorecard", str(soak_in),
                "--memory-limit-bytes", "1073741824",
                "--swap-disabled",
                "--out", str(out),
            ])
            self.assertEqual(rc, 0)
            written = json.loads(out.read_text(encoding="utf-8"))
            self.assertEqual(written["imageDigest"], INDEX_DIGEST)

    def test_postgis_upgrade_and_gis_clients_proxy_proofs(self) -> None:
        upgrade = emitter.build_postgis_upgrade_proof(self.identity, {
            "priorRcImageDigest": "sha256:" + "9" * 64,
            "preUpgradeFeatureCount": 6,
            "postUpgradeFeatureCount": 7,
            "migrationLockFailures": 0,
            "recoveredReadyHttpStatus": 200,
            "degradedStartupSurvived": True,
            "degradedInitialNotReady": True,
            "editableAfterUpgrade": True,
        })
        self.assertEqual(upgrade["postUpgradeFeatureCount"], 7)

        proxy = emitter.build_gis_clients_proxy_proof(self.identity, {
            "clients": {name: {"status": "PASS", "evidence": f"{name} ok"} for name in emitter.REQUIRED_CLIENTS},
            "corsOnErrorCodes": [400, 401, 402, 403, 429],
            **{flag: True for flag in emitter.CLIENT_PROXY_FLAGS},
        })
        self.assertEqual(proxy["corsOnErrorCodes"], [400, 401, 402, 403, 429])

    def test_gis_proxy_proof_excludes_unqualified_optional_client(self) -> None:
        clients = {
            name: {"status": "PASS", "evidence": f"{name} ok"}
            for name in emitter.REQUIRED_CLIENTS
        }
        clients["optional-client"] = {"status": "FAIL", "evidence": "could not load layer"}
        proof = emitter.build_gis_clients_proxy_proof(self.identity, {
            "clients": clients,
            "corsOnErrorCodes": sorted(emitter.REQUIRED_CORS_ERROR_CODES),
            **{flag: True for flag in emitter.CLIENT_PROXY_FLAGS},
        })
        self.assertEqual(set(proof["clients"]), emitter.REQUIRED_CLIENTS)

    def test_gis_proxy_proof_rejects_null_required_evidence(self) -> None:
        observation = {
            "clients": {
                name: {"status": "PASS", "evidence": None}
                for name in emitter.REQUIRED_CLIENTS
            },
            "corsOnErrorCodes": sorted(emitter.REQUIRED_CORS_ERROR_CODES),
            **{flag: True for flag in emitter.CLIENT_PROXY_FLAGS},
        }
        with self.assertRaisesRegex(ValueError, "did not record PASS with evidence"):
            emitter.build_gis_clients_proxy_proof(self.identity, observation)

    def test_tenant_isolation_proof_and_fail_closed(self) -> None:
        base_obs = {
            "singleTenantConsoleZero403": True,
            "platformAdminSucceeded": True,
            "singleTenantWorkflowsPassed": 7,
            "tenantAdminAllowedHostRoutes": 0,
            "crossTenantLeaks": 0,
            "multiTenantPlatformAdminRequiredRoutes": sorted(emitter.REQUIRED_HOST_ADMIN_ROUTES),
            "hiddenFieldProtocolsBlocked": sorted(emitter.REQUIRED_HIDDEN_PROTOCOLS),
        }
        tenant = emitter.build_tenant_isolation_proof(self.identity, base_obs)
        self.assertEqual(tenant["crossTenantLeaks"], 0)

        with self.assertRaises(ValueError):
            emitter.build_tenant_isolation_proof(self.identity, {**base_obs, "tenantAdminAllowedHostRoutes": 1})

    def test_verify_candidate_image_identity_requires_native_aot_label(self) -> None:
        index_raw = json.dumps({
            "manifests": [{"digest": AMD64_DIGEST, "platform": {"os": "linux", "architecture": "amd64"}}],
        }).encode("utf-8")
        aot_labels = json.dumps({"org.opencontainers.image.revision": SHA, "honua.runtime.compilation": "native-aot"})
        jit_labels = json.dumps({"org.opencontainers.image.revision": SHA, "honua.runtime.compilation": "jit"})
        with mock.patch("subprocess.check_output", side_effect=[index_raw, aot_labels]):
            emitter.verify_candidate_image_identity(SHA, INDEX_DIGEST, ("amd64", AMD64_DIGEST))
        with mock.patch("subprocess.check_output", side_effect=[index_raw, jit_labels]):
            with self.assertRaisesRegex(ValueError, "native-aot"):
                emitter.verify_candidate_image_identity(SHA, INDEX_DIGEST, ("amd64", AMD64_DIGEST))


if __name__ == "__main__":
    unittest.main()
