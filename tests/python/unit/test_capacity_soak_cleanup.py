# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
"""Execute the capacity substrate lifecycle with a recording Docker control."""
from __future__ import annotations

import json
import os
import shutil
from pathlib import Path
import stat
import subprocess

import pytest
import yaml

ROOT = Path(__file__).parents[3]
SCRIPT = ROOT / "scripts/soak/capacity-substrate.sh"
PROJECT = "honua-capacity-soak"


@pytest.fixture
def substrate(tmp_path):
    binary = tmp_path / "bin"
    binary.mkdir()
    docker = binary / "docker"
    docker.write_text('''#!/usr/bin/env python3
import json, os, pathlib, subprocess, sys
args = sys.argv[1:]
with open(os.environ["CALLS"], "a") as log:
    log.write(json.dumps(args) + "\\n")
if args[0] == "compose":
    assert args[1:3] == ["--project-name", "honua-capacity-soak"]
    path = pathlib.Path(args[4])
    if args[5] == "config":
        if os.environ.get("REAL_DOCKER"):
            sys.exit(subprocess.run([os.environ["REAL_DOCKER"], *args]).returncode)
        if os.environ.get("CONFIG_FAIL"): sys.exit(11)
        print("name: honua-capacity-soak\\nservices: {}\\n# " + os.environ["SOAK_LICENSE_CONTENT"])
    else:
        assert path.is_file(), "configuration must exist before any mutation"
        assert "synthetic-license" in path.read_text(), "startup inputs must survive"
        if args[5] == "up" and os.environ.get("UP_FAIL"): sys.exit(12)
        if args[5] == "down" and os.environ.get("DOWN_FAIL"): sys.exit(13)
else:
    assert args[-1] == "label=com.docker.compose.project=honua-capacity-soak"
    kind = {"ps": "container", "network": "network", "volume": "volume"}[args[0]]
    if os.environ.get("QUERY_FAIL") == kind: sys.exit(14)
    if os.environ.get("LEFTOVER") == kind: print("owned-resource")
''')
    docker.chmod(0o755)
    calls = tmp_path / "calls.jsonl"
    environment = {**os.environ, "PATH": f"{binary}:{os.environ['PATH']}",
                   "RUNNER_TEMP": str(tmp_path), "CALLS": str(calls)}

    def run(action, **extra):
        return subprocess.run(["bash", str(SCRIPT), action], cwd=ROOT,
                              env={**environment, **extra}, capture_output=True, text=True)

    return run, tmp_path / "capacity-substrate/compose.yaml", calls


def _calls(path):
    return [json.loads(line) for line in path.read_text().splitlines()]


def test_cleanup_reuses_private_configuration_without_startup_environment(substrate):
    run, prepared, calls = substrate
    started = run("start", SOAK_LICENSE_CONTENT="synthetic-license")
    assert started.returncode == 0, started.stderr
    assert stat.S_IMODE(prepared.stat().st_mode) == 0o600
    assert stat.S_IMODE(prepared.parent.stat().st_mode) == 0o700
    finished = run("cleanup")
    assert finished.returncode == 0, finished.stderr
    mutations = [call for call in _calls(calls) if call[0] == "compose" and call[5] in ("up", "down")]
    assert [call[5] for call in mutations] == ["up", "down"]
    assert mutations[0][1:5] == mutations[1][1:5]  # Same project and exact private file.
    assert not prepared.exists()
    assert "synthetic-license" not in started.stdout + started.stderr + finished.stdout + finished.stderr


def test_partial_startup_still_has_cleanup_inputs(substrate):
    run, prepared, calls = substrate
    assert run("start", SOAK_LICENSE_CONTENT="synthetic-license", UP_FAIL="1").returncode == 12
    assert prepared.exists()
    assert run("cleanup").returncode == 0
    assert any(call[0] == "compose" and call[5] == "down" for call in _calls(calls))


@pytest.mark.parametrize("render_failed", [False, True])
def test_early_failure_confirms_no_owned_resources_without_dummy_inputs(substrate, render_failed):
    run, prepared, calls = substrate
    if render_failed:
        assert run("start", CONFIG_FAIL="1").returncode == 11
    assert not prepared.exists()
    assert run("cleanup").returncode == 0
    recorded = _calls(calls)
    assert not any(call[0] == "compose" and call[5] in ("up", "down") for call in recorded)
    assert [call[0] for call in recorded if call[0] != "compose"] == ["ps", "network", "volume"]


