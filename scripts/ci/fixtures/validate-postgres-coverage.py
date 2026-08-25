#!/usr/bin/env python3
"""Static regression checks for Postgres compatibility and coverage semantics."""

from __future__ import annotations

import json
import re
import xml.etree.ElementTree as ET
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
WORKFLOW = ROOT / ".github" / "workflows" / "ci.yml"
SETTINGS = ROOT / "coverlet.postgres.runsettings"
TEXT = WORKFLOW.read_text(encoding="utf-8")


def require(pattern: str, description: str) -> re.Match[str]:
    match = re.search(pattern, TEXT, flags=re.MULTILINE | re.DOTALL)
    if match is None:
        raise AssertionError(f"Postgres CI must {description}")
    return match


full_matrix_match = require(
    r"POSTGRES_FULL='(?P<matrix>\[[^']+\])'",
    "declare an explicit full compatibility matrix",
)
full_matrix = json.loads(full_matrix_match.group("matrix"))
if [entry["pg_suffix"] for entry in full_matrix] != ["16", "17", "18"]:
    raise AssertionError("full Postgres compatibility matrix must cover versions 16, 17, and 18")
if [entry["pg_suffix"] for entry in full_matrix if entry["collect_coverage"]] != ["16"]:
    raise AssertionError("full Postgres matrix must retain one authoritative PG16 coverage leg")

job = require(
    r"^  postgres-compat:\n(?P<body>.*?)(?=^  [a-z0-9-]+:\n)",
    "retain the postgres-compat job",
).group("body")
if "timeout-minutes: 30" not in job:
    raise AssertionError("Postgres compatibility must retain its bounded 30-minute budget")
if "tests/dotnet/Honua.Db.Postgres.Tests/Honua.Postgres.Tests.csproj" not in job:
    raise AssertionError("Postgres compatibility must execute the full provider test assembly")
if "--settings coverlet.postgres.runsettings" not in job:
    raise AssertionError("PG16 coverage must use provider-scoped collector settings")
if "--settings coverlet.runsettings" in job:
    raise AssertionError("Postgres coverage must not instrument the repository-wide assembly graph")
if "always() && matrix.collect_coverage" not in job:
    raise AssertionError("Postgres coverage evidence must still upload from its selected matrix leg")

ci_gate = require(
    r"^  ci-gate:\n(?P<body>.*)\Z",
    "retain the CI Gate aggregator",
).group("body")
if "postgres-compat" not in ci_gate:
    raise AssertionError("Postgres compatibility must remain authoritative in CI Gate")

configuration = ET.parse(SETTINGS).getroot().find(".//Configuration")
if configuration is None:
    raise AssertionError("Postgres Coverlet settings must define collector configuration")
include = configuration.findtext("Include")
if include != "[Honua.Postgres]*":
    raise AssertionError(
        "Postgres coverage must instrument only the Honua.Postgres provider assembly, "
        f"got {include!r}"
    )
if configuration.findtext("IncludeTestAssembly") != "false":
    raise AssertionError("Postgres coverage must exclude the test assembly")

print("Postgres compatibility coverage fixtures passed.")
