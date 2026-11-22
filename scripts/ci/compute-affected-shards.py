#!/usr/bin/env python3
"""Select the server-test shard families a pull request should run in PR Gate.

WHY THIS EXISTS
    `PR Gate` is deliberately lean (build + fast unit + architecture). Every
    entry of `.github/ci-shards.json` runs per trunk tip in the trailing
    matrix, so a shard regression is detected AFTER the merge that caused it.
    This module picks a small, bounded subset of those shard families for the
    diff so the same red appears BEFORE the merge.

    It is a SELECTOR, not a second router. Every routing decision comes from
    machinery that already gates trunk:

      * scripts/ci/honua-server-targeted-tests.sh -- the ADR-0037 router. It
        owns the `paths` map, the infrastructure short-circuit, the
        targeted-override prefixes and the unmapped-source safety net. It is
        invoked here verbatim with `--stdin`, so PR Gate can never route
        differently from the trailing matrix it is trying to predict.
      * scripts/ci/compute-affected-projects.sh -- the ADR-0043 reverse
        dependency closure. It maps changed SOURCE projects to the TEST
        projects that consume them through <ProjectReference>, which is the
        source->test mapping this selector needs and does not reimplement.

    What this module adds on top is the part neither of those can supply: a
    hard CAP, and an ordering that spends it on the shards most likely to be
    the red one.

THE CAP IS THE WHOLE SAFETY ARGUMENT
    A router answer of `run_all` means one job per shard entry. Fanning that out per
    pull request is the 2026-06-18 runner-starvation spiral that #2865 removed
    and that ./.github/actions/lean-gate is explicitly written to avoid. So the
    selection is truncated to `--cap` shards (default 6) and the dropped
    families are named in the receipt. This lane is a DETECTOR with a fixed
    budget, never a re-derivation of the full matrix.

THE TIME BUDGET
    With `--time-budget`, the ranking is re-spent against `pr_time_budget`
    from the shard map (scripts/ci/shard_time_budget.py): a shard over
    `max_shard_minutes`, or one that would push the selection past
    `max_total_minutes` runner-minutes, is named under `deferred_to_trunk`
    and left to the next trunk run. When every candidate is deferred the
    selection skips with `affected_shards_deferred_to_trunk`, which the
    verdict reports as a green `deferred` result naming those shards.

ORDERING ("largest-impact first")
    Sorted by, in order:
      1. `test_class_hits` -- how many CHANGED TEST FILES declare a class this
         shard's own `dotnet test --filter` actually selects, evaluated with
         scripts/ci/check-server-test-shard-coverage.py's filter evaluator and
         class inventory. This is not a heuristic: it is the same question the
         shard runner asks, so a shard with a hit here is a shard that will
         literally execute the test the diff touched.

         Scored against the EFFECTIVE runner filter. run-server-test-shard.sh
         composes integration filters with `&Tier!=Slow&Tier!=Fast`, so a
         changed class whose tests are all Fast or all Slow is run by none of
         the shard families and must credit none of them -- otherwise the shard
         claiming it by name receives the top-priority ranking signal for tests
         it never runs, and under a capped run_all answer that spends a slot a
         genuinely runnable affected shard needed.

         It matters most where the router is weakest. Shard `paths` name
         individual test FILES for the protocol-split projects, so a diff that
         ADDS a test file to a directory a shard already owns is claimed by no
         `paths` entry at all and escalates to run_all -- and then the cap has
         to choose among every family with no routing signal. Measured on the
         merge commits of #4465 and #4489, this term moves the shard family
         that actually went red on trunk to rank 4 and rank 2 respectively;
         without it, #4465's owner sat at rank 10, outside the cap.

      2. `path_hits` -- how many of the diff's files this shard's own `paths`
         prefixes claim. A shard that owns twelve changed files is a better
         guess at the regression than one that owns a single file.
      3. `priority_source_hits` -- changed files covered by a targeted override
         marked `prioritize: true` for this shard. Explicit source owners rank
         ahead of broad inherited coverage paths when path-hit counts tie.
         Priority never adds a candidate or bypasses the time budget.
      4. `project_affected` -- whether the shard's test project is in the
         reverse-dependency closure of the diff. Under `run_all` this is the
         only discriminator the router leaves, and it is what keeps an
         unmapped-source escalation from spending the cap on six shards whose
         assemblies the change cannot even reach.
      5. `dispatch_rank` descending -- the observed shard duration in minutes
         that ci.yml already dispatches by. The longest shard carries the most
         test surface, so it is the better use of a remaining slot; it also
         front-loads the slowest job, which is what keeps six parallel shards
         near one shard of wall clock.
      6. Shard name, so the selection is deterministic.

    Numbered children of a shard split for capacity ("WFS 1" .. "WFS 9") are
    one family. Among candidates that tie on the first four terms, each
    family's first child ranks before any family's second child, so a change to
    a class split across many children cannot spend the whole cap and time
    budget on that one class while another changed test's owner waits.

    `path_hits` is computed here with the same `startswith` prefix test the
    router's jq uses. Both it and `test_class_hits` are RANKING inputs only --
    membership of the candidate set always comes from the router's own answer --
    so a divergence costs at most a reordering, never a mis-route. The one
    place `test_class_hits` also ADDS a candidate is the router's
    `no_path_match` default, which is not an ownership claim at all; see
    select(). The class walk is FAIL-OPEN throughout: if the coverage module
    cannot be loaded or the walk throws, the receipt records
    `test_ownership: unavailable` and the ranking degrades to `path_hits`,
    while preserving the router's authoritative candidate set.

FAIL-SAFE DIRECTION
    An untrustworthy base cannot produce a trustworthy shard selection. The
    selector emits an explicit untrustworthy_base skip without spending runners
    on arbitrary shards. Enforcement treats that reason as unavailable and
    fails closed; only skips proving no affected product code may succeed.
"""

