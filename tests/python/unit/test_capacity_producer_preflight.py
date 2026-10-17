# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
"""The in-progress producer must neither suppress verification failures nor admit a release."""
from __future__ import annotations

import hashlib
import importlib.util
import json
from pathlib import Path
import sys
from types import ModuleType
from unittest.mock import Mock

import pytest
import yaml

ROOT = Path(__file__).resolve().parents[3]
SOURCE = "a" * 40
REF = "honua-io/honua-server/.github/workflows/capacity-soak-candidate.yml@refs/heads/trunk"


def setup(tmp_path, monkeypatch):
    verifier = ModuleType("check_capacity_soak")
    verifier.evaluate = Mock(return_value=[])
    verifier.bind_attestation_content = Mock(return_value=[])
    verifier.bind_attestation = Mock(side_effect=AssertionError("completed-run admission cannot run inside its job"))
    verifier.lock_digest = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()
    monkeypatch.setitem(sys.modules, verifier.__name__, verifier)
    spec = importlib.util.spec_from_file_location("producer_preflight", ROOT / "scripts/soak/producer_preflight.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    receipt = {"producer": {"runId": "100", "runAttempt": "2", "workflowRef": REF}}
    values = {"lock": {}, "receipt": receipt, "attestation": [{"verified": True}]}
    for name, value in values.items():
        (tmp_path / name).write_text(json.dumps(value))
    (tmp_path / "bundle").write_bytes(b"attested bundle bytes")
    argv = [item for name in values for item in ("--" + name, str(tmp_path / name))]
    argv += ["--bundle", str(tmp_path / "bundle"), "--artifact-root", str(tmp_path),
             "--expected-revision", SOURCE, "--expected-image-digest", "sha256:" + "b" * 64,
             "--expected-run-id", "100", "--expected-run-attempt", "2", "--expected-workflow-ref", REF]
    return module, verifier, receipt, argv


def test_preflight_binds_exact_verified_bytes_and_recomputes_without_self_admission(tmp_path, monkeypatch, capsys):
    module, verifier, receipt, argv = setup(tmp_path, monkeypatch)
    assert module.main(argv) == 0
    output = capsys.readouterr().out
    assert output.strip() == "producer preflight pass; completed-run release admission pending"
    assert "capacity-soak: PASS" not in output
    verifier.evaluate.assert_called_once_with({}, receipt, verifier.lock_digest(tmp_path / "lock"), SOURCE,
                                               tmp_path, "sha256:" + "b" * 64)
    verifier.bind_attestation_content.assert_called_once_with([{"verified": True}],
        hashlib.sha256(b"attested bundle bytes").hexdigest(), receipt, SOURCE)
    verifier.bind_attestation.assert_not_called()


@pytest.mark.parametrize("producer", [
    {"runId": "101", "runAttempt": "2", "workflowRef": REF},
    {"runId": "100", "runAttempt": "1", "workflowRef": REF},
    {"runId": "100", "runAttempt": "2", "workflowRef": REF.replace("trunk", "other")},
    {},
])
def test_prior_or_unrelated_invocation_cannot_preflight(tmp_path, monkeypatch, producer):
    module, _, receipt, argv = setup(tmp_path, monkeypatch)
    receipt["producer"] = producer
    (tmp_path / "receipt").write_text(json.dumps(receipt))
    assert module.main(argv) == 1


@pytest.mark.parametrize("operation", ["evaluate", "bind_attestation_content"])
def test_signal_or_attestation_content_refusal_cannot_be_dropped(tmp_path, monkeypatch, capsys, operation):
    module, verifier, _, argv = setup(tmp_path, monkeypatch)
    getattr(verifier, operation).return_value = ["seeded verification failure"]
    assert module.main(argv) == 1
    assert "seeded verification failure" in capsys.readouterr().out


@pytest.mark.parametrize("operation", ["evaluate", "bind_attestation_content"])
def test_unreadable_verification_evidence_cannot_become_success(tmp_path, monkeypatch, operation):
    module, verifier, _, argv = setup(tmp_path, monkeypatch)
    getattr(verifier, operation).side_effect = ValueError("invalid evidence")
    assert module.main(argv) == 1


def test_workflow_fetches_all_verifier_dependencies_at_one_commit_and_pins_crypto():
    steps = yaml.safe_load((ROOT / ".github/workflows/capacity-soak-candidate.yml").read_text())["jobs"]["soak"]["steps"]
    fetch = next(step["run"] for step in steps if step.get("id") == "lock")
    assert "release_runner_policy.py" in fetch
    assert '$root/certification/release-runners.v1.json' in fetch
    assert '"$RUNNER_TEMP/certification/release-runners.v1.json"' in fetch
    assert 'honua-release/$release_sha' in fetch
    check = next(step["run"] for step in steps if step.get("id") == "receipt")
    for option in ("--signer-workflow honua-io/honua-server/.github/workflows/capacity-soak-candidate.yml",
                   '--source-digest "$CANDIDATE_SHA"', "--predicate-type https://slsa.dev/provenance/v1"):
        assert option in check
    assert 'PYTHONPATH="$RUNNER_TEMP/release-tools" python producer/scripts/soak/producer_preflight.py' in check
    assert "completed-run release admission pending" in check
    assert 'python "$RUNNER_TEMP/release-tools/check_capacity_soak.py"' not in check