@pytest.mark.parametrize("prepared_first", [False, True])
@pytest.mark.parametrize("kind", ["container", "network", "volume"])
def test_unresolved_owned_resources_fail_cleanup(substrate, prepared_first, kind):
    run, prepared, _ = substrate
    if prepared_first:
        assert run("start", SOAK_LICENSE_CONTENT="synthetic-license").returncode == 0
    result = run("cleanup", LEFTOVER=kind)
    assert result.returncode != 0
    assert f"project-owned {kind}" in result.stderr
    assert prepared.exists() == prepared_first


def test_teardown_nonzero_is_not_hidden_even_if_no_resources_remain(substrate):
    run, prepared, _ = substrate
    assert run("start", SOAK_LICENSE_CONTENT="synthetic-license").returncode == 0
    assert run("cleanup", DOWN_FAIL="1").returncode == 13
    assert prepared.exists()  # Retain the private recovery inputs on failure.


@pytest.mark.parametrize("kind", ["container", "network", "volume"])
def test_unavailable_resource_inventory_fails_closed(substrate, kind):
    run, _, _ = substrate
    assert run("cleanup", QUERY_FAIL=kind).returncode == 14


def test_workflow_always_runs_strict_cleanup_and_never_uploads_private_config():
    workflow = yaml.safe_load((ROOT / ".github/workflows/capacity-soak-candidate.yml").read_text())
    steps = workflow["jobs"]["soak"]["steps"]
    startup = next(step for step in steps if step.get("id") == "substrate")
    cleanup = next(step for step in steps if step.get("name") == "Tear down the substrate")
    assert "capacity-substrate.sh start" in startup["run"]
    assert 'SOAK_LICENSE_CONTENT="$(cat "$RUNNER_TEMP/license/license.json")"' in startup["run"]
    assert cleanup["if"] == "always()"
    assert cleanup["run"] == "scripts/soak/capacity-substrate.sh cleanup"
    assert not cleanup.get("continue-on-error", False)
    for step in steps:
        if "actions/upload-artifact@" in step.get("uses", ""):
            paths = step["with"]["path"]
            assert "capacity-substrate" not in paths
            assert not any(line.strip().rstrip("/") in ("${{ runner.temp }}", "${{ runner.temp }}/**")
                           for line in paths.splitlines())
    contract = (ROOT / ".github/workflows/capacity-evidence-contract.yml").read_text()
    assert "tests/python/unit/test_capacity_soak_cleanup.py" in contract


def test_actual_compose_render_retains_required_inputs_for_later_cleanup(substrate, tmp_path):
    docker = shutil.which("docker")
    if not docker or subprocess.run([docker, "compose", "version"], capture_output=True).returncode:
        pytest.skip("Docker Compose is not installed; this test renders only and creates no resources")
    run, prepared, _ = substrate
    license_content = "synthetic-license-$NOT_FROM_ENV-${NOT_FROM_ENV}-$$"
    master_key = "synthetic-master-$NOT_FROM_ENV"
    started = run("start", REAL_DOCKER=docker,
                  SOAK_SERVER_IMAGE="ghcr.io/honua-io/honua-server@sha256:" + "a" * 64,
                  SOAK_CANDIDATE_SHA="b" * 40, SOAK_DB_PASSWORD="synthetic-db",
                  SOAK_ADMIN_PASSWORD="synthetic-admin", SOAK_MASTER_KEY=master_key,
                  SOAK_LICENSE_CONTENT=license_content, SOAK_LICENSE_TRUSTED_KEY="synthetic-public-key",
                  SOAK_KEYRING_PASSWORD="synthetic-keyring-password", SOAK_KEYRING_CERTIFICATE=str(tmp_path / "keyring.pfx"))
    assert started.returncode == 0, started.stderr
    # Parse the retained file again with actual Compose, as cleanup does, after
    # every startup input has disappeared. Dollar literals must not become a
    # second interpolation into the teardown process environment.
    clean_env = {key: value for key, value in os.environ.items() if not key.startswith("SOAK_")}
    clean_env["NOT_FROM_ENV"] = "must-not-replace-the-startup-value"
    reparsed = subprocess.run([docker, "compose", "--project-name", PROJECT, "-f", str(prepared),
                              "config", "--format", "json"], env=clean_env,
                             capture_output=True, text=True)
    assert reparsed.returncode == 0, reparsed.stderr
    document = json.loads(reparsed.stdout)
    assert document["name"] == PROJECT
    services = document["services"]
    assert services["honua"]["image"] == "ghcr.io/honua-io/honua-server@sha256:" + "a" * 64
    assert services["honua"]["environment"]["Licensing__LicenseContent"] == license_content
    assert services["honua"]["environment"]["Security__ConnectionEncryption__MasterKey"] == master_key
    assert services["honua"]["environment"]["HONUA_GIT_SHA"] == "b" * 40
    assert run("cleanup").returncode == 0
