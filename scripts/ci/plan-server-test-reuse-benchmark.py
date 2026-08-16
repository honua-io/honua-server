#!/usr/bin/env python3
"""Build bounded core/full matrices for the repeated-project reuse benchmark."""

from __future__ import annotations

import argparse
import json
from collections import Counter
from pathlib import Path

CONTRACT = "honua.server-test-reuse-plan/v1"
DEFAULT_PROJECT = "tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj"
MAX_SHARDS = 100


def load_object(path: Path) -> dict:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError(f"{path} must contain a JSON object")
    return value


def registry_by_project(registry: dict) -> dict[str, dict]:
    projects = registry.get("projects")
    if not isinstance(projects, list) or not projects:
        raise ValueError("artifact registry has no projects")
    result: dict[str, dict] = {}
    for item in projects:
        if not isinstance(item, dict):
            raise ValueError("artifact registry entries must be objects")
        project = item.get("csproj")
        suffix = item.get("artifact_suffix")
        proof_filter = item.get("proof_filter")
        if not all(isinstance(part, str) and part for part in (project, suffix, proof_filter)):
            raise ValueError("artifact registry entry is incomplete")
        if project in result:
            raise ValueError(f"duplicate artifact registry project: {project}")
        result[project] = item
    return result


def normalize_shard(raw: dict, registry: dict[str, dict], *, full: bool) -> dict:
    if not isinstance(raw, dict):
        raise ValueError("shard entries must be objects")
    identity = raw.get("artifact_suffix") if full else raw.get("name")
    project = raw.get("csproj") or raw.get("project") or DEFAULT_PROJECT
    if not isinstance(identity, str) or not identity or not identity.replace("-", "").isalnum():
        raise ValueError(f"invalid benchmark identity: {identity!r}")
    if project not in registry:
        raise ValueError(f"unregistered benchmark project: {project}")
    selected_filter = raw.get("filter") if full else registry[project]["proof_filter"]
    if not isinstance(selected_filter, str) or not selected_filter:
        raise ValueError(f"missing test filter for {identity}")
    return {
        "identity": identity,
        "project": project,
        "project_suffix": registry[project]["artifact_suffix"],
        "filter": selected_filter,
    }


def build_plan(config: dict, shard_config: dict, registry: dict, mode: str) -> dict:
    if config.get("contract_version") != 1:
        raise ValueError("unsupported reuse benchmark contract")
    by_project = registry_by_project(registry)
    if mode == "core":
        raw_shards = config.get("shards")
        raw_profiles = config.get("profiles")
        if not isinstance(raw_shards, list) or not isinstance(raw_profiles, list):
            raise ValueError("core benchmark config is incomplete")
        shards = [normalize_shard(item, by_project, full=False) for item in raw_shards]
        profiles = raw_profiles
    elif mode == "observed-full":
        raw_shards = shard_config.get("shards")
        if not isinstance(raw_shards, list):
            raise ValueError("ci-shards.json has no shards")
        shards = [normalize_shard(item, by_project, full=True) for item in raw_shards]
        profiles = [{"name": "observed-full-matrix", "shards": [item["identity"] for item in shards]}]
    else:
        raise ValueError(f"unsupported mode: {mode}")

    if not shards or len(shards) > MAX_SHARDS:
        raise ValueError("benchmark shard count is outside the bounded range")
    identities = [item["identity"] for item in shards]
    if len(identities) != len(set(identities)):
        raise ValueError("benchmark shard identities must be unique")
    identity_set = set(identities)
    for profile in profiles:
        if not isinstance(profile, dict) or not isinstance(profile.get("name"), str):
            raise ValueError("invalid benchmark profile")
        members = profile.get("shards")
        if not isinstance(members, list) or not members or not set(members) <= identity_set:
            raise ValueError(f"profile {profile.get('name')} references unknown shards")

    counts = Counter(item["project"] for item in shards)
    reusable_projects = {project for project, count in counts.items() if count >= 2}
    producers = []
    for project in sorted(reusable_projects):
        entry = by_project[project]
        producers.append(
            {
                "identity": entry["artifact_suffix"],
                "project": project,
                "project_suffix": entry["artifact_suffix"],
            }
        )
    reused_consumers = [item for item in shards if item["project"] in reusable_projects]
    return {
        "contract": CONTRACT,
        "mode": mode,
        "baseline": shards,
        "producers": producers,
        "profiles": profiles,
        "reused_consumers": reused_consumers,
    }


def matrix(items: list[dict]) -> str:
    return json.dumps({"include": items}, separators=(",", ":"), sort_keys=True)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--shards", type=Path, required=True)
    parser.add_argument("--registry", type=Path, required=True)
    parser.add_argument("--mode", choices=("core", "observed-full"), required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--github-output", type=Path)
    args = parser.parse_args()

    plan = build_plan(
        load_object(args.config), load_object(args.shards), load_object(args.registry), args.mode
    )
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(plan, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    if args.github_output:
        with args.github_output.open("a", encoding="utf-8", newline="\n") as handle:
            handle.write(f"baseline={matrix(plan['baseline'])}\n")
            handle.write(f"producers={matrix(plan['producers'])}\n")
            handle.write(f"reused_consumers={matrix(plan['reused_consumers'])}\n")
    print(
        f"reuse-plan={args.mode} shards={len(plan['baseline'])} "
        f"producers={len(plan['producers'])} reused={len(plan['reused_consumers'])}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
