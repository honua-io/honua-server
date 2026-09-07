#!/usr/bin/env python3
"""Offline fixtures for the PR Gate affected-shard selector.

Runs against the REAL `.github/ci-shards.json`, so a routing change that
stopped a feature namespace from selecting its owning shard fails here rather
than silently shrinking the detector. No git, no network, no dotnet.

Every assertion is paired with a failure injection: the fixture proves the
check REJECTS a mutated input before it accepts the live one, so a green run
means the check is still load-bearing.
"""

from __future__ import annotations

import copy
import importlib.util
import json
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Any


REPO_ROOT = Path(__file__).resolve().parents[3]
SELECTOR = REPO_ROOT / "scripts/ci/compute-affected-shards.py"
CONFIG_PATH = REPO_ROOT / ".github/ci-shards.json"

# The fields ci.yml::server-tests reads off its own matrix entries. An entry
# missing one of these would fail at run time inside the shard job, not here,
# so the shape is asserted at selection time.
REQUIRED_MATRIX_FIELDS = (
    "shard_name",
    "artifact_suffix",
    "log_name",
    "timeout_minutes",
    "test_timeout_minutes",
    "max_cpu_count",
    "filter",
    "csproj",
)


class FixtureError(AssertionError):
    """A fixture expectation was violated."""


# Memoised by runnable_method_index(); the tier injection resets it so the
# patched walk is re-derived rather than served from the live run.
_RUNNABLE_METHODS: dict[str, list[str]] | None = None


def load_module():
    spec = importlib.util.spec_from_file_location("compute_affected_shards", SELECTOR)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


selector = load_module()


def run_selector(changed_files: list[str], *, cap: int = 6) -> dict[str, Any]:
    """Drive the real CLI, so argument plumbing is covered too."""
    argv = [sys.executable, str(SELECTOR), "--changed-files", "-", "--cap", str(cap)]
    completed = subprocess.run(
        argv,
        input="\n".join(changed_files) + "\n",
        capture_output=True,
        text=True,
        cwd=str(REPO_ROOT),
    )
    if completed.returncode != 0:
        raise FixtureError(f"selector exited {completed.returncode}: {completed.stderr[-800:]}")
    return json.loads(completed.stdout)


def select_offline(
    config: dict[str, Any],
    changed_files: list[str],
    *,
    descriptor: dict[str, Any],
    projects: list[str] | None = None,
    cap: int = 6,
) -> dict[str, Any]:
    """Exercise the pure selection with a supplied router answer."""
    return selector.select(
        config=config,
        changed_files=changed_files,
        descriptor=descriptor,
        affected_projects=projects,
        cap=cap,
        test_class_hits=selector.count_test_class_hits(REPO_ROOT, config, changed_files),
    )


def router(
    config: dict[str, Any],
    changed_files: list[str],
    *,
    config_path: Path = CONFIG_PATH,
) -> dict[str, Any]:
    return selector.run_router(REPO_ROOT, selector.DEFAULT_ROUTER, config_path, changed_files)


def candidates(result: dict[str, Any]) -> set[str]:
    return set(result["shards"]) | set(result["dropped"])


def unique_owned_prefix(config: dict[str, Any], shard: dict[str, Any]) -> str | None:
    """A source/test directory prefix that ONLY this shard's `paths` claim.

    Shared prefixes (the Core validation pipeline, the TestKit harness) are
    deliberately mapped to several shards, so they prove nothing about a single
    shard's ownership.
    """
    others = [
        prefix
        for other in config["shards"]
        if other["name"] != shard["name"]
        for prefix in other.get("paths", ())
    ]
    for prefix in shard.get("paths", ()):
        if not prefix.endswith("/"):
            continue
        if not prefix.startswith(("src/", "tests/")):
            continue
        if any(prefix.startswith(other) or other.startswith(prefix) for other in others):
            continue
        return prefix
    return None


def runnable_method_index(classes: dict[str, dict[str, Any]]) -> dict[str, list[str]]:
    """`{class fqn: the methods a shard would actually run}` for the inventory.

    Resolved once and shared. The ownership walk below is 71 shards x ~1400
    classes, and deriving tiers inside that loop re-reads every test source per
    shard -- the fixture went from 55s to over two minutes before this.
    """
    global _RUNNABLE_METHODS
    if _RUNNABLE_METHODS is None:
        _RUNNABLE_METHODS = {
            fqn: selector.runnable_methods(REPO_ROOT, fqn, entry)
            for fqn, entry in classes.items()
        }
    return _RUNNABLE_METHODS


