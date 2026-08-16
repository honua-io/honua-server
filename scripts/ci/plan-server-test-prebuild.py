#!/usr/bin/env python3
"""Plan a bounded set of repeated test projects for pre-review shadow builds."""

from __future__ import annotations

import argparse
import json
from collections import Counter
from pathlib import Path

CONTRACT = "honua.server-test-prebuild-plan/v1"
DEFAULT_PROJECT = "tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj"


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
        if not isinstance(project, str) or not project.endswith(".csproj"):
            raise ValueError("artifact registry project is invalid")
        if not isinstance(suffix, str) or not suffix.replace("-", "").isalnum():
            raise ValueError(f"artifact suffix is invalid for {project}")
        if project in result:
            raise ValueError(f"duplicate artifact registry project: {project}")
        result[project] = item
    return result


def build_plan(config: dict, shard_config: dict, registry: dict, descriptor: dict) -> dict:
    if config.get("contract_version") != 1:
        raise ValueError("unsupported prebuild planner contract")
    max_projects = config.get("max_projects_per_head")
    max_shards = config.get("max_selected_shards")
    if not isinstance(max_projects, int) or not 1 <= max_projects <= 5:
        raise ValueError("max_projects_per_head must be between 1 and 5")
    if not isinstance(max_shards, int) or not 1 <= max_shards <= 100:
        raise ValueError("max_selected_shards must be between 1 and 100")

    raw_shards = shard_config.get("shards")
    selected_names = descriptor.get("shards")
    if not isinstance(raw_shards, list) or not raw_shards:
        raise ValueError("ci-shards.json has no shards")
    if not isinstance(selected_names, list) or not selected_names or len(selected_names) > max_shards:
        raise ValueError("selected shard set is outside the bounded range")
    if len(selected_names) != len(set(selected_names)) or not all(
        isinstance(name, str) and name for name in selected_names
    ):
        raise ValueError("selected shard names must be unique non-empty strings")

    shards_by_name: dict[str, dict] = {}
    for shard in raw_shards:
        if not isinstance(shard, dict):
            raise ValueError("shard entries must be objects")
        name = shard.get("name")
        if not isinstance(name, str) or not name or name in shards_by_name:
            raise ValueError(f"invalid or duplicate shard name: {name!r}")
        shards_by_name[name] = shard
    unknown = sorted(set(selected_names) - set(shards_by_name))
    if unknown:
        raise ValueError(f"selected shard set contains unknown names: {unknown}")

    selected = []
    for name in selected_names:
        raw = shards_by_name[name]
        project = raw.get("csproj") or DEFAULT_PROJECT
        if not isinstance(project, str) or not project.endswith(".csproj"):
            raise ValueError(f"selected shard {name} has an invalid project")
        selected.append({"identity": name, "project": project, "filter": raw.get("filter")})

    registry_projects = registry_by_project(registry)
    counts = Counter(item["project"] for item in selected)
    repeated = sorted(
        (project for project, count in counts.items() if count >= 2),
        key=lambda project: (-counts[project], project),
    )
    unregistered = [project for project in repeated if project not in registry_projects]
    if unregistered:
        raise ValueError(f"repeated projects are not registered: {unregistered}")
    chosen = repeated[:max_projects]
    producers = [
        {
            "identity": registry_projects[project]["artifact_suffix"],
            "project": project,
            "project_suffix": registry_projects[project]["artifact_suffix"],
            "selected_shard_count": counts[project],
        }
        for project in chosen
    ]
    consumers = [item for item in selected if item["project"] in chosen]
    return {
        "contract": CONTRACT,
        "descriptor_reason": descriptor.get("reason", "unknown"),
        "selected_shard_count": len(selected),
        "producers": producers,
        "consumers": consumers,
        "deferred_repeated_projects": repeated[max_projects:],
    }


def matrix(items: list[dict]) -> str:
    return json.dumps({"include": items}, separators=(",", ":"), sort_keys=True)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--shards", type=Path, required=True)
    parser.add_argument("--registry", type=Path, required=True)
    parser.add_argument("--descriptor", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--github-output", type=Path)
    args = parser.parse_args()
    plan = build_plan(
        load_object(args.config),
        load_object(args.shards),
        load_object(args.registry),
        load_object(args.descriptor),
    )
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(plan, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    if args.github_output:
        with args.github_output.open("a", encoding="utf-8", newline="\n") as handle:
            handle.write(f"has_producers={'true' if plan['producers'] else 'false'}\n")
            handle.write(f"producers={matrix(plan['producers'])}\n")
            handle.write(f"consumers={matrix(plan['consumers'])}\n")
    print(
        f"prebuild-plan shards={plan['selected_shard_count']} producers={len(plan['producers'])} "
        f"consumers={len(plan['consumers'])} deferred={len(plan['deferred_repeated_projects'])}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
