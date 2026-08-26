#!/usr/bin/env python3
"""Static regression checks for the shared lean-gate command contract."""

from __future__ import annotations

import os
import re
import subprocess
import tempfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
ACTION = ROOT / ".github" / "actions" / "lean-gate" / "action.yml"
SCOPE_SCRIPT = ROOT / "scripts" / "ci" / "compute-lean-gate-build-scope.sh"
PR_GATE = ROOT / ".github" / "workflows" / "pr-gate.yml"
CI = ROOT / ".github" / "workflows" / "ci.yml"
TEXT = ACTION.read_text(encoding="utf-8")


def require(pattern: str, description: str, text: str = TEXT) -> None:
    if re.search(pattern, text, flags=re.MULTILINE) is None:
        raise AssertionError(f"lean gate must {description}")


require(
    r"dotnet build Honua\.sln \\\s+--no-restore",
    "build from the assets produced by its explicit restore",
)
require(
    r"dotnet format Honua\.sln --verify-no-changes --no-restore ",
    "format without repeating restore after the full build",
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

# ---------------------------------------------------------------------------
# Affected-scope build contract (epic #3213).
#
# `PR Gate` narrows the warnings-as-errors build to a generated solution filter.
# The invariants below are the ones whose violation is silent rather than loud:
# a narrowed build that forgets --no-restore or the warnings-as-errors property
# weakens the required gate without failing, and a test step added without a
# matching entry in the scope script's REQUIRED_TEST_PROJECTS blows up at run
# time with an opaque "assembly not found" from `--no-build`.
# ---------------------------------------------------------------------------

require(
    r"^\s{2}build-scope:\s*$",
    "expose a `build-scope` input",
)
require(
    r"build-scope:.*?\n(?:.*\n)*?\s+default: 'full'",
    "default `build-scope` to full so a new caller cannot silently narrow the gate",
)
require(
    r"dotnet build \"\$\{GATE_BUILD_FILTER\}\" \\\s+--no-restore \\\s+"
    r"--configuration Release \\\s+/p:TreatWarningsAsErrors=true",
    "build the affected solution filter with the same "
    "--no-restore/Release/warnings-as-errors contract as the full build",
)
require(
    r"run: scripts/ci/compute-lean-gate-build-scope\.sh",
    "compute the build scope with the shared script",
)

if not SCOPE_SCRIPT.is_file():
    raise AssertionError(f"lean gate must ship {SCOPE_SCRIPT.relative_to(ROOT)}")
SCOPE_TEXT = SCOPE_SCRIPT.read_text(encoding="utf-8")

required_block = re.search(
    r"REQUIRED_TEST_PROJECTS=\(\n(.*?)\n\)", SCOPE_TEXT, flags=re.DOTALL
)
if required_block is None:
    raise AssertionError(
        "compute-lean-gate-build-scope.sh must declare REQUIRED_TEST_PROJECTS"
    )
seeded = set(re.findall(r'"([^"]+\.csproj)"', required_block.group(1)))

# Every project the gate's own `dotnet test` steps execute must be compiled by a
# narrowed build, because all of them run --no-build.
executed = set(re.findall(r"dotnet test (\S+\.csproj)", TEXT))

if seeded != executed:
    missing = sorted(executed - seeded)
    extra = sorted(seeded - executed)
    raise AssertionError(
        "compute-lean-gate-build-scope.sh REQUIRED_TEST_PROJECTS must be exactly the "
        "projects the lean gate's --no-build test steps execute; "
        f"missing={missing} unexpected={extra}"
    )

# ---------------------------------------------------------------------------
# Caller contract: only `PR Gate` narrows, and it must check out deeply enough
# for the narrowing to have a diff base at all. fetch-depth 1 would not fail —
# it would silently force-full every run and make the whole change a no-op.
# ---------------------------------------------------------------------------

PR_GATE_TEXT = PR_GATE.read_text(encoding="utf-8")
require(
    r"uses: \./\.github/actions/lean-gate\n(?:.*\n)*?\s+build-scope: affected",
    "have pr-gate.yml pass build-scope: affected",
    text=PR_GATE_TEXT,
)
require(
    r"uses: actions/checkout@[^\n]+\n\s+with:\n\s+fetch-depth: 2",
    "have pr-gate.yml check out with fetch-depth 2 so the merge ref's base "
    "parent exists locally",
    text=PR_GATE_TEXT,
)

CI_TEXT = CI.read_text(encoding="utf-8")
require(
    r"uses: \./\.github/actions/lean-gate\n(?:.*\n)*?\s+build-scope: full",
    "have ci.yml's Merge Queue Gate keep build-scope: full as the full-solution "
    "backstop for the per-PR narrowing",
    text=CI_TEXT,
)

# ---------------------------------------------------------------------------
# Behavioural smoke for the scope script's fail-safe directions. Both scripts
# must at minimum parse, and the two "never narrow" paths must actually not
# narrow — a regression here does not fail loudly at run time, it quietly builds
# less than the gate promises.
# ---------------------------------------------------------------------------

PREPULL_SCRIPT = ROOT / "scripts" / "ci" / "prepull-testcontainers-postgis.sh"
if not PREPULL_SCRIPT.is_file():
    raise AssertionError(f"lean gate must ship {PREPULL_SCRIPT.relative_to(ROOT)}")

for script in (SCOPE_SCRIPT, PREPULL_SCRIPT):
    if subprocess.run(["bash", "-n", str(script)]).returncode != 0:
        raise AssertionError(f"{script.relative_to(ROOT)} is not valid bash")


def scope_mode(**overrides: str) -> str:
    with tempfile.TemporaryDirectory() as tmp:
        output = Path(tmp) / "gh-output"
        output.touch()
        env = {
            **os.environ,
            "GITHUB_OUTPUT": str(output),
            "GATE_FILTER_PATH": str(Path(tmp) / "gate.slnf"),
            **overrides,
        }
        result = subprocess.run(
            [str(SCOPE_SCRIPT)], env=env, capture_output=True, text=True
        )
        if result.returncode != 0:
            raise AssertionError(
                f"compute-lean-gate-build-scope.sh exited {result.returncode}: "
                f"{result.stderr}"
            )
        modes = re.findall(r"^mode=(\S+)$", output.read_text(encoding="utf-8"), re.M)
        if len(modes) != 1:
            raise AssertionError(f"expected exactly one mode= output, got {modes}")
        return modes[0]


if scope_mode(BUILD_SCOPE="full", GITHUB_EVENT_NAME="pull_request") != "full":
    raise AssertionError("build-scope=full must never narrow the build")
if scope_mode(BUILD_SCOPE="affected", GITHUB_EVENT_NAME="merge_group") != "full":
    raise AssertionError(
        "a merge_group batch has no trustworthy diff base and must build the "
        "full solution; it is the backstop for the per-PR narrowing"
    )

print("Lean-gate command contract fixtures passed.")
