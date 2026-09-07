#!/usr/bin/env python3
"""Select the server-test shard families a pull request should run in PR Gate.

WHY THIS EXISTS
    `PR Gate` is deliberately lean (build + fast unit + architecture). The 71
    entries of `.github/ci-shards.json` run per trunk tip in the trailing
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
    A router answer of `run_all` means 71 shard jobs. Fanning that out per
    pull request is the 2026-06-18 runner-starvation spiral that #2865 removed
    and that ./.github/actions/lean-gate is explicitly written to avoid. So the
    selection is truncated to `--cap` shards (default 6) and the dropped
    families are named in the receipt. This lane is a DETECTOR with a fixed
    budget, never a re-derivation of the full matrix.

ORDERING ("largest-impact first")
    Sorted by, in order:
      1. `test_class_hits` -- how many CHANGED TEST FILES declare a class this
         shard's own `dotnet test --filter` actually selects, evaluated with
         scripts/ci/check-server-test-shard-coverage.py's filter evaluator and
         class inventory. This is not a heuristic: it is the same question the
         shard runner asks, so a shard with a hit here is a shard that will
         literally execute the test the diff touched.

         It matters most where the router is weakest. Shard `paths` name
         individual test FILES for the protocol-split projects, so a diff that
         ADDS a test file to a directory a shard already owns is claimed by no
         `paths` entry at all and escalates to run_all -- and then the cap has
         to choose among 71 families with no routing signal. Measured on the
         merge commits of #4465 and #4489, this term moves the shard family
         that actually went red on trunk to rank 4 and rank 2 respectively;
         without it, #4465's owner sat at rank 10, outside the cap.

      2. `path_hits` -- how many of the diff's files this shard's own `paths`
         prefixes claim. A shard that owns twelve changed files is a better
         guess at the regression than one that owns a single file, and this is
         the only signal a source-only diff produces.
      3. `project_affected` -- whether the shard's test project is in the
         reverse-dependency closure of the diff. Under `run_all` this is the
         only discriminator the router leaves, and it is what keeps an
         unmapped-source escalation from spending the cap on six shards whose
         assemblies the change cannot even reach.
      4. `dispatch_rank` descending -- the observed shard duration in minutes
         that ci.yml already dispatches by. The longest shard carries the most
         test surface, so it is the better use of a remaining slot; it also
         front-loads the slowest job, which is what keeps six parallel shards
         near one shard of wall clock.
      5. Shard name, so the selection is deterministic.

    `path_hits` is computed here with the same `startswith` prefix test the
    router's jq uses. Both it and `test_class_hits` are RANKING inputs only --
    membership of the candidate set always comes from the router's own answer --
    so a divergence costs at most a reordering, never a mis-route. That is also
    why the class walk is FAIL-OPEN: if the coverage module cannot be loaded or
    the walk throws, the receipt records `test_ownership: unavailable` and the
    ranking degrades to `path_hits`, rather than failing a report-only lane.

FAIL-SAFE DIRECTION
    Opposite to the required gate's. The lean gate force-FULLS when its diff
    base is untrustworthy, because under-testing a required context is the
    dangerous direction. This lane is report-only and bounded, so it SKIPS
    instead: an untrustworthy base cannot produce a trustworthy shard
    selection, and spending six runners on six arbitrary shards would publish a
    verdict that means nothing. Every skip states its reason in the receipt.
"""

from __future__ import annotations

import argparse
import functools
import importlib.util
import json
import os
import subprocess
import sys
from pathlib import Path
from typing import Any, Iterable, Sequence


CONTRACT = "honua.pr-gate-affected-shards/v1"
DEFAULT_CAP = 6
DEFAULT_CONFIG = ".github/ci-shards.json"
DEFAULT_ROUTER = "scripts/ci/honua-server-targeted-tests.sh"
DEFAULT_PROJECTS_SCRIPT = "scripts/ci/compute-affected-projects.sh"
# Owns the `dotnet test --filter` evaluator and the test-class inventory the
# ADR-0037 coverage guard already runs over these same shards. Reused rather
# than reimplemented: a second filter parser that disagreed with the guard's
# would make this lane predict a shard the matrix does not actually run.
COVERAGE_MODULE = "scripts/ci/check-server-test-shard-coverage.py"

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
        ["git", "-C", str(root), "diff", "--name-only", f"{base}...{head}"],
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
    return shard.get("csproj") or DEFAULT_TEST_PROJECT


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


