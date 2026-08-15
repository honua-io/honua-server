#!/usr/bin/env python3
"""Turn one exact observer plan into a bounded read-only parity matrix."""

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path

OBSERVATION_CONTRACT = "honua.server-test-prebuild-plan/v1"
BENCHMARK_CONTRACT = "honua.server-test-prebuild-benchmark-plan/v1"
SAFE_PROJECT = re.compile(r"^[A-Za-z0-9._/-]+\.csproj$")
SAFE_SUFFIX = re.compile(r"^[a-z0-9]+(?:-[a-z0-9]+)*$")


def safe_project(value: object) -> bool:
    return (
        isinstance(value, str)
        and bool(SAFE_PROJECT.fullmatch(value))
        and not value.startswith("/")
        and ".." not in value.split("/")
    )


def load_object(path: Path) -> dict:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError(f"{path} must contain a JSON object")
    return value


def build_plan(observation: dict, registry: dict) -> dict:
    if observation.get("contract") != OBSERVATION_CONTRACT:
        raise ValueError("observer plan contract is invalid")
    producers = observation.get("producers")
    projects = registry.get("projects")
    if not isinstance(producers, list) or len(producers) > 2:
        raise ValueError("observer producers are outside the bounded range")
    if not isinstance(projects, list) or not projects:
        raise ValueError("artifact registry has no projects")

    by_project: dict[str, dict] = {}
    for item in projects:
        if not isinstance(item, dict):
            raise ValueError("artifact registry entries must be objects")
        project = item.get("csproj")
        suffix = item.get("artifact_suffix")
        proof_filter = item.get("proof_filter")
        if (
            not safe_project(project)
            or not isinstance(suffix, str)
            or not SAFE_SUFFIX.fullmatch(suffix)
            or not isinstance(proof_filter, str)
            or not proof_filter
            or "\n" in proof_filter
            or "\r" in proof_filter
        ):
            raise ValueError("artifact registry entry is incomplete")
        if project in by_project:
            raise ValueError(f"duplicate artifact registry project: {project}")
        by_project[project] = item

    selected: list[dict] = []
    profile_shard_counts: list[int] = []
    seen_projects: set[str] = set()
    for producer in producers:
        if not isinstance(producer, dict):
            raise ValueError("observer producer entries must be objects")
        project = producer.get("project")
        suffix = producer.get("project_suffix")
        identity = producer.get("identity")
        selected_shards = producer.get("selected_shard_count")
        if (
            not isinstance(project, str)
            or project in seen_projects
            or project not in by_project
            or suffix != by_project[project]["artifact_suffix"]
            or identity != suffix
            or not isinstance(selected_shards, int)
            or selected_shards < 2
        ):
            raise ValueError(f"observer producer identity is invalid for {project!r}")
        seen_projects.add(project)
        profile_shard_counts.append(selected_shards)
        selected.append(
            {
                "identity": suffix,
                "project": project,
                "project_suffix": suffix,
                "filter": by_project[project]["proof_filter"],
                "reuse_expected": True,
            }
        )

    return {
        "contract": BENCHMARK_CONTRACT,
        "profile": (
            "exact-head-shadow:none"
            if not profile_shard_counts
            else (
                "exact-head-shadow:two-shard"
                if max(profile_shard_counts) == 2
                else "exact-head-shadow:multi-shard"
            )
        ),
        "baseline": selected,
        "candidates": selected,
        "reused_projects": sorted(seen_projects),
    }


def matrix(items: list[dict]) -> str:
    return json.dumps({"include": items}, separators=(",", ":"), sort_keys=True)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--observation", type=Path, required=True)
    parser.add_argument("--registry", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--github-output", type=Path)
    args = parser.parse_args()
    plan = build_plan(load_object(args.observation), load_object(args.registry))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(plan, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    if args.github_output:
        with args.github_output.open("a", encoding="utf-8", newline="\n") as handle:
            handle.write(f"has_candidates={'true' if plan['candidates'] else 'false'}\n")
            handle.write(f"baseline={matrix(plan['baseline'])}\n")
            handle.write(f"candidates={matrix(plan['candidates'])}\n")
    print(
        f"prebuild-parity projects={len(plan['reused_projects'])} "
        f"candidates={len(plan['candidates'])}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
