#!/usr/bin/env python3
"""Static regression checks for the shared lean-gate command contract."""

from __future__ import annotations

import re
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
ACTION = ROOT / ".github" / "actions" / "lean-gate" / "action.yml"
TEXT = ACTION.read_text(encoding="utf-8")


def require(pattern: str, description: str) -> None:
    if re.search(pattern, TEXT, flags=re.MULTILINE) is None:
        raise AssertionError(f"lean gate must {description}")


require(
    r"dotnet build Honua\.sln \\\s+--no-restore",
    "build from the assets produced by its explicit restore",
)
require(
    r"dotnet format Honua\.sln --verify-no-changes --no-restore ",
    "format without repeating restore after the full build",
)

catalog_test = (
    "Honua.Architecture.Tests.FeatureCatalog.FeatureCatalogDriftTests."
    "CommittedCatalog_EqualsFreshlyGeneratedOutput"
)
require(
    rf'--filter "FullyQualifiedName={re.escape(catalog_test)}"',
    "run the generated feature-catalog drift guard in a focused invocation",
)
require(
    rf'--filter "FullyQualifiedName!={re.escape(catalog_test)}"',
    "exclude the already-run catalog guard from the remaining architecture suite",
)
if TEXT.count(catalog_test) != 2:
    raise AssertionError(
        "lean gate must reference the catalog drift method exactly twice: "
        "once in the focused include and once in the complementary exclusion"
    )
require(
    r"dotnet test tests/dotnet/Honua\.Server\.Tests/Honua\.Server\.Tests\.csproj \\\s+--no-build \\\s+--no-restore",
    "run the Server Fast smoke without rebuilding or restoring",
)
require(
    r"dotnet test tests/dotnet/Honua\.Architecture\.Tests/Honua\.Architecture\.Tests\.csproj \\\s+--no-build \\\s+--no-restore",
    "run architecture tests without rebuilding or restoring",
)
require(
    r"dotnet test tests/dotnet/Honua\.Ai\.Tests/Honua\.Ai\.Tests\.csproj \\\s+--no-build \\\s+--no-restore",
    "run the focused MCP roster drift smoke without rebuilding or restoring",
)
require(
    r"CapabilityRegistryConformanceTests\.LiveMcpTools_MatchRegistryToolDescriptors.*"
    r"CapabilityRegistryConformanceTests\.RegistryToolDescriptors_MirrorLiveWorkflowFamilies.*"
    r"McpTaxonomyAlignmentTests\.ToolNames_MatchTaxonomyRoster.*"
    r"McpTaxonomyAlignmentTests\.TaxonomyRoster_MatchesCapabilityRegistryToolDescriptors.*"
    r"McpTaxonomyAlignmentTests\.ToolRoster_MatchesFullMcpDependencyInjectionRegistrations.*"
    r"McpTaxonomyAlignmentTests\.ErrorEnvelopeRoster_CoversEveryStaticallyRegisteredTool",
    "gate registry names, workflow families, and the taxonomy roster together",
)


def step_position(name: str) -> int:
    marker = f"- name: {name}"
    position = TEXT.find(marker)
    if position < 0:
        raise AssertionError(f"lean gate must contain the {name!r} step")
    return position


ordered_steps = (
    "Build (warnings as errors)",
    "Generated Feature Catalog Drift",
    "Format Check",
    "Run .NET Tests (Server Fast Tier)",
    "Run .NET Tests (Architecture)",
)
positions = tuple(step_position(name) for name in ordered_steps)
if positions != tuple(sorted(positions)):
    raise AssertionError(
        "lean gate must fail fast on generated feature-catalog drift after build "
        "and before format, Server Fast, and the remaining architecture suite"
    )

print("Lean-gate command contract fixtures passed.")