def check_feature_namespace_selects_its_shard(
    config: dict[str, Any], *, config_path: Path = CONFIG_PATH
) -> int:
    """A change under one feature namespace must select that namespace's shard.

    This is the property the whole lane rests on: if a diff under
    `src/Honua.Protocols.OgcClassic/Wfs20/` stops selecting `WFS`, PR Gate
    silently stops predicting the trailing matrix for that family.
    """
    covered = 0
    for shard in config["shards"]:
        prefix = unique_owned_prefix(config, shard)
        if prefix is None:
            continue
        covered += 1
        probe = f"{prefix}AffectedShardsFixtureProbe.cs"
        result = select_offline(
            config,
            [probe],
            descriptor=router(config, [probe], config_path=config_path),
            projects=None,
            cap=6,
        )
        if shard["name"] not in candidates(result):
            raise FixtureError(
                f"a change under {prefix!r} did not select shard {shard['name']!r} "
                f"(reason={result['reason']}, candidates={sorted(candidates(result))[:8]})"
            )
    if covered < 10:
        raise FixtureError(
            f"only {covered} shards declare a uniquely owned source prefix; the "
            "namespace fixture has stopped covering the routing map"
        )
    return covered


def check_cap_is_enforced(config: dict[str, Any]) -> None:
    """A run_all router answer must never fan out past the cap."""
    probe = ["tests/dotnet/Honua.TestKit/AffectedShardsFixtureProbe.cs"]
    descriptor = router(config, probe)
    if not descriptor.get("run_all"):
        raise FixtureError(
            "the TestKit probe no longer escalates to run_all; pick a live "
            "infrastructure_paths entry for this fixture"
        )
    for cap in (1, 3, 6):
        result = select_offline(config, probe, descriptor=descriptor, cap=cap)
        if len(result["shards"]) != cap:
            raise FixtureError(f"cap={cap} selected {len(result['shards'])} shards")
        if result["candidate_count"] != len(config["shards"]):
            raise FixtureError(
                f"run_all should consider every shard; got {result['candidate_count']}"
            )
        if len(result["dropped"]) != len(config["shards"]) - cap:
            raise FixtureError("dropped families were not reported")


def check_largest_impact_first(config: dict[str, Any]) -> None:
    """Path hits outrank dispatch_rank: the shard owning more of the diff wins."""
    ranked = sorted(config["shards"], key=lambda shard: -float(shard.get("dispatch_rank") or 0))
    slow = next(s for s in ranked if unique_owned_prefix(config, s))
    fast = next(
        s
        for s in reversed(ranked)
        if unique_owned_prefix(config, s) and s["name"] != slow["name"]
    )
    slow_prefix = unique_owned_prefix(config, slow)
    fast_prefix = unique_owned_prefix(config, fast)
    changed = [f"{slow_prefix}One.cs"] + [f"{fast_prefix}Probe{index}.cs" for index in range(5)]
    descriptor = {"run_all": False, "reason": "targeted", "shards": [slow["name"], fast["name"]]}
    result = select_offline(config, changed, descriptor=descriptor, cap=6)
    if result["shards"][0] != fast["name"]:
        raise FixtureError(
            f"expected {fast['name']!r} (5 claimed files, dispatch_rank "
            f"{fast['dispatch_rank']}) to outrank {slow['name']!r} (1 file, "
            f"dispatch_rank {slow['dispatch_rank']}); got {result['shards']}"
        )