def count_test_class_hits(
    root: Path, config: dict[str, Any], changed_files: Sequence[str]
) -> dict[str, int] | None:
    """Changed test files each shard's own `--filter` would actually select.

    Returns None when the inventory cannot be built -- the caller then ranks on
    `path_hits` alone. A report-only detector must not fail because an optional
    precision signal was unavailable.
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
        for shard in config["shards"]:
            project = shard_test_project(shard)
            count = 0
            for changed in test_files:
                for fqn, entry in declared.get(changed, ()):
                    if entry["csproj"] != project:
                        continue
                    if any(
                        coverage.shard_claims(shard["filter"], f"{fqn}.{method}")
                        for method in entry["methods"]
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


def product_code_prefixes(config: dict[str, Any]) -> tuple[str, ...]:
    """`PRODUCT_CODE_PREFIXES` plus every root the shard map itself routes on.

    `src/` and `tests/dotnet/` hold nearly all of the routable tree, but not
    all of it: `.github/ci-shards.json` also routes `observability/`,
    `samples/gp/` and `tests/fixtures/toolbox-translation/` -- shard INPUTS
    that live outside both. `SloMetricContractTests` reads
    `observability/slo-metric-contract.json` and `ToolboxTranslationEndpointTests`
    consumes the toolbox fixtures, so a diff that edits only one of those can
    turn the owning shard red on trunk while this admission test answers
    `no_product_code` and the lane runs nothing. Deriving the set from the same
    config the router reads is what stops the two drifting again the next time
    a shard claims a new root.

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