from __future__ import annotations

import argparse
import functools
import importlib.util
import json
import os
import re
import subprocess
import sys
from pathlib import Path
from typing import Any, Iterable, NamedTuple, Sequence


def _load_sibling(name: str) -> Any:
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(f"{name}.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


shard_projects = _load_sibling("shard_projects").shard_projects
time_budget = _load_sibling("shard_time_budget")
apply_time_budget = time_budget.apply_time_budget


CONTRACT = "honua.pr-gate-affected-shards/v1"
DEFAULT_CAP = 6
SPLIT_CHILD_SUFFIX = re.compile(r" \d+$")
DEFAULT_CONFIG = ".github/ci-shards.json"
DEFAULT_ROUTER = "scripts/ci/honua-server-targeted-tests.sh"
DEFAULT_PROJECTS_SCRIPT = "scripts/ci/compute-affected-projects.sh"
# Owns the `dotnet test --filter` evaluator and the test-class inventory the
# ADR-0037 coverage guard already runs over these same shards. Reused rather
# than reimplemented: a second filter parser that disagreed with the guard's
# would make this lane predict a shard the matrix does not actually run.
COVERAGE_MODULE = "scripts/ci/check-server-test-shard-coverage.py"

# Where the TestKit attributes that EMIT a Tier trait are declared. `[UnitTest]`
# expands to Tier=Fast, `[ScaleTest]`/`[CloudTest]`/... to Tier=Slow, and so on,
# so an attribute name is a tier the same way a literal `[Trait("Tier", ...)]`
# is. Discovered from the source rather than hard-coded, for the same reason the
# coverage guard discovers its test-method attributes: a new tiered attribute
# must not silently stop being recognised.
TIER_ATTRIBUTE_DIRS = (
    "tests/dotnet/Honua.TestKit/Attributes",
    "tests/dotnet/Honua.TestKit.Abstractions/Attributes",
)
# Retained for fixture/API compatibility; policy comes from the catalog.
ODATA_UNIT_PROJECT = "tests/dotnet/Honua.Protocols.OData.UnitTests/Honua.Protocols.OData.UnitTests.csproj"

def catalog_unit_project(root: Path, project: str) -> bool:
    catalog = json.loads((root / ".github/test-projects.json").read_text())
    return catalog["projects"].get(project, {}).get("role") in {"unit", "component"}


# Default exclusions for integration shards. The independent OData unit project
# owns Fast cases as well (see runnable_methods and the runner project override).
# It composes the shard's own `filter` with `&Tier!=Slow` and then `&Tier!=Fast`
# (HONUA_SERVER_TEST_EXCLUDE_SLOW / _EXCLUDE_FAST, both defaulting to true, and
# pr-gate.yml sets neither), so a changed test carrying one of these tiers is
# executed by NO shard this lane can select -- Fast runs once per CI run in
# dotnet-foundation-tests, Slow runs nightly.
#
# This has to be resolved from the SOURCE, not by handing the composed filter
# to `coverage.shard_claims`. That evaluator returns neutral-true for every
# non-FullyQualifiedName clause on purpose (check-server-test-shard-coverage.py
# `_eval`, so a Trait filter cannot falsely orphan a class), which makes
# `(base)&Tier!=Fast` and `base` indistinguishable to it.
EXCLUDED_TIERS = frozenset({"Fast", "Slow"})

_TIER_TRAIT_RE = re.compile(r'Trait\(\s*"Tier"\s*,\s*(?:"([A-Za-z]+)"|Tiers\.([A-Za-z]+))')
_TIER_EMITTER_RE = re.compile(r'"Tier"\s*,\s*(?:Tiers\.([A-Za-z]+)|"([A-Za-z]+)")')
_ATTRIBUTE_DECL_RE = re.compile(r"\bclass\s+([A-Za-z_][A-Za-z0-9_]*)Attribute\b")
_ATTRIBUTE_USE_RE = re.compile(r"[\[,]\s*([A-Za-z_][A-Za-z0-9_]*)")
_ATTRIBUTE_LINE_START_RE = re.compile(r"(?:\A|\n)[ \t]*\[")

# The test assembly a shard runs when its record declares no `csproj`. Mirrors
# the same default in scripts/ci/run-server-test-shard.sh and ci.yml.
DEFAULT_TEST_PROJECT = "tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj"

# "Product code changed" for the purposes of this lane. A diff that touches
# none of these prefixes (docs, ADRs, workflow YAML on its own, sample data)
# cannot change what a server-test shard executes, so the lane skips rather
# than spending runners to re-confirm trunk.
#
# tests/dotnet/ is IN on purpose: the shard filters select test classes by
# fully-qualified name, so a test-only change is exactly the kind of diff that
# turns a shard red without touching src/ at all.
#
# These two are the STATIC floor only. product_code_prefixes() below unions
# them with the roots the shard map itself routes on, which is what keeps the
# detector's own admission test from being narrower than the router it wraps.
PRODUCT_CODE_PREFIXES = ("src/", "tests/dotnet/")

# Fields ci.yml's `targeted-shards` job projects into its matrix. Kept in the
# same shape so both matrix jobs consume identical shard records and a red here
# means a red there. `upload_operator_eval_report` / `upload_odata_evidence` are
# deliberately absent: those steps publish trailing-matrix evidence artifacts
# that this detector lane does not produce.
MATRIX_FIELDS = (
    "shard_name",
    "artifact_suffix",
    "log_name",
    "timeout_minutes",
    "test_timeout_minutes",
    "max_cpu_count",
    "filter",
)


class SelectionError(Exception):
    """The selector could not be run at all (bad config, missing script)."""


def _repo_root() -> Path:
    return Path(__file__).resolve().parents[2]


def load_config(path: Path) -> dict[str, Any]:
    try:
        config = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as error:
        raise SelectionError(f"cannot read shard config {path}: {error}") from error
    shards = config.get("shards")
    if not isinstance(shards, list) or not shards:
        raise SelectionError(f"{path} declares no shards")
    return config


def read_lines(source: str) -> list[str]:
    text = sys.stdin.read() if source == "-" else Path(source).read_text(encoding="utf-8")
    return [line.strip() for line in text.splitlines() if line.strip()]


def git_changed_files(root: Path, base: str, head: str) -> list[str] | None:
    """Diff base...head, or None when the base is not present in this checkout."""
    probe = subprocess.run(
        ["git", "-C", str(root), "cat-file", "-e", f"{base}^{{commit}}"],
        capture_output=True,
    )
    if probe.returncode != 0:
        return None
    result = subprocess.run(
        ["git", "-C", str(root), "diff", "--no-renames", "--name-only", f"{base}...{head}"],
        capture_output=True,
        text=True,
    )
    if result.returncode != 0:
        return None
    return [line.strip() for line in result.stdout.splitlines() if line.strip()]


def run_router(root: Path, script: str, config: Path, changed_files: Sequence[str]) -> dict[str, Any]:
    """Ask the ADR-0037 router which shards claim this diff.

    Invoked with `--stdin` so the router sees exactly the file list this module
    saw -- no second diff, no chance of the two disagreeing about the base.
    """
    result = subprocess.run(
        [str(root / script), "--stdin", "--config", str(config)],
        input="\n".join(changed_files) + "\n",
        capture_output=True,
        text=True,
        cwd=str(root),
    )
    if result.returncode != 0:
        raise SelectionError(
            f"{script} exited {result.returncode}: {result.stderr.strip() or '(no stderr)'}"
        )
    try:
        descriptor = json.loads(result.stdout)
    except ValueError as error:
        raise SelectionError(f"{script} emitted non-JSON: {result.stdout[:200]!r}") from error
    if not isinstance(descriptor, dict) or "shards" not in descriptor:
        raise SelectionError(f"{script} emitted an unusable descriptor: {descriptor!r}")
    return descriptor


def run_affected_projects(root: Path, script: str, base: str, head: str) -> list[str] | None:
    """Reverse-dependency closure of the diff, or None when it cannot be trusted.

    Returns ["ALL"] when the underlying script force-fulls (shared build
    infrastructure changed), an empty list when the diff touches no project,
    and None when the script itself failed.
    """
    environment = dict(os.environ, BASE_REF=base, HEAD_REF=head)
    result = subprocess.run(
        [str(root / script)],
        capture_output=True,
        text=True,
        cwd=str(root),
        env=environment,
    )
    if result.returncode != 0:
        return None
    lines = [line.strip() for line in result.stdout.splitlines() if line.strip()]
    if "ALL" in lines:
        return ["ALL"]
    return [line for line in lines if line.endswith(".csproj")]


def shard_test_project(shard: dict[str, Any]) -> str:
    return "" if shard.get("csprojs") else shard.get("csproj") or DEFAULT_TEST_PROJECT


@functools.lru_cache(maxsize=1)
def load_shard_coverage(root: Path) -> tuple[Any, dict[str, dict[str, Any]]]:
    """Load the coverage guard's filter evaluator and its class inventory.

    Cached: the inventory is a walk of every test source in the repository
    (~1400 classes, a few seconds). Production calls this once; the fixtures
    call it per shard, and without the cache that alone is minutes.
    """
    spec = importlib.util.spec_from_file_location("honua_shard_coverage", root / COVERAGE_MODULE)
    coverage = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(coverage)
    return coverage, coverage.enumerate_test_classes()


@functools.lru_cache(maxsize=1)
def tier_attributes(root: Path) -> dict[str, str]:
    """`{attribute short name: tier}` for every TestKit attribute that emits one."""
    mapping: dict[str, str] = {}
    paths = (path for directory in TIER_ATTRIBUTE_DIRS for path in (root / directory).glob("*.cs"))
    for path in sorted(paths):
        text = path.read_text(encoding="utf-8", errors="replace")
        emitted = _TIER_EMITTER_RE.search(text)
        if not emitted:
            continue
        tier = emitted.group(1) or emitted.group(2)
        for name in _ATTRIBUTE_DECL_RE.findall(text):
            mapping[name] = tier
    return mapping


def _blank_literals(coverage: Any, text: str) -> str:
    """Blank comments and string literals to spaces, preserving every offset.

    `coverage._strip_noise` collapses them to a fixed token instead, so its
    output cannot be indexed back into the original source -- and the tier this
    scan needs lives INSIDE a string literal. Same single left-to-right
    alternation, so the same construct wins; only the replacement differs.
    """
    return coverage._NOISE.sub(
        lambda match: "".join(" " if char != "\n" else "\n" for char in match.group(0)),
        text,
    )


def _tiers_in(attribute_text: str, attributes: dict[str, str]) -> set[str]:
    tiers = {
        literal or constant for literal, constant in _TIER_TRAIT_RE.findall(attribute_text)
    }
    for name in _ATTRIBUTE_USE_RE.findall(attribute_text):
        tier = attributes.get(name) or attributes.get(name.removesuffix("Attribute"))
        if tier:
            tiers.add(tier)
    return tiers


@functools.lru_cache(maxsize=256)
def scan_tiers(root: Path, source: str) -> tuple[dict[str, set[str]], dict[str, set[str]]]:
    """`({class name: tiers}, {method name: tiers})` declared in one test source.

    Attributes are read positionally: each run of `[...]` blocks that starts a
    line is attributed to whatever declaration follows it, using the coverage
    guard's own bracket-depth skipper and declaration patterns so an
    `[InlineData(new[] { 1, 2 })]` cannot end the run early. Scoped to a single
    file (there are only ever a handful of changed test files), so no brace
    tracking is needed: the coverage inventory already tells the caller which
    class owns which method names.
    """
    coverage, _ = load_shard_coverage(root)
    raw = (root / source).read_text(encoding="utf-8", errors="replace")
    blanked = _blank_literals(coverage, raw)
    attributes = tier_attributes(root)

    classes: dict[str, set[str]] = {}
    methods: dict[str, set[str]] = {}
    position = 0
    while True:
        opening = _ATTRIBUTE_LINE_START_RE.search(blanked, position)
        if not opening:
            break
        start = opening.end() - 1
        end = coverage._skip_attribute_blocks(blanked, start)
        if end <= start:
            position = start + 1
            continue
        position = end
        tiers = _tiers_in(raw[start:end], attributes)
        if not tiers:
            continue
        declared = coverage.CLASS_RE.match(blanked, end)
        if declared:
            classes.setdefault(declared.group(1), set()).update(tiers)
            continue
        method = coverage._method_name_after(blanked, end)
        if method:
            methods.setdefault(method, set()).update(tiers)
    return classes, methods


def runnable_methods(root: Path, fqn: str, entry: dict[str, Any]) -> list[str]:
    """`entry["methods"]` minus the ones no selectable shard would execute.

    A class-level tier applies to every test in the class (xUnit unions class
    and method traits, and `Tier!=Fast` excludes a test if ANY of its Tier
    values is Fast), so an excluded class tier empties the class outright.
    A method with no Tier trait at all survives `Tier!=X`, which is why the
    untiered majority is unaffected.
    """
    # Catalogued isolated unit shards own Fast tests too; foundation
    # jobs still exclude Fast from every existing integration shard.
    excluded_tiers = EXCLUDED_TIERS - {"Fast"} if catalog_unit_project(root, entry.get("csproj", "")) else EXCLUDED_TIERS
    fqn = entry.get("fqn", fqn)
    simple = fqn.rsplit(".", 1)[-1]
    class_tiers: set[str] = set()
    method_tiers: dict[str, set[str]] = {}
    for source in entry["src"]:
        classes, methods = scan_tiers(root, source)
        class_tiers |= classes.get(simple, set())
        for name, tiers in methods.items():
            method_tiers.setdefault(name, set()).update(tiers)
    if class_tiers & excluded_tiers:
        return []
    return [
        method
        for method in entry["methods"]
        if not (method_tiers.get(method, set()) & excluded_tiers)
    ]


def count_test_class_hits(
    root: Path, config: dict[str, Any], changed_files: Sequence[str]
) -> dict[str, int] | None:
    """Changed test files each shard's own `--filter` would actually select.

    Returns None when the inventory cannot be built -- the caller then ranks on
    `path_hits` alone while preserving the router's authoritative candidates.
    """
    test_files = [changed for changed in changed_files if changed.startswith("tests/")]
    if not test_files:
        return {}
    try:
        coverage, classes = load_shard_coverage(root)
    except Exception as error:  # noqa: BLE001 - fail-open by design
        print(f"::debug::test-class ownership unavailable: {error}", file=sys.stderr)
        return None

    # A changed file can declare several test classes, and a class can be
    # declared across several partial files.
    declared: dict[str, list[tuple[str, dict[str, Any]]]] = {}
    for fqn, entry in classes.items():
        for source in entry["src"]:
            declared.setdefault(source, []).append((fqn, entry))

    hits: dict[str, int] = {}
    try:
        # Restricted to the methods a selectable shard would ACTUALLY execute.
        # run-server-test-shard.sh composes integration shard filters with
        # `&Tier!=Slow&Tier!=Fast`, so a changed class whose tests are all Fast
        # -- the class-level `[Trait("Tier", "Fast")]` on
        # ServingObservabilityContractTests is the shape -- is run by none of
        # them. Counting it anyway handed the claiming shard the
        # highest-priority ranking signal there is for tests it never runs,
        # which under a capped `run_all` selection spends a slot and can push a
        # genuinely runnable affected shard past the cap.
        runnable = {
            fqn: runnable_methods(root, fqn, entry)
            for changed in test_files
            for fqn, entry in declared.get(changed, ())
        }
        for shard in config["shards"]:
            projects = shard_projects(shard)
            count = 0
            for changed in test_files:
                for fqn, entry in declared.get(changed, ()):
                    if entry["csproj"] not in projects:
                        continue
                    if any(
                        coverage.shard_claims(shard["filter"], f"{entry.get('fqn', fqn)}.{method}")
                        for method in runnable[fqn]
                    ):
                        count += 1
                        break
            hits[shard["name"]] = count
    except Exception as error:  # noqa: BLE001 - fail-open by design
        print(f"::debug::shard filter evaluation failed: {error}", file=sys.stderr)
        return None
    return hits


def count_path_hits(shard: dict[str, Any], changed_files: Sequence[str]) -> int:
    prefixes = tuple(shard.get("paths") or ())
    if not prefixes:
        return 0
    return sum(1 for changed in changed_files if changed.startswith(prefixes))


def count_priority_source_hits(name: str, changed_files: Sequence[str], config: dict[str, Any]) -> int:
    prefixes = tuple(
        override["prefix"]
        for override in config.get("targeted_override_prefixes") or ()
        if override.get("prioritize") is True and name in override.get("shards", ())
    )
    return sum(changed.startswith(prefixes) for changed in changed_files)


def product_code_prefixes(config: dict[str, Any]) -> tuple[str, ...]:
    """`PRODUCT_CODE_PREFIXES` plus every root the shard map itself routes on.

    `src/` and `tests/dotnet/` hold nearly all of the routable tree, but not
    all of it: the shard map also routes telemetry contracts and GP, plugin
    and toolbox fixtures outside both trees. Those consumers must run when
    their input is the only changed file. Deriving admission from the live
    map keeps the selector from silently skipping them.

    `infrastructure_paths` is deliberately NOT unioned in. Those are the
    router's run_all short-circuit (`.github/`, `Directory.*.props`,
    `scripts/ci/`), and admitting a workflow-only diff as product code would
    spend the whole cap re-confirming trunk on every CI edit. The fixture's
    check_skip_paths() pins that boundary.
    """
    prefixes = set(PRODUCT_CODE_PREFIXES)
    for shard in config.get("shards") or ():
        prefixes.update(shard.get("paths") or ())
    for override in config.get("targeted_override_prefixes") or ():
        prefix = override.get("prefix")
        if prefix:
            prefixes.add(prefix)
    prefixes.update(config.get("unmapped_source_run_all_prefixes") or ())
    return tuple(sorted(prefixes))


def has_product_code(changed_files: Iterable[str], config: dict[str, Any]) -> bool:
    prefixes = product_code_prefixes(config)
    return any(changed.startswith(prefixes) for changed in changed_files)


class SelectionInputs(NamedTuple):
    """Resolved router and ownership signals plus the selection cap."""
    descriptor: dict[str, Any]
    affected_projects: list[str] | None
    cap: int
    test_class_hits: dict[str, int] | None = None


def project_scope(projects: list[str] | None) -> str:
    if projects is None:
        return "unknown"
    return "all" if projects == ["ALL"] else "closure"


def project_affected(shard: dict[str, Any], projects: list[str] | None) -> bool:
    # Unknown and force-full closures carry no narrowing information.
    return project_scope(projects) != "closure" or any(
        project in projects for project in shard_projects(shard)
    )


def selection_candidates(config: dict[str, Any], inputs: SelectionInputs) -> tuple[list[str], str]:
    shards = {shard["name"]: shard for shard in config["shards"]}
    descriptor = inputs.descriptor
    names = [name for name in descriptor.get("shards") or [] if name in shards]
    if descriptor.get("run_all"):
        # Only an escalated answer may narrow by project closure.
        narrowed = [name for name in names if project_affected(shards[name], inputs.affected_projects)]
        if narrowed and len(narrowed) < len(names):
            return narrowed, "run_all_narrowed_by_affected_projects"
        return names, "run_all"
    reason = descriptor.get("reason") or "targeted"
    if reason == "no_path_match":
        # A default answer says nothing about ownership. Add exact changed-test
        # owners, never the whole project closure, and preserve the default.
        owners = [name for name, hits in sorted((inputs.test_class_hits or {}).items())
                  if hits > 0 and name not in names]
        if owners:
            return names + owners, "no_path_match_plus_test_owners"
    return names, reason


def ranked_candidates(config: dict[str, Any], changed_files: Sequence[str],
                      inputs: SelectionInputs) -> tuple[list[dict[str, Any]], str]:
    shards = {shard["name"]: shard for shard in config["shards"]}
    candidates, reason = selection_candidates(config, inputs)
    ranked = []
    for name in candidates:
        shard = shards[name]
        # Advisory buckets remain outside gating selection.
        if shard.get("advisory", False):
            continue
        ranked.append({
            "name": name,
            "test_class_hits": (inputs.test_class_hits or {}).get(name, 0),
            "path_hits": count_path_hits(shard, changed_files),
            "priority_source_hits": count_priority_source_hits(name, changed_files, config),
            "project_affected": project_affected(shard, inputs.affected_projects),
            "dispatch_rank": float(shard.get("dispatch_rank") or 0.0),
            "timeout_minutes": shard.get("timeout_minutes"),
            "test_timeout_minutes": shard.get("test_timeout_minutes"),
            "test_project": shard_test_project(shard),
            "test_projects": shard_projects(shard),
        })
    ranked.sort(key=lambda entry: (
        -entry["test_class_hits"], -entry["path_hits"], -entry["priority_source_hits"],
        not entry["project_affected"], -entry["dispatch_rank"], entry["name"],
    ))
    return interleave_split_families(ranked), reason


def selection_result(ranked: list[dict[str, Any]], changed_files: Sequence[str],
                     inputs: SelectionInputs) -> dict[str, Any]:
    selected = ranked[:inputs.cap]
    return {
        "contract": CONTRACT,
        "skip": not selected,
        "router_reason": inputs.descriptor.get("reason"),
        "router_run_all": bool(inputs.descriptor.get("run_all")),
        "project_scope": project_scope(inputs.affected_projects),
        "test_ownership": (
            "unavailable" if inputs.test_class_hits is None else (
                "resolved" if any(path.startswith("tests/") for path in changed_files)
                else "not_applicable"
            )
        ),
        "cap": inputs.cap,
        "changed_file_count": len(changed_files),
        "candidate_count": len(ranked),
        "selected": selected,
        "shards": [entry["name"] for entry in selected],
        "dropped": [entry["name"] for entry in ranked[inputs.cap:]],
        "time_budget": None,
        "deferred_to_trunk": [],
        **time_budget.projected_minutes(selected),
    }


def select(config: dict[str, Any], changed_files: Sequence[str],
           inputs: SelectionInputs) -> dict[str, Any]:
    """Pure selection. Every I/O-bound input is already resolved by the caller."""
    ranked, reason = ranked_candidates(config, changed_files, inputs)
    result = selection_result(ranked, changed_files, inputs)
    result["reason"] = reason if ranked[:inputs.cap] else "no_shards_selected"
    return result


def interleave_split_families(ranked: Sequence[dict[str, Any]]) -> list[dict[str, Any]]:
    """Within each tie on the routing signals, take one child per split family per round."""
    rounds: dict[tuple[Any, ...], int] = {}

    def round_of(entry: dict[str, Any]) -> int:
        family = (tie(entry), SPLIT_CHILD_SUFFIX.sub("", entry["name"]))
        rounds[family] = rounds.get(family, 0) + 1
        return rounds[family]

    def tie(entry: dict[str, Any]) -> tuple[Any, ...]:
        return (-entry["test_class_hits"], -entry["path_hits"],
                -entry.get("priority_source_hits", 0), not entry["project_affected"])

    keyed = [(tie(entry), round_of(entry), position, entry) for position, entry in enumerate(ranked)]
    return [entry for *_, entry in sorted(keyed, key=lambda item: item[:3])]


def skipped(reason: str, *, cap: int, changed_file_count: int = 0, **extra: Any) -> dict[str, Any]:
    result = {
        "contract": CONTRACT,
        "skip": True,
        "reason": reason,
        "cap": cap,
        "changed_file_count": changed_file_count,
        "candidate_count": 0,
        "test_ownership": "not_evaluated",
        "selected": [],
        "shards": [],
        "dropped": [],
        "time_budget": None,
        "deferred_to_trunk": [],
        "projected_runner_minutes": 0.0,
        "projected_wall_clock_minutes": 0.0,
    }
    result.update(extra)
    return result


def uses_server_producer(entry: dict[str, Any]) -> bool:
    """A shard restores the parallel Server.Tests producer only when that is its whole closure.

    Multi-project and non-server entries stay with the main producer. A mixed
    closure rebuilds locally and must not start on the server-producer lane.
    """
    if entry.get("csprojs"):
        return False
    return entry.get("csproj") == DEFAULT_TEST_PROJECT


def partition_producer_matrices(
    include: list[dict[str, Any]],
) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    """Split a selection into server-producer entries and everything else."""
    early = [entry for entry in include if uses_server_producer(entry)]
    held = [entry for entry in include if not uses_server_producer(entry)]
    return early, held


def place_heavy_shards(include: list[dict[str, Any]], slowdown: float) -> list[dict[str, Any]]:
    """Mark heavy-pool shards per producer lane; each lane starts when its own producer ends."""
    early, held = partition_producer_matrices(include)
    placed = time_budget.place_heavy(early, slowdown) + time_budget.place_heavy(held, slowdown)
    by_name = {entry["shard_name"]: entry for entry in placed}
    return [by_name[entry["shard_name"]] for entry in include]


def matrix_include(result: dict[str, Any], config: dict[str, Any]) -> list[dict[str, Any]]:
    """Project the selection into ci.yml's matrix-entry shape.

    Sorted by -dispatch_rank, the same order ci.yml uses, so the longest shard
    starts first and six parallel shards cost roughly one shard of wall clock.
    """
    shards_by_name = {shard["name"]: shard for shard in config["shards"]}
    entries = []
    for name in result["shards"]:
        shard = shards_by_name[name]
        entry = {field: shard.get(field, "") for field in MATRIX_FIELDS}
        entry["csproj"] = shard.get("csproj") or ("" if "csprojs" in shard else DEFAULT_TEST_PROJECT)
        if "csprojs" in shard:
            entry["csprojs"] = shard_projects(shard)
        entry["dispatch_rank"] = shard.get("dispatch_rank")
        entries.append(entry)
    entries.sort(key=lambda entry: -(entry["dispatch_rank"] or 0.0))
    return entries


def selection_rows(result: dict[str, Any]) -> list[str]:
    lines = []
    for entry in result["selected"]:
        lines.append(
            f"| {entry['name']} | {entry.get('test_class_hits', 0)} | {entry['path_hits']} | "
            f"{entry.get('priority_source_hits', 0)} | "
            f"{'yes' if entry['project_affected'] else 'no'} | {entry['dispatch_rank']} |"
        )
    return lines


def render_summary(result: dict[str, Any]) -> str:
    lines = ["### PR Gate / Affected shards — selection", ""]
    if result["skip"] and result["reason"] == time_budget.DEFERRED_SKIP_REASON:
        return "\n".join(lines + time_budget.deferred_summary(result)) + "\n"
    if result["skip"]:
        lines += [
            f"No shards selected (`{result['reason']}`).",
            "",
            "This lane runs the trailing matrix's own shard families against the "
            + "diff so a shard regression is visible before the merge. Nothing in "
            + "this change can move one.",
        ]
        return "\n".join(lines) + "\n"

    lines += [
        f"- Router verdict: `{result['router_reason']}` "
        f"(`run_all={str(result['router_run_all']).lower()}`)",
        f"- Selection: `{result['reason']}`, project scope "
        f"`{result['project_scope']}`, test ownership `{result['test_ownership']}`",
        f"- Candidates: `{result['candidate_count']}`, cap `{result['cap']}`, "
        f"selected `{len(result['shards'])}`",
        f"- Projected cost: `{result['projected_runner_minutes']}` runner-minutes, "
        f"`{result['projected_wall_clock_minutes']}` min wall clock (parallel)",
        "",
        "| Shard | Changed tests it runs | Diff files claimed | Priority source files | Test project affected | dispatch_rank (min) |",
        "|---|---:|---:|---:|:---:|---:|",
    ]
    lines += selection_rows(result)
    if result["dropped"]:
        lines += [
            "",
            f"Dropped by the cap ({len(result['dropped'])}): "
            + ", ".join(f"`{name}`" for name in result["dropped"]),
            "",
            "The trailing matrix still runs these per trunk tip. The cap is what "
            + "keeps this lane a bounded detector instead of a second full fan-out.",
        ]
    if result["deferred_to_trunk"]:
        lines += [""] + time_budget.deferred_summary(result)
    return "\n".join(lines) + "\n"


def append_github_output(result: dict[str, Any], include: list[dict[str, Any]]) -> None:
    path = os.environ.get("GITHUB_OUTPUT")
    if not path:
        return
    with open(path, "a", encoding="utf-8") as handle:
        handle.write(f"skip={str(result['skip']).lower()}\n")
        handle.write(f"reason={result['reason']}\n")
        handle.write(f"selected_count={len(result['shards'])}\n")
        handle.write(f"matrix_include={json.dumps(include, separators=(',', ':'))}\n")
        early, held = partition_producer_matrices(include)
        handle.write(f"matrix_server={json.dumps(early, separators=(',', ':'))}\n")
        handle.write(f"matrix_other={json.dumps(held, separators=(',', ':'))}\n")
        # Suffixes only. The execution matrix carries test filters and must
        # not enter a job environment (validate-matrix-file-transport.py).
        receipt_matrix = [
            {"shard_name": entry["shard_name"], "artifact_suffix": entry["artifact_suffix"]}
            for entry in include
        ]
        handle.write(f"receipt_matrix={json.dumps(receipt_matrix, separators=(',', ':'))}\n")
        deferred_names = [entry["name"] for entry in result["deferred_to_trunk"]]
        handle.write(f"deferred_to_trunk={json.dumps(deferred_names, separators=(',', ':'))}\n")


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--base", default="HEAD^1", help="diff base (default: HEAD^1)")
    parser.add_argument("--head", default="HEAD", help="diff head (default: HEAD)")
    parser.add_argument(
        "--changed-files",
        help="read the changed-file list from this path ('-' for stdin) instead of git",
    )
    parser.add_argument(
        "--projects",
        help=(
            "read the affected-project closure from this path ('-' for stdin, one "
            "csproj per line or the single token ALL) instead of running "
            f"{DEFAULT_PROJECTS_SCRIPT}"
        ),
    )
    parser.add_argument("--config", default=DEFAULT_CONFIG)
    parser.add_argument("--router", default=DEFAULT_ROUTER)
    parser.add_argument("--projects-script", default=DEFAULT_PROJECTS_SCRIPT)
    parser.add_argument("--cap", type=int, default=DEFAULT_CAP)
    parser.add_argument("--time-budget", action="store_true",
                        help=f"apply the {time_budget.TIME_BUDGET_KEY} policy from the shard map")
    parser.add_argument("--annotate-resources", action="store_true",
                        help="annotate execution matrix using the shared conservative catalog policy")
    parser.add_argument("--json", dest="json_path", help="write the receipt JSON here")
    parser.add_argument("--summary", dest="summary_path", help="write the markdown summary here")
    parser.add_argument(
        "--github-output",
        action="store_true",
        help="append skip/reason/matrix_include to $GITHUB_OUTPUT",
    )
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    args = parse_args(argv)
    if args.cap < 1:
        print("--cap must be at least 1", file=sys.stderr)
        return 2

    root = _repo_root()
    config_path = root / args.config
    config = load_config(config_path)
    budget = time_budget.load_time_budget(config) if args.time_budget else None

    if args.changed_files:
        changed_files = read_lines(args.changed_files)
    else:
        resolved = git_changed_files(root, args.base, args.head)
        if resolved is None:
            result = skipped("untrustworthy_base", cap=args.cap, base=args.base)
            return emit(result, config, args)
        changed_files = resolved

    if not changed_files:
        return emit(skipped("no_changed_files", cap=args.cap), config, args)
    if not has_product_code(changed_files, config):
        return emit(
            skipped("no_product_code", cap=args.cap, changed_file_count=len(changed_files)),
            config,
            args,
        )

    descriptor = run_router(root, args.router, config_path, changed_files)

    if args.projects:
        lines = read_lines(args.projects)
        affected_projects = ["ALL"] if "ALL" in lines else lines
    elif args.changed_files:
        # A caller that supplied its own file list has no git base to close over.
        affected_projects = None
    else:
        affected_projects = run_affected_projects(root, args.projects_script, args.base, args.head)

    inputs = SelectionInputs(descriptor, affected_projects,
                             len(config["shards"]) if budget else args.cap,
                             count_test_class_hits(root, config, changed_files))
    result = select(config, changed_files, inputs)
    if budget:
        result = apply_time_budget(result, args.cap, budget)
    return emit(result, config, args)


def emit(result: dict[str, Any], config: dict[str, Any], args: argparse.Namespace) -> int:
    include = place_heavy_shards(matrix_include(result, config), time_budget.load_heavy_slowdown(config))
    if getattr(args, "annotate_resources", False):
        root = _repo_root()
        include = json.loads(subprocess.check_output(
            [sys.executable, str(root / "scripts/ci/annotate-shard-resources.py"),
             "--catalog", str(root / ".github/test-projects.json")],
            input=json.dumps(include), text=True))
        # Record the exact annotated fan-out without changing selection/cap evidence.
        result = {**result, "executionMatrix": include}
    summary = render_summary(result)
    if args.json_path:
        Path(args.json_path).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    if args.summary_path:
        Path(args.summary_path).write_text(summary, encoding="utf-8")
    if args.github_output:
        append_github_output(result, include)
    print(json.dumps(result, indent=2))
    print(summary, file=sys.stderr)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (SelectionError, time_budget.TimeBudgetError) as error:
        print(f"::error::compute-affected-shards: {error}", file=sys.stderr)
        raise SystemExit(2)