def check_changed_test_file_selects_its_running_shard(config: dict[str, Any]) -> int:
    """A changed test file must select the shard whose `--filter` runs it.

    This is the signal that makes the cap usable on a run_all answer. Shard
    `paths` name individual test FILES for the protocol-split projects, so a
    diff that ADDS one is claimed by no routing entry at all and escalates --
    and then only the filter tells the truth about which of the 71 families
    executes the changed test. Proven against live test classes rather than
    synthetic paths, because the claim is about the real filter grammar.
    """
    coverage, classes = selector.load_shard_coverage(REPO_ROOT)

    # One pass over the whole inventory: which shards run each class. Scored on
    # the RUNNABLE methods, matching the selector -- a class every shard skips
    # on tier cannot demonstrate ownership of one.
    runnable = runnable_method_index(classes)
    owners: dict[str, list[str]] = {}
    for shard in config["shards"]:
        project = selector.shard_test_project(shard)
        for fqn, entry in classes.items():
            if entry["csproj"] != project or not entry["methods"]:
                continue
            if any(
                coverage.shard_claims(shard["filter"], f"{fqn}.{method}")
                for method in runnable[fqn]
            ):
                owners.setdefault(fqn, []).append(shard["name"])

    # Only a class exactly one shard runs proves ownership; a class two shards
    # both execute cannot rank one above the other.
    exclusive: dict[str, str] = {}
    for fqn, shard_names in owners.items():
        if len(shard_names) == 1 and shard_names[0] not in exclusive:
            exclusive[shard_names[0]] = classes[fqn]["src"][0]

    if len(exclusive) < 5:
        raise FixtureError(
            f"only {len(exclusive)} shards exclusively own a discoverable test class; "
            "the ownership fixture has stopped covering the filter map"
        )

    run_all = {
        "run_all": True,
        "reason": "infrastructure_change",
        "shards": [shard["name"] for shard in config["shards"]],
    }
    for shard_name, source in exclusive.items():
        hits = selector.count_test_class_hits(REPO_ROOT, config, [source])
        if hits is None or hits.get(shard_name, 0) < 1:
            raise FixtureError(
                f"changing {source!r} did not credit {shard_name!r}, the only shard "
                "whose filter runs it"
            )
        result = select_offline(config, [source], descriptor=run_all, cap=6)
        # Without the ownership term this ranks on dispatch_rank alone, which is
        # what put #4465's red shard at position 10 -- outside the cap.
        if shard_name not in result["shards"]:
            raise FixtureError(
                f"a run_all diff whose only changed test is {source!r} did not select "
                f"{shard_name!r}; got {result['shards']}"
            )
    return len(exclusive)


def check_non_runnable_tiers_are_not_credited(config: dict[str, Any]) -> int:
    """A changed test NO shard would execute must credit no shard at all.

    Every shard runs `(<filter>)&Tier!=Slow&Tier!=Fast`
    (scripts/ci/run-server-test-shard.sh; both exclusions default on and
    pr-gate.yml overrides neither), so a class whose tests are all Fast or all
    Slow is executed by none of the 71 families no matter which one's
    `FullyQualifiedName` filter claims it. Crediting it anyway would hand that
    shard `test_class_hits` -- the top-priority ranking term -- for tests it
    never runs, and under a capped run_all answer that spends a slot a
    genuinely runnable affected shard needed.

    Discovered from the live inventory rather than naming a class: the point is
    that the rule holds for whatever the repository currently declares.
    """
    coverage, classes = selector.load_shard_coverage(REPO_ROOT)
    runnable = runnable_method_index(classes)

    # A source file can declare several classes and count_test_class_hits reads
    # the FILE, so only a file that is entirely tier-excluded proves anything.
    runnable_sources = {
        source
        for fqn, entry in classes.items()
        if runnable[fqn]
        for source in entry["src"]
    }

    proven = 0
    for fqn, entry in sorted(classes.items()):
        if not entry["methods"] or runnable[fqn]:
            continue
        source = entry["src"][0]
        if source in runnable_sources:
            continue
        claimed_by = [
            shard["name"]
            for shard in config["shards"]
            if selector.shard_test_project(shard) == entry["csproj"]
            and any(
                coverage.shard_claims(shard["filter"], f"{fqn}.{method}")
                for method in entry["methods"]
            )
        ]
        if not claimed_by:
            continue
        hits = selector.count_test_class_hits(REPO_ROOT, config, [source])
        if hits is None:
            raise FixtureError("test-class ownership was unavailable")
        credited = sorted(name for name, count in hits.items() if count)
        if credited:
            raise FixtureError(
                f"{source!r} declares only tier-excluded tests, which no shard runs, "
                f"but it credited {credited}"
            )
        proven += 1
        if proven >= 3:
            break

    if proven == 0:
        raise FixtureError(
            "no entirely tier-excluded test class is claimed by a shard filter; the "
            "non-runnable-tier fixture has stopped covering anything"
        )
    return proven


def check_run_all_narrows_by_affected_projects(config: dict[str, Any]) -> None:
    """A run_all answer is narrowed to the shards the diff's closure can reach."""
    project = "tests/dotnet/Honua.Protocols.OData.Tests/Honua.Protocols.OData.Tests.csproj"
    owners = {
        shard["name"]
        for shard in config["shards"]
        if selector.shard_test_project(shard) == project
    }
    if not owners:
        raise FixtureError(f"no shard runs {project}; refresh this fixture")
    descriptor = {
        "run_all": True,
        "reason": "infrastructure_change",
        "shards": [shard["name"] for shard in config["shards"]],
    }
    result = select_offline(config, ["src/Honua.Core/Models/Probe.cs"], descriptor=descriptor, projects=[project], cap=6)
    if result["reason"] != "run_all_narrowed_by_affected_projects":
        raise FixtureError(f"expected the closure to narrow run_all; got {result['reason']}")
    if not candidates(result) <= owners:
        raise FixtureError(
            f"narrowing leaked shards outside the closure: {sorted(candidates(result) - owners)}"
        )
    # ALL carries no narrowing information and must not be read as one.
    unnarrowed = select_offline(
        config, ["src/Honua.Core/Models/Probe.cs"], descriptor=descriptor, projects=["ALL"], cap=6
    )
    if unnarrowed["reason"] != "run_all":
        raise FixtureError(f"a force-full closure must not narrow; got {unnarrowed['reason']}")