def select(
    *,
    config: dict[str, Any],
    changed_files: Sequence[str],
    descriptor: dict[str, Any],
    affected_projects: list[str] | None,
    cap: int,
    test_class_hits: dict[str, int] | None = None,
) -> dict[str, Any]:
    """Pure selection. Every I/O-bound input is already resolved by the caller."""
    shards_by_name = {shard["name"]: shard for shard in config["shards"]}

    if affected_projects is None:
        project_scope = "unknown"
        closure: set[str] = set()
    elif affected_projects == ["ALL"]:
        project_scope = "all"
        closure = set()
    else:
        project_scope = "closure"
        closure = set(affected_projects)

    def project_affected(shard: dict[str, Any]) -> bool:
        # An unknown or force-full closure carries no narrowing information, so
        # it must not be read as "this shard is unaffected".
        if project_scope in ("unknown", "all"):
            return True
        return shard_test_project(shard) in closure

    router_run_all = bool(descriptor.get("run_all"))
    router_shards = [name for name in descriptor.get("shards") or [] if name in shards_by_name]

    if router_run_all:
        # The router escalated. Narrowing by the reverse-dependency closure is
        # the only honest signal left: a shard whose test assembly the diff
        # cannot reach is not a candidate for the regression this lane hunts.
        narrowed = [name for name in router_shards if project_affected(shards_by_name[name])]
        if narrowed and len(narrowed) < len(router_shards):
            candidates = narrowed
            candidate_reason = "run_all_narrowed_by_affected_projects"
        else:
            candidates = router_shards
            candidate_reason = "run_all"
    else:
        # The router already targeted. Its answer is authoritative and is NEVER
        # trimmed by the project closure here -- a shard the `paths` map
        # explicitly claims is exactly what the trailing matrix will run, and
        # dropping it would make this lane predict something else.
        candidates = router_shards
        candidate_reason = descriptor.get("reason") or "targeted"

        # ...but `no_path_match` is not a targeted answer. It is the router's
        # DEFAULT (`default_shards_when_no_match`, currently ["Core and Cloud
        # Contracts"]) for a file no `paths` entry claims, so it says nothing
        # about ownership. For a changed TEST file under a protocol-split
        # project that matters: `tests/dotnet/Honua.Protocols.OData.Tests/Source/
        # ODataFeatureProviderResolverTests.cs` is not listed by exact path, so
        # the router answers with the Core shard while the shard that actually
        # runs the changed test (`OData Core`, a different assembly entirely)
        # is never selected.
        #
        # `test_class_hits` already answers exactly that question, and it
        # answers it with the shard runner's own filter evaluator rather than a
        # guess -- so a nonzero entry IS the owning shard. Add those, keeping
        # the router's default alongside them.
        #
        # Only the ownership signal is used. Widening to the project closure
        # instead would add every shard sharing the test assembly (~40 for the
        # Honua.Server.Tests default), which the cap would then truncate by
        # dispatch_rank -- six arbitrary shards, the exact meaningless verdict
        # this module's FAIL-SAFE DIRECTION note refuses to publish.
        if candidate_reason == "no_path_match":
            owners = [
                name
                for name, hits in sorted((test_class_hits or {}).items())
                if hits > 0 and name not in router_shards
            ]
            if owners:
                candidates = router_shards + owners
                candidate_reason = "no_path_match_plus_test_owners"

    # Advisory shards (ci.yml matrix.advisory) are the #1965 rotted buckets:
    # non-gating and currently always red. Including one would manufacture a
    # false red in the very measurement that decides whether to promote.
    candidates = [name for name in candidates if not shards_by_name[name].get("advisory", False)]

    ranked = []
    for name in candidates:
        shard = shards_by_name[name]
        ranked.append(
            {
                "name": name,
                "test_class_hits": (test_class_hits or {}).get(name, 0),
                "path_hits": count_path_hits(shard, changed_files),
                "project_affected": project_affected(shard),
                "dispatch_rank": float(shard.get("dispatch_rank") or 0.0),
                "timeout_minutes": shard.get("timeout_minutes"),
                "test_timeout_minutes": shard.get("test_timeout_minutes"),
                "test_project": shard_test_project(shard),
            }
        )
    ranked.sort(
        key=lambda entry: (
            -entry["test_class_hits"],
            -entry["path_hits"],
            not entry["project_affected"],
            -entry["dispatch_rank"],
            entry["name"],
        )
    )

    selected = ranked[:cap]
    dropped = [entry["name"] for entry in ranked[cap:]]

    result: dict[str, Any] = {
        "contract": CONTRACT,
        "skip": False,
        "reason": candidate_reason,
        "router_reason": descriptor.get("reason"),
        "router_run_all": router_run_all,
        "project_scope": project_scope,
        "test_ownership": (
            "unavailable"
            if test_class_hits is None
            else (
                "resolved"
                if any(changed.startswith("tests/") for changed in changed_files)
                else "not_applicable"
            )
        ),
        "cap": cap,
        "changed_file_count": len(changed_files),
        "candidate_count": len(ranked),
        "selected": selected,
        "shards": [entry["name"] for entry in selected],
        "dropped": dropped,
        # dispatch_rank is the observed shard duration in minutes that ci.yml
        # dispatches by (commit 27bd481f0, "dispatch longest test shards
        # first"), so these two are the runner-minute and wall-clock estimates
        # for the fan-out this selection asks for.
        "projected_runner_minutes": round(sum(entry["dispatch_rank"] for entry in selected), 1),
        "projected_wall_clock_minutes": round(
            max((entry["dispatch_rank"] for entry in selected), default=0.0), 1
        ),
    }

    if not selected:
        result["skip"] = True
        result["reason"] = "no_shards_selected"
    return result


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
        "projected_runner_minutes": 0.0,
        "projected_wall_clock_minutes": 0.0,
    }
    result.update(extra)
    return result


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
        entry["csproj"] = shard_test_project(shard)
        entry["dispatch_rank"] = shard.get("dispatch_rank")
        entries.append(entry)
    entries.sort(key=lambda entry: -(entry["dispatch_rank"] or 0.0))
    return entries


def render_summary(result: dict[str, Any]) -> str:
    lines = ["### PR Gate / Affected shards — selection", ""]
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
        "| Shard | Changed tests it runs | Diff files claimed | Test project affected | dispatch_rank (min) |",
        "|---|---:|---:|:---:|---:|",
    ]
    for entry in result["selected"]:
        lines.append(
            f"| {entry['name']} | {entry.get('test_class_hits', 0)} | {entry['path_hits']} | "
            f"{'yes' if entry['project_affected'] else 'no'} | {entry['dispatch_rank']} |"
        )
    if result["dropped"]:
        lines += [
            "",
            f"Dropped by the cap ({len(result['dropped'])}): "
            + ", ".join(f"`{name}`" for name in result["dropped"]),
            "",
            "The trailing matrix still runs these per trunk tip. The cap is what "
            + "keeps this lane a bounded detector instead of a second full fan-out.",
        ]
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

    result = select(
        config=config,
        changed_files=changed_files,
        descriptor=descriptor,
        affected_projects=affected_projects,
        cap=args.cap,
        test_class_hits=count_test_class_hits(root, config, changed_files),
    )
    return emit(result, config, args)


def emit(result: dict[str, Any], config: dict[str, Any], args: argparse.Namespace) -> int:
    include = matrix_include(result, config)
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
    except SelectionError as error:
        print(f"::error::compute-affected-shards: {error}", file=sys.stderr)
        raise SystemExit(2)
