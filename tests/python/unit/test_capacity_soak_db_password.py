# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

"""The capacity soak's database password is minted per run, never shipped as a literal.

Since #5077 the Production startup policy refuses every credential literal this repository
ships (ConfigurationValidationService.KnownPlaceholderSecrets), and the soak boots candidates
under that policy. ``SOAK_DB_PASSWORD: soak-candidate-db-password`` reached the server through
``ConnectionStrings__DefaultConnection`` in docker-compose.soak.yml, so honua-release nightly
40354887147's capacity producer (honua-server run 40355678030, candidate 8cb074f7) exited before
readiness: "'ConnectionStrings__DefaultConnection' is set to a placeholder value that ships in
this repository's example and harness files". No receipt was published and the night refused.
"""

from __future__ import annotations

from pathlib import Path

import yaml

REPOSITORY_ROOT = Path(__file__).parents[3]
WORKFLOW = REPOSITORY_ROOT / ".github" / "workflows" / "capacity-soak-candidate.yml"
VALIDATION = (
    REPOSITORY_ROOT / "src" / "Honua.Server" / "Features" / "Admin" / "Services" / "ConfigurationValidationService.cs"
)

GENERATE_STEP = "Mint a throwaway database password for this run"
START_STEP = "Start the local-docker substrate"


def _workflow() -> dict:
    return yaml.safe_load(WORKFLOW.read_text(encoding="utf-8"))


def _steps() -> list[dict]:
    return _workflow()["jobs"]["soak"]["steps"]


def test_the_workflow_ships_no_database_password_literal() -> None:
    assert "SOAK_DB_PASSWORD" not in _workflow()["env"]
    assert "soak-candidate-db-password" not in WORKFLOW.read_text(encoding="utf-8")
    for step in _steps():
        assert "SOAK_DB_PASSWORD" not in (step.get("env") or {}), step.get("name")


def test_a_per_run_password_is_minted_masked_and_exported_before_the_substrate_starts() -> None:
    names = [step.get("name", "") for step in _steps()]
    assert GENERATE_STEP in names
    assert names.index(GENERATE_STEP) < names.index(START_STEP)
    script = _steps()[names.index(GENERATE_STEP)]["run"]
    assert 'SOAK_DB_PASSWORD="$(openssl rand -hex 24)"' in script
    assert script.index('::add-mask::$SOAK_DB_PASSWORD') < script.index('>> "$GITHUB_ENV"')
    assert 'echo "SOAK_DB_PASSWORD=$SOAK_DB_PASSWORD" >> "$GITHUB_ENV"' in script


def test_the_production_refusal_of_the_old_literal_is_kept() -> None:
    # The fix supplies a real secret; it never relaxes the server's shipped-literal refusal.
    assert '"soak-candidate-db-password"' in VALIDATION.read_text(encoding="utf-8")
