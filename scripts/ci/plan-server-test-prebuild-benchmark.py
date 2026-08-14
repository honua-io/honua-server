#!/usr/bin/env python3
"""Select a bounded profile for cross-workflow prebuild A/B evidence."""

from __future__ import annotations

import argparse
import json
from collections import Counter
from pathlib import Path

CONTRACT = "honua.server-test-prebuild-benchmark-plan/v1"
ALLOWED_PROFILES = {"two-same-project", "five-hybrid-project"}


def load_object(path: Path) -> dict:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError(f"{path} must contain a JSON object")
    return value


def build_plan(config: dict, registry: dict, profile_name: str) -> dict:
    if config.get("contract_version") != 1 or profile_name not in ALLOWED_PROFILES:
        raise ValueError("unsupported prebuild benchmark contract/profile")
    profiles = config.get("profiles")
    shards = config.get("shards")
    projects = registry.get("projects")
    if not all(isinstance(item, list) for item in (profiles, shards, projects)):
        raise ValueError("benchmark config or artifact registry is incomplete")

    by_project: dict[str, dict] = {}
    for item in projects:
        if not isinstance(item, dict):
            raise ValueError("artifact registry entries must be objects")
        project = item.get("csproj")
        suffix = item.get("artifact_suffix")
        proof_filter = item.get("proof_filter")
        if not all(isinstance(value, str) and value for value in (project, suffix, proof_filter)):
            raise ValueError("artifact registry entry is incomplete")
        if project in by_project:
            raise ValueError(f"duplicate artifact registry project: {project}")
        by_project[project] = item

    by_name: dict[str, dict] = {}
    for item in shards:
        if not isinstance(item, dict):
            raise ValueError("benchmark shard entries must be objects")
        name = item.get("name")
        project = item.get("project")
        if not isinstance(name, str) or not name or name in by_name:
            raise ValueError(f"invalid or duplicate benchmark shard: {name!r}")
        if project not in by_project:
            raise ValueError(f"unregistered benchmark project: {project}")
        by_name[name] = {
            "identity": name,
            "project": project,
            "project_suffix": by_project[project]["artifact_suffix"],
            "filter": by_project[project]["proof_filter"],
        }

    matching = [item for item in profiles if isinstance(item, dict) and item.get("name") == profile_name]
    if len(matching) != 1:
        raise ValueError(f"profile {profile_name!r} is missing or duplicated")
    names = matching[0].get("shards")
    if not isinstance(names, list) or not names or len(names) != len(set(names)):
        raise ValueError("profile shard selection is invalid")
    unknown = sorted(set(names) - set(by_name))
    if unknown:
        raise ValueError(f"profile references unknown shards: {unknown}")
    selected = [dict(by_name[name]) for name in names]
    counts = Counter(item["project"] for item in selected)
    reusable = {project for project, count in counts.items() if count >= 2}
    if not reusable:
        raise ValueError("prebuild benchmark profile must contain a repeated project")
    for item in selected:
        item["reuse_expected"] = item["project"] in reusable
    return {
        "contract": CONTRACT,
        "profile": profile_name,
        "baseline": selected,
        "candidates": selected,
        "reused_projects": sorted(reusable),
    }


def matrix(items: list[dict]) -> str:
    return json.dumps({"include": items}, separators=(",", ":"), sort_keys=True)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--registry", type=Path, required=True)
    parser.add_argument("--profile", choices=sorted(ALLOWED_PROFILES), required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--github-output", type=Path)
    args = parser.parse_args()
    plan = build_plan(load_object(args.config), load_object(args.registry), args.profile)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(plan, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    if args.github_output:
        with args.github_output.open("a", encoding="utf-8", newline="\n") as handle:
            handle.write(f"baseline={matrix(plan['baseline'])}\n")
            handle.write(f"candidates={matrix(plan['candidates'])}\n")
    print(
        f"prebuild-benchmark-plan={plan['profile']} shards={len(plan['baseline'])} "
        f"reused_projects={len(plan['reused_projects'])}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