def check_targeted_answer_is_never_trimmed(config: dict[str, Any]) -> None:
    """The router's targeted answer is authoritative; the closure cannot drop it."""
    shard = next(s for s in config["shards"] if unique_owned_prefix(config, s))
    descriptor = {"run_all": False, "reason": "targeted", "shards": [shard["name"]]}
    result = select_offline(
        config,
        [f"{unique_owned_prefix(config, shard)}Probe.cs"],
        descriptor=descriptor,
        projects=["tests/dotnet/Honua.Definitely.Not.A.Real.Tests/Nope.csproj"],
        cap=6,
    )
    if result["shards"] != [shard["name"]]:
        raise FixtureError(
            f"a targeted router answer was trimmed by the project closure: {result['shards']}"
        )


def check_advisory_shards_are_excluded(config: dict[str, Any]) -> None:
    """The #1965 always-red buckets must never enter the false-red measurement."""
    mutated = copy.deepcopy(config)
    mutated["shards"][0]["advisory"] = True
    name = mutated["shards"][0]["name"]
    descriptor = {"run_all": True, "reason": "infrastructure_change", "shards": [s["name"] for s in mutated["shards"]]}
    result = select_offline(mutated, ["src/Honua.Core/Models/Probe.cs"], descriptor=descriptor, cap=71)
    if name in candidates(result):
        raise FixtureError(f"advisory shard {name!r} was selected")


def check_matrix_include_shape(config: dict[str, Any]) -> None:
    """Matrix entries must carry everything the shard job and runner consume."""
    probe = ["tests/dotnet/Honua.TestKit/AffectedShardsFixtureProbe.cs"]
    result = select_offline(config, probe, descriptor=router(config, probe), cap=6)
    include = selector.matrix_include(result, config)
    if len(include) != len(result["shards"]):
        raise FixtureError("matrix_include does not match the selection")
    ranks = [entry["dispatch_rank"] for entry in include]
    if ranks != sorted(ranks, reverse=True):
        raise FixtureError(f"matrix_include must dispatch longest-first; got {ranks}")
    for entry in include:
        for field in REQUIRED_MATRIX_FIELDS:
            if field not in entry:
                raise FixtureError(f"matrix entry {entry.get('shard_name')!r} is missing {field!r}")
        if not entry["filter"] or not entry["log_name"] or not entry["csproj"]:
            raise FixtureError(f"matrix entry {entry.get('shard_name')!r} has an empty required field")
        if not isinstance(entry["timeout_minutes"], (int, float)):
            raise FixtureError(f"matrix entry {entry.get('shard_name')!r} has a non-numeric outer budget")
        if entry["timeout_minutes"] <= entry["test_timeout_minutes"]:
            raise FixtureError(
                f"matrix entry {entry.get('shard_name')!r} would cancel the runner before the "
                "inner cap could upload its receipt"
            )


def check_skip_paths() -> None:
    """Diffs that cannot move a shard must cost no runners at all."""
    docs_only = run_selector(["docs/internal/ci/gate-model.md", "README.md"])
    if not docs_only["skip"] or docs_only["reason"] != "no_product_code":
        raise FixtureError(f"docs-only diff did not skip: {docs_only['reason']}")
    if docs_only["shards"]:
        raise FixtureError("a skipped selection must name no shards")

    workflow_only = run_selector([".github/workflows/pr-gate.yml", "scripts/ci/validate-ci-router.sh"])
    if not workflow_only["skip"] or workflow_only["reason"] != "no_product_code":
        raise FixtureError(f"CI-only diff did not skip: {workflow_only['reason']}")


