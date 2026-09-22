# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""The capacity-soak substrate boots candidates that require a key-ring certificate (#4885).

Since #4722 the Production startup policy composes the Redis-backed durable operation secret
channel and exits at boot unless ``Operations:SecretChannel:KeyRingCertificatePath`` names a
PKCS#12 certificate with a private key. ``capacity-soak-candidate.yml`` runs every candidate under
that policy, so a soak without the certificate never reaches readiness and no receipt can be
minted. These tests pin the three halves of the fix together: the workflow mints a usable
per-run certificate, the compose file mounts it read-only and points the server at it, and
nothing in the repository relaxes the production requirement or carries key material.
"""

from __future__ import annotations

import os
import shutil
import stat
import subprocess
from pathlib import Path

import pytest
import yaml

REPOSITORY_ROOT = Path(__file__).parents[3]
WORKFLOW = REPOSITORY_ROOT / ".github" / "workflows" / "capacity-soak-candidate.yml"
COMPOSE = REPOSITORY_ROOT / "docker-compose.soak.yml"
KEY_RING_RESOLVER = (
    REPOSITORY_ROOT / "src" / "Honua.Server" / "Features" / "Operations" / "OperationSecretKeyRingProtection.cs"
)

MINT_STEP = "Mint a throwaway key-ring certificate for this run"
CONTAINER_PATH = "/keyring/operation-keyring.pfx"


def _steps() -> list[dict]:
    return yaml.safe_load(WORKFLOW.read_text(encoding="utf-8"))["jobs"]["soak"]["steps"]


def _step_index(predicate) -> int:
    return next(index for index, step in enumerate(_steps()) if predicate(step))


def _honua_service() -> dict:
    return yaml.safe_load(COMPOSE.read_text(encoding="utf-8"))["services"]["honua"]


def test_compose_points_the_server_at_the_mounted_certificate():
    environment = _honua_service()["environment"]
    assert environment["Operations__SecretChannel__KeyRingCertificatePath"] == CONTAINER_PATH
    # Required interpolation, never a literal: a committed password would be a committed secret, and
    # a silent empty default would boot a certificate that cannot be opened.
    assert environment["Operations__SecretChannel__KeyRingCertificatePassword"].startswith(
        "${SOAK_KEYRING_PASSWORD:?"
    )


def test_compose_mounts_the_per_run_certificate_read_only():
    mounts = [volume for volume in _honua_service()["volumes"] if volume.endswith(f":{CONTAINER_PATH}:ro")]
    assert len(mounts) == 1, _honua_service()["volumes"]
    assert mounts[0].startswith("${SOAK_KEYRING_CERTIFICATE:?")


def test_certificate_is_minted_before_the_substrate_first_boots():
    mint = _step_index(lambda step: step.get("name") == MINT_STEP)
    boot = _step_index(lambda step: "docker-compose.soak.yml up" in step.get("run", ""))
    assert mint < boot


def test_every_compose_invocation_after_minting_can_resolve_the_certificate():
    # The republish restart, the driver's recovery drill and teardown all re-read the compose file,
    # so the pair must reach the job environment rather than a single step's env block.
    run = _steps()[_step_index(lambda step: step.get("name") == MINT_STEP)]["run"]
    assert 'SOAK_KEYRING_CERTIFICATE=$keyring/operation-keyring.pfx" >> "$GITHUB_ENV"' in run
    assert 'SOAK_KEYRING_PASSWORD=$SOAK_KEYRING_PASSWORD" >> "$GITHUB_ENV"' in run
    assert 'echo "::add-mask::$SOAK_KEYRING_PASSWORD"' in run
    # The password never appears on an openssl command line, where the process table would show it.
    assert "pass:" not in run


def test_no_key_ring_material_is_committed():
    assert not list(REPOSITORY_ROOT.glob("*.pfx"))
    assert not list((REPOSITORY_ROOT / "scripts" / "soak").glob("*.p12"))
    assert not list((REPOSITORY_ROOT / "scripts" / "soak").glob("*.pfx"))


def test_the_production_requirement_is_not_relaxed_for_the_soak():
    source = KEY_RING_RESOLVER.read_text(encoding="utf-8")
    assert "is required when the durable operation secret channel is enabled" in source
    assert "must reference a certificate with a private key" in source
    environment = _honua_service()["environment"]
    assert environment["ASPNETCORE_ENVIRONMENT"] == "Production"


@pytest.mark.skipif(shutil.which("openssl") is None, reason="openssl is not installed")
def test_mint_step_produces_a_password_protected_pkcs12_with_a_private_key(tmp_path: Path):
    run = _steps()[_step_index(lambda step: step.get("name") == MINT_STEP)]["run"]
    github_env = tmp_path / "github_env"
    github_env.touch()
    environment = {**os.environ, "RUNNER_TEMP": str(tmp_path), "GITHUB_ENV": str(github_env)}
    subprocess.run(["bash", "-e", "-c", run], env=environment, check=True, capture_output=True, text=True)

    exported = dict(line.split("=", 1) for line in github_env.read_text(encoding="utf-8").splitlines())
    certificate = Path(exported["SOAK_KEYRING_CERTIFICATE"])
    password = exported["SOAK_KEYRING_PASSWORD"]
    assert certificate == tmp_path / "keyring" / "operation-keyring.pfx"
    assert len(password) >= 32

    # World-readable so the image's non-root user can read the read-only bind mount.
    assert stat.S_IMODE(certificate.stat().st_mode) == 0o644
    assert stat.S_IMODE(certificate.parent.stat().st_mode) == 0o755
    # Only the PKCS#12 survives: no loose PEM private key or certificate is left on the runner.
    assert sorted(path.name for path in certificate.parent.iterdir()) == ["operation-keyring.pfx"]

    opened = {**os.environ, "KEYRING_PASSWORD": password}
    keys = subprocess.run(
        ["openssl", "pkcs12", "-in", str(certificate), "-nocerts", "-nodes", "-passin", "env:KEYRING_PASSWORD"],
        env=opened, check=True, capture_output=True, text=True,
    ).stdout
    assert "PRIVATE KEY-----" in keys, "OperationSecretKeyRingProtection refuses a certificate without a private key"

    wrong = {**os.environ, "KEYRING_PASSWORD": "not-the-run-password"}
    refused = subprocess.run(
        ["openssl", "pkcs12", "-in", str(certificate), "-nokeys", "-passin", "env:KEYRING_PASSWORD"],
        env=wrong, capture_output=True, text=True,
    )
    assert refused.returncode != 0

    second = tmp_path / "second"
    second.mkdir()
    (second / "github_env").touch()
    subprocess.run(
        ["bash", "-e", "-c", run],
        env={**os.environ, "RUNNER_TEMP": str(second), "GITHUB_ENV": str(second / "github_env")},
        check=True, capture_output=True, text=True,
    )
    again = dict(line.split("=", 1) for line in (second / "github_env").read_text(encoding="utf-8").splitlines())
    assert again["SOAK_KEYRING_PASSWORD"] != password, "the certificate password must be per run"