def check_failure_injections(config: dict[str, Any]) -> None:
    """Prove each check above rejects the specific defect it is written for.

    Each injection replaces `select` with a deliberately broken variant -- one
    that ignores the cap, one that forgets to drop advisory shards, one that
    never narrows a run_all answer, one that ranks by shard duration alone --
    and asserts the matching check FAILS. A check that still passes against its
    own defect is decoration, not a gate.
    """
    original = selector.select

    def ignores_the_cap(**kwargs):
        return original(**{**kwargs, "cap": len(kwargs["config"]["shards"])})

    def forgets_advisory(**kwargs):
        stripped = copy.deepcopy(kwargs["config"])
        for shard in stripped["shards"]:
            shard.pop("advisory", None)
        return original(**{**kwargs, "config": stripped})

    def never_narrows(**kwargs):
        return original(**{**kwargs, "affected_projects": None})

    def ranks_by_duration_only(**kwargs):
        return original(**{**kwargs, "changed_files": []})

    def ignores_test_ownership(**kwargs):
        return original(**{**kwargs, "test_class_hits": None})

    injections = (
        ("cap", ignores_the_cap, lambda: check_cap_is_enforced(config)),
        ("advisory", forgets_advisory, lambda: check_advisory_shards_are_excluded(config)),
        (
            "run_all narrowing",
            never_narrows,
            lambda: check_run_all_narrows_by_affected_projects(config),
        ),
        ("largest-impact ordering", ranks_by_duration_only, lambda: check_largest_impact_first(config)),
        (
            "changed-test ownership",
            ignores_test_ownership,
            lambda: check_changed_test_file_selects_its_running_shard(config),
        ),
    )

    for label, broken, check in injections:
        selector.select = broken
        try:
            check()
        except FixtureError:
            continue
        finally:
            selector.select = original
        raise FixtureError(f"the {label!r} check accepted a selector that breaks it")
    selector.select = original

    # The tier check runs below `select`, on count_test_class_hits, so its
    # injection is the pre-fix ownership walk: score every declared method
    # regardless of tier. That is exactly the defect the check exists for.
    global _RUNNABLE_METHODS
    original_runnable = selector.runnable_methods
    selector.runnable_methods = lambda root, fqn, entry: list(entry["methods"])
    _RUNNABLE_METHODS = None
    try:
        check_non_runnable_tiers_are_not_credited(config)
    except FixtureError:
        pass
    else:
        raise FixtureError(
            "the non-runnable-tier check accepted an ownership walk that ignores tiers"
        )
    finally:
        selector.runnable_methods = original_runnable
        _RUNNABLE_METHODS = None

    # The namespace check reads the ROUTER's answer, and the router reads
    # ci-shards.json off disk -- so its injection has to be a mutated config
    # file, not a patched function.
    #
    # Deleting the shard's `paths` is NOT a usable injection: every mapped
    # source namespace also sits under an `unmapped_source_run_all_prefixes`
    # entry, so an unclaimed file escalates to run_all and the shard comes back
    # as a candidate anyway. (That safety net working is the point of #1897.)
    # Removing the shard from the routable set entirely is the mutation the
    # check must catch, and it is the shape a bad ci-shards.json edit actually
    # takes.
    with tempfile.TemporaryDirectory() as directory:
        victim = next(shard for shard in config["shards"] if unique_owned_prefix(config, shard))
        mutated = copy.deepcopy(config)
        mutated["shards"] = [
            shard for shard in mutated["shards"] if shard["name"] != victim["name"]
        ]
        mutated_path = Path(directory) / "ci-shards.json"
        mutated_path.write_text(json.dumps(mutated), encoding="utf-8")
        try:
            check_feature_namespace_selects_its_shard(config, config_path=mutated_path)
        except FixtureError:
            pass
        else:
            raise FixtureError(
                f"the feature-namespace check accepted a routing map that dropped "
                f"{victim['name']!r}"
            )


def main() -> int:
    config = selector.load_config(CONFIG_PATH)
    covered = check_feature_namespace_selects_its_shard(config)
    check_cap_is_enforced(config)
    check_largest_impact_first(config)
    owned = check_changed_test_file_selects_its_running_shard(config)
    tiered = check_non_runnable_tiers_are_not_credited(config)
    check_run_all_narrows_by_affected_projects(config)
    check_targeted_answer_is_never_trimmed(config)
    check_advisory_shards_are_excluded(config)
    check_matrix_include_shape(config)
    check_skip_paths()
    check_failure_injections(config)
    print(
        f"affected-shards=ok (namespace routing proven for {covered} and changed-test "
        f"ownership for {owned} of {len(config['shards'])} shard families; "
        f"tier exclusion proven on {tiered} non-runnable classes; "
        f"cap={selector.DEFAULT_CAP})"
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except FixtureError as error:
        print(f"::error::validate-affected-shards: {error}", file=sys.stderr)
        raise SystemExit(1)
